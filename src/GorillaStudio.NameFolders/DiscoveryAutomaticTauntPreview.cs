using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class AutomaticTauntPreview
        {
            internal readonly AutomaticTauntPreviewQueue Queue = new AutomaticTauntPreviewQueue();
            internal readonly RandyTauntPreviewState Selection = new RandyTauntPreviewState();
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly CancellationTokenSource Closing = new CancellationTokenSource();
            internal ListBox List;
            internal DispatcherTimer Timer;
            internal RandyTauntPreviewIntent Intent;
            internal RandyTauntPreviewIntent RunningIntent;
            internal RandyTauntPreviewSchedule Schedule;
            internal TaskCompletionSource<bool> RunningDone;
            internal long InputGeneration;
            internal long RequestedInput;
            internal bool InputArmed;
            internal bool Manual;
            internal bool Closed;
            internal bool ForegroundRequested;
            internal bool RunningAutomatic;
            internal string Status;
            internal string DisplayedKey;
            internal string DisplayedContext;
            internal BitmapSource ResultRoot;
        }

        private static readonly Dictionary<Window, AutomaticTauntPreview> automaticTauntPreviews =
            new Dictionary<Window, AutomaticTauntPreview>();

        internal static void AttachAutomaticTauntPreview(Window window)
        {
            if (automaticTauntPreviews.ContainsKey(window)) return;
            var list = AutomaticTauntField(window, "TauntList") as ListBox;
            if (list == null) return;
            var state = new AutomaticTauntPreview { List = list };
            automaticTauntPreviews.Add(window, state);

            // The old handler prepared the selected wrestler for every taunt.
            // Browsing now depends only on Randy and the current animation data.
            MethodInfo tauntChanged = window.GetType().GetMethod("OnTauntChanged", InstanceMembers, null,
                new[] { typeof(object), typeof(SelectionChangedEventArgs) }, null);
            if (tauntChanged != null)
                list.RemoveHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                    (SelectionChangedEventHandler)Delegate.CreateDelegate(typeof(SelectionChangedEventHandler), window, tauntChanged));
            list.PreviewMouseLeftButtonDown += (sender, args) => {
                var item = ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) as ListBoxItem;
                if (item != null && item.IsEnabled) ArmAutomaticTauntInput(window, state);
            };
            list.PreviewKeyDown += (sender, args) => {
                if (args.Key == Key.Up || args.Key == Key.Down || args.Key == Key.Home || args.Key == Key.End
                    || args.Key == Key.PageUp || args.Key == Key.PageDown || args.Key == Key.Space)
                    ArmAutomaticTauntInput(window, state);
            };
            list.SelectionChanged += (sender, args) => {
                // Keep MAKE's original background cleanup, without preparing a
                // different selected-wrestler animation for the preview browser.
                if (!(AutomaticTauntField(window, "Busy") is bool busy && busy)) StopAutomaticMake(window);
                ClearRandyBrowserPreview(window);
                if (state.InputArmed) RequestAutomaticTauntPreview(window, state, false);
                else CancelAutomaticTauntIntent(state);
                UpdateRandyPreviewButton(window, state);
            };
            var preview = AutomaticTauntField(window, "PreviewButton") as Button;
            if (preview != null)
            {
                MethodInfo original = window.GetType().GetMethod("OnPreview", InstanceMembers, null,
                    new[] { typeof(object), typeof(RoutedEventArgs) }, null);
                if (original != null)
                    preview.RemoveHandler(Button.ClickEvent,
                        (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, original));
                preview.ToolTip = "Preview this taunt on Randy Orton. MAKE uses your selected wrestler and attire.";
                preview.Click += async (sender, args) => {
                    state.InputGeneration++;
                    RequestAutomaticTauntPreview(window, state, true);
                    await TryAutomaticTauntPreviewAsync(window, state);
                };
            }
            var make = AutomaticTauntField(window, "MakeButton") as Button;
            if (make != null)
            {
                MethodInfo originalMake = window.GetType().GetMethod("OnMake", InstanceMembers, null,
                    new[] { typeof(object), typeof(RoutedEventArgs) }, null);
                if (originalMake != null)
                {
                    make.RemoveHandler(Button.ClickEvent,
                        (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, originalMake));
                    make.Click += async (sender, args) => {
                        if (state.ForegroundRequested) return;
                        state.ForegroundRequested = true;
                        try
                        {
                            await CancelAndDrainAutomaticRandyPreviewAsync(window);
                            if (state.Closed || !window.IsLoaded) return;
                            CancelAutomaticTauntIntent(state);
                            originalMake.Invoke(window, new object[] { sender, args });
                        }
                        catch (Exception error)
                        {
                            if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                            if (!state.Closed) SetModelStatus(window, "MAKE could not start: " + error.Message);
                        }
                        finally { state.ForegroundRequested = false; }
                    };
                }
                else make.Click += (sender, args) => CancelAutomaticTauntIntent(state);
            }
            var install = AutomaticTauntField(window, "InstallButton") as Button;
            if (install != null)
            {
                // Keep the original install/sign-in path, but reject a Randy
                // browser result even if native UpdateControls re-enables it.
                MethodInfo originalInstall = window.GetType().GetMethod("OnInstall", InstanceMembers, null,
                    new[] { typeof(object), typeof(RoutedEventArgs) }, null);
                if (originalInstall != null)
                {
                    install.RemoveHandler(Button.ClickEvent,
                        (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, originalInstall));
                    install.Click += (sender, args) => {
                        if (RandyPreviewDisplayed(window)) { args.Handled = true; install.IsEnabled = false; return; }
                        originalInstall.Invoke(window, new object[] { sender, args });
                    };
                }
            }
            window.Closing += (sender, args) => CancelAutomaticTauntIntent(state);
            window.Closed += (sender, args) => {
                state.Closed = true; state.Closing.Cancel(); state.Timer.Stop(); state.Queue.Close(); state.Selection.Close();
                automaticTauntPreviews.Remove(window);
                // The producer can still be observing this token while its
                // owned process exits; leave source disposal to that completion.
                if (!state.Queue.IsRunning) state.Closing.Dispose();
            };
            state.Timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(150) };
            state.Timer.Tick += async (sender, args) => {
                if (state.Intent != null && (AutomaticTauntContextChanging(window)
                    || !state.Selection.IsCurrent(state.Intent, AutomaticTauntKey(state.List.SelectedItem), RandyPreviewContext(window))))
                    CancelAutomaticTauntIntent(state);
                if (install != null && RandyPreviewDisplayed(window)) install.IsEnabled = false;
                UpdateRandyPreviewButton(window, state);
                await TryAutomaticTauntPreviewAsync(window, state);
            };
            state.Timer.Start();
            ClearRandyBrowserPreview(window);
            UpdateRandyPreviewButton(window, state);
        }

        private static void ArmAutomaticTauntInput(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed) return;
            state.InputArmed = true;
            long generation = ++state.InputGeneration;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                if (state.Closed || generation != state.InputGeneration) return;
                try { RequestAutomaticTauntPreview(window, state, false); }
                finally { state.InputArmed = false; }
            }));
        }

        private static string RandyPreviewContext(Window window)
        {
            return (DiscoveryEngineRoot ?? "") + "\n" + (AutomaticTauntField(window, "Library") as string ?? "");
        }

        private static bool AutomaticTauntContextChanging(Window window)
        {
            return !window.IsLoaded || DiscoveryDataRenderBlocked(window) || combinedGameSetups.Contains(window)
                || batchWindows.ContainsKey(window);
        }

        private static bool AutomaticTauntBlocked(Window window)
        {
            if (AutomaticTauntContextChanging(window)) return true;
            foreach (string name in new[] { "Busy", "GameBusy" })
                if (AutomaticTauntField(window, name) is bool busy && busy) return true;
            return AutomaticTauntField(window, "BgTask") is Task background && !background.IsCompleted;
        }

        private static void UpdateRandyPreviewButton(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed || !window.IsLoaded) return;
            var preview = AutomaticTauntField(window, "PreviewButton") as Button;
            if (preview == null) return;
            bool valid = RandyTauntPreviewState.ValidKey(AutomaticTauntKey(state.List.SelectedItem));
            preview.IsEnabled = valid && !state.Queue.IsRunning && !AutomaticTauntBlocked(window);
        }

        private static void RequestAutomaticTauntPreview(Window window, AutomaticTauntPreview state, bool manual)
        {
            if (state.Closed) return;
            string key = AutomaticTauntKey(state.List.SelectedItem), context = RandyPreviewContext(window);
            if (!RandyTauntPreviewState.ValidKey(key)) { CancelAutomaticTauntIntent(state); return; }
            if (!manual && state.RequestedInput == state.InputGeneration && state.Intent != null
                && state.Selection.IsCurrent(state.Intent, key, context)) return;
            state.RequestedInput = state.InputGeneration;
            RandyTauntPreviewIntent previous = state.Intent;
            state.Intent = state.Selection.Select(key, context, state.Closing.Token);
            if (previous != null && !ReferenceEquals(previous, state.RunningIntent)) previous.Dispose();
            state.Manual = manual;
            state.Schedule = new RandyTauntPreviewSchedule(manual, state.Clock.ElapsedMilliseconds);
            ClearRandyBrowserPreview(window);
            state.Status = "Randy Orton preview queued…";
            // Cache reads have no debounce. An automatic miss is queued again
            // with its original selection time before a renderer may start.
            state.Queue.CancelPending();
            state.Queue.Enqueue(RandyTauntPreviewState.Identity(key, context), state.Clock.ElapsedMilliseconds - 1000);
            PresentRandyPreviewHint(window, state, key);
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
                await TryAutomaticTauntPreviewAsync(window, state)));
        }

        private static async Task TryAutomaticTauntPreviewAsync(Window window, AutomaticTauntPreview state)
        {
            if (state.Closed || state.ForegroundRequested || state.Queue.IsRunning || state.Intent == null
                || state.Schedule == null || state.Schedule.Completed) return;
            RandyTauntPreviewIntent intent = state.Intent;
            RandyTauntPreviewSchedule schedule = state.Schedule;
            string key = AutomaticTauntKey(state.List.SelectedItem), context = RandyPreviewContext(window);
            if (!state.Selection.IsCurrent(intent, key, context)) { CancelAutomaticTauntIntent(state); return; }
            // A producer may return a verified Randy atlas without touching
            // native render/model ownership. It waits for Busy only on a cache
            // miss, so browsing cached taunts stays responsive during model prep.
            if (AutomaticTauntContextChanging(window))
            {
                state.Status = DiscoveryDataRenderBlocked(window) ? "Waiting for matching Discovery animations…"
                    : "Randy preview queued while the current operation finishes…";
                PresentRandyPreviewHint(window, state, key); return;
            }
            if (!state.Manual && schedule.AllowRender && AutomaticTauntBlocked(window))
            {
                state.Status = "Randy preview queued while the current model or render finishes…";
                PresentRandyPreviewHint(window, state, key);
                return;
            }
            string identity = RandyTauntPreviewState.Identity(key, context);
            AutomaticTauntPreviewRequest request = state.Queue.TryBegin(identity, false, state.Clock.ElapsedMilliseconds);
            if (request == null) return;
            state.RunningIntent = intent;
            var runningDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.RunningDone = runningDone;
            bool manual = state.Manual;
            state.RunningAutomatic = !manual;
            bool generationQueued = false;
            try
            {
                // The separate Randy workflow still uses the application's
                // original sign-in gate. It never calls Go(true), which binds a
                // quick render to the selected wrestler and its preparation.
                var unlocked = window.GetType().GetMethod("Unlocked", InstanceMembers, null, Type.EmptyTypes, null);
                if (unlocked == null || !(unlocked.Invoke(window, null) is bool allowed) || !allowed)
                {
                    state.Status = "Sign in to create a Randy Orton preview.";
                    return;
                }
                state.Status = "Opening Randy Orton's taunt preview…";
                PresentRandyPreviewHint(window, state, key);
                RandyTauntPreviewResult result = await PrepareRandyTauntPreviewAsync(window, key, message => {
                    if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
                    Action show = () => {
                        if (!state.Closed && state.Selection.IsCurrent(intent, AutomaticTauntKey(state.List.SelectedItem), RandyPreviewContext(window)))
                        { state.Status = message; PresentRandyPreviewHint(window, state, key); }
                    };
                    if (window.Dispatcher.CheckAccess()) show(); else window.Dispatcher.BeginInvoke(show);
                }, intent.Token, schedule.AllowRender, !manual);
                if (state.Closed || !state.Selection.IsCurrent(intent, AutomaticTauntKey(state.List.SelectedItem), RandyPreviewContext(window))) return;
                if (result == null)
                {
                    generationQueued = schedule.QueueGeneration(state.ForegroundRequested
                        || batchWindows.ContainsKey(window) || combinedGameSetups.Contains(window));
                    if (generationQueued)
                    {
                        state.Queue.Enqueue(identity, schedule.SelectedAtMilliseconds);
                        state.Status = "Preparing this taunt's Randy preview automatically…";
                    }
                    else state.Status = "Studio is busy. Select this taunt again when the current operation finishes.";
                    PresentRandyPreviewHint(window, state, key);
                    return;
                }
                ShowRandyAtlas(window, state, key, context, result.AtlasPath, manual);
                state.Status = result.Cached ? "Randy Orton preview ready · cached" : "Randy Orton preview ready";
                PresentRandyPreviewHint(window, state, key);
            }
            catch (OperationCanceledException)
            {
                if (!state.Closed && state.Selection.IsCurrent(intent, AutomaticTauntKey(state.List.SelectedItem), RandyPreviewContext(window)))
                { state.Status = "Randy preview canceled. Press Preview to retry."; PresentRandyPreviewHint(window, state, key); }
            }
            catch (Exception error)
            {
                if (!state.Closed && state.Selection.IsCurrent(intent, AutomaticTauntKey(state.List.SelectedItem), RandyPreviewContext(window)))
                {
                    if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                    state.Status = "Randy preview stopped: " + error.Message;
                    PresentRandyPreviewHint(window, state, key);
                }
            }
            finally
            {
                try
                {
                    state.Queue.Complete(request);
                    if (!generationQueued) schedule.Complete();
                    if (!generationQueued || !ReferenceEquals(state.Intent, intent)) intent.Dispose();
                    if (ReferenceEquals(state.RunningIntent, intent))
                    { state.RunningIntent = null; state.RunningAutomatic = false; }
                    if (state.Closed) state.Closing.Dispose();
                    else UpdateRandyPreviewButton(window, state);
                }
                finally
                {
                    runningDone.TrySetResult(true);
                    if (ReferenceEquals(state.RunningDone, runningDone)) state.RunningDone = null;
                }
            }
        }

        private static void ClearRandyBrowserPreview(Window window)
        {
            AutomaticTauntPreview state;
            if (automaticTauntPreviews.TryGetValue(window, out state))
            { state.DisplayedKey = null; state.DisplayedContext = null; }
            (AutomaticTauntField(window, "PreviewTimer") as DispatcherTimer)?.Stop();
            var image = AutomaticTauntField(window, "TauntPreview") as Image;
            if (image != null) image.Source = null;
            object selected = (AutomaticTauntField(window, "TauntList") as ListBox)?.SelectedItem;
            string key = AutomaticTauntKey(selected);
            var name = AutomaticTauntField(window, "TauntPreviewName") as TextBlock;
            if (name != null) name.Text = Convert.ToString(selected?.GetType().GetProperty("Name")?.GetValue(selected, null)) ?? "Pick a taunt";
            var note = AutomaticTauntField(window, "TauntPreviewNote") as TextBlock;
            if (note != null) note.Text = RandyTauntPreviewState.ValidKey(key) ? "Preview model: Randy Orton"
                : Convert.ToString(selected?.GetType().GetProperty("Note")?.GetValue(selected, null));
            var hint = AutomaticTauntField(window, "PreviewMaking") as TextBlock;
            if (hint != null)
            {
                hint.Text = RandyTauntPreviewState.ValidKey(key)
                    ? "Randy Orton previews every taunt.\nSelect a taunt or press Preview."
                    : key != null && DiscoveryTauntSearch.IsUnavailableKey(key)
                        ? Convert.ToString(selected.GetType().GetProperty("Note")?.GetValue(selected, null)) : "Pick an available taunt.";
                hint.Visibility = Visibility.Visible;
            }
        }

        internal static void ShowRandyTauntPreviewHint(Window window)
        {
            AutomaticTauntPreview state;
            if (!automaticTauntPreviews.TryGetValue(window, out state)) return;
            string key = AutomaticTauntKey(state.List.SelectedItem), context = RandyPreviewContext(window);
            if (state.DisplayedKey != key || state.DisplayedContext != context) ClearRandyBrowserPreview(window);
            PresentRandyPreviewHint(window, state, key);
        }

        private static void PresentRandyPreviewHint(Window window, AutomaticTauntPreview state, string key)
        {
            if (state.Closed || key == null || AutomaticTauntKey(state.List.SelectedItem) != key) return;
            var hint = AutomaticTauntField(window, "PreviewMaking") as TextBlock;
            if (hint == null) return;
            if (state.DisplayedKey == key && state.DisplayedContext == RandyPreviewContext(window))
            { hint.Visibility = Visibility.Collapsed; return; }
            string message = AutomaticTauntPreviewStatus(window, key);
            if (!string.IsNullOrWhiteSpace(message)) hint.Text = message;
            hint.Visibility = Visibility.Visible;
        }

        private static void ShowRandyAtlas(Window window, AutomaticTauntPreview state, string key, string context, string path, bool showResult)
        {
            BitmapSource bitmap = PreviewAtlasLoader.Load(path);
            int height = bitmap.PixelHeight;
            if (height <= 0 || bitmap.PixelWidth < height || bitmap.PixelWidth % height != 0)
                throw new InvalidDataException("The Randy preview has no complete square frames.");
            var frames = new List<ImageSource>();
            for (int index = 0; index < bitmap.PixelWidth / height; index++)
            {
                var frame = new CroppedBitmap(bitmap, new Int32Rect(index * height, 0, height, height));
                frame.Freeze(); frames.Add(frame);
            }
            var timer = AutomaticTauntField(window, "PreviewTimer") as DispatcherTimer;
            timer?.Stop();
            window.GetType().GetField("PreviewFrames", InstanceMembers)?.SetValue(window, frames);
            window.GetType().GetField("PreviewIndex", InstanceMembers)?.SetValue(window, 0);
            var image = AutomaticTauntField(window, "TauntPreview") as Image;
            if (image != null) image.Source = frames[0];
            if (timer != null) { timer.Interval = TimeSpan.FromSeconds(1.0 / 15.0); timer.Start(); }
            state.DisplayedKey = key; state.DisplayedContext = context;
            if (showResult)
            {
                window.GetType().GetField("ResultFreeze", InstanceMembers)?.SetValue(window, false);
                window.GetType().GetMethod("PlayStrip", InstanceMembers, null, new[] { typeof(string), typeof(double) }, null)
                    ?.Invoke(window, new object[] { path, 15.0 });
                state.ResultRoot = PngBitmapRoot((AutomaticTauntField(window, "ResultImage") as Image)?.Source as BitmapSource);
                var note = AutomaticTauntField(window, "ResultInfo") as TextBlock;
                if (note != null) note.Text = "Randy Orton · taunt preview\n" +
                    (Convert.ToString(state.List.SelectedItem?.GetType().GetProperty("Name")?.GetValue(state.List.SelectedItem, null)) ?? key)
                    + "\nMAKE uses your selected wrestler and attire. This preview cannot be installed.";
                var install = AutomaticTauntField(window, "InstallButton") as Button;
                if (install != null) install.IsEnabled = false;
            }
        }

        internal static string AutomaticTauntPreviewStatus(Window window, string key)
        {
            AutomaticTauntPreview state;
            if (!automaticTauntPreviews.TryGetValue(window, out state) || state.Closed || state.Intent == null
                || !state.Selection.IsCurrent(state.Intent, key, RandyPreviewContext(window))) return null;
            return state.Status;
        }

        internal static bool AutomaticTauntPreviewRequested(Window window, string key)
        {
            AutomaticTauntPreview state;
            return automaticTauntPreviews.TryGetValue(window, out state) && state.Intent != null
                && state.Selection.IsCurrent(state.Intent, key, RandyPreviewContext(window))
                && state.Schedule != null && !state.Schedule.Completed;
        }

        internal static async Task CancelAndDrainAutomaticRandyPreviewAsync(Window window)
        {
            if (window == null) return;
            if (!window.Dispatcher.CheckAccess())
            {
                await window.Dispatcher.InvokeAsync(new Func<Task>(() => CancelAndDrainAutomaticRandyPreviewAsync(window))).Task.Unwrap();
                return;
            }
            AutomaticTauntPreview state;
            if (!automaticTauntPreviews.TryGetValue(window, out state) || (state.Manual && !state.RunningAutomatic)) return;
            bool previousForeground = state.ForegroundRequested;
            state.ForegroundRequested = true;
            try
            {
                Task drainage = state.RunningAutomatic ? state.RunningDone?.Task : null;
                CancelAutomaticTauntIntent(state);
                if (drainage != null) await drainage;
                // Drop any later browse request made while the foreground
                // action was waiting for its owned process to exit.
                CancelAutomaticTauntIntent(state);
            }
            finally { state.ForegroundRequested = previousForeground; }
        }

        internal static bool IsRandyTauntPreviewImage(Window window, BitmapSource image)
        {
            AutomaticTauntPreview state;
            return automaticTauntPreviews.TryGetValue(window, out state) && state.ResultRoot != null
                && ReferenceEquals(state.ResultRoot, PngBitmapRoot(image));
        }

        private static bool RandyPreviewDisplayed(Window window)
        {
            return IsRandyTauntPreviewImage(window,
                (AutomaticTauntField(window, "ResultImage") as Image)?.Source as BitmapSource);
        }

        // Selected-wrestler quick renders no longer drive the taunt browser.
        // Keep the process bridge's existing observation contract harmless.
        internal static void ObserveAutomaticTauntPreviewResult(object owner, string script, IEnumerable<string> arguments,
            int exitCode, IEnumerable<string> lines) { }

        private static void CancelAutomaticTauntIntent(AutomaticTauntPreview state)
        {
            RandyTauntPreviewIntent previous = state.Intent;
            state.Intent = null; state.InputArmed = false; state.InputGeneration++;
            state.Selection.Cancel(); state.Queue.CancelPending();
            state.Schedule?.Complete(); state.Schedule = null;
            if (previous != null && !ReferenceEquals(previous, state.RunningIntent)) previous.Dispose();
        }

        private static object AutomaticTauntField(object owner, string name)
        { return owner.GetType().GetField(name, InstanceMembers)?.GetValue(owner); }
        private static string AutomaticTauntKey(object item)
        { return item == null ? null : item.GetType().GetProperty("Key")?.GetValue(item, null) as string; }
    }
}
