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
    private static string root, game, library;
    private static int checks;
    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-installed-screen-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Reset(); string a = Save(122), b = Save(312), other = Save(468); var original = Snapshot(library);
            string config = Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.cfg"), plugin = Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp");
            File.WriteAllText(config, "enabled 0\r\ncustom true\r\n"); File.WriteAllText(plugin, "private original plugin");
            string map = Path.Combine(library, "_roster_pictures.txt"); File.WriteAllText(map, "FEDCBA9876543210 312 122 468\r\n");
            var sideFiles = Snapshot(Path.GetDirectoryName(config)).Where(pair => !pair.Key.Contains("GorillaRenders")).ToDictionary(pair => pair.Key, pair => pair.Value);
            string configHash = Hash(config), pluginHash = Hash(plugin), mapHash = Hash(map);
            var result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.RosterSelection);
            Check(result.Changed == 2 && result.Failed.Count == 0 && result.RollbackComplete, "One bulk operation changes both selected installed movies");
            Check(Metadata(a).GetProperty("screens").GetString() == "select" && Metadata(b).GetProperty("screens").GetString() == "select", "Roster selection is applied to each selected movie");
            Check(Video(a).SequenceEqual(VideoBytes(original[Path.GetFileName(a)])) && Video(b).SequenceEqual(VideoBytes(original[Path.GetFileName(b)])), "All encoded video bytes remain unchanged for both movies");
            Check(Metadata(a).GetProperty("wrestler_id").GetInt32() == 122 && Metadata(b).GetProperty("wrestler_id").GetInt32() == 312
                && Metadata(a).GetProperty("taunt").GetProperty("clip").GetString() == "fixture" && Metadata(a).GetProperty("end").GetString() == "loop", "Logical IDs, ending and taunt metadata are retained");
            Check(Metadata(a).GetProperty("opaque").GetUInt64() == ulong.MaxValue && Metadata(a).GetProperty("nested").GetProperty("screens").GetString() == "retain", "Unknown metadata and 64-bit numeric precision are retained");
            Check(File.ReadAllBytes(other).SequenceEqual(original[Path.GetFileName(other)]), "An unselected installed wrestler is never changed");
            Check(Hash(config) == configHash && Hash(plugin) == pluginHash && Hash(map) == mapHash, "Disabled configuration, plugin and raw picture map remain byte-identical");
            Check(Directory.Exists(result.BackupDirectory) && File.ReadAllBytes(Path.Combine(result.BackupDirectory, Path.GetFileName(a))).SequenceEqual(original[Path.GetFileName(a)])
                && File.ReadAllBytes(Path.Combine(result.BackupDirectory, Path.GetFileName(b))).SequenceEqual(original[Path.GetFileName(b)]), "Verified exact original backups exist for every selected movie");
            Check(!Directory.EnumerateFiles(library).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)), "Publication leaves no private temporary files in the installed movie folder");
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.MatchCard);
            Check(result.Changed == 2 && result.Failed.Count == 0 && Metadata(a).GetProperty("screens").GetString() == "matchcard" && Metadata(b).GetProperty("screens").GetString() == "matchcard", "A second bulk operation targets only match cards");
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both);
            Check(result.Changed == 2 && result.Failed.Count == 0 && Metadata(a).GetProperty("screens").GetString() == "both" && Metadata(b).GetProperty("screens").GetString() == "both", "Both screens is supported for multiple installed movies");
            string bothA = Hash(a), bothB = Hash(b);
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both);
            Check(result.Changed == 0 && result.Unchanged == 2 && result.BackupDirectory == null && Hash(a) == bothA && Hash(b) == bothB, "Already-matching rows are no-ops with unchanged bytes and no new backup set");
            result = Apply(new[] { Choice(a, 122), Choice(other, 468) }, InstalledRenderScreenMode.MatchCard);
            Check(result.Changed == 1 && result.Unchanged == 1 && result.Failed.Count == 0, "Mixed selections count already-matching and changed rows independently");

            Reset(); a = Save(122); b = Save(312); original = Snapshot(library);
            ExpectNoChange(Array.Empty<InstalledRenderScreenChoice>(), InstalledRenderScreenMode.Both, original, "Empty bulk selections cannot mutate installed files");
            ExpectNoChange(new[] { Choice(a, 122) }, (InstalledRenderScreenMode)999, original, "Unknown screen modes cannot mutate installed files");
            ExpectNoChange(new[] { Choice(a, 122), Choice(a, 122) }, InstalledRenderScreenMode.Both, original, "Duplicate selected paths and IDs fail before writing");
            ExpectNoChange(new[] { Choice(a, 313) }, InstalledRenderScreenMode.Both, original, "A stale or incorrect logical wrestler ID is rejected");
            ExpectNoChange(new[] { Choice(a, 0) }, InstalledRenderScreenMode.Both, original, "Out-of-range wrestler IDs are rejected");
            string outside = Path.Combine(root, "122_saved.gmr"); File.Copy(a, outside); string outsideHash = Hash(outside);
            ExpectNoChange(new[] { Choice(outside, 122) }, InstalledRenderScreenMode.Both, original, "Saved output files outside the installed root are never treated as installed sources");
            Check(Hash(outside) == outsideHash, "A rejected saved source remains byte-identical");
            string nested = Path.Combine(library, "nested"); Directory.CreateDirectory(nested); File.Copy(a, Path.Combine(nested, "122.gmr"));
            ExpectNoChange(new[] { Choice(Path.Combine(nested, "122.gmr"), 122) }, InstalledRenderScreenMode.Both, Snapshot(library), "Nested movies that the native plugin does not scan cannot be edited through bulk selection");
            Reset(); a = Save(122); b = Save(312); byte[] bad = File.ReadAllBytes(b); Array.Copy(BitConverter.GetBytes(60U), 0, bad, 28, 4); File.WriteAllBytes(b, bad); original = Snapshot(library);
            ExpectNoChange(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, original, "One incompatible selected movie fails the complete preflight before any other movie changes");
            Reset(); a = Save(122); b = Save(312); File.WriteAllText(b, "incomplete"); original = Snapshot(library);
            ExpectNoChange(new[] { Choice(a, 122) }, InstalledRenderScreenMode.Both, original, "An unreadable installed header prevents guessing that duplicate slot claims are absent");
            Reset(); a = Save(122); b = Save(312, extra: new[] { 122 }); original = Snapshot(library);
            ExpectNoChange(new[] { Choice(a, 122) }, InstalledRenderScreenMode.Both, original, "Another movie's active extra ID can conflict with a selected primary ID");
            Reset(); a = Save(122, extra: new[] { 610 }); b = Save(312, extra: new[] { 610 }); original = Snapshot(library);
            ExpectNoChange(new[] { Choice(a, 122) }, InstalledRenderScreenMode.Both, original, "Conflicting selected extra ID claims are rejected even when its primary ID is unique");
            Reset(); a = Save(122, extra: new[] { 1, 2, 3, 312 }); b = Save(312);
            result = Apply(new[] { Choice(a, 122) }, InstalledRenderScreenMode.Both);
            Check(result.Changed == 1 && result.Failed.Count == 0, "The native four-owner capacity includes the primary ID and only its first three extra slots");

            Reset(); a = Save(122); b = Save(312); original = Snapshot(library);
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, before: (index, path) => { if (index == 2) throw new IOException("injected second replacement failure"); });
            Check(result.Changed == 0 && result.Failed.Count == 1 && result.RollbackComplete && Same(original, library), "A later publication failure rolls back an earlier successful movie replacement");
            Check(result.Warnings.Any(value => value.Contains("rolled back")) && Directory.EnumerateFiles(result.BackupDirectory).Count() == 2, "Rollback is reported and original backups remain available");
            Reset(); a = Save(122); b = Save(312); original = Snapshot(library); string edited = null;
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, before: (index, path) => { if (index == 2) { File.AppendAllText(path, "external edit"); edited = Hash(path); } });
            Check(result.Changed == 0 && result.RollbackComplete && File.ReadAllBytes(a).SequenceEqual(original[Path.GetFileName(a)]) && Hash(b) == edited, "A concurrently edited next source is preserved while an earlier owned replacement is rolled back");
            Reset(); a = Save(122); b = Save(312); original = Snapshot(library); string added = null;
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, before: (index, path) => { if (index == 2) added = Save(468, extra: new[] { 122 }); });
            Check(result.Changed == 0 && result.RollbackComplete && Hash(a) == ByteHash(original[Path.GetFileName(a)]) && Hash(b) == ByteHash(original[Path.GetFileName(b)]) && File.Exists(added), "New installed files and introduced duplicate claims are detected between replacements without removing the new file");
            Reset(); a = Save(122); b = Save(312); original = Snapshot(library);
            result = Apply(new[] { Choice(a, 122) }, InstalledRenderScreenMode.Both, closed: () => false);
            Check(result.Changed == 0 && result.BackupDirectory == null && result.Failed.Any(value => value.Contains("Close WWE")) && Same(original, library), "A running game blocks the operation before staging or backups");
            bool closed = true;
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, closed: () => closed, before: (index, path) => { if (index == 2) closed = false; });
            Check(result.Changed == 1 && !result.RollbackComplete && result.Failed.Any(value => value.Contains("Restore needed")), "A game started during publication stops further writes and reports partial changes needing restore");
            Check(File.ReadAllBytes(b).SequenceEqual(original[Path.GetFileName(b)]) && File.ReadAllBytes(Path.Combine(result.BackupDirectory, Path.GetFileName(a))).SequenceEqual(original[Path.GetFileName(a)]), "No later movie is touched after the game opens, and its earlier original is backed up");
            Reset(); a = Save(122); b = Save(312); original = Snapshot(library);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); result = InstalledRenderScreenPlan.Apply(game, new[] { Choice(a, 122) }, InstalledRenderScreenMode.Both, canceled.Token, () => true, null);
                Check(result.Changed == 0 && result.BackupDirectory == null && Same(original, library) && result.Failed.Any(value => value.Contains("canceled")), "Pre-canceled operations leave installed files and backup locations unchanged");
            }
            using (var canceled = new CancellationTokenSource())
            {
                result = InstalledRenderScreenPlan.Apply(game, new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, canceled.Token, () => true, (index, path) => { if (index == 2) canceled.Cancel(); });
                Check(result.Changed == 0 && result.RollbackComplete && Same(original, library), "Cancellation during publication rolls back owned changes while the game is closed");
            }
            Reset(); a = Save(122); b = Save(312); original = Snapshot(library);
            result = Apply(new[] { Choice(a, 122), Choice(b, 312) }, InstalledRenderScreenMode.Both, before: (index, path) =>
            {
                if (index == 2) File.AppendAllText(Directory.GetFiles(Path.Combine(game, "Plugins", "GorillaRenderBackups"), Path.GetFileName(b), SearchOption.AllDirectories).Single(), "tampered");
            });
            Check(result.Changed == 0 && result.RollbackComplete && Same(original, library) && result.Failed.Any(value => value.Contains("backup")), "Changed original backups stop publication before replacing the associated installed source");
            if (args.Length == 1) Native(args[0]);
            Console.WriteLine(checks + " installed render screen checks passed."); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Native(string fixture)
    {
        Reset(); string source = Path.Combine(library, "161_Native.gmr"); File.Copy(fixture, source);
        int frames = BitConverter.ToInt32(File.ReadAllBytes(source), 8);
        Check(SelectionLoopPolicy.TryWrite(source, 161, new Dictionary<string, object> { { "id", 161 }, { "frames", frames - 11 }, { "end", "freeze" } }), "Original native-encoder freeze fixture has a verified authorized loop policy");
        byte[] originalMovie = File.ReadAllBytes(source), originalPolicy = File.ReadAllBytes(source + ".selection-loop.json"), originalVideo = Video(source);
        SelectionLoopPolicyRecord policy; Check(SelectionLoopPolicy.Validate(source, source + ".selection-loop.json", 161, out policy), "The original native policy verifies before a bulk change");
        var result = Apply(new[] { Choice(source, 161) }, InstalledRenderScreenMode.RosterSelection);
        SelectionLoopPolicyRecord rebound;
        Check(result.Changed == 1 && result.Failed.Count == 0 && SelectionLoopPolicy.Validate(source, source + ".selection-loop.json", 161, out rebound), "The installed native movie and rebound policy remain mutually valid");
        SelectionLoopPolicy.Validate(source, source + ".selection-loop.json", 161, out rebound);
        Check(rebound.Sha256 != policy.Sha256 && rebound.VideoFrames == policy.VideoFrames && rebound.MotionFrames == policy.MotionFrames && rebound.Fps == policy.Fps && rebound.Ending == policy.Ending, "Only the movie hash changes in the policy; authorized motion range, rate and ending remain exact");
        Check(Video(source).SequenceEqual(originalVideo) && Metadata(source).GetProperty("screens").GetString() == "select", "A genuine native encoded video is unchanged after installed-screen targeting");
        Check(File.ReadAllBytes(Path.Combine(result.BackupDirectory, Path.GetFileName(source))).SequenceEqual(originalMovie)
            && File.ReadAllBytes(Path.Combine(result.BackupDirectory, Path.GetFileName(source) + ".selection-loop.json")).SequenceEqual(originalPolicy), "Original native movie and valid policy are both backed up byte-for-byte");
        originalMovie = File.ReadAllBytes(source); originalPolicy = File.ReadAllBytes(source + ".selection-loop.json");
        result = Apply(new[] { Choice(source, 161) }, InstalledRenderScreenMode.Both, before: (index, path) => { if (index == 2) throw new IOException("policy commit failure"); });
        Check(result.Changed == 0 && result.RollbackComplete && File.ReadAllBytes(source).SequenceEqual(originalMovie) && File.ReadAllBytes(source + ".selection-loop.json").SequenceEqual(originalPolicy), "A policy replacement failure restores its earlier movie and leaves the original policy unchanged");
        Check(SelectionLoopPolicy.Validate(source, source + ".selection-loop.json", 161, out rebound), "Rollback restores an exactly matching native movie-policy pair");
        File.WriteAllText(source + ".selection-loop.json", "invalid legacy policy"); string invalidHash = Hash(source + ".selection-loop.json");
        result = Apply(new[] { Choice(source, 161) }, InstalledRenderScreenMode.Both);
        Check(result.Changed == 1 && result.Warnings.Any(value => value.Contains("invalid")) && Hash(source + ".selection-loop.json") == invalidHash, "An invalid existing policy is reported and preserved without authorizing new frame ranges");
        Check(Hash(Path.Combine(result.BackupDirectory, Path.GetFileName(source) + ".selection-loop.json")) == invalidHash, "An invalid existing policy is also preserved in the original backup set");
    }
    private static InstalledRenderScreenChoice Choice(string path, int id) { return new InstalledRenderScreenChoice { Path = path, WrestlerId = id }; }
    private static InstalledRenderScreenReport Apply(IEnumerable<InstalledRenderScreenChoice> selected, InstalledRenderScreenMode mode, Func<bool> closed = null, Action<int, string> before = null)
    { return InstalledRenderScreenPlan.Apply(game, selected, mode, CancellationToken.None, closed ?? (() => true), before); }
    private static void ExpectNoChange(InstalledRenderScreenChoice[] selected, InstalledRenderScreenMode mode, Dictionary<string, byte[]> before, string label)
    { var report = Apply(selected, mode); Check(report.Changed == 0 && report.Failed.Count != 0 && report.BackupDirectory == null && Same(before, library), label); }
    private static void Reset() { if (Directory.Exists(root)) Directory.Delete(root, true); game = Path.Combine(root, "game"); library = Path.Combine(game, "Plugins", "GorillaRenders"); Directory.CreateDirectory(library); }
    private static string Save(int id, string screens = "matchcard", int[] extra = null)
    {
        string path = Path.Combine(library, id + "_Fixture.gmr");
        string metadata = "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":" + id + ",\"name\":\"Fixture\",\"screens\":\"" + screens + "\",\"end\":\"loop\",\"taunt\":{\"name\":\"Move\",\"clip\":\"fixture\"},\"render_hashes\":[\"0123456789ABCDEF\"],\"opaque\":18446744073709551615,\"nested\":{\"screens\":\"retain\"},\"extra_ids\":" + JsonSerializer.Serialize(extra ?? Array.Empty<int>()) + "}";
        byte[] json = Encoding.UTF8.GetBytes(metadata);
        using (var stream = File.Create(path)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi")); foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); writer.Write(json); writer.Write((uint)json.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static byte[] Video(string path) { return VideoBytes(File.ReadAllBytes(path)); }
    private static byte[] VideoBytes(byte[] bytes) { return bytes.Take(BitConverter.ToInt32(bytes, 4) + 8).ToArray(); }
    private static JsonElement Metadata(string path) { byte[] bytes = File.ReadAllBytes(path); int length = BitConverter.ToInt32(bytes, bytes.Length - 8); using (var json = JsonDocument.Parse(bytes.AsMemory(bytes.Length - 8 - length, length))) return json.RootElement.Clone(); }
    private static Dictionary<string, byte[]> Snapshot(string directory) { return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes); }
    private static bool Same(Dictionary<string, byte[]> before, string directory) { var after = Snapshot(directory); return before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && after[pair.Key].SequenceEqual(pair.Value)); }
    private static string Hash(string path) { return ByteHash(File.ReadAllBytes(path)); }
    private static string ByteHash(byte[] bytes) { return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
    private static void Check(bool passed, string label) { if (!passed) throw new Exception("FAILED: " + label); checks++; Console.WriteLine("PASS " + label); }
}
