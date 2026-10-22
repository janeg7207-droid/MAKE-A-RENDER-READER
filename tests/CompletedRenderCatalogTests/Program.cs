using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root, output, batches, game, installed, app;
    private static int checks;
    private const string Map = "0123456789ABCDEF 122 610\n";

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-completed-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Reset();
            var absent = Scan();
            Check(absent.Entries.Count == 0 && absent.Complete, "Empty output and batch roots are readable without creating output or game files");
            string pac = Save(Path.Combine(output, "924_PAC.gmr"), 924, "PAC", "Cruiser King 3");
            string hardy = Save(Path.Combine(output, "695_Jeff_Hardy.gmr"), 695, "JEFF HARDY '02", "Jeff Hardy 1");
            var before = Snapshot(root);
            var found = Scan();
            var row = found.Entries.Single(item => item.WrestlerId == 924);
            Check(found.Entries.Count == 2 && found.Entries.All(item => item.IsValid), "Saved full renders load without any selected wrestler, model or taunt");
            Check(row.Name == "PAC" && row.Taunt == "Cruiser King 3" && row.Screens == "both" && row.Ending == "loop" && row.Frames == 2,
                "Display data comes from the completed movie metadata");
            Check(found.Entries.Select(item => item.Name).SequenceEqual(new[] { "JEFF HARDY '02", "PAC" }), "Recovered results are sorted by wrestler name");
            Check(found.Entries.All(item => item.InstalledState == CompletedRenderInstallState.NotInstalled), "Absent installed ID counterparts are reported without movie hashing");
            Check(found.ComparisonBytes == 0 && Same(before, root), "Metadata-only inventory preserves every source and game byte");
            Check(CompletedRenderCatalog.Scan(new[] { pac, pac }, game, CancellationToken.None).Entries.Count == 1, "Explicit Add files inputs are deduplicated by exact source path");
            Check(CompletedRenderCatalog.Scan(new[] { pac }, null, CancellationToken.None).Entries.Single().InstalledState == CompletedRenderInstallState.NeedsComparison,
                "No configured game does not falsely imply the movie is uninstalled");

            Directory.CreateDirectory(installed);
            File.Copy(pac, Path.Combine(installed, "renamed_movie.gmr"));
            found = Scan(); row = found.Entries.Single(item => item.WrestlerId == 924);
            Check(row.InstalledState == CompletedRenderInstallState.InstalledIdentical && row.InstalledPath.EndsWith("renamed_movie.gmr"),
                "Exact installed bytes are recognized under another filename by metadata ID");
            Check(found.ComparisonBytes == new FileInfo(pac).Length * 2, "Exact installed status reads both complete files rather than trusting timestamps");
            byte[] altered = File.ReadAllBytes(pac); altered[56] ^= 23; File.WriteAllBytes(Path.Combine(installed, "renamed_movie.gmr"), altered);
            File.SetLastWriteTimeUtc(Path.Combine(installed, "renamed_movie.gmr"), File.GetLastWriteTimeUtc(pac));
            Check(Scan().Entries.Single(item => item.WrestlerId == 924).InstalledState == CompletedRenderInstallState.InstalledDifferent,
                "Equal length and timestamp do not hide different movie bytes");
            File.Copy(pac, Path.Combine(installed, "924_second.gmr"));
            row = Scan().Entries.Single(item => item.WrestlerId == 924);
            Check(row.InstalledState == CompletedRenderInstallState.NeedsComparison && row.InstallationDetails.Contains("Multiple"),
                "Duplicate installed ID claims are not mislabeled as one proven active installed movie");
            Directory.Delete(installed, true); Directory.CreateDirectory(installed);
            Save(Path.Combine(installed, "100_alias.gmr"), 100, "Other", "Other", aliases: new[] { 695 });
            Check(Scan().Entries.Single(item => item.WrestlerId == 695).InstalledState == CompletedRenderInstallState.InstalledDifferent,
                "An installed alias claims the logical wrestler slot independently of filenames");
            Directory.Delete(installed, true); Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "102_unreadable.gmr"), "not a movie");
            Check(Scan().Entries.Single(item => item.WrestlerId == 695).InstalledState == CompletedRenderInstallState.NeedsComparison,
                "Unreadable installed metadata cannot rule out unknown alias claims using its filename alone");

            Reset();
            string batch = Path.Combine(batches, "Batch-old"), workspace = Path.Combine(batch, "job-122");
            string original = Save(Path.Combine(workspace, "122_AVERY.gmr"), 122, "AVERY STYLES", "AJ Styles 1");
            string published = Path.Combine(output, "122_AVERY_attire01_Blue.gmr"); File.Copy(original, published);
            File.WriteAllText(Path.Combine(workspace, "_roster_pictures.txt"), Map);
            File.WriteAllText(original + ".selection-loop.json", "original sidecar remains intact");
            Manifest(batch, output, new[] { Job(122, workspace, original, published, "Blue") });
            before = Snapshot(root); found = Scan(); row = found.Entries.Single();
            Check(found.Entries.Count == 1 && row.Path == original && row.PublishedPath == published, "Manifest recovery prefers the proven workspace and collapses its exact published copy");
            Check(row.OutputDirectory == workspace && row.HasRosterPictureMap && row.HasSelectionLoopPolicy && row.AttireName == "Blue", "Recovered source keeps its own picture map, sidecar and saved attire label");
            Check(row.SourceDescription == "Recovered batch" && Same(before, root), "Reopening an old manifest changes no saved file or settings");
            WritePlugin();
            var install = BatchRenderInstaller.Install(app, game, new[] { ToJob(row) });
            Check(install.Installed == 1 && install.Failed.Count == 0, "A recovered result installs through the existing installer with no models or current roster");
            Check(File.ReadAllBytes(Path.Combine(installed, "122_AVERY.gmr")).SequenceEqual(File.ReadAllBytes(original)), "Recovered installation preserves the exact completed movie bytes");
            Check(File.ReadAllText(Path.Combine(installed, "_roster_pictures.txt")) == Map, "Recovered installation uses the original workspace picture map");
            Check(Hash(original) == before[Path.GetRelativePath(root, original)] && Hash(published) == before[Path.GetRelativePath(root, published)], "Installation does not rerender or alter original and published movies");
            Check(Scan().Entries.Single().InstalledState == CompletedRenderInstallState.InstalledIdentical, "A fresh scan recognizes the recovered movie's installed bytes");

            Directory.Delete(workspace, true);
            found = Scan(); row = found.Entries.Single();
            Check(row.Path == published && row.IsValid && !row.HasRosterPictureMap, "A published movie remains recoverable after its original workspace is unavailable");
            Check(found.Warnings.Any(note => note.Contains("picture map is unavailable")), "Missing original shared-picture context is explained without inventing the current roster map");
            Check(row.InstalledState == CompletedRenderInstallState.InstalledIdentical, "A source-context warning does not invalidate a separate exact installed-byte comparison");

            Reset(); batch = Path.Combine(batches, "Batch-invalid"); workspace = Path.Combine(batch, "job-122");
            string outside = Save(Path.Combine(root, "outside", "122_unrelated.gmr"), 122);
            Manifest(batch, output, new[] { Job(122, Path.GetDirectoryName(outside), outside, null, "Forged") });
            found = Scan();
            Check(found.Entries.Count == 0 && found.Warnings.Any(note => note.Contains("outside its proven")), "A manifest cannot import an unrelated source outside its own workspace");
            original = Save(Path.Combine(workspace, "122_Safe.gmr"), 122);
            string externalPublished = Save(Path.Combine(root, "unselected-output", "122_Published.gmr"), 122);
            Manifest(batch, Path.GetDirectoryName(externalPublished), new[] { Job(122, workspace, original, externalPublished, "Blue") });
            found = Scan();
            Check(found.Entries.Count == 1 && found.Entries[0].Path == original && found.Warnings.Any(note => note.Contains("outside the selected")), "Unselected published paths are not followed through a manifest");
            Manifest(batch, output, new[] { Job(999, workspace, original, null, "Wrong slot") });
            found = Scan();
            Check(found.Entries.Single().WrestlerId == 122 && found.Warnings.Any(note => note.Contains("manifest ID")), "A manifest cannot change the movie's logical wrestler ID");
            Manifest(batch, output, new[] { Job(122, workspace, original, null, "Canceled", "Canceled") });
            Check(Scan().Entries.Single().SourceDescription == "Recovered batch", "Valid completed movie bytes are recovered even when the last saved job status says Canceled");
            string manifestPath = Path.Combine(batch, "batch-results.json");
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(",\"Status\":\"Canceled\"", ""));
            Check(Scan().Entries.Single().IsValid, "A stale manifest without a job Status does not hide a verified completed movie");
            string partial = Path.Combine(workspace, "610_interrupted.gmr"); File.WriteAllBytes(partial, new byte[] { 1, 2, 3 });
            Manifest(batch, output, new[] { Job(122, workspace, original, null, "Blue", "Canceled"), Job(610, workspace, partial, null, "Partial", "Canceled") });
            before = Snapshot(root); found = Scan();
            Check(found.Entries.Count(item => item.IsValid) == 1 && found.Entries.Single(item => item.Path == partial).Error != null && Same(before, root),
                "A canceled batch recovers its finished movie while keeping interrupted output disabled and every source unchanged");

            Reset(); batch = Path.Combine(batches, "Batch-with-frames"); workspace = Path.Combine(batch, "job-122");
            original = Save(Path.Combine(workspace, "work", "results", "122_finished.gmr"), 122);
            string ordinary = Save(Path.Combine(output, "610_ordinary.gmr"), 610);
            string frames = Path.Combine(workspace, "work", "frames"); Directory.CreateDirectory(frames);
            for (int n = 0; n < 8200; n++) File.WriteAllBytes(Path.Combine(frames, n.ToString("D5") + ".png"), Array.Empty<byte>());
            Manifest(batch, output, new[] { Job(122, workspace, original, null, "Blue", "Canceled") });
            found = Scan();
            Check(found.Entries.Count(item => item.IsValid) == 2 && found.Entries.Any(item => item.Path == ordinary) && found.Entries.Any(item => item.Path == original),
                "Ordinary output and manifest-targeted finished files survive a batch containing thousands of cached frames");
            Check(found.Complete && found.Warnings.Any(note => note.Contains("cache/frame")), "Generated batch caches are skipped explicitly instead of consuming the completed-file inventory limit");

            Reset();
            string misleading = Save(Path.Combine(output, "999_Actual122.gmr"), 122);
            row = Scan().Entries.Single();
            Check(!row.IsValid && row.WrestlerId == 122 && row.Error.Contains("122_"), "An ID-mismatched filename is unavailable with the existing installer's exact requirement");
            File.Delete(misleading); Save(Path.Combine(output, "plain_name.gmr"), 122);
            Check(!Scan().Entries.Single().IsValid, "A prefixless movie is shown but cannot bypass the existing installer filename rule");
            File.WriteAllBytes(Path.Combine(output, "610_incomplete.gmr"), new byte[] { 1, 2, 3 });
            Check(Scan().Entries.Single(item => item.Path.EndsWith("610_incomplete.gmr")).Error != null, "Incomplete GMRs stay visible with a reason instead of becoming installable results");
            Save(Path.Combine(output, "777_wrongmetadata.gmr"), 777, rawId: "7.77e2");
            Check(!Scan().Entries.Single(item => item.Path.EndsWith("777_wrongmetadata.gmr")).IsValid, "Exponent-spelled metadata IDs cannot normalize into another native slot");
            Save(Path.Combine(output, "778_body.gmr"), 778, tauntObject: true);
            Check(Scan().Entries.Single(item => item.WrestlerId == 778).Taunt == "Fixture taunt", "Native object-shaped taunt metadata keeps its actual display name");
            byte[] bad = File.ReadAllBytes(Path.Combine(output, "778_body.gmr")); bad[44] = 56; File.WriteAllBytes(Path.Combine(output, "778_body.gmr"), bad);
            Check(!Scan().Entries.Single(item => item.WrestlerId == 778 || item.Path.EndsWith("778_body.gmr")).IsValid, "Dependent-frame output cannot bypass the full-render installer verification");
            File.WriteAllText(Path.Combine(output, ".partial.gmr.tmp"), "in progress");
            Check(Scan().Entries.All(item => !item.Path.EndsWith(".tmp")), "Temporary partial writes are excluded from completed output discovery");

            Reset();
            original = Save(Path.Combine(output, "122_first.gmr"), 122);
            string second = Save(Path.Combine(output, "122_second.gmr"), 122, "Second attire");
            found = Scan(); WritePlugin(); before = Snapshot(game);
            install = BatchRenderInstaller.Install(app, game, found.Entries.Select(ToJob));
            Check(install.Installed == 0 && install.Failed.Any(note => note.Contains("Choose one")) && Same(before, game), "Two recovered attires for one ID are rejected before any game write");
            Save(Path.Combine(output, "610_secondslot.gmr"), 610);
            File.WriteAllText(Path.Combine(output, "_roster_pictures.txt"), Map);
            string otherFolder = Path.Combine(root, "other-source"); Save(Path.Combine(otherFolder, "888_other.gmr"), 888);
            found = CompletedRenderCatalog.Scan(new[] { output, otherFolder }, game, CancellationToken.None); before = Snapshot(game);
            install = BatchRenderInstaller.Install(app, game, found.Entries.Where(item => item.WrestlerId == 610 || item.WrestlerId == 888).Select(ToJob));
            Check(install.Installed == 0 && install.Failed.Any(note => note.Contains("roster picture maps")) && Same(before, game), "Recovered selections preserve the installer's strict mixed-map context rejection");

            Reset();
            string ownGameMovie = Save(Path.Combine(installed, "122_installed.gmr"), 122);
            found = CompletedRenderCatalog.Scan(new[] { game }, game, CancellationToken.None);
            Check(found.Entries.Count == 0, "The installed game cannot be selected as a recovery source");
            found = CompletedRenderCatalog.Scan(new[] { root }, game, CancellationToken.None);
            Check(found.Entries.All(item => item.Path != ownGameMovie), "Choosing an ancestor source directory does not scan the installed game subtree");
            string linked = Path.Combine(output, "linked"); Directory.CreateSymbolicLink(linked, installed);
            Check(Scan().Entries.Count == 0 && Scan().Warnings.Any(note => note.Contains("linked")), "Linked source directories are not followed into installed or external files");
            using (var stopped = new CancellationTokenSource())
            {
                stopped.Cancel(); bool canceled = false;
                try { CompletedRenderCatalog.Scan(new[] { output }, game, stopped.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Closing before catalog loading cancels without publishing a partial result");
            }

            Reset();
            string sparse = SaveSparse(Path.Combine(output, "122_large.gmr"), 122, 600L * 1024 * 1024);
            SaveSparse(Path.Combine(installed, "122_large.gmr"), 122, 600L * 1024 * 1024);
            found = Scan(); row = found.Entries.Single();
            Check(row.IsValid && row.InstalledState == CompletedRenderInstallState.NeedsComparison && found.ComparisonBytes == 0,
                "Large counterpart movies remain honestly unverified without exceeding the exact comparison read budget");
            Check(found.Warnings.Any(note => note.Contains("byte limit")), "The bounded comparison fallback is explained");

            if (args.Length == 1) Native(args[0]);
            Console.WriteLine(checks + " completed render recovery checks passed."); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Native(string actual)
    {
        Reset();
        string native = Path.Combine(output, "161_native.gmr"); File.Copy(actual, native);
        var row = Scan().Entries.Single();
        Check(row.IsValid && row.WrestlerId == 161 && row.Taunt == "Synthetic end audit fixture", "Actual original-native encoded GMR is recognized without rendering or model preparation");
        uint videoFrames = BitConverter.ToUInt32(File.ReadAllBytes(native), 8);
        Check(SelectionLoopPolicy.TryWrite(native, 161, new Dictionary<string, object> { { "id", 161 }, { "end", "freeze" }, { "frames", (int)videoFrames - 11 } }), "Actual native freeze movie receives its source-verified selection-loop policy");
        string originalHash = Hash(native), policyHash = Hash(native + ".selection-loop.json");
        row = Scan().Entries.Single(); WritePlugin();
        var report = BatchRenderInstaller.Install(app, game, new[] { ToJob(row) });
        string destination = Path.Combine(installed, Path.GetFileName(native));
        Check(report.Installed == 1 && report.Failed.Count == 0 && Hash(destination) == originalHash, "Actual completed native movie installs with byte-for-byte preservation");
        Check(SelectionLoopPolicy.TryCopy(native, destination, row.WrestlerId), "Recovered native result preserves its validated selection-loop policy through the existing policy copier");
        Check(Hash(native) == originalHash && Hash(native + ".selection-loop.json") == policyHash && Hash(destination + ".selection-loop.json") == policyHash,
            "Native completed source, source policy and installed policy remain exact");
        Check(Scan().Entries.Single().InstalledState == CompletedRenderInstallState.InstalledIdentical, "Fresh catalog confirms actual native installed bytes without claiming game playback");
    }

    private static CompletedRenderCatalogReport Scan() { return CompletedRenderCatalog.Scan(new[] { output, batches }, game, CancellationToken.None); }
    private static BatchRenderJob ToJob(CompletedRenderEntry entry) { return new BatchRenderJob { Entry = new RosterEntry { Id = entry.WrestlerId, Name = entry.Name }, RenderPath = entry.Path, OutputDirectory = entry.OutputDirectory, Status = "Complete" }; }
    private static object Job(int id, string workspace, string gmr, string published, string attire, string status = "Complete")
    { return new { id, name = "Saved name", workspaceDirectory = workspace, gmr, publishedGmr = published, attireName = attire, Status = status }; }
    private static void Manifest(string batch, string publishRoot, object[] jobs)
    {
        Directory.CreateDirectory(batch);
        File.WriteAllText(Path.Combine(batch, "batch-results.json"), JsonSerializer.Serialize(new { version = 2, workspaceDirectory = batch, outputDirectory = publishRoot, jobs }));
    }
    private static void Reset()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        output = Path.Combine(root, "normal output"); batches = Path.Combine(root, "batches"); game = Path.Combine(root, "game");
        installed = Path.Combine(game, "Plugins", "GorillaRenders"); app = Path.Combine(root, "app");
        Directory.CreateDirectory(output); Directory.CreateDirectory(batches); Directory.CreateDirectory(Path.Combine(game, "Plugins"));
    }
    private static void WritePlugin()
    {
        Directory.CreateDirectory(Path.Combine(app, "plugin"));
        File.WriteAllText(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.dlp"), "fixture MOVINGRENDERS-4.21");
        File.WriteAllText(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.cfg"), "enabled 1\n");
    }
    private static string Save(string path, int id, string name = "Fixture", string taunt = "Fixture taunt", int[] aliases = null, string rawId = null, bool tauntObject = false)
    {
        var metadata = new Dictionary<string, object> { { "format", "GorillaRender" }, { "version", 1 }, { "wrestler_id", id }, { "name", name },
            { "taunt", tauntObject ? (object)new { name = taunt, clip = "fixture" } : taunt }, { "screens", "both" }, { "end", "loop" }, { "extra_ids", aliases ?? new int[0] } };
        string json = JsonSerializer.Serialize(metadata);
        if (rawId != null) json = json.Replace("\"wrestler_id\":" + id, "\"wrestler_id\":" + rawId);
        return SaveBytes(path, Encoding.UTF8.GetBytes(json), 64);
    }
    private static string SaveSparse(string path, int id, long size)
    { return SaveBytes(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = "GorillaRender", version = 1, wrestler_id = id, name = "Large fixture", taunt = "Fixture", screens = "both", end = "loop" })), size); }
    private static string SaveBytes(string path, byte[] json, long videoSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var stream = File.Create(path)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint word in new uint[] { (uint)videoSize - 8, 2, (uint)videoSize - 56, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, (uint)videoSize }) writer.Write(word);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            stream.Position = videoSize; writer.Write(json); writer.Write((uint)json.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static Dictionary<string, string> Snapshot(string path)
    { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(path, file), Hash); }
    private static bool Same(Dictionary<string, string> before, string path)
    { var after = Snapshot(path); return before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var hash) && hash == pair.Value); }
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); checks++; Console.WriteLine("PASS " + message); }
}
