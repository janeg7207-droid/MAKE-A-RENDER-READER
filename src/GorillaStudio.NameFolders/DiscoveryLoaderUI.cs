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
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Called after the original window has initialized; its resources and existing controls are retained.
        public static void Initialize(object instance)
        {
            var window = instance as Window;
            if (window == null) return;
            SetGameModelWindow(window);
            RoutedEventHandler loaded = null;
            loaded = (sender, args) =>
            {
                window.Loaded -= loaded;
                AttachDiscoveryButton(window);
            };
            if (window.IsLoaded) AttachDiscoveryButton(window);
            else window.Loaded += loaded;
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
                Content = "Load Discovery roster…",
                ToolTip = "Open your remembered Discovery roster. Choose its folder only the first time.",
                Style = reference.Style,
                FontFamily = reference.FontFamily,
                FontSize = reference.FontSize,
                Padding = reference.Padding,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var change = new Button
            {
                Content = "Change…",
                ToolTip = "Choose a different Discovery folder.",
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
            button.Click += async (sender, args) => await OpenDiscoveryAsync(window, button, change, false);
            change.Click += async (sender, args) => await OpenDiscoveryAsync(window, button, change, true);
            grid.Children.Add(access);
            attachedWindows.Add(window);
            window.Closed += (sender, args) => attachedWindows.Remove(window);
        }

        private static async Task OpenDiscoveryAsync(Window window, Button button, Button change, bool chooseAnother)
        {
            Type type = window.GetType();
            foreach (string name in new[] { "Busy", "GameBusy" })
            {
                var busy = type.GetField(name, InstanceMembers);
                if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window)) return;
            }
            button.IsEnabled = false;
            change.IsEnabled = false;
            try
            {
                string source = RememberedRoot(window);
                if (chooseAnother || source == null)
                {
                    using (var dialog = new Forms.FolderBrowserDialog
                    {
                        Description = "Choose the extracted discovery_data folder containing Characters and Roster.",
                        ShowNewFolderButton = false,
                        SelectedPath = source ?? ""
                    })
                    {
                        if (dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(window).Handle)) != Forms.DialogResult.OK) return;
                        source = RosterAccess.RootFor(dialog.SelectedPath);
                    }
                }
                if (source == null)
                    throw new InvalidDataException("Choose discovery_data, Characters or Roster from the extracted Discovery files. The data needs Characters and Roster\\CharProfileTable.jsfb together.");
                // Remember the location before the character picker opens: cancelling the
                // character choice must not make the next click browse for the same root.
                RememberRoot(window, source);
                string prepared = PrepareFolder(source);
                if (prepared == null) return;

                string selectedRoot = RosterAccess.RootFor(lastDiscoveryRoot);
                if (selectedRoot != null && !string.Equals(selectedRoot, source, StringComparison.OrdinalIgnoreCase))
                    RememberRoot(window, selectedRoot);

                var setFolder = type.GetMethod("SetFolder", InstanceMembers, null, new[] { typeof(string) }, null);
                if (setFolder == null) throw new MissingMethodException("This Gorilla Studio version has no supported folder-loading method.");
                // The picker has already supplied the choice. Do not open it twice for this load.
                preparedForLoad = prepared;
                try
                {
                    var task = setFolder.Invoke(window, new object[] { prepared }) as Task;
                    if (task != null) await task;
                }
                finally { preparedForLoad = null; }
            }
            catch (Exception error)
            {
                var invocation = error as TargetInvocationException;
                if (invocation != null && invocation.InnerException != null) error = invocation.InnerException;
                Forms.MessageBox.Show(error.Message, "Gorilla Studio — Discovery roster", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
            finally { button.IsEnabled = true; change.IsEnabled = true; }
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
