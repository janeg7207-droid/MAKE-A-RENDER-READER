using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly HashSet<Window> installedRenderControls = new HashSet<Window>();
        private static readonly Dictionary<Window, Window> installedRenderWindows = new Dictionary<Window, Window>();

        private sealed class InstalledRenderRow : INotifyPropertyChanged
        {
            internal CompletedRenderEntry Entry;
            private bool selected;
            public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
            public bool IsValid { get { return Entry.IsValid; } }
            public int Id { get { return Entry.WrestlerId; } }
            public string Name { get { return Entry.Name; } }
            public string Taunt { get { return Entry.Taunt; } }
            public string Ending { get { return Entry.Ending == "freeze" ? "Flash freeze" : Entry.Ending == "still" ? "Still 3D picture" : Entry.Ending == "loop" ? "Loop" : Entry.Ending; } }
            public string Screens { get { return Entry.Screens == "select" ? "Roster selection" : Entry.Screens == "matchcard" ? "Match card" : Entry.Screens == "both" || string.IsNullOrWhiteSpace(Entry.Screens) ? "Both screens" : Entry.Screens; } }
            public string File { get { return Path.GetFileName(Entry.Path); } }
            public string Details { get { return (Entry.IsValid ? "Installed file" : "Unavailable: " + Entry.Error) + "\n" + Entry.Path; } }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static void AttachInstalledRenderControls(Window owner)
        {
            if (installedRenderControls.Contains(owner)) return;
            Type type = owner.GetType();
            var button = type.GetField("InstalledButton", InstanceMembers)?.GetValue(owner) as Button;
            var native = type.GetMethod("OnInstalled", InstanceMembers, null, new[] { typeof(object), typeof(RoutedEventArgs) }, null);
            if (button == null || native == null) return;
            // Remove the exact original handler, including keyboard/programmatic Click,
            // rather than appending a handler that runs after the old modal window.
            var previous = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), owner, native);
            RoutedEventHandler replacement = (sender, args) =>
            {
                args.Handled = true;
                try { OpenInstalledRenders(owner); }
                catch (Exception error) { SetModelStatus(owner, "Could not open installed renders: " + BatchError(error).Message); }
            };
            button.Click -= previous;
            button.Click += replacement;
            installedRenderControls.Add(owner);
            owner.Closed += (sender, args) =>
            {
                button.Click -= replacement;
                installedRenderControls.Remove(owner);
            };
        }

        private static void OpenInstalledRenders(Window owner)
        {
            Window existing;
            if (installedRenderWindows.TryGetValue(owner, out existing)) { existing.Activate(); return; }
            Type type = owner.GetType();
            string game = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
            if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game)) { SetModelStatus(owner, "Choose your installed WWE 2K26 game folder before opening installed renders."); return; }
            string folder = Path.Combine(Path.GetFullPath(game), "Plugins", "GorillaRenders");
            var background = owner.TryFindResource("Bg") as Brush ?? new SolidColorBrush(Color.FromRgb(22, 22, 27));
            var surface = owner.TryFindResource("Surface") as Brush ?? new SolidColorBrush(Color.FromRgb(29, 29, 35));
            var border = owner.TryFindResource("Line") as Brush ?? new SolidColorBrush(Color.FromRgb(65, 65, 76));
            var text = owner.TryFindResource("Text") as Brush ?? Brushes.White;
            var muted = owner.TryFindResource("Muted") as Brush ?? new SolidColorBrush(Color.FromRgb(170, 170, 184));
            var dialog = new Window { Owner = owner, Title = "Installed renders", Width = 1100, Height = 650, MinWidth = 870, MinHeight = 470,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Icon = owner.Icon,
                Background = background, Foreground = text, FontFamily = owner.FontFamily, FontSize = 13 };
            installedRenderWindows[owner] = dialog;
            var grid = new Grid { Margin = new Thickness(18) };
            for (int index = 0; index < 6; index++) grid.RowDefinitions.Add(new RowDefinition { Height = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            dialog.Content = grid;
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            heading.Children.Add(new TextBlock { Text = "INSTALLED RENDERS", Foreground = muted, FontSize = 13 });
            heading.Children.Add(new TextBlock { Text = "Check several renders, or use Ctrl/Shift to select rows. Apply one screen setting to the selected files.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 5) });
            heading.Children.Add(new TextBlock { Text = folder, Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(heading);
            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var refresh = InstalledRenderButton("Refresh"); DockPanel.SetDock(refresh, Dock.Right); searchRow.Children.Add(refresh);
            var openFolder = InstalledRenderButton("Open installed folder"); DockPanel.SetDock(openFolder, Dock.Right); searchRow.Children.Add(openFolder);
            var search = new TextBox { MinWidth = 200, Margin = new Thickness(0, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Search wrestler name, ID, taunt or filename" };
            searchRow.Children.Add(search); Grid.SetRow(searchRow, 1); grid.Children.Add(searchRow);
            var table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                CanUserResizeRows = false, CanUserSortColumns = true, SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow, IsReadOnly = true, EnableRowVirtualization = true, EnableColumnVirtualization = true,
                HeadersVisibility = DataGridHeadersVisibility.Column, Background = surface, Foreground = text, RowBackground = surface,
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(34, 34, 41)), BorderBrush = border,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = border, FrozenColumnCount = 2 };
            ScrollViewer.SetCanContentScroll(table, true); VirtualizingPanel.SetIsVirtualizing(table, true); VirtualizingPanel.SetVirtualizationMode(table, VirtualizationMode.Recycling);
            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, surface)); headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, text));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 6, 7, 6))); table.ColumnHeaderStyle = headerStyle;
            var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Details")));
            table.RowStyle = rowStyle;
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(UIElement.IsEnabledProperty, new Binding("IsValid"));
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            table.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 35, CellTemplate = new DataTemplate { VisualTree = check } });
            AddInstalledRenderColumn(table, "ID", "Id", 65); AddInstalledRenderColumn(table, "Wrestler", "Name", 190);
            AddInstalledRenderColumn(table, "Taunt", "Taunt", 175); AddInstalledRenderColumn(table, "Ending", "Ending", 130);
            AddInstalledRenderColumn(table, "Shown on", "Screens", 135);
            table.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new Binding("File"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 200 });
            Grid.SetRow(table, 2); grid.Children.Add(table);
            var count = new TextBlock { Margin = new Thickness(0, 10, 0, 8), Foreground = muted };
            Grid.SetRow(count, 3); grid.Children.Add(count);
            var status = new TextBlock { Text = "Reading installed renders…", TextWrapping = TextWrapping.Wrap, MinHeight = 38 };
            var statusScroll = new ScrollViewer { Content = status, MaxHeight = 135, Margin = new Thickness(0, 0, 0, 9), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(statusScroll, 4); grid.Children.Add(statusScroll);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Button selectAll = InstalledRenderButton("Select all shown"), clear = InstalledRenderButton("Clear"), cancelScan = InstalledRenderButton("Cancel scan");
            actions.Children.Add(selectAll); actions.Children.Add(clear); actions.Children.Add(cancelScan);
            actions.Children.Add(new TextBlock { Text = "Show on", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
            var showOn = new ComboBox { MinWidth = 170, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Change the screen setting for the checked installed movies. No new render is made." };
            showOn.Items.Add(new ComboBoxItem { Content = "Roster selection only", Tag = InstalledRenderScreenMode.RosterSelection });
            showOn.Items.Add(new ComboBoxItem { Content = "Match cards only", Tag = InstalledRenderScreenMode.MatchCard });
            showOn.Items.Add(new ComboBoxItem { Content = "Both screens", Tag = InstalledRenderScreenMode.Both }); showOn.SelectedIndex = 0;
            actions.Children.Add(showOn);
            var apply = InstalledRenderButton("Apply to selected"); actions.Children.Add(apply);
            var close = InstalledRenderButton("Close"); actions.Children.Add(close); Grid.SetRow(actions, 5); grid.Children.Add(actions);
            var rows = new List<InstalledRenderRow>();
            ListCollectionView view = null;
            bool closed = false, changing = false, scanning = false, closeRequested = false, ownerCloseRequested = false, selecting = false;
            int scanRevision = 0;
            CancellationTokenSource scanCancellation = null;
            Func<InstalledRenderRow, bool> shown = row => string.IsNullOrWhiteSpace(search.Text)
                || (row.Name ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                || row.Id.ToString(CultureInfo.InvariantCulture).Contains(search.Text.Trim())
                || (row.Taunt ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                || (row.File ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
            Action controls = () =>
            {
                int selected = rows.Count(row => row.IsValid && row.Selected), hidden = rows.Count(row => row.IsValid && row.Selected && !shown(row));
                count.Text = rows.Count.ToString(CultureInfo.InvariantCulture) + " installed file(s) · " + selected.ToString(CultureInfo.InvariantCulture) + " selected"
                    + (hidden == 0 ? "" : " (" + hidden.ToString(CultureInfo.InvariantCulture) + " hidden by search)");
                searchRow.IsEnabled = table.IsEnabled = showOn.IsEnabled = !changing && !scanning;
                selectAll.IsEnabled = clear.IsEnabled = !changing && !scanning && rows.Count != 0;
                apply.IsEnabled = !changing && !scanning && selected != 0;
                apply.Content = selected == 0 ? "Apply to selected" : "Apply to " + selected.ToString(CultureInfo.InvariantCulture) + " selected";
                cancelScan.IsEnabled = scanning && !changing;
            };
            Action refreshSelection = () =>
            {
                selecting = true;
                try
                {
                    table.UnselectAll();
                    foreach (InstalledRenderRow row in rows.Where(row => row.Selected && row.IsValid && shown(row))) table.SelectedItems.Add(row);
                }
                finally { selecting = false; }
                controls();
            };
            Action refreshFilter = () =>
            {
                selecting = true;
                try { view?.Refresh(); }
                finally { selecting = false; }
                refreshSelection();
            };
            check.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((sender, args) =>
            {
                var row = (sender as FrameworkElement)?.DataContext as InstalledRenderRow;
                if (row == null || !row.IsValid || changing || scanning || selecting) return;
                // DataGridCell's class handler also sees handled mouse events and
                // can replace the selection. Keep checkbox changes canonical until
                // routing finishes, then restore all visible selected rows together.
                // dotnet/wpf: DataGridCell.OnAnyMouseLeftButtonDown checks focus
                // and Ctrl selection even when the mouse event is already handled.
                selecting = true; row.Selected = !row.Selected; args.Handled = true;
                try { dialog.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    selecting = false;
                    if (!closed) refreshSelection();
                })); }
                catch (InvalidOperationException) { selecting = false; }
            }));
            table.SelectionChanged += (sender, args) =>
            {
                if (selecting) return;
                selecting = true;
                try
                {
                    foreach (InstalledRenderRow row in args.RemovedItems.OfType<InstalledRenderRow>()) row.Selected = false;
                    foreach (InstalledRenderRow row in args.AddedItems.OfType<InstalledRenderRow>())
                        if (row.IsValid) row.Selected = true;
                        else table.SelectedItems.Remove(row);
                }
                finally { selecting = false; }
                controls();
            };
            Func<Task> scan = async () =>
            {
                if (closed || changing) return;
                scanCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); scanCancellation = cancellation;
                CancellationToken token = cancellation.Token; int revision = ++scanRevision;
                var selected = new HashSet<string>(rows.Where(row => row.Selected).Select(row => row.Entry.Path), StringComparer.OrdinalIgnoreCase);
                scanning = true; controls(); status.Text = "Reading installed .gmr files… You can cancel or close this window.";
                try
                {
                    CompletedRenderCatalogReport result = await Task.Run(() => ReadInstalledRenderRows(folder, token), token);
                    if (closed || token.IsCancellationRequested || revision != scanRevision) return;
                    rows = result.Entries.Select(entry => new InstalledRenderRow { Entry = entry, Selected = entry.IsValid && selected.Contains(entry.Path) }).ToList();
                    foreach (InstalledRenderRow row in rows) row.PropertyChanged += (sender, args) =>
                    {
                        if (selecting) return;
                        selecting = true;
                        try
                        {
                            if (row.Selected && row.IsValid && shown(row)) { if (!table.SelectedItems.Contains(row)) table.SelectedItems.Add(row); }
                            else table.SelectedItems.Remove(row);
                        }
                        finally { selecting = false; }
                        controls();
                    };
                    selecting = true;
                    try
                    {
                        view = new ListCollectionView(rows) { Filter = item => shown((InstalledRenderRow)item) };
                        view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                        view.SortDescriptions.Add(new SortDescription("Id", ListSortDirection.Ascending)); table.ItemsSource = view;
                    }
                    finally { selecting = false; }
                    refreshSelection();
                    int unavailable = rows.Count(row => !row.IsValid);
                    status.Text = rows.Count == 0 ? "No renders are installed in this game's GorillaRenders folder. Use Completed renders… to install saved files."
                        : "Choose renders and a screen setting, then Apply to selected. Existing installed files are backed up before changes."
                            + (unavailable == 0 ? "" : " " + unavailable.ToString(CultureInfo.InvariantCulture) + " unavailable file(s) cannot be changed; hover a row for details.");
                    if (!result.Complete) status.Text += " The inventory was incomplete; no unseen files are selected.";
                    if (result.Warnings.Count != 0) status.Text += "\n" + string.Join("\n", result.Warnings.Take(6));
                }
                catch (OperationCanceledException) { if (!closed && revision == scanRevision) status.Text = "Scan canceled. Previously listed files remain available."; }
                catch (Exception error) { if (!closed && revision == scanRevision) status.Text = "Could not read installed renders: " + BatchError(error).Message; }
                finally
                {
                    if (revision == scanRevision) { scanning = false; if (!closed) controls(); }
                    if (ReferenceEquals(scanCancellation, cancellation)) scanCancellation = null;
                    cancellation.Dispose();
                }
            };
            search.TextChanged += (sender, args) => refreshFilter();
            selectAll.Click += (sender, args) => { selecting = true; try { foreach (InstalledRenderRow row in rows.Where(row => row.IsValid && shown(row))) row.Selected = true; } finally { selecting = false; } refreshSelection(); };
            clear.Click += (sender, args) => { selecting = true; try { foreach (InstalledRenderRow row in rows) row.Selected = false; } finally { selecting = false; } refreshSelection(); };
            refresh.Click += async (sender, args) => await scan(); cancelScan.Click += (sender, args) => scanCancellation?.Cancel();
            openFolder.Click += (sender, args) =>
            {
                try { if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); else status.Text = "No GorillaRenders folder exists yet. Install a saved render first."; }
                catch (Exception error) { status.Text = BatchError(error).Message; }
            };
            apply.Click += async (sender, args) =>
            {
                if (closed || changing || scanning) return;
                try { await CancelAndDrainAutomaticRandyPreviewAsync(owner); }
                catch (Exception error) { if (!closed) status.Text = "Could not finish the current preview: " + BatchError(error).Message; return; }
                if (closed || changing || scanning || !owner.IsLoaded) return;
                if (BatchNativeBusy(owner)) { status.Text = "Wait for the current model, render or setup to finish before changing installed renders."; return; }
                var chosen = rows.Where(row => row.IsValid && row.Selected).Select(row => new InstalledRenderScreenChoice { Path = row.Entry.Path, WrestlerId = row.Id }).ToArray();
                if (chosen.Length == 0) return;
                var unlocked = type.GetMethod("Unlocked", InstanceMembers, null, Type.EmptyTypes, null);
                try
                {
                    if (unlocked == null) { status.Text = "Studio's native unlock check is unavailable. No installed files were changed."; return; }
                    if (!(bool)unlocked.Invoke(owner, null)) { status.Text = "Complete Studio's sign-in/unlock step, then apply the selected files."; return; }
                }
                catch (Exception error) { status.Text = "Studio could not authorize the update: " + BatchError(error).Message; return; }
                string currentGame = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
                if (!string.Equals(currentGame, game, StringComparison.OrdinalIgnoreCase)) { status.Text = "The selected game folder changed. Close and reopen Installed renders before applying changes."; return; }
                var choice = (ComboBoxItem)showOn.SelectedItem;
                var mode = (InstalledRenderScreenMode)choice.Tag; string label = choice.Content as string;
                changing = true; controls();
                var enabled = new BatchEnabledRestore(owner.Content as UIElement);
                var bodyEnabled = new BatchEnabledRestore(type.GetField("BodyGrid", InstanceMembers)?.GetValue(owner) as DependencyObject);
                var busy = type.GetField("Busy", InstanceMembers);
                bool ownsBusy = busy != null && busy.FieldType == typeof(bool) && !(bool)busy.GetValue(owner);
                var content = owner.Content as UIElement;
                if (content != null) content.IsEnabled = false;
                if (ownsBusy) busy.SetValue(owner, true);
                bool rescan = false; string summary = null;
                try
                {
                    status.Text = "Applying " + label + " to " + chosen.Length.ToString(CultureInfo.InvariantCulture) + " installed render(s)… Existing files will be backed up. No new render is made.";
                    InstalledRenderScreenReport result = await Task.Run(() => InstalledRenderScreenPlan.Apply(currentGame, chosen, mode, CancellationToken.None));
                    summary = result.Summary;
                    if (result.BackupDirectory != null) summary += "\nPrevious installed files backed up to: " + result.BackupDirectory;
                    if (result.Failed.Count != 0) summary += "\n" + string.Join("\n", result.Failed);
                    if (result.Warnings.Count != 0) summary += "\n" + string.Join("\n", result.Warnings);
                    summary += "\nSaved output files are retained. Match cards still require compatible screen layouts; setup and baking are separate.";
                    status.Text = summary; SetModelStatus(owner, result.Summary + " Show on: " + label + ".");
                    rescan = result.Changed > 0 || result.Unchanged > 0 || !result.RollbackComplete;
                    if (result.Failed.Count == 0) { selecting = true; try { foreach (InstalledRenderRow row in rows) row.Selected = false; } finally { selecting = false; } }
                }
                catch (Exception error) { status.Text = "Screen settings were not applied: " + BatchError(error).Message; }
                finally
                {
                    if (ownsBusy) busy.SetValue(owner, false);
                    bodyEnabled.Restore(); enabled.Restore(); changing = false; controls();
                    if (closeRequested) dialog.Close();
                    if (ownerCloseRequested && owner.IsLoaded) owner.Close();
                }
                if (rescan && !closed && !closeRequested) { await scan(); if (!closed) status.Text = summary + "\n\n" + status.Text; }
            };
            CancelEventHandler ownerClosing = (sender, args) =>
            {
                if (!changing) return;
                args.Cancel = true; closeRequested = ownerCloseRequested = true;
                status.Text = "Finishing the current file update before closing.";
            };
            owner.Closing += ownerClosing;
            dialog.Closing += (sender, args) =>
            {
                if (!changing) return;
                args.Cancel = true; closeRequested = true; status.Text = "Finishing the current file update before closing.";
            };
            dialog.Closed += (sender, args) =>
            {
                closed = true; scanCancellation?.Cancel(); owner.Closing -= ownerClosing; installedRenderWindows.Remove(owner);
            };
            close.Click += (sender, args) => dialog.Close(); dialog.Loaded += async (sender, args) => await scan();
            controls(); dialog.Show();
        }

        private static CompletedRenderCatalogReport ReadInstalledRenderRows(string folder, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var paths = new List<string>();
            if (Directory.Exists(folder))
            {
                // Only the native plugin's top-level movie folder. Never traverse a
                // linked parent, backup tree or nested GMR directory.
                for (DirectoryInfo part = new DirectoryInfo(folder); part != null; part = part.Parent)
                    if ((part.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The installed render folder is linked. Choose the real game folder in Studio.");
                foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase)) continue;
                    paths.Add(path);
                    if (paths.Count > 8192) throw new IOException("The installed folder contains too many GMR files to list at once.");
                }
            }
            return CompletedRenderCatalog.Scan(paths, null, cancellation);
        }

        private static Button InstalledRenderButton(string label)
        { return new Button { Content = label, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 7, 0) }; }

        private static void AddInstalledRenderColumn(DataGrid table, string label, string property, double width)
        { table.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), IsReadOnly = true, Width = width }); }
    }
}
