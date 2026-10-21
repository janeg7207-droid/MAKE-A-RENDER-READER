using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<Window, Window> batchWindows = new Dictionary<Window, Window>();
        private static readonly Dictionary<object, BatchRenderLifetime> batchRenderLifetimes = new Dictionary<object, BatchRenderLifetime>();

        internal static bool BatchRenderCancellationRequested(object owner)
        {
            BatchRenderLifetime lifetime;
            return owner != null && batchRenderLifetimes.TryGetValue(owner, out lifetime) && lifetime.IsCancellationRequested;
        }

        internal static void TrackBatchRenderProcess(object owner, Process process)
        {
            BatchRenderLifetime lifetime;
            if (owner != null && process != null && batchRenderLifetimes.TryGetValue(owner, out lifetime)) lifetime.TrackProcess(process);
        }

        private sealed class BatchEnabledRestore
        {
            private readonly DependencyObject element;
            private readonly object localValue;
            private readonly BindingBase binding;
            internal BatchEnabledRestore(DependencyObject element)
            {
                this.element = element;
                localValue = element?.ReadLocalValue(UIElement.IsEnabledProperty);
                binding = element == null ? null : BindingOperations.GetBindingBase(element, UIElement.IsEnabledProperty);
            }
            internal void Restore()
            {
                if (element == null) return;
                if (binding != null) BindingOperations.SetBinding(element, UIElement.IsEnabledProperty, binding);
                else if (ReferenceEquals(localValue, DependencyProperty.UnsetValue)) element.ClearValue(UIElement.IsEnabledProperty);
                else element.SetValue(UIElement.IsEnabledProperty, localValue);
            }
        }

        private sealed class BatchRosterRow
        {
            internal RosterEntry Entry;
            internal bool Selected;
            internal List<BatchAttireSelection> Attires;
        }

        private static void AttachBatchControls(Window owner)
        {
            var make = owner.GetType().GetField("MakeButton", InstanceMembers)?.GetValue(owner) as Button;
            var parent = make?.Parent as Panel;
            if (parent == null || parent.Children.OfType<Button>().Any(button => Equals(button.Tag, "GorillaBatchMake"))) return;
            var batch = new Button { Content = "Batch MAKE…", Tag = "GorillaBatchMake", Style = make.Style,
                FontFamily = make.FontFamily, FontSize = make.FontSize, Padding = make.Padding, Margin = new Thickness(0, 5, 0, 3),
                ToolTip = "Select several wrestlers and make them one after another with the current taunt and settings." };
            parent.Children.Insert(parent.Children.IndexOf(make) + 1, batch);
            batch.Click += async (sender, args) => await OpenBatchRosterAsync(owner);
            owner.Closed += (sender, args) =>
            {
                Window dialog;
                if (batchWindows.TryGetValue(owner, out dialog)) dialog.Close();
                batchWindows.Remove(owner);
            };
        }

        private static bool BatchNativeBusy(Window owner)
        {
            foreach (string name in new[] { "Busy", "GameBusy" })
                if (owner.GetType().GetField(name, InstanceMembers)?.GetValue(owner) is bool value && value) return true;
            return rosterWindowsBusy.Contains(owner);
        }

        private static async Task OpenBatchRosterAsync(Window owner)
        {
            Window existing;
            if (batchWindows.TryGetValue(owner, out existing)) { existing.Activate(); return; }
            await CancelAndDrainAutomaticRandyPreviewAsync(owner);
            if (!owner.IsLoaded) return;
            if (BatchNativeBusy(owner)) { SetModelStatus(owner, "Wait for the current model or render to finish before opening Batch MAKE."); return; }
            Type type = owner.GetType();
            var unlocked = type.GetMethod("Unlocked", InstanceMembers, null, Type.EmptyTypes, null);
            if (unlocked == null || !(bool)unlocked.Invoke(owner, null)) return;
            var taunts = type.GetField("TauntList", InstanceMembers)?.GetValue(owner) as ListBox;
            object taunt = taunts?.SelectedItem;
            if (taunt == null) { SetModelStatus(owner, "Pick a taunt first, then open Batch MAKE."); return; }
            var build = type.GetMethods(InstanceMembers).FirstOrDefault(method => method.Name == "BuildArgs" && method.GetParameters().Length == 2);
            var go = type.GetMethod("Go", InstanceMembers, null, new[] { typeof(bool) }, null);
            var setFolder = type.GetMethod("SetFolder", InstanceMembers, null, new[] { typeof(string) }, null);
            if (build == null || go == null || setFolder == null) return;
            string tauntName = taunt.GetType().GetProperty("Name")?.GetValue(taunt, null) as string ?? taunt.ToString();
            string tauntKey = taunt.GetType().GetProperty("Key")?.GetValue(taunt, null) as string;
            if (tauntKey == null) tauntKey = taunt.GetType().GetField("Key", InstanceMembers)?.GetValue(taunt) as string;
            var currentArgs = build.Invoke(owner, new[] { taunt, (object)false }) as IEnumerable<string>;
            if (currentArgs == null) { SetModelStatus(owner, "Tick Roster selection, Match card, or both before starting a batch."); return; }

            var dialog = new Window { Owner = owner, Title = "Batch MAKE", Width = 880, Height = 690, MinWidth = 660, MinHeight = 470,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)),
                Foreground = Brushes.White, FontFamily = owner.FontFamily, FontSize = 13 };
            batchWindows[owner] = dialog;
            var grid = new Grid { Margin = new Thickness(18) };
            for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            dialog.Content = grid;
            var explanation = new TextBlock { Text = "Choose wrestlers, then use Choose attires… to render two or more outfits with the same taunt and settings. Each checked attire gets its own file. Jobs run one at a time.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) };
            grid.Children.Add(explanation);
            var settings = new TextBlock { Text = "Taunt: " + tauntName + " · Always fast MAKE · Completed files are saved; installation is separate.",
                TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(162, 187, 208)), Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(settings, 1); grid.Children.Add(settings);
            var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var search = new TextBox { MinWidth = 160, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Search wrestler name or ID" };
            var selectAll = new Button { Content = "Select all shown", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0) };
            var clear = new Button { Content = "Clear", Padding = new Thickness(9, 4, 9, 4) };
            searchRow.Children.Add(search); Grid.SetColumn(selectAll, 1); searchRow.Children.Add(selectAll); Grid.SetColumn(clear, 2); searchRow.Children.Add(clear);
            Grid.SetRow(searchRow, 2); grid.Children.Add(searchRow);
            var list = new ListBox { Background = new SolidColorBrush(Color.FromRgb(29, 29, 35)), Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 76)) };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            Grid.SetRow(list, 3); grid.Children.Add(list);
            var status = new TextBlock { Text = "Reading the current roster and remembered model locations…", TextWrapping = TextWrapping.Wrap,
                MinHeight = 38 };
            var statusScroll = new ScrollViewer { Content = status, Margin = new Thickness(0, 9, 0, 9), MaxHeight = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(statusScroll, 4); grid.Children.Add(statusScroll);
            var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var open = new Button { Content = "Open output folder", IsEnabled = false, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 0) };
            var details = new Button { Content = "Batch details", IsEnabled = false, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 0) };
            var installAll = new Button { Content = "Install completed", IsEnabled = false, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 0) };
            var use = new Button { Content = "Use selected result", IsEnabled = false, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 0) };
            var start = new Button { Content = "MAKE selected", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 7, 0) };
            var stop = new Button { Content = "Close", Padding = new Thickness(9, 6, 9, 6) };
            actions.Children.Add(open); actions.Children.Add(details); actions.Children.Add(installAll); actions.Children.Add(use); actions.Children.Add(start); actions.Children.Add(stop);
            Grid.SetRow(actions, 5); grid.Children.Add(actions);
            bool running = false, cancel = false, loaded = false, showingResults = false, closeRequested = false, closed = false;
            var rosterLoadingCancellation = new CancellationTokenSource();
            CancellationToken rosterLoadingToken = rosterLoadingCancellation.Token;
            BatchRenderLifetime renderLifetime = null;
            Task cancelTask = Task.CompletedTask;
            Action requestCancel = () =>
            {
                cancel = true;
                stop.IsEnabled = false;
                status.Text = renderLifetime == null ? "Finishing the current operation before closing…" : "Canceling the batch render… Waiting for its render processes to stop. Completed files are kept.";
                BatchRenderLifetime active = renderLifetime;
                if (active == null) return;
                cancelTask = Task.Run(async () => await active.CancelAsync());
                cancelTask.ContinueWith(task => owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (closed || !task.IsFaulted) return;
                    status.Text = "The batch process could not be stopped yet. Retrying safely… " + BatchError(task.Exception.GetBaseException()).Message;
                })), TaskContinuationOptions.OnlyOnFaulted);
            };
            Action closeWhenRequested = () => { if (closeRequested && !closed && !running) dialog.Close(); };
            var rows = new List<BatchRosterRow>();
            var inventory = new List<ModelCandidate>();
            var completed = new List<BatchRenderJob>();
            var allJobs = new List<BatchRenderJob>();
            string table = null, runDirectory = null;
            string normalOutputDirectory = type.GetField("Output", InstanceMembers)?.GetValue(owner) as string;
            Func<BatchRosterRow, Task> chooseAttires = null;
            Action refresh = () =>
            {
                if (showingResults) return;
                list.Items.Clear();
                string query = search.Text.Trim();
                foreach (BatchRosterRow row in rows.Where(row => query.Length == 0 || row.Entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || row.Entry.Id.ToString(CultureInfo.InvariantCulture).Contains(query)))
                {
                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    string attireText = row.Attires == null ? "Current/default attire" : row.Attires.Count.ToString(CultureInfo.InvariantCulture) + " attire(s): " + string.Join(", ", row.Attires.Select(attire => attire.DisplayName));
                    var label = new StackPanel();
                    label.Children.Add(new TextBlock { Text = row.Entry.Id.ToString(CultureInfo.InvariantCulture) + "   " + row.Entry.Name });
                    label.Children.Add(new TextBlock { Text = attireText, FontSize = 11, Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = attireText });
                    var check = new CheckBox { Content = label,
                        IsChecked = row.Selected, Tag = row, Foreground = Brushes.White, Margin = new Thickness(5, 4, 5, 4) };
                    check.Checked += (sender, args) => { row.Selected = true; start.IsEnabled = loaded && !running && rows.Any(item => item.Selected); };
                    check.Unchecked += (sender, args) => { row.Selected = false; start.IsEnabled = loaded && !running && rows.Any(item => item.Selected); };
                    var outfits = new Button { Content = "Choose attires…", Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(8, 4, 5, 4), VerticalAlignment = VerticalAlignment.Center };
                    outfits.Click += async (sender, args) => { if (chooseAttires != null) await chooseAttires(row); };
                    rowGrid.Children.Add(check); Grid.SetColumn(outfits, 1); rowGrid.Children.Add(outfits);
                    list.Items.Add(new ListBoxItem { Content = rowGrid, Tag = row, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                start.IsEnabled = loaded && !running && rows.Any(row => row.Selected);
            };
            search.TextChanged += (sender, args) => refresh();
            selectAll.Click += (sender, args) => { foreach (var item in list.Items.OfType<ListBoxItem>()) if (item.Tag is BatchRosterRow row) row.Selected = true; refresh(); };
            clear.Click += (sender, args) => { foreach (var row in rows) row.Selected = false; refresh(); };
            list.SelectionChanged += (sender, args) => use.IsEnabled = !running && list.SelectedItem is ListBoxItem item && item.Tag is BatchRenderJob job && completed.Contains(job);
            stop.Click += (sender, args) => { if (running) requestCancel(); else dialog.Close(); };
            open.Click += (sender, args) => { if (Directory.Exists(normalOutputDirectory)) Process.Start(new ProcessStartInfo { FileName = normalOutputDirectory, UseShellExecute = true }); };
            details.Click += (sender, args) => { if (Directory.Exists(runDirectory)) Process.Start(new ProcessStartInfo { FileName = runDirectory, UseShellExecute = true }); };
            chooseAttires = async row =>
            {
                if (running || !loaded || BatchNativeBusy(owner)) return;
                running = true; cancel = false; searchRow.IsEnabled = false; list.IsEnabled = false; start.IsEnabled = false; stop.IsEnabled = false;
                try
                {
                    status.Text = "Reading attire models for " + row.Entry.Name + "…";
                    DiscoveryAttireSource selectedDiscovery = SelectedDiscoveryAttireSource(owner);
                    DiscoveryAttireCatalogResult discovery = await Task.Run(() => DiscoveryAttireCatalog.Find(row.Entry, inventory, table));
                    if (discovery.References.Count == 0 && discovery.Issues.Count > 0)
                        throw new InvalidOperationException(string.Join("\n", discovery.Issues));
                    List<BatchAttireSelection> available;
                    List<BatchAttireSelection> current = row.Attires;
                    if (discovery.References.Count > 0)
                    {
                        available = await Task.Run(() => BatchRenderPlan.AvailableAttires(row.Entry, discovery));
                        if (available.Count == 0) throw new InvalidOperationException("No linked or local attire models were found for " + row.Entry.Name
                            + ".\n" + string.Join("\n", discovery.Issues));
                        if (current == null && selectedDiscovery != null && selectedDiscovery.LogicalId == row.Entry.Id)
                        {
                            string key = DiscoveryAttireReferenceKey(selectedDiscovery);
                            BatchAttireSelection same = await Task.Run(() => available.Where(choice => choice.DiscoveryKey == key).FirstOrDefault(choice =>
                            {
                                try { return string.Equals(DirectoryLinks.Resolve(choice.SourceFolder).TrimEnd('\\', '/'), selectedDiscovery.SourceFolder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
                                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is Win32Exception) { return false; }
                            }));
                            if (same != null) current = new List<BatchAttireSelection> { same };
                        }
                    }
                    else
                    {
                        BatchRenderPlan resolved = await Task.Run(() => BatchRenderPlan.Create(new[] { row.Entry }, inventory, table));
                        if (resolved.Jobs.Count != 1) throw new InvalidOperationException(string.Join("\n", resolved.Skipped));
                        BatchRenderJob model = resolved.Jobs[0];
                        if (model.ExtractFromGame)
                        {
                            model.Entry.Folder = await RequestGameModelAsync(model.Entry, model.Entry.StockFolder, message => status.Text = message);
                            if (!Directory.Exists(model.Entry.Folder)) throw new IOException("The game model was not extracted.");
                        }
                        List<AlternateAttire> local = await Task.Run(() => AlternateAttires.Read(model.Entry.Folder));
                        row.Entry = BatchRenderPlan.Copy(model.Entry);
                        available = local.Select(attire => new BatchAttireSelection { SourceFolder = row.Entry.Folder, SourceId = row.Entry.SourceId,
                            RelativePath = attire.RelativePath, DisplayName = attire.DisplayName, IsDefault = attire.IsDefault }).ToList();
                        string loadedFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                        string loadedId = type.GetField("TargetSlot", InstanceMembers)?.GetValue(owner) as string;
                        string selected = SelectedAlternateAttire(owner, loadedFolder);
                        if (current == null && selected != null && loadedId == row.Entry.Id.ToString(CultureInfo.InvariantCulture))
                        {
                            bool wrapped = AutomaticAttire.Plan(row.Entry.Folder, CharacterFolders.PhysicalId(row.Entry.Folder) ?? (row.Entry.SourceId > 0 ? row.Entry.SourceId : row.Entry.Id)).WrapDirectModel;
                            string sourcePath = wrapped && selected.StartsWith("Default_Attire/", StringComparison.OrdinalIgnoreCase) ? selected.Substring("Default_Attire/".Length) : selected;
                            BatchAttireSelection same = available.FirstOrDefault(attire => string.Equals(attire.RelativePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                            if (same != null) current = new List<BatchAttireSelection> { same };
                        }
                    }
                    if (current == null && selectedDiscovery == null && discovery.References.Count > 0)
                    {
                        string loadedFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                        DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, loadedFolder);
                        if (binding != null && binding.Context.Entry.Id == row.Entry.Id)
                        {
                            string selected = SelectedAlternateAttire(owner, loadedFolder);
                            string sourcePath = AlternateAttires.SourceRelativePath(loadedFolder, selected ?? "");
                            BatchAttireSelection same = available.FirstOrDefault(choice => string.IsNullOrWhiteSpace(choice.DiscoveryKey)
                                && SameAttireFolder(choice.SourceFolder, binding.SourceFolder)
                                && string.Equals(choice.RelativePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                            if (same != null) current = new List<BatchAttireSelection> { same };
                        }
                    }
                    if (cancel) return;
                    if (available.Count == 0) throw new InvalidOperationException("No extracted attire models were found for " + row.Entry.Name + ".");
                    List<BatchAttireSelection> chosen = ChooseBatchAttires(dialog, row.Entry, available, current);
                    if (chosen != null) { row.Attires = chosen; row.Selected = true; }
                    status.Text = "Attire choices stay with each wrestler. MAKE selected renders one file per chosen attire using " + tauntName + ".";
                }
                catch (Exception error) { status.Text = BatchError(error).Message; }
                finally { running = false; searchRow.IsEnabled = true; list.IsEnabled = true; stop.IsEnabled = true; refresh(); closeWhenRequested(); }
            };
            installAll.Click += async (sender, args) =>
            {
                if (running || BatchNativeBusy(owner) || completed.Count == 0 || !(bool)unlocked.Invoke(owner, null)) return;
                if (completed.GroupBy(job => job.Entry.Id).Any(group => group.Count() > 1))
                { status.Text = "Several completed outfits use the same wrestler slot. Select the outfit you want, use Use selected result, then Install into the game. Only one moving render can be active for each wrestler ID."; return; }
                if (Process.GetProcessesByName("WWE2K26_x64").Length > 0) { status.Text = "Close WWE 2K26 before installing the completed batch."; return; }
                string game = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
                if (!Directory.Exists(game)) { status.Text = "Choose your WWE 2K26 game folder in Gorilla Studio before installing this batch."; return; }
                running = true; installAll.IsEnabled = false; use.IsEnabled = false; stop.IsEnabled = false;
                var content = owner.Content as UIElement;
                var enabled = new BatchEnabledRestore(content);
                if (content != null) content.IsEnabled = false;
                try
                {
                    status.Text = "Installing verified completed renders…";
                    var report = await Task.Run(() => BatchRenderInstaller.Install(AppDomain.CurrentDomain.BaseDirectory, game, completed.ToArray()));
                    status.Text = report.Summary;
                    if (report.Failed.Count > 0) status.Text += "\n" + string.Join("\n", report.Failed);
                    if (report.Installed > 0)
                    {
                        await RunCombinedGameSetupAsync(owner);
                        var nativeStatus = type.GetField("StageText", InstanceMembers)?.GetValue(owner) as TextBlock;
                        status.Text = report.Summary + "\n" + (nativeStatus?.Text ?? "Run Game setup, then bake once in Discovery if setup requests it.");
                        if (report.Failed.Count > 0) status.Text += "\n" + string.Join("\n", report.Failed);
                    }
                }
                catch (Exception error) { status.Text = "Batch installation stopped: " + BatchError(error).Message; }
                finally { running = false; enabled.Restore(); installAll.IsEnabled = true; stop.IsEnabled = true; closeWhenRequested(); }
            };
            CancelEventHandler ownerClosing = (sender, args) => { if (running) { args.Cancel = true; closeRequested = true; requestCancel(); } };
            owner.Closing += ownerClosing;
            dialog.Closing += (sender, args) => { if (running) { args.Cancel = true; closeRequested = true; requestCancel(); } };
            dialog.Closed += (sender, args) => { closed = true; rosterLoadingCancellation.Cancel(); owner.Closing -= ownerClosing; batchWindows.Remove(owner); };
            use.Click += async (sender, args) =>
            {
                var job = (list.SelectedItem as ListBoxItem)?.Tag as BatchRenderJob;
                if (job == null || BatchNativeBusy(owner)) return;
                string reason;
                if (!BatchRenderPlan.VerifyCompleted(job.RenderPath, job.Entry.Id, job.OutputDirectory, out reason)) { status.Text = reason; return; }
                running = true; use.IsEnabled = false; start.IsEnabled = false; installAll.IsEnabled = false; stop.IsEnabled = false;
                var ownerContent = owner.Content as UIElement;
                var ownerEnabled = new BatchEnabledRestore(ownerContent);
                if (ownerContent != null) ownerContent.IsEnabled = false;
                try
                {
                    await BatchLoadCharacterAsync(owner, job.Entry, setFolder, null, null, job, true, inventory, table);
                    string resultFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                    SetAlternateAttireSelection(owner, resultFolder, job.AttireRelativePath);
                    type.GetField("LastGmr", InstanceMembers)?.SetValue(owner, job.RenderPath);
                    var install = type.GetField("InstallButton", InstanceMembers)?.GetValue(owner) as Button;
                    if (install != null) install.IsEnabled = type.GetField("Game", InstanceMembers)?.GetValue(owner) is string game && Directory.Exists(game);
                    var resultInfo = type.GetField("ResultInfo", InstanceMembers)?.GetValue(owner) as TextBlock;
                    if (resultInfo != null) resultInfo.Text = job.Entry.Name + " · wrestler " + job.Entry.Id.ToString(CultureInfo.InvariantCulture)
                        + "\nAttire: " + (job.AttireName ?? "Current/default") + "\nCompleted batch render: " + (job.PublishedRenderPath ?? job.RenderPath) + "\nReady to install without rendering again.";
                    SetModelStatus(owner, "Batch result ready: " + job.Entry.Name + ". Use Install into the game.");
                    status.Text = "Loaded the completed result for " + job.Entry.Name + ". Close this window, then press Install into the game.";
                }
                catch (Exception error) { status.Text = BatchError(error).Message; }
                finally { running = false; ownerEnabled.Restore(); use.IsEnabled = true; installAll.IsEnabled = CanInstallBatchTogether(completed); stop.IsEnabled = true; closeWhenRequested(); }
            };
            start.Click += async (sender, args) =>
            {
                if (running || BatchNativeBusy(owner) || !(bool)unlocked.Invoke(owner, null)) return;
                if (!ReferenceEquals(taunts.SelectedItem, taunt)) { status.Text = "The selected taunt changed. Close and reopen Batch MAKE with the taunt you want."; return; }
                running = true; cancel = false; completed.Clear(); allJobs.Clear();
                renderLifetime = new BatchRenderLifetime();
                batchRenderLifetimes[owner] = renderLifetime;
                start.IsEnabled = false; searchRow.IsEnabled = false; list.IsEnabled = false; use.IsEnabled = false;
                stop.Content = "Cancel batch";
                var ownerContent = owner.Content as UIElement;
                var ownerEnabled = new BatchEnabledRestore(ownerContent);
                var bodyEnabled = new BatchEnabledRestore(type.GetField("BodyGrid", InstanceMembers)?.GetValue(owner) as DependencyObject);
                var renderButtons = new[] { "PreviewButton", "MakeButton" }.Select(name =>
                    new BatchEnabledRestore(type.GetField(name, InstanceMembers)?.GetValue(owner) as DependencyObject)).ToArray();
                if (ownerContent != null) ownerContent.IsEnabled = false;
                var owned = owner.OwnedWindows.Cast<Window>().Where(window => window != dialog).ToDictionary(window => window, window => new BatchEnabledRestore(window));
                foreach (Window other in owned.Keys) other.IsEnabled = false;
                var output = type.GetField("Output", InstanceMembers);
                string previousOutput = output?.GetValue(owner) as string;
                normalOutputDirectory = previousOutput;
                string previousFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                string previousAttire = SelectedAlternateAttire(owner, previousFolder);
                var busy = type.GetField("Busy", InstanceMembers);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                string currentLabel = "Preparing batch";
                timer.Tick += (s, e) =>
                {
                    if (cancel) return;
                    var nativeStatus = type.GetField("StageText", InstanceMembers)?.GetValue(owner) as TextBlock;
                    status.Text = currentLabel + "\n" + (nativeStatus?.Text ?? "Working…");
                };
                try
                {
                    StopAutomaticMake(owner);
                    var preparation = type.GetField("PrepTask", InstanceMembers)?.GetValue(owner) as Task;
                    if (preparation != null) try { await preparation; } catch { }
                    renderLifetime.Token.ThrowIfCancellationRequested();
                    SetPerformanceBusy(owner, true);
                    var chosen = rows.Where(row => row.Selected).Select(row => BatchRenderPlan.Copy(row.Entry)).ToList();
                    var choices = rows.Where(row => row.Selected && row.Attires != null).GroupBy(row => row.Entry.Id)
                        .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First().Attires);
                    var plan = await Task.Run(() => BatchRenderPlan.CreateWithAttires(chosen, inventory, table, choices));
                    renderLifetime.Token.ThrowIfCancellationRequested();
                    if (plan.Jobs.Count == 0) { status.Text = string.Join("\n", plan.Skipped.Take(8)); return; }
                    if (output == null || string.IsNullOrWhiteSpace(previousOutput)) throw new InvalidOperationException("The original render output folder is unavailable.");
                    runDirectory = Path.Combine(StorageDirectory, "batches", "Batch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(runDirectory);
                    allJobs.AddRange(plan.Jobs);
                    for (int index = 0; index < plan.Jobs.Count; index++)
                    {
                        if (cancel) break;
                        BatchRenderJob job = plan.Jobs[index];
                        currentLabel = (index + 1).ToString(CultureInfo.InvariantCulture) + " / " + plan.Jobs.Count.ToString(CultureInfo.InvariantCulture) + " · " + job.Entry.Name + (job.AttireName == null ? "" : " · " + job.AttireName);
                        status.Text = currentLabel + "\nLoading model and selected attire…";
                        job.Status = "Running";
                        job.OutputDirectory = Path.Combine(runDirectory, BatchRenderPlan.WorkspaceName(job));
                        if (Directory.Exists(job.OutputDirectory)) throw new IOException("The isolated batch output directory already exists.");
                        Directory.CreateDirectory(job.OutputDirectory);
                        output.SetValue(owner, job.OutputDirectory);
                        type.GetField("LastGmr", InstanceMembers)?.SetValue(owner, null);
                        var install = type.GetField("InstallButton", InstanceMembers)?.GetValue(owner) as Button;
                        if (install != null) install.IsEnabled = false;
                        try
                        {
                            if (job.ExtractFromGame)
                            {
                                job.Entry.Folder = await RequestGameModelAsync(job.Entry, job.Entry.StockFolder, message => status.Text = currentLabel + "\n" + message);
                                renderLifetime.Token.ThrowIfCancellationRequested();
                                if (!Directory.Exists(job.Entry.Folder)) throw new IOException("The game model was not extracted.");
                            }
                            await BatchLoadCharacterAsync(owner, job.Entry, setFolder, previousFolder, previousAttire, job, false, inventory, table);
                            renderLifetime.Token.ThrowIfCancellationRequested();
                            string jobFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                            job.AttireRelativePath = SelectedAlternateAttire(owner, jobFolder);
                            if (!ReferenceEquals(taunts.SelectedItem, taunt)) { cancel = true; throw new InvalidOperationException("The selected taunt changed; the batch stopped before rendering another taunt."); }
                            timer.Start();
                            var task = go.Invoke(owner, new object[] { false }) as Task;
                            if (task == null) throw new InvalidOperationException("The original render command did not return a task.");
                            await task;
                            timer.Stop();
                            renderLifetime.Token.ThrowIfCancellationRequested();
                            string renderPath = type.GetField("LastGmr", InstanceMembers)?.GetValue(owner) as string;
                            string reason;
                            if (!BatchRenderPlan.VerifyCompleted(renderPath, job.Entry.Id, job.OutputDirectory, out reason)) throw new InvalidDataException(reason);
                            job.RenderPath = renderPath; job.Status = "Complete"; completed.Add(job);
                            try
                            {
                                status.Text = currentLabel + "\nSaving the completed file to your usual output folder…";
                                await Task.Run(() => BatchRenderPlan.PublishCompleted(job, normalOutputDirectory));
                                var resultInfo = type.GetField("ResultInfo", InstanceMembers)?.GetValue(owner) as TextBlock;
                                if (resultInfo != null) resultInfo.Text += "\nSaved to: " + job.PublishedRenderPath;
                            }
                            catch (Exception error) { job.Status = "Complete; output copy failed: " + BatchError(error).Message; }
                        }
                        catch (OperationCanceledException) { timer.Stop(); job.Status = "Canceled"; type.GetField("LastGmr", InstanceMembers)?.SetValue(owner, null); }
                        catch (Exception error) { timer.Stop(); job.Status = "Failed: " + BatchError(error).Message; }
                        finally
                        {
                            pendingRosterSelection = null; preparedForLoad = null;
                            if (busy != null) busy.SetValue(owner, false);
                            WriteBatchManifest(runDirectory, normalOutputDirectory, tauntName, tauntKey, currentArgs, allJobs, plan.Skipped, cancel);
                        }
                    }
                    foreach (BatchRenderJob job in plan.Jobs.Where(job => job.Status == "Ready" || job.Status == "Load from installed game")) job.Status = "Not started";
                    WriteBatchManifest(runDirectory, normalOutputDirectory, tauntName, tauntKey, currentArgs, allJobs, plan.Skipped, cancel);
                    showingResults = true; list.Items.Clear(); list.IsEnabled = true;
                    foreach (BatchRenderJob job in allJobs)
                        list.Items.Add(new ListBoxItem { Content = job.Entry.Id.ToString(CultureInfo.InvariantCulture) + "   " + job.Entry.Name + " · " + (job.AttireName ?? "Current/default") + " · " + job.Status, Tag = job, Foreground = Brushes.White });
                    foreach (string skipped in plan.Skipped) list.Items.Add(new ListBoxItem { Content = "Skipped · " + skipped, Foreground = Brushes.LightGray });
                    status.Text = completed.Count.ToString(CultureInfo.InvariantCulture) + " completed · " + allJobs.Count(job => job.Status.StartsWith("Failed", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)
                        + " failed · " + plan.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped" + (cancel ? " · canceled; interrupted files are not installed" : "")
                        + ".\n" + completed.Count(job => job.PublishedRenderPath != null).ToString(CultureInfo.InvariantCulture) + " saved to: " + normalOutputDirectory
                        + (completed.Any(job => job.PublishedRenderPath == null) ? "\nSome output copies failed; their completed workspace files are kept and can still be installed." : "")
                        + "\nOpen output folder for your GMR files. Batch details opens the manifest and separate workspaces."
                        + (completed.GroupBy(job => job.Entry.Id).Any(group => group.Count() > 1) ? "\nSeveral outfits use the same wrestler ID. Select one completed outfit and use Use selected result to install that version." : "");
                }
                catch (OperationCanceledException) { status.Text = "Batch canceled. Previously completed files are kept."; }
                catch (Exception error) { status.Text = "Batch stopped: " + BatchError(error).Message; }
                finally
                {
                    timer.Stop();
                    // A canceled native Go may exit before a queued preparation
                    // or descendant has drained. Keep the modal owner locked until
                    // every process registered to this batch is confirmed stopped.
                    if (renderLifetime != null)
                    {
                        if (busy != null) busy.SetValue(owner, true);
                        // End the session even after success: an attire preparation
                        // queued at the end must not start behind a closed dialog.
                        Task finalStop = renderLifetime.CancelAsync();
                        var preparation = type.GetField("PrepTask", InstanceMembers)?.GetValue(owner) as Task;
                        if (preparation != null) try { await preparation; } catch { }
                        bool stopped = false;
                        while (!stopped)
                        {
                            try { await finalStop; await renderLifetime.CancelAsync(); stopped = !renderLifetime.HasRunningProcesses; }
                            catch (Exception error) { status.Text = "Waiting for this batch's render processes to stop before unlocking the app… " + BatchError(error).Message; }
                            if (!stopped) { await Task.Delay(750); finalStop = renderLifetime.CancelAsync(); }
                        }
                        try { await cancelTask; } catch { }
                    }
                    if (renderLifetime != null)
                    {
                        batchRenderLifetimes.Remove(owner);
                        renderLifetime.Dispose(); renderLifetime = null;
                    }
                    running = false; stop.IsEnabled = true; stop.Content = "Close";
                    if (output != null) output.SetValue(owner, previousOutput);
                    if (busy != null) busy.SetValue(owner, false);
                    type.GetField("PrepShown", InstanceMembers)?.SetValue(owner, false);
                    var progress = type.GetField("Progress", InstanceMembers)?.GetValue(owner) as ProgressBar;
                    if (progress != null) progress.IsIndeterminate = false;
                    pendingRosterSelection = null; preparedForLoad = null;
                    SetPerformanceBusy(owner, false);
                    bodyEnabled.Restore(); ownerEnabled.Restore();
                    foreach (BatchEnabledRestore button in renderButtons) button.Restore();
                    foreach (var other in owned) if (other.Key.IsLoaded) other.Value.Restore();
                    open.IsEnabled = Directory.Exists(normalOutputDirectory);
                    details.IsEnabled = Directory.Exists(runDirectory);
                    installAll.IsEnabled = CanInstallBatchTogether(completed);
                    installAll.ToolTip = installAll.IsEnabled ? "Install the completed group." : "Choose one completed outfit with Use selected result; only one moving render can be active for each wrestler ID.";
                    if (!showingResults) { searchRow.IsEnabled = true; list.IsEnabled = true; refresh(); }
                    if (allJobs.Count > 0)
                    {
                        string current = type.GetField("LastGmr", InstanceMembers)?.GetValue(owner) as string;
                        string target = type.GetField("TargetSlot", InstanceMembers)?.GetValue(owner) as string;
                        BatchRenderJob matching = completed.FirstOrDefault(job => string.Equals(job.RenderPath, current, StringComparison.OrdinalIgnoreCase)
                            && job.Entry.Id.ToString(CultureInfo.InvariantCulture) == target);
                        var install = type.GetField("InstallButton", InstanceMembers)?.GetValue(owner) as Button;
                        if (matching == null) type.GetField("LastGmr", InstanceMembers)?.SetValue(owner, null);
                        if (install != null) install.IsEnabled = matching != null && type.GetField("Game", InstanceMembers)?.GetValue(owner) is string game && Directory.Exists(game);
                    }
                    QueueSelectedPicture(owner);
                    closeWhenRequested();
                }
            };
            dialog.Loaded += async (sender, args) =>
            {
                try
                {
                    string root = RememberedRoot(owner), rosterFile = RememberedRosterFile();
                    List<RosterEntry> saved = ReadRoster();
                    RosterSessionData session = await Task.Run(() => BatchRosterLoading.Open(root, saved, rosterFile,
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rosters", "Discovery_roster.json"), rosterLoadingToken), rosterLoadingToken);
                    if (closed) return;
                    table = session.DiscoveryTable;
                    rows = session.Entries.Where(entry => entry.Id > 0 && entry.Id <= 65535 && !string.IsNullOrWhiteSpace(entry.Name))
                        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id).Select(entry => new BatchRosterRow { Entry = entry }).ToList();
                    string currentId = type.GetField("TargetSlot", InstanceMembers)?.GetValue(owner) as string;
                    var currentRows = rows.Where(row => row.Entry.Id.ToString(CultureInfo.InvariantCulture) == currentId).ToList();
                    if (currentRows.Count == 1) currentRows[0].Selected = true;
                    // Display names/IDs as soon as metadata is ready. Model
                    // indexing keeps running off the dispatcher, and MAKE stays
                    // disabled until that inventory is complete.
                    status.Text = BatchRosterLoading.LoadingStatus(session);
                    refresh();
                    inventory = await Task.Run(() => BatchModelInventory(session, root, rosterLoadingToken), rosterLoadingToken);
                    if (closed) return;
                    loaded = true; status.Text = BatchRosterLoading.ReadyStatus(session, inventory.Count);
                    refresh();
                }
                catch (OperationCanceledException) when (closed) { }
                catch (Exception error) { if (!closed) status.Text = "Could not load the batch roster: " + BatchError(error).Message; }
            };
            try { dialog.ShowDialog(); }
            finally { rosterLoadingCancellation.Cancel(); rosterLoadingCancellation.Dispose(); }
            await Task.CompletedTask;
        }

        private static List<ModelCandidate> BatchModelInventory(RosterSessionData session, string fallbackRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string data = session.DiscoveryTable == null ? fallbackRoot : DiscoveryProfiles.DataRoot(session.DiscoveryTable);
            string context = session.DiscoveryTable ?? data;
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(context))
            {
                string saved = Path.Combine(StorageDirectory, "models-" + FolderNames.SourceKey(Path.GetFullPath(context).ToUpperInvariant()) + ".json");
                if (File.Exists(saved)) roots.AddRange(new JavaScriptSerializer().Deserialize<List<string>>(File.ReadAllText(saved)) ?? new List<string>());
            }
            roots.AddRange(session.Entries.Where(entry => Directory.Exists(entry.Folder)).Select(entry => entry.Folder));
            if (Directory.Exists(data))
            {
                roots.Add(data);
                DirectoryInfo parent = Directory.GetParent(data);
                if (parent != null)
                    foreach (string peer in new[] { Path.Combine("BakeMe_Characters", "Characters"), "Characters", Path.Combine("BakeMe", "Characters"), Path.Combine("discovery_data", "Characters") })
                        roots.Add(Path.Combine(parent.FullName, peer));
            }
            var unique = roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            unique = unique.Where(path => !unique.Any(other => !string.Equals(path, other, StringComparison.OrdinalIgnoreCase)
                && path.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
            var models = new Dictionary<string, ModelCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ModelCandidate model in ModelLibrary.Scan(root, cancellationToken)) models[model.Folder] = model;
            }
            return models.Values.ToList();
        }

        private static async Task BatchLoadCharacterAsync(Window owner, RosterEntry entry, MethodInfo setFolder, string previousFolder, string previousAttire,
            BatchRenderJob job = null, bool restoreResult = false, IEnumerable<ModelCandidate> inventory = null, string rosterTable = null)
        {
            DiscoveryAttireCatalogResult discovery = await Task.Run(() =>
            {
                DiscoveryAttireCatalogResult result = BatchRenderPlan.RefreshDiscoveryAttires(job ?? new BatchRenderJob { Entry = BatchRenderPlan.Copy(entry) }, inventory, rosterTable);
                ResolveDiscoveryAttireSources(result, System.Threading.CancellationToken.None);
                return result;
            });
            string source = DirectoryLinks.Resolve(entry.Folder);
            CharacterFolderPlan plan = CharacterFolders.Plan(source, entry);
            int physical = CharacterFolders.PhysicalId(source) ?? (entry.SourceId > 0 ? entry.SourceId : entry.Id);
            DirectAttirePlan attire = AutomaticAttire.Plan(source, physical);
            Dictionary<ulong, string> pictures = PrepareRenderImages(plan, entry.Id);
            string alias = PrepareCharacterAlias(plan, attire, pictures);
            RememberRenderTable(alias, entry.RosterTable);
            RememberPreparedRender(alias, plan, entry, pictures);
            RegisterDiscoveryAttires(alias, entry, inventory, discovery, source);
            if (job != null && job.HasExplicitAttire
                && !SeedDiscoveryAttireChoice(owner, alias, job.AttireSourceRelativePath, job.AttireDiscoveryKey))
                throw new InvalidOperationException("The attire controls are unavailable. Reopen Gorilla Studio before rendering this batch.");
            RegisterSelection(alias, entry);
            var busy = owner.GetType().GetField("Busy", InstanceMembers);
            try
            {
                if (busy != null) busy.SetValue(owner, true);
                await PrepareSelectedRenderAsync(owner, alias);
                if (busy != null) busy.SetValue(owner, false);
                preparedForLoad = alias;
                var task = setFolder.Invoke(owner, new object[] { alias }) as Task;
                if (task != null) await task;
                ApplySelection(owner);
                string selectedAttire = BatchRenderPlan.LoadedAttire(job, restoreResult,
                    job != null && job.HasExplicitAttire ? BatchRenderPlan.AliasAttirePath(job, attire.WrapDirectModel) : null,
                    discovery.Sources.Count > 0, discovery.Sources.Count > 0 ? SelectedAlternateAttire(owner, alias) : null,
                    previousAttire, string.Equals(alias, previousFolder, StringComparison.OrdinalIgnoreCase));
                SetAlternateAttireSelection(owner, alias, selectedAttire);
                string target = owner.GetType().GetField("TargetSlot", InstanceMembers)?.GetValue(owner) as string;
                if (target != entry.Id.ToString(CultureInfo.InvariantCulture)) throw new InvalidOperationException("The selected logical wrestler slot could not be applied.");
            }
            finally { preparedForLoad = null; pendingRosterSelection = null; if (busy != null) busy.SetValue(owner, false); }
        }

        private static void WriteBatchManifest(string directory, string outputDirectory, string taunt, string key, IEnumerable<string> arguments,
            IEnumerable<BatchRenderJob> jobs, IEnumerable<string> skipped, bool stopped)
        {
            string temporary = Path.Combine(directory, ".batch-results-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var document = new { version = 2, createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), outputDirectory, workspaceDirectory = directory, taunt, tauntKey = key,
                    settings = arguments?.Skip(3).Where(argument => argument != null && !argument.StartsWith("slot=", StringComparison.Ordinal)).ToArray(),
                    stopped, jobs = jobs.Select(job => new { id = job.Entry.Id, name = job.Entry.Name, model = job.Entry.Folder,
                        attire = job.AttireRelativePath, sourceAttire = job.AttireSourceRelativePath, attireName = job.AttireName, job.VariantIndex,
                        discoveryAttireKey = job.AttireDiscoveryKey, discoveryModelHash = job.AttireDiscoveryModelHash,
                        job.Status, workspaceDirectory = job.OutputDirectory, gmr = job.RenderPath, publishedGmr = job.PublishedRenderPath }).ToArray(), skipped = skipped.ToArray() };
                File.WriteAllText(temporary, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(document), new UTF8Encoding(false));
                string destination = Path.Combine(directory, "batch-results.json");
                if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static Exception BatchError(Exception error)
        {
            var invocation = error as TargetInvocationException;
            return invocation?.InnerException ?? error;
        }
    }
}
