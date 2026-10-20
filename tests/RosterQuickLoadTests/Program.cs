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
    private static string temporary, originalRoot, originalTable, originalMapping, originalSdb, stockIndex;
    private static int passed;

    private static int Main(string[] args)
    {
        originalRoot = Path.GetFullPath(args.Length > 0 ? args[0] : "/workspace/discovery-work/discovery_data");
        stockIndex = Path.GetFullPath(args.Length > 1 ? args[1] : "/workspace/gorilla-v102-work/original/GorillaStudio/data/gamepack/stock_index.json");
        originalTable = Path.Combine(originalRoot, "Roster", "CharProfileTable.jsfb");
        originalMapping = Path.Combine(originalRoot, "Characters", "CharacterMapping.jsfb");
        originalSdb = DiscoveryProfiles.FindEnglishSdb(originalRoot);
        var originals = new[] { originalTable, originalMapping, originalSdb, stockIndex }.ToDictionary(path => path, Hash);
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-roster-quick-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            List<DiscoveryProfile> profiles = DiscoveryProfiles.Read(originalTable);
            List<RosterEntry> quick = RosterImport.LoadDiscoveryQuick(originalTable, originalSdb, stockIndex);
            Check("Quick import reads all 1,457 current uploaded profiles without extra inferred rows", () => {
                Equal(1457, profiles.Count); Equal(profiles.Count, quick.Count);
                Equal(true, profiles.Select(profile => profile.Id).OrderBy(id => id).SequenceEqual(quick.Select(entry => entry.Id).OrderBy(id => id)));
                Equal(true, quick.All(entry => entry.Id > 0 && string.IsNullOrWhiteSpace(entry.Folder) && !entry.IsMod));
            });
            Check("Quick import keeps actual SDB names and exact roster context", () => {
                foreach (DiscoveryProfile profile in profiles) {
                    RosterEntry row = quick.Single(entry => entry.Id == profile.Id);
                    Equal(profile.Name ?? "Wrestler " + profile.Id, row.Name); Equal(originalTable, row.RosterTable);
                }
                Equal("\"DEMON\" FINN BÁLOR", quick.Single(entry => entry.Id == 626).Name);
                Equal("CM PUNK", quick.Single(entry => entry.Id == 1481).Name);
            });
            Check("Quick stock availability matches the established exact path-hash join", () => {
                Dictionary<int, string> expected = StockModels.Read(originalRoot, stockIndex);
                var profileIds = new HashSet<int>(profiles.Select(profile => profile.Id));
                Equal(expected.Count(pair => profileIds.Contains(pair.Key)), quick.Count(entry => entry.StockFolder != null));
                foreach (var pair in expected.Where(pair => profileIds.Contains(pair.Key))) Equal(pair.Value, quick.Single(entry => entry.Id == pair.Key).StockFolder);
                Equal(true, quick.Where(entry => entry.StockFolder != null).All(entry => expected.ContainsKey(entry.Id)));
            });
            Check("Quick import can omit an unavailable stock index without guessing game availability", () => {
                List<RosterEntry> rows = RosterImport.LoadDiscoveryQuick(originalTable, originalSdb, Path.Combine(temporary, "missing-stock-index.json"));
                Equal(1457, rows.Count); Equal(true, rows.All(entry => entry.StockFolder == null && string.IsNullOrWhiteSpace(entry.Folder)));
                Equal(576, rows.Single(entry => entry.Id == 626).SourceId);
            });
            Check("LoadFileQuick recognizes the actual JSFB roster", () => {
                List<RosterEntry> rows = RosterImport.LoadFileQuick(originalTable);
                Equal(1457, rows.Count); Equal(true, rows.All(entry => entry.RosterTable == originalTable && string.IsNullOrWhiteSpace(entry.Folder)));
            });
            Check("Quick session accepts Discovery, Characters and Roster entry points", () => {
                foreach (string source in new[] { originalRoot, Path.Combine(originalRoot, "Characters"), Path.Combine(originalRoot, "Roster") }) {
                    RosterSessionData session = RosterSession.OpenQuick(source, null);
                    Equal(1457, session.Entries.Count); Equal(originalTable, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
                    Equal(true, session.Entries.All(entry => string.IsNullOrWhiteSpace(entry.Folder) && !entry.IsMod));
                }
            });
            Check("Quick launch opens current Discovery and an explicit JSFB without a model-folder prompt", () => {
                foreach (RosterSessionData session in new[] {
                    RosterLaunch.OpenQuick(originalRoot, null, null, null), RosterLaunch.OpenQuick(null, null, originalTable, null)
                }) {
                    Equal(1457, session.Entries.Count); Equal(originalTable, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
                }
            });

            string model = Directory.CreateDirectory(Path.Combine(temporary, "Chosen models", "1506_Flammer")).FullName;
            File.WriteAllText(Path.Combine(model, "Attire.mcd"), "model path fixture");
            string name = quick.Single(entry => entry.Id == 1506).Name;
            var explicitChoice = Saved(1506, name, model, originalTable, "Chosen model");
            Check("A current explicitly chosen ID/name/context binding survives quick refresh", () => {
                RosterEntry row = RosterSession.RefreshQuick(originalTable, new[] { explicitChoice }).Single(entry => entry.Id == 1506);
                Equal(model, row.Folder); Equal(1506, row.SourceId); Equal(true, row.IsMod); Equal("Chosen model", row.Source);
            });
            Check("Quick launch copies saved choices and never mutates them while merging", () => {
                string before = JsonSerializer.Serialize(explicitChoice);
                RosterEntry row = RosterLaunch.OpenQuick(originalRoot, new[] { explicitChoice }, null, null).Entries.Single(entry => entry.Id == 1506);
                Equal(model, row.Folder); Equal(false, ReferenceEquals(row, explicitChoice)); row.Name = "Canceled picker edit";
                Equal(before, JsonSerializer.Serialize(explicitChoice));
            });
            Check("Inferred and unknown legacy bindings wait for fresh background inventory", () => {
                foreach (string source in new[] { "Character mapping", "Name match", "Found in models folder", "Folder ID", "NotChosen", null }) {
                    RosterEntry row = RosterSession.RefreshQuick(originalTable, new[] { Saved(1506, name, model, originalTable, source) }).Single(entry => entry.Id == 1506);
                    Equal((string)null, row.Folder); Equal(false, row.IsMod);
                }
            });
            Check("A matching ID in another roster context cannot restore its chosen model", () => {
                var foreign = Saved(1506, name, model, Path.Combine(temporary, "Another roster", "CharProfileTable.jsfb"), "Chosen model");
                RosterEntry row = RosterSession.RefreshQuick(originalTable, new[] { foreign }).Single(entry => entry.Id == 1506);
                Equal((string)null, row.Folder); Equal(originalTable, row.RosterTable);
            });
            Check("A renamed logical slot cannot inherit its previous chosen model", () => {
                var renamed = Saved(1506, "An older wrestler name", model, originalTable, "Chosen model");
                RosterEntry row = RosterSession.RefreshQuick(originalTable, new[] { renamed }).Single(entry => entry.Id == 1506);
                Equal(name, row.Name); Equal((string)null, row.Folder);
            });
            Check("A missing chosen model folder is not presented as available", () => {
                var missing = Saved(1506, name, Path.Combine(temporary, "no-such-model"), originalTable, "Chosen model");
                Equal((string)null, RosterSession.RefreshQuick(originalTable, new[] { missing }).Single(entry => entry.Id == 1506).Folder);
            });
            Check("Conflicting explicit model bindings do not select one arbitrarily", () => {
                string alternate = Directory.CreateDirectory(Path.Combine(temporary, "Other chosen model")).FullName;
                var choices = new[] { explicitChoice, Saved(1506, name, alternate, originalTable, "Chosen model") };
                Equal((string)null, RosterSession.RefreshQuick(originalTable, choices).Single(entry => entry.Id == 1506).Folder);
            });

            string fixture = Metadata("growth"); string table = Table(fixture); string sdb = Path.Combine(fixture, "Sdb", "ENG.sdb");
            uint newKey = UnusedKey(DiscoveryProfiles.ReadStrings(sdb));
            File.WriteAllBytes(table, ExtendProfiles(File.ReadAllBytes(table), 4000, 1506, newKey));
            Check("Quick refresh sees a newly added profile before its localization is supplied", () => {
                List<RosterEntry> rows = RosterSession.RefreshQuick(table, null);
                Equal(1458, rows.Count); Equal("Wrestler 4000", rows.Single(entry => entry.Id == 4000).Name);
            });
            WriteString(sdb, newKey, "QUICK LOAD WRESTLER");
            Check("A later SDB edit supplies the newly added character's current name", () => {
                RosterEntry row = RosterSession.RefreshQuick(table, null).Single(entry => entry.Id == 4000);
                Equal("QUICK LOAD WRESTLER", row.Name); Equal(1506, row.SourceId); Equal(table, row.RosterTable); Equal((string)null, row.Folder);
            });
            var grownChoice = Saved(4000, "QUICK LOAD WRESTLER", model, table, "Chosen model");
            DateTime oldWrite = File.GetLastWriteTimeUtc(sdb); long oldLength = new FileInfo(sdb).Length;
            WriteString(sdb, newKey, "QUICK NAME WRESTLER"); File.SetLastWriteTimeUtc(sdb, oldWrite);
            Check("Same-path, same-size SDB edits reread names and discard stale chosen bindings", () => {
                Equal(oldLength, new FileInfo(sdb).Length);
                RosterEntry row = RosterSession.RefreshQuick(table, new[] { grownChoice }).Single(entry => entry.Id == 4000);
                Equal("QUICK NAME WRESTLER", row.Name); Equal((string)null, row.Folder);
            });
            uint boundaryKey = newKey - 1; File.WriteAllBytes(table, ExtendProfiles(File.ReadAllBytes(table), 65527, 1506, boundaryKey)); WriteString(sdb, boundaryKey, "QUICK BOUNDARY WRESTLER");
            Check("New high logical IDs load from the changed table without a fixed roster-size limit", () => {
                List<RosterEntry> rows = RosterSession.RefreshQuick(table, null);
                Equal(1459, rows.Count); Equal("QUICK BOUNDARY WRESTLER", rows.Single(entry => entry.Id == 65527).Name);
            });

            string trapRoot = Metadata("tree-trap");
            string trap = Directory.CreateDirectory(Path.Combine(trapRoot, "Characters", "TraversalTrap")).FullName;
            string deep = trap; for (int i = 0; i < 72; i++) deep = Directory.CreateDirectory(Path.Combine(deep, "deep")).FullName;
            File.WriteAllText(Path.Combine(deep, "Attire.mcd"), "must not be indexed by quick metadata loading");
            string animationTrap = Directory.CreateDirectory(Path.Combine(trapRoot, "Animation", "TraversalTrap")).FullName;
            File.WriteAllText(Path.Combine(animationTrap, "motion.clip"), "must not be scanned by the roster phase");
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateSymbolicLink(Path.Combine(trap, "Loop"), trap);
                DateTime stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Directory.SetLastAccessTimeUtc(trap, stamp); DateTime beforeAccess = Directory.GetLastAccessTimeUtc(trap);
                Directory.GetFileSystemEntries(trap);
                Check("The Linux directory-access probe detects a real model-tree enumeration", () => Equal(false, beforeAccess == Directory.GetLastAccessTimeUtc(trap)));
                Directory.SetLastAccessTimeUtc(trap, stamp); Directory.SetLastAccessTimeUtc(animationTrap, stamp);
                DateTime modelBefore = Directory.GetLastAccessTimeUtc(trap), animationBefore = Directory.GetLastAccessTimeUtc(animationTrap);
                RosterImport.LoadDiscoveryQuick(Table(trapRoot), null, stockIndex);
                RosterSession.OpenQuick(trapRoot, null);
                RosterLaunch.OpenQuick(trapRoot, null, null, null);
                Check("Quick import, session and launch do not traverse deep linked model or animation trees", () => {
                    Equal(modelBefore, Directory.GetLastAccessTimeUtc(trap)); Equal(animationBefore, Directory.GetLastAccessTimeUtc(animationTrap));
                });
                var unnumberedChoice = Saved(1506, name, trap, Table(trapRoot), "Chosen model"); unnumberedChoice.SourceId = 0;
                RosterEntry retained = RosterSession.RefreshQuick(Table(trapRoot), new[] { unnumberedChoice }).Single(entry => entry.Id == 1506);
                Check("An explicit choice with no saved physical ID is retained without scanning its attire children", () => {
                    Equal(trap, retained.Folder); Equal(modelBefore, Directory.GetLastAccessTimeUtc(trap));
                });
                File.Delete(Path.Combine(trap, "Loop"));
            }
            else Check("Quick metadata loading ignores deeply nested character model inventory", () => {
                Equal(1457, RosterImport.LoadDiscoveryQuick(Table(trapRoot), null, stockIndex).Count);
                Equal(true, RosterSession.OpenQuick(trapRoot, null).Entries.All(entry => string.IsNullOrWhiteSpace(entry.Folder)));
            });

            string jsonPath = Path.Combine(temporary, "roster.json");
            File.WriteAllText(jsonPath, "[{\"id\":1506,\"name\":\"JSON WRESTLER\"},{\"id\":4000,\"name\":\"NEW JSON WRESTLER\"}]");
            Check("Quick JSON import retains the file's current membership and names", () => {
                Equal("1506,4000", string.Join(",", RosterImport.LoadFileQuick(jsonPath).Select(entry => entry.Id).OrderBy(id => id)));
                Equal("NEW JSON WRESTLER", RosterImport.LoadFileQuick(jsonPath).Single(entry => entry.Id == 4000).Name);
            });
            var jsonChoice = Saved(1506, "JSON WRESTLER", model, originalTable, "Chosen model");
            Check("Quick JSON launch retains an explicit matching choice and Discovery context", () => {
                RosterSessionData session = RosterLaunch.OpenQuick(originalRoot, new[] { jsonChoice }, jsonPath, null);
                Equal(2, session.Entries.Count); Equal(originalTable, session.DiscoveryTable); Equal(model, session.Entries.Single(entry => entry.Id == 1506).Folder);
            });
            File.WriteAllText(jsonPath, "[{\"id\":1506,\"name\":\"RENAMED JSON WRESTLER\"},{\"id\":65527,\"name\":\"LATER JSON WRESTLER\"}]");
            Check("Quick JSON refresh reads additions, removals and renames without restoring old membership", () => {
                List<RosterEntry> rows = RosterSession.RefreshQuick(jsonPath, new[] { jsonChoice }, originalTable);
                Equal("1506,65527", string.Join(",", rows.Select(entry => entry.Id).OrderBy(id => id)));
                Equal("RENAMED JSON WRESTLER", rows.Single(entry => entry.Id == 1506).Name); Equal((string)null, rows.Single(entry => entry.Id == 1506).Folder);
            });
            var oldStockChoice = Saved(1506, "JSON WRESTLER", model, originalTable, "Chosen model");
            oldStockChoice.StockFolder = "194_Doctor_Trainer_2017";
            Check("Quick JSON refresh cannot carry a renamed chosen model through an obsolete stock mapping", () => {
                RosterEntry row = RosterSession.RefreshQuick(jsonPath, new[] { oldStockChoice }, originalTable).Single(entry => entry.Id == 1506);
                Equal("RENAMED JSON WRESTLER", row.Name); Equal((string)null, row.Folder); Equal(0, row.SourceId); Equal((string)null, row.StockFolder);
            });
            Check("Quick JSON launch cannot carry a renamed chosen model through an obsolete stock mapping", () => {
                RosterEntry row = RosterLaunch.OpenQuick(originalRoot, new[] { oldStockChoice }, jsonPath, null).Entries.Single(entry => entry.Id == 1506);
                Equal("RENAMED JSON WRESTLER", row.Name); Equal((string)null, row.Folder); Equal(0, row.SourceId); Equal((string)null, row.StockFolder);
            });
            Check("Clearing obsolete stock evidence retains an explicit same-identity chosen folder", () => {
                string sameIdentity = Path.Combine(temporary, "same-identity-with-old-stock.json");
                File.WriteAllText(sameIdentity, "[{\"id\":1506,\"name\":\"JSON WRESTLER\"}]");
                string before = JsonSerializer.Serialize(oldStockChoice);
                foreach (RosterEntry row in new[] {
                    RosterSession.RefreshQuick(sameIdentity, new[] { oldStockChoice }, originalTable).Single(),
                    RosterLaunch.OpenQuick(originalRoot, new[] { oldStockChoice }, sameIdentity, null).Entries.Single()
                }) { Equal(model, row.Folder); Equal(1506, row.SourceId); Equal("Chosen model", row.Source); }
                Equal(before, JsonSerializer.Serialize(oldStockChoice));
            });
            Check("Quick JSON launch does not reuse a same-name choice from another roster", () => {
                string foreignJson = Path.Combine(temporary, "foreign-roster.json");
                File.WriteAllText(foreignJson, JsonSerializer.Serialize(new[] { new RosterEntry { Id = 1506, Name = name, RosterTable = Path.Combine(temporary, "foreign", "CharProfileTable.jsfb") } }));
                Equal((string)null, RosterLaunch.OpenQuick(originalRoot, new[] { explicitChoice }, foreignJson, null).Entries.Single().Folder);
            });
            Check("Malformed profile metadata remains an error instead of returning stale names", () => {
                string invalid = Metadata("invalid-profile"); File.WriteAllText(Table(invalid), "damaged");
                Throws<InvalidDataException>(() => RosterImport.LoadDiscoveryQuick(Table(invalid), null, stockIndex));
            });
            Check("Malformed localization remains an error instead of retaining stale labels", () => {
                string invalid = Metadata("invalid-sdb"); File.WriteAllBytes(Path.Combine(invalid, "Sdb", "ENG.sdb"), new byte[3]);
                Throws<InvalidDataException>(() => RosterImport.LoadDiscoveryQuick(Table(invalid), null, stockIndex));
            });
            string recovering = Metadata("recovering-sdb"); File.WriteAllBytes(Path.Combine(recovering, "Sdb", "ENG.sdb"), new byte[3]);
            Check("An explicit warnings collector recovers roster IDs from unusable localization", () => {
                var warnings = new List<string>(); List<RosterEntry> rows = RosterImport.LoadDiscoveryQuick(Table(recovering), null, stockIndex, warnings);
                Equal(1457, rows.Count); Equal("Wrestler 626", rows.Single(entry => entry.Id == 626).Name); Equal(true, warnings.Count > 0);
                Equal(492, rows.Count(entry => entry.StockFolder != null));
            });
            Check("Quick refresh propagates localization warnings and placeholder names", () => {
                var warnings = new List<string>(); List<RosterEntry> rows = RosterSession.RefreshQuick(Table(recovering), null, null, warnings);
                Equal(1457, rows.Count); Equal("Wrestler 626", rows.Single(entry => entry.Id == 626).Name); Equal(true, warnings.Count > 0);
            });
            Check("Quick session surfaces localization recovery warnings", () => {
                RosterSessionData session = RosterSession.OpenQuick(recovering, null);
                Equal(1457, session.Entries.Count); Equal("Wrestler 626", session.Entries.Single(entry => entry.Id == 626).Name);
                Equal(true, session.Warnings != null && session.Warnings.Count > 0);
            });
            Check("Quick launch carries localization recovery warnings to its caller", () => {
                RosterSessionData session = RosterLaunch.OpenQuick(recovering, null, null, null);
                Equal(1457, session.Entries.Count); Equal("Wrestler 626", session.Entries.Single(entry => entry.Id == 626).Name);
                Equal(true, session.Warnings != null && session.Warnings.Count > 0);
            });
            Check("An absent optional SDB preserves roster IDs with a visible localization warning", () => {
                string missing = Metadata("missing-sdb"); File.Delete(Path.Combine(missing, "Sdb", "ENG.sdb")); var warnings = new List<string>();
                List<RosterEntry> rows = RosterImport.LoadDiscoveryQuick(Table(missing), null, stockIndex, warnings);
                Equal(1457, rows.Count); Equal("Wrestler 626", rows.Single(entry => entry.Id == 626).Name); Equal(true, warnings.Any(warning => warning.Contains("ENG.sdb")));
            });
            Check("A damaged mapping cannot claim installed-game availability", () => {
                string invalid = Metadata("invalid-mapping"); File.WriteAllText(Path.Combine(invalid, "Characters", "CharacterMapping.jsfb"), "damaged");
                Throws<InvalidDataException>(() => RosterImport.LoadDiscoveryQuick(Table(invalid), null, stockIndex));
            });
            Check("A missing Characters companion remains an actionable import error", () => {
                string missing = Path.Combine(temporary, "missing-characters"); string target = Copy(originalTable, missing, "Roster", "CharProfileTable.jsfb");
                Throws<InvalidDataException>(() => RosterImport.LoadDiscoveryQuick(target, originalSdb, stockIndex));
            });
            Check("Quick file import rejects damaged JSON instead of showing the previous roster", () => {
                string invalid = Path.Combine(temporary, "invalid.json"); File.WriteAllText(invalid, "{broken");
                Throws<InvalidDataException>(() => RosterImport.LoadFileQuick(invalid));
            });
            Check("Original uploaded metadata and supplied stock index remain byte-for-byte unchanged", () => {
                foreach (var original in originals) Equal(original.Value, Hash(original.Key));
            });
            Console.WriteLine("PASS " + passed + " quick roster load checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(temporary, true); } catch (IOException) { } }
    }

    private static RosterEntry Saved(int id, string name, string folder, string table, string source)
    { return new RosterEntry { Id = id, Name = name, Folder = folder, RosterTable = table, SourceId = 1506, IsMod = true, Source = source }; }
    private static string Metadata(string name)
    {
        string root = Path.Combine(temporary, name); Copy(originalTable, root, "Roster", "CharProfileTable.jsfb");
        Copy(originalMapping, root, "Characters", "CharacterMapping.jsfb"); Copy(originalSdb, root, "Sdb", "ENG.sdb"); return root;
    }
    private static string Table(string root) { return Path.Combine(root, "Roster", "CharProfileTable.jsfb"); }
    private static string Copy(string source, string root, params string[] parts)
    { string path = parts.Aggregate(root, Path.Combine); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(source, path, true); return path; }
    private static string Hash(string path) { using (var input = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(input)); }
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
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output, new UTF8Encoding(false), true))
        {
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
