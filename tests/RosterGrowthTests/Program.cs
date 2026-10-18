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

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: RosterGrowthTests <extracted Discovery data directory>"); return 2; }
        string originalRoot = Path.GetFullPath(args[0]);
        string originalTable = Path.Combine(originalRoot, "Roster", "CharProfileTable.jsfb");
        string originalMapping = Path.Combine(originalRoot, "Characters", "CharacterMapping.jsfb");
        string originalSdb = DiscoveryProfiles.FindEnglishSdb(originalRoot);
        string originalModel = Path.Combine(originalRoot, "Characters", "1506_Flammer", "Default_Attire", "Attire.mcd");
        var originals = new[] { originalTable, originalMapping, originalSdb, originalModel }.ToDictionary(path => path, Hash);
        string temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-roster-growth-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string data = Directory.CreateDirectory(Path.Combine(temporary, "data")).FullName;
            string table = Copy(originalTable, data, "Roster", "CharProfileTable.jsfb");
            Copy(originalMapping, data, "Characters", "CharacterMapping.jsfb");
            string sdb = Copy(originalSdb, data, "Sdb", "ENG.sdb");
            string library = Directory.CreateDirectory(Path.Combine(temporary, "Extracted Mods")).FullName;
            List<RosterEntry> baseline = RosterSession.Open(data, null).Entries;
            const int newId = 4000;
            const ushort boundaryId = 65527;
            const string newName = "GROWTH TEST WRESTLER";
            uint newKey = UnusedKey(DiscoveryProfiles.ReadStrings(sdb));
            File.WriteAllBytes(table, ExtendProfiles(File.ReadAllBytes(table), newId, 1506, newKey));
            string model = Copy(originalModel, library, newName, "1506_Default_Attire", "Attire.mcd");
            List<RosterEntry> waitingForSdb = RosterSession.Refresh(table, baseline);
            RosterEntry placeholder = waitingForSdb.Single(entry => entry.Id == newId);
            Equal("Wrestler 4000", placeholder.Name);
            RosterSession.RememberSelection(placeholder, Directory.GetParent(model).Parent.FullName, true);
            placeholder.Source = "Manual selection"; placeholder.IsMod = true;
            WriteString(sdb, newKey, newName);
            List<RosterEntry> refreshed = RosterSession.Refresh(table, baseline);
            Check("Refreshing the same JSFB path discovers a newly added profile and SDB name", () => {
                Equal(1457, baseline.Count); // This copied fixture contains the profile tables, not all original models.
                Equal(1457, baseline.Count(entry => entry.Id > 0));
                Equal(1457, baseline.Select(entry => entry.Id).Distinct().Count());
                Equal(false, baseline.Any(entry => entry.Id == newId || entry.Id == boundaryId));
                Equal(1458, refreshed.Count);
                Equal(1458, refreshed.Count(entry => entry.Id > 0));
                RosterEntry added = refreshed.Single(entry => entry.Id == newId);
                Equal(newName, added.Name); Equal(1506, added.SourceId); Equal(table, added.RosterTable);
                Equal(true, DiscoveryProfiles.Read(table).Single(profile => profile.Id == newId).Female);
                foreach (RosterEntry prior in baseline)
                    Equal(prior.Name, refreshed.Single(entry => entry.Id == prior.Id).Name);
            });

            List<ModelCandidate> candidates = ModelLibrary.Scan(library);
            ModelLibraryBindingResult binding = ModelLibraryBinding.Apply(refreshed, candidates, table, false);
            RosterEntry addedEntry = refreshed.Single(entry => entry.Id == newId);
            Check("Rescanning binds the real added plain-name model while preserving its logical roster ID", () => {
                Equal(1, candidates.Count);
                Equal(newName, candidates[0].Name); Equal(1506, candidates[0].SourceId);
                Equal(Directory.GetParent(model).Parent.FullName, candidates[0].Folder);
                Equal(Hash(originalModel), Hash(model));
                Equal(1, binding.BoundCount); Equal(0, binding.Choices.Count); Equal(0, binding.AddedCount);
                Equal(4000, addedEntry.Id); Equal(1506, addedEntry.SourceId); Equal(true, addedEntry.IsMod);
                Equal(candidates[0].Folder, addedEntry.Folder);
            });
            string saved = Path.Combine(temporary, "saved-roster.json");
            File.WriteAllText(saved, JsonSerializer.Serialize(refreshed));
            List<RosterEntry> reopened = RosterSession.Open(data, RosterImport.LoadFile(saved)).Entries;
            Check("Saving and reopening retains the added character, physical model and Discovery render context", () => {
                CharacterFolderPlan plan = CharacterFolders.Plan(addedEntry.Folder, addedEntry);
                Equal("1506_GROWTH_TEST_WRESTLER", plan.AliasName);
                Equal(Path.GetDirectoryName(table), plan.RosterDirectory); Equal(data, plan.DataRoot);
                Equal(candidates[0].Folder, plan.SourceFolder); Equal(4000, addedEntry.Id);
                Equal(1458, reopened.Count);
                Equal(1458, reopened.Count(entry => entry.Id > 0));
                RosterEntry entry = reopened.Single(item => item.Id == newId);
                Equal(newName, entry.Name); Equal(4000, entry.Id); Equal(1506, entry.SourceId);
                Equal(candidates[0].Folder, entry.Folder); Equal(table, entry.RosterTable); Equal(true, entry.IsMod);
            });

            const string renamed = "GROWTH TEST WRESTLER RENAMED";
            WriteString(sdb, newKey, renamed);
            List<RosterEntry> renamedEntries = RosterSession.Refresh(table, reopened);
            List<RosterEntry> namedPlaceholder = RosterSession.Refresh(table, waitingForSdb);
            Check("Late SDB names replace stale rows and a model rescan binds their matching models", () => {
                Equal(1458, renamedEntries.Count);
                RosterEntry entry = renamedEntries.Single(item => item.Id == newId);
                Equal(renamed, entry.Name); Equal((string)null, entry.Folder); Equal(1506, entry.SourceId);
                Equal(1458, namedPlaceholder.Count);
                RosterEntry resolved = namedPlaceholder.Single(item => item.Id == newId);
                Equal(renamed, resolved.Name); Equal((string)null, resolved.Folder); Equal(1506, resolved.SourceId);
                string renamedModelFolder = Path.Combine(library, renamed);
                Directory.Move(candidates[0].Folder, renamedModelFolder);
                List<ModelCandidate> renamedModels = ModelLibrary.Scan(library);
                Equal(1, ModelLibraryBinding.Apply(renamedEntries, renamedModels, table, false).BoundCount);
                Equal(1, ModelLibraryBinding.Apply(namedPlaceholder, renamedModels, table, false).BoundCount);
                Equal(renamedModelFolder, entry.Folder); Equal(renamedModelFolder, resolved.Folder);
                Equal(4000, entry.Id); Equal(1506, entry.SourceId);
            });

            uint boundaryKey = UnusedKey(DiscoveryProfiles.ReadStrings(sdb));
            File.WriteAllBytes(table, ExtendProfiles(File.ReadAllBytes(table), boundaryId, 1506, boundaryKey));
            WriteString(sdb, boundaryKey, "GROWTH BOUNDARY WRESTLER");
            List<RosterEntry> extendedAgain = RosterSession.Refresh(table, renamedEntries);
            Check("A later addition at ID 65527 loads without a cached roster size or stock-ID limit", () => {
                Equal(1459, extendedAgain.Count);
                Equal(1459, extendedAgain.Count(entry => entry.Id > 0));
                Equal("GROWTH BOUNDARY WRESTLER", extendedAgain.Single(entry => entry.Id == boundaryId).Name);
                Equal(baseline.Single(entry => entry.Id == ushort.MaxValue).Name, extendedAgain.Single(entry => entry.Id == ushort.MaxValue).Name);
                Equal(renamed, extendedAgain.Single(entry => entry.Id == newId).Name);
            });

            string json = Path.Combine(temporary, "external-roster.json");
            File.WriteAllText(json, "[{\"id\":4000,\"name\":\"GROWTH TEST WRESTLER\"}]");
            List<RosterEntry> jsonBefore = RosterSession.Refresh(json, null, table);
            Copy(originalModel, library, newName, "1506_Default_Attire", "Attire.mcd");
            int initiallyBound = ModelLibraryBinding.Apply(jsonBefore, ModelLibrary.Scan(library), table, false).BoundCount;
            string oldFolder = jsonBefore.Single(entry => entry.Id == newId).Folder;
            File.WriteAllText(json, "[{\"id\":4000,\"name\":\"GROWTH TEST WRESTLER RENAMED\"},{\"id\":65527,\"name\":\"GROWTH BOUNDARY WRESTLER\"}]");
            List<RosterEntry> jsonAfter = RosterSession.Refresh(json, jsonBefore, table);
            Check("Reloading an edited JSON roster sees new explicit IDs at the same file path", () => {
                Equal(1, jsonBefore.Count); Equal(2, jsonAfter.Count);
                Equal(1, initiallyBound); Equal(Path.Combine(library, newName), oldFolder);
                Equal("Found in models folder", jsonBefore.Single(entry => entry.Id == newId).Source);
                Equal(true, Directory.Exists(oldFolder));
                Equal("GROWTH BOUNDARY WRESTLER", jsonAfter.Single(entry => entry.Id == boundaryId).Name);
                Equal(renamed, jsonAfter.Single(entry => entry.Id == newId).Name);
                Equal((string)null, jsonAfter.Single(entry => entry.Id == newId).Folder);
                Equal(1, ModelLibraryBinding.Apply(jsonAfter, ModelLibrary.Scan(library), table, false).BoundCount);
                RosterEntry matched = jsonAfter.Single(entry => entry.Id == newId);
                Equal(Path.Combine(library, renamed), matched.Folder); Equal(1506, matched.SourceId); Equal(table, matched.RosterTable);
            });

            foreach (KeyValuePair<string, string> original in originals) Equal(original.Value, Hash(original.Key));
            Console.WriteLine("Original uploaded roster, mapping, SDB and real model SHA-256 hashes unchanged.");
            Console.WriteLine(passed + " roster growth checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (KeyValuePair<string, string> original in originals)
                if (Hash(original.Key) != original.Value) Console.Error.WriteLine("ORIGINAL DATA CHANGED: " + original.Key);
            Directory.Delete(temporary, true);
        }
    }

    // A new root/vector points into the relocated original blob. Every original
    // object's relative references remain unchanged; only the new row is synthetic.
    private static byte[] ExtendProfiles(byte[] original, ushort id, ushort baseId, uint nameKey)
    {
        var file = new FlatBufferFile(original, "Char");
        int[] rows = file.Tables(file.Root, 0).ToArray();
        int prefix = Align(28 + (rows.Length + 1) * 4);
        int wrapperVtable = Align(prefix + original.Length), wrapper = wrapperVtable + 8;
        int profileVtable = wrapper + 8, profile = profileVtable + 36;
        byte[] bytes = new byte[profile + 16];
        Write32(bytes, 0, 16); Encoding.ASCII.GetBytes("Char").CopyTo(bytes, 4);
        Write16(bytes, 8, 6); Write16(bytes, 10, 8); Write16(bytes, 12, 4);
        Write32(bytes, 16, 8); Write32(bytes, 20, 4); Write32(bytes, 24, (uint)(rows.Length + 1));
        for (int index = 0; index < rows.Length; index++)
        {
            int element = 28 + index * 4;
            Write32(bytes, element, (uint)(prefix + rows[index] - element));
        }
        int newElement = 28 + rows.Length * 4;
        Write32(bytes, newElement, (uint)(wrapper - newElement));
        Array.Copy(original, 0, bytes, prefix, original.Length);
        Write16(bytes, wrapperVtable, 8); Write16(bytes, wrapperVtable + 2, 8);
        Write16(bytes, wrapperVtable + 6, 4);
        Write32(bytes, wrapper, 8); Write32(bytes, wrapper + 4, (uint)(profile - wrapper - 4));
        Write16(bytes, profileVtable, 36); Write16(bytes, profileVtable + 2, 16);
        Write16(bytes, profileVtable + 4 + 2 * 2, 4); Write16(bytes, profileVtable + 4 + 2 * 3, 6);
        Write16(bytes, profileVtable + 4 + 2 * 7, 8); Write16(bytes, profileVtable + 4 + 2 * 15, 12);
        Write32(bytes, profile, 36); Write16(bytes, profile + 4, id); Write16(bytes, profile + 6, baseId);
        Write32(bytes, profile + 8, nameKey); bytes[profile + 12] = 1;
        return bytes;
    }

    // Rebuild the real index/text layout so updates cover the parser's actual
    // offset, UTF-8 length and termination rules rather than replacing the reader.
    private static void WriteString(string path, uint key, string value)
    {
        byte[] header = File.ReadAllBytes(path).Take(4).ToArray();
        Dictionary<uint, string> strings = DiscoveryProfiles.ReadStrings(path);
        strings[key] = value;
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output, new UTF8Encoding(false), true))
        {
            writer.Write(header); writer.Write((uint)strings.Count);
            uint offset = checked(8U + (uint)strings.Count * 12U);
            var texts = strings.Select(pair => new { pair.Key, Bytes = Encoding.UTF8.GetBytes(pair.Value) }).ToList();
            foreach (var text in texts)
            {
                writer.Write(offset); writer.Write((uint)text.Bytes.Length); writer.Write(text.Key);
                offset = checked(offset + (uint)text.Bytes.Length + 1);
            }
            foreach (var text in texts) { writer.Write(text.Bytes); writer.Write((byte)0); }
            File.WriteAllBytes(path, output.ToArray());
        }
    }

    private static uint UnusedKey(Dictionary<uint, string> strings)
    {
        uint key = 0xFDFDFDFD;
        while (strings.ContainsKey(key)) key--;
        return key;
    }
    private static int Align(int size) { return (size + 15) & ~15; }
    private static void Write16(byte[] bytes, int position, ushort value) { bytes[position] = (byte)value; bytes[position + 1] = (byte)(value >> 8); }
    private static void Write32(byte[] bytes, int position, uint value) { for (int shift = 0; shift < 4; shift++) bytes[position + shift] = (byte)(value >> (shift * 8)); }
    private static string Copy(string source, string root, params string[] parts)
    {
        string destination = parts.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true); return destination;
    }
    private static string Hash(string path) { using (var input = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name, error); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
}
