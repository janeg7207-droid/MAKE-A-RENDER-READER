using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<Window, Window> completedRenderWindows = new Dictionary<Window, Window>();

        private sealed class CompletedRenderRow : INotifyPropertyChanged
        {
            internal CompletedRenderEntry Entry;
            private bool selected;
            public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
            public bool IsValid { get { return Entry.IsValid; } }
            public int Id { get { return Entry.WrestlerId; } }
            public string Name { get { return Entry.Name; } }
            public string Taunt { get { return Entry.Taunt; } }
            public string Attire { get { return Entry.AttireName; } }
            public string File { get { return System.IO.Path.GetFileName(Entry.PublishedPath ?? Entry.Path); } }
            public string Details { get { return (Entry.Error == null ? Entry.Path : Entry.Error + "\n" + Entry.Path)
                + (string.IsNullOrWhiteSpace(Entry.InstallationDetails) ? "" : "\n" + Entry.InstallationDetails); } }
            public string State
            {
                get
                {
                    if (!Entry.IsValid) return "Unavailable: " + Entry.Error;
                    switch (Entry.InstalledState)
                    {
                        case CompletedRenderInstallState.InstalledIdentical: return "Installed — identical copy";
                        case CompletedRenderInstallState.InstalledDifferent: return "Different version installed";
                        case CompletedRenderInstallState.NeedsComparison: return "Installation not fully compared";
                        default: return "Not installed";
                    }
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private static void AttachCompletedRenderControls(Window owner)
        {
            var install = owner.GetType().GetField("InstallButton", InstanceMembers)?.GetValue(owner) as Button;
            var parent = install?.Parent as Panel;
            if (parent == null || parent.Children.OfType<Button>().Any(candidate => Equals(candidate.Tag, "GorillaCompletedRenders"))) return;
            var button = new Button { Content = "Completed renders…", Tag = "GorillaCompletedRenders", Style = install.Style,
                FontFamily = install.FontFamily, FontSize = install.FontSize, Margin = new Thickness(0, 5, 0, 0),
                ToolTip = "Find and install previously completed renders, including saved batches, without making them again." };
            parent.Children.Insert(parent.Children.IndexOf(install) + 1, button);
            button.Click += async (sender, args) => await OpenCompletedRendersAsync(owner);
        }

        private static async Task OpenCompletedRendersAsync(Window owner)
        {
            Window existing;
            if (completedRenderWindows.TryGetValue(owner, out existing)) { existing.Activate(); return; }
            await CancelAndDrainAutomaticRandyPreviewAsync(owner);
            if (!owner.IsLoaded) return;
            if (completedRenderWindows.TryGetValue(owner, out existing)) { existing.Activate(); return; }
            if (BatchNativeBusy(owner)) { SetModelStatus(owner, "Wait for the current model, render or setup to finish before opening completed renders."); return; }
            Type type = owner.GetType();
            string output = type.GetField("Output", InstanceMembers)?.GetValue(owner) as string;
            if (string.IsNullOrWhiteSpace(output)) output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Gorilla Studio", "Renders");
            string game = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { output, Path.Combine(StorageDirectory, "batches") };
            string last = type.GetField("LastGmr", InstanceMembers)?.GetValue(owner) as string;
            if (!string.IsNullOrWhiteSpace(last)) roots.Add(last);
            var dialog = new Window { Owner = owner, Title = "Gorilla Studio — completed renders", Width = 1080, Height = 650,
                MinWidth = 850, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White, FontFamily = owner.FontFamily, FontSize = 13 };
            completedRenderWindows[owner] = dialog;
            var grid = new Grid { Margin = new Thickness(18) };
            for (int index = 0; index < 5; index++) grid.RowDefinitions.Add(new RowDefinition { Height = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            dialog.Content = grid;
            var explanation = new TextBlock { Text = "Install renders you have already made. Choose one version per wrestler slot. Existing game files are backed up when replaced.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            grid.Children.Add(explanation);
            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            var sourceButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var addFiles = CompletedRenderButton("Add files…");
            var addFolder = CompletedRenderButton("Add folder…");
            var refresh = CompletedRenderButton("Refresh");
            sourceButtons.Children.Add(addFiles); sourceButtons.Children.Add(addFolder); sourceButtons.Children.Add(refresh);
            DockPanel.SetDock(sourceButtons, Dock.Right); searchRow.Children.Add(sourceButtons);
            var hideInstalled = new CheckBox { Content = "Hide installed copies", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(hideInstalled, Dock.Right); searchRow.Children.Add(hideInstalled);
            var search = new TextBox { MinWidth = 160, Margin = new Thickness(0, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Search wrestler name, ID, taunt, attire or filename" };
            searchRow.Children.Add(search); Grid.SetRow(searchRow, 1); grid.Children.Add(searchRow);
            var table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                CanUserResizeRows = false, CanUserSortColumns = true, SelectionMode = DataGridSelectionMode.Extended,
                EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
                Background = new SolidColorBrush(Color.FromRgb(29, 29, 35)), Foreground = Brushes.White,
                RowBackground = new SolidColorBrush(Color.FromRgb(29, 29, 35)), AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(34, 34, 41)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 76)), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(48, 48, 58)), FrozenColumnCount = 2 };
            ScrollViewer.SetCanContentScroll(table, true);
            VirtualizingPanel.SetIsVirtualizing(table, true); VirtualizingPanel.SetVirtualizationMode(table, VirtualizationMode.Recycling);
            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(39, 39, 46))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 6, 7, 6))); table.ColumnHeaderStyle = headerStyle;
            var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Details"))); table.RowStyle = rowStyle;
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Selected") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(UIElement.IsEnabledProperty, new Binding("IsValid"));
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            table.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 35, CellTemplate = new DataTemplate { VisualTree = check } });
            AddCompletedRenderColumn(table, "ID", "Id", 65);
            AddCompletedRenderColumn(table, "Wrestler", "Name", 170);
            AddCompletedRenderColumn(table, "Taunt", "Taunt", 150);
            AddCompletedRenderColumn(table, "Attire", "Attire", 130);
            AddCompletedRenderColumn(table, "Status", "State", 205);
            table.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new Binding("File"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
            Grid.SetRow(table, 2); grid.Children.Add(table);
            var status = new TextBlock { Text = "Finding completed renders…", TextWrapping = TextWrapping.Wrap, MinHeight = 38 };
            var statusScroll = new ScrollViewer { Content = status, Margin = new Thickness(0, 10, 0, 10), MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(statusScroll, 3); grid.Children.Add(statusScroll);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var selectAll = CompletedRenderButton("Select all shown");
            var clear = CompletedRenderButton("Clear");
            var open = CompletedRenderButton("Open file folder");
            var cancelScan = CompletedRenderButton("Cancel scan");
            var checkSetup = CompletedRenderButton("Check match-card setup…");
            checkSetup.ToolTip = "Read the current setup report. Running setup or enabling the plugin remains a separate explicit action.";
            var installSelected = CompletedRenderButton("Install selected"); installSelected.IsEnabled = false;
            var close = CompletedRenderButton("Close");
            foreach (Button action in new[] { selectAll, clear, open, cancelScan, checkSetup, installSelected, close }) actions.Children.Add(action);
            Grid.SetRow(actions, 4); grid.Children.Add(actions);
            bool closed = false, installing = false, closeRequested = false, ownerCloseRequested = false, scanning = false;
            int scanRevision = 0;
            CancellationTokenSource scanCancellation = null;
            var rows = new List<CompletedRenderRow>();
            ListCollectionView view = null;
            Action applyFilter = () => { if (view != null) view.Refresh(); };
            Func<CompletedRenderRow, bool> shown = row =>
                !(hideInstalled.IsChecked == true && row.Entry.InstalledState == CompletedRenderInstallState.InstalledIdentical)
                && (string.IsNullOrWhiteSpace(search.Text) || (row.Name ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                    || row.Id.ToString(CultureInfo.InvariantCulture).Contains(search.Text.Trim())
                    || (row.Taunt ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                    || (row.Attire ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0
                    || (row.File ?? "").IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            Action controls = () =>
            {
                searchRow.IsEnabled = !installing;
                table.IsEnabled = !installing;
                selectAll.IsEnabled = clear.IsEnabled = !installing && !scanning;
                open.IsEnabled = !installing && table.SelectedItem is CompletedRenderRow;
                installSelected.IsEnabled = !installing && !scanning && rows.Any(row => row.Selected && row.IsValid);
                cancelScan.IsEnabled = scanning && !installing;
                checkSetup.IsEnabled = !scanning && !installing;
            };
            Func<Task> scan = async () =>
            {
                if (closed || installing) return;
                scanCancellation?.Cancel();
                var cancellation = new CancellationTokenSource(); scanCancellation = cancellation;
                CancellationToken token = cancellation.Token;
                int revision = ++scanRevision; scanning = true; controls();
                string scanGame = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
                var selected = new HashSet<string>(rows.Where(row => row.Selected).Select(row => row.Entry.Path), StringComparer.OrdinalIgnoreCase);
                string[] sources = roots.ToArray();
                status.Text = "Finding completed files and comparing installed copies… You can cancel or close this window.";
                try
                {
                    CompletedRenderCatalogReport result = await Task.Run(() => CompletedRenderCatalog.Scan(sources, scanGame, token), token);
                    if (closed || token.IsCancellationRequested || revision != scanRevision) return;
                    game = scanGame;
                    rows = result.Entries.Select(entry => new CompletedRenderRow { Entry = entry, Selected = entry.IsValid
                        && entry.InstalledState != CompletedRenderInstallState.InstalledIdentical && selected.Contains(entry.Path) }).ToList();
                    foreach (CompletedRenderRow row in rows) row.PropertyChanged += (sender, args) => controls();
                    view = new ListCollectionView(rows) { Filter = item => shown((CompletedRenderRow)item) };
                    view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                    view.SortDescriptions.Add(new SortDescription("Id", ListSortDirection.Ascending));
                    table.ItemsSource = view;
                    int ready = rows.Count(row => row.IsValid), installed = rows.Count(row => row.Entry.InstalledState == CompletedRenderInstallState.InstalledIdentical);
                    status.Text = ready.ToString(CultureInfo.InvariantCulture) + " completed render(s); " + installed.ToString(CultureInfo.InvariantCulture) + " identical installed copy/copies."
                        + (result.Complete ? "" : " The scan was incomplete; add a specific folder or file to narrow it.")
                        + (ready == 0 ? " Use Add files… or Add folder… to locate older renders." : " Check the files you want, then Install selected.");
                    if (result.Warnings.Count != 0) status.Text += "\n" + string.Join("\n", result.Warnings.Take(8));
                }
                catch (OperationCanceledException) { if (!closed && revision == scanRevision) status.Text = "Scan canceled. Previously listed files remain available."; }
                catch (Exception error) { if (!closed && revision == scanRevision) status.Text = "Could not read completed renders: " + BatchError(error).Message; }
                finally
                {
                    if (revision == scanRevision) { scanning = false; if (!closed) controls(); }
                    if (ReferenceEquals(scanCancellation, cancellation)) scanCancellation = null;
                    cancellation.Dispose();
                }
            };
            search.TextChanged += (sender, args) => applyFilter();
            hideInstalled.Checked += (sender, args) => applyFilter(); hideInstalled.Unchecked += (sender, args) => applyFilter();
            table.SelectionChanged += (sender, args) => controls();
            selectAll.Click += (sender, args) => { foreach (CompletedRenderRow row in rows.Where(row => row.IsValid && shown(row))) row.Selected = true; controls(); };
            clear.Click += (sender, args) => { foreach (CompletedRenderRow row in rows) row.Selected = false; controls(); };
            refresh.Click += async (sender, args) => await scan();
            cancelScan.Click += (sender, args) => scanCancellation?.Cancel();
            checkSetup.Click += (sender, args) =>
            {
                if (closed || scanning || installing) return;
                if (BatchNativeBusy(owner)) { status.Text = "Wait for the current model, render or setup to finish before checking match-card setup."; return; }
                var selected = table.SelectedItem as CompletedRenderRow;
                if (selected == null || !selected.IsValid) selected = rows.FirstOrDefault(row => row.Selected && row.IsValid);
                if (selected == null) { status.Text = "Select a completed render row, then check its match-card setup."; return; }
                ShowPlaybackCheck(owner, selected.Entry.Path, selected.Id);
            };
            addFiles.Click += async (sender, args) =>
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose completed Gorilla renders", Filter = "Gorilla renders (*.gmr)|*.gmr", Multiselect = true,
                    CheckFileExists = true, InitialDirectory = Directory.Exists(output) ? output : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
                if (picker.ShowDialog(dialog) != true) return;
                foreach (string path in picker.FileNames) roots.Add(path);
                await scan();
            };
            addFolder.Click += async (sender, args) =>
            {
                string folder = FolderLocationDialog.Choose(new NativeOwner(new WindowInteropHelper(dialog).Handle), "Find completed renders",
                    "Choose or paste a folder containing your completed .gmr files.", output);
                if (folder == null) return;
                roots.Add(folder); await scan();
            };
            open.Click += (sender, args) =>
            {
                var row = table.SelectedItem as CompletedRenderRow;
                if (row == null) return;
                try { Process.Start(new ProcessStartInfo { FileName = Path.GetDirectoryName(row.Entry.PublishedPath ?? row.Entry.Path), UseShellExecute = true }); }
                catch (Exception error) { status.Text = BatchError(error).Message; }
            };
            installSelected.Click += async (sender, args) =>
            {
                if (installing || scanning) return;
                if (BatchNativeBusy(owner)) { status.Text = "Wait for the current model, render or setup to finish, then install the selected files."; return; }
                var chosen = rows.Where(row => row.Selected && row.IsValid).ToArray();
                if (chosen.Length == 0) return;
                var unlocked = type.GetMethod("Unlocked", InstanceMembers, null, Type.EmptyTypes, null);
                if (unlocked == null || !(bool)unlocked.Invoke(owner, null)) return;
                int[] duplicates = chosen.GroupBy(row => row.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
                if (duplicates.Length != 0) { status.Text = "Choose one render per wrestler ID before installing. Multiple versions are checked for: " + string.Join(", ", duplicates) + ". Other outfits stay saved."; return; }
                string currentGame = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
                if (!Directory.Exists(currentGame)) { status.Text = "Choose your installed WWE 2K26 game folder in Studio before installing."; return; }
                if (!string.Equals(currentGame, game, StringComparison.OrdinalIgnoreCase)) { status.Text = "The game folder changed. Refresh this list before installing."; return; }
                if (Process.GetProcessesByName("WWE2K26_x64").Length != 0) { status.Text = "Close WWE 2K26 before installing completed renders."; return; }
                var jobs = chosen.Select(row => new BatchRenderJob { Entry = new RosterEntry { Id = row.Id, Name = row.Name },
                    RenderPath = row.Entry.Path, OutputDirectory = row.Entry.OutputDirectory, PublishedRenderPath = row.Entry.PublishedPath,
                    AttireName = row.Entry.AttireName, Status = "Complete" }).ToArray();
                string finalSummary = null;
                bool rescan = false;
                installing = true; controls();
                var content = owner.Content as UIElement;
                var enabled = new BatchEnabledRestore(content);
                var bodyEnabled = new BatchEnabledRestore(type.GetField("BodyGrid", InstanceMembers)?.GetValue(owner) as DependencyObject);
                var busy = type.GetField("Busy", InstanceMembers);
                bool ownsBusy = busy != null && busy.FieldType == typeof(bool) && !(bool)busy.GetValue(owner);
                if (content != null) content.IsEnabled = false;
                if (ownsBusy) busy.SetValue(owner, true);
                try
                {
                    status.Text = "Installing selected completed renders… Existing replacements are backed up. No render is running.";
                    BatchRenderInstallReport report = await Task.Run(() =>
                    {
                        BatchRenderInstallReport installed = BatchRenderInstaller.Install(AppDomain.CurrentDomain.BaseDirectory, currentGame, jobs);
                        if (installed.Installed > 0)
                            foreach (BatchRenderJob job in jobs) CopyInstalledSelectionLoopPolicy(job.RenderPath, currentGame);
                        return installed;
                    });
                    string summary = report.Summary;
                    if (report.BackupDirectory != null) summary += "\nPrevious files backed up to: " + report.BackupDirectory;
                    if (report.Failed.Count != 0) summary += "\n" + string.Join("\n", report.Failed);
                    if (report.Installed > 0) summary += "\nScreen layout setup and baking remain separate actions. Use Check match-card setup… for a read-only diagnosis.";
                    status.Text = summary;
                    finalSummary = summary; rescan = report.Installed > 0;
                    SetModelStatus(owner, report.Summary + (report.Installed > 0 ? " Use Check match-card setup… for diagnosis; screen setup and baking remain separate." : " Open Completed renders for details."));
                }
                catch (Exception error) { status.Text = "Installation stopped: " + BatchError(error).Message; }
                finally
                {
                    if (ownsBusy) busy.SetValue(owner, false);
                    bodyEnabled.Restore(); enabled.Restore(); installing = false;
                    controls();
                    if (closeRequested) dialog.Close();
                    if (ownerCloseRequested && owner.IsLoaded) owner.Close();
                }
                if (rescan && !closed && !closeRequested)
                {
                    await scan();
                    if (!closed) status.Text = finalSummary + "\n\n" + status.Text;
                }
            };
            CancelEventHandler ownerClosing = (sender, args) =>
            {
                if (!installing) return;
                args.Cancel = true; closeRequested = true; ownerCloseRequested = true;
                status.Text = "Finishing the current installation before closing. Completed source renders are kept.";
            };
            owner.Closing += ownerClosing;
            dialog.Closing += (sender, args) =>
            {
                if (!installing) return;
                args.Cancel = true; closeRequested = true;
                status.Text = "Finishing the current installation before closing. Completed source renders are kept.";
            };
            dialog.Closed += (sender, args) =>
            {
                closed = true; scanCancellation?.Cancel(); owner.Closing -= ownerClosing; completedRenderWindows.Remove(owner);
            };
            close.Click += (sender, args) => dialog.Close();
            dialog.Loaded += async (sender, args) => await scan();
            controls(); dialog.Show();
        }

        private static Button CompletedRenderButton(string label)
        {
            return new Button { Content = label, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 7, 0) };
        }

        private static void AddCompletedRenderColumn(DataGrid table, string label, string property, double width)
        {
            table.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), IsReadOnly = true, Width = width });
        }
    }
}
