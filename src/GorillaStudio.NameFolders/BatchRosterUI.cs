using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<Window, Window> batchWindows = new Dictionary<Window, Window>();

        private sealed class BatchRosterRow
        {
            internal RosterEntry Entry;
            internal bool Selected;
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

            var dialog = new Window { Owner = owner, Title = "Batch MAKE", Width = 820, Height = 690, MinWidth = 600, MinHeight = 470,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)),
                Foreground = Brushes.White, FontFamily = owner.FontFamily, FontSize = 13 };
            batchWindows[owner] = dialog;
            var grid = new Grid { Margin = new Thickness(18) };
            for (int i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            dialog.Content = grid;
            var explanation = new TextBlock { Text = "Choose wrestlers, then start once. Each uses the current taunt, hair, gear, ending and screen settings. Jobs run one at a time; existing model choices and default attire are used.",
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
            var installAll = new Button { Content = "Install completed", IsEnabled = false, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 0) };
            var use = new Button { Content = "Use selected result", IsEnabled = false, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 7, 0) };
            var start = new Button { Content = "MAKE selected", IsEnabled = false, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 7, 0) };
            var stop = new Button { Content = "Close", Padding = new Thickness(9, 6, 9, 6) };
            actions.Children.Add(open); actions.Children.Add(installAll); actions.Children.Add(use); actions.Children.Add(start); actions.Children.Add(stop);
            Grid.SetRow(actions, 5); grid.Children.Add(actions);
            bool running = false, cancel = false, loaded = false, showingResults = false;
            var rows = new List<BatchRosterRow>();
            var inventory = new List<ModelCandidate>();
            var completed = new List<BatchRenderJob>();
            var allJobs = new List<BatchRenderJob>();
            string table = null, runDirectory = null;
            Action refresh = () =>
            {
                if (showingResults) return;
                list.Items.Clear();
                string query = search.Text.Trim();
                foreach (BatchRosterRow row in rows.Where(row => query.Length == 0 || row.Entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || row.Entry.Id.ToString(CultureInfo.InvariantCulture).Contains(query)))
                {
                    var check = new CheckBox { Content = row.Entry.Id.ToString(CultureInfo.InvariantCulture) + "   " + row.Entry.Name,
                        IsChecked = row.Selected, Tag = row, Foreground = Brushes.White, Margin = new Thickness(5, 4, 5, 4) };
                    check.Checked += (sender, args) => { row.Selected = true; start.IsEnabled = loaded && !running && rows.Any(item => item.Selected); };
                    check.Unchecked += (sender, args) => { row.Selected = false; start.IsEnabled = loaded && !running && rows.Any(item => item.Selected); };
                    list.Items.Add(check);
                }
                start.IsEnabled = loaded && !running && rows.Any(row => row.Selected);
            };
            search.TextChanged += (sender, args) => refresh();
            selectAll.Click += (sender, args) => { foreach (CheckBox check in list.Items.OfType<CheckBox>()) check.IsChecked = true; };
            clear.Click += (sender, args) => { foreach (var row in rows) row.Selected = false; refresh(); };
            list.SelectionChanged += (sender, args) => use.IsEnabled = !running && list.SelectedItem is ListBoxItem item && item.Tag is BatchRenderJob job && completed.Contains(job);
            stop.Click += (sender, args) => { if (running) { cancel = true; stop.IsEnabled = false; status.Text = "Stopping after the current wrestler finishes. Completed files are kept."; } else dialog.Close(); };
            open.Click += (sender, args) => { if (Directory.Exists(runDirectory)) Process.Start(new ProcessStartInfo { FileName = runDirectory, UseShellExecute = true }); };
            installAll.Click += async (sender, args) =>
            {
                if (running || BatchNativeBusy(owner) || completed.Count == 0 || !(bool)unlocked.Invoke(owner, null)) return;
                if (Process.GetProcessesByName("WWE2K26_x64").Length > 0) { status.Text = "Close WWE 2K26 before installing the completed batch."; return; }
                string game = type.GetField("Game", InstanceMembers)?.GetValue(owner) as string;
                if (!Directory.Exists(game)) { status.Text = "Choose your WWE 2K26 game folder in Gorilla Studio before installing this batch."; return; }
                running = true; installAll.IsEnabled = false; use.IsEnabled = false; stop.IsEnabled = false;
                var content = owner.Content as UIElement;
                bool enabled = content != null && content.IsEnabled;
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
                finally { running = false; if (content != null) content.IsEnabled = enabled; installAll.IsEnabled = true; stop.IsEnabled = true; }
            };
            CancelEventHandler ownerClosing = (sender, args) => { if (running) { args.Cancel = true; cancel = true; status.Text = "Stopping after the current wrestler finishes. Completed files are kept."; } };
            owner.Closing += ownerClosing;
            dialog.Closing += (sender, args) => { if (running) { args.Cancel = true; cancel = true; status.Text = "Stopping after the current wrestler finishes. Completed files are kept."; } };
            dialog.Closed += (sender, args) => { owner.Closing -= ownerClosing; batchWindows.Remove(owner); };
            use.Click += async (sender, args) =>
            {
                var job = (list.SelectedItem as ListBoxItem)?.Tag as BatchRenderJob;
                if (job == null || BatchNativeBusy(owner)) return;
                string reason;
                if (!BatchRenderPlan.VerifyCompleted(job.RenderPath, job.Entry.Id, job.OutputDirectory, out reason)) { status.Text = reason; return; }
                running = true; use.IsEnabled = false; start.IsEnabled = false; installAll.IsEnabled = false; stop.IsEnabled = false;
                var ownerContent = owner.Content as UIElement;
                bool ownerEnabled = ownerContent != null && ownerContent.IsEnabled;
                if (ownerContent != null) ownerContent.IsEnabled = false;
                try
                {
                    await BatchLoadCharacterAsync(owner, job.Entry, setFolder, null, null);
                    string resultFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                    SetAlternateAttireSelection(owner, resultFolder, job.AttireRelativePath);
                    type.GetField("LastGmr", InstanceMembers)?.SetValue(owner, job.RenderPath);
                    var install = type.GetField("InstallButton", InstanceMembers)?.GetValue(owner) as Button;
                    if (install != null) install.IsEnabled = type.GetField("Game", InstanceMembers)?.GetValue(owner) is string game && Directory.Exists(game);
                    var resultInfo = type.GetField("ResultInfo", InstanceMembers)?.GetValue(owner) as TextBlock;
                    if (resultInfo != null) resultInfo.Text = job.Entry.Name + " · wrestler " + job.Entry.Id.ToString(CultureInfo.InvariantCulture)
                        + "\nCompleted batch render: " + Path.GetFileName(job.RenderPath) + "\nReady to install without rendering again.";
                    SetModelStatus(owner, "Batch result ready: " + job.Entry.Name + ". Use Install into the game.");
                    status.Text = "Loaded the completed result for " + job.Entry.Name + ". Close this window, then press Install into the game.";
                }
                catch (Exception error) { status.Text = BatchError(error).Message; }
                finally { running = false; if (ownerContent != null) ownerContent.IsEnabled = ownerEnabled; use.IsEnabled = true; installAll.IsEnabled = completed.Count > 0; stop.IsEnabled = true; }
            };
            start.Click += async (sender, args) =>
            {
                if (running || BatchNativeBusy(owner) || !(bool)unlocked.Invoke(owner, null)) return;
                if (!ReferenceEquals(taunts.SelectedItem, taunt)) { status.Text = "The selected taunt changed. Close and reopen Batch MAKE with the taunt you want."; return; }
                running = true; cancel = false; completed.Clear(); allJobs.Clear();
                start.IsEnabled = false; searchRow.IsEnabled = false; list.IsEnabled = false; use.IsEnabled = false;
                stop.Content = "Stop after current";
                var ownerContent = owner.Content as UIElement;
                bool ownerEnabled = ownerContent != null && ownerContent.IsEnabled;
                if (ownerContent != null) ownerContent.IsEnabled = false;
                var owned = owner.OwnedWindows.Cast<Window>().Where(window => window != dialog).ToDictionary(window => window, window => window.IsEnabled);
                foreach (Window other in owned.Keys) other.IsEnabled = false;
                var output = type.GetField("Output", InstanceMembers);
                string previousOutput = output?.GetValue(owner) as string;
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
                    SetPerformanceBusy(owner, true);
                    var chosen = rows.Where(row => row.Selected).Select(row => BatchRenderPlan.Copy(row.Entry)).ToList();
                    var plan = await Task.Run(() => BatchRenderPlan.Create(chosen, inventory, table));
                    if (plan.Jobs.Count == 0) { status.Text = string.Join("\n", plan.Skipped.Take(8)); return; }
                    if (output == null || string.IsNullOrWhiteSpace(previousOutput)) throw new InvalidOperationException("The original render output folder is unavailable.");
                    runDirectory = Path.Combine(previousOutput, "Batch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(runDirectory);
                    allJobs.AddRange(plan.Jobs);
                    for (int index = 0; index < plan.Jobs.Count; index++)
                    {
                        if (cancel) break;
                        BatchRenderJob job = plan.Jobs[index];
                        currentLabel = (index + 1).ToString(CultureInfo.InvariantCulture) + " / " + plan.Jobs.Count.ToString(CultureInfo.InvariantCulture) + " · " + job.Entry.Name;
                        status.Text = currentLabel + "\nLoading model and default attire…";
                        job.Status = "Running";
                        job.OutputDirectory = Path.Combine(runDirectory, job.Entry.Id.ToString(CultureInfo.InvariantCulture));
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
                                if (!Directory.Exists(job.Entry.Folder)) throw new IOException("The game model was not extracted.");
                            }
                            await BatchLoadCharacterAsync(owner, job.Entry, setFolder, previousFolder, previousAttire);
                            string jobFolder = type.GetField("Folder", InstanceMembers)?.GetValue(owner) as string;
                            job.AttireRelativePath = SelectedAlternateAttire(owner, jobFolder);
                            if (!ReferenceEquals(taunts.SelectedItem, taunt)) { cancel = true; throw new InvalidOperationException("The selected taunt changed; the batch stopped before rendering another taunt."); }
                            timer.Start();
                            var task = go.Invoke(owner, new object[] { false }) as Task;
                            if (task == null) throw new InvalidOperationException("The original render command did not return a task.");
                            await task;
                            timer.Stop();
                            string renderPath = type.GetField("LastGmr", InstanceMembers)?.GetValue(owner) as string;
                            string reason;
                            if (!BatchRenderPlan.VerifyCompleted(renderPath, job.Entry.Id, job.OutputDirectory, out reason)) throw new InvalidDataException(reason);
                            job.RenderPath = renderPath; job.Status = "Complete"; completed.Add(job);
                        }
                        catch (Exception error) { timer.Stop(); job.Status = "Failed: " + BatchError(error).Message; }
                        finally
                        {
                            pendingRosterSelection = null; preparedForLoad = null;
                            if (busy != null) busy.SetValue(owner, false);
                            WriteBatchManifest(runDirectory, tauntName, tauntKey, currentArgs, allJobs, plan.Skipped, cancel);
                        }
                    }
                    foreach (BatchRenderJob job in plan.Jobs.Where(job => job.Status == "Ready" || job.Status == "Load from installed game")) job.Status = "Not started";
                    WriteBatchManifest(runDirectory, tauntName, tauntKey, currentArgs, allJobs, plan.Skipped, cancel);
                    showingResults = true; list.Items.Clear(); list.IsEnabled = true;
                    foreach (BatchRenderJob job in allJobs)
                        list.Items.Add(new ListBoxItem { Content = job.Entry.Id.ToString(CultureInfo.InvariantCulture) + "   " + job.Entry.Name + " · " + job.Status, Tag = job, Foreground = Brushes.White });
                    foreach (string skipped in plan.Skipped) list.Items.Add(new ListBoxItem { Content = "Skipped · " + skipped, Foreground = Brushes.LightGray });
                    status.Text = completed.Count.ToString(CultureInfo.InvariantCulture) + " completed · " + allJobs.Count(job => job.Status.StartsWith("Failed", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)
                        + " failed · " + plan.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped" + (cancel ? " · stopped after current wrestler" : "")
                        + ".\nOpen the output folder for the files and batch-results.json. Use a completed result to install it without rendering again.";
                }
                catch (Exception error) { status.Text = "Batch stopped: " + BatchError(error).Message; }
                finally
                {
                    timer.Stop(); running = false; stop.IsEnabled = true; stop.Content = "Close";
                    if (output != null) output.SetValue(owner, previousOutput);
                    if (busy != null) busy.SetValue(owner, false);
                    pendingRosterSelection = null; preparedForLoad = null;
                    SetPerformanceBusy(owner, false);
                    if (ownerContent != null) ownerContent.IsEnabled = ownerEnabled;
                    foreach (var other in owned) if (other.Key.IsLoaded) other.Key.IsEnabled = other.Value;
                    open.IsEnabled = Directory.Exists(runDirectory);
                    installAll.IsEnabled = completed.Count > 0;
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
                }
            };
            dialog.Loaded += async (sender, args) =>
            {
                try
                {
                    string root = RememberedRoot(owner), rosterFile = RememberedRosterFile();
                    List<RosterEntry> saved = ReadRoster();
                    RosterSessionData session = await Task.Run(() => RosterLaunch.Open(root, saved, rosterFile,
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rosters", "Discovery_roster.json")));
                    table = session.DiscoveryTable;
                    rows = session.Entries.Where(entry => entry.Id > 0 && entry.Id <= 65535 && !string.IsNullOrWhiteSpace(entry.Name))
                        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id).Select(entry => new BatchRosterRow { Entry = entry }).ToList();
                    inventory = await Task.Run(() => BatchModelInventory(session, root));
                    loaded = true; status.Text = rows.Count.ToString(CultureInfo.InvariantCulture) + " roster entries · " + inventory.Count.ToString(CultureInfo.InvariantCulture) + " model folders indexed. Select wrestlers above.";
                    refresh();
                }
                catch (Exception error) { status.Text = "Could not load the batch roster: " + BatchError(error).Message; }
            };
            dialog.ShowDialog();
            await Task.CompletedTask;
        }

        private static List<ModelCandidate> BatchModelInventory(RosterSessionData session, string fallbackRoot)
        {
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
            foreach (string root in unique) foreach (ModelCandidate model in ModelLibrary.Scan(root)) models[model.Folder] = model;
            return models.Values.ToList();
        }

        private static async Task BatchLoadCharacterAsync(Window owner, RosterEntry entry, MethodInfo setFolder, string previousFolder, string previousAttire)
        {
            string source = DirectoryLinks.Resolve(entry.Folder);
            CharacterFolderPlan plan = CharacterFolders.Plan(source, entry);
            int physical = CharacterFolders.PhysicalId(source) ?? (entry.SourceId > 0 ? entry.SourceId : entry.Id);
            DirectAttirePlan attire = AutomaticAttire.Plan(source, physical);
            Dictionary<ulong, string> pictures = PrepareRenderImages(plan, entry.Id);
            string alias = PrepareCharacterAlias(plan, attire, pictures);
            RememberRenderTable(alias, entry.RosterTable);
            RememberPreparedRender(alias, plan, entry, pictures);
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
                SetAlternateAttireSelection(owner, alias, string.Equals(alias, previousFolder, StringComparison.OrdinalIgnoreCase) ? previousAttire : null);
                string target = owner.GetType().GetField("TargetSlot", InstanceMembers)?.GetValue(owner) as string;
                if (target != entry.Id.ToString(CultureInfo.InvariantCulture)) throw new InvalidOperationException("The selected logical wrestler slot could not be applied.");
            }
            finally { preparedForLoad = null; pendingRosterSelection = null; if (busy != null) busy.SetValue(owner, false); }
        }

        private static void WriteBatchManifest(string directory, string taunt, string key, IEnumerable<string> arguments,
            IEnumerable<BatchRenderJob> jobs, IEnumerable<string> skipped, bool stopped)
        {
            string temporary = Path.Combine(directory, ".batch-results-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var document = new { version = 1, createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), taunt, tauntKey = key,
                    settings = arguments?.Skip(3).Where(argument => argument != null && !argument.StartsWith("slot=", StringComparison.Ordinal)).ToArray(),
                    stopped, jobs = jobs.Select(job => new { id = job.Entry.Id, name = job.Entry.Name, model = job.Entry.Folder,
                        attire = job.AttireRelativePath, job.Status, outputDirectory = job.OutputDirectory, gmr = job.RenderPath }).ToArray(), skipped = skipped.ToArray() };
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
