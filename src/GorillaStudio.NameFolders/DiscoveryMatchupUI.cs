using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private static readonly Dictionary<Window, DiscoveryMatchupSetupReport> matchupSetupReports =
            new Dictionary<Window, DiscoveryMatchupSetupReport>();

        private static async Task<DiscoveryMatchupSetupReport> ReadDiscoveryMatchupsAsync(Window window, string root, bool checkOnly)
        {
            string game = window.GetType().GetField("Game", InstanceMembers)?.GetValue(window) as string;
            await LayoutSetupGate.WaitAsync();
            try
            {
                bool active = true;
                DiscoveryMatchupSetupReport report;
                try
                {
                    report = await DiscoveryMatchupSetup.RunAsync(AppDomain.CurrentDomain.BaseDirectory,
                        game, root, Path.Combine(StorageDirectory, "picture-archives"), checkOnly,
                        message => {
                            if (!window.Dispatcher.HasShutdownStarted && !window.Dispatcher.HasShutdownFinished)
                                window.Dispatcher.BeginInvoke(new Action(() => {
                                    if (active && window.IsLoaded) SetModelStatus(window, message);
                                }));
                        }, CancellationToken.None);
                }
                finally { active = false; }
                if (window.IsLoaded) matchupSetupReports[window] = report;
                return report;
            }
            finally { LayoutSetupGate.Release(); }
        }

        private static bool DiscoveryMatchupsNeedAttention(Window window)
        {
            DiscoveryMatchupSetupReport report;
            return matchupSetupReports.TryGetValue(window, out report)
                && (report.Issues.Count + report.Missing.Count + report.Layouts.Failed.Count + report.Layouts.Unsupported.Count > 0);
        }

        private static string DiscoveryMatchupDetails(DiscoveryMatchupSetupReport report)
        {
            var lines = new List<string> { report.Summary };
            lines.AddRange(report.Issues);
            lines.AddRange(report.Warnings.Take(12));
            if (report.Missing.Count > 0)
            {
                lines.Add("Referenced screens not located:");
                lines.AddRange(report.Missing.Take(20));
                if (report.Missing.Count > 20) lines.Add("See Matchup cards for the remaining references.");
            }
            if (!string.IsNullOrWhiteSpace(report.ReportPath)) lines.Add("Report: " + report.ReportPath);
            return string.Join("\n", lines);
        }

        private sealed class MatchupCardRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string State { get; set; }
            public string Screen { get; set; }
            public string Search { get; set; }
        }

        private static void ShowDiscoveryMatchupCards(Window owner)
        {
            DiscoveryMatchupSetupReport report;
            if (!matchupSetupReports.TryGetValue(owner, out report))
            { SetModelStatus(owner, "Run Check match-card setup first to read the current Discovery matchup catalog."); return; }
            var rows = report.Catalog.Entries.Select(entry => {
                var hashes = entry.AssetHashes.ToList();
                var assets = hashes.Where(hash => report.Resolution.Assets.ContainsKey(hash)).Select(hash => report.Resolution.Assets[hash]).ToList();
                string state = hashes.Count == 0 ? "No matchup file linked in data"
                    : assets.Count != hashes.Count ? "Screen not located · see setup check"
                    : assets.Any(asset => !report.PreparedPaths.ContainsKey(asset.Hash)) ? "Project found · run game setup"
                    : "Screen located · check layout and bake";
                string screen = string.Join("; ", assets.Select(asset => asset.AssetPath).Concat(entry.AssetPaths).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase));
                if (screen.Length == 0) screen = string.Join("; ", entry.TemplateNames);
                return new MatchupCardRow { Id = entry.Id, Name = entry.Name, State = state, Screen = screen,
                    Search = entry.Id.ToString(CultureInfo.InvariantCulture) + " " + entry.Name + " " + string.Join(" ", entry.Names) + " " + string.Join(" ", entry.TemplateNames) };
            }).OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id).ToList();
            var dialog = new Window { Owner = owner, Title = "Discovery matchup cards", Width = 940, Height = 610,
                MinWidth = 620, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 27)), Foreground = Brushes.White,
                FontFamily = owner.FontFamily, FontSize = 13 };
            var body = new DockPanel { Margin = new Thickness(16) };
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            header.Children.Add(new TextBlock { Text = report.Summary, TextWrapping = TextWrapping.Wrap });
            header.Children.Add(new TextBlock { Text = "Names and links come from the current Discovery data. A located file does not confirm in-game playback.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.LightGray, Margin = new Thickness(0, 5, 0, 8) });
            var search = new TextBox { ToolTip = "Search an arena/show name, matchup template or ID", Padding = new Thickness(5) };
            header.Children.Add(search); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(39, 39, 46))));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5)));
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
                Background = new SolidColorBrush(Color.FromRgb(29, 29, 35)), Foreground = Brushes.White,
                RowBackground = new SolidColorBrush(Color.FromRgb(29, 29, 35)),
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(35, 35, 42)), ColumnHeaderStyle = headerStyle,
                HeadersVisibility = DataGridHeadersVisibility.Column, ItemsSource = rows };
            grid.Columns.Add(new DataGridTextColumn { Header = "ID", Binding = new Binding("Id"), Width = 60 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Arena / show", Binding = new Binding("Name"), Width = new DataGridLength(1.1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Matchup status", Binding = new Binding("State"), Width = 245 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Screen file / template", Binding = new Binding("Screen"), Width = new DataGridLength(1.6, DataGridLengthUnitType.Star) });
            search.TextChanged += (sender, args) => grid.ItemsSource = rows.Where(row => row.Search.IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            body.Children.Add(grid); dialog.Content = body; dialog.Show();
        }
    }
}
