using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<Window, PngExportControls> pngExportWindows = new Dictionary<Window, PngExportControls>();

        private sealed class PngExportControls
        {
            internal Button Save;
            internal Button Open;
            internal TextBlock Status;
            internal DispatcherTimer Timer;
            internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            internal PngExportContext LastResult;
            internal bool Closed;
            internal int Saving;
        }

        private sealed class PngExportContext
        {
            internal string Output, Folder, TauntKey, TauntName, Name, Id, Attire;
            internal BitmapSource ImageRoot;
            internal readonly HashSet<string> Images = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal string Stem { get { return PngLabel(Id, "wrestler", 8) + "_" + PngLabel(Name, "Wrestler", 24)
                + "__Attire-" + PngLabel(Attire, "Default", 18) + "__" + PngLabel(TauntName ?? TauntKey, "Pose", 26); } }
        }

        private sealed class PngExportPixels
        {
            internal byte[] Bgra;
            internal int Width, Height, Stride;
        }

        private static void AttachPngExportControls(Window owner)
        {
            if (pngExportWindows.ContainsKey(owner)) return;
            var anchor = PngExportField(owner, "ResultInfo") as UIElement;
            var reference = PngExportField(owner, "PreviewButton") as Button;
            if (anchor == null) return;
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var row = new WrapPanel();
            var controls = new PngExportControls();
            controls.Save = new Button { Content = "Save PNG", Style = reference?.Style, FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Save the currently displayed pose at its original pixel size, including its alpha channel." };
            controls.Open = new Button { Content = "Previews folder", Style = reference?.Style, FontSize = 11,
                Padding = controls.Save.Padding, ToolTip = "Open the Previews folder inside your configured output folder." };
            controls.Status = new TextBlock { Text = "Successful previews also save a single PNG automatically.",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(159, 173, 192)),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 44 };
            row.Children.Add(controls.Save); row.Children.Add(controls.Open); footer.Children.Add(row); footer.Children.Add(controls.Status);
            if (!WrapPngResult(anchor, footer)) return;
            pngExportWindows.Add(owner, controls);
            controls.Save.Click += async (sender, args) => await SaveDisplayedPngAsync(owner, controls);
            controls.Open.Click += (sender, args) => OpenPngPreviewsFolder(owner, controls);
            controls.Timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            controls.Timer.Tick += (sender, args) => UpdatePngButtons(owner, controls);
            controls.Timer.Start();
            owner.Closed += (sender, args) => {
                controls.Closed = true; controls.Cancellation.Cancel(); controls.Timer.Stop();
                pngExportWindows.Remove(owner); controls.Cancellation.Dispose();
            };
            UpdatePngButtons(owner, controls);
        }

        private static bool WrapPngResult(UIElement anchor, UIElement footer)
        {
            var element = anchor as FrameworkElement;
            DependencyObject parent = element?.Parent;
            var wrapper = new DockPanel { LastChildFill = true };
            if (parent is Border)
            {
                var border = (Border)parent;
                if (!ReferenceEquals(border.Child, anchor)) return false;
                border.Child = null; border.Child = wrapper;
            }
            else if (parent is ContentControl)
            {
                var content = (ContentControl)parent;
                if (!ReferenceEquals(content.Content, anchor)) return false;
                content.Content = null; content.Content = wrapper;
            }
            else if (parent is Panel)
            {
                var panel = (Panel)parent;
                int index = panel.Children.IndexOf(anchor);
                if (index < 0) return false;
                Grid.SetRow(wrapper, Grid.GetRow(anchor)); Grid.SetColumn(wrapper, Grid.GetColumn(anchor));
                Grid.SetRowSpan(wrapper, Grid.GetRowSpan(anchor)); Grid.SetColumnSpan(wrapper, Grid.GetColumnSpan(anchor));
                DockPanel.SetDock(wrapper, DockPanel.GetDock(anchor));
                panel.Children.RemoveAt(index); panel.Children.Insert(index, wrapper);
            }
            else return false;
            DockPanel.SetDock(footer, Dock.Bottom); wrapper.Children.Add(footer); wrapper.Children.Add(anchor);
            return true;
        }

        // Called for both a newly completed Preview and an equivalent cache hit.
        // Native Go must first assign its single CroppedBitmap to ResultImage.
        public static void ExportCompletedPreviewPng(object owner, string script, IEnumerable<string> arguments, IEnumerable<string> lines)
        {
            if (arguments == null) return;
            string[] args = arguments.ToArray();
            if (PreviewCache.IsPreviewRequest(script, args)) CaptureCompletedPngResult(owner, script, args, lines);
        }

        // Full MAKE results are retained for the explicit Save PNG action, while
        // successful quick Previews additionally publish a PNG automatically.
        public static void CaptureCompletedPngResult(object owner, string script, IEnumerable<string> arguments, IEnumerable<string> lines)
        {
            var window = owner as Window;
            if (window == null || arguments == null || lines == null) return;
            string[] args = arguments.ToArray();
            if (!AlternateAttires.IsRender(script) || args.Any(value => value != null && value.StartsWith("prepare=", StringComparison.Ordinal))) return;
            bool autoSave = PreviewCache.IsPreviewRequest(script, args);
            PngExportContext context;
            try
            {
                string result = lines.LastOrDefault(line => line != null && line.StartsWith("RESULT ", StringComparison.Ordinal));
                if (result == null || args.Length < 3) return;
                Dictionary<string, object> data = DiscoveryTauntLibrary.JsonObject(result.Substring(7));
                bool completeMake = !string.IsNullOrWhiteSpace(PngResultText(data, "gmr"));
                if (autoSave == completeMake || !data.ContainsKey("frames") || Convert.ToInt32(data["frames"], CultureInfo.InvariantCulture) <= 0) return;
                context = new PngExportContext { Output = args[2], Folder = args[0], TauntKey = args[1],
                    Name = PngResultText(data, "name"), Id = PngResultText(data, "id"),
                    Attire = args.FirstOrDefault(value => value != null && value.StartsWith("attire=", StringComparison.Ordinal)) };
                if (context.Attire != null) context.Attire = context.Attire.Substring(7);
                foreach (string key in new[] { "play", "preview" })
                {
                    string path = PngResultText(data, key);
                    if (!string.IsNullOrWhiteSpace(path)) context.Images.Add(Path.GetFullPath(path));
                }
                if (context.Images.Count == 0 || string.IsNullOrWhiteSpace(context.Output)) return;
            }
            catch (Exception error) when (PngExportError(error)) { return; }
            if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished) return;
            bool accepted = false;
            Action capture = () => {
                if (!window.IsLoaded || !PngContextMatches(window, context)) return;
                context.TauntName = PngTauntName(window, context.TauntKey);
                accepted = true;
            };
            if (window.Dispatcher.CheckAccess()) capture();
            else window.Dispatcher.Invoke(capture);
            if (!accepted) return;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () => {
                if (!window.IsLoaded) return;
                AttachPngExportControls(window);
                PngExportControls controls;
                if (!pngExportWindows.TryGetValue(window, out controls) || controls.Closed) return;
                try
                {
                    if (PngOwnerBusy(window) || !PngContextMatches(window, context)) return;
                    var source = (PngExportField(window, "ResultImage") as Image)?.Source as BitmapSource;
                    string path = PngBitmapPath(source);
                    // An old model, a later preview, or the whole atlas must never
                    // be published under this completed request's name.
                    if (path == null || !context.Images.Contains(path) || !PngSingleFrame(source)) return;
                    context.ImageRoot = PngBitmapRoot(source);
                    controls.LastResult = context;
                    UpdatePngButtons(window, controls);
                    if (autoSave)
                    {
                        PngExportPixels pixels = CopyPngPixels(source);
                        await PublishPngAsync(window, controls, context, pixels);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (PngExportError(error))
                { if (!controls.Closed) controls.Status.Text = "PNG could not be saved: " + error.Message; }
            }));
        }

        private static async Task SaveDisplayedPngAsync(Window owner, PngExportControls controls)
        {
            if (controls.Closed || controls.Saving != 0 || PngOwnerBusy(owner)) return;
            try
            {
                var source = (PngExportField(owner, "ResultImage") as Image)?.Source as BitmapSource;
                if (!PngSingleFrame(source)) throw new InvalidOperationException("Wait for a single preview pose to appear, then save it.");
                PngExportContext context = controls.LastResult;
                string origin = PngBitmapPath(source);
                if (context == null || !ReferenceEquals(context.ImageRoot, PngBitmapRoot(source)) || origin == null || !context.Images.Contains(origin))
                    throw new InvalidOperationException("Complete a Preview or MAKE for this wrestler before saving its PNG.");
                if (!PngContextMatches(owner, context))
                    throw new InvalidOperationException("Complete a Preview or MAKE for the current wrestler, taunt and attire before saving its PNG.");
                // Manual exports follow the current configured output directory.
                var chosen = new PngExportContext { Output = PngExportField(owner, "Output") as string,
                    Name = context.Name, Id = context.Id, Attire = context.Attire, TauntKey = context.TauntKey, TauntName = context.TauntName };
                PngExportPixels pixels = CopyPngPixels(source);
                await PublishPngAsync(owner, controls, chosen, pixels);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (PngExportError(error))
            { if (!controls.Closed) controls.Status.Text = "PNG could not be saved: " + error.Message; }
        }

        private static async Task PublishPngAsync(Window owner, PngExportControls controls, PngExportContext context, PngExportPixels pixels)
        {
            CancellationToken token = controls.Cancellation.Token;
            token.ThrowIfCancellationRequested();
            string directory = PngOutputDirectory(context.Output);
            controls.Saving++; UpdatePngButtons(owner, controls);
            try
            {
                string path = await Task.Run(() => {
                    token.ThrowIfCancellationRequested();
                    return PreviewPngPublisher.Publish(directory, context.Stem, pixels.Bgra, pixels.Width, pixels.Height, pixels.Stride);
                }, token);
                if (!controls.Closed && owner.IsLoaded)
                {
                    controls.Status.Text = "PNG saved · " + pixels.Width + " × " + pixels.Height + " · " + Path.GetFileName(path);
                    controls.Status.ToolTip = path;
                }
            }
            finally { controls.Saving--; if (!controls.Closed) UpdatePngButtons(owner, controls); }
        }

        private static PngExportPixels CopyPngPixels(BitmapSource source)
        {
            if (!PngSingleFrame(source)) throw new InvalidDataException("The displayed image is not a single preview frame.");
            BitmapSource straight = source.Format == PixelFormats.Bgra32 ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(straight.PixelWidth * 4);
            var pixels = new byte[checked(stride * straight.PixelHeight)];
            straight.CopyPixels(pixels, stride, 0);
            return new PngExportPixels { Width = straight.PixelWidth, Height = straight.PixelHeight, Stride = stride, Bgra = pixels };
        }

        private static bool PngSingleFrame(BitmapSource source)
        {
            return source != null && source.PixelWidth > 0 && source.PixelHeight > 0
                && source.PixelWidth <= 8192 && source.PixelHeight <= 8192
                && source.PixelWidth <= source.PixelHeight;
        }

        private static string PngBitmapPath(BitmapSource source)
        {
            var image = PngBitmapRoot(source) as BitmapImage;
            return image != null && image.UriSource != null && image.UriSource.IsAbsoluteUri && image.UriSource.IsFile
                ? Path.GetFullPath(image.UriSource.LocalPath) : null;
        }

        private static BitmapSource PngBitmapRoot(BitmapSource source)
        {
            for (int depth = 0; source != null && depth < 12; depth++)
            {
                var crop = source as CroppedBitmap;
                if (crop != null) { source = crop.Source; continue; }
                var converted = source as FormatConvertedBitmap;
                if (converted != null) { source = converted.Source; continue; }
                var transformed = source as TransformedBitmap;
                if (transformed != null) { source = transformed.Source; continue; }
                return source;
            }
            return null;
        }

        private static bool PngContextMatches(object owner, PngExportContext context)
        {
            string folder = PngExportField(owner, "Folder") as string;
            string id = PngExportField(owner, "TargetSlot") as string;
            var selected = (PngExportField(owner, "TauntList") as ListBox)?.SelectedItem;
            string key = selected?.GetType().GetProperty("Key")?.GetValue(selected, null) as string;
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(context.Folder)
                || !string.Equals(Path.GetFullPath(folder).TrimEnd('\\', '/'), Path.GetFullPath(context.Folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(id, context.Id, StringComparison.Ordinal)
                || !string.Equals(key, context.TauntKey, StringComparison.Ordinal)) return false;
            string attire = SelectedAlternateAttire(owner, folder);
            return string.Equals((attire ?? "").Replace('\\', '/'), (context.Attire ?? "").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string PngTauntName(object owner, string key)
        {
            var items = PngExportField(owner, "AllTaunts") as IEnumerable;
            if (items != null)
                foreach (object item in items)
                    if (string.Equals(item?.GetType().GetProperty("Key")?.GetValue(item, null) as string, key, StringComparison.Ordinal))
                        return item.GetType().GetProperty("Name")?.GetValue(item, null) as string;
            return key;
        }

        private static void OpenPngPreviewsFolder(Window owner, PngExportControls controls)
        {
            try
            {
                string directory = PngOutputDirectory(PngExportField(owner, "Output") as string);
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            }
            catch (Exception error) when (PngExportError(error) || error is System.ComponentModel.Win32Exception)
            { controls.Status.Text = "Previews folder could not be opened: " + error.Message; }
        }

        private static string PngOutputDirectory(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("Choose an output folder first.");
            return Path.Combine(Path.GetFullPath(output), "Previews");
        }

        private static void UpdatePngButtons(Window owner, PngExportControls controls)
        {
            if (controls.Closed) return;
            var source = (PngExportField(owner, "ResultImage") as Image)?.Source as BitmapSource;
            if (controls.LastResult != null && !ReferenceEquals(controls.LastResult.ImageRoot, PngBitmapRoot(source))) controls.LastResult = null;
            controls.Save.IsEnabled = controls.LastResult != null && controls.Saving == 0 && !PngOwnerBusy(owner)
                && PngContextMatches(owner, controls.LastResult)
                && PngSingleFrame(source);
            controls.Open.IsEnabled = !string.IsNullOrWhiteSpace(PngExportField(owner, "Output") as string);
        }

        private static bool PngOwnerBusy(object owner)
        {
            foreach (string name in new[] { "Busy", "GameBusy" })
                if (PngExportField(owner, name) is bool value && value) return true;
            return false;
        }

        private static object PngExportField(object owner, string name)
        { return owner?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(owner); }
        private static string PngLabel(string value, string fallback, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string label = value.Trim();
            if (label.Length <= maximum) return label;
            int length = maximum;
            if (char.IsHighSurrogate(label[length - 1])) length--;
            return label.Substring(0, length);
        }
        private static string PngResultText(Dictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        private static bool PngExportError(Exception error)
        { return error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is ArgumentException
                || error is InvalidOperationException || error is NotSupportedException || error is OverflowException || error is FormatException; }
    }
}
