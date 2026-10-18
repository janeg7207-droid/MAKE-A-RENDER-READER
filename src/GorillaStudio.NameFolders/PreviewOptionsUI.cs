using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static bool performanceSettingsLoaded;
        public static bool FastPreviewEnabled { get; private set; } = true;
        public static bool FastRenderEnabled { get; private set; } = true;
        public static bool BackgroundMakeEnabled { get; private set; } = false;
        private static readonly Dictionary<object, PerformanceControls> performanceWindows = new Dictionary<object, PerformanceControls>();

        private sealed class PerformanceControls
        {
            public TextBlock Timing;
            public Window Dialog;
            public StackPanel Options;
            public Button Compare;
            public bool Busy;
            public string PreviewTime;
            public string RenderTime;
        }

        internal static void SetPerformanceModes(bool preview, bool render)
        {
            FastPreviewEnabled = preview;
            FastRenderEnabled = render;
        }

        private static void LoadPerformanceSettings()
        {
            if (performanceSettingsLoaded) return;
            performanceSettingsLoaded = true;
            try
            {
                string path = Path.Combine(StorageDirectory, "preview-settings.json");
                if (!File.Exists(path)) return;
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, bool>>(File.ReadAllText(path));
                if (settings == null) return;
                bool value;
                if (settings.TryGetValue("fastPreview", out value)) FastPreviewEnabled = value;
                if (settings.TryGetValue("fastRender", out value)) FastRenderEnabled = value;
                if (settings.TryGetValue("backgroundMake", out value)) BackgroundMakeEnabled = value;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException) { }
        }

        private static void SavePerformanceSettings()
        {
            string temporary = null;
            try
            {
                Directory.CreateDirectory(StorageDirectory);
                string path = Path.Combine(StorageDirectory, "preview-settings.json");
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new Dictionary<string, bool>
                {
                    { "fastPreview", FastPreviewEnabled }, { "fastRender", FastRenderEnabled },
                    { "backgroundMake", BackgroundMakeEnabled }
                }), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            finally { try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private static void AttachPerformanceControls(Window owner)
        {
            if (performanceWindows.ContainsKey(owner)) return;
            LoadPerformanceSettings();
            var field = owner.GetType().GetField("PreviewButton", InstanceMembers);
            var preview = field == null ? null : field.GetValue(owner) as Button;
            var parent = preview == null ? null : preview.Parent as Panel;
            if (parent == null) return;
            int index = parent.Children.IndexOf(preview);
            var row = new Grid { Margin = preview.Margin };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            Grid.SetRow(row, Grid.GetRow(preview));
            Grid.SetColumn(row, Grid.GetColumn(preview));
            Grid.SetColumnSpan(row, Grid.GetColumnSpan(preview));
            parent.Children.RemoveAt(index);
            preview.Margin = new Thickness(0);
            row.Children.Add(preview);
            var speed = new Button
            {
                Content = "Speed…", Style = preview.Style, FontSize = preview.FontSize,
                Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(5, 3, 5, 3),
                ToolTip = "Fast preview and render settings; compare original and faster rendering times."
            };
            Grid.SetColumn(speed, 1);
            row.Children.Add(speed);
            parent.Children.Insert(index, row);
            var controls = new PerformanceControls();
            // The supported release places these buttons in a scrolling stack panel.
            if (parent is StackPanel)
            {
                controls.Timing = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(159, 173, 192)), FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 3), TextWrapping = TextWrapping.Wrap,
                    Text = "Fast preview + fast MAKE enabled", MaxHeight = 30
                };
                parent.Children.Insert(index + 1, controls.Timing);
            }
            performanceWindows.Add(owner, controls);
            speed.Click += (sender, args) => ShowPerformanceDialog(owner, controls);
            owner.Closed += (sender, args) =>
            {
                if (controls.Dialog != null) controls.Dialog.Close();
                performanceWindows.Remove(owner);
            };
        }

        private static void ShowPerformanceDialog(Window owner, PerformanceControls controls)
        {
            if (controls.Dialog != null) { controls.Dialog.Activate(); return; }
            var dialog = new Window
            {
                Owner = owner, Title = "Preview and render speed", Width = 475, Height = 480,
                MinWidth = 420, MinHeight = 430, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)),
                Foreground = Brushes.White, FontFamily = owner.FontFamily, FontSize = 13,
                ResizeMode = ResizeMode.CanResize
            };
            controls.Dialog = dialog;
            var panel = new StackPanel { Margin = new Thickness(20) };
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var options = new StackPanel();
            controls.Options = options;
            var preview = new CheckBox { Content = "Fast Preview", IsChecked = FastPreviewEnabled, Margin = new Thickness(0, 0, 0, 5), Foreground = Brushes.White };
            var render = new CheckBox { Content = "Fast MAKE GORILLA RENDER", IsChecked = FastRenderEnabled, Margin = new Thickness(0, 9, 0, 5), Foreground = Brushes.White };
            var background = new CheckBox { Content = "Start MAKE automatically after Preview", IsChecked = BackgroundMakeEnabled, Margin = new Thickness(0, 9, 0, 5), Foreground = Brushes.White };
            options.Children.Add(preview);
            options.Children.Add(Note("Smaller preview image, same motion. Unchanged previews reopen from cache."));
            options.Children.Add(render);
            options.Children.Add(Note("Full output resolution, fewer lighting samples. Untick for the original shadow quality."));
            options.Children.Add(background);
            options.Children.Add(Note("Off by default so browsing previews leaves your GPU free."));
            panel.Children.Add(options);
            RoutedEventHandler changed = (sender, args) =>
            {
                StopAutomaticMake(owner);
                SetPerformanceModes(preview.IsChecked == true, render.IsChecked == true);
                BackgroundMakeEnabled = background.IsChecked == true;
                SavePerformanceSettings();
            };
            preview.Click += changed;
            render.Click += changed;
            background.Click += changed;
            panel.Children.Add(new Separator { Margin = new Thickness(0, 13, 0, 10) });
            panel.Children.Add(Note("Compare original and faster modes on your selected wrestler and taunt. One warm-up runs first. No files are installed into the game."));
            var includeMake = new CheckBox { Content = "Also compare full MAKE (takes longer)", IsChecked = true, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 8) };
            panel.Children.Add(includeMake);
            var compare = new Button { Content = "Compare old / new times", Margin = new Thickness(0, 0, 0, 9), Padding = new Thickness(9, 6, 9, 6) };
            controls.Compare = compare;
            panel.Children.Add(compare);
            var report = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 12 };
            panel.Children.Add(report);
            compare.Click += async (sender, args) =>
            {
                compare.IsEnabled = false;
                includeMake.IsEnabled = false;
                try
                {
                    report.Text = await CompareRenderSpeeds(owner, includeMake.IsChecked == true, message => report.Text = message);
                }
                catch (Exception error)
                {
                    var invocation = error as TargetInvocationException;
                    report.Text = "Comparison stopped: " + (invocation != null && invocation.InnerException != null ? invocation.InnerException.Message : error.Message);
                }
                finally { compare.IsEnabled = !controls.Busy; includeMake.IsEnabled = true; }
            };
            dialog.Closing += (sender, args) =>
            {
                if (controls.Busy) { args.Cancel = true; report.Text = "Wait for the running render to finish before closing this window."; }
            };
            dialog.Closed += (sender, args) => { controls.Dialog = null; controls.Options = null; controls.Compare = null; };
            options.IsEnabled = !controls.Busy;
            compare.IsEnabled = !controls.Busy;
            dialog.Show();
        }

        private static TextBlock Note(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(172, 185, 201)) };
        }

        private static void StopAutomaticMake(object owner)
        {
            var stop = owner.GetType().GetMethod("StopBackgroundMake", InstanceMembers, null, Type.EmptyTypes, null);
            if (stop != null) stop.Invoke(owner, null);
        }

        internal static void SetPerformanceBusy(object owner, bool busy)
        {
            var window = owner as Window;
            if (window == null) return;
            if (!window.Dispatcher.CheckAccess()) { window.Dispatcher.Invoke(new Action(() => SetPerformanceBusy(owner, busy))); return; }
            PerformanceControls controls;
            if (!performanceWindows.TryGetValue(owner, out controls)) return;
            controls.Busy = busy;
            if (controls.Options != null) controls.Options.IsEnabled = !busy;
            if (controls.Compare != null) controls.Compare.IsEnabled = !busy;
        }

        internal static void ReportRenderTiming(object owner, bool preview, bool fast, bool cached, double seconds)
        {
            var window = owner as Window;
            if (window == null) return;
            if (!window.Dispatcher.CheckAccess()) { window.Dispatcher.Invoke(new Action(() => ReportRenderTiming(owner, preview, fast, cached, seconds))); return; }
            PerformanceControls controls;
            if (!performanceWindows.TryGetValue(owner, out controls)) return;
            string text = (preview ? cached ? "Preview cache: " : "Preview render: " : "MAKE render: ") + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" + (cached ? "" : fast ? " (fast)" : " (original)");
            if (preview) controls.PreviewTime = text; else controls.RenderTime = text;
            if (controls.Timing != null)
            {
                controls.Timing.Text = string.Join(" · ", new[] { controls.PreviewTime, controls.RenderTime }).Trim(' ', '·');
                controls.Timing.ToolTip = "Measured render request time; initial model preparation is separate. Use Speed… to compare both modes.";
            }
        }
    }
}
