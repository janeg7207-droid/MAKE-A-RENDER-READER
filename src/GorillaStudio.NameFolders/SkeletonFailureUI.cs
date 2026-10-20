using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class SkeletonObserverState
        {
            internal long Generation;
            internal bool Closed;
        }

        private sealed class SkeletonProcessObservation
        {
            internal readonly TaskCompletionSource<bool> OutputEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ErrorEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int SkeletonFailure;
            internal void Read(string line, bool error)
            {
                if (line == null) { (error ? ErrorEnded : OutputEnded).TrySetResult(true); return; }
                if (line.IndexOf("the skeleton could not be read", StringComparison.OrdinalIgnoreCase) >= 0)
                    Interlocked.Exchange(ref SkeletonFailure, 1);
            }
        }

        private static readonly ConditionalWeakTable<Window, SkeletonObserverState> skeletonObservers =
            new ConditionalWeakTable<Window, SkeletonObserverState>();

        // Installed before native Process.Start. It observes the original error;
        // it never supplies a replacement skeleton or changes render ownership.
        static partial void ObserveSkeletonFailureProcess(object owner, Process process, string script, IEnumerable<string> args)
        {
            var window = owner as Window;
            if (window == null || process == null || !window.Dispatcher.CheckAccess()
                || RandyPreviewProcessScope.IsRequest(owner, script, args)) return;
            string[] arguments = args == null ? null : args.ToArray();
            if (arguments == null || arguments.Length < 3 || !string.Equals(
                Path.GetFileNameWithoutExtension((script ?? "").Replace('\\', '/')), "gorilla_render", StringComparison.OrdinalIgnoreCase)) return;
            string folder = SkeletonObserverField(window, "Folder") as string;
            if (!SkeletonObserverSamePath(folder, arguments[0])) return;
            string target = SkeletonObserverField(window, "TargetSlot") as string;
            string context = DiscoveryEngineRoot;
            string game = SkeletonObserverField(window, "Game") as string;
            string blender = SkeletonObserverField(window, "BlenderPath") as string;
            AttireControls attire;
            bool hasAttire = attireWindows.TryGetValue(owner, out attire);
            int attireGeneration = hasAttire ? attire.Generation : 0;
            // Native preparation can append the selected outfit to StartInfo
            // without updating its original argument array. Keep that snapshot
            // so the diagnostic proves ownership of the actual outfit's cache.
            if (!arguments.Skip(3).Any(value => value != null && value.StartsWith("attire=", StringComparison.Ordinal))
                && hasAttire && SameAttireFolder(attire.Folder, folder) && !string.IsNullOrWhiteSpace(attire.Selected))
                arguments = arguments.Concat(new[] { "attire=" + attire.Selected }).ToArray();
            SkeletonObserverState state = skeletonObservers.GetValue(window, key => {
                var created = new SkeletonObserverState();
                key.Closed += (sender, eventArgs) => { created.Closed = true; created.Generation++; };
                return created;
            });
            long generation = ++state.Generation;
            DateTime started = DateTime.UtcNow;
            var observation = new SkeletonProcessObservation();
            DataReceivedEventHandler output = (sender, line) => observation.Read(line.Data, false);
            DataReceivedEventHandler error = (sender, line) => observation.Read(line.Data, true);
            EventHandler exited = null;
            exited = (sender, eventArgs) => {
                int code;
                try { code = process.ExitCode; }
                catch (InvalidOperationException) { return; }
                if (code == 0) return;
                Task report = ShowObservedSkeletonFailureAsync(window, state, generation, observation, script, arguments,
                    started, folder, target, context, game, blender, hasAttire, attire, attireGeneration);
                report.ContinueWith(done => { var ignored = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            };
            process.OutputDataReceived += output;
            process.ErrorDataReceived += error;
            process.Exited += exited;
        }

        private static async Task ShowObservedSkeletonFailureAsync(Window window, SkeletonObserverState state, long generation,
            SkeletonProcessObservation observation, string script, string[] arguments, DateTime started, string folder,
            string target, string context, string game, string blender, bool hadAttire, AttireControls attire, int attireGeneration)
        {
            // Exited can arrive before redirected text. Read its final error
            // without blocking the UI or depending on the native Process lifetime.
            await Task.WhenAny(Task.WhenAll(observation.OutputEnded.Task, observation.ErrorEnded.Task), Task.Delay(1500)).ConfigureAwait(false);
            if (Volatile.Read(ref observation.SkeletonFailure) == 0) return;
            SkeletonFailureReport details = await Task.Run(() => SkeletonFailureDetails.Find(script, arguments, started, DirectoryLinks.Resolve)).ConfigureAwait(false);
            if (details == null || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            try
            {
                await window.Dispatcher.InvokeAsync(new Action(() => {
                    if (state.Closed || !window.IsLoaded || state.Generation != generation
                        || BatchRenderCancellationRequested(window) || DiscoveryDataRenderBlocked(window)
                        || !SkeletonObserverSamePath(folder, SkeletonObserverField(window, "Folder") as string)
                        || !string.Equals(target, SkeletonObserverField(window, "TargetSlot") as string, StringComparison.Ordinal)
                        || !string.Equals(context, DiscoveryEngineRoot, StringComparison.Ordinal)
                        || !SkeletonObserverSamePath(game, SkeletonObserverField(window, "Game") as string)
                        || !SkeletonObserverSamePath(blender, SkeletonObserverField(window, "BlenderPath") as string)) return;
                    AttireControls current;
                    bool hasAttire = attireWindows.TryGetValue(window, out current);
                    if (hadAttire != hasAttire || hasAttire && (!ReferenceEquals(current, attire) || current.Generation != attireGeneration)) return;
                    var information = SkeletonObserverField(window, "ResultInfo") as TextBlock;
                    if (information != null) information.Text = details.FullDetails;
                    var status = SkeletonObserverField(window, "StageText") as TextBlock;
                    if (status != null) status.Text = "Stopped: " + details.Summary + "  See the result panel for rest.log.";
                }), DispatcherPriority.ContextIdle);
            }
            catch (TaskCanceledException) { }
            catch (InvalidOperationException) { }
        }

        private static object SkeletonObserverField(object owner, string name)
        { return owner.GetType().GetField(name, InstanceMembers)?.GetValue(owner); }

        private static bool SkeletonObserverSamePath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
            try { return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
    }
}
