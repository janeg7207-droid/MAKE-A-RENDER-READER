using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    internal sealed class ModelChoiceDialog : Form
    {
        private readonly TextBox search = new TextBox();
        private readonly ListView models = new ListView();
        private readonly Label status = new Label();
        private readonly Button load = new Button();
        private readonly List<ModelCandidate> candidates;
        private readonly RosterEntry selected;
        private readonly string initialPath;
        private string selectedPath;
        private List<ModelCandidate> selectedModels;
        private readonly HashSet<string> checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool refreshing;
        private readonly bool matchingOnly;

        internal static string Choose(IWin32Window owner, RosterEntry selected,
            IEnumerable<ModelCandidate> candidates, string initialPath, bool matchingOnly = false)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            using (var dialog = new ModelChoiceDialog(selected, candidates, initialPath, matchingOnly))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.selectedPath : null;
        }

        internal static List<ModelCandidate> ChooseMany(IWin32Window owner, RosterEntry selected,
            IEnumerable<ModelCandidate> candidates, string initialPath, IEnumerable<string> remembered = null, bool matchingOnly = false)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            using (var dialog = new ModelChoiceDialog(selected, candidates, initialPath, matchingOnly, remembered))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.selectedModels : null;
        }

        private ModelChoiceDialog(RosterEntry selected, IEnumerable<ModelCandidate> candidates, string initialPath, bool matchingOnly,
            IEnumerable<string> remembered = null)
        {
            this.selected = selected;
            this.initialPath = initialPath;
            this.matchingOnly = matchingOnly;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.candidates = (candidates ?? Enumerable.Empty<ModelCandidate>())
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Folder)
                    && seen.Add(candidate.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string path in remembered ?? Enumerable.Empty<string>())
                if (this.candidates.Any(item => string.Equals(item.Folder, path, StringComparison.OrdinalIgnoreCase))) checkedFolders.Add(path);
            if (checkedFolders.Count == 0 && this.candidates.Any(item => string.Equals(item.Folder, initialPath, StringComparison.OrdinalIgnoreCase)))
                checkedFolders.Add(initialPath);

            Text = "Choose model for " + selected.Name;
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(740, 440);
            Size = new Size(980, 560);
            MinimizeBox = false;
            ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < layout.RowCount; row++)
                layout.RowStyles.Add(new RowStyle(row == 2 ? SizeType.Percent : SizeType.AutoSize, row == 2 ? 100 : 0));
            Controls.Add(layout);
            layout.Controls.Add(new Label
            {
                Text = "Check one or more models for " + selected.Name + ". Each selected pack becomes an available outfit; the roster batch can use the same taunt for all of them.",
                AutoSize = true, MaximumSize = new Size(900, 0),
                Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 14)
            }, 0, 0);

            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 10) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.Controls.Add(new Label { Text = "Search models", AutoSize = true, Margin = new Padding(0, 6, 12, 0) }, 0, 0);
            search.Dock = DockStyle.Fill;
            search.Margin = new Padding(0, 3, 10, 3);
            search.TextChanged += (sender, args) => RefreshModels();
            searchRow.Controls.Add(search, 1, 0);
            var showAll = new Button { Text = "Show all", AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0) };
            showAll.Click += (sender, args) => { search.Clear(); search.Focus(); };
            searchRow.Controls.Add(showAll, 2, 0);
            layout.Controls.Add(searchRow, 0, 1);

            models.Dock = DockStyle.Fill;
            models.View = View.Details;
            models.FullRowSelect = true;
            models.MultiSelect = true;
            models.CheckBoxes = true;
            models.HideSelection = false;
            models.ShowItemToolTips = true;
            models.Columns.Add("Model", 250);
            models.Columns.Add("Folder", 610);
            models.ItemChecked += (sender, args) => {
                if (refreshing) return;
                var item = (ModelCandidate)args.Item.Tag;
                if (args.Item.Checked) checkedFolders.Add(item.Folder); else checkedFolders.Remove(item.Folder);
                UpdateStatus();
            };
            models.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.Handled = true;
                args.SuppressKeyPress = true;
                LoadSelected();
            };
            layout.Controls.Add(models, 0, 2);

            status.AutoSize = true;
            status.Margin = new Padding(0, 8, 0, 12);
            layout.Controls.Add(status, 0, 3);
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0)
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
            load.Text = "Load checked models";
            load.AutoSize = true;
            load.Padding = new Padding(10, 5, 10, 5);
            load.Click += (sender, args) => LoadSelected();
            var chooseFolder = new Button { Text = "Choose another folder…", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
            chooseFolder.Click += (sender, args) =>
            {
                string folder = FolderLocationDialog.Choose(this, "Model folder for " + selected.Name,
                    "Choose the model folder for " + selected.Name, initialPath);
                if (folder != null) Complete(folder);
            };
            actions.Controls.Add(cancel);
            actions.Controls.Add(load);
            var checkVisible = new Button { Text = "Check visible", AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
            checkVisible.Click += (sender, args) => { foreach (ListViewItem item in models.Items) item.Checked = true; };
            var clear = new Button { Text = "Clear checks", AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
            clear.Click += (sender, args) => { checkedFolders.Clear(); RefreshModels(); };
            actions.Controls.Add(checkVisible);
            actions.Controls.Add(clear);
            if (!matchingOnly) actions.Controls.Add(chooseFolder);
            layout.Controls.Add(actions, 0, 4);
            CancelButton = cancel;

            ApplyTheme(this);
            status.ForeColor = Color.FromArgb(156, 156, 166);
            load.BackColor = Color.FromArgb(227, 32, 42);
            load.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 58, 67);
            models.OwnerDraw = true;
            models.DrawColumnHeader += (sender, args) =>
            {
                using (var background = new SolidBrush(Color.FromArgb(29, 29, 34))) args.Graphics.FillRectangle(background, args.Bounds);
                TextRenderer.DrawText(args.Graphics, args.Header.Text, Font,
                    new Rectangle(args.Bounds.X + 7, args.Bounds.Y, args.Bounds.Width - 7, args.Bounds.Height),
                    ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            };
            models.DrawItem += (sender, args) => { if (models.View != View.Details) args.DrawDefault = true; };
            models.DrawSubItem += (sender, args) => args.DrawDefault = true;
            search.Text = matchingOnly ? "" : selected.Name ?? "";
            RefreshModels();
            Shown += (sender, args) => { search.Focus(); search.SelectAll(); };
        }

        private void RefreshModels()
        {
            string query = SearchKey(search.Text);
            RosterEntry matchingSelection = selected;
            if (string.IsNullOrWhiteSpace(selected.StockFolder) && (string.Equals(selected.Source, "Profile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selected.Source, "Found in models folder", StringComparison.OrdinalIgnoreCase)
                || (selected.Source != null && selected.Source.StartsWith("Chosen", StringComparison.OrdinalIgnoreCase))))
                matchingSelection = new RosterEntry { Id = selected.Id, Name = selected.Name, SourceId = 0 };
            var recommended = new HashSet<string>(ModelLibrary.Match(candidates, matchingSelection).Select(candidate => candidate.Folder), StringComparer.OrdinalIgnoreCase);
            models.BeginUpdate();
            refreshing = true;
            try
            {
                models.Items.Clear();
                foreach (ModelCandidate candidate in candidates.OrderByDescending(candidate => recommended.Contains(candidate.Folder)))
                {
                    if (query.Length > 0 && SearchKey(candidate.Name).IndexOf(query, StringComparison.Ordinal) < 0
                        && SearchKey(candidate.Folder).IndexOf(query, StringComparison.Ordinal) < 0) continue;
                    var item = new ListViewItem(candidate.Name ?? Path.GetFileName(candidate.Folder)) { Tag = candidate, ToolTipText = candidate.Folder };
                    item.SubItems.Add(candidate.Folder);
                    models.Items.Add(item);
                    item.Checked = checkedFolders.Contains(candidate.Folder);
                    if (string.Equals(candidate.Folder, initialPath, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
                }
                if (models.SelectedItems.Count == 0 && models.Items.Count > 0) models.Items[0].Selected = true;
            }
            finally { refreshing = false; models.EndUpdate(); }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            load.Enabled = checkedFolders.Count > 0;
            status.Text = models.Items.Count == 0
                ? (matchingOnly ? "No matches for this search. Click Show all." : "No matching model folders. Click Show all or choose another folder.")
                : models.Items.Count + " of " + candidates.Count + " model folders · " + checkedFolders.Count + " checked (checks remain when searching)";
        }

        private static string SearchKey(string value)
        {
            var result = new StringBuilder();
            foreach (char character in (value ?? "").Normalize(NormalizationForm.FormC))
                if (char.IsLetterOrDigit(character)) result.Append(char.ToUpperInvariant(character));
            return result.ToString();
        }

        private void LoadSelected()
        {
            var choices = candidates.Where(candidate => checkedFolders.Contains(candidate.Folder)).ToList();
            if (choices.Count == 0) return;
            if (choices.Any(candidate => !Directory.Exists(candidate.Folder)))
            {
                status.Text = "This folder is no longer available. Choose another folder or refresh your mod library.";
                return;
            }
            selectedModels = choices;
            selectedPath = choices[0].Folder;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Complete(string path)
        {
            selectedPath = path;
            selectedModels = new List<ModelCandidate> { new ModelCandidate { Folder = path, Name = Path.GetFileName(path) } };
            DialogResult = DialogResult.OK;
            Close();
        }

        private static void ApplyTheme(Control control)
        {
            control.BackColor = Color.FromArgb(13, 13, 15);
            control.ForeColor = Color.FromArgb(243, 243, 245);
            if (control is TextBox || control is ListView) control.BackColor = Color.FromArgb(22, 22, 26);
            var button = control as Button;
            if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.FromArgb(29, 29, 34);
                button.FlatAppearance.BorderColor = Color.FromArgb(42, 42, 49);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 42, 49);
            }
            foreach (Control child in control.Controls) ApplyTheme(child);
        }
    }
}
