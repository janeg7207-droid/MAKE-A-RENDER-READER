using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class CompletedRenderVersionGroupRow
        {
            internal CompletedRenderVersionGroup Group;
            internal CompletedRenderEntry Chosen;
            public string Label { get { return Group.Name + " · ID " + Group.WrestlerId.ToString(CultureInfo.InvariantCulture)
                + " (" + Group.Entries.Count.ToString(CultureInfo.InvariantCulture) + " versions)"; } }
        }

        private sealed class CompletedRenderVersionOption
        {
            internal CompletedRenderEntry Entry;
            internal bool Suggested;
            public string Attire { get { return string.IsNullOrWhiteSpace(Entry.AttireName) ? "Not recorded" : Entry.AttireName; } }
            public string Taunt { get { return string.IsNullOrWhiteSpace(Entry.Taunt) ? "Not recorded" : Entry.Taunt; } }
            public string Saved { get { return Entry.LastWriteTimeUtc == default(DateTime) ? "Unknown"
                : Entry.LastWriteTimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture); } }
            public string File { get { return Path.GetFileName(Entry.PublishedPath ?? Entry.Path); } }
            public string Suggestion { get { return Suggested ? "Newest suggested" : ""; } }
            public string Details { get { return "Source: " + Entry.Path + (string.IsNullOrWhiteSpace(Entry.PublishedPath) ? "" : "\nOutput: " + Entry.PublishedPath); } }
        }

        // This dialog only returns explicit choices. It never changes checks, saved
        // movies or installed files; Cancel therefore leaves the caller unchanged.
        private static Dictionary<int, string> PickCompletedRenderVersions(Window owner, IList<CompletedRenderVersionGroup> conflicts)
        {
            if (conflicts == null || conflicts.Count == 0) return new Dictionary<int, string>();
            var groups = conflicts.Select(group => new CompletedRenderVersionGroupRow { Group = group, Chosen = group.Suggested }).ToList();
            var background = new SolidColorBrush(Color.FromRgb(22, 22, 27));
            var surface = new SolidColorBrush(Color.FromRgb(29, 29, 35));
            var line = new SolidColorBrush(Color.FromRgb(65, 65, 76));
            var muted = new SolidColorBrush(Color.FromRgb(175, 187, 202));
            var dialog = new Window { Owner = owner, Title = "Choose one render per wrestler ID", Width = 1100, Height = 650,
                MinWidth = 890, MinHeight = 490, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, Background = background, Foreground = Brushes.White, FontFamily = owner.FontFamily, FontSize = 13 };
            var layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dialog.Content = layout;
            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 13) };
            heading.Children.Add(new TextBlock { Text = "Choose one version for each repeated wrestler ID", FontSize = 17, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "The newest saved version is suggested for each ID. Select a different outfit or taunt below, then Apply choices. Other checked wrestlers stay selected; every saved file is kept.",
                TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 7, 0, 0) });
            layout.Children.Add(heading);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(content, 1); layout.Children.Add(content);
            var groupList = new ListBox { ItemsSource = groups, DisplayMemberPath = "Label", Background = surface,
                Foreground = Brushes.White, BorderBrush = line, Margin = new Thickness(0, 0, 12, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            VirtualizingPanel.SetIsVirtualizing(groupList, true); VirtualizingPanel.SetVirtualizationMode(groupList, VirtualizationMode.Recycling);
            ScrollViewer.SetHorizontalScrollBarVisibility(groupList, ScrollBarVisibility.Auto); content.Children.Add(groupList);
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(right, 1); content.Children.Add(right);
            var groupHeading = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9), FontWeight = FontWeights.SemiBold };
            right.Children.Add(groupHeading);
            var table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                CanUserResizeRows = false, CanUserSortColumns = true, SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
                EnableRowVirtualization = true, EnableColumnVirtualization = true, Background = surface, Foreground = Brushes.White,
                RowBackground = surface, AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(34, 34, 41)), BorderBrush = line,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = line };
            ScrollViewer.SetCanContentScroll(table, true); VirtualizingPanel.SetIsVirtualizing(table, true);
            VirtualizingPanel.SetVirtualizationMode(table, VirtualizationMode.Recycling);
            var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(39, 39, 46))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 6, 7, 6))); table.ColumnHeaderStyle = headerStyle;
            var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Details"))); table.RowStyle = rowStyle;
            AddCompletedRenderColumn(table, "Attire", "Attire", 155); AddCompletedRenderColumn(table, "Taunt", "Taunt", 155);
            AddCompletedRenderColumn(table, "Saved", "Saved", 135); AddCompletedRenderColumn(table, "Suggestion", "Suggestion", 125);
            table.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new Binding("File"), IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 190 });
            Grid.SetRow(table, 1); right.Children.Add(table);
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 9, 0, 0) };
            var detailsScroll = new ScrollViewer { Content = details, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(detailsScroll, 2); right.Children.Add(detailsScroll);
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10), Foreground = muted };
            Grid.SetRow(summary, 2); layout.Children.Add(summary);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var newest = CompletedRenderButton("Use newest versions");
            var apply = CompletedRenderButton("Apply choices"); apply.IsDefault = true;
            var cancel = CompletedRenderButton("Cancel"); cancel.IsCancel = true;
            actions.Children.Add(newest); actions.Children.Add(apply); actions.Children.Add(cancel);
            Grid.SetRow(actions, 3); layout.Children.Add(actions);
            CompletedRenderVersionGroupRow active = null;
            bool binding = false;
            Action refreshSummary = () =>
            {
                int chosen = groups.Count(group => group.Chosen != null);
                summary.Text = chosen.ToString(CultureInfo.InvariantCulture) + " of " + groups.Count.ToString(CultureInfo.InvariantCulture)
                    + " repeated ID(s) have one version selected. These choices take effect only when you click Apply choices.";
                apply.IsEnabled = chosen == groups.Count;
            };
            Action showActive = () =>
            {
                binding = true;
                try
                {
                    active = groupList.SelectedItem as CompletedRenderVersionGroupRow;
                    if (active == null) { table.ItemsSource = null; groupHeading.Text = "Select a wrestler ID."; details.Text = ""; return; }
                    groupHeading.Text = active.Group.Name + " · wrestler ID " + active.Group.WrestlerId.ToString(CultureInfo.InvariantCulture);
                    var options = active.Group.Entries.Select(entry => new CompletedRenderVersionOption { Entry = entry, Suggested = ReferenceEquals(entry, active.Group.Suggested) }).ToList();
                    table.ItemsSource = options;
                    table.SelectedItem = options.FirstOrDefault(option => ReferenceEquals(option.Entry, active.Chosen));
                    details.Text = (table.SelectedItem as CompletedRenderVersionOption)?.Details ?? "Select one version for this ID.";
                }
                finally { binding = false; }
                refreshSummary();
            };
            groupList.SelectionChanged += (sender, args) => showActive();
            table.SelectionChanged += (sender, args) =>
            {
                if (binding || active == null) return;
                var option = table.SelectedItem as CompletedRenderVersionOption;
                active.Chosen = option?.Entry;
                details.Text = option?.Details ?? "Select one version for this ID.";
                refreshSummary();
            };
            newest.Click += (sender, args) => { foreach (CompletedRenderVersionGroupRow group in groups) group.Chosen = group.Group.Suggested; showActive(); refreshSummary(); };
            Dictionary<int, string> result = null;
            apply.Click += (sender, args) =>
            {
                if (groups.Any(group => group.Chosen == null || !group.Group.Entries.Contains(group.Chosen))) return;
                result = groups.ToDictionary(group => group.Group.WrestlerId, group => group.Chosen.Path);
                dialog.DialogResult = true;
            };
            cancel.Click += (sender, args) => dialog.DialogResult = false;
            groupList.SelectedIndex = 0; refreshSummary();
            return dialog.ShowDialog() == true ? result : null;
        }
    }
}
