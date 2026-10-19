using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class TransactionTests
{
    private static int passed, cases;
    private static string root;
    private static readonly string[] Names = { "GorillaStudio.exe", "GorillaStudio.NameFolders.dll", "GorillaStudio.Updater.exe", "Pfim.dll", "README_DISCOVERY.txt", "TEST_RESULTS.json", "BUILD_SHA256.json", "rosters/Discovery_roster.json", "third_party/Pfim/LICENSE.txt", "third_party/Pfim/NOTICE.txt", "engine/gr_render.py" };
    private sealed class Fixture { internal string App, Work; internal AppUpdatePlan Plan; internal Dictionary<string, byte[]> New, Old; }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--plan") return (int)typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { args });
        root = Path.Combine(Path.GetTempPath(), "gorilla-app-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Success(); Failures(); Links();
            if (args.Length == 1) await Handshake(args[0]);
            Console.WriteLine(passed + " application update transaction checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Success()
    {
        var f = Make();
        string planPath = Path.Combine(f.Work, "update-plan.json"); AppUpdateTransaction.WritePlan(f.Plan, planPath);
        AppUpdatePlan read = AppUpdateTransaction.ReadPlan(planPath);
        Check(read.PackageSha256 == f.Plan.PackageSha256 && read.ParentStartTimeUtcTicks == f.Plan.ParentStartTimeUtcTicks && read.CanonicalAppRoot == f.Plan.CanonicalAppRoot, "The handoff plan round-trips exact package, process and physical root identity");
        using (var loaded = new FileStream(Path.Combine(f.App, "GorillaStudio.exe"), FileMode.Open, FileAccess.Read, FileShare.Read)) AppUpdateTransaction.Preflight(read, f.Work);
        Check(Originals(f), "Preflight works with an open application executable and changes no application files");
        string packageHash = Hash(f.Plan.PackagePath);
        var result = AppUpdateTransaction.Apply(read, f.Work);
        Check(result.Success && result.RestartSafe && !result.RolledBack, "A verified update commits and permits native restart");
        Check(Names.All(name => File.ReadAllBytes(Path.Combine(f.App, name)).SequenceEqual(f.New[name])), "Exactly all eleven approved targets receive their validated new contents");
        Check(Names.All(name => File.ReadAllBytes(Path.Combine(result.BackupDirectory, name + ".before-update")).SequenceEqual(f.Old[name])), "All application files have exact original backups before replacement");
        Check(Unrelated(f), "Settings, user roster and existing renders remain unchanged");
        Check(Hash(f.Plan.PackagePath) == packageHash, "The staged update ZIP is never modified");
        Check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(result.BackupDirectory), "recovery.json")).Contains("complete"), "The completed transaction retains a recovery journal and backup locations");
        Check(!Directory.GetFiles(f.App, ".gorilla-update-*", SearchOption.AllDirectories).Any(), "Completed updates leave no adjacent temporary application files");
    }

    private static void Failures()
    {
        var f = Make(true);
        var result = AppUpdateTransaction.Apply(f.Plan, f.Work, (index, name) => { if (index == 3) throw new IOException("Injected write failure"); });
        Check(!result.Success && result.RolledBack && result.RestartSafe, "A write failure restores the complete old application before restart");
        Check(Originals(f) && !File.Exists(Path.Combine(f.App, "GorillaStudio.Updater.exe")), "Rollback restores prior files and removes a newly created updater target");
        Check(Unrelated(f) && File.Exists(f.Plan.PackagePath), "Rollback preserves settings, user data and the source package");
        Check(f.Old.All(item => File.ReadAllBytes(Path.Combine(result.BackupDirectory, item.Key + ".before-update")).SequenceEqual(item.Value)), "Rollback keeps every original backup available for recovery");

        f = Make(true); string newerUpdater = Path.Combine(f.App, "GorillaStudio.Updater.exe");
        result = AppUpdateTransaction.Apply(f.Plan, f.Work, (index, name) => { if (index == 3) throw new IOException("Injected later failure"); }, null,
            name => { if (name == "GorillaStudio.Updater.exe") File.WriteAllText(newerUpdater, "external replacement after rollback hash check"); });
        Check(!result.Success && !result.RolledBack && !result.RestartSafe, "A replacement racing the new-target rollback is detected and blocks restart");
        Check(File.ReadAllText(newerUpdater) == "external replacement after rollback hash check", "Capture-first rollback restores the racing external file instead of deleting it");

        f = Make();
        result = AppUpdateTransaction.Apply(f.Plan, f.Work, null, state => { if (state == "complete") throw new IOException("Injected final journal failure"); });
        Check(!result.Success && result.RolledBack && result.RestartSafe, "A final journal failure after all eleven swaps reports failure and restores the old application");
        Check(Originals(f) && Unrelated(f), "Final-journal rollback restores all targets and preserves user files");

        f = Make(); string pending = Path.Combine(f.App, Names[2]);
        result = AppUpdateTransaction.Apply(f.Plan, f.Work, (index, name) => { if (index == 2) File.WriteAllText(pending, "external pending target"); });
        Check(!result.Success && !result.RestartSafe, "An external change after the snapshot stops automatic restart");
        Check(File.ReadAllText(pending) == "external pending target", "A newer external change to a pending target is not overwritten");
        Check(File.ReadAllBytes(Path.Combine(f.App, Names[0])).SequenceEqual(f.Old[Names[0]]) && File.ReadAllBytes(Path.Combine(f.App, Names[1])).SequenceEqual(f.Old[Names[1]]), "Earlier applied files are rolled back when a later target changes externally");

        f = Make(); string applied = Path.Combine(f.App, Names[0]);
        result = AppUpdateTransaction.Apply(f.Plan, f.Work, (index, name) => { if (index == 3) { File.WriteAllText(applied, "external after update"); throw new IOException("Injected later failure"); } });
        Check(!result.Success && !result.RolledBack && !result.RestartSafe, "A rollback conflict blocks restart instead of launching mixed application files");
        Check(File.ReadAllText(applied) == "external after update", "Rollback preserves an external change to an already applied target");
        Check(File.ReadAllBytes(Path.Combine(result.BackupDirectory, Names[0] + ".before-update")).SequenceEqual(f.Old[Names[0]]), "A conflicting target's original backup remains available");
        Check(File.ReadAllBytes(Path.Combine(f.App, Names[1])).SequenceEqual(f.Old[Names[1]]), "Rollback continues restoring other targets after one conflict");

        f = Make(); AppUpdateTransaction.Preflight(f.Plan, f.Work); File.AppendAllText(f.Plan.PackagePath, "tampered");
        result = AppUpdateTransaction.Apply(f.Plan, f.Work);
        Check(!result.Success && Originals(f) && result.RestartSafe, "A changed ZIP is rejected before any application write and the unchanged original app can restart");
        f = Make(); File.AppendAllText(f.Plan.PackagePath, "tampered"); File.WriteAllText(Path.Combine(f.App, "Pfim.dll"), "external change after handoff");
        result = AppUpdateTransaction.Apply(f.Plan, f.Work);
        Check(!result.Success && !result.RestartSafe, "A corrupt package cannot permit restart when another original application file also changed");
        Check(File.ReadAllText(Path.Combine(f.App, "Pfim.dll")) == "external change after handoff", "A pre-apply external application change survives package rejection");
        f = Make(); Directory.CreateDirectory(Path.Combine(f.App, "occupied")); f.Plan.PackagePath = Path.Combine(f.App, "occupied", "update.zip");
        Throws(() => AppUpdateTransaction.Preflight(f.Plan, f.Work), "An update package outside its staging workspace is rejected");
        Check(Originals(f), "Invalid staging paths leave the application intact");
        f = Make(); string blocked = Path.Combine(f.App, "Pfim.dll"); File.Delete(blocked); Directory.CreateDirectory(blocked);
        Throws(() => AppUpdateTransaction.Preflight(f.Plan, f.Work), "An existing directory cannot be replaced by an update file");
        Check(Directory.Exists(blocked) && File.ReadAllBytes(Path.Combine(f.App, Names[0])).SequenceEqual(f.Old[Names[0]]), "Target-type conflicts preserve existing directories and earlier application files");
    }

    private static void Links()
    {
        if (Path.DirectorySeparatorChar != '/') return;
        var f = Make(); string external = Path.Combine(root, "external-file"); File.WriteAllText(external, "external unchanged");
        string child = Path.Combine(f.App, "GorillaStudio.NameFolders.dll"); File.Delete(child); File.CreateSymbolicLink(child, external);
        Throws(() => AppUpdateTransaction.Preflight(f.Plan, f.Work), "A linked child application file is rejected");
        Check(File.ReadAllText(external) == "external unchanged", "Linked child validation never writes the external target");
        f = Make(); string externalDirectory = Directory.CreateDirectory(Path.Combine(root, "external-engine")).FullName;
        File.WriteAllText(Path.Combine(externalDirectory, "gr_render.py"), "external engine unchanged");
        Directory.Delete(Path.Combine(f.App, "engine"), true); Directory.CreateSymbolicLink(Path.Combine(f.App, "engine"), externalDirectory);
        Throws(() => AppUpdateTransaction.Preflight(f.Plan, f.Work), "A linked child directory cannot redirect update writes");
        Check(File.ReadAllText(Path.Combine(externalDirectory, "gr_render.py")) == "external engine unchanged", "Linked directory validation preserves its external files");
        f = Make(); string alias = Path.Combine(root, "legitimate-app-root-link"); Directory.CreateSymbolicLink(alias, f.App);
        f.Plan = AppUpdateTransaction.CreatePlan(alias, f.Plan.PackagePath, f.Plan.TargetVersion, f.Plan.PackageSha256, f.Plan.PackageBytes, f.Plan.ParentProcessId, f.Plan.ParentStartTimeUtcTicks, f.Plan.DownloadUrl);
        var result = AppUpdateTransaction.Apply(f.Plan, f.Work);
        Check(result.Success && File.ReadAllBytes(Path.Combine(f.App, Names[0])).SequenceEqual(f.New[Names[0]]), "A legitimate selected root link updates its intended physical application folder");
        Check(Unrelated(f), "Updating through the selected root link preserves user data");
        var redirected = Make(); string stagingAlias = Path.Combine(root, "legitimate-staging-link"); Directory.CreateSymbolicLink(stagingAlias, redirected.Work);
        redirected.Plan.PackagePath = Path.Combine(stagingAlias, Path.GetFileName(redirected.Plan.PackagePath));
        var redirectedResult = AppUpdateTransaction.Apply(redirected.Plan, stagingAlias);
        Check(redirectedResult.Success && Unrelated(redirected), "A legitimate redirected staging root supports the update without weakening application child-path checks");
        var moved = Make(); Directory.Delete(alias); Directory.CreateSymbolicLink(alias, moved.App);
        result = AppUpdateTransaction.Apply(f.Plan, f.Work);
        Check(!result.Success && !result.RestartSafe && Originals(moved), "Changing the selected root link after plan creation cannot update a different application");
    }

    private static async Task Handshake(string dotnet)
    {
        var f = Make();
        string assembly = Assembly.GetExecutingAssembly().Location, name = Path.GetFileNameWithoutExtension(assembly);
        foreach (string extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" }) File.Copy(Path.Combine(Path.GetDirectoryName(assembly), name + extension), Path.Combine(f.Work, name + extension));
        using (var parent = Process.GetCurrentProcess()) { f.Plan.ParentProcessId = parent.Id; f.Plan.ParentStartTimeUtcTicks = parent.StartTime.ToUniversalTime().Ticks; f.Plan.ParentStartToken = AppUpdateTransaction.ReadParentStartToken(parent.Id); }
        string plan = Path.Combine(f.Work, "update-plan.json"); AppUpdateTransaction.WritePlan(f.Plan, plan);
        using (var child = new Process { StartInfo = new ProcessStartInfo(dotnet) { UseShellExecute = false } })
        {
            child.StartInfo.ArgumentList.Add(Path.Combine(f.Work, name + ".dll")); child.StartInfo.ArgumentList.Add("--plan"); child.StartInfo.ArgumentList.Add(plan);
            child.Start();
            var wait = Stopwatch.StartNew(); while (!File.Exists(plan + ".ready") && !child.HasExited && wait.ElapsedMilliseconds < 15000) await Task.Delay(50);
            Check(File.Exists(plan + ".ready") && !child.HasExited, "The real updater process acknowledges readiness while the exact original parent remains running");
            Check(Originals(f), "The waiting updater applies no files before the parent exits");
            File.WriteAllText(plan + ".cancel", "Cancel handoff");
            if (!child.WaitForExit(5000)) { child.Kill(); throw new Exception("The waiting updater did not honor cancellation"); }
            Check(child.ExitCode == 3 && Originals(f), "The waiting updater honors cancel and preserves the running application");
        }
        File.Delete(plan + ".ready"); File.Delete(plan + ".cancel"); f.Plan.ParentStartTimeUtcTicks++; if (f.Plan.ParentStartToken != null) f.Plan.ParentStartToken += "1"; AppUpdateTransaction.WritePlan(f.Plan, plan);
        using (var child = new Process { StartInfo = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardError = true } })
        {
            child.StartInfo.ArgumentList.Add(Path.Combine(f.Work, name + ".dll")); child.StartInfo.ArgumentList.Add("--plan"); child.StartInfo.ArgumentList.Add(plan);
            child.Start(); if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Parent identity check hung"); }
            Check(child.ExitCode == 1 && File.Exists(plan + ".error") && !File.Exists(plan + ".ready"), "A reused or mismatched parent identity fails before readiness and writes an error acknowledgement");
            Check(Originals(f), "A rejected parent identity makes no application changes");
        }
    }

    private static Fixture Make(bool missingUpdater = false)
    {
        string folder = Path.Combine(root, "case-" + (++cases));
        var f = new Fixture { App = Directory.CreateDirectory(Path.Combine(folder, "app")).FullName, Work = Directory.CreateDirectory(Path.Combine(folder, "staging")).FullName,
            New = Names.ToDictionary(name => name, name => Encoding.UTF8.GetBytes("new application " + name)), Old = new Dictionary<string, byte[]>() };
        f.New["TEST_RESULTS.json"] = Encoding.UTF8.GetBytes("{\"helper_version\":\"1.6.6\"}");
        f.New["BUILD_SHA256.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(f.New.Where(item => item.Key != "BUILD_SHA256.json").ToDictionary(item => item.Key, item => Hash(item.Value))));
        foreach (string name in Names)
        {
            if (missingUpdater && name == "GorillaStudio.Updater.exe") continue;
            byte[] bytes = Encoding.UTF8.GetBytes("original application " + name); f.Old[name] = bytes; Write(Path.Combine(f.App, name), bytes);
        }
        Write(Path.Combine(f.App, "settings.json"), Encoding.UTF8.GetBytes("user settings")); Write(Path.Combine(f.App, "rosters", "user.json"), Encoding.UTF8.GetBytes("user roster")); Write(Path.Combine(f.App, "Renders", "saved.gmr"), Encoding.UTF8.GetBytes("user render"));
        string package = Path.Combine(f.Work, "update.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create)) foreach (var item in f.New) using (Stream output = zip.CreateEntry(item.Key).Open()) output.Write(item.Value);
        f.Plan = AppUpdateTransaction.CreatePlan(f.App, package, "1.6.6", Hash(package), new FileInfo(package).Length, 4242, 638000000000000000L,
            "https://raw.githubusercontent.com/janeg7207-droid/MAKE-A-RENDER-READER/1111111111111111111111111111111111111111/downloads/GorillaStudio_Update.zip"); return f;
    }
    private static bool Originals(Fixture f) { return f.Old.All(item => File.Exists(Path.Combine(f.App, item.Key)) && File.ReadAllBytes(Path.Combine(f.App, item.Key)).SequenceEqual(item.Value)); }
    private static bool Unrelated(Fixture f) { return File.ReadAllText(Path.Combine(f.App, "settings.json")) == "user settings" && File.ReadAllText(Path.Combine(f.App, "rosters", "user.json")) == "user roster" && File.ReadAllText(Path.Combine(f.App, "Renders", "saved.gmr")) == "user render"; }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static string Hash(string path) { return Hash(File.ReadAllBytes(path)); }
    private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); passed++; }
    private static void Throws(Action action, string text) { try { action(); } catch { passed++; return; } throw new Exception(text); }
}
