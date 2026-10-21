using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GorillaStudio.SelectionLoop;

internal static class Program
{
    private static string temporary;
    private static byte[] original;
    private static int passed;
    private static void Main(string[] args)
    {
        string originalPath = args.Length > 0 ? args[0] : "/workspace/gorilla-v102-work/original/GorillaStudio/plugin/WWE2K26_MovingRenders.dlp";
        original = File.ReadAllBytes(originalPath);
        Check(Sha(original) == SelectionLoopTransaction.OriginalSha256, "The positive fixture uses the exact original 4.21 plugin bytes required by production");
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-selection-installer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
        try
        {
            Basic(); RestoreOriginalCompanion(); ModifiedOwnedFiles(); Upgrades(); RollbackFailures(); RejectedPackages(); LinksAndBoundaries(); ClosedGame();
            if (args.Length > 1) ActualCompanionPackage(args[1], args.Length > 2 ? args[2] : null);
            Console.WriteLine("PASS " + passed + " selection-loop installer checks; no plugin or game execution");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void ActualCompanionPackage(string package, string realGmr)
    {
        Fixture fixture = Make("actual-compiled-companion");
        var before = Snapshot(package);
        byte[] render = realGmr == null ? null : File.ReadAllBytes(realGmr);
        string gameRender = Path.Combine(fixture.Game, "Plugins", "GorillaRenders", "1727_existing_real_fixture.gmr");
        if (render != null) File.WriteAllBytes(gameRender, render);
        byte[] real = File.ReadAllBytes(Path.Combine(package, SelectionLoopTransaction.PluginName));
        SelectionLoopInstallReport result = SelectionLoopTransaction.Install(package, fixture.Executable, () => true, null);
        Check(result.Success && File.ReadAllBytes(fixture.Plugin).SequenceEqual(real), "The actual compiled companion and generated package manifest pass production install validation");
        Check(File.ReadAllText(fixture.Config).Split('\n').Count(line => line.Trim() == "enabled 1") == 1,
            "The actual default-disabled configuration is enabled only in the explicitly installed copy");
        Check(Remove(fixture).Success && !File.Exists(fixture.Plugin) && !File.Exists(fixture.Config), "The actual packaged companion's unchanged installed copies uninstall safely");
        Check(Unchanged(before, package), "Actual DLL, manifest, disabled configuration and package documentation remain byte-for-byte unchanged");
        if (render != null) Check(File.ReadAllBytes(gameRender).SequenceEqual(render) && File.ReadAllBytes(realGmr).SequenceEqual(render),
            "An existing real GMR and its source fixture remain byte-for-byte unchanged through actual-package install and uninstall");
    }

    private static void Basic()
    {
        Fixture fixture = Make("basic"); var before = Snapshot(fixture.Package);
        byte[] existingOriginal = File.ReadAllBytes(fixture.Original), game = File.ReadAllBytes(fixture.Executable);
        SelectionLoopInstallReport installed = Install(fixture);
        Check(installed.Success && installed.Changed == 2, "Explicit installation installs exactly the new companion DLL and enabled configuration");
        Check(File.ReadAllBytes(fixture.Plugin).SequenceEqual(fixture.Payload), "The installed companion exactly matches the verified package");
        Check(File.ReadAllText(fixture.Config).Contains("enabled 1") && File.ReadAllText(fixture.Config).Contains("# optional"), "Enablement changes only the documented setting and retains configuration comments");
        Check(File.ReadAllText(Path.Combine(fixture.Package, SelectionLoopTransaction.ConfigName)).Contains("enabled 0"), "The downloaded default configuration remains disabled and unchanged");
        Check(File.Exists(fixture.State), "Successful installation records exact ownership for later safe removal");
        int backups = Directory.GetDirectories(Path.Combine(fixture.Game, "Plugins", "GorillaSelectionLoopBackups")).Length;
        SelectionLoopInstallReport repeated = Install(fixture);
        Check(repeated.Success && repeated.Changed == 0 && Directory.GetDirectories(Path.Combine(fixture.Game, "Plugins", "GorillaSelectionLoopBackups")).Length == backups, "Repeating the same installation is a no-op without accumulating backups");
        SelectionLoopInstallReport removed = Remove(fixture);
        Check(removed.Success && removed.Changed == 2 && !File.Exists(fixture.Plugin) && !File.Exists(fixture.Config) && !File.Exists(fixture.State), "Uninstall removes newly introduced unchanged files and their ownership record");
        Check(File.ReadAllBytes(fixture.Original).SequenceEqual(existingOriginal) && File.ReadAllBytes(fixture.Executable).SequenceEqual(game), "Install and uninstall never change the original module or game executable");
        Check(File.ReadAllText(Path.Combine(fixture.Game, "Plugins", "GorillaRenders", "1727_fixture.gmr")) == "existing render" && File.ReadAllText(Path.Combine(fixture.Game, "roster.json")) == "existing roster", "Existing render and roster data remain unchanged");
        Check(File.ReadAllText(Path.Combine(fixture.Game, "Plugins", "WWE2K26_MovingRenders.cfg")) == "original plugin settings", "The original Moving Renders configuration is preserved separately from the optional companion configuration");
        Check(Unchanged(before, fixture.Package), "All package source bytes remain unchanged through install, repeat and uninstall");
    }

    private static void RestoreOriginalCompanion()
    {
        foreach (bool originalRemoved in new[] { false, true })
        {
            Fixture fixture = Make("restore-" + originalRemoved); File.WriteAllText(fixture.Plugin, "older manually installed optional DLL"); File.WriteAllText(fixture.Config, "user's previous companion settings");
            Check(Install(fixture).Success, "Explicit installation backs up pre-existing companion files");
            if (originalRemoved) File.Delete(fixture.Original); else File.WriteAllText(fixture.Original, "updated original module unrelated to uninstall");
            Check(Remove(fixture).Success && File.ReadAllText(fixture.Plugin) == "older manually installed optional DLL" && File.ReadAllText(fixture.Config) == "user's previous companion settings", "Uninstall restores original companion bytes even when the original Moving Renders DLL is " + (originalRemoved ? "absent" : "updated"));
        }
    }

    private static void ModifiedOwnedFiles()
    {
        Fixture fixture = Make("modified-owned"); Check(Install(fixture).Success, "Modified-owned-file fixture installs successfully");
        byte[] installedConfig = File.ReadAllBytes(fixture.Config); File.WriteAllText(fixture.Config, "user changed this config");
        SelectionLoopInstallReport removed = Remove(fixture);
        Check(!removed.Success && !File.Exists(fixture.Plugin) && File.ReadAllText(fixture.Config) == "user changed this config" && File.Exists(fixture.State), "Uninstall removes a clean owned DLL while preserving a changed configuration and remaining ownership");
        Check(removed.Messages.Any(message => message.Contains(SelectionLoopTransaction.ConfigName) && message.Contains("preserved")), "The partial uninstall explains exactly which externally changed file was preserved");
        File.WriteAllBytes(fixture.Config, installedConfig);
        Check(Remove(fixture).Success && !File.Exists(fixture.Config) && !File.Exists(fixture.State), "A later uninstall can finish once the remaining owned configuration is restored");
        Fixture changed = Make("upgrade-preserves-user-edit"); Check(Install(changed).Success, "User-change upgrade fixture installs");
        File.WriteAllText(changed.Plugin, "external replacement"); byte[] state = File.ReadAllBytes(changed.State);
        Check(!Install(changed).Success && File.ReadAllText(changed.Plugin) == "external replacement" && File.ReadAllBytes(changed.State).SequenceEqual(state), "Reinstallation never overwrites an externally replaced owned companion");
    }

    private static void Upgrades()
    {
        Fixture fixture = Make("upgrade"); File.WriteAllText(fixture.Plugin, "before first install"); File.WriteAllText(fixture.Config, "before first config");
        Check(Install(fixture).Success, "First optional version installs before upgrade");
        fixture.Payload = Encoding.ASCII.GetBytes("MZ second optional DLL version"); SavePackage(fixture);
        Check(Install(fixture).Success && File.ReadAllBytes(fixture.Plugin).SequenceEqual(fixture.Payload), "An unchanged owned companion upgrades atomically to a newly verified package");
        Check(Remove(fixture).Success && File.ReadAllText(fixture.Plugin) == "before first install" && File.ReadAllText(fixture.Config) == "before first config", "Upgrade retains the first pre-install backup for exact uninstall restoration");
    }

    private static void RollbackFailures()
    {
        foreach (int boundary in new[] { 0, 1, 2 })
        {
            Fixture fixture = Make("rollback-" + boundary); File.WriteAllText(fixture.Plugin, "previous DLL"); File.WriteAllText(fixture.Config, "previous config");
            SelectionLoopInstallReport failed = SelectionLoopTransaction.Install(fixture.Package, fixture.Executable, () => true, (index, name) => { if (index == boundary) throw new IOException("injected install boundary"); });
            Check(!failed.Success && File.ReadAllText(fixture.Plugin) == "previous DLL" && File.ReadAllText(fixture.Config) == "previous config" && !File.Exists(fixture.State), "Failure at installation boundary " + boundary + " preserves or restores all original companion bytes");
            Check(boundary == 0 || failed.RolledBack, "A partial write reports completed rollback at boundary " + boundary);
        }
        Fixture absent = Make("rollback-new-files");
        SelectionLoopInstallReport newFailed = SelectionLoopTransaction.Install(absent.Package, absent.Executable, () => true, (index, name) => { if (index == 2) throw new IOException("state write failed"); });
        Check(!newFailed.Success && newFailed.RolledBack && !File.Exists(absent.Plugin) && !File.Exists(absent.Config) && !File.Exists(absent.State), "Ownership-write failure removes newly added companion files instead of leaving an untracked partial install");
        Fixture external = Make("external-change-during-install"); File.WriteAllText(external.Config, "snapshot config");
        SelectionLoopInstallReport changed = SelectionLoopTransaction.Install(external.Package, external.Executable, () => true, (index, name) => { if (index == 1) File.WriteAllText(external.Config, "new external edit"); });
        Check(!changed.Success && changed.RolledBack && !File.Exists(external.Plugin) && File.ReadAllText(external.Config) == "new external edit", "A destination edited after its snapshot is preserved while earlier installer writes roll back");
        Fixture changedApplied = Make("external-edit-before-rollback"); File.WriteAllText(changedApplied.Plugin, "before DLL");
        SelectionLoopInstallReport preserved = SelectionLoopTransaction.Install(changedApplied.Package, changedApplied.Executable, () => true, (index, name) => {
            if (index == 1) { File.WriteAllText(changedApplied.Plugin, "externally replaced after install"); throw new IOException("stop before config"); }
        });
        Check(!preserved.Success && !preserved.RolledBack && File.ReadAllText(changedApplied.Plugin) == "externally replaced after install"
            && preserved.Messages.Any(message => message.Contains("preserved")), "Rollback preserves an external replacement of a previously written file and reports incomplete restoration");
        Fixture processQuery = Make("process-query-failure");
        SelectionLoopInstallReport queryFailed = SelectionLoopTransaction.Install(processQuery.Package, processQuery.Executable, () => true,
            (index, name) => { if (index == 1) throw new System.ComponentModel.Win32Exception("process enumeration failed"); });
        Check(!queryFailed.Success && queryFailed.RolledBack && !File.Exists(processQuery.Plugin) && !File.Exists(processQuery.Config),
            "A process-enumeration failure after the first write triggers rollback rather than bypassing the transaction");
        Fixture stale = Make("stale-package");
        SelectionLoopInstallReport staleResult = SelectionLoopTransaction.Install(stale.Package, stale.Executable, () => true, (index, name) => { if (index == 1) File.WriteAllText(Path.Combine(stale.Package, SelectionLoopTransaction.PluginName), "source changed"); });
        Check(!staleResult.Success && staleResult.RolledBack && !File.Exists(stale.Plugin) && !File.Exists(stale.Config), "A package changed after validation cannot leave a partial installed companion");
        Fixture remove = Make("uninstall-rollback"); Check(Install(remove).Success, "Uninstall failure fixture installs");
        byte[] dll = File.ReadAllBytes(remove.Plugin), cfg = File.ReadAllBytes(remove.Config), ownership = File.ReadAllBytes(remove.State);
        SelectionLoopInstallReport failedRemove = SelectionLoopTransaction.Uninstall(remove.Executable, () => true, (index, name) => { if (index == 1) throw new IOException("injected uninstall boundary"); });
        Check(!failedRemove.Success && failedRemove.RolledBack && File.ReadAllBytes(remove.Plugin).SequenceEqual(dll) && File.ReadAllBytes(remove.Config).SequenceEqual(cfg) && File.ReadAllBytes(remove.State).SequenceEqual(ownership), "Failed uninstall rolls removed files back before reporting failure");
    }

    private static void RejectedPackages()
    {
        foreach (string mutation in new[] { "hash", "filename", "schema", "boolean-schema", "size", "extra", "duplicate", "large", "invalid-json", "enabled", "duplicate-config", "other-config", "empty-config", "nul-config", "unknown-original", "missing-original" })
        {
            Fixture fixture = Make("reject-" + mutation);
            string manifest = Path.Combine(fixture.Package, "package.json"), cfg = Path.Combine(fixture.Package, SelectionLoopTransaction.ConfigName);
            switch (mutation)
            {
                case "hash": File.WriteAllText(manifest, Manifest(fixture.Payload, new string('0', 64))); break;
                case "filename": File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(SelectionLoopTransaction.PluginName, "../other.dlp")); break;
                case "schema": File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"schema\":1", "\"schema\":2")); break;
                case "boolean-schema": File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"schema\":1", "\"schema\":true")); break;
                case "size": File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"size\":" + fixture.Payload.Length, "\"size\":1")); break;
                case "extra": File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("{", "{\"extra\":1,")); break;
                case "duplicate": File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("{", "{\"schema\":1,")); break;
                case "large": fixture.Payload = new byte[4 * 1024 * 1024 + 1]; SavePackage(fixture); break;
                case "invalid-json": File.WriteAllText(manifest, "null"); break;
                case "enabled": File.WriteAllText(cfg, "enabled 1\n"); break;
                case "duplicate-config": File.WriteAllText(cfg, "enabled 0\nenabled 0\n"); break;
                case "other-config": File.WriteAllText(cfg, "enabled 0\nunknown 1\n"); break;
                case "empty-config": File.WriteAllText(cfg, ""); break;
                case "nul-config": File.WriteAllText(cfg, "# comment\0\nenabled 0\n"); break;
                case "unknown-original": File.WriteAllText(fixture.Original, "another original version"); break;
                case "missing-original": File.Delete(fixture.Original); break;
            }
            byte[] beforeOriginal = File.Exists(fixture.Original) ? File.ReadAllBytes(fixture.Original) : null;
            SelectionLoopInstallReport report = Install(fixture);
            Check(!report.Success && !File.Exists(fixture.Plugin) && !File.Exists(fixture.Config) && !File.Exists(fixture.State), "Unsafe or incompatible package rejected before companion writes: " + mutation);
            Check(beforeOriginal == null ? !File.Exists(fixture.Original) : File.ReadAllBytes(fixture.Original).SequenceEqual(beforeOriginal), "Original plugin remains untouched after rejected " + mutation);
        }
    }

    private static void LinksAndBoundaries()
    {
        foreach (string linked in new[] { "plugin-file", "plugins-folder", "backup-folder", "package-file", "manifest-file" })
        {
            Fixture fixture = Make("link-" + linked); string outside = Path.Combine(temporary, "outside-" + linked); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep"), "outside unchanged");
            if (linked == "plugin-file") File.CreateSymbolicLink(fixture.Plugin, Path.Combine(outside, "keep"));
            else if (linked == "plugins-folder") { Directory.Move(Path.Combine(fixture.Game, "Plugins"), Path.Combine(outside, "Plugins")); Directory.CreateSymbolicLink(Path.Combine(fixture.Game, "Plugins"), Path.Combine(outside, "Plugins")); }
            else if (linked == "backup-folder") Directory.CreateSymbolicLink(Path.Combine(fixture.Game, "Plugins", "GorillaSelectionLoopBackups"), outside);
            else { string file = Path.Combine(fixture.Package, linked == "package-file" ? SelectionLoopTransaction.PluginName : "package.json"); string destination = Path.Combine(outside, "linked-source"); File.Move(file, destination); File.CreateSymbolicLink(file, destination); }
            Check(!Install(fixture).Success && File.ReadAllText(Path.Combine(outside, "keep")) == "outside unchanged", "Linked child target/source is rejected without external writes: " + linked);
        }
        Fixture game = Make("selected-game-root-link"); string alias = Path.Combine(temporary, "selected-game-alias"); Directory.CreateSymbolicLink(alias, game.Game);
        string exe = Path.Combine(alias, "WWE2K26_x64.exe");
        Check(SelectionLoopTransaction.Install(game.Package, exe, () => true, null).Success && SelectionLoopTransaction.Uninstall(exe, () => true, null).Success, "A deliberately selected game-root junction is allowed while child links remain rejected");
        Fixture backup = Make("tampered-backup"); File.WriteAllText(backup.Plugin, "original optional plugin"); Check(Install(backup).Success, "Backup-integrity fixture installs");
        string backupFile = Directory.GetFiles(Path.Combine(backup.Game, "Plugins", "GorillaSelectionLoopBackups"), SelectionLoopTransaction.PluginName + ".before.bak", SearchOption.AllDirectories).Single(); File.WriteAllText(backupFile, "changed backup");
        byte[] installed = File.ReadAllBytes(backup.Plugin);
        Check(!Remove(backup).Success && File.ReadAllBytes(backup.Plugin).SequenceEqual(installed), "Uninstall refuses a modified original backup before replacing current files");
        Fixture owner = Make("ownership-backup-escape"); File.WriteAllText(owner.Plugin, "before optional module"); Check(Install(owner).Success, "Ownership-path guard fixture installs");
        SelectionLoopOwnership record = JsonSerializer.Deserialize<SelectionLoopOwnership>(File.ReadAllText(owner.State));
        record.Files.Single(file => file.Name == SelectionLoopTransaction.PluginName).OriginalBackup = "../../../outside.bak";
        File.WriteAllText(owner.State, JsonSerializer.Serialize(record)); byte[] current = File.ReadAllBytes(owner.Plugin);
        Check(!Remove(owner).Success && File.ReadAllBytes(owner.Plugin).SequenceEqual(current), "A forged ownership backup path cannot authorize writes outside the fixed companion files");
    }

    private static void ClosedGame()
    {
        Fixture fixture = Make("closed-game");
        Check(!SelectionLoopTransaction.Install(fixture.Package, fixture.Executable, () => false, null).Success && !File.Exists(fixture.Plugin), "Installation requires the game to be closed");
        Check(Install(fixture).Success && !SelectionLoopTransaction.Uninstall(fixture.Executable, () => false, null).Success && File.Exists(fixture.Plugin), "Uninstall also requires the game to be closed");
        Fixture started = Make("game-started-during-install"); bool closed = true;
        SelectionLoopInstallReport report = SelectionLoopTransaction.Install(started.Package, started.Executable, () => closed, (index, name) => { if (index == 1) closed = false; });
        Check(!report.Success && report.RolledBack && !File.Exists(started.Plugin) && !File.Exists(started.Config), "A game start detected between writes stops the operation and rolls prior writes back");
    }

    private sealed class Fixture
    {
        internal string Game, Package; internal byte[] Payload;
        internal string Executable { get { return Path.Combine(Game, "WWE2K26_x64.exe"); } }
        internal string Original { get { return Path.Combine(Game, "Plugins", SelectionLoopTransaction.OriginalPluginName); } }
        internal string Plugin { get { return Path.Combine(Game, "Plugins", SelectionLoopTransaction.PluginName); } }
        internal string Config { get { return Path.Combine(Game, "Plugins", SelectionLoopTransaction.ConfigName); } }
        internal string State { get { return Path.Combine(Game, "Plugins", SelectionLoopTransaction.OwnershipName); } }
    }
    private static Fixture Make(string name)
    {
        var result = new Fixture { Game = Path.Combine(temporary, name, "Game"), Package = Path.Combine(temporary, name, "Package"), Payload = Encoding.ASCII.GetBytes("MZ optional companion fixture") };
        Directory.CreateDirectory(Path.Combine(result.Game, "Plugins", "GorillaRenders")); Directory.CreateDirectory(result.Package);
        File.WriteAllBytes(result.Executable, Encoding.ASCII.GetBytes("game executable fixture")); File.WriteAllBytes(result.Original, original);
        File.WriteAllText(Path.Combine(result.Game, "Plugins", "GorillaRenders", "1727_fixture.gmr"), "existing render"); File.WriteAllText(Path.Combine(result.Game, "roster.json"), "existing roster");
        File.WriteAllText(Path.Combine(result.Game, "Plugins", "WWE2K26_MovingRenders.cfg"), "original plugin settings");
        SavePackage(result); return result;
    }
    private static void SavePackage(Fixture fixture) { File.WriteAllBytes(Path.Combine(fixture.Package, SelectionLoopTransaction.PluginName), fixture.Payload); File.WriteAllText(Path.Combine(fixture.Package, "package.json"), Manifest(fixture.Payload)); File.WriteAllText(Path.Combine(fixture.Package, SelectionLoopTransaction.ConfigName), "# optional experimental prefix loop\nenabled 0\n"); }
    private static string Manifest(byte[] bytes, string hash = null) { return "{\"schema\":1,\"filename\":\"" + SelectionLoopTransaction.PluginName + "\",\"sha256\":\"" + (hash ?? Sha(bytes)) + "\",\"size\":" + bytes.Length + "}"; }
    private static SelectionLoopInstallReport Install(Fixture fixture) { return SelectionLoopTransaction.Install(fixture.Package, fixture.Executable, () => true, null); }
    private static SelectionLoopInstallReport Remove(Fixture fixture) { return SelectionLoopTransaction.Uninstall(fixture.Executable, () => true, null); }
    private static string Sha(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static Dictionary<string, byte[]> Snapshot(string directory) { return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path.Substring(directory.Length), File.ReadAllBytes); }
    private static bool Unchanged(Dictionary<string, byte[]> before, string directory) { var after = Snapshot(directory); return before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && pair.Value.SequenceEqual(after[pair.Key])); }
    private static void Check(bool value, string description) { if (!value) throw new Exception(description); passed++; Console.WriteLine("PASS " + description); }
}
