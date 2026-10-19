using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static bool CanInstallBatchTogether(IEnumerable<BatchRenderJob> jobs)
        {
            List<BatchRenderJob> completed = jobs.ToList();
            return completed.Count > 0 && completed.GroupBy(job => job.Entry.Id).All(group => group.Count() == 1);
        }

        private static List<BatchAttireSelection> ChooseBatchAttires(Window owner, RosterEntry entry,
            List<BatchAttireSelection> available, List<BatchAttireSelection> current)
        {
            var dialog = new Window { Owner = owner, Title = "Attires · " + entry.Name, Width = 640, Height = 530,
                MinWidth = 440, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = owner.FontFamily, FontSize = 13 };
            var grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dialog.Content = grid;
            grid.Children.Add(new TextBlock { Text = "Check the attire models you want to render with the same taunt. Each checked model creates a separate file. Choose the version to install afterward.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var items = new StackPanel();
            var scroll = new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
            var count = new TextBlock { Margin = new Thickness(0, 9, 0, 9), Foreground = Brushes.LightGray };
            Grid.SetRow(count, 2); grid.Children.Add(count);
            var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var all = new Button { Content = "Select all", Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 7, 0) };
            var none = new Button { Content = "Clear", Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 7, 0) };
            var accept = new Button { Content = "Use checked attires", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(8, 5, 8, 5), IsCancel = true };
            controls.Children.Add(all); controls.Children.Add(none); controls.Children.Add(accept); controls.Children.Add(cancel);
            Grid.SetRow(controls, 3); grid.Children.Add(controls);
            var selected = new HashSet<string>((current ?? new List<BatchAttireSelection>()).Select(choice => BatchRenderPlan.SelectionIdentity(choice, entry.Folder)), StringComparer.OrdinalIgnoreCase);
            if (current == null && available.Count > 0)
                selected.Add(BatchRenderPlan.SelectionIdentity(available.FirstOrDefault(attire => attire.IsDefault) ?? available.First(), entry.Folder));
            var checks = new Dictionary<BatchAttireSelection, CheckBox>();
            Action update = () =>
            {
                int total = checks.Values.Count(check => check.IsChecked == true);
                count.Text = total.ToString(CultureInfo.InvariantCulture) + " checked · " + total.ToString(CultureInfo.InvariantCulture) + " separate output file(s)";
                accept.IsEnabled = total > 0;
            };
            foreach (BatchAttireSelection attire in available)
            {
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = attire.DisplayName + (attire.IsDefault ? " (default)" : ""), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
                label.Children.Add(new TextBlock { Text = (attire.SourceFolder ?? entry.Folder) + "\n" + attire.RelativePath,
                    Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                var check = new CheckBox { Content = label, IsChecked = selected.Contains(BatchRenderPlan.SelectionIdentity(attire, entry.Folder)), Margin = new Thickness(4, 7, 4, 7) };
                check.Checked += (sender, args) => update(); check.Unchecked += (sender, args) => update();
                checks.Add(attire, check); items.Children.Add(check);
            }
            all.Click += (sender, args) => { foreach (CheckBox check in checks.Values) check.IsChecked = true; };
            none.Click += (sender, args) => { foreach (CheckBox check in checks.Values) check.IsChecked = false; };
            accept.Click += (sender, args) => { if (checks.Values.Any(check => check.IsChecked == true)) dialog.DialogResult = true; };
            update();
            return dialog.ShowDialog() == true ? available.Where(attire => checks[attire].IsChecked == true)
                .Select(attire => new BatchAttireSelection { SourceFolder = attire.SourceFolder, SourceId = attire.SourceId,
                    DiscoveryKey = attire.DiscoveryKey, DiscoveryModelHash = attire.DiscoveryModelHash, IsDefault = attire.IsDefault,
                    RelativePath = attire.RelativePath, DisplayName = attire.DisplayName }).ToList() : null;
        }
    }
}
