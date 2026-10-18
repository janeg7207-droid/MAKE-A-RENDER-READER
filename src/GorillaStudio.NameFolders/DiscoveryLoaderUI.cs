using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
                ToolTip = "Choose the extracted discovery_data folder to load your superstar roster.",
                Style = reference.Style,
                FontFamily = reference.FontFamily,
                FontSize = reference.FontSize,
                Padding = reference.Padding,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(button, row);
            Grid.SetColumn(button, 0);
            Grid.SetColumnSpan(button, Math.Max(1, grid.ColumnDefinitions.Count));
            button.Click += async (sender, args) => await OpenDiscoveryAsync(window, button);
            grid.Children.Add(button);
            attachedWindows.Add(window);
            window.Closed += (sender, args) => attachedWindows.Remove(window);
        }

        private static async Task OpenDiscoveryAsync(Window window, Button button)
        {
            Type type = window.GetType();
            var busy = type.GetField("Busy", InstanceMembers);
            if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window)) return;
            button.IsEnabled = false;
            try
            {
                string source;
                using (var dialog = new Forms.FolderBrowserDialog
                {
                    Description = "Choose the extracted discovery_data folder containing Characters and Roster.",
                    ShowNewFolderButton = false
                })
                {
                    if (dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(window).Handle)) != Forms.DialogResult.OK) return;
                    source = dialog.SelectedPath;
                }
                if (CharacterFolders.FindDiscoveryTable(source) == null)
                    throw new InvalidDataException("Choose discovery_data, Characters or Roster from the extracted Discovery files. The data needs Characters and Roster\\CharProfileTable.jsfb together.");
                string prepared = PrepareFolder(source);
                if (prepared == null) return;

                // The native name finder and game setup will use the same selected Discovery root.
                if (!string.IsNullOrWhiteSpace(lastDiscoveryRoot))
                {
                    var setup = type.GetField("SetupFolder", InstanceMembers);
                    if (setup != null && setup.FieldType == typeof(string))
                    {
                        setup.SetValue(window, lastDiscoveryRoot);
                        var save = type.GetMethod("SaveUserSettings", InstanceMembers, null, Type.EmptyTypes, null);
                        try { if (save != null) save.Invoke(window, null); }
                        catch (TargetInvocationException error)
                        {
                            Forms.MessageBox.Show("The roster loaded, but its location could not be saved.\n\n" + (error.InnerException ?? error).Message,
                                "Gorilla Studio — Discovery roster", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
                        }
                    }
                }

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
            finally { button.IsEnabled = true; }
        }

        private sealed class NativeOwner : Forms.IWin32Window
        {
            public NativeOwner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }
    }
}
