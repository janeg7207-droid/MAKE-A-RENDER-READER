using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly HashSet<Window> playbackWindows = new HashSet<Window>();
        private static readonly Dictionary<Window, MatchCardSetupReport> screenSetupReports = new Dictionary<Window, MatchCardSetupReport>();
        private static readonly Dictionary<Window, MatchCardLayoutsReport> layoutSetupReports = new Dictionary<Window, MatchCardLayoutsReport>();
        private static readonly HashSet<Window> combinedGameSetups = new HashSet<Window>();
        private static readonly HashSet<Window> directPlaybackChecks = new HashSet<Window>();
        private static readonly SemaphoreSlim LayoutSetupGate = new SemaphoreSlim(1, 1);

        private static void AttachPlaybackControls(Window window)
        {
            if (!playbackWindows.Add(window)) return;
            var install = window.GetType().GetField("InstallButton", InstanceMembers)?.GetValue(window) as Button;
            var panel = install?.Parent as Panel;
            if (panel != null)
            {
                var check = new Button {
                    Content = "Check match-card setup…", Style = install.Style,
                    FontSize = install.FontSize, Margin = new Thickness(0, 5, 0, 0),
                    ToolTip = "Check the installed render slot, screen setting, plugin and required moving screen files."
                };
                panel.Children.Insert(panel.Children.IndexOf(install) + 1, check);
                check.Click += (sender, args) => ShowPlaybackCheck(window);
            }
            MethodInfo originalSetup = window.GetType().GetMethod("OnGameSetup", InstanceMembers, null,
                new[] { typeof(object), typeof(RoutedEventArgs) }, null);
            if (originalSetup != null)
                foreach (Button button in PlaybackButtons(window))
                    if (string.Equals(button.Content as string, "Game setup", StringComparison.OrdinalIgnoreCase))
                    {
                        var original = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, originalSetup);
                        button.RemoveHandler(Button.ClickEvent, original);
                        button.Click += async (sender, args) => await RunCombinedGameSetupAsync(window);
                    }
            window.Closed += (sender, args) => { playbackWindows.Remove(window); screenSetupReports.Remove(window); layoutSetupReports.Remove(window); matchupSetupReports.Remove(window); };
        }

        private static IEnumerable<Button> PlaybackButtons(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                var button = child as Button;
                if (button != null) yield return button;
                foreach (Button nested in PlaybackButtons(child)) yield return nested;
            }
        }

        private static async Task<MatchCardLayoutsReport> CompleteMatchCardLayoutsAsync(Window window, string root)
        {
            var busy = window.GetType().GetField("Busy", InstanceMembers);
            var body = window.GetType().GetField("BodyGrid", InstanceMembers)?.GetValue(window) as UIElement;
            if (busy != null && busy.FieldType == typeof(bool) && (bool)busy.GetValue(window) && !combinedGameSetups.Contains(window))
            {
                SetModelStatus(window, "Run Game setup after the current model or render finishes to refresh Discovery matchup cards.");
                return new MatchCardLayoutsReport();
            }
            bool ownsBusy = busy != null && busy.FieldType == typeof(bool) && !(bool)busy.GetValue(window);
            bool enabled = body?.IsEnabled ?? true;
            if (ownsBusy) { busy.SetValue(window, true); if (body != null) body.IsEnabled = false; }
            try
            {
                var discovery = await ReadDiscoveryMatchupsAsync(window, root, false);
                var report = discovery.Layouts;
                if (!window.IsLoaded) return report;
                layoutSetupReports[window] = report;
                if (DiscoveryMatchupsNeedAttention(window)) SetModelStatus(window, "Some Discovery matchup cards need attention. Open Check match-card setup, then Matchup cards.");
                else if (discovery.BakeRequired) SetModelStatus(window, "Moving match-card layers are prepared. Bake once in Discovery, then restart WWE.");
                return report;
            }
            finally { if (ownsBusy) { busy.SetValue(window, false); if (body != null) body.IsEnabled = enabled; } }
        }

        private static async Task RunCombinedGameSetupAsync(Window window)
        {
            if (combinedGameSetups.Contains(window)) return;
            var busy = window.GetType().GetField("Busy", InstanceMembers);
            if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window))
            { SetModelStatus(window, "Wait for the current model or render to finish before game setup."); return; }
            combinedGameSetups.Add(window);
            bool ownsBusy = false;
            var body = window.GetType().GetField("BodyGrid", InstanceMembers)?.GetValue(window) as UIElement;
            bool bodyEnabled = body?.IsEnabled ?? true;
            try
            {
                string root = PlaybackRoot(window);
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                { SetModelStatus(window, "Choose your Discovery data folder first, then run Game setup."); return; }
                RememberRoot(window, root);
                // Use the original setup process, but inspect this invocation's exit
                // code and result instead of the native Task's completion alone.
                MethodInfo run = window.GetType().GetMethod("RunPy", InstanceMembers, null,
                    new[] { typeof(string), typeof(IEnumerable<string>), typeof(Action<string>), typeof(Action<Process>) }, null);
                string studio = window.GetType().GetField("Studio", InstanceMembers)?.GetValue(window) as string;
                busy?.SetValue(window, true); ownsBusy = busy != null;
                if (body != null) body.IsEnabled = false;
                SetModelStatus(window, "Preparing moving match-card screens…");
                var result = MatchCardReadiness.FromSetupResult(null);
                try
                {
                    var task = run?.Invoke(window, new object[] {
                        Path.Combine(studio ?? AppDomain.CurrentDomain.BaseDirectory, "pipeline", "setup_screens.py"),
                        new[] { root }, null, null
                    }) as Task<ValueTuple<int, List<string>>>;
                    if (task == null) throw new InvalidOperationException("The original game setup process could not be started.");
                    var output = await task;
                    result = MatchCardReadiness.FromSetupResult(output.Item2);
                    if (output.Item1 != 0)
                    {
                        result.Success = false;
                        result.Failed.Add("Game setup exited with code " + output.Item1.ToString(CultureInfo.InvariantCulture) + ".");
                        result.Summary = "Game setup did not finish. Open the app log for the setup error, then try again.";
                    }
                }
                catch (Exception error)
                {
                    string message = (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message;
                    result.Success = false;
                    result.Failed.Add(message);
                    result.Summary = "The original screen setup could not finish: " + message;
                }
                if (!window.IsLoaded) return;
                screenSetupReports[window] = result;
                layoutSetupReports.Remove(window);
                // Exact Discovery references can still be resolved when the older
                // filename-based setup reports a partial archive failure.
                var layouts = await CompleteMatchCardLayoutsAsync(window, root);
                if (!window.IsLoaded) return;
                if (!result.HasResult || !result.Success) SetModelStatus(window, "The original screen setup has unresolved errors. Discovery matchup lookup also ran; open Check match-card setup for both reports.");
                else if (DiscoveryMatchupsNeedAttention(window)) SetModelStatus(window, "Discovery matchup setup prepared the available cards; some references or layouts still need attention. Open Matchup cards in the setup check.");
                else if (layouts.Failed.Count + layouts.Unsupported.Count == 0 && (result.BakeRequired || layouts.BakeRequired || matchupSetupReports[window].BakeRequired))
                    SetModelStatus(window, "Game setup files are ready. Bake once in Discovery, then restart WWE.");
                else if (!result.BakeRequired && !layouts.BakeRequired && layouts.Failed.Count + layouts.Unsupported.Count == 0)
                    SetModelStatus(window, "Game setup reports no changes. Open Check match-card setup if cards still show a still picture.");
            }
            catch (Exception error)
            { SetModelStatus(window, "Game setup could not finish: " + (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message); }
            finally
            {
                if (ownsBusy) busy.SetValue(window, false);
                if (body != null) body.IsEnabled = bodyEnabled;
                combinedGameSetups.Remove(window);
            }
        }

        private static string PlaybackRoot(Window window)
        {
            string folder = window.GetType().GetField("Folder", InstanceMembers)?.GetValue(window) as string;
            string table;
            if (!string.IsNullOrWhiteSpace(folder) && renderTables.TryGetValue(SelectionPath(folder), out table) && File.Exists(table))
                return DiscoveryProfiles.DataRoot(table);
            return RememberedRoot(window) ?? window.GetType().GetField("SetupFolder", InstanceMembers)?.GetValue(window) as string;
        }

        private static void ShowPlaybackCheck(Window window)
        {
            var dialog = new Window {
                Owner = window, Title = "Match-card playback setup", Width = 560, Height = 465,
                MinWidth = 450, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = window.FontFamily, FontSize = 13
            };
            var body = new DockPanel { Margin = new Thickness(16) };
            var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var setup = new Button { Content = "Run game setup", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 8, 0) };
            var enable = new Button { Content = "Enable moving renders", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 8, 0) };
            var refresh = new Button { Content = "Check again", Padding = new Thickness(9, 5, 9, 5) };
            var cards = new Button { Content = "Matchup cards…", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(8, 0, 0, 0) };
            cards.Click += (sender, args) => ShowDiscoveryMatchupCards(window);
            buttons.Children.Add(setup); buttons.Children.Add(enable); buttons.Children.Add(refresh); buttons.Children.Add(cards);
            DockPanel.SetDock(buttons, Dock.Bottom); body.Children.Add(buttons);
            var report = new TextBox {
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0)
            };
            body.Children.Add(report); dialog.Content = body;
            var lifetime = new CancellationTokenSource();
            dialog.Closed += (sender, args) => lifetime.Cancel();
            int reportRequest = 0;
            Func<Task> update = async () =>
            {
                if (lifetime.IsCancellationRequested) return;
                int request = ++reportRequest;
                try
                {
                    string game = window.GetType().GetField("Game", InstanceMembers)?.GetValue(window) as string;
                    string render = window.GetType().GetField("LastGmr", InstanceMembers)?.GetValue(window) as string;
                    int id;
                    int? expected = int.TryParse(window.GetType().GetField("TargetSlot", InstanceMembers)?.GetValue(window) as string,
                        NumberStyles.None, CultureInfo.InvariantCulture, out id) ? (int?)id : null;
                    string app = AppDomain.CurrentDomain.BaseDirectory, root = PlaybackRoot(window);
                    report.Text = "Checking the installed render library and match-card files…";
                    var state = await Task.Run(() => MatchCardReadiness.Evaluate(app, game, root, render, expected, lifetime.Token), lifetime.Token);
                    if (lifetime.IsCancellationRequested || request != reportRequest) return;
                    report.Text = state.Summary + "\n\n" + string.Join("\n", state.Notes);
                    if (!string.IsNullOrWhiteSpace(state.InstalledPath)) report.Text += "\n\nInstalled render:\n" + state.InstalledPath;
                    MatchCardSetupReport last;
                    if (screenSetupReports.TryGetValue(window, out last)) report.Text += "\n\nLast game setup:\n" + last.Summary
                        + (last.Failed.Count == 0 ? "" : "\n" + string.Join("\n", last.Failed));
                    MatchCardLayoutsReport layouts;
                    if (layoutSetupReports.TryGetValue(window, out layouts)) report.Text += "\n\nMatch-card layout repair:\n" + layouts.Summary
                        + LayoutDetails(layouts);
                    DiscoveryMatchupSetupReport matchups;
                    if (matchupSetupReports.TryGetValue(window, out matchups)) report.Text += "\n\nDiscovery matchup catalog:\n" + DiscoveryMatchupDetails(matchups);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (PictureError(error)) { if (!lifetime.IsCancellationRequested) report.Text = error.Message; }
            };
            bool checking = false;
            Func<Task> checkScreens = async () =>
            {
                if (checking || lifetime.IsCancellationRequested) return;
                var nativeBusy = window.GetType().GetField("Busy", InstanceMembers);
                if (nativeBusy != null && nativeBusy.GetValue(window) is bool && (bool)nativeBusy.GetValue(window))
                { report.Text = "Wait for the current model, render or setup to finish, then check again."; return; }
                checking = true; refresh.IsEnabled = false;
                setup.IsEnabled = false; cards.IsEnabled = false; enable.IsEnabled = false;
                bool ownsBusy = nativeBusy != null && nativeBusy.FieldType == typeof(bool);
                if (ownsBusy) nativeBusy.SetValue(window, true);
                try
                {
                    string root = PlaybackRoot(window);
                    if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    {
                        MethodInfo run = window.GetType().GetMethod("RunPy", InstanceMembers, null,
                            new[] { typeof(string), typeof(IEnumerable<string>), typeof(Action<string>), typeof(Action<Process>) }, null);
                        string studio = window.GetType().GetField("Studio", InstanceMembers)?.GetValue(window) as string;
                        await update();
                        if (lifetime.IsCancellationRequested) return;
                        try
                        {
                            Process nativeCheck = null;
                            Task<ValueTuple<int, List<string>>> task;
                            directPlaybackChecks.Add(window);
                            try
                            {
                                task = run?.Invoke(window, new object[] {
                                    Path.Combine(studio ?? AppDomain.CurrentDomain.BaseDirectory, "pipeline", "setup_screens.py"),
                                    new[] { root, "check" }, null, (Action<Process>)(process => nativeCheck = process)
                                }) as Task<ValueTuple<int, List<string>>>;
                            }
                            finally { directPlaybackChecks.Remove(window); }
                            if (task == null) throw new InvalidOperationException("The original screen check could not be started.");
                            // Stop only this dialog's read-only native check. The
                            // original RunPy owns disposal until its task finishes.
                            var stop = lifetime.Token.Register(() => StopPlaybackCheck(nativeCheck));
                            _ = task.ContinueWith(completed => stop.Dispose(), CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            var output = await MatchCardReadiness.WaitForCheckAsync(task, lifetime.Token);
                            lifetime.Token.ThrowIfCancellationRequested();
                            var checkedSetup = MatchCardReadiness.FromSetupResult(output.Item2);
                            if (output.Item1 != 0)
                            {
                                checkedSetup.Success = false;
                                checkedSetup.Summary = "Screen check exited with code " + output.Item1.ToString(CultureInfo.InvariantCulture) + ". Open the app log for details.";
                            }
                            report.Text += "\n\nCurrent screen check:\n" + checkedSetup.Summary;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            lifetime.Token.ThrowIfCancellationRequested();
                            report.Text += "\n\nOriginal screen check could not run: " + (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message;
                        }
                        lifetime.Token.ThrowIfCancellationRequested();
                        var matchups = await ReadDiscoveryMatchupsAsync(window, root, true, lifetime.Token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        var layouts = matchups.Layouts;
                        report.Text += "\n\nCurrent match-card layout check:\n" + layouts.Summary
                            + LayoutDetails(layouts);
                        report.Text += "\n\nDiscovery matchup catalog:\n" + DiscoveryMatchupDetails(matchups);
                    }
                    else await update();
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                { await update(); if (!lifetime.IsCancellationRequested) report.Text += "\n\nScreen check could not run: " + (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message; }
                finally {
                    if (ownsBusy) nativeBusy.SetValue(window, false);
                    checking = false;
                    if (!lifetime.IsCancellationRequested)
                    { refresh.IsEnabled = true; setup.IsEnabled = true; cards.IsEnabled = true; enable.IsEnabled = true; }
                }
            };
            refresh.Click += async (sender, args) => await checkScreens();
            enable.Click += async (sender, args) =>
            {
                try
                {
                    string game = window.GetType().GetField("Game", InstanceMembers)?.GetValue(window) as string;
                    if (!MatchCardReadiness.EnablePlugin(game)) report.Text = "The plugin configuration could not be enabled. Check Game setup and the selected game folder.";
                    else { await update(); if (!lifetime.IsCancellationRequested) report.Text += "\n\nRestart WWE after enabling moving renders."; }
                }
                catch (Exception error) when (PictureError(error)) { report.Text = error.Message; }
            };
            setup.Click += async (sender, args) =>
            {
                var busy = window.GetType().GetField("Busy", InstanceMembers);
                if (busy != null && busy.GetValue(window) is bool && (bool)busy.GetValue(window))
                { report.Text = "Wait for the current model or render to finish, then run game setup."; return; }
                setup.IsEnabled = false;
                try
                {
                    await RunCombinedGameSetupAsync(window);
                    await update();
                }
                catch (Exception error)
                { report.Text = (error is TargetInvocationException && error.InnerException != null ? error.InnerException : error).Message; }
                finally { setup.IsEnabled = true; }
            };
            dialog.Loaded += async (sender, args) => await checkScreens();
            report.Text = "Checking the installed render library and match-card files…"; dialog.Show();
        }

        private static void StopPlaybackCheck(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static string LayoutDetails(MatchCardLayoutsReport report)
        {
            return (report.Failed.Count == 0 ? "" : "\n" + string.Join("\n", report.Failed))
                + (report.Unsupported.Count == 0 ? "" : "\nUnsupported layouts left unchanged:\n" + string.Join("\n", report.Unsupported));
        }

        static partial void ObservePlaybackSetup(object owner, Process process, string script, IEnumerable<string> arguments)
        {
            var window = owner as Window;
            if (window == null || process == null || !string.Equals(Path.GetFileNameWithoutExtension(script?.Replace('\\', '/')), "setup_screens", StringComparison.OrdinalIgnoreCase)) return;
            string setupRoot = null;
            bool requestedCheck = false;
            if (arguments != null) foreach (string argument in arguments)
            {
                if (setupRoot == null) setupRoot = argument;
                else if (argument == "check") requestedCheck = true;
            }
            bool handledByCaller = combinedGameSetups.Contains(window) || directPlaybackChecks.Contains(window);
            if (handledByCaller) return; // The direct caller validates exit code and result.
            var completion = new TaskCompletionSource<MatchCardSetupReport>();
            var gate = new object();
            MatchCardSetupReport captured = null;
            bool outputClosed = false;
            int? exitCode = null;
            Action finish = () =>
            {
                if (!outputClosed || !exitCode.HasValue) return;
                var result = captured ?? MatchCardReadiness.FromSetupResult(null);
                if (exitCode.Value != 0)
                {
                    result.Success = false;
                    result.Failed.Add("Game setup exited with code " + exitCode.Value.ToString(CultureInfo.InvariantCulture) + ".");
                    result.Summary = "Game setup did not finish. Open the app log for the setup error, then try again.";
                }
                if (requestedCheck) result.IsCheck = true;
                completion.TrySetResult(result);
            };
            process.Exited += (sender, args) =>
            {
                lock (gate)
                {
                    try { exitCode = process.ExitCode; }
                    catch (InvalidOperationException) { exitCode = -1; }
                    finish();
                }
            };
            process.OutputDataReceived += (sender, args) =>
            {
                lock (gate)
                {
                    if (args.Data == null) { outputClosed = true; finish(); }
                    else if (args.Data.StartsWith("RESULT ", StringComparison.Ordinal))
                        captured = MatchCardReadiness.FromSetupResult(new[] { args.Data });
                }
            };
            completion.Task.ContinueWith(finished =>
            {
                MatchCardSetupReport result = finished.Result;
                window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
                {
                    if (!window.IsLoaded) return;
                    if (!result.IsCheck || !screenSetupReports.ContainsKey(window)) screenSetupReports[window] = result;
                    if (!result.HasResult || !result.Success) SetModelStatus(window, "Game setup is incomplete. Open Check match-card setup for the failed files.");
                    else if (result.IsCheck && (result.Added > 0 || result.Patched > 0))
                        SetModelStatus(window, "Moving screen files need game setup. Run game setup, then bake once in Discovery.");
                    else if (result.BakeRequired) SetModelStatus(window, "Game setup files are ready. Bake once in Discovery, then restart WWE for moving match cards.");
                    window.GetType().GetMethod("Log", InstanceMembers, null, new[] { typeof(string) }, null)?.Invoke(window, new object[] { result.Summary });
                    if (!result.IsCheck && !string.IsNullOrWhiteSpace(setupRoot))
                        try {
                            await CompleteMatchCardLayoutsAsync(window, setupRoot);
                            if (window.IsLoaded && (!result.HasResult || !result.Success))
                                SetModelStatus(window, "The original screen setup has unresolved errors. Discovery matchup lookup also ran; open Check match-card setup for both reports.");
                        }
                        catch (Exception error) when (PictureError(error)) { SetModelStatus(window, "Match-card layout setup could not finish: " + error.Message); }
                }));
            });
        }
    }
}
