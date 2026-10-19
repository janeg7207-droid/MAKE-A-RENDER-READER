using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int Main(string[] args)
    {
        string planPath = args.Length == 2 && args[0] == "--plan" ? Path.GetFullPath(args[1]) : null;
        if (planPath == null) { Console.Error.WriteLine("Launch this updater using Gorilla Studio's Update app button."); return 2; }
        AppUpdatePlan plan = null;
        bool parentExited = false;
        try
        {
            string work = Path.GetDirectoryName(planPath);
            plan = AppUpdateTransaction.ReadPlan(planPath);
            string running = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (!running.StartsWith(work.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The updater must run from its separate staging folder.");
            if (plan.ParentProcessId == Process.GetCurrentProcess().Id) throw new IOException("The updater cannot replace its own running application.");
            AppUpdateTransaction.Preflight(plan, work);
            if (File.Exists(planPath + ".cancel")) return 3;
            using (Process parent = FindParent(plan))
            {
                Write(planPath + ".ready", "Ready for Gorilla Studio to close.");
                var wait = Stopwatch.StartNew();
                while (parent != null && !parent.WaitForExit(200))
                {
                    if (File.Exists(planPath + ".cancel")) return 3;
                    if (wait.Elapsed > TimeSpan.FromSeconds(60)) throw new IOException("Gorilla Studio did not close within 60 seconds. The update was not installed.");
                }
            }
            parentExited = true;
            if (File.Exists(planPath + ".cancel")) return 3;
            AppUpdateApplyReport result = AppUpdateTransaction.Apply(plan, work);
            Write(planPath + ".result", (result.Success ? "Update installed." : "Update failed.") + Environment.NewLine + string.Join(Environment.NewLine, result.Errors)
                + Environment.NewLine + "Backups: " + result.BackupDirectory);
            if (result.RestartSafe)
                Process.Start(new ProcessStartInfo { FileName = Path.Combine(plan.CanonicalAppRoot, "GorillaStudio.exe"), WorkingDirectory = plan.CanonicalAppRoot, UseShellExecute = true });
            if (!result.Success)
            {
                string message = "The update could not finish. " + (result.RestartSafe ? "The original app was restored and reopened." : "The app was not restarted because some files need attention.")
                    + Environment.NewLine + string.Join(Environment.NewLine, result.Errors) + Environment.NewLine + "Backups: " + result.BackupDirectory;
                Write(planPath + ".error", message); Notice(message); return 1;
            }
            return 0;
        }
        catch (Exception error)
        {
            try { Write(planPath + ".error", error.Message); } catch { }
            Console.Error.WriteLine(error.Message);
            if (parentExited) Notice("The update stopped: " + error.Message + Environment.NewLine + "Open GorillaStudio.exe manually. Update details: " + planPath + ".result");
            return 1;
        }
    }

    private static Process FindParent(AppUpdatePlan plan)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentProcessId); }
        catch (ArgumentException) { return null; }
        try
        {
            if (parent.HasExited) { parent.Dispose(); return null; }
            bool matches = Path.DirectorySeparatorChar == '/' && !string.IsNullOrWhiteSpace(plan.ParentStartToken)
                ? AppUpdateTransaction.ReadParentStartToken(plan.ParentProcessId) == plan.ParentStartToken
                : parent.StartTime.ToUniversalTime().Ticks == plan.ParentStartTimeUtcTicks;
            if (!matches) throw new IOException("The original application process no longer matches the update request.");
            if (parent.HasExited) { parent.Dispose(); return null; }
            return parent;
        }
        catch { parent.Dispose(); throw; }
    }
    private static void Write(string path, string text) { File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private static void Notice(string text) { if (Path.DirectorySeparatorChar == '\\') MessageBox(IntPtr.Zero, text, "Gorilla Studio update", 0x10); }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint flags);
}
