using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        // Ordinary Preview and MAKE requests always use the efficient renderer.
        // Older preview-settings.json flags deliberately do not change these defaults.
        public static bool FastPreviewEnabled { get; private set; } = true;
        public static bool FastRenderEnabled { get; private set; } = true;
        public static bool RosterSpeedEnabled { get; private set; } = true;
        public static bool BackgroundMakeEnabled { get; private set; }
        private static readonly Dictionary<object, PerformanceControls> performanceWindows = new Dictionary<object, PerformanceControls>();

        private sealed class PerformanceControls
        {
            public TextBlock Timing;
            public string PreviewTime;
            public string RenderTime;
        }

        // Used only by the programmatic validation comparison, never by a UI option.
        internal static void SetPerformanceModes(bool preview, bool render)
        {
            FastPreviewEnabled = preview;
            FastRenderEnabled = render;
        }

        private static void AttachPerformanceControls(Window owner)
        {
            if (performanceWindows.ContainsKey(owner)) return;
            var preview = owner.GetType().GetField("PreviewButton", InstanceMembers)?.GetValue(owner) as Button;
            var parent = preview?.Parent as Panel;
            if (parent == null) return;
            var controls = new PerformanceControls();
            if (parent is StackPanel)
            {
                controls.Timing = new TextBlock {
                    Foreground = new SolidColorBrush(Color.FromRgb(159, 173, 192)), FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 3), TextWrapping = TextWrapping.Wrap,
                    Text = "Fast rendering", MaxHeight = 42,
                    ToolTip = "Fast rendering is automatic. Full MAKE keeps 1024 × 1024, every frame and animation timing, with 8 lighting samples. Shadows may be noisier. Timings exclude initial model preparation."
                };
                parent.Children.Insert(parent.Children.IndexOf(preview) + 1, controls.Timing);
            }
            performanceWindows.Add(owner, controls);
            owner.Closed += (sender, args) => performanceWindows.Remove(owner);
        }

        private static void StopAutomaticMake(object owner)
        {
            owner.GetType().GetMethod("StopBackgroundMake", InstanceMembers, null, Type.EmptyTypes, null)?.Invoke(owner, null);
        }

        internal static void SetPerformanceBusy(object owner, bool busy)
        {
            // Native Busy/BodyGrid and the roster/batch workflows own interaction.
            // There are no mutable speed controls to freeze.
        }

        internal static void ReportRenderTiming(object owner, bool preview, bool fast, bool cached, double seconds)
        {
            var window = owner as Window;
            if (window == null) return;
            if (!window.Dispatcher.CheckAccess()) {
                window.Dispatcher.Invoke(new Action(() => ReportRenderTiming(owner, preview, fast, cached, seconds)));
                return;
            }
            PerformanceControls controls;
            if (!performanceWindows.TryGetValue(owner, out controls)) return;
            string text = (preview ? cached ? "Preview cache: " : "Preview render: " : "MAKE render: ")
                + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" + (cached ? "" : fast ? " (fast)" : " (original)");
            if (preview) controls.PreviewTime = text; else controls.RenderTime = text;
            if (controls.Timing != null)
                controls.Timing.Text = string.Join(" · ", new[] { controls.PreviewTime, controls.RenderTime }).Trim(' ', '·');
        }
    }
}
