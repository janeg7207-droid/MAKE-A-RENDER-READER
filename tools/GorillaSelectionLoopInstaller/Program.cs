using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GorillaStudio.SelectionLoop
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerWindow());
        }
    }

    internal sealed class InstallerWindow : Form
    {
        private readonly TextBox executable = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly TextBox result = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly Button choose = new Button { Text = "Choose WWE2K26_x64.exe…", AutoSize = true };
        private readonly Button install = new Button { Text = "Install and enable", AutoSize = true };
        private readonly Button uninstall = new Button { Text = "Uninstall", AutoSize = true };
        private bool working;
        private static string RememberedFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GorillaStudio", "selection-loop-game.txt"); } }

        internal InstallerWindow()
        {
            Text = "Optional selection-loop companion"; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 430); MinimumSize = new Size(560, 350); Font = new Font("Segoe UI", 10);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
            for (int row = 0; row < 5; row++) layout.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Percent : SizeType.AutoSize, row == 4 ? 100 : 0));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Experimental selection loop", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) }, 0, 0);
            layout.Controls.Add(new Label { Text = "Loops the moving render prefix on wrestler selection. This does not restore the full taunt or add match-card playback. Close WWE 2K26 before installing or uninstalling. The original Moving Renders DLL stays unchanged.",
                AutoSize = true, MaximumSize = new Size(660, 0), Margin = new Padding(0, 0, 0, 14) }, 0, 1);
            var picker = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 12) };
            picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            picker.Controls.Add(executable, 0, 0); picker.Controls.Add(choose, 1, 0); layout.Controls.Add(picker, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            actions.Controls.Add(install); actions.Controls.Add(uninstall); layout.Controls.Add(actions, 0, 3); layout.Controls.Add(result, 0, 4);
            choose.Click += (sender, args) => ChooseGame();
            install.Click += async (sender, args) => await Run(true);
            uninstall.Click += async (sender, args) => await Run(false);
            FormClosing += (sender, args) => { if (working) { args.Cancel = true; result.AppendText("\r\nWait for the current file operation to finish before closing."); } };
            try { if (File.Exists(RememberedFile)) executable.Text = File.ReadAllText(RememberedFile).Trim(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            result.Text = "Choose the game's executable once, then use Install and enable. The downloaded configuration starts disabled. No game or Gorilla Studio process will be launched.";
        }

        private void ChooseGame()
        {
            using (var dialog = new OpenFileDialog { Title = "Choose installed WWE 2K26", Filter = "WWE 2K26 executable|WWE2K26_x64.exe", CheckFileExists = true, FileName = "WWE2K26_x64.exe" })
            {
                if (File.Exists(executable.Text)) dialog.InitialDirectory = Path.GetDirectoryName(executable.Text);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                executable.Text = dialog.FileName;
                try { Directory.CreateDirectory(Path.GetDirectoryName(RememberedFile)); File.WriteAllText(RememberedFile, dialog.FileName); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private async Task Run(bool installing)
        {
            if (working) return;
            if (!File.Exists(executable.Text)) { ChooseGame(); if (!File.Exists(executable.Text)) return; }
            working = true; choose.Enabled = install.Enabled = uninstall.Enabled = false;
            string selected = executable.Text;
            result.Text = installing ? "Verifying the optional package and installed game…" : "Checking owned companion files…";
            try
            {
                SelectionLoopInstallReport report = await Task.Run(() => installing
                    ? SelectionLoopTransaction.Install(AppDomain.CurrentDomain.BaseDirectory, selected)
                    : SelectionLoopTransaction.Uninstall(selected));
                result.Text = string.Join(Environment.NewLine, report.Messages)
                    + (report.RolledBack ? Environment.NewLine + "The operation failed and its changes were rolled back." : "")
                    + (report.BackupDirectory == null ? "" : Environment.NewLine + "Backups: " + report.BackupDirectory);
            }
            catch (Exception error) { result.Text = "The operation could not finish: " + error.Message; }
            finally { working = false; choose.Enabled = install.Enabled = uninstall.Enabled = true; }
        }
    }
}
