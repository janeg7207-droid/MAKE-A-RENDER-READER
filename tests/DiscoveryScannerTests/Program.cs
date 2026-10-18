using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary;
    private static int passed;
    private static int Main(string[] args)
    {
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-discovery-scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Check("FNV matches the actual Discovery model-path hash", () => {
                Equal(0x3B240B1F6FF2E945UL, CharacterMappingReader.ModelPathHash(@"Characters\1506_Flammer\Default_Attire\Attire.mcd"));
                Equal(CharacterMappingReader.ModelPathHash("Characters/A/A.mcd"), CharacterMappingReader.ModelPathHash("/characters/a/a.MCD"));
            });
            Check("CharacterMapping retains all logical IDs and deduplicates variants", () => {
                string path = Path.Combine(temporary, "mapping.jsfb");
                Mapping(path, new[] { Pair(100, 42UL), Pair(200, 42UL), Pair(100, 42UL) });
                var ids = CharacterMappingReader.Read(path)[42UL]; Equal(2, ids.Count); Equal(true, ids.Contains(100)); Equal(true, ids.Contains(200));
            });
            Check("Placeholder names use their explicit slot", () => {
                string root = Data("placeholder"); Model(root, "placeholder_368_A0", "Default_Attire", "Attire.mcd");
                var entry = DiscoveryScanner.Scan(root, Profiles(368, "Roman Reigns")).Entries.Single();
                Equal(368, entry.Id); Equal(368, entry.SourceId); Equal("Roman Reigns", entry.Name); Equal(true, entry.IsMod); Equal("Folder ID", entry.Source);
            });
            Check("Plain mod names use numbered child IDs and remove attire suffix", () => {
                string root = Data("plain-child"); Model(root, "Roman_Reigns_A1", "368_Default_Attire", "368_Attire.mcd");
                var entry = DiscoveryScanner.Scan(root, null).Entries.Single();
                Equal(368, entry.Id); Equal(368, entry.SourceId); Equal("Roman Reigns", entry.Name);
            });
            Check("Numeric stage names do not become roster IDs", () => {
                string root = Data("numeric-name"); Model(root, "2 Cold Scorpio", "Default_Attire", "Attire.mcd");
                var entry = DiscoveryScanner.Scan(root, null).Entries.Single();
                Equal(0, entry.Id); Equal(0, entry.SourceId); Equal("2 Cold Scorpio", entry.Name); Equal("Choose roster slot", entry.Source);
            });
            Check("Unresolved plain mods remain visible alongside valid mods", () => {
                string root = Data("unresolved"); Model(root, "Unnumbered Mod", "Default_Attire", "Attire.mcd"); Model(root, "1506_Flammer", "Default_Attire", "Attire.mcd");
                var entries = DiscoveryScanner.Scan(root, Profiles(1506, "Flammer")).Entries;
                Equal(2, entries.Count); Equal(1, entries.Count(e => e.Id == 0)); Equal(1, entries.Count(e => e.Id == 1506));
            });
            Check("Conflicting child IDs require an explicit attire folder selection", () => {
                string root = Data("conflicting-children"); Model(root, "Mixed Mod", "368_Default_Attire", "Attire.mcd"); Model(root, "Mixed Mod", "101_Default_Attire", "Attire.mcd");
                var entries = DiscoveryScanner.Scan(root, null).Entries;
                Equal(1, entries.Count); Equal(0, entries[0].Id); Equal(0, entries[0].SourceId); Equal("Choose an attire folder", entries[0].Source);
            });
            Check("Mapped conflicting child models cannot attach their whole parent to a slot", () => {
                string root = Data("mapped-conflicting-children"); Model(root, "Mixed Mod", "368_Default_Attire", "Attire.mcd"); Model(root, "Mixed Mod", "101_Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] {
                    Pair(4000, CharacterMappingReader.ModelPathHash("Characters/Mixed Mod/368_Default_Attire/Attire.mcd")),
                    Pair(4001, CharacterMappingReader.ModelPathHash("Characters/Mixed Mod/101_Default_Attire/Attire.mcd")) });
                var entry = DiscoveryScanner.Scan(root, new[] { new DiscoveryProfile { Id = 4000, BaseId = 368, Name = "Slot One" }, new DiscoveryProfile { Id = 4001, BaseId = 101, Name = "Slot Two" } }).Entries.Single();
                Equal(0, entry.Id); Equal(0, entry.SourceId); Equal("Mixed Mod", entry.Name); Equal("Choose an attire folder", entry.Source);
            });
            Check("Plain folder name can match an explicit localized profile name", () => {
                string root = Data("name-match"); Model(root, "Seth_Rollins_A0", "Default_Attire", "Attire.mcd");
                var entry = DiscoveryScanner.Scan(root, new[] { new DiscoveryProfile { Id = 371, BaseId = 371, Name = "Seth Rollins" } }).Entries.Single();
                Equal(371, entry.Id); Equal(371, entry.SourceId); Equal("Name match", entry.Source);
            });
            Check("Shared display names keep every logical roster slot", () => {
                string root = Data("shared-name"); Model(root, "Undertaker", "Default_Attire", "Attire.mcd");
                var entries = DiscoveryScanner.Scan(root, new[] { new DiscoveryProfile { Id = 100, BaseId = 100, Name = "Undertaker" }, new DiscoveryProfile { Id = 200, BaseId = 100, Name = "Undertaker" } }).Entries;
                Equal(2, entries.Count); Equal(true, entries.Any(e => e.Id == 200 && e.SourceId == 100));
            });
            Check("Model mapping overrides logical folder ID and preserves physical source ID", () => {
                string root = Data("mapped-model"); string model = Model(root, "312_CM_Punk", "312_Default_Attire", "312_Attire.mcd");
                ulong hash = CharacterMappingReader.ModelPathHash("Characters/312_CM_Punk/312_Default_Attire/312_Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] { Pair(1481, hash) });
                var entry = DiscoveryScanner.Scan(root, Profiles(1481, "CM Punk GM")).Entries.Single();
                Equal(1481, entry.Id); Equal(312, entry.SourceId); Equal("CM Punk GM", entry.Name); Equal("Character mapping", entry.Source);
                Equal(Directory.GetParent(model).Parent.FullName, entry.Folder);
            });
            Check("A physical folder number cannot select a conflicting localized roster profile", () => {
                string root = Data("conflicting-profile"); Model(root, "109_Hollywood_Hogan_2002", "Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new KeyValuePair<int, ulong>[0]);
                var entry = DiscoveryScanner.Scan(root, Profiles(109, "Karl Anderson")).Entries.Single();
                Equal(0, entry.Id); Equal(109, entry.SourceId); Equal("Hollywood Hogan 2002", entry.Name); Equal("Choose roster slot", entry.Source);
            });
            Check("An unmapped numbered model may resolve by a unique exact localized name", () => {
                string root = Data("numbered-name"); Model(root, "109_Hollywood_Hogan_2002", "Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new KeyValuePair<int, ulong>[0]);
                var entry = DiscoveryScanner.Scan(root, new[] { new DiscoveryProfile { Id = 109, Name = "Karl Anderson" }, new DiscoveryProfile { Id = 9000, Name = "Hollywood Hogan 2002" } }).Entries.Single();
                Equal(9000, entry.Id); Equal(109, entry.SourceId); Equal("Hollywood Hogan 2002", entry.Name); Equal("Name match", entry.Source);
            });
            Check("A numbered model's matching profile name remains loadable with no path mapping", () => {
                string root = Data("matching-numbered-name"); Model(root, "343_Brock_Lesnar", "Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new KeyValuePair<int, ulong>[0]);
                var entry = DiscoveryScanner.Scan(root, Profiles(343, "BROCK LESNAR")).Entries.Single();
                Equal(343, entry.Id); Equal(343, entry.SourceId); Equal("Name match", entry.Source);
            });
            Check("A numbered model does not choose between duplicate localized names", () => {
                string root = Data("duplicate-numbered-name"); Model(root, "215_PAC", "Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new KeyValuePair<int, ulong>[0]);
                var entry = DiscoveryScanner.Scan(root, new[] { new DiscoveryProfile { Id = 4000, Name = "PAC" }, new DiscoveryProfile { Id = 4001, Name = "PAC" } }).Entries.Single();
                Equal(0, entry.Id); Equal(215, entry.SourceId); Equal("PAC", entry.Name);
            });
            Check("Explicit placeholder slot folders remain supported beside a mapping", () => {
                string root = Data("mapped-placeholder"); Model(root, "placeholder_368_A0", "Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new KeyValuePair<int, ulong>[0]);
                var entry = DiscoveryScanner.Scan(root, Profiles(368, "Roman Reigns")).Entries.Single(); Equal(368, entry.Id); Equal("Folder ID", entry.Source);
            });
            Check("Mapping-free collections retain their explicit folder-ID import", () => {
                string root = Data("legacy-folder-id"); Model(root, "109_Hollywood_Hogan_2002", "Default_Attire", "Attire.mcd");
                var entry = DiscoveryScanner.Scan(root, Profiles(109, "Karl Anderson")).Entries.Single(); Equal(109, entry.Id); Equal("Folder ID", entry.Source);
            });
            Check("Profiles without usable names retain explicit folder-ID import", () => {
                string root = Data("unnamed-profiles"); Model(root, "109_Hollywood_Hogan_2002", "Default_Attire", "Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new KeyValuePair<int, ulong>[0]);
                var entry = DiscoveryScanner.Scan(root, Profiles(109, null)).Entries.Single(); Equal(109, entry.Id); Equal(109, entry.SourceId);
            });
            Check("One shared physical model keeps all distinct mapped slots", () => {
                string root = Data("shared-model"); Model(root, "Plain Mod", "Default_Attire", "Attire.mcd");
                ulong hash = CharacterMappingReader.ModelPathHash("Characters/Plain Mod/Default_Attire/Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] { Pair(4000, hash), Pair(4001, hash), Pair(4000, hash) });
                var profiles = new[] { new DiscoveryProfile { Id = 4000, BaseId = 368, Name = "Slot One" }, new DiscoveryProfile { Id = 4001, BaseId = 101, Name = "Slot Two" } };
                var entries = DiscoveryScanner.Scan(root, profiles).Entries;
                Equal(2, entries.Count); Equal(368, entries.Single(e => e.Id == 4000).SourceId); Equal(101, entries.Single(e => e.Id == 4001).SourceId);
            });
            Check("Physical path matching includes the whole root-relative path", () => {
                string root = Data("path-collision"); Model(root, "Alpha", "Default_Attire", "Attire.mcd"); Model(root, "Beta", "Default_Attire", "Attire.mcd");
                ulong hash = CharacterMappingReader.ModelPathHash("Characters/Alpha/Default_Attire/Attire.mcd");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] { Pair(4000, hash) });
                var entries = DiscoveryScanner.Scan(root, Profiles(4000, "Mapped Alpha")).Entries;
                Equal(4000, entries.Single(e => Path.GetFileName(e.Folder) == "Alpha").Id); Equal(0, entries.Single(e => Path.GetFileName(e.Folder) == "Beta").Id);
            });
            Check("TRIBUTE sibling Characters work even without primary Characters", () => {
                string parent = Directory.CreateDirectory(Path.Combine(temporary, "tribute")).FullName;
                string root = Directory.CreateDirectory(Path.Combine(parent, "BakeMe")).FullName;
                string roster = Directory.CreateDirectory(Path.Combine(root, "Roster")).FullName; File.WriteAllText(Path.Combine(roster, "CharProfileTable.jsfb"), "fixture");
                string alternate = Directory.CreateDirectory(Path.Combine(parent, "BakeMe_Characters")).FullName; Model(alternate, "placeholder_368_A0", "368_Default_Attire", "Attire.mcd");
                var entry = DiscoveryScanner.Scan(root, Profiles(368, "Roman Reigns")).Entries.Single();
                Equal(368, entry.Id); Equal(Path.Combine(roster, "CharProfileTable.jsfb"), entry.RosterTable); Equal(true, entry.Folder.StartsWith(alternate, StringComparison.Ordinal));
            });
            Check("Corrupt mapping reports a warning and keeps physical folders", () => {
                string root = Data("corrupt"); Model(root, "1506_Flammer", "Default_Attire", "Attire.mcd"); File.WriteAllText(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), "invalid");
                var scan = DiscoveryScanner.Scan(root, null); Equal(1, scan.Entries.Count); Equal(1506, scan.Entries[0].Id); Equal(1, scan.Warnings.Count);
            });
            Check("Hidden helper folders are skipped and stock-only profiles are not mods", () => {
                string root = Data("hidden"); Model(root, "_cache", "Default_Attire", "Attire.mcd"); Model(root, "1506_Flammer", "Default_Attire", "Attire.mcd");
                var entries = DiscoveryScanner.Scan(root, new[] { new DiscoveryProfile { Id = 1506, Name = "Flammer" }, new DiscoveryProfile { Id = 368, Name = "Roman Reigns" } }).Entries;
                Equal(1, entries.Count); Equal(1506, entries[0].Id);
            });
            Check("Stock models resolve by verified path mapping rather than logical ID prefix", () => {
                string root = Data("stock-logical-id");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] { Pair(9000, CharacterMappingReader.ModelPathHash("characters/123_Stock_Body/Default_Attire/Attire.mcd")) });
                string index = Index("stock-logical.json", new Dictionary<string,string[]> { { "123_Stock_Body", new[] { "123_Stock_Body/Default_Attire/Attire.mcd" } }, { "9000_Wrong_Prefix", new[] { "9000_Wrong_Prefix/Default_Attire/Attire.mcd" } } });
                var models = StockModels.Read(root, index); Equal(1, models.Count); Equal("123_Stock_Body", models[9000]); Equal(false, models.ContainsKey(123));
            });
            Check("Ambiguous stock folders are excluded while multiple attires in one folder remain unique", () => {
                string root = Data("stock-ambiguity");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] {
                    Pair(4000, CharacterMappingReader.ModelPathHash("characters/100_A/A.mcd")), Pair(4000, CharacterMappingReader.ModelPathHash("characters/200_B/B.mcd")),
                    Pair(4001, CharacterMappingReader.ModelPathHash("characters/100_A/A.mcd")), Pair(4001, CharacterMappingReader.ModelPathHash("characters/100_A/C.mcd")) });
                string index = Index("stock-ambiguity.json", new Dictionary<string,string[]> { { "100_A", new[] { "100_A/A.mcd", "100_A/C.mcd" } }, { "200_B", new[] { "200_B/B.mcd" } } });
                var models = StockModels.Read(root, index); Equal(false, models.ContainsKey(4000)); Equal("100_A", models[4001]);
            });
            Check("Stock paths must remain inside their declared physical folder", () => {
                string root = Data("stock-paths");
                Mapping(Path.Combine(root, "Characters", "CharacterMapping.jsfb"), new[] { Pair(4000, CharacterMappingReader.ModelPathHash("characters/100_A/A.mcd")) });
                string index = Index("stock-paths.json", new Dictionary<string,string[]> { { "999_Bad", new[] { "100_A/A.mcd", "../100_A/A.mcd" } }, { "../100_A", new[] { "100_A/A.mcd" } } });
                Equal(0, StockModels.Read(root, index).Count);
            });
            if (args.Length > 0) Actual(args[0], args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null);
            Console.WriteLine(passed + " Discovery scanner checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static void Actual(string root, string sdb, string stockIndex)
    {
        string table = Path.Combine(root, "Roster", "CharProfileTable.jsfb");
        var profiles = DiscoveryProfiles.Read(table, sdb);
        Console.WriteLine("Discovery profiles: " + profiles.Count + "; localized: " + profiles.Count(p => !string.IsNullOrWhiteSpace(p.Name)));
        Check("Actual binary profiles and supplied localized names are available to the scanner", () => {
            Equal(1457, profiles.Count);
            if (sdb != null) Equal(true, profiles.Count(p => !string.IsNullOrWhiteSpace(p.Name)) > 100);
        });
        if (sdb != null) Check("Actual localized video roster names are read from the supplied SDB", () => {
            Equal("NICK JACKSON '14", profiles.Single(p => p.Id == 204).Name);
            Equal("DAX HARWOOD", profiles.Single(p => p.Id == 211).Name);
            Equal("CASH WHEELER", profiles.Single(p => p.Id == 213).Name);
            Equal("CM PUNK", profiles.Single(p => p.Id == 1481).Name);
        });
        if (stockIndex != null) Check("Actual stock index resolves verified logical-slot model associations", () => {
            var models = StockModels.Read(root, stockIndex); Equal(true, models.Count > 100);
            Console.WriteLine("Uniquely mapped stock model slots: " + models.Count);
            foreach (int id in new[] { 204, 211, 213, 1481 })
                Console.WriteLine("STOCK " + id + " " + (models.ContainsKey(id) ? models[id] : "not uniquely mapped"));
        });
        var scan = DiscoveryScanner.Scan(root, profiles);
        Check("Actual physical Discovery mods all remain visible", () => { Equal(10, scan.Entries.Select(e => e.Folder).Distinct().Count()); Equal(0, scan.Warnings.Count); });
        Check("Actual mapped Rey slot is 910 while physical source remains 123", () => {
            var entry = scan.Entries.Single(e => Path.GetFileName(e.Folder) == "123_Rey_Mysterio_2019");
            Equal(910, entry.Id); Equal(123, entry.SourceId); Equal("Character mapping", entry.Source);
        });
        Check("Actual mapped Punk slot is 1481 while physical source remains 312", () => {
            var entry = scan.Entries.Single(e => Path.GetFileName(e.Folder) == "312_CM_Punk");
            Equal(1481, entry.Id); Equal(312, entry.SourceId); Equal("Character mapping", entry.Source);
        });
        Check("Actual mapping variants collapse without dropping modded identities", () => {
            Equal(10, scan.Entries.Count); Equal(1, scan.Entries.Count(e => e.Id == 927));
            Equal(true, scan.Entries.All(e => e.IsMod && e.SourceId > 0 && Directory.Exists(e.Folder)));
        });
        Check("Actual unmapped Hollywood Hogan is not attached to Karl Anderson's profile", () => {
            var entry = scan.Entries.Single(e => Path.GetFileName(e.Folder) == "109_Hollywood_Hogan_2002");
            Equal(0, entry.Id); Equal(109, entry.SourceId); Equal("Hollywood Hogan 2002", entry.Name); Equal("Choose roster slot", entry.Source);
        });
        Check("Actual unmapped Brock resolves by his unique profile name", () => {
            var entry = scan.Entries.Single(e => Path.GetFileName(e.Folder) == "343_Brock_Lesnar");
            Equal(343, entry.Id); Equal(343, entry.SourceId); Equal("BROCK LESNAR", entry.Name); Equal("Name match", entry.Source);
        });
        Check("Actual localized names come from profiles without hash-derived labels", () => {
            foreach (var entry in scan.Entries)
            {
                var profile = profiles.FirstOrDefault(p => p.Id == entry.Id);
                if (profile != null && !string.IsNullOrWhiteSpace(profile.Name)) Equal(profile.Name, entry.Name);
                Console.WriteLine("MOD " + entry.Id + " source=" + entry.SourceId + " " + entry.Name + " (" + entry.Source + ")");
            }
        });
    }
    private static IEnumerable<DiscoveryProfile> Profiles(int id, string name) { return new[] { new DiscoveryProfile { Id = id, BaseId = id, Name = name } }; }
    private static string Data(string name) { string root = Directory.CreateDirectory(Path.Combine(temporary, name)).FullName; Directory.CreateDirectory(Path.Combine(root, "Characters")); return root; }
    private static string Model(string root, string name, string attire, string file) { string folder = Directory.CreateDirectory(Path.Combine(root, "Characters", name, attire)).FullName; string path = Path.Combine(folder, file); File.WriteAllText(path, "model fixture"); return path; }
    private static KeyValuePair<int,ulong> Pair(int id, ulong hash) { return new KeyValuePair<int,ulong>(id,hash); }
    private static string Index(string name, Dictionary<string,string[]> entries) { string path = Path.Combine(temporary,name); File.WriteAllText(path,JsonSerializer.Serialize(entries)); return path; }
    private static void Mapping(string path, IEnumerable<KeyValuePair<int,ulong>> input)
    {
        var rows = input.ToList();
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write((uint)16); output.Write(Encoding.ASCII.GetBytes("Char")); output.Write((ushort)6); output.Write((ushort)8); output.Write((ushort)4); output.Write((ushort)0);
            output.Write(8); output.Write((uint)4); output.Write((uint)rows.Count);
            long vector = output.BaseStream.Position; foreach (var row in rows) output.Write((uint)0);
            for (int i = 0; i < rows.Count; i++)
            {
                long vtable = output.BaseStream.Position; output.Write((ushort)20); output.Write((ushort)16);
                for (int field = 0; field < 8; field++) output.Write((ushort)(field == 0 ? 4 : field == 7 ? 8 : 0));
                long rowOffset = output.BaseStream.Position; output.Write((int)(rowOffset - vtable)); output.Write((ushort)rows[i].Key); output.Write((ushort)0); output.Write(rows[i].Value);
                long end = output.BaseStream.Position; output.BaseStream.Position = vector + 4 * i; output.Write((uint)(rowOffset - output.BaseStream.Position)); output.BaseStream.Position = end;
            }
        }
    }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name,error); } }
    private static void Equal<T>(T expected,T actual) { if (!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception("Expected '"+expected+"', got '"+actual+"'."); }
}
