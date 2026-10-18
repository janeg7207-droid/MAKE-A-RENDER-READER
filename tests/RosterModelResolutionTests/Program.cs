using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;

    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-roster-resolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check("Unique plain-name wrestler loads without choosing a folder", () => {
                string folder = Model("plain", "Jon Moxley"); var entry = Profile(4000, "JON MOXLEY", 194);
                var result = Resolve(entry, "plain"); Equal(RosterModelState.Ready, result.State); Equal(folder, result.Folder);
                Equal(false, result.ExistingAssociation); Equal(4000, entry.Id); Equal((string)null, entry.Folder);
            });
            Check("Existing chosen model is authoritative over other matching packs", () => {
                string saved = Model("saved", "900_Chosen Body"); Model("saved-other", "Jon Moxley");
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = saved; entry.Source = "Chosen model folder";
                var result = Resolve(entry, "saved-other"); Equal(RosterModelState.Ready, result.State); Equal(saved, result.Folder);
                Equal(900, result.SourceId); Equal(true, result.ExistingAssociation); Equal(194, entry.SourceId);
            });
            Check("Deleted saved folder can repair by unique wrestler name", () => {
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = Path.Combine(root, "removed"); entry.Source = "Chosen model folder";
                string current = Model("moved", "Jon_Moxley"); var result = Resolve(entry, "moved");
                Equal(RosterModelState.Ready, result.State); Equal(current, result.Folder);
            });
            Check("An existing empty metadata folder cannot count as a model", () => {
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = Directory.CreateDirectory(Path.Combine(root, "metadata-only")).FullName;
                File.WriteAllText(Path.Combine(entry.Folder, "profile.json"), "{}");
                Equal(RosterModelState.Missing, Resolve(entry).State);
            });
            Check("An empty saved folder does not hide a real exact-name model", () => {
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = Directory.CreateDirectory(Path.Combine(root, "empty-saved")).FullName;
                string current = Model("empty-repair", "Jon Moxley"); Equal(current, Resolve(entry, "empty-repair").Folder);
            });
            Check("Logical slot and inherited profile body cannot auto-select another wrestler", () => {
                Model("wrong-body", "194_Doctor Trainer"); Model("wrong-body", "4000_Other Wrestler");
                var result = Resolve(Profile(4000, "Jon Moxley", 194), "wrong-body"); Equal(RosterModelState.Missing, result.State);
            });
            Check("Verified stock mapping can locate an extracted body by physical ID", () => {
                string body = Model("verified-physical", "194_Doctor Trainer"); var entry = Profile(4000, "Jon Moxley", 204);
                entry.StockFolder = "194_Doctor_Trainer_2017"; var result = Resolve(entry, "verified-physical");
                Equal(RosterModelState.Ready, result.State); Equal(body, result.Folder); Equal(194, result.SourceId); Equal(4000, entry.Id);
            });
            Check("Verified stock mapping uses installed-game extractor when no local model exists", () => {
                var entry = Profile(4000, "Jon Moxley", 204); entry.StockFolder = "194_Doctor_Trainer_2017";
                Equal(RosterModelState.InstalledGame, Resolve(entry).State);
            });
            Check("Missing model is explicit and does not fall back to arbitrary inventory", () => {
                Model("unrelated", "Adam Cole"); Model("unrelated", "PAC");
                var result = Resolve(Profile(4000, "Jon Moxley", 194), "unrelated");
                Equal(RosterModelState.Missing, result.State); Equal(0, result.Choices.Count); Equal((string)null, result.Folder);
            });
            Check("Ambiguous same-name packs offer only that wrestler's models", () => {
                string first = Model("same-name", "First/Jon Moxley"); string second = Model("same-name", "Second/Jon Moxley");
                Model("same-name", "Adam Cole"); Model("same-name", "PAC");
                var result = Resolve(Profile(4000, "Jon Moxley", 194), "same-name");
                Equal(RosterModelState.ChooseMatchingModel, result.State); Equal(2, result.Choices.Count);
                Equal(true, result.Choices.Any(model => model.Folder == first)); Equal(true, result.Choices.Any(model => model.Folder == second));
            });
            Check("Remembering an ambiguous pack avoids a repeat choice", () => {
                string first = Model("remembered-choice", "First/Jon Moxley"); Model("remembered-choice", "Second/Jon Moxley");
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = first; entry.Source = "Chosen model folder";
                var result = Resolve(entry, "remembered-choice"); Equal(RosterModelState.Ready, result.State); Equal(first, result.Folder);
            });
            Check("Exact custom name takes priority over a verified stock fallback", () => {
                string custom = Model("name-over-stock", "368_PAC"); Model("name-over-stock", "215_Neville");
                var entry = Profile(4000, "PAC", 194); entry.StockFolder = "215_Neville";
                var result = Resolve(entry, "name-over-stock"); Equal(custom, result.Folder); Equal(368, result.SourceId);
            });
            Check("Moved manually chosen name does not reuse its inherited physical ID", () => {
                Model("stale-chosen", "194_Doctor Trainer"); var entry = Profile(4000, "Jon Moxley", 194);
                entry.Folder = Path.Combine(root, "unavailable-chosen"); entry.Source = "Chosen model folder";
                Equal(RosterModelState.Missing, Resolve(entry, "stale-chosen").State);
            });
            Check("Another roster context is not automatically matched by name", () => {
                Model("other-context", "Jon Moxley"); var entry = Profile(4000, "Jon Moxley", 194); entry.RosterTable = Table("Other");
                var result = RosterModelResolution.Resolve(entry, ModelLibrary.Scan(Path.Combine(root, "other-context")), Table("Current"));
                Equal(RosterModelState.Missing, result.State);
            });
            Check("Multiple physical bodies inside an unqualified saved parent cannot load arbitrarily", () => {
                string parent = Path.Combine(root, "mixed", "Jon Moxley");
                Model("mixed", "Jon Moxley/194_Default_Attire"); Model("mixed", "Jon Moxley/215_Default_Attire");
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = parent;
                Equal(RosterModelState.Missing, Resolve(entry, "mixed").State);
            });
            Check("A deleted candidate in an otherwise ambiguous match is ignored", () => {
                string real = Model("one-live", "Jon Moxley"); var inventory = ModelLibrary.Scan(Path.Combine(root, "one-live"));
                inventory.Add(new ModelCandidate { Name = "Jon Moxley", Folder = Path.Combine(root, "missing-copy") });
                var result = RosterModelResolution.Resolve(Profile(4000, "Jon Moxley", 194), inventory, null);
                Equal(RosterModelState.Ready, result.State); Equal(real, result.Folder);
            });
            Check("Mapping selects a renamed wrestler with a different logical and physical ID", () => {
                string body = Model("mapped-renamed", "123_Stock Body"); string table = Mapping("mapped-table", 4000, "characters/123_Stock Body/Default_Attire/attire.mcd");
                var entry = Profile(4000, "Jon Moxley", 194); entry.RosterTable = table;
                var result = RosterModelResolution.Resolve(entry, ModelLibrary.Scan(Path.Combine(root, "mapped-renamed")), table);
                Equal(RosterModelState.Ready, result.State); Equal(body, result.Folder); Equal(123, result.SourceId); Equal(4000, entry.Id);
            });
            Check("Ambiguous mapping stays ambiguous despite an exact-name fallback", () => {
                string first = Model("mapped-first", "123_Stock Body"); string second = Model("mapped-second", "123_Stock Body");
                Model("mapped-named", "Jon Moxley"); string table = Mapping("mapped-ambiguity", 4000, "characters/123_Stock Body/Default_Attire/attire.mcd");
                var inventory = new[] { "mapped-first", "mapped-second", "mapped-named" }.SelectMany(name => ModelLibrary.Scan(Path.Combine(root, name))).ToList();
                var result = RosterModelResolution.Resolve(Profile(4000, "Jon Moxley", 194), inventory, table);
                Equal(RosterModelState.ChooseMatchingModel, result.State); Equal(2, result.Choices.Count);
                Equal(true, result.Choices.All(item => item.Folder == first || item.Folder == second));
            });
            Check("Saved explicit model remains authoritative when mapping points elsewhere", () => {
                string saved = Model("mapped-saved", "Jon Moxley"); Model("mapped-replacement", "123_Stock Body");
                string table = Mapping("mapped-explicit", 4000, "characters/123_Stock Body/Default_Attire/attire.mcd");
                var entry = Profile(4000, "Jon Moxley", 194); entry.Folder = saved; entry.Source = "Chosen model folder";
                var result = RosterModelResolution.Resolve(entry, ModelLibrary.Scan(Path.Combine(root, "mapped-replacement")), table);
                Equal(saved, result.Folder); Equal(true, result.ExistingAssociation);
            });
            Check("Corrupt character mapping cannot silently promote an inherited stock body", () => {
                string table = Mapping("corrupt", 4000, "characters/123_Stock Body/Default_Attire/attire.mcd");
                File.WriteAllText(Path.Combine(root, "corrupt", "Characters", "CharacterMapping.jsfb"), "corrupt");
                var entry = Profile(4000, "Jon Moxley", 194); entry.StockFolder = "194_Doctor_Trainer_2017";
                bool rejected = false;
                try { RosterModelResolution.Resolve(entry, new List<ModelCandidate>(), table); }
                catch (InvalidDataException) { rejected = true; }
                Equal(true, rejected);
            });
            Check("Unassigned extracted models require a roster ID instead of becoming a wrestler automatically", () => {
                bool rejected = false;
                try { RosterModelResolution.Resolve(new RosterEntry { Id = 0, Name = "Unknown" }, new List<ModelCandidate>(), null); }
                catch (ArgumentException) { rejected = true; }
                Equal(true, rejected);
            });
            Console.WriteLine(passed + " roster-first model resolution checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static RosterEntry Profile(int id, string name, int sourceId)
    { return new RosterEntry { Id = id, Name = name, SourceId = sourceId, Source = "Profile" }; }

    private static RosterModelResolution Resolve(RosterEntry entry, string collection = null)
    { return RosterModelResolution.Resolve(entry, collection == null ? new List<ModelCandidate>() : ModelLibrary.Scan(Path.Combine(root, collection)), null); }

    private static string Model(string collection, string name)
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, collection, name.Replace('/', Path.DirectorySeparatorChar))).FullName;
        string attire = Directory.CreateDirectory(Path.Combine(folder, "Default_Attire")).FullName;
        File.WriteAllBytes(Path.Combine(attire, "attire.mcd"), new byte[] { 1, 2, 3 }); return folder;
    }

    private static string Table(string name) { return Path.Combine(root, name, "Roster", "CharProfileTable.jsfb"); }

    private static string Mapping(string name, int id, string modelPath)
    {
        string data = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        string roster = Directory.CreateDirectory(Path.Combine(data, "Roster")).FullName;
        string characters = Directory.CreateDirectory(Path.Combine(data, "Characters")).FullName;
        string table = Path.Combine(roster, "CharProfileTable.jsfb"); File.WriteAllText(table, "fixture");
        using (var stream = File.Create(Path.Combine(characters, "CharacterMapping.jsfb")))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, false))
        {
            // Same minimal Char FlatBuffer layout as the independent scanner fixtures.
            writer.Write(16U); writer.Write(Encoding.ASCII.GetBytes("Char"));
            writer.Write((ushort)6); writer.Write((ushort)8); writer.Write((ushort)4); writer.Write((ushort)0);
            writer.Write(8); writer.Write(4U); writer.Write(1U);
            long vector = writer.BaseStream.Position; writer.Write(0U);
            long vtable = writer.BaseStream.Position; writer.Write((ushort)20); writer.Write((ushort)16);
            for (int field = 0; field < 8; field++) writer.Write((ushort)(field == 0 ? 4 : field == 7 ? 8 : 0));
            long row = writer.BaseStream.Position; writer.Write((int)(row - vtable)); writer.Write((ushort)id); writer.Write((ushort)0);
            writer.Write(CharacterMappingReader.ModelPathHash(modelPath));
            writer.BaseStream.Position = vector; writer.Write((uint)(row - vector));
        }
        return table;
    }

    private static void Check(string name, Action action)
    { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name, error); } }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
}
