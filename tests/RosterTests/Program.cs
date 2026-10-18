using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-roster-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Test("JSON array aliases and Unicode", () => {
                var entries = Load("array.json", "[{\"wrestler_id\":64,\"wrestler_name\":\"Rhea Ripley\"},{\"characterId\":65,\"displayName\":\"André the Giant\"}]");
                Equal(2, entries.Count); Equal("Rhea Ripley", entries[0].Name); Equal(65, entries[1].Id); Equal("André the Giant", entries[1].Name);
            });
            Test("JSON UTF-8 BOM", () => Equal(71, RosterImport.LoadFile(Write("bom.json", "[{\"id\":71,\"name\":\"AJ Styles\"}]", true)).Single().Id));
            Test("JSON wrappers and metadata", () => Equal(71, Load("wrapper.json", "{\"version\":1,\"data\":{\"roster\":[{\"id\":71,\"name\":\"AJ Styles\"}]}}").Single().Id));
            Test("JSON name-ID map", () => Equal(72, Load("namemap.json", "{\"AJ Styles\":72}").Single().Id));
            Test("JSON ID-name map", () => Equal("AJ Styles", Load("idmap.json", "{\"73\":\"AJ Styles\"}").Single().Name));
            Test("JSON keyed object name fallback", () => Equal("Seth Rollins", Load("nestednamemap.json", "{\"Seth Rollins\":{\"id\":74}}").Single().Name));
            Test("JSON keyed object ID fallback", () => Equal(75, Load("nestedidmap.json", "{\"75\":{\"name\":\"Seth Rollins\"}}").Single().Id));
            Test("JSON roster context round trip preserves explicit metadata and identity", () => {
                var original = new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = @"C:\Mods\2 Cold Scorpio", RosterTable = @"D:\Discovery\Roster\CharProfileTable.jsfb" };
                var entry = Load("context.json", JsonSerializer.Serialize(new[] { original })).Single();
                Equal(original.Id, entry.Id); Equal(original.Name, entry.Name); Equal(original.Folder, entry.Folder); Equal(original.RosterTable, entry.RosterTable);
                var alias = Load("contextalias.json", "[{\"id\":4000,\"name\":\"2 Cold Scorpio\",\"charProfileTablePath\":\"relative/CharProfileTable.jsfb\"}]").Single();
                Equal("relative/CharProfileTable.jsfb", alias.RosterTable);
            });
            Test("JSON numeric IDs with decimal representation", () => Equal(76, Load("decimal.json", "[{\"id\":76.0,\"name\":\"Seth Rollins\"}]").Single().Id));
            Test("Distinct IDs preserve same names and exact duplicates collapse", () => {
                var entries = Load("duplicates.json", "[{\"id\":12,\"name\":\"Undertaker\"},{\"id\":13,\"name\":\"Undertaker\"},{\"id\":12,\"name\":\"Undertaker\"}]");
                Equal(2, entries.Count); Equal(13, entries[1].Id);
            });
            Test("Fractional, negative, zero, and out-of-range IDs are rejected", () => {
                foreach (string id in new[] { "76.5", "-1", "0", "65536" })
                    Reject(() => Load("range.json", "[{\"id\":" + id + ",\"name\":\"Seth Rollins\"}]"), "whole number");
            });
            Test("Plain name arrays cannot invent IDs", () => Reject(() => Load("names.json", "[\"Roman Reigns\",\"The Rock\"]"), "IDs"));
            Test("Object without ID is rejected", () => Reject(() => Load("missingid.json", "[{\"name\":\"Roman Reigns\"}]"), "whole number"));
            Test("Game JSON stringname localization keys are not wrestler names", () => Reject(() => Load("game.json", "{\"CharProfileTable\":[{\"character_id\":123,\"stringname\":987654}]}"), "stringname"));
            Test("Numeric name values are not display names", () => Reject(() => Load("numericname.json", "[{\"id\":123,\"name\":987654}]"), "readable wrestler name"));
            Test("Numeric stage names remain strings", () => Equal("911", Load("numericstage.json", "[{\"id\":234,\"name\":\"911\"}]").Single().Name));
            Test("Malformed JSON is rejected clearly", () => Reject(() => Load("broken.json", "{\"roster\":"), "Could not read"));
            Test("Empty roster is rejected", () => Reject(() => Load("empty.json", "[]"), "No roster entries"));
            Test("Experimental release accepts only JSON and JSFB", () => {
                foreach (string ext in new[] { "csv", "tsv", "txt", "xlsx", "xls" })
                    Reject(() => Load("unsupported." + ext, ""), "Choose a JSON roster");
            });
            Test("Numbered folders use underscore and readable names", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "folders")).FullName;
                Directory.CreateDirectory(Path.Combine(path, "123_Roman_Reigns"));
                Directory.CreateDirectory(Path.Combine(path, "456_Roman_Reigns"));
                Directory.CreateDirectory(Path.Combine(path, "2 Cold Scorpio"));
                var entries = RosterImport.LoadDirectory(path);
                Equal(2, entries.Count); Equal(123, entries[0].Id); Equal("Roman Reigns", entries[1].Name);
                Equal(Path.Combine(path, "123_Roman_Reigns"), entries[0].Folder);
            });
            Test("Selected numbered root itself is imported", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "777_John_Cena")).FullName;
                Equal(777, RosterImport.LoadDirectory(path).Single().Id);
            });
            Test("Plain numeric stage names cannot invent folder IDs", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "plain")).FullName;
                Directory.CreateDirectory(Path.Combine(path, "2 Cold Scorpio"));
                Reject(() => RosterImport.LoadDirectory(path), "No roster entries");
            });
            Test("Discovery root and case-insensitive Characters folder", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "case")).FullName;
                Directory.CreateDirectory(Path.Combine(path, "cHaRaCtErS", "1506_Flammer"));
                Equal(1506, RosterImport.LoadDirectory(path).Single().Id);
            });
            Test("JSFB companion roster uses known Characters folders", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "jsfb")).FullName;
                Directory.CreateDirectory(Path.Combine(path, "Characters", "1506_Flammer"));
                Equal("Flammer", RosterImport.LoadFile(Profile(path)).Single().Name);
            });
            Test("JSFB file without companion Characters is actionable", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "nocompanion")).FullName;
                Reject(() => RosterImport.LoadFile(Profile(path)), "companion Characters");
            });
            Test("Unrelated JSFB is rejected", () => Reject(() => Load("CharMoveSetDataTable.jsfb", "binary"), "Only CharProfileTable.jsfb"));
            Test("JSFB requires real Char identifier and valid root", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "badprofile")).FullName;
                string profile = Profile(path);
                byte[] bytes = File.ReadAllBytes(profile); bytes[4] = (byte)'X'; File.WriteAllBytes(profile, bytes);
                Reject(() => RosterImport.LoadFile(profile), "not a valid CharProfileTable");
                profile = Profile(path); bytes = File.ReadAllBytes(profile); bytes[0] = 255; File.WriteAllBytes(profile, bytes);
                Reject(() => RosterImport.LoadFile(profile), "root/vector");
            });
            Test("JSFB truncated vector cannot pass structure validation", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "badvector")).FullName;
                string profile = Profile(path);
                byte[] bytes = File.ReadAllBytes(profile); bytes[24] = 1; File.WriteAllBytes(profile, bytes);
                Reject(() => RosterImport.LoadFile(profile), "root/vector");
            });
            Test("Plain leading-digit names survive the alias round trip", () => {
                Equal((int?)null, FolderNames.ExistingId("2 Cold Scorpio"));
                Equal("2 Cold Scorpio", FolderNames.DisplayName("2 Cold Scorpio"));
                string alias = FolderNames.AliasName(4000, "2 Cold Scorpio");
                Equal("4000_2_Cold_Scorpio", alias); Equal((int?)4000, FolderNames.ExistingId(alias)); Equal("2 Cold Scorpio", FolderNames.DisplayName(alias));
            });
            Test("Unsafe path characters are removed from aliases", () => {
                string alias = FolderNames.AliasName(123, "  A/B\\C: D?  ");
                Equal("123_A_B_C_D", alias);
            });
            Test("Discovery table discovery accepts all three root entry points", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "entrypoints")).FullName;
                string characters = Directory.CreateDirectory(Path.Combine(path, "cHaRaCtErS")).FullName;
                string table = Profile(path);
                Equal(table, CharacterFolders.FindDiscoveryTable(path));
                Equal(table, CharacterFolders.FindDiscoveryTable(characters));
                Equal(table, CharacterFolders.FindDiscoveryTable(Path.GetDirectoryName(table)));
            });
            Test("Standalone plain names produce a compatibility plan without changing source", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "2 Cold Scorpio")).FullName;
                var plan = CharacterFolders.Plan(path, new RosterEntry { Id = 4000, Name = "2 Cold Scorpio" });
                Equal(path, plan.SourceFolder); Equal("4000_2_Cold_Scorpio", plan.AliasName); Equal(false, plan.UseOriginal);
                Equal((string)null, plan.DataRoot); Equal((string)null, plan.RosterDirectory);
                Equal(0, Directory.GetFileSystemEntries(path).Length);
            });
            Test("Plan preserves imported roster context for an outside character source", () => {
                string discovery = Directory.CreateDirectory(Path.Combine(root, "external-context")).FullName;
                string known = Directory.CreateDirectory(Path.Combine(discovery, "Characters", "1506_Flammer")).FullName;
                string table = Profile(discovery);
                var selected = RosterImport.LoadFile(table).Single();
                string outside = Directory.CreateDirectory(Path.Combine(root, "Flammer")).FullName;
                var plan = CharacterFolders.Plan(outside, selected);
                Equal(outside, plan.SourceFolder); Equal("1506_Flammer", plan.AliasName); Equal(false, plan.UseOriginal);
                Equal(discovery, plan.DataRoot); Equal(Path.GetDirectoryName(table), plan.RosterDirectory); Equal(known, selected.Folder);
            });
            Test("Plan cache changes with the selected roster and prevents incorrect direct reuse", () => {
                string first = Directory.CreateDirectory(Path.Combine(root, "first-roster")).FullName;
                string source = Directory.CreateDirectory(Path.Combine(first, "Characters", "1506_Flammer")).FullName;
                string firstTable = Profile(first);
                string second = Directory.CreateDirectory(Path.Combine(root, "second-roster")).FullName;
                Directory.CreateDirectory(Path.Combine(second, "Characters", "1506_Flammer"));
                string secondTable = Profile(second);
                var firstPlan = CharacterFolders.Plan(source, RosterImport.LoadFile(firstTable).Single());
                var secondPlan = CharacterFolders.Plan(source, RosterImport.LoadFile(secondTable).Single());
                Equal(true, firstPlan.UseOriginal); Equal(false, secondPlan.UseOriginal);
                Equal(false, firstPlan.CacheKey == secondPlan.CacheKey); Equal(second, secondPlan.DataRoot);
            });
            Test("Missing or invalid plan inputs fail before any path writes", () => {
                try { CharacterFolders.Plan(Path.Combine(root, "not-present"), new RosterEntry { Id = 1, Name = "A" }); throw new Exception("Expected missing source rejection."); }
                catch (DirectoryNotFoundException) { }
                Equal((string)null, CharacterFolders.FindDiscoveryTable(Path.Combine(root, "not-present")));
                try { CharacterFolders.Plan(root, null); throw new Exception("Expected missing selection rejection."); }
                catch (ArgumentNullException) { }
            });
            Test("Opening an unrelated folder retains the saved roster without choosing another source", () => {
                string folder = Directory.CreateDirectory(Path.Combine(root, "standalone-session")).FullName;
                var remembered = new RosterEntry { Id = 4000, Name = "2 Cold Scorpio" };
                var session = RosterSession.Open(folder, new[] { remembered });
                Equal(1, session.Entries.Count); Equal(remembered, session.Entries[0]);
                Equal(false, session.ChooseCharacterFolder); Equal((string)null, session.DiscoveryTable);
            });
            Test("Discovery sessions isolate other contexts and prefer fresh folder metadata", () => {
                string discovery = Directory.CreateDirectory(Path.Combine(root, "session-merge")).FullName;
                string freshFolder = Directory.CreateDirectory(Path.Combine(discovery, "Characters", "1506_Flammer")).FullName;
                string table = Profile(discovery);
                var entries = new[] {
                    new RosterEntry { Id = 1506, Name = "FLAMMER", Folder = Path.Combine(root, "stale-folder"), RosterTable = table.ToUpperInvariant() },
                    new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", RosterTable = table.ToUpperInvariant() },
                    new RosterEntry { Id = 4001, Name = "Different Roster", RosterTable = Path.Combine(root, "other", "CharProfileTable.jsfb") },
                    new RosterEntry { Id = 4002, Name = "No Explicit Roster" }
                };
                var session = RosterSession.Open(discovery, entries);
                Equal(2, session.Entries.Count); Equal(true, session.ChooseCharacterFolder); Equal(table, session.DiscoveryTable);
                Equal(freshFolder, session.Entries.Single(e => e.Id == 1506).Folder);
                Equal(1, session.Entries.Count(e => e.Id == 4000));
            });
            Test("Remembered model folders persist only in collection mode", () => {
                string folder = Directory.CreateDirectory(Path.Combine(root, "remembered-model")).FullName;
                string original = Directory.CreateDirectory(Path.Combine(root, "original-model")).FullName;
                var entry = new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = original, RosterTable = "preserved-context" };
                RosterSession.RememberSelection(entry, folder, false); Equal(original, entry.Folder);
                RosterSession.RememberSelection(entry, folder, true); Equal(folder, entry.Folder); Equal("preserved-context", entry.RosterTable);
                var loaded = Load("remembered-folder.json", JsonSerializer.Serialize(new[] { entry })).Single();
                Equal(folder, loaded.Folder); Equal("preserved-context", loaded.RosterTable);
            });
            if (args.Length > 0) ActualDiscovery(args[0]);
            Console.WriteLine(passed.ToString(CultureInfo.InvariantCulture) + " roster and folder-name checks passed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void ActualDiscovery(string discovery)
    {
        var expected = new Dictionary<int,string> {
            {109,"Hollywood Hogan 2002"}, {123,"Rey Mysterio 2019"}, {1506,"Flammer"}, {210,"Undertaker BigEvil 2003"}, {246,"Mankind"},
            {312,"CM Punk"}, {337,"Vader"}, {343,"Brock Lesnar"}, {927,"Raquel Rodriguez"}, {958,"Hulk Hogan 2002 nWo"}
        };
        Test("Actual uploaded Discovery root loads all 10 known names and IDs", () => CheckActual(RosterImport.LoadDirectory(discovery), expected));
        Test("Actual uploaded JSFB validates signed vtable and loads all 10 companions", () => CheckActual(RosterImport.LoadFile(Path.Combine(discovery, "Roster", "CharProfileTable.jsfb")), expected));
        Test("Actual JSFB roster entries retain their original table context", () => {
            string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
            var entries = RosterImport.LoadFile(table);
            foreach (RosterEntry entry in entries) Equal(Path.GetFullPath(table), entry.RosterTable);
            var roundTrip = Load("actual-context.json", JsonSerializer.Serialize(entries));
            foreach (RosterEntry entry in roundTrip) Equal(Path.GetFullPath(table), entry.RosterTable);
            CheckActual(roundTrip, expected);
        });
        Test("All 10 actual Discovery characters produce usable source and roster plans", () => {
            string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
            var entries = RosterImport.LoadFile(table);
            foreach (RosterEntry entry in entries)
            {
                var plan = CharacterFolders.Plan(entry.Folder, entry);
                Equal(entry.Folder, plan.SourceFolder); Equal(Path.GetFullPath(discovery), plan.DataRoot);
                Equal(Path.GetDirectoryName(table), plan.RosterDirectory); Equal(true, plan.UseOriginal);
                Equal(FolderNames.AliasName(entry.Id, entry.Name), plan.AliasName);
                Equal(true, Directory.GetFiles(plan.SourceFolder, "*.mcd", SearchOption.AllDirectories).Length > 0);
            }
        });
        Test("Actual Discovery root, Characters, and Roster find the same table", () => {
            string table = Path.GetFullPath(Path.Combine(discovery, "Roster", "CharProfileTable.jsfb"));
            Equal(table, CharacterFolders.FindDiscoveryTable(discovery));
            Equal(table, CharacterFolders.FindDiscoveryTable(Path.Combine(discovery, "Characters")));
            Equal(table, CharacterFolders.FindDiscoveryTable(Path.Combine(discovery, "Roster")));
        });
        Test("Actual Discovery roster reopen preserves an eleventh saved character", () => {
            string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
            var saved = RosterImport.LoadFile(table);
            string addedFolder = Directory.CreateDirectory(Path.Combine(root, "saved-eleventh-model")).FullName;
            saved.Add(new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = addedFolder, RosterTable = table });
            var reloaded = Load("saved-eleventh.json", JsonSerializer.Serialize(saved));
            var session = RosterSession.Open(discovery, reloaded);
            Equal(11, session.Entries.Count); Equal(true, session.ChooseCharacterFolder);
            Equal(addedFolder, session.Entries.Single(e => e.Id == 4000).Folder);
            foreach (RosterEntry entry in session.Entries.Where(e => e.Id != 4000)) Equal(expected[entry.Id], entry.Name);
        });
        Test("Dropping actual Flammer discovers all ten but preserves single-source mapping mode", () => {
            string source = Path.Combine(discovery, "Characters", "1506_Flammer");
            var session = RosterSession.Open(source, null);
            CheckActual(session.Entries, expected); Equal(false, session.ChooseCharacterFolder);
            Equal(Path.GetFullPath(Path.Combine(discovery, "Roster", "CharProfileTable.jsfb")), session.DiscoveryTable);
        });
        Test("Actual roster plus one new unnumbered character imports 11 without guessing its ID", () => {
            string stage = Directory.CreateDirectory(Path.Combine(root, "added-character")).FullName;
            string characters = Directory.CreateDirectory(Path.Combine(stage, "Characters")).FullName;
            var actual = RosterImport.LoadDirectory(discovery);
            foreach (RosterEntry entry in actual) Directory.CreateDirectory(Path.Combine(characters, Path.GetFileName(entry.Folder)));
            Directory.CreateDirectory(Path.Combine(characters, "2 Cold Scorpio"));
            CheckActual(RosterImport.LoadDirectory(stage), expected);
            actual.Add(new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = Path.Combine(characters, "2 Cold Scorpio") });
            string rosterPath = Path.Combine(stage, "roster.json"); File.WriteAllText(rosterPath, JsonSerializer.Serialize(actual));
            var loaded = RosterImport.LoadFile(rosterPath); Equal(11, loaded.Count);
            var added = loaded.Single(e => e.Name == "2 Cold Scorpio"); Equal(4000, added.Id);
            Equal("4000_2_Cold_Scorpio", FolderNames.AliasName(added.Id, added.Name));
        });
    }
    private static void CheckActual(List<RosterEntry> entries, Dictionary<int,string> expected)
    {
        Equal(10, entries.Count);
        foreach (RosterEntry entry in entries) { Equal(expected[entry.Id], entry.Name); Equal(true, Directory.Exists(entry.Folder)); }
    }
    private static string Profile(string discovery)
    {
        string roster = Directory.CreateDirectory(Path.Combine(discovery, "Roster")).FullName;
        string path = Path.Combine(roster, "CharProfileTable.jsfb");
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write((uint)16); output.Write(Encoding.ASCII.GetBytes("Char"));
            output.Write((ushort)6); output.Write((ushort)8); output.Write((ushort)4); output.Write((ushort)0);
            output.Write(8); output.Write((uint)4); output.Write((uint)0);
        }
        return path;
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception) { throw new Exception("FAIL " + name, exception); }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'.");
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException exception)
        {
            if (exception.Message.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0) return;
            throw new Exception("Expected error containing '" + message + "', got: " + exception.Message, exception);
        }
        throw new Exception("Expected InvalidDataException containing: " + message);
    }
    private static string Write(string name, string content, bool bom = false)
    {
        string path = Path.Combine(root, name); File.WriteAllText(path, content, new UTF8Encoding(bom)); return path;
    }
    private static List<RosterEntry> Load(string name, string content) { return RosterImport.LoadFile(Write(name, content)); }
}
