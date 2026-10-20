using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string temporary;
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: DiscoveryMatchupCatalogTests actual-discovery-root [result-json]"); return 2; }
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-matchup-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            string original = Path.GetFullPath(args[0]);
            string sourceTable = Path.Combine(original, "CreateShow", "MatchUpScreenAsset.jsfb");
            byte[] source = File.ReadAllBytes(sourceTable);
            byte[] sourceDigest = SHA256.HashData(source);
            var actual = DiscoveryMatchupCatalog.Read(original);
            Console.WriteLine("Actual catalog: " + JsonSerializer.Serialize(new { actual.TableCount, Entries = actual.Entries.Count, Linked = actual.Entries.Count(entry => entry.AssetHashes.Count > 0), Hashes = actual.RequiredHashes.Count, actual.ScreenFiles, actual.Warnings }));
            Check(actual.Warnings.Count == 0 && actual.TableCount == 6, "Five actual JSFB tables and the supplied Arena JSON load with the current SDB");
            Check(actual.Entries.Count(entry => entry.AssetHashes.Count > 0) == 282 && actual.Entries.Single(entry => entry.Id == 913).AssetHashes.Count == 0, "All 282 nonzero matchup references load and the explicitly empty ID 913 stays unlinked");
            Check(actual.ScreenFiles.Count == 0, "The uploaded metadata references screens without falsely claiming their pixel/project files are extracted");
            Has(actual, 806, 0x342ff29b4876b646UL, "AEW DOUBLE OR NOTHING 2026");
            Has(actual, 800, 0x2380c945533596caUL, "AEW DYNAMITE");
            Has(actual, 801, 0xb9684e1cc73cf513UL, "AEW COLLISION");
            Has(actual, 625, 0x3a5f65330f4e84bfUL, "DOUBLE RING");
            Has(actual, 626, 0x33a98abd8f130710UL, "SINGLE RING");
            Has(actual, 807, 0x325dff3e0e980ae3UL, "AEW FORBIDDEN DOOR 2026");
            var survivor = actual.Entries.Single(entry => entry.Id == 626);
            Check(survivor.ShowLoadIds.Contains(8079), "Show-load numeric references join by the actual ID without becoming project hashes");
            Check(actual.Entries.Single(entry => entry.Id == 806).AssetPaths.Count == 0, "A template label alone never invents a source file path");
            Check(!actual.RequiredHashes.Contains(0xffffe0f0UL), "SDB name keys do not become archive asset hashes");
            Check(DiscoveryMatchupCatalog.HashPath("UI/Projects/MatchUp/MatchUp_orgNeon_2K19/2K19_match_card.jsfb") == 0x94e985399efe3bfdUL, "The actual screen path matches its stored exact FNV64 reference");
            Check(DiscoveryMatchupCatalog.HashPath("UI\\Projects\\MATCHUP\\MatchUp_orgNeon_2K19\\2K19_match_card.jsfb") == 0x94e985399efe3bfdUL, "Path matching follows game case and slash normalization");

            string data = Path.Combine(temporary, "discovery_data");
            foreach (string relative in new[] { "CreateShow/MatchUpScreenAsset.jsfb", "CreateShow/MatchupMenuAssetData.jsfb", "CreateShow/ShowLoadData.jsfb", "CreateShow/ShowLoadMenuAssetData.jsfb", "Arena/ArenaData.jsfb", "Arena/ArenaData.json", "Sdb/ENG.sdb" }) Copy(Path.Combine(original, relative), Path.Combine(data, relative));
            string sdbPath = Path.Combine(data, "Sdb", "ENG.sdb"); byte[] originalSdb = File.ReadAllBytes(sdbPath);
            var namesBefore = DiscoveryMatchupCatalog.Read(data).Entries.ToDictionary(entry => entry.Id, entry => entry.Names.ToArray());
            var usedNames = new HashSet<string>(namesBefore.Values.SelectMany(names => names), StringComparer.Ordinal);
            Dictionary<uint, string> allStrings = DiscoveryProfiles.ReadStrings(sdbPath);
            byte[] oneBadName = (byte[])originalSdb.Clone(); int badRow = -1;
            for (int index = 0; index < BitConverter.ToUInt32(oneBadName, 4); index++)
            {
                int row = 8 + index * 12; uint key = BitConverter.ToUInt32(oneBadName, row + 8);
                if (usedNames.Contains(allStrings[key])) continue;
                Put32(oneBadName, row, 8); badRow = row; break;
            }
            Check(badRow >= 0, "The real SDB fixture includes a localization entry outside the matchup labels");
            File.WriteAllBytes(sdbPath, oneBadName);
            var partialNames = DiscoveryMatchupCatalog.Read(data);
            Check(partialNames.RequiredHashes.SetEquals(actual.RequiredHashes) && partialNames.TableCount == actual.TableCount,
                "One malformed optional SDB row never changes exact matchup references or record-table coverage");
            Check(partialNames.Entries.All(entry => namesBefore[entry.Id].SequenceEqual(entry.Names)),
                "One unrelated malformed SDB row preserves every valid actual arena and menu label");
            Check(partialNames.Warnings.Count == 1 && partialNames.Warnings[0].Contains("1 malformed string row(s) of " + BitConverter.ToUInt32(originalSdb, 4)),
                "A recovered matchup catalog surfaces the exact optional-name failure count");
            File.WriteAllBytes(sdbPath, originalSdb);
            string tablePath = Path.Combine(data, "CreateShow", "MatchUpScreenAsset.jsfb");
            var file = new FlatBufferFile(source, "StCr");
            int aew = file.Tables(file.Root, 1).Single(row => file.Field(row, 0) != 0 && file.U32(file.Field(row, 0)) == 806);
            byte[] changed = (byte[])source.Clone(); Put64(changed, file.Field(aew, 4), 0xfffffffffffffff1UL); File.WriteAllBytes(tablePath, changed);
            var refreshed = DiscoveryMatchupCatalog.Read(data);
            Check(refreshed.Entries.Single(entry => entry.Id == 806).AssetHashes.SetEquals(new[] { 0xfffffffffffffff1UL }), "A changed live matchup reference is reread without a stale stock or previous catalog cache");
            Check(refreshed.RequiredHashes.Contains(0xfffffffffffffff1UL), "Uint64 values above signed Int64 retain all bits");

            string relativeScene = "UI/Entirely_Custom_AEW/never_named_matchup/graphics.jsfb";
            ulong sceneHash = DiscoveryMatchupCatalog.HashPath(relativeScene);
            File.WriteAllBytes(tablePath, AppendRow(source, 60000, sceneHash, "A future custom arena"));
            string json = Path.Combine(data, "Arena", "ArenaData.json");
            var arenas = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(json));
            string addedJson = File.ReadAllText(json);
            int end = addedJson.LastIndexOf(']');
            addedJson = addedJson.Insert(end, ", {\"first_id\":60000,\"arena_name\":\"MY FUTURE AEW ARENA\"}");
            File.WriteAllText(json, addedJson);
            var added = DiscoveryMatchupCatalog.Read(data);
            var newEntry = added.Entries.Single(entry => entry.Id == 60000);
            Check(added.Entries.Count(entry => entry.AssetHashes.Count > 0) == 283 && newEntry.AssetHashes.SetEquals(new[] { sceneHash }), "A new record appended to a copy of the actual matchup table is discovered automatically");
            Check(newEntry.Name == "MY FUTURE AEW ARENA" && newEntry.TemplateNames.Contains("A future custom arena"), "Provided JSON labels supplement new JSFB records without replacing their references");
            Check(newEntry.AssetPaths.Count == 0 && added.ScreenFiles.Count == 0, "A linked but absent new screen remains a reference, without fabricated file availability");
            string scene = Path.Combine(data, relativeScene); Directory.CreateDirectory(Path.GetDirectoryName(scene));
            File.WriteAllBytes(scene, new byte[] { 12, 0, 0, 0, (byte)'S', (byte)'c', (byte)'r', (byte)'e', 0, 0, 0, 0 });
            var found = DiscoveryMatchupCatalog.Read(data);
            Check(found.ScreenFiles.Contains(scene), "A real Scre header is discovered outside all former matchup directory and filename filters");
            Check(found.Entries.Single(entry => entry.Id == 60000).AssetPaths.SequenceEqual(new[] { relativeScene }), "A loose scene path is attached only after its exact FNV matches the current table");
            string unlisted = Path.Combine(data, "UI", "OtherGraphics", "another_screen.jsfb"); Copy(scene, unlisted);
            Check(DiscoveryMatchupCatalog.Read(data).ScreenFiles.Count == 2, "New unlisted Scre projects remain visible to the scene scanner");
            string backup = Path.Combine(data, "Gorilla Studio Layout Backups", "old", "irrelevant.jsfb"); Copy(scene, backup);
            Check(DiscoveryMatchupCatalog.Read(data).ScreenFiles.Count == 2, "Old setup backups are not mistaken for current game screens");
            File.WriteAllBytes(tablePath, AppendRow(source, 60001, 0x94e985399efe3bfdUL, "UI/Projects/MatchUp/MatchUp_orgNeon_2K19/2K19_match_card.jsfb"));
            var fullPath = DiscoveryMatchupCatalog.Read(data).Entries.Single(entry => entry.Id == 60001);
            Check(fullPath.AssetPaths.Count == 1, "A full path explicitly stored in the table is accepted when its FNV matches");
            File.WriteAllBytes(tablePath, AppendRow(source, 60001, 0x94e985399efe3bfdUL, "UI/Projects/Wrong/file.jsfb"));
            Check(DiscoveryMatchupCatalog.Read(data).Entries.Single(entry => entry.Id == 60001).AssetPaths.Count == 0, "An explicit path with a different asset hash is not trusted");
            File.WriteAllBytes(tablePath, new byte[32]);
            var broken = DiscoveryMatchupCatalog.Read(data);
            Check(broken.Warnings.Any(warning => warning.Contains("MatchUpScreenAsset.jsfb")) && broken.RequiredHashes.Count == 0, "Malformed screen metadata reports a warning instead of deriving hashes from menu pictures");
            File.WriteAllBytes(tablePath, source);
            byte[] badString = AppendRow(source, 60001, sceneHash, "bad");
            var badFile = new FlatBufferFile(badString, "StCr"); int last = badFile.Tables(badFile.Root, 1).Last(); Put32(badString, badFile.Field(last, 2), uint.MaxValue); File.WriteAllBytes(tablePath, badString);
            Check(DiscoveryMatchupCatalog.Read(data).Warnings.Any(warning => warning.Contains("string")), "Invalid string offsets fail safely");
            Check(DiscoveryMatchupCatalog.Read(Path.Combine(temporary, "missing")).Warnings.Count == 1, "A missing data root is a readable failure");
            Check(SHA256.HashData(File.ReadAllBytes(sourceTable)).SequenceEqual(sourceDigest), "Every original uploaded matchup table byte remains unchanged");
            if (args.Length > 1) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))); File.WriteAllText(args[1], JsonSerializer.Serialize(new { checks = passed, linked_entries = 282, required_hashes = actual.RequiredHashes.Count, sources = actual.TableCount, examples = actual.Entries.Where(entry => new[] { 625, 626, 800, 801, 806, 807 }.Contains(entry.Id)).Select(entry => new { entry.Id, entry.Name, entry.Names, entry.TemplateNames, hashes = entry.AssetHashes.Select(hash => hash.ToString("X16")) }) }, new JsonSerializerOptions { WriteIndented = true })); }
            Console.WriteLine(passed + " Discovery matchup catalog checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static void Has(DiscoveryMatchupCatalogResult catalog, int id, ulong hash, string name)
    {
        var entry = catalog.Entries.Single(item => item.Id == id);
        Check(entry.AssetHashes.Contains(hash) && catalog.RequiredHashes.Contains(hash), "Actual matchup " + id + " preserves its own exact screen hash");
        Check(entry.Names.Concat(entry.TemplateNames).Any(value => value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0), "Actual matchup " + id + " resolves the expected arena name: " + name);
    }
    private static byte[] AppendRow(byte[] source, int id, ulong hash, string label)
    {
        var file = new FlatBufferFile(source, "StCr"); int[] rows = file.Tables(file.Root, 1).ToArray();
        var bytes = new List<byte>(new byte[28 + 4 * (rows.Length + 1)]);
        while (bytes.Count % 8 != 0) bytes.Add(0);
        int copyOffset = bytes.Count; bytes.AddRange(source);
        Put32(bytes, 0, 16); bytes[4] = (byte)'S'; bytes[5] = (byte)'t'; bytes[6] = (byte)'C'; bytes[7] = (byte)'r';
        Put16(bytes, 8, 8); Put16(bytes, 10, 8); Put16(bytes, 12, 0); Put16(bytes, 14, 4); Put32(bytes, 16, 8); Put32(bytes, 20, 4); Put32(bytes, 24, (uint)rows.Length + 1);
        for (int i = 0; i < rows.Length; i++) Put32(bytes, 28 + 4 * i, (uint)(copyOffset + rows[i] - 28 - 4 * i));
        while (bytes.Count % 8 != 0) bytes.Add(0);
        int vt = bytes.Count; bytes.AddRange(new byte[16]); Put16(bytes, vt, 14); Put16(bytes, vt + 2, 32);
        foreach (var pair in new[] { (0, 4), (1, 8), (2, 12), (4, 24) }) Put16(bytes, vt + 4 + 2 * pair.Item1, pair.Item2);
        int row = bytes.Count; bytes.AddRange(new byte[32]); Put32(bytes, row, (uint)(row - vt)); Put32(bytes, row + 4, (uint)id); Put32(bytes, row + 8, (uint)id); Put64(bytes, row + 24, hash);
        int text = bytes.Count; byte[] value = Encoding.UTF8.GetBytes(label); bytes.AddRange(new byte[4]); bytes.AddRange(value); bytes.Add(0); Put32(bytes, text, (uint)value.Length); Put32(bytes, row + 12, (uint)(text - row - 12));
        int pointer = 28 + 4 * rows.Length; Put32(bytes, pointer, (uint)(row - pointer)); return bytes.ToArray();
    }
    private static void Put16(List<byte> bytes, int at, int value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
    private static void Put32(List<byte> bytes, int at, uint value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (8 * i)); }
    private static void Put32(byte[] bytes, int at, uint value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (8 * i)); }
    private static void Put64(IList<byte> bytes, int at, ulong value) { for (int i = 0; i < 8; i++) bytes[at + i] = (byte)(value >> (8 * i)); }
    private static void Copy(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true); }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }
}
