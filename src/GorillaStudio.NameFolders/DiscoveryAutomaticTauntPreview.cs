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
            internal string ActiveIdentity;
            internal string CompletionIdentity;
            internal AutomaticTauntPreviewOutcome Outcome;
            internal readonly HashSet<string> ResultImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            state.Outcome = null; state.CompletionIdentity = null; state.ResultImages.Clear();
            state.IntentGeneration = state.InputGeneration;
            if (ValidAutomaticModel(folder, target))
                state.Queue.Enqueue(AutomaticTauntIdentity(window, key, folder, target), state.Clock.ElapsedMilliseconds);
            state.Timer.Start();
            if (NeedsAutomaticBrowserPreview(window, key))
            {
                var hint = window.GetType().GetField("PreviewMaking", InstanceMembers)?.GetValue(window) as TextBlock;
                if (hint != null)
                {
                    hint.Text = ValidAutomaticModel(folder, target) ? "Preview queued…" : "Select a wrestler to preview this taunt.";
                    hint.Visibility = Visibility.Visible;
                }
            }
        }

        private static async Task TryAutomaticTauntPreviewAsync(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed) { state.Timer.Stop(); return; }
            if (state.Queue.IsRunning) { UpdateAutomaticPreviewHint(window, state); return; }
            if (state.Key == null) { state.Timer.Stop(); return; }
            string key = AutomaticTauntKey(state.List.SelectedItem);
            if (key != state.Key) { CancelAutomaticTauntIntent(state); return; }
            if (AutomaticTauntContextChanging(window)) { UpdateAutomaticPreviewHint(window, state); return; }
            string folder = AutomaticTauntField(window, "Folder") as string;
            string target = AutomaticTauntField(window, "TargetSlot") as string;
            if (!ValidAutomaticModel(folder, target)) { UpdateAutomaticPreviewHint(window, state); return; }
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
            if (blocked) UpdateAutomaticPreviewHint(window, state);
            AutomaticTauntPreviewRequest request = state.Queue.TryBegin(identity, blocked, state.Clock.ElapsedMilliseconds);
            if (request == null) return;
            long generation = state.IntentGeneration;
            state.ActiveIdentity = identity; state.CompletionIdentity = null; state.Outcome = null; state.ResultImages.Clear();
            bool started = false, failed = false;
            try
            {
                var go = window.GetType().GetMethod("Go", InstanceMembers, null, new[] { typeof(bool) }, null);
                if (go == null) throw new InvalidOperationException("The original Preview operation is unavailable.");
                // Go(true) is exactly what the original Preview click handler
                // awaits. Its license check, preparation, cache and rendering
                // remain in the original application and existing bridge.
                var task = go.Invoke(window, new object[] { true }) as Task;
                if (task == null) throw new InvalidOperationException("The original Preview operation is unavailable.");
                started = true;
                UpdateAutomaticPreviewHint(window, state);
                await task;
                if (!state.Closed && !SameAutomaticPreview(window, state, key, folder, target, identity))
                    ClearStaleAutomaticPreview(window);
                else if (!state.Closed && state.Key == key && state.IntentGeneration == generation && state.InputGeneration == generation)
                {
                    if (state.Outcome == null)
                    {
                        var locked = AutomaticTauntField(window, "LockOverlay") as UIElement;
                        string note = locked != null && locked.Visibility == Visibility.Visible
                            ? (AutomaticTauntField(window, "LockText") as TextBlock)?.Text
                            : (AutomaticTauntField(window, "StageText") as TextBlock)?.Text;
                        state.Outcome = AutomaticTauntPreviewOutcome.Failure(string.IsNullOrWhiteSpace(note) || note == "Ready."
                            ? "Preview did not start. Press Preview once the app is ready." : note);
                    }
                    else if (state.Outcome.Succeeded)
                    {
                        var image = (AutomaticTauntField(window, "ResultImage") as Image)?.Source as System.Windows.Media.Imaging.BitmapSource;
                        string path = PngBitmapPath(image);
                        if (path == null || !state.ResultImages.Contains(path))
                            state.Outcome = AutomaticTauntPreviewOutcome.Failure("The completed image could not be displayed. Press Preview to retry.");
                    }
                    state.CompletionIdentity = identity;
                    PresentAutomaticPreviewOutcome(window, state, key);
                }
            }
            catch (Exception error)
            {
                failed = true;
                if (!state.Closed)
                {
                    string message = error is TargetInvocationException && error.InnerException != null ? error.InnerException.Message : error.Message;
                    state.Outcome = AutomaticTauntPreviewOutcome.Failure("Preview did not finish: " + message);
                    state.CompletionIdentity = identity;
                    if (state.Key == key && state.IntentGeneration == generation && state.InputGeneration == generation
                        && SameAutomaticPreview(window, state, key, folder, target, identity))
                    {
                        SetModelStatus(window, "Automatic preview did not finish: " + message + " Use Preview to try again.");
                        PresentAutomaticPreviewOutcome(window, state, key);
                    }
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
                state.ActiveIdentity = null;
                if (state.IntentGeneration == generation) { state.Key = null; state.Queue.CancelPending(); }
                if (!state.Closed && state.Key != null) state.Timer.Start();
                else if (!state.Closed) state.Timer.Stop();
            }
        }

        private static bool AutomaticTauntBlocked(Window window)
        {
            if (AutomaticTauntContextChanging(window) || pictureWork.Contains(window)) return true;
            foreach (string name in new[] { "Busy", "GameBusy" })
                if (AutomaticTauntField(window, name) is bool busy && busy) return true;
            // Go owns PrepTask: it awaits that task, exposes native preparation
            // progress and preserves the original authorization check. Waiting
            // here hid that work behind a permanent-looking generic hint.
            foreach (string name in new[] { "BgTask" })
                if (AutomaticTauntField(window, name) is Task task && !task.IsCompleted) return true;
            var preview = AutomaticTauntField(window, "PreviewButton") as Button;
            return preview == null;
        }

        internal static void ObserveAutomaticTauntPreviewResult(object owner, string script, IEnumerable<string> arguments,
            int exitCode, IEnumerable<string> lines)
        {
            var window = owner as Window;
            if (window == null || arguments == null || !PreviewCache.IsPreviewRequest(script, arguments)) return;
            string[] args = arguments.ToArray();
            string[] output = lines == null ? new string[0] : lines.ToArray();
            if (args.Length < 2 || window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            Action observe = () => {
                AutomaticTauntPreview state;
                if (!automaticTauntPreviews.TryGetValue(window, out state) || state.Closed
                    || AutomaticTauntKey(state.List.SelectedItem) != args[1]
                    || !SameAutomaticModel(args[0], AutomaticTauntField(window, "Folder") as string)) return;
                long input = state.InputGeneration;
                if (state.Queue.IsRunning && (state.Key != args[1] || state.IntentGeneration != input)) return;
                string target = AutomaticTauntField(window, "TargetSlot") as string;
                string identity = AutomaticTauntIdentity(window, args[1], args[0], target);
                if (state.Queue.IsRunning && state.ActiveIdentity != identity) return;
                state.ResultImages.Clear();
                try
                {
                    string result = output.LastOrDefault(line => line != null && line.StartsWith("RESULT ", StringComparison.Ordinal));
                    if (result != null)
                    {
                        Dictionary<string, object> data = DiscoveryTauntLibrary.JsonObject(result.Substring(7));
                        foreach (string name in new[] { "play", "preview" })
                        {
                            object value;
                            if (data.TryGetValue(name, out value) && value is string && File.Exists((string)value))
                                state.ResultImages.Add(Path.GetFullPath((string)value));
                        }
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                    || error is InvalidOperationException || error is FormatException || error is OverflowException) { }
                state.Outcome = AutomaticTauntPreviewOutcome.FromProcess(exitCode, output, state.ResultImages.Count > 0);
                state.CompletionIdentity = identity;
                // Manual Preview uses the same outcome path. Wait until native
                // Go has handled RESULT before reading its image or showing a
                // success state; failed exits can be shown without a fake image.
                window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                    if (state.Closed || state.InputGeneration != input || state.CompletionIdentity != identity || AutomaticTauntKey(state.List.SelectedItem) != args[1]
                        || AutomaticTauntIdentity(window, args[1], AutomaticTauntField(window, "Folder") as string,
                            AutomaticTauntField(window, "TargetSlot") as string) != identity) return;
                    if (!state.Queue.IsRunning) PresentAutomaticPreviewOutcome(window, state, args[1]);
                }));
            };
            if (window.Dispatcher.CheckAccess()) observe(); else window.Dispatcher.Invoke(observe);
        }

        internal static string AutomaticTauntPreviewStatus(Window window, string key)
        {
            AutomaticTauntPreview state;
            if (!automaticTauntPreviews.TryGetValue(window, out state) || state.Closed) return null;
            string folder = AutomaticTauntField(window, "Folder") as string;
            string target = AutomaticTauntField(window, "TargetSlot") as string;
            string identity = AutomaticTauntIdentity(window, key, folder, target);
            if (state.Key == key)
            {
                if (!ValidAutomaticModel(folder, target)) return "Select a wrestler to preview this taunt.";
                if (AutomaticTauntContextChanging(window)) return "Waiting for the roster or Discovery data load to finish…";
                if (state.Queue.IsRunning && state.ActiveIdentity == identity)
                {
                    string progress = (AutomaticTauntField(window, "StageText") as TextBlock)?.Text;
                    if (!string.IsNullOrWhiteSpace(progress) && progress != "Ready." && progress != "Now pick a taunt.") return progress;
                    return "Creating the selected wrestler preview…";
                }
                if (pictureWork.Contains(window)) return "Waiting for the wrestler picture lookup to finish…";
                if (AutomaticTauntField(window, "Busy") is bool busy && busy) return "Waiting for the current preview or render to finish…";
                if (AutomaticTauntField(window, "GameBusy") is bool gameBusy && gameBusy) return "Waiting for the current game task to finish…";
                if (AutomaticTauntField(window, "BgTask") is Task background && !background.IsCompleted) return "Waiting for the current background render to finish…";
                return "Preview queued…";
            }
            if (state.CompletionIdentity == identity && state.Outcome != null) return state.Outcome.Message;
            return null;
        }

        private static void UpdateAutomaticPreviewHint(Window window, AutomaticTauntPreview state)
        {
            string key = AutomaticTauntKey(state.List.SelectedItem);
            if (state.Closed || key == null || state.Key != key || !NeedsAutomaticBrowserPreview(window, key)) return;
            string status = AutomaticTauntPreviewStatus(window, key);
            var hint = AutomaticTauntField(window, "PreviewMaking") as TextBlock;
            if (hint != null && status != null) { hint.Text = status; hint.Visibility = Visibility.Visible; }
        }

        private static void PresentAutomaticPreviewOutcome(Window window, AutomaticTauntPreview state, string key)
        {
            if (state.Closed || AutomaticTauntKey(state.List.SelectedItem) != key || state.Outcome == null) return;
            if (state.Outcome.Succeeded && DiscoveryTauntBridge.ShowCapturedWrestlerPreview(window, key)) return;
            var hint = AutomaticTauntField(window, "PreviewMaking") as TextBlock;
            if (hint != null && NeedsAutomaticBrowserPreview(window, key))
            { hint.Text = state.Outcome.Message; hint.Visibility = Visibility.Visible; }
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
            if (!state.Queue.IsRunning)
            {
                state.Timer.Stop(); state.CompletionIdentity = null; state.Outcome = null; state.ResultImages.Clear();
                var window = Window.GetWindow(state.List);
                string key = AutomaticTauntKey(state.List.SelectedItem);
                if (window != null && window.IsLoaded && key != null && NeedsAutomaticBrowserPreview(window, key))
                {
                    var hint = AutomaticTauntField(window, "PreviewMaking") as TextBlock;
                    if (hint != null && hint.Visibility == Visibility.Visible) hint.Text = "Press Preview to create this wrestler preview.";
                }
            }
        }
    }
}
