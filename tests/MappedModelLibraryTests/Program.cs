using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary;
    private static int passed;

    private static int Main(string[] args)
    {
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-mapped-models-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Check("Missing roster context does not guess a model", () => {
                var candidate = Candidate("no-context", "312_CM_Punk", "312_Default_Attire", "312_Attire.mcd");
                Equal(0, Find(Entry(1481), candidate, null).Count);
            });
            Check("Missing CharacterMapping is a safe empty result", () => {
                var candidate = Candidate("no-map", "312_CM_Punk", "312_Default_Attire", "312_Attire.mcd");
                string table = Table("no-map-data"); Equal(0, Find(Entry(1481), candidate, table).Count);
            });
            Check("Null collections are harmless", () => {
                string table = Data("nulls", Pair(1481, "312_CM_Punk/312_Default_Attire/312_Attire.mcd"));
                Equal(0, MappedModelLibrary.FindMatches(null, null, table).Count);
                Equal(0, MappedModelLibrary.FindMatches(new[] { Entry(1481) }, null, table).Count);
            });
            Check("A mapped logical ID wins without display-name or source-ID agreement", () => {
                var candidate = Candidate("logical", "312_CM_Punk", "312_Default_Attire", "312_Attire.mcd");
                candidate.Name = "An unrelated saved display name"; candidate.SourceId = 777;
                string table = Data("logical-data", Pair(1481, "312_CM_Punk/312_Default_Attire/312_Attire.mcd"));
                var entry = Entry(1481); entry.Name = "Custom localized superstar"; entry.SourceId = 1481;
                Equal(candidate.Folder, Find(entry, candidate, table)[entry].Single().Folder);
                Equal(777, Find(entry, candidate, table)[entry].Single().SourceId);
            });
            Check("One physical model may explicitly serve multiple logical slots", () => {
                var candidate = Candidate("shared", "123_Rey", "Default_Attire", "Attire.mcd");
                string table = Data("shared-data", Pair(910, "123_Rey/Default_Attire/Attire.mcd"), Pair(4000, "123_Rey/Default_Attire/Attire.mcd"));
                var first = Entry(910); var second = Entry(4000);
                var result = MappedModelLibrary.FindMatches(new[] { first, second }, new[] { candidate }, table);
                Equal(2, result.Count); Equal(candidate.Folder, result[first].Single().Folder); Equal(candidate.Folder, result[second].Single().Folder);
            });
            Check("Several MCDs for the same slot do not duplicate a character choice", () => {
                var candidate = Candidate("many-models", "PAC", "Default_Attire", "Attire.mcd");
                File.WriteAllText(Path.Combine(candidate.Folder, "Default_Attire", "Alt.mcd"), "fixture");
                string table = Data("many-models-data", Pair(4000, "PAC/Default_Attire/Attire.mcd"), Pair(4000, "PAC/Default_Attire/Alt.mcd"));
                var entry = Entry(4000); Equal(1, Find(entry, candidate, table)[entry].Count);
            });
            Check("Repeated candidate paths and trailing separators do not create ambiguity", () => {
                var candidate = Candidate("duplicate-path", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("duplicate-path-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var entry = Entry(4000);
                var duplicate = new ModelCandidate { Folder = candidate.Folder + Path.DirectorySeparatorChar, Name = "duplicate" };
                Equal(1, MappedModelLibrary.FindMatches(new[] { entry }, new[] { candidate, candidate, duplicate }, table)[entry].Count);
            });
            Check("Repeated row references do not fail or duplicate results", () => {
                var candidate = Candidate("duplicate-row", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("duplicate-row-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var entry = Entry(4000);
                Equal(1, MappedModelLibrary.FindMatches(new[] { entry, entry }, new[] { candidate }, table).Count);
            });
            Check("Distinct roster rows sharing a slot retain their identities", () => {
                var candidate = Candidate("same-slot", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("same-slot-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var first = Entry(4000); var second = Entry(4000);
                var result = MappedModelLibrary.FindMatches(new[] { first, second }, new[] { candidate }, table);
                Equal(2, result.Count); Equal(true, result.ContainsKey(first)); Equal(true, result.ContainsKey(second));
            });
            Check("A matching display name cannot override a different path hash", () => {
                var candidate = Candidate("name-not-proof", "PAC", "Default_Attire", "Attire.mcd"); candidate.Name = "PAC";
                string table = Data("name-not-proof-data", Pair(4000, "Different/Default_Attire/Attire.mcd")); var entry = Entry(4000); entry.Name = "PAC";
                Equal(0, Find(entry, candidate, table).Count);
            });
            Check("A matching physical number cannot override a different path hash", () => {
                var candidate = Candidate("number-not-proof", "4000_Wrong", "Default_Attire", "Attire.mcd"); candidate.SourceId = 4000;
                string table = Data("number-not-proof-data", Pair(4000, "215_Right/Default_Attire/Attire.mcd"));
                Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("A path mapped to an unrequested ID cannot leak to another slot", () => {
                var candidate = Candidate("wrong-id", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("wrong-id-data", Pair(4001, "PAC/Default_Attire/Attire.mcd")); Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("A row from a different roster context is excluded", () => {
                var candidate = Candidate("foreign-roster", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("foreign-roster-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var entry = Entry(4000); entry.RosterTable = Table("other-roster");
                Equal(0, Find(entry, candidate, table).Count);
            });
            Check("A matching roster path and an unspecified row context are supported", () => {
                var candidate = Candidate("same-roster", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("same-roster-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var entry = Entry(4000); entry.RosterTable = table;
                Equal(1, Find(entry, candidate, table).Count);
            });
            Check("Missing folders are ignored", () => {
                string table = Data("missing-folder-data", Pair(4000, "PAC/Default_Attire/Attire.mcd"));
                Equal(0, Find(Entry(4000), new ModelCandidate { Folder = Path.Combine(temporary, "missing") }, table).Count);
            });
            Check("Profile and texture files alone never become a model", () => {
                var candidate = Candidate("non-model", "PAC", "Default_Attire", "Attire.json");
                string table = Data("non-model-data", Pair(4000, "PAC/Default_Attire/Attire.json")); Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("A selected nested attire keeps its character path when moved", () => {
                var character = Candidate("attire", "Mixed Mod", "368_Default_Attire", "368_Attire.mcd");
                var attire = new ModelCandidate { Folder = Path.Combine(character.Folder, "368_Default_Attire"), Name = "Wrong human name", SourceId = 368 };
                string table = Data("attire-data", Pair(4000, "Mixed Mod/368_Default_Attire/368_Attire.mcd")); var entry = Entry(4000);
                Equal(attire.Folder, Find(entry, attire, table)[entry].Single().Folder);
            });
            Check("Four retained wrapper parents may provide an exact virtual path", () => {
                var candidate = Candidate("wrapper", "Original Character", "Wrapper1", "Wrapper2", "Wrapper3", "Default_Attire", "Attire.mcd");
                candidate.Folder = Path.Combine(candidate.Folder, "Wrapper1", "Wrapper2", "Wrapper3", "Default_Attire");
                string table = Data("wrapper-data", Pair(4000, "Original Character/Wrapper1/Wrapper2/Wrapper3/Default_Attire/Attire.mcd")); var entry = Entry(4000);
                Equal(1, Find(entry, candidate, table)[entry].Count);
            });
            Check("An actual Characters boundary supports deeply nested packaged paths", () => {
                var candidate = Candidate("chars-boundary", "Characters", "Character", "a", "b", "c", "d", "e", "Default_Attire", "Attire.mcd");
                candidate.Folder = Path.Combine(candidate.Folder, "Character", "a", "b", "c", "d", "e", "Default_Attire");
                string table = Data("chars-boundary-data", Pair(4000, "Character/a/b/c/d/e/Default_Attire/Attire.mcd"));
                Equal(1, Find(Entry(4000), candidate, table).Count);
            });
            Check("Arbitrary ancestor suffix guessing is bounded", () => {
                var candidate = Candidate("bounded-wrapper", "Original", "a", "b", "c", "d", "e", "Default_Attire", "Attire.mcd");
                candidate.Folder = Path.Combine(candidate.Folder, "a", "b", "c", "d", "e", "Default_Attire");
                string table = Data("bounded-wrapper-data", Pair(4000, "Original/a/b/c/d/e/Default_Attire/Attire.mcd")); Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("Hash normalization tolerates case and uppercase MCD extensions", () => {
                var candidate = Candidate("case", "Pac", "Default_Attire", "ATTIRE.MCD");
                string table = Data("case-data", Pair(4000, "PAC/DEFAULT_ATTIRE/attire.mcd")); Equal(1, Find(Entry(4000), candidate, table).Count);
            });
            Check("Duplicate packs remain explicit choices", () => {
                var first = Candidate("pack-a", "PAC", "Default_Attire", "Attire.mcd"); var second = Candidate("pack-b", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("packs-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var entry = Entry(4000);
                Equal(2, MappedModelLibrary.FindMatches(new[] { entry }, new[] { first, second }, table)[entry].Count);
            });
            Check("A parent containing conflicting physical IDs is never assigned wholesale", () => {
                var candidate = Candidate("conflicting-parent", "Mixed Mod", "368_Default_Attire", "Attire.mcd");
                string second = Directory.CreateDirectory(Path.Combine(candidate.Folder, "101_Default_Attire")).FullName; File.WriteAllText(Path.Combine(second, "Attire.mcd"), "fixture");
                string table = Data("conflicting-parent-data", Pair(4000, "Mixed Mod/368_Default_Attire/Attire.mcd"), Pair(4001, "Mixed Mod/101_Default_Attire/Attire.mcd"));
                Equal(0, Find(Entry(4000), candidate, table).Count);
                var children = ModelLibrary.Scan(candidate.Folder); Equal(2, children.Count);
                var result = MappedModelLibrary.FindMatches(new[] { Entry(4000), Entry(4001) }, children, table); Equal(2, result.Count);
                Equal(true, result.Values.SelectMany(items => items).All(item => item.Folder != candidate.Folder));
            });
            Check("A Characters collection is not mistaken for one wrestler", () => {
                var candidate = Candidate("collection", "Characters", "PAC", "Default_Attire", "Attire.mcd");
                string table = Data("collection-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("A nested directory link cannot inject another model or recurse", () => {
                var candidate = Candidate("links", "PAC", "Default_Attire", "Attire.mcd");
                Directory.CreateSymbolicLink(Path.Combine(candidate.Folder, "Loop"), candidate.Folder);
                string table = Data("links-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); Equal(1, Find(Entry(4000), candidate, table).Count);
            });
            Check("A linked candidate root is skipped", () => {
                var original = Candidate("link-root-original", "PAC", "Default_Attire", "Attire.mcd");
                string parent = Directory.CreateDirectory(Path.Combine(temporary, "linked-root")).FullName;
                string linked = Path.Combine(parent, "PAC"); Directory.CreateSymbolicLink(linked, original.Folder);
                string table = Data("link-root-data", Pair(4000, "PAC/Default_Attire/Attire.mcd"));
                Equal(0, Find(Entry(4000), new ModelCandidate { Folder = linked }, table).Count);
            });
            Check("A linked MCD file is skipped", () => {
                var candidate = Candidate("link-file", "PAC", "Default_Attire", "Original.bin");
                File.CreateSymbolicLink(Path.Combine(candidate.Folder, "Default_Attire", "Attire.mcd"), Path.Combine(candidate.Folder, "Default_Attire", "Original.bin"));
                string table = Data("link-file-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("Cancellation interrupts matching before it starts", () => {
                using (var cancel = new CancellationTokenSource()) {
                    cancel.Cancel(); Throws<OperationCanceledException>(() => MappedModelLibrary.FindMatches(null, null, null, cancel.Token));
                }
            });
            Check("A damaged mapping reports an actionable error", () => {
                string table = Table("damaged"); Directory.CreateDirectory(Path.Combine(DiscoveryProfiles.DataRoot(table), "Characters"));
                File.WriteAllText(Path.Combine(DiscoveryProfiles.DataRoot(table), "Characters", "CharacterMapping.jsfb"), "damaged");
                var error = Throws<InvalidDataException>(() => Find(Entry(4000), null, table)); Equal(true, error.Message.Contains("CharacterMapping.jsfb"));
            });
            Check("Empty mapped rows do not fabricate models", () => {
                var candidate = Candidate("empty-map", "PAC", "Default_Attire", "Attire.mcd"); string table = Data("empty-map-data");
                Equal(0, Find(Entry(4000), candidate, table).Count);
            });
            Check("Evidence does not mutate valid saved associations", () => {
                var candidate = Candidate("saved", "PAC", "Default_Attire", "Attire.mcd"); string table = Data("saved-data", Pair(4000, "PAC/Default_Attire/Attire.mcd"));
                var entry = Entry(4000); entry.Folder = "saved-path"; entry.Source = "Chosen model"; entry.SourceId = 123;
                Equal(1, Find(entry, candidate, table).Count); Equal("saved-path", entry.Folder); Equal("Chosen model", entry.Source); Equal(123, entry.SourceId);
            });
            Check("Null candidates and invalid row IDs are ignored", () => {
                var candidate = Candidate("invalid", "PAC", "Default_Attire", "Attire.mcd"); string table = Data("invalid-data", Pair(4000, "PAC/Default_Attire/Attire.mcd"));
                Equal(0, MappedModelLibrary.FindMatches(new[] { (RosterEntry)null, Entry(0), Entry(-1), Entry(65536) }, new[] { candidate }, table).Count);
                Equal(0, MappedModelLibrary.FindMatches(new[] { Entry(4000) }, new[] { (ModelCandidate)null, new ModelCandidate { Folder = "" } }, table).Count);
            });
            Check("Characters and mapping names are discovered without case sensitivity", () => {
                string table = Table("mapping-case"); string characters = Directory.CreateDirectory(Path.Combine(DiscoveryProfiles.DataRoot(table), "CHARACTERS")).FullName;
                Mapping(Path.Combine(characters, "CHARACTERMAPPING.JSFB"), new[] { Pair(4000, "PAC/Default_Attire/Attire.mcd") });
                var candidate = Candidate("mapping-case-model", "PAC", "Default_Attire", "Attire.mcd"); Equal(1, Find(Entry(4000), candidate, table).Count);
            });
            Check("Excessive model nesting produces a clear bounded-scan error", () => {
                string table = Data("depth-data", Pair(4000, "PAC/Default_Attire/Attire.mcd"));
                var candidate = Candidate("depth", "PAC", "Default_Attire", "Attire.mcd"); string deep = candidate.Folder;
                for (int index = 0; index < 66; index++) deep = Directory.CreateDirectory(Path.Combine(deep, "nested")).FullName;
                Throws<InvalidDataException>(() => Find(Entry(4000), candidate, table));
            });
            Check("A newly added character is picked up by a fresh mapping read", () => {
                var oldModel = Candidate("roster-growth", "PAC", "Default_Attire", "Attire.mcd");
                var newModel = Candidate("roster-growth", "New Character", "Default_Attire", "Attire.mcd");
                string table = Data("roster-growth-data", Pair(4000, "PAC/Default_Attire/Attire.mcd")); var oldEntry = Entry(4000); var newEntry = Entry(65527);
                Equal(0, Find(newEntry, newModel, table).Count);
                Mapping(Path.Combine(DiscoveryProfiles.DataRoot(table), "Characters", "CharacterMapping.jsfb"), new[] { Pair(4000, "PAC/Default_Attire/Attire.mcd"), Pair(65527, "New Character/Default_Attire/Attire.mcd") });
                var result = MappedModelLibrary.FindMatches(new[] { oldEntry, newEntry }, new[] { oldModel, newModel }, table);
                Equal(2, result.Count); Equal(newModel.Folder, result[newEntry].Single().Folder); Equal(oldModel.Folder, result[oldEntry].Single().Folder);
            });
            if (args.Length > 0) Actual(args[0]);
            Console.WriteLine(passed + " mapped model library checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static void Actual(string root)
    {
        string table = Path.Combine(root, "Roster", "CharProfileTable.jsfb");
        List<RosterEntry> entries = DiscoveryProfiles.Read(table).Select(profile => new RosterEntry { Id = profile.Id, Name = "Intentionally unmatched display name", SourceId = 0 }).ToList();
        List<ModelCandidate> candidates = ModelLibrary.Scan(root);
        Dictionary<RosterEntry, List<ModelCandidate>> matches = MappedModelLibrary.FindMatches(entries, candidates, table);
        Check("The actual uploaded mapping proves eight of ten extracted models without matching names", () => {
            Equal(10, candidates.Count); Equal(8, matches.Values.SelectMany(items => items).Select(item => item.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Check("The user's two unreferenced MCD paths are not guessed from their folder numbers", () => {
            var folders = new HashSet<string>(matches.Values.SelectMany(items => items).Select(item => Path.GetFileName(item.Folder)), StringComparer.OrdinalIgnoreCase);
            Equal(false, folders.Contains("109_Hollywood_Hogan_2002")); Equal(false, folders.Contains("343_Brock_Lesnar"));
        });
        Check("The user's CM Punk physical 312 resolves logical 1481", () => {
            Equal(312, matches[entries.Single(entry => entry.Id == 1481)].Single().SourceId);
        });
        Check("The user's Rey physical 123 resolves logical 910", () => {
            Equal(123, matches[entries.Single(entry => entry.Id == 910)].Single().SourceId);
        });
        Check("Moved model-path suffixes remain valid against the uploaded Discovery mapping", () => {
            string moved = Directory.CreateDirectory(Path.Combine(temporary, "moved-user-models")).FullName;
            foreach (ModelCandidate candidate in candidates)
            {
                string target = Path.Combine(moved, Path.GetFileName(candidate.Folder));
                foreach (string model in Directory.GetFiles(candidate.Folder, "*.mcd", SearchOption.AllDirectories))
                {
                    string suffix = model.Substring(candidate.Folder.Length + 1); string file = Path.Combine(target, suffix);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, "MCD path fixture; source data remains unchanged");
                }
            }
            List<ModelCandidate> relocated = ModelLibrary.Scan(moved); foreach (ModelCandidate candidate in relocated) { candidate.Name = "No display-name match"; candidate.SourceId = 0; }
            var relocatedMatches = MappedModelLibrary.FindMatches(entries, relocated, table);
            Equal(8, relocatedMatches.Values.SelectMany(items => items).Select(item => item.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Equal("312_CM_Punk", Path.GetFileName(relocatedMatches[entries.Single(entry => entry.Id == 1481)].Single().Folder));
        });
    }

    private static Dictionary<RosterEntry, List<ModelCandidate>> Find(RosterEntry entry, ModelCandidate candidate, string table)
    { return MappedModelLibrary.FindMatches(new[] { entry }, new[] { candidate }, table); }
    private static RosterEntry Entry(int id) { return new RosterEntry { Id = id, Name = "Unrelated roster name", Source = "Profile" }; }
    private static string Table(string name)
    {
        string roster = Directory.CreateDirectory(Path.Combine(temporary, name, "Roster")).FullName;
        string table = Path.Combine(roster, "CharProfileTable.jsfb"); File.WriteAllText(table, "Only table location is used by the mapping matcher"); return table;
    }
    private static string Data(string name, params KeyValuePair<int, ulong>[] rows)
    {
        string table = Table(name); string characters = Directory.CreateDirectory(Path.Combine(DiscoveryProfiles.DataRoot(table), "Characters")).FullName;
        Mapping(Path.Combine(characters, "CharacterMapping.jsfb"), rows); return table;
    }
    private static ModelCandidate Candidate(string pack, params string[] path)
    {
        string folder = Path.Combine(temporary, pack); foreach (string part in path.Take(path.Length - 1)) folder = Path.Combine(folder, part);
        Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, path[path.Length - 1]), "model fixture");
        return new ModelCandidate { Folder = Path.Combine(temporary, pack, path[0]), Name = path[0] };
    }
    private static KeyValuePair<int, ulong> Pair(int id, string path)
    { return new KeyValuePair<int, ulong>(id, CharacterMappingReader.ModelPathHash("characters/" + path)); }
    private static void Mapping(string path, IEnumerable<KeyValuePair<int, ulong>> input)
    {
        var rows = input.ToList();
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write((uint)16); output.Write(Encoding.ASCII.GetBytes("Char")); output.Write((ushort)6); output.Write((ushort)8); output.Write((ushort)4); output.Write((ushort)0);
            output.Write(8); output.Write((uint)4); output.Write((uint)rows.Count);
            long vector = output.BaseStream.Position; foreach (var row in rows) output.Write((uint)0);
            for (int index = 0; index < rows.Count; index++)
            {
                long vtable = output.BaseStream.Position; output.Write((ushort)20); output.Write((ushort)16);
                for (int field = 0; field < 8; field++) output.Write((ushort)(field == 0 ? 4 : field == 7 ? 8 : 0));
                long offset = output.BaseStream.Position; output.Write((int)(offset - vtable)); output.Write((ushort)rows[index].Key); output.Write((ushort)0); output.Write(rows[index].Value);
                long end = output.BaseStream.Position; output.BaseStream.Position = vector + 4 * index; output.Write((uint)(offset - output.BaseStream.Position)); output.BaseStream.Position = end;
            }
        }
    }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name, error); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
    private static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
}
