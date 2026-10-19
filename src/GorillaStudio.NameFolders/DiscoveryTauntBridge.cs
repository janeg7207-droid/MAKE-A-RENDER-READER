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

        public static Task<DiscoveryTauntImport> RefreshAsync(object owner, string discoveryRoot)
        {
            return RefreshAsync(owner, discoveryRoot, null, CancellationToken.None, null);
        }

        public static async Task<DiscoveryTauntImport> RefreshAsync(object owner, string discoveryRoot,
            Action<string> progress, CancellationToken cancellationToken, Action<bool> cancellationEnabled = null)
        {
            var window = owner as Window;
            if (window == null) throw new ArgumentException("Choose an open Gorilla Studio window.", nameof(owner));
            long request = Interlocked.Increment(ref nextRefreshRequest);
            lock (refreshRequests) refreshRequests[window] = request;
            string game = null, folder = null;
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
                        folder = window.GetType().GetField("Folder", Members)?.GetValue(window) as string;
                        window.Closed += closed;
                    });
                    await RefreshLock.WaitAsync(cancellation.Token);
                    entered = true;
                    cancellation.Token.ThrowIfCancellationRequested();
                    progress?.Invoke("Reading Discovery taunt definitions and extracted animation assets…");
                    string app = AppDomain.CurrentDomain.BaseDirectory;
                    string dataRoot = null;
                    List<string> missing = await Task.Run(() => DiscoveryTauntLibrary.FindUnresolvedClips(app,
                        discoveryRoot, out dataRoot), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    DiscoveryMotionFallbacks fallbacks = null;
                    if (missing.Count > 0 && !string.IsNullOrWhiteSpace(dataRoot))
                    {
                        progress?.Invoke("Looking for " + missing.Count.ToString(CultureInfo.InvariantCulture) + " animation clips in game and Discovery archives…");
                        try
                        {
                            fallbacks = await DiscoveryArchiveMotions.FindAsync(app, game, dataRoot, missing,
                                Path.Combine(FolderBridge.StorageDirectory, "motion-archives"), progress, cancellation.Token);
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
                    cancellation.Token.ThrowIfCancellationRequested();
                    RefreshOnDispatcher(window, () => {
                        if (!RefreshContextCurrent(window, request, game, folder)) throw new OperationCanceledException(cancellation.Token);
                        cancellationEnabled?.Invoke(false);
                    });
                    // Build updates a shared overlay. Once this short commit starts,
                    // complete the matching UI switch rather than leaving old keys
                    // pointing at an animation removed by a half-canceled refresh.
                    progress?.Invoke("Finishing the animation library…");
                    DiscoveryTauntImport result = await Task.Run(() => DiscoveryTauntLibrary.Build(
                        app, FolderBridge.StorageDirectory, discoveryRoot, fallbacks));
                    RefreshOnDispatcher(window, () => {
                        if (RefreshContextCurrent(window, request, game, folder)) Apply(window, result);
                    });
                    return result;
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

        private static void RefreshOnDispatcher(Window window, Action action)
        {
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
                throw new OperationCanceledException("The Gorilla Studio window is closing.");
            if (window.Dispatcher.CheckAccess()) action();
            else window.Dispatcher.Invoke(action);
        }

        private static bool RefreshContextCurrent(Window window, long request, string game, string folder)
        {
            long current;
            lock (refreshRequests)
                if (!refreshRequests.TryGetValue(window, out current) || current != request) return false;
            return window.IsLoaded
                && string.Equals(game, window.GetType().GetField("Game", Members)?.GetValue(window) as string, StringComparison.OrdinalIgnoreCase)
                && string.Equals(folder, window.GetType().GetField("Folder", Members)?.GetValue(window) as string, StringComparison.OrdinalIgnoreCase);
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
                itemType.GetProperty("Note").SetValue(item, entry.Reason + " · " + entry.Clip, null);
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
            var list = Field<ListBox>(window, "TauntList");
            object selected = list == null ? null : list.SelectedItem;
            if (selected == null) return;
            string key = Convert.ToString(selected.GetType().GetProperty("Key").GetValue(selected, null));
            if (DiscoveryTauntSearch.IsUnavailableKey(key))
            {
                Field<DispatcherTimer>(window, "PreviewTimer")?.Stop();
                var image = Field<Image>(window, "TauntPreview");
                if (image != null) image.Source = null;
                var unavailable = Field<TextBlock>(window, "PreviewMaking");
                if (unavailable != null)
                {
                    unavailable.Text = Convert.ToString(selected.GetType().GetProperty("Note").GetValue(selected, null));
                    unavailable.Visibility = Visibility.Visible;
                }
                return;
            }
            bool discovery = key.StartsWith("discovery", StringComparison.Ordinal) || key.Contains("_discovery_");
            string library = window.GetType().GetField("Library", Members)?.GetValue(window) as string;
            // Bundled thumbnails are retained exactly. A missing stock atlas
            // can be supplied by the same actual wrestler preview as a newly
            // discovered taunt, without changing any existing native atlas.
            if (!discovery && !string.IsNullOrWhiteSpace(library)
                && File.Exists(Path.Combine(library, "previews_game", key + ".png"))) return;
            if (ShowDiscoveryPreview(window, key)) return;
            Field<DispatcherTimer>(window, "PreviewTimer")?.Stop();
            var preview = Field<Image>(window, "TauntPreview");
            if (preview != null) preview.Source = null;
            var making = Field<TextBlock>(window, "PreviewMaking");
            if (making != null)
            {
                making.Text = FolderBridge.AutomaticTauntPreviewRequested(window, key)
                    ? "Preparing your wrestler preview…" : "Select this taunt to create its preview\non your selected wrestler.";
                making.Visibility = Visibility.Visible;
            }
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
