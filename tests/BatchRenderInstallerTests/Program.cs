using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary, app;
    private static int passed;
    private const string Map = "0123456789ABCDEF 122 610\n";

    private static int Main()
    {
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-batch-install-" + Guid.NewGuid().ToString("N"));
        app = Path.Combine(temporary, "app");
        try
        {
            Write(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.dlp"), "fixture MOVINGRENDERS-4.26 plugin");
            Write(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.cfg"), "enabled 1\ncustom_default 7\n");
            var appBefore = Snapshot(app);
            BatchRenderJob one = Job(122, "Current", Map), two = Job(610, "Second", Map);
            var sourceBefore = Snapshot(Path.Combine(temporary, "outputs"));
            string game = Game("fresh");
            var installed = BatchRenderInstaller.Install(app, game, new[] { one, two });
            Check(installed.Installed == 2 && installed.Failed.Count == 0, "Both verified jobs are installed");
            string renders = Path.Combine(game, "Plugins", "GorillaRenders");
            Check(File.ReadAllBytes(Path.Combine(renders, Path.GetFileName(one.RenderPath))).SequenceEqual(File.ReadAllBytes(one.RenderPath)), "Installed GMR bytes match the completed source");
            Check(File.ReadAllText(Path.Combine(renders, "_roster_pictures.txt")) == Map, "One agreed roster picture map is installed");
            Check(File.ReadAllBytes(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp")).SequenceEqual(File.ReadAllBytes(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.dlp"))), "A missing plugin is installed from the application");
            Check(SameSnapshot(sourceBefore, Path.Combine(temporary, "outputs")), "All source renders and maps remain unchanged");
            var repeated = BatchRenderInstaller.Install(app, game, new[] { one, two });
            Check(repeated.Installed == 2 && repeated.Failed.Count == 0 && repeated.BackupDirectory == null, "Repeating an identical batch creates no replacement backups");
            Check(Directory.GetFiles(renders, "*.gmr").Length == 2, "Repeating the batch does not duplicate active renders");

            Write(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp"), "old MOVINGRENDERS-4.20 plugin");
            Write(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.cfg"), "enabled 0\nold_custom 33\n");
            Write(Path.Combine(renders, "122_Old.gmr"), "older installed video");
            Write(Path.Combine(renders, "_roster_pictures.txt"), "1111111111111111 122\n");
            byte[] oldCurrent = File.ReadAllBytes(Path.Combine(renders, "122_Current.gmr"));
            BatchRenderJob revised = Job(122, "Current", Map, 71, "revised");
            var upgraded = BatchRenderInstaller.Install(app, game, new[] { revised });
            Check(upgraded.Installed == 1 && upgraded.Failed.Count == 0 && Directory.Exists(upgraded.BackupDirectory), "Updating a slot and outdated plugin retains backups");
            Check(!File.Exists(Path.Combine(renders, "122_Old.gmr")), "Superseded same-slot renders are retired from active files");
            string[] backups = Directory.GetFiles(upgraded.BackupDirectory, "*", SearchOption.AllDirectories);
            Check(backups.Length == 5 && backups.All(path => path.EndsWith(".bak", StringComparison.Ordinal)), "Previous GMRs, plugin, configuration and picture map use inactive backup extensions");
            Check(File.ReadAllBytes(backups.Single(path => path.EndsWith("122_Current.gmr.bak", StringComparison.Ordinal))).SequenceEqual(oldCurrent), "The replaced current GMR has an exact original backup");
            Check(File.ReadAllText(backups.Single(path => path.EndsWith("WWE2K26_MovingRenders.cfg.bak", StringComparison.Ordinal))).Contains("old_custom 33"), "Original plugin settings survive in their backup");
            Check(File.ReadAllText(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.cfg")) == File.ReadAllText(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.cfg")), "An outdated plugin receives the matching supplied configuration");
            Check(File.ReadAllBytes(Path.Combine(renders, "122_Current.gmr")).SequenceEqual(File.ReadAllBytes(revised.RenderPath)), "The replacement source is installed completely");

            string preservedGame = Game("compatible");
            Write(Path.Combine(preservedGame, "Plugins", "WWE2K26_MovingRenders.dlp"), "existing MOVINGRENDERS-5.1 custom plugin");
            Write(Path.Combine(preservedGame, "Plugins", "WWE2K26_MovingRenders.cfg"), "enabled 1\nkeep_my_option 99\n");
            byte[] pluginBefore = File.ReadAllBytes(Path.Combine(preservedGame, "Plugins", "WWE2K26_MovingRenders.dlp"));
            var preserved = BatchRenderInstaller.Install(app, preservedGame, new[] { one });
            Check(preserved.Installed == 1 && preserved.Failed.Count == 0, "Compatible plugin installation succeeds");
            Check(File.ReadAllBytes(Path.Combine(preservedGame, "Plugins", "WWE2K26_MovingRenders.dlp")).SequenceEqual(pluginBefore), "A newer compatible plugin is never downgraded");
            Check(File.ReadAllText(Path.Combine(preservedGame, "Plugins", "WWE2K26_MovingRenders.cfg")).Contains("keep_my_option 99"), "Compatible plugin configuration remains untouched");

            Reject("Duplicate logical wrestler IDs abort before any game write", new[] { one, one });
            var wrong = new BatchRenderJob { Entry = new RosterEntry { Id = 123, Name = "Wrong ID" }, RenderPath = one.RenderPath, OutputDirectory = one.OutputDirectory };
            Reject("A render for another wrestler ID is rejected", new[] { wrong });
            var stale = new BatchRenderJob { Entry = one.Entry, RenderPath = one.RenderPath, OutputDirectory = two.OutputDirectory };
            Reject("A stale result from another job directory is rejected", new[] { stale });
            BatchRenderJob misleading = Job(130, "Misleading", Map, suffix: "misleading");
            string misleadingPath = Path.Combine(misleading.OutputDirectory, "131_Misleading.gmr");
            File.Move(misleading.RenderPath, misleadingPath); misleading.RenderPath = misleadingPath;
            Reject("A misleading filename cannot retire another wrestler's slot", new[] { misleading });
            BatchRenderJob invalid = Job(124, "Invalid", Map, suffix: "invalid");
            byte[] invalidBytes = File.ReadAllBytes(invalid.RenderPath); invalidBytes[0] = 0;
            File.WriteAllBytes(invalid.RenderPath, invalidBytes);
            Reject("Malformed GMR data is rejected before game files change", new[] { invalid });
            BatchRenderJob conflict = Job(125, "Conflict", "0123456789ABCDEF 125\n", suffix: "conflict");
            Reject("Conflicting roster maps abort the complete installation", new[] { one, conflict });
            BatchRenderJob missingMap = Job(126, "Missing", null, suffix: "missing-map");
            Reject("Mixed missing and present roster maps cannot overwrite the installed map", new[] { one, missingMap });
            BatchRenderJob invalidMap = Job(127, "BadMap", "not-a-hash 127\n", suffix: "bad-map");
            Reject("Malformed roster map keys are rejected", new[] { invalidMap });
            BatchRenderJob invalidOwner = Job(128, "BadOwner", "0123456789ABCDEF 65536\n", suffix: "bad-owner");
            Reject("Out-of-range picture owner IDs are rejected", new[] { invalidOwner });
            Reject("An empty job collection does not update the plugin", new BatchRenderJob[0]);

            BatchRenderJob semantic = Job(129, "Semantic", "0123456789abcdef\t610  122\r\n", suffix: "semantic");
            string semanticGame = Game("semantic");
            var equivalent = BatchRenderInstaller.Install(app, semanticGame, new[] { one, semantic });
            Check(equivalent.Installed == 2 && equivalent.Failed.Count == 0, "Equivalent map ordering, whitespace and hash case are accepted");
            string noMapGame = Game("no-map");
            var noMap = BatchRenderInstaller.Install(app, noMapGame, new[] { missingMap });
            Check(noMap.Installed == 1 && noMap.Failed.Count == 0 && !File.Exists(Path.Combine(noMapGame, "Plugins", "GorillaRenders", "_roster_pictures.txt")), "A batch with no roster map does not invent one");
            string missingPlugins = Path.Combine(temporary, "no-plugins"); Directory.CreateDirectory(missingPlugins);
            Check(BatchRenderInstaller.Install(app, missingPlugins, new[] { one }).Installed == 0 && !Directory.Exists(Path.Combine(missingPlugins, "Plugins")), "CakeHook's Plugins folder is required and is not fabricated");
            string linkedGame = Game("linked-game"), outside = Path.Combine(temporary, "outside");
            Directory.CreateDirectory(outside); Write(Path.Combine(outside, "keep.txt"), "unrelated files");
            Directory.CreateSymbolicLink(Path.Combine(linkedGame, "Plugins", "GorillaRenders"), outside);
            var linked = BatchRenderInstaller.Install(app, linkedGame, new[] { one });
            Check(linked.Installed == 0 && linked.Failed.Count > 0 && Directory.GetFiles(outside).Length == 1 && File.ReadAllText(Path.Combine(outside, "keep.txt")) == "unrelated files", "A linked render destination cannot redirect installation into another folder");
            Directory.Delete(Path.Combine(linkedGame, "Plugins", "GorillaRenders"));
            string brokenApp = Path.Combine(temporary, "broken-app"); Directory.CreateDirectory(brokenApp);
            string brokenGame = Game("broken-app-game"); var beforeMissingPlugin = Snapshot(brokenGame);
            var missingPlugin = BatchRenderInstaller.Install(brokenApp, brokenGame, new[] { one });
            Check(missingPlugin.Installed == 0 && missingPlugin.Failed.Count > 0 && SameSnapshot(beforeMissingPlugin, brokenGame), "Missing supplied plugin files cannot partially create game render files");
            Check(SameSnapshot(appBefore, app), "Application plugin resources remain immutable across all installs");
            Check(!Directory.GetFiles(temporary, "*.tmp", SearchOption.AllDirectories).Any(), "Successful and rejected installs leave no temporary copies");
            Console.WriteLine("PASS " + passed + " batch render installation checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static void Reject(string label, IEnumerable<BatchRenderJob> jobs)
    {
        string game = Game("reject-" + passed);
        Write(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp"), "old MOVINGRENDERS-4.20 plugin");
        var before = Snapshot(game);
        var result = BatchRenderInstaller.Install(app, game, jobs);
        Check(result.Installed == 0 && result.Failed.Count > 0 && SameSnapshot(before, game), label);
    }
    private static BatchRenderJob Job(int id, string name, string map, byte fill = 0, string suffix = "")
    {
        string directory = Path.Combine(temporary, "outputs", id + "-" + suffix); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, id + "_" + name + ".gmr");
        byte[] video = new byte[64];
        Put(video, 0, 0x694b4942); Put(video, 4, 56); Put(video, 8, 2); Put(video, 12, 4); Put(video, 16, 2);
        Put(video, 20, 1024); Put(video, 24, 1024); Put(video, 28, 30); Put(video, 32, 1); Put(video, 36, 0x100000);
        Put(video, 44, 57); Put(video, 48, 61); Put(video, 52, 64);
        for (int i = 56; i < video.Length; i++) video[i] = fill;
        byte[] json = Encoding.UTF8.GetBytes("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":" + id + ",\"screens\":\"both\",\"end\":\"loop\"}");
        using (var stream = File.Create(file)) using (var writer = new BinaryWriter(stream))
        { writer.Write(video); writer.Write(json); writer.Write((uint)json.Length); writer.Write(0x31524d47u); }
        if (map != null) Write(Path.Combine(directory, "_roster_pictures.txt"), map);
        return new BatchRenderJob { Entry = new RosterEntry { Id = id, Name = name }, RenderPath = file, OutputDirectory = directory, Status = "Complete" };
    }
    private static string Game(string name) { string root = Path.Combine(temporary, name); Directory.CreateDirectory(Path.Combine(root, "Plugins")); return root; }
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private static void Put(byte[] bytes, int at, uint value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (i * 8)); }
    private static Dictionary<string, byte[]> Snapshot(string root) { return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path.Substring(root.Length), File.ReadAllBytes); }
    private static bool SameSnapshot(Dictionary<string, byte[]> before, string root)
    { var after = Snapshot(root); return before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && pair.Value.SequenceEqual(after[pair.Key])); }
    private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); passed++; Console.WriteLine("PASS " + label); }
}
