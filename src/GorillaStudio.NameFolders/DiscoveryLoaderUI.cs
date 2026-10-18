using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        [ThreadStatic] private static string preparedForLoad;
        private static string lastDiscoveryRoot;
        private static readonly HashSet<Window> attachedWindows = new HashSet<Window>();
        private static readonly HashSet<Window> rosterWindowsBusy = new HashSet<Window>();
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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
                AttachPictureSelection(window);
                AttachPlaybackControls(window);
                if (!NativeStartupAutomation(window))
                    await RefreshDiscoveryTauntsAsync(window, RememberedRoot(window), false);
            };
            if (window.IsLoaded)
            {
                AttachDiscoveryButton(window);
                AttachPerformanceControls(window);
                AttachPictureSelection(window);
                AttachPlaybackControls(window);
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
            Grid.SetColumn(change, 2);
            access.Children.Add(button);
            access.Children.Add(change);
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
                    RosterImport.LoadFile(rosterFile);
                    RememberRosterFile(rosterFile);
                }
                await RefreshDiscoveryTauntsAsync(window, source, action == 1);
                string prepared = PrepareRoster(source, rosterFile);
                if (prepared == null) return;
                foreach (string name in new[] { "PreviewButton", "MakeButton", "InstallButton" })
                {
                    var control = type.GetField(name, InstanceMembers)?.GetValue(window) as Button;
                    if (control != null) { loadingControls[control] = control.IsEnabled; control.IsEnabled = false; }
                }
                type.GetField("LastGmr", InstanceMembers)?.SetValue(window, null);
                var previousInstall = type.GetField("InstallButton", InstanceMembers)?.GetValue(window) as Button;
                if (previousInstall != null) loadingControls[previousInstall] = false;
                if (renderBusy != null && renderBusy.FieldType == typeof(bool))
                { renderBusy.SetValue(window, true); ownsRenderBusy = true; }
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
                QueueSelectedPicture(window);
            }
        }

        private static async Task RefreshDiscoveryTauntsAsync(Window window, string source, bool explicitSetup)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            Type type = window.GetType();
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
            try
            {
                StopAutomaticMake(window);
                var prepField = type.GetField("PrepTask", InstanceMembers);
                var preparation = prepField == null ? null : prepField.GetValue(window) as Task;
                if (preparation != null && !preparation.IsCompleted)
                {
                    try { await preparation; }
                    catch { /* The next native preparation retains its normal error handling. */ }
                }
                SetModelStatus(window, "Reading Discovery taunts and animation assets…");
                DiscoveryTauntImport import = await DiscoveryTauntBridge.RefreshAsync(window, source);
                string message = import.Definitions == 0
                    ? "No Discovery moves table found; the bundled taunts remain available."
                    : import.Available + " taunts available · " + import.Imported + " added from Discovery";
                if (import.Missing + import.Unsupported > 0)
                    message += " · " + import.Missing + " need animation files · " + import.Unsupported + " need another animation format";
                SetModelStatus(window, message);
                if (explicitSetup)
                {
                    string details = message + "\n\nSelect a wrestler, choose a taunt, then click Preview.";
                    if (import.Warnings.Count > 0) details += "\n\n" + string.Join("\n", import.Warnings);
                    Forms.MessageBox.Show(new NativeOwner(new WindowInteropHelper(window).Handle), details,
                        "Discovery data loaded", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
                }
            }
            catch (Exception error)
            {
                SetModelStatus(window, "Discovery taunts could not be refreshed: " + error.Message);
                if (explicitSetup)
                    Forms.MessageBox.Show(new NativeOwner(new WindowInteropHelper(window).Handle), error.Message,
                        "Discovery taunts", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            }
            finally
            {
                if (body != null) body.IsEnabled = enabled;
                if (ownsBusy) busyField.SetValue(window, false);
                SetPerformanceBusy(window, false);
            }
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
