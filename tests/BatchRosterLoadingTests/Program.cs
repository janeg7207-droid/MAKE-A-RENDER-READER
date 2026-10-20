using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary, sourceRoot, sourceTable, sourceMapping, sourceSdb;
    private static int passed;

    private static int Main(string[] args)
    {
        sourceRoot = Path.GetFullPath(args.Length == 0 ? "/workspace/discovery-work/discovery_data" : args[0]);
        sourceTable = Table(sourceRoot); sourceMapping = Path.Combine(sourceRoot, "Characters", "CharacterMapping.jsfb");
        sourceSdb = DiscoveryProfiles.FindEnglishSdb(sourceRoot);
        var originalHashes = new[] { sourceTable, sourceMapping, sourceSdb }.ToDictionary(path => path, Hash);
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-batch-roster-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var profiles = DiscoveryProfiles.Read(sourceTable);
            var original = profiles.ToDictionary(row => row.Id);
            RosterSessionData current = BatchRosterLoading.Open(sourceRoot, null, null, null);
            Check("A canceled batch load rejects before reading its unavailable source", () => {
                using (var cancellation = new CancellationTokenSource()) {
                    cancellation.Cancel();
                    Throws<OperationCanceledException>(() => BatchRosterLoading.Open("unavailable-source", null,
                        Path.Combine(temporary, "unavailable.jsfb"), null, cancellation.Token));
                }
            });
            Check("The actual batch loader opens all 1,457 current profiles before model indexing", () => {
                Equal(1457, current.Entries.Count); Equal(sourceTable, current.DiscoveryTable);
                Equal(true, current.Entries.All(row => string.IsNullOrWhiteSpace(row.Folder)));
                Equal(true, current.Entries.Select(row => row.Id).OrderBy(id => id).SequenceEqual(profiles.Select(row => row.Id).OrderBy(id => id)));
            });
            Check("Batch rows keep actual localized names and are returned in name/ID order", () => {
                foreach (RosterEntry row in current.Entries) Equal(original[row.Id].Name ?? "Wrestler " + row.Id, row.Name);
                Equal(true, current.Entries.Select(row => row.Id).SequenceEqual(current.Entries.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id).Select(row => row.Id)));
                Equal("CM PUNK", current.Entries.Single(row => row.Id == 1481).Name);
                Equal(0, current.Warnings.Count);
            });
            string fixture = Metadata("isolated-error"); string sdb = Path.Combine(fixture, "Sdb", "ENG.sdb");
            uint brokenKey = original[1481].NameKey; CorruptOffset(sdb, brokenKey);
            string badHash = Hash(sdb); string fixtureTable = Table(fixture);
            Check("The strict legacy path reproduces the screenshot's batch-roster failure", () => {
                InvalidDataException error = Throws<InvalidDataException>(() => RosterLaunch.Open(fixture, null, fixtureTable, null));
                Equal(true, error.Message.Contains("Invalid Discovery SDB") && error.Message.Contains("outside the text section"));
            });
            RosterSessionData recovered = BatchRosterLoading.Open(fixture, null, fixtureTable, null);
            Check("The production batch path keeps all IDs after one malformed SDB entry", () => {
                Equal(1457, recovered.Entries.Count); Equal(fixtureTable, recovered.DiscoveryTable);
                Equal(true, recovered.Entries.Select(row => row.Id).OrderBy(id => id).SequenceEqual(profiles.Select(row => row.Id).OrderBy(id => id)));
                Equal("Wrestler 1481", recovered.Entries.Single(row => row.Id == 1481).Name);
                foreach (RosterEntry row in recovered.Entries.Where(row => original[row.Id].NameKey != brokenKey))
                    Equal(original[row.Id].Name ?? "Wrestler " + row.Id, row.Name);
                Equal(true, recovered.Warnings.Any(warning => warning.Contains("1 malformed string row") && warning.Contains("ENG.sdb")));
            });
            Check("Loading and ready batch status retain nonfatal localization diagnostics", () => {
                string loading = BatchRosterLoading.LoadingStatus(recovered), ready = BatchRosterLoading.ReadyStatus(recovered, 10);
                Equal(true, loading.Contains("1457")); Equal(true, ready.Contains("1457") && ready.Contains("10"));
                Equal(true, loading.Contains("ENG.sdb") && ready.Contains("ENG.sdb"));
                Equal(true, ready.Contains("malformed string"));
                Equal(badHash, Hash(sdb));
            });
            string structure = Metadata("structural-error"); File.WriteAllBytes(Path.Combine(structure, "Sdb", "ENG.sdb"), new byte[3]);
            Check("A structurally unreadable SDB still exposes current batch IDs with explicit warnings", () => {
                RosterSessionData session = BatchRosterLoading.Open(structure, null, null, null);
                Equal(1457, session.Entries.Count); Equal(true, session.Entries.All(row => row.Name == "Wrestler " + row.Id));
                Equal(true, session.Warnings.Any(warning => warning.Contains("Header is truncated")));
                Equal(true, BatchRosterLoading.ReadyStatus(session, 0).Contains("Header is truncated"));
            });
            string chosenFolder = Directory.CreateDirectory(Path.Combine(temporary, "chosen-model")).FullName;
            File.WriteAllText(Path.Combine(chosenFolder, "Attire.mcd"), "selected source path fixture");
            var chosen = new RosterEntry { Id = 1506, Name = original[1506].Name, Folder = chosenFolder,
                RosterTable = fixtureTable, SourceId = 1506, IsMod = true, Source = "Chosen model" };
            string savedHash = JsonSerializer.Serialize(chosen);
            Check("An exact chosen model binding survives while unrelated SDB names fail", () => {
                RosterEntry row = BatchRosterLoading.Open(fixture, new[] { chosen }, fixtureTable, null).Entries.Single(item => item.Id == 1506);
                Equal(chosenFolder, row.Folder); Equal(1506, row.SourceId); Equal("Chosen model", row.Source);
                Equal(savedHash, JsonSerializer.Serialize(chosen)); Equal(false, ReferenceEquals(chosen, row));
            });
            Check("A previously inferred model waits for the current inventory instead of silently binding", () => {
                var inferred = Copy(chosen); inferred.Source = "Character mapping";
                Equal((string)null, BatchRosterLoading.Open(fixture, new[] { inferred }, fixtureTable, null).Entries.Single(row => row.Id == 1506).Folder);
            });
            Check("A failed localization key cannot restore an obsolete name/model binding", () => {
                var stale = Copy(chosen); stale.Id = 1481; stale.Name = original[1481].Name;
                RosterEntry row = BatchRosterLoading.Open(fixture, new[] { stale }, fixtureTable, null).Entries.Single(item => item.Id == 1481);
                Equal("Wrestler 1481", row.Name); Equal((string)null, row.Folder);
            });
            Check("The explicit JSFB owns its Discovery table even with another remembered root", () => {
                RosterSessionData session = BatchRosterLoading.Open(sourceRoot, new[] { chosen }, fixtureTable, null);
                Equal(fixtureTable, session.DiscoveryTable); Equal("Wrestler 1481", session.Entries.Single(row => row.Id == 1481).Name);
                Equal(chosenFolder, session.Entries.Single(row => row.Id == 1506).Folder);
            });
            string grown = Metadata("grown-damaged-roster"), grownTable = Table(grown), grownSdb = Path.Combine(grown, "Sdb", "ENG.sdb");
            uint key = UnusedKey(DiscoveryProfiles.ReadStrings(grownSdb));
            File.WriteAllBytes(grownTable, ExtendProfiles(File.ReadAllBytes(grownTable), 4000, 1506, key)); WriteString(grownSdb, key, "BATCH NEW CHARACTER"); CorruptOffset(grownSdb, brokenKey);
            Check("A new character loads from the same current table despite another rejected name", () => {
                RosterSessionData session = BatchRosterLoading.Open(grown, recovered.Entries, grownTable, null);
                Equal(1458, session.Entries.Count); Equal("BATCH NEW CHARACTER", session.Entries.Single(row => row.Id == 4000).Name);
                Equal(1506, session.Entries.Single(row => row.Id == 4000).SourceId);
                Equal("Wrestler 1481", session.Entries.Single(row => row.Id == 1481).Name);
                Equal(true, session.Warnings.Any(warning => warning.Contains("1 malformed string row")));
            });
            string json = Path.Combine(temporary, "batch-roster.json");
            File.WriteAllText(json, "[{\"id\":4000,\"name\":\"First added wrestler\"},{\"id\":1506,\"name\":\"Flammer\"}]");
            Check("An explicitly selected JSON keeps its names and current roster membership", () => {
                RosterSessionData session = BatchRosterLoading.Open(fixture, null, json, null);
                Equal("1506,4000", string.Join(",", session.Entries.Select(row => row.Id).OrderBy(id => id)));
                Equal("First added wrestler", session.Entries.Single(row => row.Id == 4000).Name);
            });
            File.WriteAllText(json, "[{\"id\":65527,\"name\":\"Second added wrestler\"}]");
            Check("Reopening the same JSON sees additions/removals without stale batch rows", () => {
                RosterSessionData session = BatchRosterLoading.Open(fixture, current.Entries, json, null);
                Equal(1, session.Entries.Count); Equal(65527, session.Entries.Single().Id); Equal("Second added wrestler", session.Entries.Single().Name);
            });
            Check("Concurrent independent batch loads never cross-contaminate roots, rows or warnings", () => {
                var sessions = Task.WhenAll(Task.Run(() => BatchRosterLoading.Open(fixture, null, fixtureTable, null)),
                    Task.Run(() => BatchRosterLoading.Open(grown, null, grownTable, null)),
                    Task.Run(() => BatchRosterLoading.Open(null, null, json, null))).GetAwaiter().GetResult();
                Equal(1457, sessions[0].Entries.Count); Equal(1458, sessions[1].Entries.Count); Equal(1, sessions[2].Entries.Count);
                Equal(fixtureTable, sessions[0].DiscoveryTable); Equal(grownTable, sessions[1].DiscoveryTable); Equal((string)null, sessions[2].DiscoveryTable);
                Equal(0, sessions[2].Warnings.Count); Equal(false, ReferenceEquals(sessions[0].Warnings, sessions[1].Warnings));
            });
            string invalid = Metadata("invalid-profile"); File.WriteAllText(Table(invalid), "broken profile table");
            Check("Broken profile metadata remains an actionable error rather than a stale roster", () => {
                Throws<InvalidDataException>(() => BatchRosterLoading.Open(invalid, current.Entries, Table(invalid), null));
            });
            string missing = Path.Combine(temporary, "missing-companions"); CopyFile(sourceTable, missing, "Roster", "CharProfileTable.jsfb");
            Check("Missing companion Characters metadata is not disguised as an SDB warning", () => {
                Throws<InvalidDataException>(() => BatchRosterLoading.Open(missing, null, Table(missing), null));
            });
            Check("An absent saved roster file recovers the actual current Discovery roster", () => {
                RosterSessionData session = BatchRosterLoading.Open(fixture, null, Path.Combine(temporary, "absent.json"), null);
                Equal(1457, session.Entries.Count); Equal(fixtureTable, session.DiscoveryTable); Equal(true, session.Warnings.Count > 0);
            });
            Check("All uploaded source metadata and the damaged local test input remain unchanged", () => {
                foreach (var pair in originalHashes) Equal(pair.Value, Hash(pair.Key)); Equal(badHash, Hash(sdb));
            });
            Console.WriteLine("PASS " + passed + " batch roster loading checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(temporary, true); } catch (IOException) { } }
    }

    private static RosterEntry Copy(RosterEntry row) { return new RosterEntry { Id = row.Id, Name = row.Name, Folder = row.Folder, SourceId = row.SourceId, RosterTable = row.RosterTable, Source = row.Source, IsMod = row.IsMod }; }
    private static string Metadata(string name) { string root = Path.Combine(temporary, name); CopyFile(sourceTable, root, "Roster", "CharProfileTable.jsfb"); CopyFile(sourceMapping, root, "Characters", "CharacterMapping.jsfb"); CopyFile(sourceSdb, root, "Sdb", "ENG.sdb"); return root; }
    private static string Table(string root) { return Path.Combine(root, "Roster", "CharProfileTable.jsfb"); }
    private static string CopyFile(string source, string root, params string[] parts) { string path = parts.Aggregate(root, Path.Combine); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(source, path, true); return path; }
    private static string Hash(string path) { using (var input = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(input)); }
    private static void CorruptOffset(string path, uint key)
    {
        byte[] bytes = File.ReadAllBytes(path); uint count = BitConverter.ToUInt32(bytes, 4);
        for (int index = 0; index < count; index++) if (BitConverter.ToUInt32(bytes, 16 + index * 12) == key) { Write32(bytes, 8 + index * 12, uint.MaxValue); File.WriteAllBytes(path, bytes); return; }
        throw new Exception("Localization fixture key was not found: " + key);
    }
    private static uint UnusedKey(Dictionary<uint, string> strings) { uint key = 0xFDFDFDFD; while (strings.ContainsKey(key)) key--; return key; }
    private static byte[] ExtendProfiles(byte[] original, ushort id, ushort baseId, uint nameKey)
    {
        var file = new FlatBufferFile(original, "Char"); int[] rows = file.Tables(file.Root, 0).ToArray();
        int prefix = Align(28 + (rows.Length + 1) * 4), wrapperVtable = Align(prefix + original.Length), wrapper = wrapperVtable + 8;
        int profileVtable = wrapper + 8, profile = profileVtable + 36; byte[] bytes = new byte[profile + 16];
        Write32(bytes, 0, 16); Encoding.ASCII.GetBytes("Char").CopyTo(bytes, 4);
        Write16(bytes, 8, 6); Write16(bytes, 10, 8); Write16(bytes, 12, 4); Write32(bytes, 16, 8); Write32(bytes, 20, 4); Write32(bytes, 24, (uint)(rows.Length + 1));
        for (int index = 0; index < rows.Length; index++) { int element = 28 + index * 4; Write32(bytes, element, (uint)(prefix + rows[index] - element)); }
        int newElement = 28 + rows.Length * 4; Write32(bytes, newElement, (uint)(wrapper - newElement)); Array.Copy(original, 0, bytes, prefix, original.Length);
        Write16(bytes, wrapperVtable, 8); Write16(bytes, wrapperVtable + 2, 8); Write16(bytes, wrapperVtable + 6, 4);
        Write32(bytes, wrapper, 8); Write32(bytes, wrapper + 4, (uint)(profile - wrapper - 4));
        Write16(bytes, profileVtable, 36); Write16(bytes, profileVtable + 2, 16);
        Write16(bytes, profileVtable + 4 + 2 * 2, 4); Write16(bytes, profileVtable + 4 + 2 * 3, 6); Write16(bytes, profileVtable + 4 + 2 * 7, 8); Write16(bytes, profileVtable + 4 + 2 * 15, 12);
        Write32(bytes, profile, 36); Write16(bytes, profile + 4, id); Write16(bytes, profile + 6, baseId); Write32(bytes, profile + 8, nameKey); bytes[profile + 12] = 1; return bytes;
    }
    private static void WriteString(string path, uint key, string value)
    {
        byte[] header = File.ReadAllBytes(path).Take(4).ToArray(); Dictionary<uint, string> strings = DiscoveryProfiles.ReadStrings(path); strings[key] = value;
        using (var output = new MemoryStream()) using (var writer = new BinaryWriter(output, new UTF8Encoding(false), true)) {
            writer.Write(header); writer.Write((uint)strings.Count); uint offset = checked(8U + (uint)strings.Count * 12U);
            var texts = strings.Select(pair => new { pair.Key, Bytes = Encoding.UTF8.GetBytes(pair.Value) }).ToList();
            foreach (var text in texts) { writer.Write(offset); writer.Write((uint)text.Bytes.Length); writer.Write(text.Key); offset = checked(offset + (uint)text.Bytes.Length + 1); }
            foreach (var text in texts) { writer.Write(text.Bytes); writer.Write((byte)0); } File.WriteAllBytes(path, output.ToArray());
        }
    }
    private static int Align(int value) { return (value + 15) & ~15; }
    private static void Write16(byte[] bytes, int at, ushort value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
    private static void Write32(byte[] bytes, int at, uint value) { for (int shift = 0; shift < 4; shift++) bytes[at + shift] = (byte)(value >> (8 * shift)); }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name, error); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
    private static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
}
