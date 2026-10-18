using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
                var original = new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = @"C:\Mods\2 Cold Scorpio", RosterTable = @"D:\Discovery\Roster\CharProfileTable.jsfb",
                    SourceId = 312, IsMod = true, Source = "Character mapping", StockFolder = "312_CM_Punk" };
                var entry = Load("context.json", JsonSerializer.Serialize(new[] { original })).Single();
                Equal(original.Id, entry.Id); Equal(original.Name, entry.Name); Equal(original.Folder, entry.Folder); Equal(original.RosterTable, entry.RosterTable);
                Equal(original.SourceId, entry.SourceId); Equal(original.IsMod, entry.IsMod); Equal(original.Source, entry.Source); Equal(original.StockFolder, entry.StockFolder);
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
            Test("Roster shortcut accepts root, Characters, Roster, and the profile file", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "shortcut-entrypoints")).FullName;
                string characters = Directory.CreateDirectory(Path.Combine(path, "Characters")).FullName;
                string table = Profile(path);
                foreach (string entry in new[] { path, characters, Path.GetDirectoryName(table), table }) Equal(path, RosterAccess.RootFor(entry));
                Equal((string)null, RosterAccess.RootFor(Write("unrelated.json", "[]")));
                Equal((string)null, RosterAccess.RootFor(Path.Combine(root, "missing-shortcut")));
            });
            Test("Roster shortcut prioritizes the remembered root then native setup then saved roster", () => {
                string first = DiscoveryFolder("shortcut-first"), second = DiscoveryFolder("shortcut-second"), third = DiscoveryFolder("shortcut-third");
                var saved = new[] { new RosterEntry { Id = 1, Name = "Saved", RosterTable = Path.Combine(third, "Roster", "CharProfileTable.jsfb") } };
                Equal(first, RosterAccess.FindRoot(first, second, saved));
                Equal(second, RosterAccess.FindRoot(Path.Combine(root, "missing"), second, saved));
                Equal(third, RosterAccess.FindRoot(null, null, saved));
                Equal((string)null, RosterAccess.FindRoot(null, null, new RosterEntry[] { null }));
            });
            Test("Standalone plain names produce a compatibility plan without changing source", () => {
                string path = Directory.CreateDirectory(Path.Combine(root, "2 Cold Scorpio")).FullName;
                var plan = CharacterFolders.Plan(path, new RosterEntry { Id = 4000, Name = "2 Cold Scorpio" });
                Equal(path, plan.SourceFolder); Equal("4000_2_Cold_Scorpio", plan.AliasName); Equal(false, plan.UseOriginal);
                Equal((string)null, plan.DataRoot); Equal((string)null, plan.RosterDirectory);
                Equal(0, Directory.GetFileSystemEntries(path).Length);
            });
            Test("A dropped numbered model keeps its own physical ID when mapped to another roster slot", () => {
                string source = Directory.CreateDirectory(Path.Combine(root, "123_Dropped_Model")).FullName;
                var selected = new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" };
                var plan = CharacterFolders.Plan(source, selected);
                Equal("123_CM_PUNK", plan.AliasName); Equal(source, plan.SourceFolder); Equal(1481, selected.Id); Equal(312, selected.SourceId);
            });
            Test("A dropped plain-name model uses the unique numbered attire ID before the selected profile base ID", () => {
                string source = Directory.CreateDirectory(Path.Combine(root, "Plain Dropped Model")).FullName;
                Directory.CreateDirectory(Path.Combine(source, "368_Default_Attire"));
                Directory.CreateDirectory(Path.Combine(source, "368_Alternate_Attire"));
                var selected = new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" };
                Equal((int?)368, CharacterFolders.PhysicalId(source)); Equal(false, CharacterFolders.HasAmbiguousChildren(source));
                Equal("368_CM_PUNK", CharacterFolders.Plan(source, selected).AliasName); Equal(1481, selected.Id); Equal(312, selected.SourceId);
            });
            Test("A dropped placeholder model keeps its explicit physical ID", () => {
                string source = Directory.CreateDirectory(Path.Combine(root, "placeholder_368_A0")).FullName;
                Directory.CreateDirectory(Path.Combine(source, "369_Alternate_Attire"));
                var selected = new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" };
                Equal((int?)368, CharacterFolders.PhysicalId(source)); Equal(false, CharacterFolders.HasAmbiguousChildren(source));
                Equal("368_CM_PUNK", CharacterFolders.Plan(source, selected).AliasName);
            });
            Test("Numeric stage names cannot invent physical model IDs", () => {
                string source = Directory.CreateDirectory(Path.Combine(root, "2 Cold Scorpio")).FullName;
                Equal((int?)null, CharacterFolders.PhysicalId(source)); Equal(false, CharacterFolders.HasAmbiguousChildren(source));
                Equal("4000_2_Cold_Scorpio", CharacterFolders.Plan(source, new RosterEntry { Id = 4000, Name = "2 Cold Scorpio" }).AliasName);
            });
            Test("A plain parent with different numbered attire IDs requires choosing a specific model folder", () => {
                string source = Directory.CreateDirectory(Path.Combine(root, "Ambiguous Dropped Model")).FullName;
                string first = Directory.CreateDirectory(Path.Combine(source, "368_Default_Attire")).FullName;
                Directory.CreateDirectory(Path.Combine(source, "369_Default_Attire"));
                var selected = new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" };
                Equal((int?)null, CharacterFolders.PhysicalId(source)); Equal(true, CharacterFolders.HasAmbiguousChildren(source));
                Reject(() => CharacterFolders.Plan(source, selected), "different model IDs");
                Equal("368_CM_PUNK", CharacterFolders.Plan(first, selected).AliasName); Equal(2, Directory.GetDirectories(source).Length);
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
            Test("Refreshing current logical IDs preserves distinct discovered model variants", () => {
                string container = Directory.CreateDirectory(Path.Combine(root, "session-variants")).FullName;
                string discovery = Directory.CreateDirectory(Path.Combine(container, "Data")).FullName;
                string first = Directory.CreateDirectory(Path.Combine(discovery, "Characters", "1506_Flammer")).FullName;
                string second = Directory.CreateDirectory(Path.Combine(container, "BakeMe_Characters", "Characters", "1506_Flammer")).FullName;
                string table = Profile(discovery);
                var previous = new RosterEntry { Id = 1506, Name = "Old variant label", RosterTable = table,
                    Folder = Directory.CreateDirectory(Path.Combine(container, "Previous")).FullName, SourceId = 123, Source = "Manual selection" };
                var refreshed = RosterSession.Refresh(table, new[] { previous });
                Equal(2, refreshed.Count); Equal(true, refreshed.All(e => e.Id == 1506 && e.Name == "Flammer" && e.SourceId == 1506));
                Equal(true, refreshed.Any(e => e.Folder == first)); Equal(true, refreshed.Any(e => e.Folder == second));
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
            Test("Remembering a numbered model updates its physical source ID while retaining the logical roster ID", () => {
                string folder = Directory.CreateDirectory(Path.Combine(root, "123_Chosen_Model")).FullName;
                var selected = new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" };
                RosterSession.RememberSelection(selected, folder, false); Equal(312, selected.SourceId); Equal((string)null, selected.Folder);
                RosterSession.RememberSelection(selected, folder, true); Equal(123, selected.SourceId); Equal(1481, selected.Id); Equal(folder, selected.Folder);
            });
            Test("Remembering a plain model stores the unique numbered attire source ID", () => {
                string folder = Directory.CreateDirectory(Path.Combine(root, "Remembered Plain Model")).FullName;
                Directory.CreateDirectory(Path.Combine(folder, "368_Default_Attire"));
                var selected = new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" };
                RosterSession.RememberSelection(selected, folder, true);
                Equal(368, selected.SourceId); Equal(1481, selected.Id); Equal(folder, selected.Folder);
            });
            Test("An existing JSON roster inherits its discovered model mapping while preserving the chosen name and logical ID", () => {
                string discovery = DiscoveryFolder("json-bound-model");
                string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
                string folder = Directory.CreateDirectory(Path.Combine(discovery, "Characters", "312_CM_Punk")).FullName;
                var current = new[] { new RosterEntry { Id = 1481, Name = "CM PUNK", RosterTable = table.ToUpperInvariant(), Folder = folder,
                    SourceId = 312, StockFolder = "312_CM_Punk", Source = "Character mapping", IsMod = true } };
                var imported = Load("json-bound-model.json", "[{\"id\":1481,\"name\":\"My Custom CM Punk\"}]");
                RosterSession.BindImportedModels(imported, current, table);
                var entry = imported.Single(); Equal(1481, entry.Id); Equal("My Custom CM Punk", entry.Name); Equal(table, entry.RosterTable);
                Equal(folder, entry.Folder); Equal(312, entry.SourceId); Equal("312_CM_Punk", entry.StockFolder); Equal("Character mapping", entry.Source); Equal(true, entry.IsMod);
                Equal("312_My_Custom_CM_Punk", CharacterFolders.Plan(entry.Folder, entry).AliasName);
            });
            Test("JSON model binding leaves ambiguous slots and different roster contexts unassigned", () => {
                string discovery = DiscoveryFolder("json-binding-ambiguity");
                string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb"), otherTable = Path.Combine(root, "other-json-roster", "CharProfileTable.jsfb");
                string first = Directory.CreateDirectory(Path.Combine(root, "json-first-model")).FullName;
                string second = Directory.CreateDirectory(Path.Combine(root, "json-second-model")).FullName;
                var current = new[] {
                    new RosterEntry { Id = 1481, Name = "First", RosterTable = table, Folder = first, SourceId = 312, IsMod = true },
                    new RosterEntry { Id = 1481, Name = "Second", RosterTable = table, Folder = second, SourceId = 123, IsMod = true },
                    new RosterEntry { Id = 211, Name = "Current", RosterTable = table, Folder = first, SourceId = 312, IsMod = true },
                    new RosterEntry { Id = 312, Name = "Other context", RosterTable = otherTable, Folder = first, SourceId = 312, IsMod = true }
                };
                var imported = new[] {
                    new RosterEntry { Id = 1481, Name = "Choose model" },
                    new RosterEntry { Id = 211, Name = "Keep other roster", RosterTable = otherTable },
                    new RosterEntry { Id = 312, Name = "No current model" }
                };
                RosterSession.BindImportedModels(imported, current, table);
                foreach (RosterEntry entry in imported) { Equal((string)null, entry.Folder); Equal(0, entry.SourceId); Equal((string)null, entry.StockFolder); Equal((string)null, entry.Source); Equal(false, entry.IsMod); }
                Equal(table, imported[0].RosterTable); Equal(otherTable, imported[1].RosterTable); Equal(table, imported[2].RosterTable);
            });
            Test("JSON model binding retains explicit folder and source metadata", () => {
                string discovery = DiscoveryFolder("json-explicit-model");
                string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
                string selected = Directory.CreateDirectory(Path.Combine(root, "123_Explicit_Model")).FullName;
                string discovered = Directory.CreateDirectory(Path.Combine(root, "312_Discovered_Model")).FullName;
                var imported = Load("json-explicit-model.json", JsonSerializer.Serialize(new[] { new RosterEntry {
                    Id = 1481, Name = "My Selected Model", RosterTable = table, Folder = selected, SourceId = 123,
                    StockFolder = "123_Explicit_Stock", Source = "Explicit JSON selection", IsMod = false
                } }));
                RosterSession.BindImportedModels(imported, new[] { new RosterEntry { Id = 1481, Name = "CM PUNK", RosterTable = table, Folder = discovered,
                    SourceId = 312, StockFolder = "312_CM_Punk", Source = "Character mapping", IsMod = true } }, table);
                var entry = imported.Single(); Equal(1481, entry.Id); Equal("My Selected Model", entry.Name); Equal(selected, entry.Folder);
                Equal(123, entry.SourceId); Equal("123_Explicit_Stock", entry.StockFolder); Equal("Explicit JSON selection", entry.Source); Equal(true, entry.IsMod);
            });
            Test("An edited JSON wrestler name cannot inherit an unverified model for the previous name", () => {
                string discovery = DiscoveryFolder("json-renamed-model");
                string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
                string previousFolder = Directory.CreateDirectory(Path.Combine(root, "json-previous-name-model")).FullName;
                string explicitFolder = Directory.CreateDirectory(Path.Combine(root, "json-new-explicit-model")).FullName;
                foreach (string source in new[] { "Found in models folder", "Chosen model folder", "Chosen folder" })
                {
                    var previous = new RosterEntry { Id = 4000, Name = "Old Wrestler", Folder = previousFolder,
                        RosterTable = table, SourceId = 1506, Source = source, IsMod = true };
                    string json = Write("json-renamed-model.json", "[{\"id\":4000,\"name\":\"New Wrestler\"}]");
                    RosterEntry current = RosterSession.Refresh(json, new[] { previous }, table).Single();
                    Equal("New Wrestler", current.Name); Equal(table, current.RosterTable); Equal((string)null, current.Folder);
                    Equal(0, current.SourceId); Equal((string)null, current.Source); Equal(false, current.IsMod);
                    File.WriteAllText(json, JsonSerializer.Serialize(new[] { new RosterEntry { Id = 4000, Name = "New Wrestler",
                        Folder = explicitFolder, SourceId = 123, Source = "Explicit JSON selection" } }));
                    current = RosterSession.Refresh(json, new[] { previous }, table).Single();
                    Equal(explicitFolder, current.Folder); Equal(123, current.SourceId); Equal("Explicit JSON selection", current.Source);
                    previous.StockFolder = "1506_Verified_Model";
                    File.WriteAllText(json, "[{\"id\":4000,\"name\":\"Custom Verified Alias\"}]");
                    current = RosterSession.Refresh(json, new[] { previous }, table).Single();
                    Equal(previousFolder, current.Folder); Equal(1506, current.SourceId); Equal(previous.StockFolder, current.StockFolder);
                }
            });
            SdbChecks();
            SelectionChecks();
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
        string table = Path.GetFullPath(Path.Combine(discovery, "Roster", "CharProfileTable.jsfb"));
        var profiles = DiscoveryProfiles.Read(table);
        var actual = RosterImport.LoadFile(table);
        Test("Actual uploaded JSFB reads all 1457 profile IDs and 1308 localized names", () => {
            Equal(1457, profiles.Count); Equal(1457, profiles.Select(p => p.Id).Distinct().Count());
            Equal(1308, profiles.Count(p => !string.IsNullOrWhiteSpace(p.Name)));
            Equal(1458, actual.Count); Equal(1457, actual.Where(p => p.Id > 0).Select(p => p.Id).Distinct().Count());
            Equal(1, actual.Count(p => p.Id == 0));
            Equal("NICK JACKSON '14", actual.Single(e => e.Id == 204).Name);
            Equal("DAX HARWOOD", actual.Single(e => e.Id == 211).Name);
            Equal("CASH WHEELER", actual.Single(e => e.Id == 213).Name);
            Equal("CM PUNK", actual.Single(e => e.Id == 1481).Name);
            Equal("REY MYSTERIO '19", actual.Single(e => e.Id == 910).Name);
            Equal(true, actual.Any(e => e.Folder == null));
            Equal(false, actual.Any(e => string.IsNullOrWhiteSpace(e.Name)));
        });
        Test("Actual mapped slots preserve physical model IDs instead of using folder prefixes as roster IDs", () => {
            var punk = actual.Single(e => e.Id == 1481); Equal(312, punk.SourceId); Equal(true, punk.IsMod);
            Equal(Path.Combine(discovery, "Characters", "312_CM_Punk"), punk.Folder); Equal("Character mapping", punk.Source);
            var rey = actual.Single(e => e.Id == 910); Equal(123, rey.SourceId); Equal(true, rey.IsMod);
            Equal(Path.Combine(discovery, "Characters", "123_Rey_Mysterio_2019"), rey.Folder);
            Equal((string)null, actual.Single(e => e.Id == 123).Folder);
            Equal((string)null, actual.Single(e => e.Id == 312).Folder);
        });
        Test("Actual unreferenced Hollywood Hogan stays separate from Karl Anderson's roster slot", () => {
            var karl = actual.Single(e => e.Id == 109); Equal("KARL ANDERSON", karl.Name); Equal((string)null, karl.Folder);
            var hogan = actual.Single(e => e.Id == 0); Equal("Hollywood Hogan 2002", hogan.Name); Equal(109, hogan.SourceId);
            Equal(Path.Combine(discovery, "Characters", "109_Hollywood_Hogan_2002"), hogan.Folder); Equal("Choose roster slot", hogan.Source);
        });
        Test("Actual JSFB roster entries retain their original table context", () => {
            foreach (RosterEntry entry in actual) Equal(table, entry.RosterTable);
            var roundTrip = Load("actual-context.json", JsonSerializer.Serialize(actual.Where(e => e.Id > 0).ToList()));
            Equal(1457, roundTrip.Count);
            foreach (RosterEntry entry in roundTrip)
            {
                RosterEntry original = actual.Single(e => e.Id == entry.Id);
                Equal(table, entry.RosterTable); Equal(original.Name, entry.Name); Equal(original.Folder, entry.Folder);
                Equal(original.SourceId, entry.SourceId); Equal(original.IsMod, entry.IsMod); Equal(original.Source, entry.Source); Equal(original.StockFolder, entry.StockFolder);
            }
        });
        Test("All 10 actual Discovery characters produce usable source and roster plans", () => {
            var models = actual.Where(e => e.Folder != null).ToList(); Equal(10, models.Count);
            foreach (RosterEntry entry in models)
            {
                var plan = CharacterFolders.Plan(entry.Folder, entry);
                Equal(entry.Folder, plan.SourceFolder); Equal(Path.GetFullPath(discovery), plan.DataRoot);
                Equal(Path.GetDirectoryName(table), plan.RosterDirectory);
                Equal(FolderNames.ExistingId(Path.GetFileName(entry.Folder)).Value, entry.SourceId);
                Equal(FolderNames.AliasName(entry.SourceId, entry.Name), plan.AliasName);
                Equal(true, Directory.GetFiles(plan.SourceFolder, "*.mcd", SearchOption.AllDirectories).Length > 0);
            }
        });
        Test("Actual Discovery root, Characters, and Roster find the same table", () => {
            Equal(table, CharacterFolders.FindDiscoveryTable(discovery));
            Equal(table, CharacterFolders.FindDiscoveryTable(Path.Combine(discovery, "Characters")));
            Equal(table, CharacterFolders.FindDiscoveryTable(Path.Combine(discovery, "Roster")));
        });
        Test("Actual Discovery roster reopen preserves an added character alongside every profile", () => {
            var saved = RosterImport.LoadFile(table);
            string addedFolder = Directory.CreateDirectory(Path.Combine(root, "saved-added-model")).FullName;
            saved.Add(new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = addedFolder, RosterTable = table, SourceId = 1506, IsMod = true, Source = "Manual selection" });
            var reloaded = Load("saved-added.json", JsonSerializer.Serialize(saved.Where(e => e.Id > 0).ToList()));
            var session = RosterSession.Open(discovery, reloaded);
            Equal(1459, session.Entries.Count); Equal(true, session.ChooseCharacterFolder);
            var added = session.Entries.Single(e => e.Id == 4000); Equal(addedFolder, added.Folder); Equal(1506, added.SourceId); Equal(true, added.IsMod);
            foreach (RosterEntry entry in session.Entries.Where(e => e.Id != 4000)) Equal(actual.Single(e => e.Id == entry.Id).Name, entry.Name);
        });
        Test("Profile rows retain an explicit model association only from the same Discovery roster", () => {
            string selected = Directory.CreateDirectory(Path.Combine(root, "remembered-dax")).FullName;
            var original = actual.Single(e => e.Id == 211); Equal((string)null, original.Folder);
            var saved = new RosterEntry { Id = original.Id, Name = original.Name, Folder = selected, RosterTable = table.ToUpperInvariant(), SourceId = 312, IsMod = true, Source = "Manual selection" };
            var restored = RosterSession.Open(discovery, new[] { saved }).Entries.Single(e => e.Id == 211);
            Equal(selected, restored.Folder); Equal(312, restored.SourceId); Equal(true, restored.IsMod); Equal("Manual selection", restored.Source);
            saved.RosterTable = Path.Combine(root, "other-roster", "CharProfileTable.jsfb");
            Equal((string)null, RosterSession.Open(discovery, new[] { saved }).Entries.Single(e => e.Id == 211).Folder);
            saved.RosterTable = table; saved.Folder = Path.Combine(root, "missing-model");
            Equal((string)null, RosterSession.Open(discovery, new[] { saved }).Entries.Single(e => e.Id == 211).Folder);
        });
        Test("A current SDB name replaces a remembered label without retaining its old model", () => {
            string previousModel = Directory.CreateDirectory(Path.Combine(root, "renamed-slot-previous-model")).FullName;
            var saved = new RosterEntry { Id = 211, Name = "Previous slot name", Folder = previousModel,
                RosterTable = table, SourceId = 312, IsMod = true, Source = "Manual selection" };
            var session = RosterSession.Open(discovery, new[] { saved });
            Equal(actual.Count, session.Entries.Count);
            var current = session.Entries.Single(e => e.Id == 211);
            Equal("DAX HARWOOD", current.Name); Equal((string)null, current.Folder);
            Equal(false, session.Entries.Any(e => e.Name == saved.Name));
            saved.Name = "Wrestler 211";
            current = RosterSession.Open(discovery, new[] { saved }).Entries.Single(e => e.Id == 211);
            Equal("DAX HARWOOD", current.Name); Equal((string)null, current.Folder);
        });
        Test("Conflicting remembered model associations cannot choose a model arbitrarily", () => {
            string first = Directory.CreateDirectory(Path.Combine(root, "conflicting-dax-first")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "conflicting-dax-second")).FullName;
            var saved = new[] {
                new RosterEntry { Id = 211, Name = "DAX HARWOOD", Folder = first, RosterTable = table, SourceId = 312, IsMod = true },
                new RosterEntry { Id = 211, Name = "DAX HARWOOD", Folder = second, RosterTable = table, SourceId = 109, IsMod = true }
            };
            var current = RosterSession.Open(discovery, saved).Entries.Single(e => e.Id == 211);
            Equal((string)null, current.Folder);
            saved[1].Folder = first;
            current = RosterSession.Open(discovery, saved).Entries.Single(e => e.Id == 211);
            Equal((string)null, current.Folder); // One folder with conflicting physical IDs is ambiguous too.
            saved[1].SourceId = 312;
            current = RosterSession.Open(discovery, saved).Entries.Single(e => e.Id == 211);
            Equal(first, current.Folder); Equal(312, current.SourceId);
            saved[0].SourceId = saved[1].SourceId = 0;
            int baseId = actual.Single(e => e.Id == 211).SourceId;
            current = RosterSession.Open(discovery, saved).Entries.Single(e => e.Id == 211);
            Equal(first, current.Folder); Equal(baseId, current.SourceId);
        });
        Test("Fresh mapped model associations override stale remembered metadata", () => {
            var original = actual.Single(e => e.Id == 1481);
            string stale = Directory.CreateDirectory(Path.Combine(root, "stale-punk-model")).FullName;
            var saved = new RosterEntry { Id = 1481, Name = original.Name, Folder = stale, RosterTable = table, SourceId = 4000, IsMod = false, Source = "Old metadata" };
            var fresh = RosterSession.Open(discovery, new[] { saved }).Entries.Single(e => e.Id == 1481);
            Equal(original.Folder, fresh.Folder); Equal(312, fresh.SourceId); Equal(true, fresh.IsMod); Equal("Character mapping", fresh.Source);
        });
        Test("Legacy folder-derived saved rows migrate to decoded logical IDs and SDB names", () => {
            var legacy = RosterImport.LoadDirectory(discovery);
            foreach (RosterEntry entry in legacy) entry.RosterTable = table;
            var saved = Load("legacy-folder-roster.json", JsonSerializer.Serialize(legacy));
            Equal(0, saved.Single(e => e.Id == 123).SourceId); Equal((string)null, saved.Single(e => e.Id == 312).Source);
            var migrated = RosterSession.Open(discovery, saved).Entries; Equal(1458, migrated.Count);
            Equal((string)null, migrated.Single(e => e.Id == 123).Folder); Equal((string)null, migrated.Single(e => e.Id == 312).Folder);
            Equal(123, migrated.Single(e => e.Id == 910).SourceId); Equal(312, migrated.Single(e => e.Id == 1481).SourceId);
            Equal(actual.Single(e => e.Id == 109).Name, migrated.Single(e => e.Id == 109).Name);
            foreach (RosterEntry entry in migrated.Where(e => e.Folder != null)) Equal(1, migrated.Count(e => e.Folder == entry.Folder));
        });
        Test("A saved guessed Hollywood Hogan body is not restored to Karl Anderson on upgrade", () => {
            var guessed = new RosterEntry { Id = 109, Name = "KARL ANDERSON", Folder = Path.Combine(discovery, "Characters", "109_Hollywood_Hogan_2002"),
                RosterTable = table, SourceId = 109, IsMod = true, Source = "Folder ID" };
            var session = RosterSession.Open(discovery, new[] { guessed }); Equal(1458, session.Entries.Count);
            var karl = session.Entries.Single(e => e.Id == 109); Equal("KARL ANDERSON", karl.Name); Equal((string)null, karl.Folder);
            Equal(guessed.Folder, session.Entries.Single(e => e.Id == 0).Folder);
        });
        Test("A saved physical folder ID cannot bypass a freshly decoded different logical slot", () => {
            var oldProfile = actual.Single(e => e.Id == 123);
            var guessed = new RosterEntry { Id = 123, Name = oldProfile.Name, Folder = Path.Combine(discovery, "Characters", "123_Rey_Mysterio_2019"),
                RosterTable = table, SourceId = 123, IsMod = true, Source = "Folder ID" };
            var session = RosterSession.Open(discovery, new[] { guessed }); Equal(1458, session.Entries.Count);
            Equal((string)null, session.Entries.Single(e => e.Id == 123).Folder);
            Equal(guessed.Folder, session.Entries.Single(e => e.Id == 910).Folder);
        });
        Test("Migration retains an explicit chosen model even when its physical number differs", () => {
            var chosen = new RosterEntry { Id = 109, Name = "KARL ANDERSON", Folder = Path.Combine(discovery, "Characters", "109_Hollywood_Hogan_2002"),
                RosterTable = table, SourceId = 109, IsMod = true, Source = "Chosen model" };
            var session = RosterSession.Open(discovery, new[] { chosen }); Equal(1458, session.Entries.Count);
            Equal(chosen.Folder, session.Entries.Single(e => e.Id == 109).Folder); Equal("Chosen model", session.Entries.Single(e => e.Id == 109).Source);
        });
        Test("Migration preserves an explicit new manual choice using an existing model folder", () => {
            var chosen = new RosterEntry { Id = 4000, Name = "2 Cold Scorpio", Folder = Path.Combine(discovery, "Characters", "312_CM_Punk"),
                RosterTable = table, SourceId = 312, IsMod = true, Source = "Manual selection" };
            var session = RosterSession.Open(discovery, new[] { chosen }); Equal(1459, session.Entries.Count);
            Equal(chosen.Folder, session.Entries.Single(e => e.Id == 4000).Folder); Equal(312, session.Entries.Single(e => e.Id == 4000).SourceId);
        });
        Test("Dropping actual Flammer discovers the full roster and preserves single-source mapping mode", () => {
            string source = Path.Combine(discovery, "Characters", "1506_Flammer");
            var session = RosterSession.Open(source, null);
            Equal(1458, session.Entries.Count); Equal(false, session.ChooseCharacterFolder);
            Equal(table, session.DiscoveryTable); Equal("DAX HARWOOD", session.Entries.Single(e => e.Id == 211).Name);
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
        Test("An added plain-name MCD is listed without inventing an ID and accepts an explicit roster choice", () => {
            string stage = DiscoveryFolder("added-plain-mcd");
            string stagedTable = Path.Combine(stage, "Roster", "CharProfileTable.jsfb"); File.Copy(table, stagedTable, true);
            string sdb = Directory.CreateDirectory(Path.Combine(stage, "Sdb")).FullName;
            File.Copy(DiscoveryProfiles.FindEnglishSdb(discovery), Path.Combine(sdb, "ENG.sdb"));
            string model = Directory.CreateDirectory(Path.Combine(stage, "Characters", "2 Cold Scorpio", "Default_Attire")).FullName;
            string originalModel = Directory.GetFiles(Path.Combine(discovery, "Characters", "1506_Flammer"), "*.mcd", SearchOption.AllDirectories).First();
            File.Copy(originalModel, Path.Combine(model, "attire.mcd"));
            var scanned = RosterImport.LoadFile(stagedTable); Equal(1458, scanned.Count);
            var unresolved = scanned.Single(e => e.Id == 0); Equal("2 Cold Scorpio", unresolved.Name); Equal("Choose roster slot", unresolved.Source);
            var chosen = new RosterEntry { Id = 4000, SourceId = 1506, Name = unresolved.Name, Folder = unresolved.Folder, RosterTable = stagedTable, IsMod = true, Source = "Manual selection" };
            var saved = Load("added-plain-choice.json", JsonSerializer.Serialize(new[] { chosen }));
            var session = RosterSession.Open(stage, saved); Equal(1459, session.Entries.Count);
            var added = session.Entries.Single(e => e.Id == 4000); Equal(unresolved.Folder, added.Folder); Equal(1506, added.SourceId);
            var plan = CharacterFolders.Plan(added.Folder, added); Equal("1506_2_Cold_Scorpio", plan.AliasName); Equal(stage, plan.DataRoot);
            Equal(true, File.ReadAllBytes(originalModel).SequenceEqual(File.ReadAllBytes(Path.Combine(model, "attire.mcd"))));
        });
    }

    private static void SdbChecks()
    {
        Test("SDB resolves Unicode display names and unsigned localization keys", () => {
            string path = Sdb("valid.sdb", uint.MaxValue, Encoding.UTF8.GetBytes("André the Giant"));
            Equal("André the Giant", DiscoveryProfiles.ReadStrings(path)[uint.MaxValue]);
        });
        Test("SDB rejects a truncated string index", () => {
            string path = Sdb("index.sdb", 1, Encoding.UTF8.GetBytes("Name"));
            byte[] bytes = File.ReadAllBytes(path); Array.Resize(ref bytes, 10); File.WriteAllBytes(path, bytes);
            Reject(() => DiscoveryProfiles.ReadStrings(path), "index is truncated");
        });
        Test("SDB rejects text offsets into the index and missing terminators", () => {
            string path = Sdb("offset.sdb", 1, Encoding.UTF8.GetBytes("Name"));
            byte[] bytes = File.ReadAllBytes(path); bytes[8] = 8; File.WriteAllBytes(path, bytes);
            Reject(() => DiscoveryProfiles.ReadStrings(path), "outside the text section");
            path = Sdb("terminator.sdb", 1, Encoding.UTF8.GetBytes("Name")); bytes = File.ReadAllBytes(path); bytes[bytes.Length - 1] = 1; File.WriteAllBytes(path, bytes);
            Reject(() => DiscoveryProfiles.ReadStrings(path), "terminator");
        });
        Test("SDB rejects malformed UTF-8 rather than displaying damaged names", () => {
            string path = Sdb("utf8.sdb", 1, new byte[] { 0xC3, 0x28 });
            Reject(() => DiscoveryProfiles.ReadStrings(path), "UTF-8");
        });
    }

    private static void SelectionChecks()
    {
        Test("Mapped native selection keeps the physical model and selects the logical roster slot", () => {
            string path = Directory.CreateDirectory(Path.Combine(root, "312_CM_PUNK")).FullName;
            var owner = new NativeWindow { Folder = path, FolderSlot = "312", TargetSlot = "312", WName = new NativeText { Text = "Physical model" } };
            owner.SlotNames.Add(("Old display name", 1481));
            FolderBridge.RegisterSelection(path, new RosterEntry { Id = 1481, SourceId = 312, Name = "CM PUNK" });
            FolderBridge.ApplySelection(owner);
            Equal("1481", owner.TargetSlot); Equal("312", owner.FolderSlot); Equal(path, owner.Folder); Equal("Physical model", owner.WName.Text);
            Equal("CM PUNK", owner.SlotNames.Single(e => e.Item2 == 1481).Item1); Equal(1, owner.TargetCalls); Equal(1, owner.SlotNames.Count);
            FolderBridge.ApplySelection(owner); Equal(1, owner.TargetCalls);
        });
        Test("Native selection refreshes an identical physical slot with the localized heading", () => {
            string path = Directory.CreateDirectory(Path.Combine(root, "211_Dax")).FullName;
            var owner = new NativeWindow { Folder = path, FolderSlot = "211", TargetSlot = "211" };
            FolderBridge.RegisterSelection(path, new RosterEntry { Id = 211, Name = "DAX HARWOOD" });
            FolderBridge.ApplySelection(owner); Equal(1, owner.TargetCalls); Equal("211", owner.TargetSlot); Equal("DAX HARWOOD", owner.SlotNames.Single().Item1); Equal("DAX HARWOOD", owner.WName.Text);
        });
        Test("An unrelated native load cannot consume a pending roster association", () => {
            string selected = Directory.CreateDirectory(Path.Combine(root, "selected-load")).FullName;
            string other = Directory.CreateDirectory(Path.Combine(root, "other-load")).FullName;
            var owner = new NativeWindow { Folder = other, FolderSlot = "312" };
            FolderBridge.RegisterSelection(selected, new RosterEntry { Id = 1481, Name = "CM PUNK" });
            FolderBridge.ApplySelection(null); FolderBridge.ApplySelection(owner); Equal(0, owner.TargetCalls); Equal(0, owner.SlotNames.Count);
            owner.Folder = selected; FolderBridge.ApplySelection(owner); Equal("1481", owner.TargetSlot); Equal(1, owner.TargetCalls);
        });
        Test("Native selection rejects invalid logical IDs before registering a load", () => {
            foreach (int id in new[] { 0, -1, 65536 }) Throws<ArgumentOutOfRangeException>(() => FolderBridge.RegisterSelection(root, new RosterEntry { Id = id, Name = "Invalid" }));
            Throws<ArgumentNullException>(() => FolderBridge.RegisterSelection(root, null));
        });
    }

    private sealed class NativeWindow
    {
        public string Folder;
        public string FolderSlot;
        public string TargetSlot;
        public NativeText WName = new NativeText { Text = "Original heading" };
        public List<(string, int)> SlotNames = new List<(string, int)>();
        public int TargetCalls;
        private void SetTarget(string id) { TargetSlot = id; TargetCalls++; }
    }

    private sealed class NativeText { public string Text { get; set; } }

    private static string Sdb(string name, uint key, byte[] text)
    {
        string path = Path.Combine(root, name);
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write((uint)0); output.Write((uint)1); output.Write((uint)20); output.Write((uint)text.Length); output.Write(key); output.Write(text); output.Write((byte)0);
        }
        return path;
    }

    private static string DiscoveryFolder(string name)
    {
        string path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(path, "Characters")); Profile(path); return path;
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
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
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

namespace GorillaStudio.NameFolders
{
    // SelectionBridge only needs the native field/method visibility constant from the
    // Windows UI partial; link its actual implementation into these portable checks.
    public static partial class FolderBridge
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    }
}
