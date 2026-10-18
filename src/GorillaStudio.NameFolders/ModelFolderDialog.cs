using System;
using System.Drawing;
using System.IO;
using System.Security;
using System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    // Shared by model-folder and library-folder choices. The caller decides which kind of folder is valid.
    internal sealed class FolderLocationDialog : Form
    {
        private readonly TextBox location = new TextBox();
        private readonly Label feedback = new Label();
        private string selectedPath;

        internal static string Choose(IWin32Window owner, string title, string prompt, string initialPath)
        {
            using (var dialog = new FolderLocationDialog(title, prompt, initialPath))
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.selectedPath : null;
        }

        private FolderLocationDialog(string title, string prompt, string initialPath)
        {
            Text = title ?? "Choose a folder";
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(650, 300);
            Size = new Size(780, 310);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 6
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            var heading = new Label
            {
                Text = prompt ?? "Choose a folder", AutoSize = true,
                MaximumSize = new Size(700, 0), Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 16)
            };
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(new Label { Text = "Folder location", AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 1);

            var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            location.Dock = DockStyle.Fill;
            location.Font = new Font("Segoe UI", 11);
            location.Text = initialPath ?? "";
            location.Margin = new Padding(0, 4, 10, 4);
            location.TextChanged += (sender, args) => { feedback.Text = ""; };
            pathRow.Controls.Add(location, 0, 0);
            var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(6, 4, 6, 4), Margin = new Padding(0) };
            browse.Click += Browse;
            pathRow.Controls.Add(browse, 1, 0);
            layout.Controls.Add(pathRow, 0, 2);

            layout.Controls.Add(new Label
            {
                Text = "Paste a path, drag a folder here, or browse with Explorer.",
                AutoSize = true, Margin = new Padding(0, 9, 0, 0)
            }, 0, 3);
            feedback.AutoSize = true;
            feedback.MaximumSize = new Size(700, 0);
            feedback.Margin = new Padding(0, 8, 0, 10);
            layout.Controls.Add(feedback, 0, 4);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Margin = new Padding(0)
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
            var use = new Button { Text = "Use folder", AutoSize = true, Padding = new Padding(12, 5, 12, 5) };
            use.Click += UseFolder;
            actions.Controls.Add(cancel);
            actions.Controls.Add(use);
            layout.Controls.Add(actions, 0, 5);
            CancelButton = cancel;
            AcceptButton = use;

            ApplyTheme(this);
            feedback.ForeColor = Color.FromArgb(255, 143, 143);
            use.BackColor = Color.FromArgb(227, 32, 42);
            use.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 58, 67);
            EnableFolderDrop(this);
            Shown += (sender, args) => { location.Focus(); location.SelectAll(); };
        }

        private void Browse(object sender, EventArgs args)
        {
            try
            {
                string path = WindowsFolderPicker.Pick(this, Text, location.Text);
                if (path == null) return;
                location.Text = path;
                location.Focus();
                location.SelectAll();
            }
            catch (Exception error)
            {
                if (!(error is System.Runtime.InteropServices.COMException) && !(error is TypeLoadException)
                    && !(error is InvalidCastException) && !(error is NotSupportedException)
                    && !(error is System.Runtime.InteropServices.ExternalException)
                    && !(error is DllNotFoundException) && !(error is EntryPointNotFoundException)
                    && !(error is System.Reflection.TargetInvocationException)) throw;
                feedback.Text = "Windows could not open the folder browser. Paste a folder path or drag a folder here.";
                location.Focus();
            }
        }

        private void UseFolder(object sender, EventArgs args)
        {
            string path = NormalizeExistingDirectory(location.Text);
            if (path == null)
            {
                feedback.Text = "Choose an existing folder, or paste its full path.";
                location.Focus();
                return;
            }
            selectedPath = path;
            DialogResult = DialogResult.OK;
            Close();
        }

        // Copy as path adds quotes in Explorer. Environment variables are useful for saved library paths.
        internal static string NormalizeExistingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string value = path.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2).Trim();
            if (value.Length == 0) return null;
            try
            {
                value = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
                return Directory.Exists(value) ? value : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SecurityException) { return null; }
        }

        private void EnableFolderDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += (sender, args) =>
                args.Effect = DroppedDirectory(args.Data) == null ? DragDropEffects.None : DragDropEffects.Copy;
            control.DragDrop += (sender, args) =>
            {
                string path = DroppedDirectory(args.Data);
                if (path == null) return;
                location.Text = path;
                location.Focus();
                location.SelectAll();
            };
            foreach (Control child in control.Controls) EnableFolderDrop(child);
        }

        private static string DroppedDirectory(IDataObject data)
        {
            if (data == null) return null;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = data.GetData(DataFormats.FileDrop) as string[];
                return paths != null && paths.Length == 1 ? NormalizeExistingDirectory(paths[0]) : null;
            }
            return data.GetDataPresent(DataFormats.UnicodeText)
                ? NormalizeExistingDirectory(data.GetData(DataFormats.UnicodeText) as string) : null;
        }

        private static void ApplyTheme(Control control)
        {
            control.BackColor = Color.FromArgb(13, 13, 15);
            control.ForeColor = Color.FromArgb(243, 243, 245);
            if (control is TextBox) control.BackColor = Color.FromArgb(22, 22, 26);
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
