using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        [ThreadStatic] private static string preparedForLoad;
        private static string lastDiscoveryRoot;
        private static readonly HashSet<Window> attachedWindows = new HashSet<Window>();
        private static readonly HashSet<Window> rosterWindowsBusy = new HashSet<Window>();
        private static readonly Dictionary<Window, ModelTauntRefresh> modelTauntRefreshes = new Dictionary<Window, ModelTauntRefresh>();
        private static readonly Dictionary<Window, TauntLoadProgress> tauntLoads = new Dictionary<Window, TauntLoadProgress>();
        private static readonly Dictionary<Window, DiscoveryTauntContextCache> tauntContexts = new Dictionary<Window, DiscoveryTauntContextCache>();
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class ModelTauntRefresh
        {
            internal readonly DiscoveryTauntRefreshGate Gate = new DiscoveryTauntRefreshGate();
            internal DispatcherTimer Timer;
            internal bool Closed;
        }

        internal static void InvalidateTauntContexts()
        {
            foreach (DiscoveryTauntContextCache context in tauntContexts.Values) context.Invalidate();
        }

        private sealed class TauntLoadProgress
        {
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal Button Cancel;
            internal Panel Host;
            internal Window Dialog;
            internal TextBlock Status;
            internal bool Committing;
            internal bool Closed;
            internal bool Finished;
        }

        private static void QueueModelDiscoveryTaunts(string alias, string discoveryRoot)
        {
            var window = gameModelWindow == null ? null : gameModelWindow.Target as Window;
            // OpenRosterAsync already refreshes the selected context before its
            // native folder load. Browse/drop and legacy name-folder loading do
            // not pass through that method and need this deferred refresh.
            if (window == null || rosterWindowsBusy.Contains(window)) return;
            string root = RosterAccess.RootFor(discoveryRoot);
            if (root == null) return;
            ModelTauntRefresh state;
            if (!modelTauntRefreshes.TryGetValue(window, out state))
            {
                state = new ModelTauntRefresh();
                var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                state.Timer = timer;
                timer.Tick += async (sender, args) => await DrainModelTauntRefreshAsync(window, state);
                modelTauntRefreshes.Add(window, state);
                window.Closed += (sender, args) =>
                {
                    state.Closed = true;
                    state.Timer.Stop();
                    state.Gate.CancelPending();
                    modelTauntRefreshes.Remove(window);
                };
            }
            state.Gate.Enqueue(root, alias);
            state.Timer.Start();
        }

        private static async Task DrainModelTauntRefreshAsync(Window window, ModelTauntRefresh state)
        {
            if (state.Closed) { state.Timer.Stop(); return; }
            if (!window.IsLoaded) return;
            Type type = window.GetType();
            string folder = type.GetField("Folder", InstanceMembers)?.GetValue(window) as string;
            var preparation = type.GetField("PrepTask", InstanceMembers)?.GetValue(window) as Task;
            bool blocked = pendingRosterSelection != null || rosterWindowsBusy.Contains(window)
                || (preparation != null && !preparation.IsCompleted);
            foreach (string name in new[] { "Busy", "GameBusy" })
            {
                FieldInfo field = type.GetField(name, InstanceMembers);
                if (field != null && field.FieldType == typeof(bool) && (bool)field.GetValue(window)) blocked = true;
            }
            DiscoveryTauntRefreshRequest request = state.Gate.TryBegin(folder, blocked);
            if (request == null)
            {
                if (!state.Gate.HasWork) state.Timer.Stop();
                return;
            }
            state.Timer.Stop();
            try
            {
                RememberRoot(window, request.Root);
                await RefreshDiscoveryTauntsAsync(window, request.Root, false);
            }
            finally
            {
                state.Gate.Complete(request);
                if (!state.Closed && state.Gate.HasWork) state.Timer.Start();
            }
        }

        // Called after the original window has initialized; its resources and existing controls are retained.
        public static void Initialize(object instance)
        {
            var window = instance as Window;
            if (window == null) return;
            SetGameModelWindow(window);
            RoutedEventHandler loaded = null;
            loaded = async (sender, args) =>
            {
                window.Loaded -= loaded;
                AttachDiscoveryButton(window);
                AttachPerformanceControls(window);
                AttachAlternateAttireControls(window);
                AttachBatchControls(window);
                AttachPictureSelection(window);
                AttachPlaybackControls(window);
                AttachUpdateControls(window);
                AttachAutomaticTauntPreview(window);
                if (!NativeStartupAutomation(window))
                    await RefreshDiscoveryTauntsAsync(window, RememberedRoot(window), false);
            };
            if (window.IsLoaded)
            {
                AttachDiscoveryButton(window);
                AttachPerformanceControls(window);
                AttachAlternateAttireControls(window);
                AttachBatchControls(window);
                AttachPictureSelection(window);
                AttachPlaybackControls(window);
                AttachUpdateControls(window);
                AttachAutomaticTauntPreview(window);
                if (!NativeStartupAutomation(window))
                    _ = RefreshDiscoveryTauntsAsync(window, RememberedRoot(window), false);
            }
            else window.Loaded += loaded;
        }

        private static bool NativeStartupAutomation(Window window)
        {
            Type app = window.GetType().Assembly.GetType("Gorilla.RenderStudio.App");
            if (app == null) return false;
            foreach (string name in new[] { "FolderArg", "AutoArg", "ShotArg", "InstalledShotArg" })
            {
                var field = app.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.GetValue(null) != null) return true;
            }
            return false;
        }

        private static void AttachDiscoveryButton(Window window)
        {
            if (attachedWindows.Contains(window)) return;
            // v1.0.2 has Browse and GameButton in row zero of a three-column grid.
            var field = window.GetType().GetField("GameButton", InstanceMembers);
            var reference = field == null ? null : field.GetValue(window) as Button;
            var grid = reference == null ? null : reference.Parent as Grid;
            if (grid == null) return; // Older releases still support the folder-drop entry point.
            if (grid.RowDefinitions.Count == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = new Button
            {
                Content = "Discovery data…",
                ToolTip = "Set your Discovery data folder once. Names, IDs and model mappings are read together.",
                Style = reference.Style,
                FontFamily = reference.FontFamily,
                FontSize = reference.FontSize,
                Padding = reference.Padding,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var change = new Button
            {
                Content = "Roster file…",
                ToolTip = "Load an existing JSON or JSFB roster. Its location is remembered.",
                Style = reference.Style,
                FontFamily = reference.FontFamily,
                FontSize = reference.FontSize,
                Padding = new Thickness(9, reference.Padding.Top, 9, reference.Padding.Bottom),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var access = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            access.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            access.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            access.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            access.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            access.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(change, 2);
            access.Children.Add(button);
            access.Children.Add(change);
            var animationDetails = new Button {
                Content = "Animation details…", Style = reference.Style, FontFamily = reference.FontFamily,
                FontSize = reference.FontSize, Padding = reference.Padding, Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Read and copy complete animation lookup warnings and unresolved clip IDs."
            };
            animationDetails.Click += (sender, args) => DiscoveryTauntBridge.ShowAnimationDetails(window, RememberedRoot(window));
            Grid.SetRow(animationDetails, 1); Grid.SetColumnSpan(animationDetails, 3); access.Children.Add(animationDetails);
            Grid.SetRow(access, row);
            Grid.SetColumn(access, 0);
            Grid.SetColumnSpan(access, Math.Max(1, grid.ColumnDefinitions.Count));
            var original = window.GetType().GetMethod("OnGameWrestler", InstanceMembers, null,
                new[] { typeof(object), typeof(RoutedEventArgs) }, null);
            if (original != null)
            {
                var handler = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, original);
                reference.RemoveHandler(Button.ClickEvent, handler);
                reference.Content = "Select wrestler…";
                reference.ToolTip = "Select a wrestler from your roster to load the model and default attire automatically.";
                reference.Click += async (sender, args) => await OpenRosterAsync(window, reference, button, change, 0);
            }
            button.Click += async (sender, args) => await OpenRosterAsync(window, reference, button, change, 1);
            change.Click += async (sender, args) => await OpenRosterAsync(window, reference, button, change, 2);
            grid.Children.Add(access);
            attachedWindows.Add(window);
            window.Closed += (sender, args) => { attachedWindows.Remove(window); rosterWindowsBusy.Remove(window); };
        }

        private static async Task OpenRosterAsync(Window window, Button select, Button button, Button change, int action)
        {
            Type type = window.GetType();
            if (rosterWindowsBusy.Contains(window)) return;
            foreach (string name in new[] { "Busy", "GameBusy" })
            {
                var busy = type.GetField(name, InstanceMembers);
                if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window)) return;
            }
            // An explicit roster/data action owns the next context, including
            // when its model picker is canceled. Do not let an older Browse
            // request switch the engine back after this action has refreshed it.
            ModelTauntRefresh queued;
            if (modelTauntRefreshes.TryGetValue(window, out queued))
            {
                queued.Gate.CancelPending();
                queued.Timer.Stop();
            }
            rosterWindowsBusy.Add(window);
            bool selectEnabled = select.IsEnabled, buttonEnabled = button.IsEnabled, changeEnabled = change.IsEnabled;
            select.IsEnabled = false;
            button.IsEnabled = false;
            change.IsEnabled = false;
            var loadingControls = new Dictionary<Button, bool>();
            FieldInfo renderBusy = type.GetField("Busy", InstanceMembers);
            bool ownsRenderBusy = false;
            try
            {
                string source = RememberedRoot(window);
                string rosterFile = action == 1 ? null : RememberedRosterFile();
                if (action == 1)
                {
                    string folder = FolderLocationDialog.Choose(new NativeOwner(new WindowInteropHelper(window).Handle),
                        "Open Discovery data", "Choose or paste the Discovery data folder containing Characters and Roster.", source ?? "");
                    if (folder == null) return;
                    source = RosterAccess.RootFor(folder);
                    if (source == null)
                        throw new InvalidDataException("Choose discovery_data, Characters or Roster from the extracted Discovery files. Keep Characters and Roster\\CharProfileTable.jsfb together.");
                    RememberRoot(window, source);
                    string table = CharacterFolders.FindDiscoveryTable(source);
                    RememberRosterFile(table);
                }
                else if (action == 2)
                {
                    using (var dialog = new Forms.OpenFileDialog
                    {
                        Title = "Load your superstar roster", Filter = "Roster files|*.json;*.jsfb|JSON roster|*.json|Discovery roster table|*.jsfb",
                        CheckFileExists = true, Multiselect = false
                    })
                    {
                        if (dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(window).Handle)) != Forms.DialogResult.OK) return;
                        rosterFile = dialog.FileName;
                    }
                    // Validate before remembering a new file, so a bad import cannot
                    // replace the working roster used by the primary selector.
                    await Task.Run(() => RosterImport.LoadFile(rosterFile));
                    RememberRosterFile(rosterFile);
                }
                // Opening the picker must not first rescan every motion/archive.
                // An explicit Discovery load still refreshes immediately.
                if (action == 1) await RefreshDiscoveryTauntsAsync(window, source, true);
                if (!window.IsLoaded) return;
                foreach (string name in new[] { "Busy", "GameBusy" })
                {
                    FieldInfo state = type.GetField(name, InstanceMembers);
                    if (state != null && state.FieldType == typeof(bool) && (bool)state.GetValue(window)) return;
                }
                foreach (string name in new[] { "PreviewButton", "MakeButton", "InstallButton" })
                {
                    var control = type.GetField(name, InstanceMembers)?.GetValue(window) as Button;
                    if (control != null) { loadingControls[control] = control.IsEnabled; control.IsEnabled = false; }
                }
                // File work now yields to the dispatcher. Hold the native busy
                // guard before yielding so a manual render cannot start with the
                // previous model while this selection is being prepared.
                if (renderBusy != null && renderBusy.FieldType == typeof(bool))
                { renderBusy.SetValue(window, true); ownsRenderBusy = true; }
                string prepared = await PrepareRosterAsync(source, rosterFile, () => window.IsLoaded);
                if (prepared == null) return;
                type.GetField("LastGmr", InstanceMembers)?.SetValue(window, null);
                var previousInstall = type.GetField("InstallButton", InstanceMembers)?.GetValue(window) as Button;
                if (previousInstall != null) loadingControls[previousInstall] = false;
                await PrepareSelectedRenderAsync(window, prepared);
                if (ownsRenderBusy) { renderBusy.SetValue(window, false); ownsRenderBusy = false; }

                string selectedRoot = RosterAccess.RootFor(lastDiscoveryRoot);
                if (selectedRoot != null)
                {
                    if (!string.Equals(selectedRoot, source, StringComparison.OrdinalIgnoreCase))
                        RememberRoot(window, selectedRoot);
                    // The picker can refresh or switch roster files while open.
                    // Import its final Discovery context before native preparation.
                    await RefreshDiscoveryTauntsAsync(window, selectedRoot, false);
                }

                var setFolder = type.GetMethod("SetFolder", InstanceMembers, null, new[] { typeof(string) }, null);
                if (setFolder == null) throw new MissingMethodException("This Gorilla Studio version has no supported folder-loading method.");
                // The picker has already supplied the choice. Do not open it twice for this load.
                preparedForLoad = prepared;
                try
                {
                    var task = setFolder.Invoke(window, new object[] { prepared }) as Task;
                    // SetFolder's initial entry must see Busy=false. After it has
                    // entered its normal async path, block a preview of half-loaded
                    // roster/model context until the lookup and slot selection finish.
                    if (task != null && !task.IsCompleted && renderBusy != null && renderBusy.FieldType == typeof(bool))
                    {
                        renderBusy.SetValue(window, true);
                        ownsRenderBusy = true;
                    }
                    if (task != null) await task;
                    await RefreshSelectedPictureAsync(window);
                }
                finally { preparedForLoad = null; }
            }
            catch (Exception error)
            {
                var invocation = error as TargetInvocationException;
                if (invocation != null && invocation.InnerException != null) error = invocation.InnerException;
                Forms.MessageBox.Show(error.Message, "Gorilla Studio — select wrestler", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
            finally
            {
                select.IsEnabled = selectEnabled;
                button.IsEnabled = buttonEnabled;
                change.IsEnabled = changeEnabled;
                if (ownsRenderBusy) renderBusy.SetValue(window, false);
                foreach (var state in loadingControls) state.Key.IsEnabled = state.Value;
                pendingRosterSelection = null;
                rosterWindowsBusy.Remove(window);
                if (window.IsLoaded) RefreshAlternateAttireSelection(window);
                QueueSelectedPicture(window);
            }
        }

        private static async Task RefreshDiscoveryTauntsAsync(Window window, string source, bool explicitSetup)
        {
            if (string.IsNullOrWhiteSpace(source) || !window.IsLoaded || tauntLoads.ContainsKey(window)) return;
            Type type = window.GetType();
            string game = type.GetField("Game", InstanceMembers)?.GetValue(window) as string;
            DiscoveryTauntContextCache context;
            if (!tauntContexts.TryGetValue(window, out context) || !context.Matches(game, source))
            {
                if (context != null) context.Dispose();
                context = new DiscoveryTauntContextCache(game, source);
                tauntContexts[window] = context;
                window.Closed += (sender, args) => {
                    DiscoveryTauntContextCache current;
                    if (tauntContexts.TryGetValue(window, out current)) { current.Dispose(); tauntContexts.Remove(window); }
                };
            }
            if (!explicitSetup && context.CanReuse) return;
            long refreshRevision = context.BeginRefresh();
            var busyField = type.GetField("Busy", InstanceMembers);
            var gameBusyField = type.GetField("GameBusy", InstanceMembers);
            if ((busyField != null && busyField.FieldType == typeof(bool) && (bool)busyField.GetValue(window))
                || (gameBusyField != null && gameBusyField.FieldType == typeof(bool) && (bool)gameBusyField.GetValue(window)))
                return;
            bool ownsBusy = busyField != null && busyField.FieldType == typeof(bool);
            if (ownsBusy) busyField.SetValue(window, true);
            SetPerformanceBusy(window, true);
            var bodyField = type.GetField("BodyGrid", InstanceMembers);
            var body = bodyField == null ? null : bodyField.GetValue(window) as UIElement;
            bool enabled = body != null && body.IsEnabled;
            if (body != null) body.IsEnabled = false;
            var load = new TauntLoadProgress();
            tauntLoads[window] = load;
            EventHandler closed = (sender, args) => {
                load.Closed = true;
                load.Cancellation.Cancel();
                CloseTauntLoadProgress(load);
            };
            window.Closed += closed;
            try
            {
                ShowTauntLoadProgress(window, body, load);
                StopAutomaticMake(window);
                var prepField = type.GetField("PrepTask", InstanceMembers);
                var preparation = prepField == null ? null : prepField.GetValue(window) as Task;
                if (preparation != null && !preparation.IsCompleted)
                {
                    try { await preparation; }
                    catch { /* The next native preparation retains its normal error handling. */ }
                }
                load.Cancellation.Token.ThrowIfCancellationRequested();
                SetModelStatus(window, "Reading Discovery taunts and animation assets…");
                DiscoveryTauntImport import = await DiscoveryTauntBridge.RefreshAsync(window, source,
                    update => UpdateTauntLoadProgress(window, load, update), load.Cancellation.Token,
                    canCancel => {
                        load.Committing = !canCancel;
                        if (load.Cancel != null) load.Cancel.IsEnabled = canCancel && !load.Closed && !load.Cancellation.IsCancellationRequested;
                    });
                if (load.Closed || !window.IsLoaded) return;
                // Nested links may point outside both watched trees. Preserve
                // normal rescans for those sources instead of retaining stale
                // external animations; ordinary extracted trees can be reused.
                context.CompleteRefresh(refreshRevision, !import.HasLinkedSources);
                string message = import.Definitions == 0
                    ? "No Discovery moves table found; the bundled taunts remain available."
                    : import.Available + " taunts available · " + import.Imported + " added from Discovery";
                if (import.Missing > 0) message += " · " + import.Missing + " animations not located";
                if (import.Unsupported > 0) message += " · " + import.Unsupported + " unsupported animations";
                if (import.ReadFailures > 0) message += " · " + import.ReadFailures + " animation read errors";
                if (import.CacheFailures > 0) message += " · " + import.CacheFailures + " preview cache errors";
                if (import.ArchiveImported > 0) message += " · " + import.ArchiveImported + " recovered from archives";
                if (import.ArchivesSearched) message += " · " + import.ArchiveCount + " archives checked";
                if (import.ScanIncomplete) message += " · lookup incomplete: open Animation details";
                SetModelStatus(window, message);
                if (explicitSetup)
                {
                    string details = message + "\n\nSelect a wrestler and choose a taunt to preview it.";
                    if (import.SearchRoots.Count > 0)
                        details += "\n\nAnimation search covers the entire Discovery folder:\n" + string.Join("\n", import.SearchRoots);
                    if (!string.IsNullOrWhiteSpace(import.ArchiveStatus)) details += "\n\n" + import.ArchiveStatus;
                    else if (import.ArchivesSearched) details += "\n\n" + import.ArchiveCount + " game/Discovery archives searched for unmatched animation clips.";
                    if (import.Warnings.Count > 0)
                    {
                        details += "\n\n" + string.Join("\n", import.Warnings.GetRange(0, Math.Min(3, import.Warnings.Count)));
                        details += "\n\nOpen Animation details for all " + import.Warnings.Count + " warnings and full clip diagnostics.";
                    }
                    Forms.MessageBox.Show(new NativeOwner(new WindowInteropHelper(window).Handle), details,
                        "Discovery data loaded", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                }
            }
            catch (OperationCanceledException)
            {
                if (!load.Closed && window.IsLoaded)
                    SetModelStatus(window, "Animation lookup canceled; the current taunt library was kept.");
            }
            catch (Exception error)
            {
                if (load.Closed || !window.IsLoaded) return;
                SetModelStatus(window, "Discovery taunts could not be refreshed: " + error.Message);
                if (explicitSetup)
                    Forms.MessageBox.Show(new NativeOwner(new WindowInteropHelper(window).Handle), error.Message,
                        "Discovery taunts", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
            finally
            {
                load.Finished = true;
                window.Closed -= closed;
                CloseTauntLoadProgress(load);
                tauntLoads.Remove(window);
                load.Cancellation.Dispose();
                if (!load.Closed)
                {
                    if (body != null) body.IsEnabled = enabled;
                    if (ownsBusy) busyField.SetValue(window, false);
                    SetPerformanceBusy(window, false);
                }
            }
        }

        private static void ShowTauntLoadProgress(Window owner, UIElement disabledBody, TauntLoadProgress load)
        {
            Button reference = null;
            foreach (Button candidate in PlaybackButtons(owner))
                if (string.Equals(candidate.Content as string, "Game setup", StringComparison.OrdinalIgnoreCase))
                { reference = candidate; break; }
            var host = reference?.Parent as Panel;
            var cancel = new Button {
                Content = "Cancel load", Padding = new Thickness(8, 3, 8, 3),
                ToolTip = "Cancel the animation archive lookup and keep the current taunt library."
            };
            load.Cancel = cancel;
            cancel.Click += (sender, args) => CancelTauntLoad(owner, load);
            if ((host is StackPanel || host is WrapPanel) && !InsideTauntBody(host, disabledBody))
            {
                cancel.Style = reference.Style;
                cancel.FontSize = reference.FontSize;
                cancel.Margin = reference.Margin;
                host.Children.Insert(host.Children.IndexOf(reference) + 1, cancel);
                load.Host = host;
                return;
            }
            var dialog = new Window {
                Owner = owner, Title = "Loading Discovery animations", Width = 460, Height = 175,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = owner.FontFamily, FontSize = 13, ShowInTaskbar = false
            };
            load.Dialog = dialog;
            var panel = new DockPanel { Margin = new Thickness(16) };
            cancel.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(cancel, Dock.Bottom); panel.Children.Add(cancel);
            load.Status = new TextBlock { Text = "Reading Discovery animation assets…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(load.Status); dialog.Content = panel;
            dialog.Closing += (sender, args) => {
                if (load.Finished || load.Closed) return;
                args.Cancel = true;
                if (!load.Committing) CancelTauntLoad(owner, load);
            };
            dialog.Show();
        }

        private static bool InsideTauntBody(DependencyObject item, UIElement body)
        {
            for (DependencyObject current = item; current != null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, body)) return true;
            return false;
        }

        private static void CancelTauntLoad(Window owner, TauntLoadProgress load)
        {
            if (load.Committing || load.Finished || load.Closed) return;
            load.Cancellation.Cancel();
            if (load.Cancel != null) load.Cancel.IsEnabled = false;
            if (load.Status != null) load.Status.Text = "Canceling animation lookup…";
            if (owner.IsLoaded) SetModelStatus(owner, "Canceling animation lookup…");
        }

        private static void UpdateTauntLoadProgress(Window owner, TauntLoadProgress load, string message)
        {
            if (owner.Dispatcher.HasShutdownStarted || owner.Dispatcher.HasShutdownFinished) return;
            owner.Dispatcher.BeginInvoke(new Action(() => {
                if (load.Finished || load.Closed || !owner.IsLoaded || (load.Cancellation.IsCancellationRequested && !load.Committing)) return;
                if (load.Status != null) load.Status.Text = message;
                SetModelStatus(owner, message);
            }));
        }

        private static void CloseTauntLoadProgress(TauntLoadProgress load)
        {
            if (load.Host != null && load.Cancel != null) { load.Host.Children.Remove(load.Cancel); load.Host = null; }
            if (load.Dialog != null && (load.Finished || load.Closed)) { load.Dialog.Close(); load.Dialog = null; }
        }

        private static string RememberedRoot(Window window)
        {
            string current = RosterAccess.RootFor(lastDiscoveryRoot);
            if (current != null) return current;
            string preferred = null;
            try
            {
                string path = Path.Combine(StorageDirectory, "discovery-root.txt");
                if (File.Exists(path)) preferred = File.ReadAllText(path).Trim();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var setup = window.GetType().GetField("SetupFolder", InstanceMembers);
            string configured = setup == null ? null : setup.GetValue(window) as string;
            string root = RosterAccess.FindRoot(preferred, configured, null);
            return root ?? RosterAccess.FindRoot(null, null, ReadRoster());
        }

        private static void RememberRoot(Window window, string root)
        {
            lastDiscoveryRoot = root;
            try
            {
                Directory.CreateDirectory(StorageDirectory);
                string path = Path.Combine(StorageDirectory, "discovery-root.txt");
                string temp = Path.Combine(StorageDirectory, "discovery-root-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllText(temp, root, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }

                // Keep the original native name finder and game setup on the same root.
                Type type = window.GetType();
                var setup = type.GetField("SetupFolder", InstanceMembers);
                if (setup != null && setup.FieldType == typeof(string))
                {
                    setup.SetValue(window, root);
                    var save = type.GetMethod("SaveUserSettings", InstanceMembers, null, Type.EmptyTypes, null);
                    if (save != null) save.Invoke(window, null);
                }
            }
            catch (Exception error)
            {
                var invocation = error as TargetInvocationException;
                if (invocation != null && invocation.InnerException != null) error = invocation.InnerException;
                Forms.MessageBox.Show("The roster location will be used this session, but could not be saved.\n\n" + error.Message,
                    "Gorilla Studio — Discovery roster", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
        }

        private sealed class NativeOwner : Forms.IWin32Window
        {
            public NativeOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }
    }
}
