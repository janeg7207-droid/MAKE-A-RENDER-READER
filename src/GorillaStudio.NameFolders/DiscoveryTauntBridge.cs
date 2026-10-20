using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        internal static string DiscoveryEngineRoot { get; set; }
    }

    public static class DiscoveryTauntBridge
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1, 1);
        private static readonly HashSet<Window> hooked = new HashSet<Window>();
        private static readonly Dictionary<Window, string> applied = new Dictionary<Window, string>();
        private static readonly HashSet<Window> searchHooked = new HashSet<Window>();
        private static readonly Dictionary<Window, long> refreshRequests = new Dictionary<Window, long>();
        private static long nextRefreshRequest;
        private static readonly Dictionary<Window, DiscoveryAnimationReport> animationReports = new Dictionary<Window, DiscoveryAnimationReport>();
        private static readonly HashSet<Window> reportWindows = new HashSet<Window>();
        private static readonly Dictionary<Window, Window> animationDialogs = new Dictionary<Window, Window>();

        public static Task<DiscoveryTauntImport> RefreshAsync(object owner, string discoveryRoot)
        {
            return RefreshAsync(owner, discoveryRoot, null, CancellationToken.None, null);
        }

        public static async Task<DiscoveryTauntImport> RefreshAsync(object owner, string discoveryRoot,
            Action<string> progress, CancellationToken cancellationToken, Action<bool> cancellationEnabled = null,
            Func<long> sourceRevision = null,
            Func<DiscoveryTauntLibrary.Preparation, CancellationToken, Task<IDisposable>> enterCommit = null,
            Func<bool> contextCurrent = null, Action<DiscoveryTauntImport> appliedResult = null,
            Func<CancellationToken, Task> beforeArchiveLookup = null, bool publishAvailableFirst = false)
        {
            var window = owner as Window;
            if (window == null) throw new ArgumentException("Choose an open Gorilla Studio window.", nameof(owner));
            long request = Interlocked.Increment(ref nextRefreshRequest);
            lock (refreshRequests) refreshRequests[window] = request;
            string game = null;
            bool entered = false;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            try
            {
                EventHandler closed = (sender, args) => cancellation.Cancel();
                try
                {
                    RefreshOnDispatcher(window, () => {
                        if (!window.IsLoaded) throw new OperationCanceledException(cancellation.Token);
                        game = window.GetType().GetField("Game", Members)?.GetValue(window) as string;
                        window.Closed += closed;
                    });
                    await RefreshLock.WaitAsync(cancellation.Token);
                    entered = true;
                    cancellation.Token.ThrowIfCancellationRequested();
                    string app = AppDomain.CurrentDomain.BaseDirectory;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        RefreshOnDispatcher(window, () => {
                            if (!RefreshContextCurrent(window, request, game, contextCurrent)) throw new OperationCanceledException(cancellation.Token);
                        });
                        // Every build has a private revision overlay. Keep the
                        // working engine library available while the new one is
                        // scanned, decoded and validated in the background.
                        CancellationToken lookupToken = cancellation.Token;
                        lookupToken.ThrowIfCancellationRequested();
                        progress?.Invoke(attempt == 0 ? "Reading Discovery taunt definitions and extracted animation assets…"
                            : "Animation sources changed. Reading the current files again…");
                        DiscoveryTauntLibrary.Preparation prepared = await Task.Run(() =>
                            DiscoveryTauntLibrary.Prepare(app, discoveryRoot, lookupToken, sourceRevision), lookupToken);
                        IReadOnlyList<string> missing = prepared.Missing;
                        string dataRoot = prepared.DataRoot;
                        if (publishAvailableFirst && missing.Count > 0 && !string.IsNullOrWhiteSpace(dataRoot))
                        {
                            DiscoveryTauntLibrary.Preparation publication = prepared;
                            DiscoveryMotionFallbacks recovered = null;
                            bool originalIncomplete = false;
                            try
                            {
                                DiscoveryTauntImport staged = await DiscoveryLibraryRefreshStages.RunAsync<DiscoveryTauntImport, DiscoveryMotionFallbacks>(
                                    async stageToken => {
                                        progress?.Invoke("Preparing available Discovery animations; additional archives will be checked afterward…");
                                        var available = await Task.Run(() => DiscoveryTauntLibrary.BuildCurrentPreparation(
                                            prepared, FolderBridge.StorageDirectory, null, progress, stageToken), stageToken);
                                        originalIncomplete = available.ScanIncomplete;
                                        available.ArchiveLookupPending = true;
                                        available.ScanIncomplete = true;
                                        available.ArchiveStatus = "Additional game and Discovery archives are being checked in the background. Available motions can be previewed now.";
                                        available.Warnings.Add("Additional animation archive lookup is pending; missing clips may become available when it finishes.");
                                        return available;
                                    },
                                    (available, stageToken) => PublishImportAsync(window, request, game, dataRoot ?? discoveryRoot,
                                        publication, available, cancellationEnabled, enterCommit, contextCurrent, appliedResult, stageToken),
                                    available => true,
                                    async (available, stageToken) => {
                                        recovered = await ReadMissingArchiveMotionsAsync(app, game, dataRoot, missing, prepared,
                                            progress, beforeArchiveLookup, stageToken);
                                        return recovered;
                                    },
                                    archive => archive != null && archive.Assets.Count > 0,
                                    async (available, archive, stageToken) => {
                                        // The first overlay may already be in use
                                        // by Blender. Enrichment gets a new root.
                                        publication = DiscoveryTauntLibrary.ForkPreparation(prepared);
                                        return await Task.Run(() => DiscoveryTauntLibrary.BuildCurrentPreparation(publication,
                                            FolderBridge.StorageDirectory, archive, progress, stageToken), stageToken);
                                    }, lookupToken);
                                if (recovered != null && recovered.Assets.Count == 0)
                                {
                                    // No recovered bytes means the working
                                    // manifest/NPZ library is already complete.
                                    // Update diagnostics without copying it again.
                                    staged = DiscoveryTauntLibrary.WithArchiveDiagnostics(staged, recovered, originalIncomplete);
                                    if (!await Task.Run(() => prepared.IsCurrent(lookupToken), lookupToken)) continue;
                                    RefreshOnDispatcher(window, () => {
                                        if (!RefreshContextCurrent(window, request, game, contextCurrent)) throw new OperationCanceledException(lookupToken);
                                        RecordAnimationReport(window, game, dataRoot ?? discoveryRoot, staged);
                                        appliedResult?.Invoke(staged);
                                    });
                                }
                                return staged;
                            }
                            catch (DiscoveryTauntSourcesChangedException) { continue; }
                        }
                        DiscoveryMotionFallbacks fallbacks = null;
                        if (missing.Count > 0 && !string.IsNullOrWhiteSpace(dataRoot))
                        {
                            // Interactive model/picture preparation gets first
                            // use of the shared archive reader after the roster
                            // handoff. No UI lock is held while this waits.
                            if (beforeArchiveLookup != null) await beforeArchiveLookup(lookupToken);
                            if (!await Task.Run(() => prepared.IsCurrent(lookupToken), lookupToken)) continue;
                            progress?.Invoke("Looking for " + missing.Count.ToString(CultureInfo.InvariantCulture) + " animation clips in game and Discovery archives…");
                            try
                            {
                                fallbacks = await DiscoveryArchiveMotions.FindAsync(app, game, dataRoot, missing,
                                    Path.Combine(FolderBridge.StorageDirectory, "motion-archives"), progress, lookupToken);
                                if (fallbacks == null) throw new InvalidOperationException("The animation archive lookup returned no result.");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                                || error is InvalidDataException || error is InvalidOperationException || error is ArgumentException
                                || error is NotSupportedException || error is System.ComponentModel.Win32Exception)
                            {
                                fallbacks = new DiscoveryMotionFallbacks {
                                    Incomplete = true, Status = "Automatic animation archive lookup could not finish: " + error.Message
                                };
                                fallbacks.Warnings.Add(fallbacks.Status);
                                foreach (string clip in missing) fallbacks.Issues[clip] = fallbacks.Status;
                            }
                        }
                        lookupToken.ThrowIfCancellationRequested();
                        // A newly exported loose override outranks an archive
                        // fallback, even if it appeared while that lookup ran.
                        if (!await Task.Run(() => prepared.IsCurrent(lookupToken), lookupToken)) continue;
                        progress?.Invoke("Preparing the new animation library in the background…");
                        DiscoveryTauntImport result;
                        try
                        {
                            result = await Task.Run(() => DiscoveryTauntLibrary.BuildCurrentPreparation(
                                prepared, FolderBridge.StorageDirectory, fallbacks, progress, lookupToken), lookupToken);
                        }
                        catch (DiscoveryTauntSourcesChangedException) { continue; }
                        if (!await Task.Run(() => prepared.IsCurrent(lookupToken), lookupToken)) continue;
                        try { await PublishImportAsync(window, request, game, dataRoot ?? discoveryRoot, prepared, result,
                            cancellationEnabled, enterCommit, contextCurrent, appliedResult, lookupToken); }
                        catch (DiscoveryTauntSourcesChangedException) { continue; }
                        return result;
                    }
                    throw new InvalidOperationException("Discovery animation files are still changing. Finish extracting or saving them, then load Discovery again.");
                }
                finally
                {
                    if (!window.Dispatcher.HasShutdownStarted && !window.Dispatcher.HasShutdownFinished)
                        RefreshOnDispatcher(window, () => window.Closed -= closed);
                }
            }
            finally
            {
                if (entered) RefreshLock.Release();
                lock (refreshRequests)
                {
                    long current;
                    if (refreshRequests.TryGetValue(window, out current) && current == request) refreshRequests.Remove(window);
                }
            }
        }

        private static async Task<DiscoveryMotionFallbacks> ReadMissingArchiveMotionsAsync(string app, string game, string dataRoot,
            IReadOnlyList<string> missing, DiscoveryTauntLibrary.Preparation prepared, Action<string> progress,
            Func<CancellationToken, Task> beforeArchiveLookup, CancellationToken token)
        {
            if (beforeArchiveLookup != null) await beforeArchiveLookup(token);
            if (!await Task.Run(() => prepared.IsCurrent(token), token)) throw new DiscoveryTauntSourcesChangedException();
            progress?.Invoke("Available animations are ready. Checking archives for " + missing.Count.ToString(CultureInfo.InvariantCulture) + " additional clips…");
            try
            {
                var fallback = await DiscoveryArchiveMotions.FindAsync(app, game, dataRoot, missing,
                    Path.Combine(FolderBridge.StorageDirectory, "motion-archives"), progress, token);
                if (fallback == null) throw new InvalidOperationException("The animation archive lookup returned no result.");
                return fallback;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException
                || error is InvalidOperationException || error is ArgumentException || error is NotSupportedException || error is System.ComponentModel.Win32Exception)
            {
                var fallback = new DiscoveryMotionFallbacks { Incomplete = true,
                    Status = "Automatic animation archive lookup could not finish: " + error.Message };
                fallback.Warnings.Add(fallback.Status);
                foreach (string clip in missing) fallback.Issues[clip] = fallback.Status;
                return fallback;
            }
        }

        private static async Task PublishImportAsync(Window window, long request, string game, string discoveryRoot,
            DiscoveryTauntLibrary.Preparation prepared, DiscoveryTauntImport result, Action<bool> cancellationEnabled,
            Func<DiscoveryTauntLibrary.Preparation, CancellationToken, Task<IDisposable>> enterCommit,
            Func<bool> contextCurrent, Action<DiscoveryTauntImport> appliedResult, CancellationToken token)
        {
            if (!await Task.Run(() => prepared.IsCurrent(token), token)) throw new DiscoveryTauntSourcesChangedException();
            IDisposable commit = null;
            try
            {
                if (enterCommit != null) commit = await enterCommit(prepared, token);
                token.ThrowIfCancellationRequested();
                RefreshOnDispatcher(window, () => {
                    if (!RefreshContextCurrent(window, request, game, contextCurrent)) throw new OperationCanceledException(token);
                    cancellationEnabled?.Invoke(false);
                    // Native preparation keys omit the engine data root.
                    window.GetType().GetField("PrepWanted", Members)?.SetValue(window, null);
                    window.GetType().GetField("PrepDone", Members)?.SetValue(window, null);
                    RecordAnimationReport(window, game, discoveryRoot, result);
                    Apply(window, result);
                    appliedResult?.Invoke(result);
                    window.GetType().GetMethod("Prepare", Members, null, new[] { typeof(string) }, null)?.Invoke(window, new object[] { null });
                });
            }
            finally
            {
                commit?.Dispose();
                if (commit != null && !window.Dispatcher.HasShutdownStarted && !window.Dispatcher.HasShutdownFinished)
                    RefreshOnDispatcher(window, () => cancellationEnabled?.Invoke(true));
            }
        }

        private static void RefreshOnDispatcher(Window window, Action action)
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
                throw new OperationCanceledException("The Gorilla Studio window is closing.");
            if (window.Dispatcher.CheckAccess()) action();
            else window.Dispatcher.Invoke(action);
        }

        private static bool RefreshContextCurrent(Window window, long request, string game, Func<bool> contextCurrent)
        {
            long current;
            lock (refreshRequests)
                if (!refreshRequests.TryGetValue(window, out current) || current != request) return false;
            return window.IsLoaded
                && string.Equals(game, window.GetType().GetField("Game", Members)?.GetValue(window) as string, StringComparison.OrdinalIgnoreCase)
                && (contextCurrent == null || contextCurrent());
        }

        public static void NoteArchiveLookupStopped(object owner, string game, string discoveryRoot,
            DiscoveryTauntImport available, string reason)
        {
            var window = owner as Window;
            if (window == null || !window.IsLoaded || available == null) return;
            var fallback = new DiscoveryMotionFallbacks { Incomplete = true, ArchivesSearched = available.ArchivesSearched,
                ArchiveCount = available.ArchiveCount, Status = reason };
            fallback.Warnings.Add(reason);
            foreach (DiscoveryUnavailableTaunt entry in available.Unavailable) fallback.Issues[entry.Clip] = reason;
            var report = DiscoveryTauntLibrary.WithArchiveDiagnostics(available, fallback, true);
            RecordAnimationReport(window, game, discoveryRoot, report);
        }

        private static void RecordAnimationReport(Window window, string game, string root, DiscoveryTauntImport import)
        {
            var report = new DiscoveryAnimationReport {
                HelperVersion = typeof(FolderBridge).Assembly.GetName().Version.ToString(3), CapturedUtc = DateTime.UtcNow,
                GameRoot = game, DiscoveryRoot = root, HasImport = true,
                Definitions = import.Definitions, Available = import.Available, Imported = import.Imported,
                ArchiveImported = import.ArchiveImported, ArchiveCount = import.ArchiveCount,
                VerifiedImported = import.VerifiedImported, VerifiedPackHash = import.VerifiedPackHash,
                Missing = import.Missing, Unsupported = import.Unsupported, ReadFailures = import.ReadFailures,
                CacheFailures = import.CacheFailures, ArchivesSearched = import.ArchivesSearched,
                ArchiveStatus = import.ArchiveStatus, ScanIncomplete = import.ScanIncomplete
            };
            report.SearchRoots.AddRange(import.SearchRoots);
            report.Warnings.AddRange(import.Warnings);
            foreach (DiscoveryUnavailableTaunt item in import.Unavailable)
            {
                var entry = new DiscoveryAnimationReportEntry { Name = item.Name, Id = item.Id, Variant = item.Variant,
                    Clip = item.Clip, Kind = item.Kind.ToString(), Reason = item.Reason };
                entry.Names.AddRange(item.Names);
                report.Unavailable.Add(entry);
            }
            SaveAnimationReport(report);
            animationReports[window] = report;
            if (reportWindows.Add(window))
                window.Closed += (sender, args) => {
                    animationReports.Remove(window); reportWindows.Remove(window); animationDialogs.Remove(window);
                };
        }

        private static void SaveAnimationReport(DiscoveryAnimationReport report)
        {
            string temporary = null;
            try
            {
                string directory = Path.Combine(FolderBridge.StorageDirectory, "animation-diagnostics");
                Directory.CreateDirectory(directory);
                report.SavedPath = Path.Combine(directory, "latest.txt");
                temporary = report.SavedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, report.Format(null), new UTF8Encoding(false));
                if (File.Exists(report.SavedPath)) File.Replace(temporary, report.SavedPath, null);
                else File.Move(temporary, report.SavedPath);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                report.SavedPath = null;
                report.Warnings.Add("The animation report could not be saved: " + error.Message + ". Copy it from Animation details instead.");
            }
            finally
            {
                if (temporary != null)
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            }
        }

        public static void ShowAnimationDetails(object owner, string discoveryRoot)
        {
            var window = owner as Window;
            if (window == null || !window.IsLoaded) return;
            Window existing;
            if (animationDialogs.TryGetValue(window, out existing) && existing.IsLoaded) { existing.Activate(); return; }
            var dialog = new Window {
                Owner = window, Title = "Animation details", Width = 880, Height = 660, MinWidth = 540, MinHeight = 350,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = window.FontFamily, FontSize = 13
            };
            animationDialogs[window] = dialog;
            var body = new DockPanel { Margin = new Thickness(14) };
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            heading.Children.Add(new TextBlock { Text = "Read the complete reason, select text, or copy the report.", TextWrapping = TextWrapping.Wrap });
            var query = new TextBox { Text = Field<TextBox>(window, "SearchBox")?.Text ?? "", Padding = new Thickness(5),
                Margin = new Thickness(0, 7, 0, 0), ToolTip = "Show matching unavailable names or clip IDs first; the full report remains below." };
            heading.Children.Add(query); DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
            var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var buttons = new WrapPanel();
            Style style = Field<Button>(window, "GameButton")?.Style;
            var copy = new Button { Content = "Copy report", Style = style, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
            var refresh = new Button { Content = "Reload report", Style = style, Padding = copy.Padding, Margin = copy.Margin };
            var close = new Button { Content = "Close", Style = style, Padding = copy.Padding };
            buttons.Children.Add(copy); buttons.Children.Add(refresh); buttons.Children.Add(close); footer.Children.Add(buttons);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 7, 0, 0) };
            footer.Children.Add(status); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
            var text = new TextBox {
                IsReadOnly = true, IsReadOnlyCaretVisible = true, AcceptsReturn = true, AcceptsTab = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(9),
                Background = new SolidColorBrush(Color.FromRgb(16, 16, 20)), Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"), FontSize = 12
            };
            body.Children.Add(text);
            Action render = () => {
                DiscoveryAnimationReport report;
                if (!animationReports.TryGetValue(window, out report))
                    report = new DiscoveryAnimationReport { HelperVersion = typeof(FolderBridge).Assembly.GetName().Version.ToString(3),
                        GameRoot = window.GetType().GetField("Game", Members)?.GetValue(window) as string, DiscoveryRoot = discoveryRoot };
                text.Text = report.Format(query.Text);
                text.ScrollToHome();
                status.Text = report.SavedPath == null ? "The text can be selected and copied with Ctrl+C."
                    : "Full report saved to " + report.SavedPath;
            };
            query.TextChanged += (sender, args) => render();
            refresh.Click += (sender, args) => render();
            close.Click += (sender, args) => dialog.Close();
            copy.Click += (sender, args) => {
                try { Clipboard.SetText(text.Text); status.Text = "Report copied."; }
                catch (System.Runtime.InteropServices.ExternalException) { status.Text = "The clipboard is busy. Select the report text and press Ctrl+C, or try Copy report again."; }
            };
            dialog.Closed += (sender, args) => animationDialogs.Remove(window);
            render(); dialog.Content = body; dialog.Show();
        }

        public static void ConfigureProcess(object owner, Process process, string script, IEnumerable<string> args)
        {
            if (args != null && args.Any(DiscoveryTauntSearch.IsUnavailableKey))
                throw new InvalidOperationException("This Discovery taunt is unavailable for rendering. Read its search-result reason for the animation lookup, scan, format, read or cache issue.");
            string root = FolderBridge.DiscoveryEngineRoot;
            if (process == null || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(script)) return;
            string name = Path.GetFileNameWithoutExtension(script.Replace('\\', '/'));
            if (string.Equals(name, "gorilla_render", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "make_taunt_capture", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "preview_game", StringComparison.OrdinalIgnoreCase))
                process.StartInfo.EnvironmentVariables["GS_ROOT"] = root;
        }

        public static void CapturePreview(object owner, string script, IEnumerable<string> args, IEnumerable<string> engineLines)
        {
            string key = DiscoveryTauntPreviewCapture.Capture(FolderBridge.DiscoveryEngineRoot, script, args, engineLines);
            var window = owner as Window;
            if (key == null || window == null) return;
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var list = Field<ListBox>(window, "TauntList");
                    object selected = list == null ? null : list.SelectedItem;
                    if (selected == null || !string.Equals(Convert.ToString(selected.GetType().GetProperty("Key").GetValue(selected, null)), key, StringComparison.Ordinal)) return;
                    ShowDiscoveryPreview(window, key);
                }
                catch (Exception error) when (error is ArgumentException || error is TargetInvocationException || error is InvalidOperationException)
                { /* A thumbnail must never turn a successful preview into a render error. */ }
            }));
        }

        private static void Apply(Window window, DiscoveryTauntImport import)
        {
            Type type = window.GetType();
            FieldInfo allField = type.GetField("AllTaunts", Members), library = type.GetField("Library", Members);
            var all = allField == null ? null : allField.GetValue(window) as IList;
            var list = Field<ListBox>(window, "TauntList");
            if (all == null || list == null || library == null || !allField.FieldType.IsGenericType)
                throw new InvalidOperationException("This Gorilla Studio version does not expose its taunt library.");
            string signature;
            using (var sha = SHA256.Create())
                signature = (import.Root ?? "") + "\n" + BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(import.Library, "taunts.json"))))
                    + "\n" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", import.Unavailable.Select(entry =>
                        entry.Key + "\t" + entry.Name + "\t" + string.Join("|", entry.Names) + "\t" + entry.Reason)))));
            string previous;
            if (applied.TryGetValue(window, out previous) && previous == signature &&
                string.Equals(FolderBridge.DiscoveryEngineRoot, import.Root, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(library.GetValue(window) as string, import.Library, StringComparison.OrdinalIgnoreCase)) return;
            AttachTauntSearch(window, list);
            Type itemType = allField.FieldType.GetGenericArguments()[0];
            object selected = list.SelectedItem;
            string key = selected == null ? null : Convert.ToString(itemType.GetProperty("Key").GetValue(selected, null));
            var replacement = new List<object>();
            foreach (Dictionary<string, object> entry in import.Taunts)
            {
                object item = Activator.CreateInstance(itemType);
                string name = Text(entry, "name"), itemKey = Text(entry, "key");
                string[] aliases = Values(entry, "also"), tags = Values(entry, "tags"), tested = Values(entry, "tested_on");
                double seconds = entry.ContainsKey("seconds") ? Convert.ToDouble(entry["seconds"], CultureInfo.InvariantCulture) : 0;
                string note = (seconds > 0 ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s · " : "") +
                    string.Join(" · ", new[] { string.Join(" ", tags), string.Join(", ", tested),
                        aliases.Length > 0 ? "also: " + string.Join(" / ", aliases) : "" }.Where(value => value.Length > 0));
                itemType.GetProperty("Key").SetValue(item, itemKey, null);
                itemType.GetProperty("Name").SetValue(item, name, null);
                itemType.GetProperty("Note").SetValue(item, note, null);
                itemType.GetField("Seconds").SetValue(item, seconds);
                itemType.GetField("Find").SetValue(item, DiscoveryTauntSearch.BuildText(name, aliases, tags, Text(entry, "clip")));
                string preview = Text(entry, "preview");
                itemType.GetField("PreviewPath").SetValue(item, string.IsNullOrEmpty(preview) ? null :
                    Path.Combine(import.Library, preview.Replace('/', Path.DirectorySeparatorChar)));
                replacement.Add(item);
            }
            foreach (DiscoveryUnavailableTaunt entry in import.Unavailable)
            {
                object item = Activator.CreateInstance(itemType);
                itemType.GetProperty("Key").SetValue(item, entry.Key, null);
                itemType.GetProperty("Name").SetValue(item, entry.Name, null);
                itemType.GetProperty("Note").SetValue(item, DiscoveryAnimationReport.ShortReason(entry.Kind.ToString(), entry.Reason)
                    + " · " + entry.Clip + " · Animation details…", null);
                itemType.GetField("Find").SetValue(item, DiscoveryTauntSearch.BuildText(entry.Name, entry.Names, new[] { "Unavailable" }, entry.Clip));
                replacement.Add(item);
            }
            replacement = replacement.OrderBy(item => Convert.ToString(itemType.GetProperty("Name").GetValue(item, null)), StringComparer.OrdinalIgnoreCase).ToList();
            // Complete the new list before switching the engine's data root.
            FolderBridge.DiscoveryEngineRoot = import.Root;
            library.SetValue(window, import.Library);
            applied[window] = signature;
            list.ItemsSource = null;
            all.Clear();
            foreach (object item in replacement) all.Add(item);
            list.ItemsSource = all;
            ApplyTauntSearch(window);
            if (key != null)
                list.SelectedItem = replacement.FirstOrDefault(item => string.Equals(
                    Convert.ToString(itemType.GetProperty("Key").GetValue(item, null)), key, StringComparison.Ordinal));
            if (hooked.Add(window))
            {
                list.SelectionChanged += (sender, args) => ShowPreviewHint(window);
                window.Closed += (sender, args) => { hooked.Remove(window); applied.Remove(window); searchHooked.Remove(window); };
            }
            ShowPreviewHint(window);
            FolderBridge.AttachAutomaticTauntPreview(window);
        }

        private sealed class UnavailableTauntConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            { return DiscoveryTauntSearch.IsUnavailableKey(value as string); }
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            { throw new NotSupportedException(); }
        }

        private static void AttachTauntSearch(Window window, ListBox list)
        {
            if (!searchHooked.Add(window)) return;
            var search = Field<TextBox>(window, "SearchBox");
            MethodInfo original = window.GetType().GetMethod("OnSearch", Members);
            if (search != null)
            {
                if (original != null)
                    search.RemoveHandler(TextBoxBase.TextChangedEvent,
                        (TextChangedEventHandler)Delegate.CreateDelegate(typeof(TextChangedEventHandler), window, original));
                search.TextChanged += (sender, args) => ApplyTauntSearch(window);
                search.ToolTip = "Search taunt names, variants or animation IDs. Unavailable animations show their reason.";
            }
            var style = new Style(typeof(ListBoxItem), list.ItemContainerStyle);
            var unavailable = new DataTrigger { Binding = new Binding("Key") { Converter = new UnavailableTauntConverter() }, Value = true };
            unavailable.Setters.Add(new Setter(UIElement.IsEnabledProperty, false));
            unavailable.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            unavailable.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
            unavailable.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Note")));
            unavailable.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));
            style.Triggers.Add(unavailable); list.ItemContainerStyle = style;
        }

        private static void ApplyTauntSearch(Window window)
        {
            var all = window.GetType().GetField("AllTaunts", Members)?.GetValue(window) as IList;
            var list = Field<ListBox>(window, "TauntList");
            if (all == null || list == null) return;
            string query = Field<TextBox>(window, "SearchBox")?.Text ?? "";
            var rows = all.Cast<object>().Where(item => DiscoveryTauntSearch.Matches(
                Convert.ToString(item.GetType().GetField("Find").GetValue(item), CultureInfo.InvariantCulture), query)).ToList();
            list.ItemsSource = rows;
            var count = Field<TextBlock>(window, "TauntCount");
            int unavailable = rows.Count(item => DiscoveryTauntSearch.IsUnavailableKey(Convert.ToString(item.GetType().GetProperty("Key").GetValue(item, null))));
            if (count != null) count.Text = (rows.Count == all.Count ? all.Count.ToString(CultureInfo.InvariantCulture)
                : rows.Count.ToString(CultureInfo.InvariantCulture) + " of " + all.Count.ToString(CultureInfo.InvariantCulture))
                + " taunts" + (unavailable > 0 ? " · " + unavailable.ToString(CultureInfo.InvariantCulture) + " unavailable" : "");
            var hint = Field<TextBlock>(window, "SearchHint");
            if (hint != null) hint.Visibility = string.IsNullOrWhiteSpace(query) ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void ShowPreviewHint(Window window)
        {
            FolderBridge.ShowRandyTauntPreviewHint(window);
        }

        internal static bool ShowCapturedWrestlerPreview(Window window, string key)
        {
            var selected = Field<ListBox>(window, "TauntList")?.SelectedItem;
            return selected != null && string.Equals(Convert.ToString(selected.GetType().GetProperty("Key").GetValue(selected, null)), key, StringComparison.Ordinal)
                && ShowDiscoveryPreview(window, key);
        }

        private static bool ShowDiscoveryPreview(Window window, string key)
        {
            if (string.IsNullOrEmpty(key) || !Regex.IsMatch(key, @"\A[A-Za-z0-9_]+\z") || DiscoveryTauntSearch.IsUnavailableKey(key)) return false;
            try
            {
                Type type = window.GetType();
                string library = type.GetField("Library", Members)?.GetValue(window) as string;
                var image = Field<Image>(window, "TauntPreview");
                var timer = Field<DispatcherTimer>(window, "PreviewTimer");
                FieldInfo framesField = type.GetField("PreviewFrames", Members), indexField = type.GetField("PreviewIndex", Members);
                if (string.IsNullOrWhiteSpace(library) || image == null || timer == null || framesField == null || indexField == null) return false;
                string path = Path.Combine(library, "previews_game", key + ".png");
                if (!File.Exists(path)) return false;

                // The native URI cache can keep an atlas from a different
                // wrestler after this filename is replaced. Decode a fresh
                // stream and retain no open file handle, so each selection uses
                // the exact preview that was most recently captured.
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                var frames = new List<ImageSource>();
                int height = bitmap.PixelHeight, width = bitmap.PixelWidth;
                if (height <= 0 || width <= 0) return false;
                if (width < height) frames.Add(bitmap);
                else
                {
                    int count = Math.Max(1, width / height);
                    for (int index = 0; index < count; index++)
                    {
                        var frame = new CroppedBitmap(bitmap, new Int32Rect(index * height, 0, height, height));
                        frame.Freeze();
                        frames.Add(frame);
                    }
                }
                timer.Stop();
                framesField.SetValue(window, frames);
                indexField.SetValue(window, 0);
                image.Source = frames[0];
                timer.Interval = TimeSpan.FromSeconds(1.0 / 15.0);
                var making = Field<TextBlock>(window, "PreviewMaking");
                if (making != null) making.Visibility = Visibility.Collapsed;
                timer.Start();
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is NotSupportedException || error is ArgumentException || error is InvalidOperationException || error is FormatException)
            { return false; }
        }

        private static T Field<T>(object owner, string name) where T : class
        {
            FieldInfo field = owner.GetType().GetField(name, Members);
            return field == null ? null : field.GetValue(owner) as T;
        }

        private static string Text(Dictionary<string, object> item, string key)
        {
            object value;
            return item.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static string[] Values(Dictionary<string, object> item, string key)
        {
            object value;
            return item.TryGetValue(key, out value) && value is IEnumerable && !(value is string)
                ? ((IEnumerable)value).Cast<object>().Select(element => Convert.ToString(element, CultureInfo.InvariantCulture)).ToArray()
                : new string[0];
        }
    }
}
