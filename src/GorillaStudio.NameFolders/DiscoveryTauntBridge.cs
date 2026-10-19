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

        public static async Task<DiscoveryTauntImport> RefreshAsync(object owner, string discoveryRoot)
        {
            await RefreshLock.WaitAsync();
            try
            {
                DiscoveryTauntImport result = await Task.Run(() => DiscoveryTauntLibrary.Build(
                    AppDomain.CurrentDomain.BaseDirectory, FolderBridge.StorageDirectory, discoveryRoot));
                var window = owner as Window;
                if (window == null) throw new ArgumentException("Choose an open Gorilla Studio window.", nameof(owner));
                if (window.Dispatcher.CheckAccess()) { if (window.IsLoaded) Apply(window, result); }
                else if (!window.Dispatcher.HasShutdownStarted && !window.Dispatcher.HasShutdownFinished)
                    window.Dispatcher.Invoke(new Action(() => { if (window.IsLoaded) Apply(window, result); }));
                return result;
            }
            finally { RefreshLock.Release(); }
        }

        public static void ConfigureProcess(object owner, Process process, string script, IEnumerable<string> args)
        {
            if (args != null && args.Any(DiscoveryTauntSearch.IsUnavailableKey))
                throw new InvalidOperationException("This Discovery taunt has no playable animation. Its search result explains which animation is missing or unsupported.");
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
            if (!key.StartsWith("discovery", StringComparison.Ordinal) && !key.Contains("_discovery_")) return;
            if (ShowDiscoveryPreview(window, key)) return;
            var making = Field<TextBlock>(window, "PreviewMaking");
            if (making != null)
            {
                making.Text = "Click Preview to see this taunt\non your selected wrestler.";
                making.Visibility = Visibility.Visible;
            }
        }

        private static bool ShowDiscoveryPreview(Window window, string key)
        {
            if (string.IsNullOrEmpty(key) || (!key.StartsWith("discovery", StringComparison.Ordinal) && !key.Contains("_discovery_"))) return false;
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
