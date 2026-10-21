using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly SemaphoreSlim RandyPreviewGate = new SemaphoreSlim(1, 1);

        internal static async Task<RandyTauntPreviewResult> PrepareRandyTauntPreviewAsync(Window window, string key,
            Action<string> progress, CancellationToken cancellationToken, bool allowRender, bool automatic = false)
        {
            if (window == null) throw new ArgumentNullException("window");
            if (!window.Dispatcher.CheckAccess())
                return await window.Dispatcher.InvokeAsync(new Func<Task<RandyTauntPreviewResult>>(() =>
                    PrepareRandyTauntPreviewAsync(window, key, progress, cancellationToken, allowRender, automatic))).Task.Unwrap();
            if (!RandyTauntPreviewState.ValidKey(key)) throw new ArgumentException("Choose a playable taunt for Randy's preview.", "key");
            cancellationToken.ThrowIfCancellationRequested();
            var unlock = window.GetType().GetMethod("Unlocked", InstanceMembers, null, Type.EmptyTypes, null);
            if (unlock == null || !(unlock.Invoke(window, null) is bool allowed) || !allowed)
                throw new InvalidOperationException("Sign in to create a Randy Orton preview.");
            string application = window.GetType().GetField("BaseDir", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string
                ?? AppDomain.CurrentDomain.BaseDirectory;
            string engineRoot = DiscoveryEngineRoot ?? application;
            string library = AutomaticTauntField(window, "Library") as string;
            string game = AutomaticTauntField(window, "Game") as string;
            string blender = AutomaticTauntField(window, "BlenderPath") as string;
            Action ensureCurrent = () => {
                cancellationToken.ThrowIfCancellationRequested();
                if (!window.IsLoaded || DiscoveryDataRenderBlocked(window)
                    || !SameRandyPath(engineRoot, DiscoveryEngineRoot ?? application)
                    || !SameRandyPath(library, AutomaticTauntField(window, "Library") as string)
                    || !SameRandyPath(game, AutomaticTauntField(window, "Game") as string)
                    || !SameRandyPath(blender, AutomaticTauntField(window, "BlenderPath") as string))
                    throw new OperationCanceledException("The animation, game or Blender context changed. Select the taunt again.", cancellationToken);
            };
            ensureCurrent();
            Action<string> report = text => {
                if (!window.IsLoaded) return;
                try { progress?.Invoke(text); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            };
            string bundled = null;
            bool found = await Task.Run(() => RandyTauntPreviewCache.TryGetBundled(application, engineRoot, key, out bundled), cancellationToken);
            ensureCurrent();
            if (found) return new RandyTauntPreviewResult(bundled, true);

            // A verified private atlas needs no renderer ownership. In
            // particular, selecting a wrestler may be preparing its model while
            // Randy's already-rendered animation is ready to display now.
            string directory = RandyPreviewAssets.DefaultDirectory;
            string store = Path.Combine(StorageDirectory, "randy-taunt-previews");
            bool assetsReady = await Task.Run(() => RandyPreviewAssets.IsReady(directory), cancellationToken);
            ensureCurrent();
            if (assetsReady && !string.IsNullOrWhiteSpace(blender) && File.Exists(blender))
            {
                string cachedBeforeWait = null;
                bool cacheHit = await Task.Run(() => {
                    RandyTauntPreviewContext cachedContext = RandyProducerContext(application, engineRoot, key, directory, blender, game);
                    return RandyTauntPreviewCache.TryRead(store, cachedContext, out cachedBeforeWait);
                }, cancellationToken);
                ensureCurrent();
                if (cacheHit) return new RandyTauntPreviewResult(cachedBeforeWait, true);
            }

            // The initial automatic lookup is read-only. Only its debounced,
            // still-current generation phase or manual Preview may render.
            if (!allowRender) return null;

            await RandyPreviewGate.WaitAsync(cancellationToken);
            try
            {
                ensureCurrent();
                assetsReady = await Task.Run(() => RandyPreviewAssets.IsReady(directory), cancellationToken);
                ensureCurrent();
                // MAKE and native preparation keep their normal ownership. A
                // cached stock atlas above never waits for a selected wrestler.
                bool waiting = false;
                while (!RandyProducerIdle(window))
                {
                    ensureCurrent();
                    if (!waiting) { report("Waiting for the current model or render before preparing Randy…"); waiting = true; }
                    await Task.Delay(125, cancellationToken);
                }
                ensureCurrent();
                string studio = AutomaticTauntField(window, "Studio") as string;
                if (string.IsNullOrWhiteSpace(studio)) throw new InvalidOperationException("The original rendering engine is unavailable.");
                if (string.IsNullOrWhiteSpace(blender) || !File.Exists(blender))
                    throw new InvalidOperationException("Choose Blender 5.2 or newer in Studio to generate this Randy preview.");
                string output = Path.Combine(store, "native-output");
                var controls = new List<BatchEnabledRestore>();
                foreach (string name in new[] { "PreviewButton", "MakeButton", "InstallButton" })
                {
                    var button = AutomaticTauntField(window, name) as Button;
                    if (button != null)
                    {
                        // MAKE's UI wrapper cancels and drains this automatic
                        // job before entering the native handler. Keep its
                        // existing eligibility; never enable an ineligible MAKE.
                        if (automatic && name == "MakeButton" && button.IsEnabled) continue;
                        controls.Add(new BatchEnabledRestore(button)); button.IsEnabled = false;
                    }
                }
                var busy = window.GetType().GetField("Busy", InstanceMembers);
                bool ownsBusy = false;
                var lifetime = new BatchRenderLifetime();
                CancellationTokenRegistration cancel = cancellationToken.Register(() => {
                    try { lifetime.CancelAsync().ContinueWith(done => { var observed = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
                    catch (ObjectDisposedException) { }
                });
                try
                {
                    StopAutomaticMake(window);
                    if (!assetsReady)
                    {
                        report("Loading the shared Randy Orton model from your game once…");
                        // The extractor checks Busy before it starts. Its async
                        // call takes GameBusy synchronously; reserve Busy just
                        // after that entry so MAKE cannot race the extraction.
                        Task<string> extraction = RequestGameModelAsync(window,
                            new RosterEntry { Id = RandyPreviewAssets.LogicalId, Name = "Randy Orton" },
                            RandyPreviewAssets.StockFolderKey, report, lifetime.TrackProcess);
                        if (busy != null) { busy.SetValue(window, true); ownsBusy = true; }
                        directory = await extraction;
                        ensureCurrent();
                    }
                    else if (busy != null) { busy.SetValue(window, true); ownsBusy = true; }
                    cancellationToken.ThrowIfCancellationRequested();
                    string inspected = directory;
                    directory = await Task.Run(() => RandyPreviewAssets.Validate(inspected), cancellationToken);
                    ensureCurrent();
                    RandyTauntPreviewContext context = await Task.Run(() => RandyProducerContext(
                        application, engineRoot, key, directory, blender, game), cancellationToken);
                    ensureCurrent();
                    string cached = null;
                    bool ready = await Task.Run(() => RandyTauntPreviewCache.TryRead(store, context, out cached), cancellationToken);
                    ensureCurrent();
                    if (ready) return new RandyTauntPreviewResult(cached, true);
                    Directory.CreateDirectory(output);
                    string[] arguments = RandyPreviewAssets.CreateArguments(directory, key, output);
                    report("Preparing Randy Orton's preview; this model is shared by every taunt…");
                    ValueTuple<int, List<string>> result;
                    using (RandyPreviewProcessScope.Begin(window, directory, output))
                        result = await RunPreviewProcess(window, Path.Combine(studio, "pipeline", "gorilla_render.py"), arguments,
                            line => {
                                if ((line ?? "").StartsWith("STAGE ", StringComparison.Ordinal)
                                    || (line ?? "").StartsWith("FRAMES ", StringComparison.Ordinal)) report("Randy preview · " + line);
                            }, lifetime.TrackProcess);
                    ensureCurrent();
                    if (result.Item1 != 0)
                    {
                        string detail = (result.Item2 ?? new List<string>()).LastOrDefault(line => !string.IsNullOrWhiteSpace(line)
                            && !line.StartsWith("STAGE ", StringComparison.Ordinal) && !line.StartsWith("FRAMES ", StringComparison.Ordinal)
                            && !line.StartsWith(" ", StringComparison.Ordinal) && !line.StartsWith("Traceback", StringComparison.Ordinal));
                        throw new InvalidOperationException("Randy's preview could not be generated" + (detail == null ? "." : ": " + detail));
                    }
                    string source;
                    if (!RandyTauntPreviewCache.TryGetPlayAtlas(result.Item2, out source))
                        throw new InvalidDataException("Randy's renderer returned no complete 15 fps preview image.");
                    string published = null;
                    bool saved = await Task.Run(() => RandyTauntPreviewCache.Publish(store, context, source, out published), cancellationToken);
                    ensureCurrent();
                    if (!saved) throw new IOException("The preview inputs changed or its image could not be cached. Select the taunt again.");
                    return new RandyTauntPreviewResult(published);
                }
                finally
                {
                    cancel.Dispose();
                    // Confirm every extraction/render descendant owned by this
                    // request has exited before restoring the main controls.
                    bool stopped = false;
                    while (!stopped)
                    {
                        try { await lifetime.CancelAsync(); stopped = !lifetime.HasRunningProcesses; }
                        catch (Exception error) { report("Waiting for Randy's preview processes to stop… " + error.Message); }
                        if (!stopped) await Task.Delay(250);
                    }
                    lifetime.Dispose();
                    if (ownsBusy && busy != null) busy.SetValue(window, false);
                    foreach (BatchEnabledRestore control in controls) control.Restore();
                    if (window.IsLoaded) UpdateDiscoveryRenderControls(window);
                }
            }
            finally { RandyPreviewGate.Release(); }
        }

        private static RandyTauntPreviewContext RandyProducerContext(string application, string engineRoot, string key,
            string directory, string blender, string game)
        {
            string[] extraFiles = new[] { blender }.Where(File.Exists).ToArray();
            string signature = "blender=" + blender + "\ngame=" + game + "\nfixed-ring-black-hair-v1";
            return RandyTauntPreviewCache.CaptureContext(application, engineRoot, key, directory, signature, extraFiles);
        }

        private static bool RandyProducerIdle(Window window)
        {
            if (!window.IsLoaded || DiscoveryDataRenderBlocked(window) || combinedGameSetups.Contains(window)
                || batchWindows.ContainsKey(window) || rosterWindowsBusy.Contains(window) || pendingRosterSelection != null
                || pictureWork.Contains(window)) return false;
            foreach (string name in new[] { "Busy", "GameBusy" })
                if (AutomaticTauntField(window, name) is bool busy && busy) return false;
            foreach (string name in new[] { "PrepTask", "BgTask" })
                if (AutomaticTauntField(window, name) is Task task && !task.IsCompleted) return false;
            return true;
        }

        private static bool SameRandyPath(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
            return string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
