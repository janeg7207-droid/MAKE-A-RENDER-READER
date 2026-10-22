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
    private const string HashOne = "0123456789ABCDEF";
    private static string root, saved, staging, game, app;
    private static int checks;

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-saved-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            Reset();
            string source = Save("122_AVERY.gmr", Json(new { render_hashes = new[] { HashOne }, render_names = new[] { "Avery original" }, shared_hashes = new[] { "FEDCBA9876543210" }, extra_ids = new[] { 610 } }));
            var before = Snapshot(saved); byte[] sourceVideo = Video(source);
            var original = Entry(source);
            var keep = Plan(original, CompletedRenderScreenMode.KeepOriginal);
            Check(keep.Failed.Count == 0 && keep.Entries.Single().Path == source && keep.StagingDirectory == null && !Directory.Exists(staging), "Keep file settings reuses the verified source without staging or changing any flags");
            Check(Metadata(source).GetProperty("screens").GetString() == "matchcard" && Same(before, saved), "Keep original preserves all source bytes and matchcard-only settings");
            var selected = Plan(original, CompletedRenderScreenMode.RosterSelection);
            Check(selected.Failed.Count == 0 && selected.Entries.Count == 1 && selected.StagingDirectory.StartsWith(staging + Path.DirectorySeparatorChar), "Roster selection preparation creates its own private child below the chosen staging root");
            string prepared = selected.Entries.Single().Path;
            Check(prepared != source && Path.GetFileName(prepared) == Path.GetFileName(source) && selected.Entries[0].WrestlerId == 122, "Prepared copies retain the original filename and logical wrestler ID");
            Check(Metadata(prepared).GetProperty("screens").GetString() == "select" && selected.Entries[0].Screens == "select", "Default conversion explicitly targets roster selection");
            Check(Video(prepared).SequenceEqual(sourceVideo), "All BIKi bytes including index and payload remain exactly unchanged");
            Check(SameMetadataExceptScreens(Metadata(source), Metadata(prepared)), "Picture hashes/names, shared ownership, ending, taunt and all unknown metadata remain unchanged");
            Check(Same(before, saved), "Screen conversion never alters the saved source movie");
            WritePlugin();
            var installed = BatchRenderInstaller.Install(app, game, selected.Entries.Select(ToJob));
            string destination = Path.Combine(game, "Plugins", "GorillaRenders", Path.GetFileName(source));
            Check(installed.Installed == 1 && installed.Failed.Count == 0 && File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(prepared)), "Prepared selection copy installs through the existing installer without rendering or models");
            Check(Metadata(destination).GetProperty("screens").GetString() == "select" && Same(before, saved), "The installed copy targets selection while the saved original retains its settings");
            Check(CompletedRenderScreenPlan.Cleanup(selected) && !Directory.Exists(selected.StagingDirectory) && File.Exists(source), "Cleanup removes only staged files after installation and preserves the source");
            var both = Plan(original, CompletedRenderScreenMode.Both);
            Check(both.Failed.Count == 0 && both.Entries.Single().Screens == "both" && Same(before, saved), "Both screens is explicit and also preserves the saved source");
            Check(CompletedRenderScreenPlan.Cleanup(both) && CompletedRenderScreenPlan.Cleanup(both), "Private cleanup is safe to repeat");

            Reset();
            source = Save("122_UnknownFields.gmr", "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":122,\"name\":\"Wide values\",\"taunt\":{\"name\":\"Move\",\"clip\":\"a001\"},\"screens\":\"matchcard\",\"end\":\"freeze\",\"render_hashes\":[\"" + HashOne + "\"],\"opaque\":18446744073709551615,\"signed\":-9223372036854775808,\"precise\":0.1234567890123456789012345678,\"future\":{\"screens\":\"do not edit nested\",\"array\":[null,true,9223372036854775809]}}");
            selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection); prepared = selected.Entries.Single().Path;
            Check(Metadata(prepared).GetProperty("opaque").GetUInt64() == ulong.MaxValue && Metadata(prepared).GetProperty("signed").GetInt64() == long.MinValue, "Metadata conversion retains full unsigned and signed 64-bit integer precision");
            Check(Metadata(prepared).GetProperty("precise").GetDecimal() == 0.1234567890123456789012345678m, "Unknown high-precision numeric metadata is retained");
            Check(Metadata(prepared).GetProperty("future").GetProperty("screens").GetString() == "do not edit nested" && SameMetadataExceptScreens(Metadata(source), Metadata(prepared)), "Only the top-level screen setting changes, including nested unknown fields");
            CompletedRenderScreenPlan.Cleanup(selected);

            Reset(); source = Save("122_MapOnly.gmr", Json(new { render_hashes = Array.Empty<string>(), render_names = Array.Empty<string>(), shared_hashes = new[] { HashOne } }));
            string mapPath = Path.Combine(saved, "_roster_pictures.txt");
            selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection);
            Check(selected.Entries.Count == 0 && selected.Failed.Any(message => message.Contains("cannot be guessed")), "Shared hashes alone cannot invent the missing roster-picture association");
            Check(selected.StagingDirectory == null || !Directory.Exists(selected.StagingDirectory), "A failed selection preparation leaves no installable partial staging result");
            byte[] rawMap = Encoding.UTF8.GetBytes("\ufeff" + HashOne + " 122 1 2 3 4\r\n"); File.WriteAllBytes(mapPath, rawMap);
            selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection); prepared = selected.Entries.Single().Path;
            Check(selected.Failed.Count == 0 && selected.Entries.Single().HasRosterPictureMap, "The source's own explicit map can establish the selection route with primary ID in its first four slots");
            Check(File.ReadAllBytes(Path.Combine(selected.Entries[0].OutputDirectory, "_roster_pictures.txt")).SequenceEqual(rawMap), "Staging retains original map bytes, BOM, ordering and line endings");
            Check(Metadata(prepared).GetProperty("render_hashes").GetArrayLength() == 0 && Metadata(prepared).GetProperty("shared_hashes")[0].GetString() == HashOne, "Map-based preparation does not inject hashes or change shared associations");
            WritePlugin(); installed = BatchRenderInstaller.Install(app, game, selected.Entries.Select(ToJob));
            Check(installed.Installed == 1 && File.ReadAllBytes(Path.Combine(game, "Plugins", "GorillaRenders", "_roster_pictures.txt")).SequenceEqual(rawMap), "The original saved map reaches the installed game unchanged");
            CompletedRenderScreenPlan.Cleanup(selected);
            foreach (string map in new[] { HashOne + " 600 500 400 300 122\n", HashOne + " 1 1 1 1 122\n", HashOne + " 122\n" + HashOne + " 1 2\n" })
            {
                File.WriteAllText(mapPath, map); selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection);
                Check(selected.Entries.Count == 0 && selected.Failed.Count == 1, "Fifth/repeated-owner capacity and ambiguous duplicate rows cannot fabricate a native picture route: " + map.Trim().Replace('\n', ';'));
            }
            File.WriteAllText(mapPath, HashOne + " 122\n" + HashOne + " 122 2\n");
            selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection);
            Check(selected.Failed.Count == 0, "Repeated map hash rows are usable only when every possible occurrence explicitly contains the owner");
            CompletedRenderScreenPlan.Cleanup(selected);
            File.WriteAllText(mapPath, "not-a-hash 122\n");
            Check(Plan(Entry(source), CompletedRenderScreenMode.Both).Failed.Any(message => message.Contains("invalid hash")), "Malformed saved maps are rejected before any game write");
            File.WriteAllText(mapPath, HashOne + " 0\n");
            Check(Plan(Entry(source), CompletedRenderScreenMode.Both).Failed.Any(message => message.Contains("invalid owner")), "Invalid map owner IDs are not accepted as selection proof");

            Reset(); source = Save("122_Named.gmr", Json(new { render_names = new[] { "Austin_named_render" } }));
            selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection);
            Check(selected.Failed.Count == 0 && !selected.Entries[0].HasRosterPictureMap, "Original named picture references support selection without a guessed roster map");
            CompletedRenderScreenPlan.Cleanup(selected);
            source = Save("122_Malformed.gmr", Json(new { render_hashes = new[] { "not a hash" } }));
            Check(Plan(Entry(source), CompletedRenderScreenMode.RosterSelection).Failed.Any(message => message.Contains("malformed")), "Malformed embedded picture hashes fail clearly instead of being silently replaced");
            source = Save("122_Empty.gmr", Json(new { render_hashes = Array.Empty<string>() }));
            keep = Plan(Entry(source), CompletedRenderScreenMode.KeepOriginal);
            Check(keep.Failed.Count == 0 && keep.Entries.Single().Path == source, "Keep file settings remains available when no selection association can be proven");
            var duplicate = CompletedRenderScreenPlan.Prepare(new[] { Entry(source), Entry(source) }, staging, CompletedRenderScreenMode.RosterSelection, CancellationToken.None);
            Check(duplicate.Entries.Count == 0 && duplicate.Failed.Any(message => message.Contains("one completed render")), "Duplicate selected attire slots are rejected before staging");
            using (var closing = new CancellationTokenSource())
            {
                closing.Cancel(); bool canceled = false;
                try { CompletedRenderScreenPlan.Prepare(new[] { Entry(source) }, staging, CompletedRenderScreenMode.RosterSelection, closing.Token); } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "Canceled preparation publishes no staged result");
            }

            Reset(); source = Save("122_Standalone.gmr", Json(new { render_hashes = new[] { HashOne } }));
            string beforeHash = Hash(source);
            Check(!GorillaRenderMetadata.SetScreens(source, 999, "select") && Hash(source) == beforeHash, "A standalone screen update cannot retarget another logical wrestler ID");
            Check(!GorillaRenderMetadata.SetScreens(source, 122, "invalid") && Hash(source) == beforeHash, "Unknown screen settings leave a source unchanged");
            foreach (string invalid in new[] { Json(new { render_hashes = new[] { HashOne } }).Replace("GorillaRender", "OtherFormat"), Json(new { render_hashes = new[] { HashOne } }).Replace("\"version\":1", "\"version\":2") })
            {
                source = Save("122_NotGmr.gmr", invalid); beforeHash = Hash(source);
                Check(!GorillaRenderMetadata.SetScreens(source, 122, "select") && Hash(source) == beforeHash, "A BIKi footer carrying an unsupported format/version cannot be converted");
            }
            source = Save("122_BadVideo.gmr", Json(new { render_hashes = new[] { HashOne } }));
            byte[] unsupported = File.ReadAllBytes(source); Array.Copy(BitConverter.GetBytes(60U), 0, unsupported, 28, 4); File.WriteAllBytes(source, unsupported); beforeHash = Hash(source);
            Check(!GorillaRenderMetadata.SetScreens(source, 122, "select") && Hash(source) == beforeHash, "The standalone screen setter rejects a movie incompatible with the original full-render format");

            Reset(); source = Save("122_Cleanup.gmr", Json(new { render_hashes = new[] { HashOne } }));
            selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection);
            string unowned = Path.Combine(selected.StagingDirectory, "user-created.txt"); File.WriteAllText(unowned, "preserve");
            Check(!CompletedRenderScreenPlan.Cleanup(selected) && File.ReadAllText(unowned) == "preserve" && File.Exists(source), "Cleanup does not recursively delete an unexpected file placed in the private stage");
            File.Delete(unowned); Check(CompletedRenderScreenPlan.Cleanup(selected), "Cleanup finishes after the unexpected file is removed explicitly");

            if (args.Length == 1) Native(args[0]);
            Console.WriteLine(checks + " completed render screen preparation checks passed."); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Native(string fixture)
    {
        Reset(); string source = Path.Combine(saved, "161_Native.gmr"); File.Copy(fixture, source);
        File.WriteAllText(Path.Combine(saved, "_roster_pictures.txt"), HashOne + " 161\n");
        int frames = BitConverter.ToInt32(File.ReadAllBytes(source), 8);
        Check(SelectionLoopPolicy.TryWrite(source, 161, new Dictionary<string, object> { { "id", 161 }, { "frames", frames - 11 }, { "end", "freeze" } }), "Actual native freeze fixture has an original source-verified loop policy");
        string sourceHash = Hash(source), policyHash = Hash(source + ".selection-loop.json"); var before = Snapshot(saved);
        SelectionLoopPolicyRecord original; Check(SelectionLoopPolicy.Validate(source, source + ".selection-loop.json", 161, out original), "Original native policy verifies before metadata conversion");
        var selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection); var row = selected.Entries.Single();
        SelectionLoopPolicyRecord rebound;
        Check(selected.Failed.Count == 0 && SelectionLoopPolicy.Validate(row.Path, row.Path + ".selection-loop.json", 161, out rebound), "Prepared selection copy carries a policy rebound to its new complete GMR hash");
        SelectionLoopPolicy.Validate(row.Path, row.Path + ".selection-loop.json", 161, out rebound);
        Check(rebound.Sha256 != original.Sha256 && rebound.MotionFrames == original.MotionFrames && rebound.VideoFrames == original.VideoFrames && rebound.Fps == original.Fps,
            "Rebinding changes the GMR hash while retaining the exact authorized motion/video range and rate");
        Check(Video(source).SequenceEqual(Video(row.Path)) && SameMetadataExceptScreens(Metadata(source), Metadata(row.Path)) && Same(before, saved), "Native video, ending, picture associations and original movie/policy bytes remain unchanged");
        WritePlugin(); var install = BatchRenderInstaller.Install(app, game, selected.Entries.Select(ToJob)); string destination = Path.Combine(game, "Plugins", "GorillaRenders", "161_Native.gmr");
        Check(install.Installed == 1 && SelectionLoopPolicy.TryCopy(row.Path, destination, 161), "Prepared native selection movie and its valid rebound policy install through existing code");
        Check(Hash(source) == sourceHash && Hash(source + ".selection-loop.json") == policyHash && Metadata(destination).GetProperty("screens").GetString() == "select", "Installing the prepared native result leaves all original files unchanged");
        CompletedRenderScreenPlan.Cleanup(selected);
        File.WriteAllText(source + ".selection-loop.json", "invalid policy");
        selected = Plan(Entry(source), CompletedRenderScreenMode.RosterSelection);
        Check(selected.Failed.Count == 0 && selected.Warnings.Any(message => message.Contains("invalid")) && !File.Exists(selected.Entries[0].Path + ".selection-loop.json"), "An invalid legacy sidecar is reported and skipped without inventing a replacement motion policy");
        CompletedRenderScreenPlan.Cleanup(selected);
    }

    private static CompletedRenderEntry Entry(string path) { return CompletedRenderCatalog.Scan(new[] { path }, null, CancellationToken.None).Entries.Single(); }
    private static CompletedRenderScreenPlanReport Plan(CompletedRenderEntry entry, CompletedRenderScreenMode mode)
    { return CompletedRenderScreenPlan.Prepare(new[] { entry }, staging, mode, CancellationToken.None); }
    private static BatchRenderJob ToJob(CompletedRenderEntry entry) { return new BatchRenderJob { Entry = new RosterEntry { Id = entry.WrestlerId, Name = entry.Name }, RenderPath = entry.Path, OutputDirectory = entry.OutputDirectory, Status = "Complete" }; }
    private static void Reset()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        saved = Path.Combine(root, "saved"); staging = Path.Combine(root, "private-staging"); game = Path.Combine(root, "game"); app = Path.Combine(root, "app");
        Directory.CreateDirectory(saved); Directory.CreateDirectory(Path.Combine(game, "Plugins"));
    }
    private static void WritePlugin()
    {
        Directory.CreateDirectory(Path.Combine(app, "plugin"));
        File.WriteAllText(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.dlp"), "fixture MOVINGRENDERS-4.21");
        File.WriteAllText(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.cfg"), "enabled 1\n");
    }
    private static string Json(object associations)
    {
        var common = new Dictionary<string, object> { { "format", "GorillaRender" }, { "version", 1 }, { "wrestler_id", 122 }, { "name", "AVERY" }, { "screens", "matchcard" }, { "end", "loop" }, { "taunt", new { name = "Taunt", clip = "fixture" } } };
        using (var fields = JsonDocument.Parse(JsonSerializer.Serialize(associations))) foreach (var property in fields.RootElement.EnumerateObject()) common[property.Name] = property.Value.Clone();
        return JsonSerializer.Serialize(common);
    }
    private static string Save(string filename, string metadata)
    {
        string path = Path.Combine(saved, filename); byte[] json = Encoding.UTF8.GetBytes(metadata);
        using (var stream = File.Create(path)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi")); foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); writer.Write(json); writer.Write((uint)json.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static byte[] Video(string path) { byte[] bytes = File.ReadAllBytes(path); int size = (int)BitConverter.ToUInt32(bytes, bytes.Length - 8); return bytes.Take(bytes.Length - 8 - size).ToArray(); }
    private static JsonElement Metadata(string path)
    { byte[] bytes = File.ReadAllBytes(path); int size = (int)BitConverter.ToUInt32(bytes, bytes.Length - 8); using (var json = JsonDocument.Parse(bytes.AsMemory(bytes.Length - size - 8, size))) return json.RootElement.Clone(); }
    private static bool SameMetadataExceptScreens(JsonElement a, JsonElement b)
    { return a.EnumerateObject().Where(p => p.Name != "screens").All(p => b.TryGetProperty(p.Name, out var other) && JsonEquivalent(p.Value, other)) && a.EnumerateObject().Count(p => p.Name != "screens") == b.EnumerateObject().Count(p => p.Name != "screens"); }
    private static bool JsonEquivalent(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        if (a.ValueKind == JsonValueKind.Object) return a.EnumerateObject().Count() == b.EnumerateObject().Count() && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var other) && JsonEquivalent(p.Value, other));
        if (a.ValueKind == JsonValueKind.Array) return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray(), JsonEquivalent).All(equal => equal);
        if (a.ValueKind == JsonValueKind.String) return a.GetString() == b.GetString();
        return a.GetRawText() == b.GetRawText();
    }
    private static Dictionary<string, string> Snapshot(string path) { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(path, p), Hash); }
    private static bool Same(Dictionary<string, string> before, string path) { var after = Snapshot(path); return before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var hash) && hash == p.Value); }
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL " + message); checks++; Console.WriteLine("PASS " + message); }
}
