using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    internal sealed partial class WrestlerPicker : Form
    {
        private readonly TextBox search = new TextBox();
        private readonly ListView wrestlers = new ListView();
        private readonly Label status = new Label();
        private readonly Button confirm = new Button();
        private readonly Button chooseSlot = new Button();
        private readonly Button browseModel = new Button();
        private readonly ComboBox filter = new ComboBox();
        private readonly NumericUpDown manualId = new NumericUpDown();
        private readonly TextBox manualName = new TextBox();
        private readonly string suggestedName;
        private readonly string sourceFolder;
        private readonly bool chooseCharacterFolder;
        private string currentRosterTable;
        private string currentRosterFile;
        private readonly Button refreshData = new Button();
        public List<RosterEntry> Entries { get; private set; }
        public RosterEntry SelectedEntry { get; private set; }
        public string SelectedFolder { get; private set; }

        public WrestlerPicker(string source, List<RosterEntry> entries, bool chooseCharacterFolder)
        {
            Text = "Gorilla Studio — choose wrestler";
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 610);
            Size = new Size(1050, 760);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            Entries = entries;
            sourceFolder = source;
            this.chooseCharacterFolder = chooseCharacterFolder;
            currentRosterTable = CommonRosterTable(entries);
            currentRosterFile = currentRosterTable;
            suggestedName = FolderNames.DisplayName(new DirectoryInfo(source).Name);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < layout.RowCount; row++)
                layout.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Percent : SizeType.AutoSize, row == 4 ? 100 : 0));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = chooseCharacterFolder ? "Choose a character from your Discovery data" : "Choose the wrestler for this folder", AutoSize = true,
                Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
            layout.Controls.Add(new Label { Text = source, AutoSize = true, MaximumSize = new Size(800, 56),
                AutoEllipsis = true, Margin = new Padding(0, 0, 0, 10) }, 0, 1);

            var importBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
                Margin = new Padding(0, 0, 0, 8) };
            var loadFile = new Button { Text = "Load roster file…", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            loadFile.Click += LoadRosterFile;
            importBar.Controls.Add(loadFile);
            refreshData.Text = "Refresh data";
            refreshData.AutoSize = true;
            refreshData.Padding = new Padding(8, 2, 8, 2);
            refreshData.Enabled = currentRosterFile != null;
            refreshData.Click += (s, e) =>
            {
                if (modelSelectionBusy || currentRosterFile == null) return;
                Import(currentRosterFile);
            };
            importBar.Controls.Add(refreshData);
            InitializeModelLibrary(importBar);
            layout.Controls.Add(importBar, 0, 2);

            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3,
                Margin = new Padding(0, 0, 0, 6) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            searchRow.Controls.Add(new Label { Text = "Search", AutoSize = true, Margin = new Padding(0, 5, 12, 0) }, 0, 0);
            search.Dock = DockStyle.Fill;
            search.TextChanged += (s, e) => RefreshList(null);
            searchRow.Controls.Add(search, 1, 0);
            filter.DropDownStyle = ComboBoxStyle.DropDownList;
            filter.Dock = DockStyle.Fill;
            filter.Items.AddRange(new object[] { "All wrestlers", "Models in data", "Model not in data" });
            filter.SelectedIndex = 0;
            filter.SelectedIndexChanged += (s, e) => RefreshList(null);
            searchRow.Controls.Add(filter, 2, 0);
            layout.Controls.Add(searchRow, 0, 3);

            wrestlers.Dock = DockStyle.Fill;
            wrestlers.View = View.Details;
            wrestlers.FullRowSelect = true;
            wrestlers.MultiSelect = false;
            wrestlers.HideSelection = false;
            wrestlers.Columns.Add("Wrestler ID", 110);
            wrestlers.Columns.Add("Wrestler name", 300);
            wrestlers.Columns.Add("Model / source", 520);
            wrestlers.ShowItemToolTips = true;
            wrestlers.SelectedIndexChanged += (s, e) => UpdateButtons();
            wrestlers.DoubleClick += (s, e) => ConfirmSelection();
            layout.Controls.Add(wrestlers, 0, 4);

            status.AutoSize = true;
            status.Margin = new Padding(0, 6, 0, 8);
            layout.Controls.Add(status, 0, 5);

            var manual = new GroupBox { Text = "Add a wrestler manually", Dock = DockStyle.Fill, AutoSize = true,
                Padding = new Padding(10), Margin = new Padding(0, 4, 0, 8) };
            var manualRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5 };
            manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            manualRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            manualRow.Controls.Add(new Label { Text = "Game ID", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
            manualId.Minimum = 1;
            manualId.Maximum = 65535;
            manualId.Dock = DockStyle.Fill;
            manualRow.Controls.Add(manualId, 1, 0);
            manualRow.Controls.Add(new Label { Text = "Name", AutoSize = true, Margin = new Padding(12, 6, 8, 0) }, 2, 0);
            manualName.Text = chooseCharacterFolder ? "" : suggestedName;
            manualName.Dock = DockStyle.Fill;
            manualRow.Controls.Add(manualName, 3, 0);
            var add = new Button { Text = "Add", AutoSize = true };
            add.Click += AddManual;
            manualRow.Controls.Add(add, 4, 0);
            manual.Controls.Add(manualRow);
            layout.Controls.Add(manual, 0, 6);

            layout.Controls.Add(new Label { Text = "Choose the game's wrestler ID, not a roster-picture number. Your original folder keeps its name.",
                AutoSize = true, Margin = new Padding(0, 2, 0, 10) }, 0, 7);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
            confirm.Text = "Load selected wrestler";
            confirm.AutoSize = true;
            confirm.Padding = new Padding(8, 4, 8, 4);
            confirm.Click += (s, e) => ConfirmSelection();
            chooseSlot.Text = "Use folder for another wrestler…";
            chooseSlot.AutoSize = true;
            chooseSlot.Padding = new Padding(8, 4, 8, 4);
            chooseSlot.Click += (s, e) => ChooseFolderSlot();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(confirm);
            if (chooseCharacterFolder) buttons.Controls.Add(chooseSlot);
            if (chooseCharacterFolder)
            {
                browseModel.Text = "Choose model folder…";
                browseModel.AutoSize = true;
                browseModel.Padding = new Padding(8, 4, 8, 4);
                browseModel.Click += async (s, e) =>
                {
                    if (modelSelectionBusy || wrestlers.SelectedItems.Count != 1) return;
                    var entry = (RosterEntry)wrestlers.SelectedItems[0].Tag;
                    if (entry.Id < 1) return;
                    modelSelectionBusy = true;
                    UpdateButtons();
                    try
                    {
                        string folder = await ChooseExistingModelAsync(entry, true);
                        if (folder != null && !IsDisposed) CompleteSelection(entry, folder);
                    }
                    catch (Exception error) { MessageBox.Show(this, error.Message, "Choose model", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                    finally { modelSelectionBusy = false; if (!IsDisposed) UpdateButtons(); }
                };
                buttons.Controls.Add(browseModel);
            }
            layout.Controls.Add(buttons, 0, 8);
            CancelButton = cancel;
            ApplyTheme(this);
            status.ForeColor = Color.FromArgb(156, 156, 166);
            confirm.BackColor = Color.FromArgb(227, 32, 42);
            confirm.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 58, 67);
            wrestlers.OwnerDraw = true;
            wrestlers.DrawColumnHeader += (s, e) =>
            {
                using (var background = new SolidBrush(Color.FromArgb(29, 29, 34))) e.Graphics.FillRectangle(background, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Font,
                    new Rectangle(e.Bounds.X + 7, e.Bounds.Y, e.Bounds.Width - 7, e.Bounds.Height),
                    ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            };
            wrestlers.DrawItem += (s, e) => { if (wrestlers.View != View.Details) e.DrawDefault = true; };
            wrestlers.DrawSubItem += (s, e) => e.DrawDefault = true;
            // Enter remains useful in the search/manual fields; loading requires an explicit click or a list double-click.
            RefreshList(null);
        }

        private static void ApplyTheme(Control control)
        {
            control.BackColor = Color.FromArgb(13, 13, 15);
            control.ForeColor = Color.FromArgb(243, 243, 245);
            if (control is TextBox || control is NumericUpDown || control is ListView)
                control.BackColor = Color.FromArgb(22, 22, 26);
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

        private void RefreshList(RosterEntry preferred)
        {
            string query = search.Text.Replace('_', ' ').Trim();
            wrestlers.BeginUpdate();
            wrestlers.Items.Clear();
            foreach (RosterEntry entry in Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id))
            {
                bool hasModel = Directory.Exists(entry.Folder);
                if ((filter.SelectedIndex == 1 && !hasModel) || (filter.SelectedIndex == 2 && hasModel)) continue;
                if (query.Length > 0 && entry.Name.Replace('_', ' ').IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Id.ToString(CultureInfo.InvariantCulture).IndexOf(query, StringComparison.Ordinal) < 0
                    && (entry.Folder ?? "").Replace('_', ' ').IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var item = new ListViewItem(entry.Id == 0 ? "Choose…" : entry.Id.ToString(CultureInfo.InvariantCulture)) { Tag = entry };
                item.SubItems.Add(entry.Name);
                List<ModelCandidate> choices;
                item.SubItems.Add(hasModel ? (entry.Source ?? "Folder") + " · " + Path.GetFileName(entry.Folder)
                    : modelChoices.TryGetValue(entry, out choices) ? choices.Count + " matching models · choose from list"
                    : entry.StockFolder != null ? "Available from installed game · " + entry.StockFolder : "Model not found · Scan models folder…");
                item.ToolTipText = entry.Folder ?? (entry.StockFolder == null ? "This roster profile has no model in the supplied data. Select its mod folder to load it." : "Load verified game model: " + entry.StockFolder);
                wrestlers.Items.Add(item);
                if ((preferred != null && preferred.Id == entry.Id && preferred.Name == entry.Name)
                    || (preferred == null && string.Equals(entry.Name.Replace('_', ' '), suggestedName, StringComparison.OrdinalIgnoreCase)))
                    item.Selected = true;
            }
            wrestlers.EndUpdate();
            UpdateButtons();
            status.Text = Entries.Count == 0 ? "Load a roster, or enter a wrestler's game ID and name below."
                : string.Format(CultureInfo.InvariantCulture, "{0} roster entries · {1} with model folders · {2} shown", Entries.Count, Entries.Count(e => Directory.Exists(e.Folder)), wrestlers.Items.Count);
            if (wrestlers.SelectedItems.Count > 0) wrestlers.SelectedItems[0].EnsureVisible();
        }

        private void LoadRosterFile(object sender, EventArgs args)
        {
            if (modelSelectionBusy) return;
            using (var dialog = new OpenFileDialog { Title = "Load your roster",
                Filter = "Roster files|*.json;*.jsfb|JSON roster|*.json|Discovery roster table|*.jsfb",
                CheckFileExists = true, Multiselect = false })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                Import(dialog.FileName);
            }
        }

        private void Import(string rosterFile)
        {
            if (modelSelectionBusy) return;
            try
            {
                List<RosterEntry> next = RosterSession.Refresh(rosterFile, Entries, currentRosterTable);
                Entries = next;
                currentRosterTable = CommonRosterTable(next);
                currentRosterFile = Path.GetFullPath(rosterFile);
                refreshData.Enabled = true;
                search.Clear();
                RefreshList(null);
                ResetModelLibrary();
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Could not load roster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void AddManual(object sender, EventArgs args)
        {
            if (modelSelectionBusy) return;
            try
            {
                string name = manualName.Text.Trim();
                int id = (int)manualId.Value;
                FolderNames.AliasName(id, name);
                var entry = Entries.Find(e => e.Id == id && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry == null) { entry = new RosterEntry { Id = id, Name = name, RosterTable = currentRosterTable }; Entries.Add(entry); }
                search.Clear();
                filter.SelectedIndex = 0;
                RefreshList(entry);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Could not add wrestler", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateButtons()
        {
            if (rosterActions != null) rosterActions.Enabled = !modelSelectionBusy;
            wrestlers.Enabled = search.Enabled = filter.Enabled = manualName.Enabled = manualId.Enabled = !modelSelectionBusy;
            confirm.Enabled = !modelSelectionBusy && wrestlers.SelectedItems.Count == 1;
            var entry = confirm.Enabled ? (RosterEntry)wrestlers.SelectedItems[0].Tag : null;
            confirm.Text = entry != null && entry.Id == 0 ? "Choose wrestler for folder…" : "Load selected wrestler";
            chooseSlot.Enabled = entry != null && Directory.Exists(entry.Folder);
            browseModel.Enabled = entry != null && entry.Id > 0;
        }

        private void ChooseFolderSlot()
        {
            if (wrestlers.SelectedItems.Count != 1) return;
            var model = (RosterEntry)wrestlers.SelectedItems[0].Tag;
            if (!Directory.Exists(model.Folder)) return;
            string modelFolder = model.Folder;
            if (model.Source == "Choose an attire folder")
            {
                modelFolder = FolderLocationDialog.Choose(this, "Choose a specific model",
                    "This folder contains different model IDs. Choose the specific character or attire folder to load.", modelFolder);
                if (modelFolder == null) return;
                if (!CharacterFolders.PhysicalId(modelFolder).HasValue)
                {
                    MessageBox.Show(this, "Choose a specific numbered character or attire folder.", "Choose model folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            using (var picker = new WrestlerPicker(modelFolder, Entries.Where(e => e.Id > 0).ToList(), false))
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                RosterEntry target = picker.SelectedEntry;
                if (!Entries.Contains(target)) Entries.Add(target);
                target.SourceId = CharacterFolders.PhysicalId(modelFolder) ?? (model.SourceId > 0 ? model.SourceId : target.Id);
                target.IsMod = true;
                target.Source = "Chosen folder";
                CompleteSelection(target, modelFolder);
            }
        }

        private async void ConfirmSelection()
        {
            if (modelSelectionBusy || wrestlers.SelectedItems.Count != 1) return;
            var entry = (RosterEntry)wrestlers.SelectedItems[0].Tag;
            if (entry.Id == 0) { ChooseFolderSlot(); return; }
            modelSelectionBusy = true;
            UpdateButtons();
            try
            {
                await modelScanTask;
                if (IsDisposed) return;
                string selectedFolder = chooseCharacterFolder ? entry.Folder : sourceFolder;
                if (CharacterFolders.HasAmbiguousChildren(selectedFolder))
                {
                    selectedFolder = FolderLocationDialog.Choose(this, "Choose a specific model",
                        "Choose the specific numbered character or attire folder to load.", selectedFolder);
                    if (selectedFolder == null || CharacterFolders.HasAmbiguousChildren(selectedFolder)) return;
                }
                List<ModelCandidate> choices;
                if (chooseCharacterFolder && !Directory.Exists(selectedFolder) && modelChoices.TryGetValue(entry, out choices))
                    selectedFolder = await ChooseExistingModelAsync(entry);
                else if (chooseCharacterFolder && !Directory.Exists(selectedFolder) && !string.IsNullOrWhiteSpace(entry.StockFolder))
                {
                    try
                    {
                        Enabled = false;
                        ControlBox = false;
                        UseWaitCursor = true;
                        status.Text = "Loading " + entry.Name + " from the installed game…";
                        selectedFolder = await FolderBridge.RequestGameModelAsync(entry, entry.StockFolder, line =>
                        {
                            Action update = () => status.Text = line.Length > 150 ? line.Substring(0, 150) : line;
                            if (!IsDisposed && IsHandleCreated) { if (InvokeRequired) BeginInvoke(update); else update(); }
                        });
                    }
                    finally { Enabled = true; ControlBox = true; UseWaitCursor = false; }
                }
                else if (!Directory.Exists(selectedFolder))
                    selectedFolder = await ChooseExistingModelAsync(entry);
                if (selectedFolder != null && !IsDisposed) CompleteSelection(entry, selectedFolder);
            }
            catch (Exception error)
            {
                if (!IsDisposed) MessageBox.Show(this, error.Message, "Could not load model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { modelSelectionBusy = false; if (!IsDisposed) UpdateButtons(); }
        }

        private void CompleteSelection(RosterEntry entry, string selectedFolder)
        {
            SelectedEntry = entry;
            SelectedFolder = selectedFolder;
            RosterSession.RememberSelection(entry, selectedFolder, chooseCharacterFolder);
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string CommonRosterTable(List<RosterEntry> entries)
        {
            string[] paths = entries.Where(e => !string.IsNullOrWhiteSpace(e.RosterTable))
                .Select(e => e.RosterTable).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            return paths.Length == 1 ? paths[0] : null;
        }
    }
}
