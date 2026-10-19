using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class AttireControls
        {
            internal ComboBox Choice;
            internal Button Refresh;
            internal TextBlock Note;
            internal string Folder;
            internal string Selected;
            internal bool Updating;
            internal bool Closed;
            internal readonly AlternateAttireCatalog Catalog = new AlternateAttireCatalog();
            internal AlternateAttire MissingChoice;
            internal DispatcherTimer Timer;
            internal int Generation;
        }

        private static readonly Dictionary<object, AttireControls> attireWindows = new Dictionary<object, AttireControls>();

        private static void AttachAlternateAttireControls(Window window)
        {
            if (attireWindows.ContainsKey(window)) return;
            var gear = AttireField(window, "GearBox") as ComboBox;
            var parent = gear == null ? null : gear.Parent as StackPanel;
            if (parent == null) return;
            var controls = new AttireControls();
            var panel = new StackPanel { Margin = new Thickness(0, 3, 0, 4) };
            panel.Children.Add(new TextBlock { Text = "Attire model", FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(159, 173, 192)), Margin = new Thickness(0, 0, 0, 3) });
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.Choice = new ComboBox { FontSize = gear.FontSize, MinWidth = 80, Style = gear.Style,
                ToolTip = "Choose an extracted attire belonging to this wrestler. Gear above selects entrance or ring pieces within that model." };
            controls.Refresh = new Button { Content = "↻", Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0),
                ToolTip = "Read this wrestler's attire folders again, including newly added models." };
            Grid.SetColumn(controls.Refresh, 1);
            row.Children.Add(controls.Choice); row.Children.Add(controls.Refresh); panel.Children.Add(row);
            controls.Note = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(159, 173, 192)), Margin = new Thickness(0, 2, 0, 0) };
            panel.Children.Add(controls.Note);
            parent.Children.Insert(parent.Children.IndexOf(gear) + 1, panel);
            attireWindows.Add(window, controls);
            controls.Choice.SelectionChanged += (sender, args) =>
            {
                if (controls.Updating) return;
                if (AttireBusy(window)) { FillAttires(window, controls, false); return; }
                var selected = controls.Choice.SelectedItem as AlternateAttire;
                string relative = selected == null ? null : selected.RelativePath;
                try { SetAlternateAttireSelection(window, controls.Folder, relative); }
                catch (Exception error) when (AttireError(error))
                {
                    FillAttires(window, controls, false);
                    controls.Note.Text = error.Message;
                }
            };
            controls.Refresh.Click += (sender, args) => { if (!AttireBusy(window)) FillAttires(window, controls, true); };
            controls.Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            controls.Timer.Tick += (sender, args) =>
            {
                string folder = AttireField(window, "Folder") as string;
                bool busy = AttireBusy(window);
                controls.Choice.IsEnabled = !busy && !controls.Catalog.IsLoading && controls.Choice.Items.Count > 1;
                controls.Refresh.IsEnabled = !busy && !controls.Catalog.IsLoading && !string.IsNullOrWhiteSpace(folder);
                if (!SameAttireFolder(folder, controls.Folder)) FillAttires(window, controls, true);
            };
            controls.Timer.Start();
            window.Closed += (sender, args) => {
                controls.Closed = true; controls.Catalog.Close(); controls.Timer.Stop(); attireWindows.Remove(window);
            };
            FillAttires(window, controls, true);
        }

        internal static void RefreshAlternateAttireSelection(object owner)
        {
            AttireControls controls;
            if (attireWindows.TryGetValue(owner, out controls)) FillAttires(owner, controls, true);
        }

        internal static string SelectedAlternateAttire(object owner, string folder)
        {
            AttireControls controls;
            return owner != null && attireWindows.TryGetValue(owner, out controls) && SameAttireFolder(controls.Folder, folder)
                ? controls.Selected : null;
        }

        internal static void SetAlternateAttireSelection(object owner, string folder, string relative)
        {
            AttireControls controls;
            if (owner == null || !attireWindows.TryGetValue(owner, out controls)) return;
            string current = AttireField(owner, "Folder") as string;
            if (!SameAttireFolder(current, folder)) throw new InvalidOperationException("Load this wrestler before choosing its attire.");
            string validated = AlternateAttires.Validate(folder, relative);
            bool sameFolder = SameAttireFolder(controls.Folder, folder);
            // SetFolder already prepares the new wrestler's automatic default.
            // Resetting a batch row to that default must not launch a second import.
            bool changed = sameFolder ? !string.Equals(controls.Selected, validated, StringComparison.Ordinal) : validated != null;
            if (!sameFolder)
            {
                controls.Generation++;
                ResetAttireChoices(controls);
            }
            controls.Folder = folder; controls.Selected = validated;
            FillAttires(owner, controls, !sameFolder);
            if (!changed) return;
            StopAutomaticMake(owner);
            SetAttireField(owner, "LastGmr", null);
            var install = AttireField(owner, "InstallButton") as Button;
            if (install != null) install.IsEnabled = false;
            var resultTimer = AttireField(owner, "ResultTimer") as DispatcherTimer;
            if (resultTimer != null) resultTimer.Stop();
            var resultFrames = AttireField(owner, "ResultFrames") as IList;
            if (resultFrames != null) resultFrames.Clear();
            var resultImage = AttireField(owner, "ResultImage") as Image;
            if (resultImage != null) resultImage.Source = null;
            var resultInfo = AttireField(owner, "ResultInfo") as TextBlock;
            if (resultInfo != null) resultInfo.Text = "Attire changed. Press Preview to check this model, then MAKE to install it.";
            int generation = ++controls.Generation;
            _ = ReprepareAlternateAttire(owner, controls, folder, validated, generation);
        }

        private static async Task ReprepareAlternateAttire(object owner, AttireControls controls, string folder, string relative, int generation)
        {
            try
            {
                await AlternateAttires.QueuePreparation(
                    () => AttireField(owner, "PrepTask") as Task,
                    task => SetAttireField(owner, "PrepTask", task),
                    () => controls.Generation == generation && attireWindows.ContainsKey(owner)
                        && SameAttireFolder(folder, AttireField(owner, "Folder") as string)
                        && controls.Selected == relative,
                    () =>
                    {
                        SetAttireField(owner, "PrepWanted", null); SetAttireField(owner, "PrepDone", null);
                        object taunt = (AttireField(owner, "TauntList") as ListBox)?.SelectedItem;
                        string key = taunt == null ? null : taunt.GetType().GetProperty("Key", InstanceMembers)?.GetValue(taunt, null) as string;
                        if (key == null && taunt != null) key = taunt.GetType().GetField("Key", InstanceMembers)?.GetValue(taunt) as string;
                        owner.GetType().GetMethod("Prepare", InstanceMembers, null, new[] { typeof(string) }, null)?.Invoke(owner, new object[] { key });
                    });
            }
            catch (Exception error) when (AttireError(error))
            {
                if (!controls.Closed && controls.Generation == generation) controls.Note.Text = "Attire preparation stopped: " + error.Message;
            }
        }

        private static void FillAttires(object owner, AttireControls controls, bool scan)
        {
            if (controls.Closed) return;
            string folder = AttireField(owner, "Folder") as string;
            bool changedFolder = !SameAttireFolder(folder, controls.Folder);
            if (changedFolder) { controls.Folder = folder; controls.Selected = null; controls.Generation++; }
            if (!scan && !changedFolder)
            {
                SelectExistingAttire(controls);
                return;
            }
            if (changedFolder)
            {
                ResetAttireChoices(controls);
            }
            controls.Choice.IsEnabled = false;
            controls.Refresh.IsEnabled = false;
            controls.Note.Text = string.IsNullOrWhiteSpace(folder) ? "Load a wrestler to read its attires." : "Reading attire models…";
            _ = ReadAttiresAsync(owner, controls, folder, scan);
        }

        private static void ResetAttireChoices(AttireControls controls)
        {
            controls.Updating = true;
            try
            {
                controls.Choice.Items.Clear(); controls.Choice.Items.Add("Default (automatic)");
                controls.Choice.SelectedIndex = 0; controls.MissingChoice = null;
            }
            finally { controls.Updating = false; }
        }

        private static void SelectExistingAttire(AttireControls controls)
        {
            controls.Updating = true;
            try
            {
                if (controls.Choice.Items.Count == 0) controls.Choice.Items.Add("Default (automatic)");
                if (controls.MissingChoice != null && controls.MissingChoice.RelativePath != controls.Selected)
                {
                    controls.Choice.Items.Remove(controls.MissingChoice);
                    controls.MissingChoice = null;
                }
                object selected = controls.Choice.Items.Cast<object>().OfType<AlternateAttire>()
                    .FirstOrDefault(item => item.RelativePath == controls.Selected);
                if (selected == null && controls.Selected != null)
                {
                    controls.MissingChoice = new AlternateAttire { RelativePath = controls.Selected, DisplayName = "Selected: " + controls.Selected };
                    controls.Choice.Items.Add(controls.MissingChoice); selected = controls.MissingChoice;
                }
                controls.Choice.SelectedItem = selected ?? controls.Choice.Items[0];
            }
            finally { controls.Updating = false; }
        }

        private static async Task ReadAttiresAsync(object owner, AttireControls controls, string folder, bool refresh)
        {
            AlternateAttireCatalogResult result = await controls.Catalog.ReadAsync(folder, refresh);
            if (controls.Closed || !controls.Catalog.IsCurrent(result)
                || !SameAttireFolder(folder, AttireField(owner, "Folder") as string)) return;
            if (result.Error != null)
            {
                controls.Note.Text = result.Error.Message;
                controls.Choice.IsEnabled = !AttireBusy(owner) && controls.Choice.Items.Count > 1;
                controls.Refresh.IsEnabled = !AttireBusy(owner) && !string.IsNullOrWhiteSpace(folder);
                return;
            }
            controls.Updating = true;
            try
            {
                controls.Choice.Items.Clear(); controls.Choice.Items.Add("Default (automatic)");
                controls.Choice.SelectedIndex = 0;
                controls.Choice.IsEnabled = false;
                controls.MissingChoice = null;
                IReadOnlyList<AlternateAttire> models = result.Models;
                foreach (AlternateAttire model in models) controls.Choice.Items.Add(model);
                AlternateAttire selected = models.FirstOrDefault(item => item.RelativePath == controls.Selected);
                controls.Choice.SelectedItem = selected == null ? controls.Choice.Items[0] : (object)selected;
                // Keep a removed selection until the user chooses a replacement;
                // rendering must fail clearly instead of substituting clothes.
                if (controls.Selected != null && selected == null)
                {
                    controls.MissingChoice = new AlternateAttire { RelativePath = controls.Selected, DisplayName = "Missing: " + controls.Selected };
                    controls.Choice.Items.Add(controls.MissingChoice); controls.Choice.SelectedItem = controls.MissingChoice;
                    controls.Note.Text = "Selected attire was removed. Choose another model.";
                }
                else controls.Note.Text = string.IsNullOrWhiteSpace(folder) ? "Load a wrestler to read its attires."
                    : models.Count == 0 ? "No extracted attire models in this character folder."
                    : models.Count + (models.Count == 1 ? " model available" : " models available") + " · Refresh after adding one";
                controls.Choice.IsEnabled = !AttireBusy(owner) && models.Count > 0;
                controls.Refresh.IsEnabled = !AttireBusy(owner) && !string.IsNullOrWhiteSpace(folder);
            }
            catch (Exception error) when (AttireError(error)) { controls.Note.Text = error.Message; }
            finally { controls.Updating = false; }
        }

        static partial void SelectAttireArguments(object owner, string script, ref string[] arguments)
        {
            if (!AlternateAttires.IsRender(script) || arguments == null || arguments.Length < 3) return;
            arguments = AlternateAttires.Apply(script, arguments, SelectedAlternateAttire(owner, arguments[0]));
        }

        static partial void ConfigureAlternateAttireProcess(object owner, Process process, string script, IEnumerable<string> args)
        {
            if (!AlternateAttires.IsRender(script) || args == null) return;
            string[] arguments = args.ToArray();
            if (arguments.Length < 3) return;
            // Foreground Preview/MAKE already captured an explicit choice before
            // computing its cache key. Preserve that snapshot verbatim.
            if (arguments.Skip(3).Any(value => value != null && value.StartsWith("attire=", StringComparison.Ordinal))) return;
            string selected = SelectedAlternateAttire(owner, arguments[0]);
            selected = AlternateAttires.Validate(arguments[0], selected);
            if (selected != null) process.StartInfo.Arguments += " " + AlternateAttires.QuoteArgument("attire=" + selected);
        }

        private static bool SameAttireFolder(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static object AttireField(object owner, string name)
        {
            return owner.GetType().GetField(name, InstanceMembers)?.GetValue(owner);
        }

        private static void SetAttireField(object owner, string name, object value)
        {
            owner.GetType().GetField(name, InstanceMembers)?.SetValue(owner, value);
        }

        private static bool AttireBusy(object owner)
        {
            return (AttireField(owner, "Busy") is bool && (bool)AttireField(owner, "Busy"))
                || (AttireField(owner, "GameBusy") is bool && (bool)AttireField(owner, "GameBusy"));
        }
        private static bool AttireError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is TargetInvocationException;
        }
    }
}
