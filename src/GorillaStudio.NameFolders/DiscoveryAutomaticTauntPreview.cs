using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class AutomaticTauntPreview
        {
            internal readonly AutomaticTauntPreviewQueue Queue = new AutomaticTauntPreviewQueue();
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal ListBox List;
            internal DispatcherTimer Timer;
            internal string Key;
            internal string Folder;
            internal string Target;
            internal long InputGeneration;
            internal long IntentGeneration;
            internal bool InputArmed;
            internal bool Closed;
        }

        private static readonly Dictionary<Window, AutomaticTauntPreview> automaticTauntPreviews =
            new Dictionary<Window, AutomaticTauntPreview>();

        internal static void AttachAutomaticTauntPreview(Window window)
        {
            if (automaticTauntPreviews.ContainsKey(window)) return;
            var list = window.GetType().GetField("TauntList", InstanceMembers)?.GetValue(window) as ListBox;
            if (list == null) return;
            var state = new AutomaticTauntPreview { List = list };
            state.Timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(150) };
            state.Timer.Tick += async (sender, args) => await TryAutomaticTauntPreviewAsync(window, state);
            automaticTauntPreviews.Add(window, state);
            list.PreviewMouseLeftButtonDown += (sender, args) =>
            {
                var item = ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) as ListBoxItem;
                if (item != null && item.IsEnabled) ArmAutomaticTauntInput(window, state);
            };
            list.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key == Key.Up || args.Key == Key.Down || args.Key == Key.Home || args.Key == Key.End
                    || args.Key == Key.PageUp || args.Key == Key.PageDown || args.Key == Key.Space)
                    ArmAutomaticTauntInput(window, state);
            };
            list.SelectionChanged += (sender, args) =>
            {
                // Refresh, search filtering, batch assignment and restored
                // selections are programmatic. They never start rendering.
                if (state.InputArmed) RequestAutomaticTauntPreview(window, state);
                else CancelAutomaticTauntIntent(state);
            };
            var preview = window.GetType().GetField("PreviewButton", InstanceMembers)?.GetValue(window) as Button;
            var make = window.GetType().GetField("MakeButton", InstanceMembers)?.GetValue(window) as Button;
            if (preview != null) preview.PreviewMouseLeftButtonDown += (sender, args) => CancelAutomaticTauntIntent(state);
            if (make != null) make.PreviewMouseLeftButtonDown += (sender, args) => CancelAutomaticTauntIntent(state);
            if (preview != null) preview.Click += (sender, args) => CancelAutomaticTauntIntent(state);
            if (make != null) make.Click += (sender, args) => CancelAutomaticTauntIntent(state);
            window.Closing += (sender, args) => CancelAutomaticTauntIntent(state);
            window.Closed += (sender, args) =>
            {
                state.Closed = true; state.Timer.Stop(); state.Queue.Close();
                automaticTauntPreviews.Remove(window);
            };
            // Attachment alone deliberately leaves the queue empty.
        }

        private static void ArmAutomaticTauntInput(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed || AutomaticTauntContextChanging(window)) return;
            state.InputArmed = true;
            long input = ++state.InputGeneration;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (state.Closed || input != state.InputGeneration) return;
                try { RequestAutomaticTauntPreview(window, state); }
                finally { state.InputArmed = false; }
            }));
        }

        private static bool AutomaticTauntContextChanging(Window window)
        {
            if (!window.IsLoaded || rosterWindowsBusy.Contains(window) || pendingRosterSelection != null
                || combinedGameSetups.Contains(window) || batchWindows.ContainsKey(window)) return true;
            var body = window.GetType().GetField("BodyGrid", InstanceMembers)?.GetValue(window) as UIElement;
            return body != null && !body.IsEnabled;
        }

        private static void RequestAutomaticTauntPreview(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed || AutomaticTauntContextChanging(window)) { CancelAutomaticTauntIntent(state); return; }
            string key = AutomaticTauntKey(state.List.SelectedItem);
            if (string.IsNullOrWhiteSpace(key) || DiscoveryTauntSearch.IsUnavailableKey(key))
            { CancelAutomaticTauntIntent(state); return; }
            string folder = AutomaticTauntField(window, "Folder") as string;
            string target = AutomaticTauntField(window, "TargetSlot") as string;
            if (state.IntentGeneration == state.InputGeneration && state.Key == key
                && SameAutomaticModel(state.Folder, folder) && state.Target == target) return;
            state.Key = key; state.Folder = folder; state.Target = target;
            state.IntentGeneration = state.InputGeneration;
            if (ValidAutomaticModel(folder, target))
                state.Queue.Enqueue(AutomaticTauntIdentity(window, key, folder, target), state.Clock.ElapsedMilliseconds);
            state.Timer.Start();
            if (NeedsAutomaticBrowserPreview(window, key))
            {
                var hint = window.GetType().GetField("PreviewMaking", InstanceMembers)?.GetValue(window) as TextBlock;
                if (hint != null)
                {
                    hint.Text = ValidAutomaticModel(folder, target) ? "Preparing your wrestler preview…" : "Select a wrestler to preview this taunt.";
                    hint.Visibility = Visibility.Visible;
                }
            }
        }

        private static async Task TryAutomaticTauntPreviewAsync(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed) { state.Timer.Stop(); return; }
            if (state.Queue.IsRunning) return;
            if (state.Key == null) { state.Timer.Stop(); return; }
            string key = AutomaticTauntKey(state.List.SelectedItem);
            if (key != state.Key) { CancelAutomaticTauntIntent(state); return; }
            if (AutomaticTauntContextChanging(window)) return;
            string folder = AutomaticTauntField(window, "Folder") as string;
            string target = AutomaticTauntField(window, "TargetSlot") as string;
            if (!ValidAutomaticModel(folder, target)) return;
            if (string.IsNullOrWhiteSpace(state.Folder))
            {
                // A user may pick the animation before loading their wrestler.
                // Bind only after that model and target are actually available.
                state.Folder = folder; state.Target = target;
            }
            else if (!SameAutomaticModel(state.Folder, folder) || state.Target != target)
            { CancelAutomaticTauntIntent(state); return; }
            string identity = AutomaticTauntIdentity(window, key, folder, target);
            state.Queue.Enqueue(identity, state.Clock.ElapsedMilliseconds);
            bool blocked = AutomaticTauntBlocked(window);
            AutomaticTauntPreviewRequest request = state.Queue.TryBegin(identity, blocked, state.Clock.ElapsedMilliseconds);
            if (request == null) return;
            long generation = state.IntentGeneration;
            state.Timer.Stop();
            bool started = false, failed = false;
            try
            {
                var go = window.GetType().GetMethod("Go", InstanceMembers, null, new[] { typeof(bool) }, null);
                if (go == null) return;
                // Go(true) is exactly what the original Preview click handler
                // awaits. Its license check, preparation, cache and rendering
                // remain in the original application and existing bridge.
                var task = go.Invoke(window, new object[] { true }) as Task;
                if (task == null) throw new InvalidOperationException("The original Preview operation is unavailable.");
                started = true;
                bool nativeWorkStarted = !task.IsCompleted || (AutomaticTauntField(window, "Busy") is bool busy && busy);
                await task;
                if (!state.Closed && !SameAutomaticPreview(window, state, key, folder, target, identity))
                    ClearStaleAutomaticPreview(window);
                else if (!state.Closed && !nativeWorkStarted)
                {
                    var hint = AutomaticTauntField(window, "PreviewMaking") as TextBlock;
                    if (hint != null && NeedsAutomaticBrowserPreview(window, key))
                    { hint.Text = "Use Preview once the app is ready."; hint.Visibility = Visibility.Visible; }
                }
            }
            catch (Exception error)
            {
                failed = true;
                if (!state.Closed)
                {
                    string message = error is TargetInvocationException && error.InnerException != null ? error.InnerException.Message : error.Message;
                    SetModelStatus(window, "Automatic preview did not finish: " + message + " Use Preview to try again.");
                }
            }
            finally
            {
                if (failed && started && !state.Closed)
                {
                    // A failed original async call must not strand its Busy
                    // flag. Only this queue's awaited operation is being reset.
                    window.GetType().GetField("Busy", InstanceMembers)?.SetValue(window, false);
                    foreach (string name in new[] { "PreviewButton", "MakeButton" })
                    {
                        var button = AutomaticTauntField(window, name) as Button;
                        if (button != null) button.IsEnabled = true;
                    }
                }
                state.Queue.Complete(request);
                if (state.IntentGeneration == generation) { state.Key = null; state.Queue.CancelPending(); }
                if (!state.Closed && state.Key != null) state.Timer.Start();
            }
        }

        private static bool AutomaticTauntBlocked(Window window)
        {
            if (AutomaticTauntContextChanging(window) || pictureWork.Contains(window)) return true;
            foreach (string name in new[] { "Busy", "GameBusy" })
                if (AutomaticTauntField(window, name) is bool busy && busy) return true;
            foreach (string name in new[] { "PrepTask", "BgTask" })
                if (AutomaticTauntField(window, name) is Task task && !task.IsCompleted) return true;
            var preview = AutomaticTauntField(window, "PreviewButton") as Button;
            return preview == null || !preview.IsEnabled;
        }

        private static bool SameAutomaticPreview(Window window, AutomaticTauntPreview state, string key, string folder, string target, string identity)
        {
            return AutomaticTauntKey(state.List.SelectedItem) == key
                && SameAutomaticModel(AutomaticTauntField(window, "Folder") as string, folder)
                && Equals(AutomaticTauntField(window, "TargetSlot"), target)
                && AutomaticTauntIdentity(window, key, folder, target) == identity;
        }

        private static void ClearStaleAutomaticPreview(Window window)
        {
            (AutomaticTauntField(window, "ResultTimer") as DispatcherTimer)?.Stop();
            (AutomaticTauntField(window, "ResultFrames") as IList)?.Clear();
            var image = AutomaticTauntField(window, "ResultImage") as Image;
            if (image != null) image.Source = null;
            var note = AutomaticTauntField(window, "ResultInfo") as TextBlock;
            if (note != null) note.Text = "Selection changed. Select a taunt or press Preview for this wrestler.";
        }

        private static string AutomaticTauntIdentity(Window window, string key, string folder, string target)
        {
            var settings = new List<string> { folder, target, key, SelectedAlternateAttire(window, folder) ?? "" };
            foreach (string name in new[] { "HairBox", "GearBox", "EndBox" })
            {
                var choice = AutomaticTauntField(window, name) as ComboBox;
                settings.Add(choice == null ? "" : choice.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            }
            foreach (string name in new[] { "EndLoop", "EndFreeze", "EndStill", "ShowSelect", "ShowMatch" })
            {
                var toggle = AutomaticTauntField(window, name) as System.Windows.Controls.Primitives.ToggleButton;
                settings.Add(toggle == null ? "" : toggle.IsChecked.ToString());
            }
            return string.Join("\n", settings);
        }

        private static bool ValidAutomaticModel(string folder, string target)
        {
            int id;
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                && int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 && id <= 65535;
        }

        private static bool SameAutomaticModel(string first, string second)
        {
            return string.Equals(first == null ? null : first.TrimEnd('\\', '/'), second == null ? null : second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static object AutomaticTauntField(object owner, string name)
        { return owner.GetType().GetField(name, InstanceMembers)?.GetValue(owner); }

        private static string AutomaticTauntKey(object item)
        { return item == null ? null : item.GetType().GetProperty("Key")?.GetValue(item, null) as string; }

        internal static bool AutomaticTauntPreviewRequested(Window window, string key)
        {
            AutomaticTauntPreview state;
            return automaticTauntPreviews.TryGetValue(window, out state) && state.Key == key;
        }

        private static bool NeedsAutomaticBrowserPreview(Window window, string key)
        {
            string library = AutomaticTauntField(window, "Library") as string;
            return key.StartsWith("discovery", StringComparison.Ordinal) || key.Contains("_discovery_")
                || string.IsNullOrWhiteSpace(library) || !File.Exists(Path.Combine(library, "previews_game", key + ".png"));
        }

        private static void CancelAutomaticTauntIntent(AutomaticTauntPreview state)
        {
            state.Key = null; state.InputArmed = false; state.InputGeneration++; state.Queue.CancelPending();
            if (!state.Queue.IsRunning) state.Timer.Stop();
        }
    }
}
