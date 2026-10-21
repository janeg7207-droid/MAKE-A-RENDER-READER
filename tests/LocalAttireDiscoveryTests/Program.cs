using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static async Task Main(string[] args)
    {
        string actual = args.Length > 0 ? Path.GetFullPath(args[0]) : "/workspace/discovery-work/discovery_data";
        string table = Path.Combine(actual, "Roster", "CharProfileTable.jsfb");
        string sourceModel = Path.Combine(actual, "Characters", "312_CM_Punk", "312_Default_Attire", "312_Attire.mcd");
        if (!File.Exists(table) || !File.Exists(sourceModel)) throw new FileNotFoundException("Pass the actual uploaded Discovery data folder.");
        string originalModelHash = Sha(sourceModel), originalTableHash = Sha(table);
        string temporary = Path.Combine(Path.GetTempPath(), "gorilla-local-attire-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var profiles = DiscoveryProfiles.Read(table);
            var cm = DiscoveryAttireCatalog.ReadReferences(table, 312);
            var custom = DiscoveryAttireCatalog.ReadReferences(table, 1481);
            var jeff = DiscoveryAttireCatalog.ReadReferences(table, 695);
            var otherJeff = DiscoveryAttireCatalog.ReadReferences(table, 539);
            Equal("Real CM Punk has eleven active attire references", 11, cm.Count);
            Equal("Real CM Punk has ten usable nonzero model mappings", 10, cm.Count(item => item.ModelHash != 0 && item.Issue == null));
            Equal("Real CM Punk's Discovery default is PINK APRIL", "PINK APRIL", cm.Single(item => item.IsDefault).DisplayName);
            Equal("Real CM Punk's default uses costume index nine", 9, cm.Single(item => item.IsDefault).CostumeIndex);
            Equal("Real CM Punk's incomplete SUMMERSLAM mapping stays unresolved", true, cm.Single(item => item.DisplayName == "SUMMERSLAM '26").Issue != null);
            Equal("Custom CM Punk is a separate logical profile despite its shared body", 312, profiles.Single(item => item.Id == 1481).BaseId);
            Equal("Custom CM Punk lists just one active attire", 1, custom.Count);
            Equal("Custom CM Punk retains its logical owner", 1481, custom.Single().LogicalId);
            Equal("Custom CM Punk maps the uploaded physical 312 MCD", 0x763099738CF25FE8UL, custom.Single().ModelHash);
            Equal("The same physical folder does not merge stock CM Punk's active mappings", false, cm.Any(item => item.ModelHash == custom.Single().ModelHash));
            Equal("Jeff Hardy '02-'03 is the real logical 695 profile", "JEFF HARDY '02-'03", profiles.Single(item => item.Id == 695).Name);
            Equal("Real Jeff Hardy '02-'03 has eleven active references", 11, jeff.Count);
            Equal("Real Jeff Hardy '02-'03 has ten usable nonzero mappings", 10, jeff.Count(item => item.ModelHash != 0 && item.Issue == null));
            Equal("Jeff Hardy's Discovery default is VS TAKER", "VS TAKER", jeff.Single(item => item.IsDefault).DisplayName);
            Equal("Jeff Hardy's explicit default uses costume index one", 1, jeff.Single(item => item.IsDefault).CostumeIndex);
            Equal("Jeff Hardy's incomplete RED MESH mapping stays unresolved", true, jeff.Single(item => item.DisplayName == "RED MESH").Issue != null);
            Equal("Jeff Hardy '01 remains its separate nine-reference profile", 9, otherJeff.Count);
            var sharedJeff = jeff.Single(item => item.DisplayName == "BLONDE PINK");
            var originalJeff = otherJeff.Single(item => item.DisplayName == "BLONDE PINK");
            Equal("Shared attire labels can use the same key across different Jeff personas", originalJeff.CostumeKey, sharedJeff.CostumeKey);
            Equal("Shared keys do not replace the exact Jeff persona's model hash", false, originalJeff.ModelHash == sharedJeff.ModelHash);
            Equal("Jeff '02 retains its own costume index", 2, sharedJeff.CostumeIndex);
            Equal("Jeff '01 retains its different costume index", 0, originalJeff.CostumeIndex);

            foreach (string relative in new[] { "Roster/CharProfileTable.jsfb", "Characters/CharacterMapping.jsfb", "Characters/CostumesTable.jsfb", "Sdb/ENG.sdb" })
                Copy(Path.Combine(actual, relative.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(temporary, relative.Replace('/', Path.DirectorySeparatorChar)));
            string character = Path.Combine(temporary, "Characters", "312_CM_Punk");
            var expected = new List<string> { "312_Default_Attire/312_Attire.mcd" };
            for (int i = 1; i <= 10; i++) expected.Add("Alternate " + i.ToString("00") + "/Attire.mcd");
            // The actual uploaded CM Punk model is copied into separate attire
            // folders to exercise discovery and selection. These are routing
            // fixtures; they do not claim to be eleven different pieces of art.
            foreach (string relative in expected) Copy(sourceModel, Path.Combine(character, relative.Replace('/', Path.DirectorySeparatorChar)));
            string fixtureTable = Path.Combine(temporary, "Roster", "CharProfileTable.jsfb");
            var entry = new RosterEntry { Id = 1481, Name = "CM PUNK", SourceId = 312, Folder = character, RosterTable = fixtureTable };
            var inventory = new[] { new ModelCandidate { Folder = character, SourceId = 312, Name = "CM Punk" } };
            var found = DiscoveryAttireCatalog.Find(entry, inventory, fixtureTable);
            Equal("Copied metadata preserves custom CM Punk's one active reference", 1, found.References.Count);
            Equal("The exact default MCD matches its real hash in the copied source", 1, found.Sources.Count);
            Equal("The exact mapped default keeps the custom logical ID", 1481, found.Sources.Single().LogicalId);
            Equal("The source retains physical model 312", 312, found.Sources.Single().SourceId);
            Equal("Local extra folders never fabricate new active costume references", "99163620", found.References.Single().CostumeKey.ToString("X8"));
            var mapped = found.Sources.Select(item => new AlternateAttire {
                SourceFolder = item.SourceFolder, SourceRelativePath = item.RelativePath,
                RelativePath = item.RelativePath, DisplayName = item.DisplayName,
                IsDefault = item.IsDefault, DiscoveryKey = item.LogicalId + ":" + item.CostumeKey.ToString("X8")
            }).ToList();
            var local = AlternateAttires.Read(character);
            Equal("All eleven physical attire folders are discovered", 11, local.Count);
            Equal("All eleven real MCD copies retain their complete bytes", true, expected.All(relative => new FileInfo(Path.Combine(character, relative.Replace('/', Path.DirectorySeparatorChar))).Length == new FileInfo(sourceModel).Length));
            var choices = DiscoveryAttireOptions.Combine(character, character, local, mapped, true);
            Equal("An authoritative profile no longer hides the ten extra local folders", 11, choices.Count);
            Equal("Mapped and local copies of the same default appear once", 1, choices.Count(item => Relative(item) == expected[0]));
            Equal("Every extra local MCD has one selectable choice", 11, choices.Select(Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Equal("The exact mapped default remains a Discovery choice", 1, choices.Count(item => item.DiscoveryKey != null));
            Equal("Local extras do not override the mapped default", 1, choices.Count(item => item.IsDefault));
            Equal("The remaining ten options are explicitly local choices", 10, choices.Count(item => item.DiscoveryKey == null && item.DisplayName.IndexOf("local", StringComparison.OrdinalIgnoreCase) >= 0));
            foreach (AlternateAttire choice in choices)
            {
                string relative = Relative(choice);
                Equal("Selectable relative MCD remains under this character: " + relative, relative, AlternateAttires.Validate(character, relative));
                Equal("Each option is a validated choice from this physical source: " + relative, true, DiscoveryAttireOptions.IsLocalChoice(character, character, relative));
                string[] render = AlternateAttires.Apply("gorilla_render.py", new[] { character, "roman3", Path.Combine(temporary, "output"), "slot=1481", "screens=both", "quick=1" }, relative);
                Equal("Selecting local attire preserves the physical source root: " + relative, character, render[0]);
                Equal("Selecting local attire preserves the logical install slot: " + relative, true, render.Contains("slot=1481"));
                Equal("The exact local MCD is passed to the renderer: " + relative, "attire=" + relative, render.Last());
            }
            var stock = DiscoveryAttireCatalog.Find(new RosterEntry { Id = 312, RosterTable = fixtureTable, Folder = character }, inventory, fixtureTable);
            Equal("Copied physical folder does not falsely satisfy the stock CM Punk hashes", 0, stock.Sources.Count);
            Equal("Stock CM Punk still retains all eleven active references", 11, stock.References.Count);
            var manual = DiscoveryAttireOptions.Combine(character, character, local, new AlternateAttire[0], true);
            Equal("A profile with no available mapped asset can still choose its eleven local models", 11, manual.Count);
            Equal("Manual local models do not claim a missing Discovery costume identity", false, manual.Any(item => item.DiscoveryKey != null));
            Equal("The native source default is the only fallback when no mapped default exists", expected[0], manual.Single(item => item.IsDefault).RelativePath);
            var listed = DiscoveryAttireDisplay.Merge(manual, stock);
            Equal("Logical 312 lists all eleven named active costumes and all eleven valid local files", 22, listed.Count);
            Equal("Missing active mappings stay visible without claiming their models exist", 11, listed.Count(item => !item.IsAvailable));
            Equal("All eleven copied local model files remain available", 11, listed.Count(item => item.IsAvailable));
            Equal("Every missing costume retains its exact Discovery tuple", true, stock.References.All(reference => listed.Any(item => item.DiscoveryKey == DiscoveryAttireDisplay.ReferenceKey(reference))));
            Equal("PINK APRIL remains visible as the unavailable Discovery default", true, listed.Any(item => !item.IsAvailable && item.IsDefault && item.DisplayName.StartsWith("PINK APRIL")));
            Equal("The zero-hash SUMMERSLAM mapping remains unavailable with an explanation", true, listed.Any(item => !item.IsAvailable && item.DiscoveryModelHash == 0 && item.DisplayName.StartsWith("SUMMERSLAM '26") && !string.IsNullOrWhiteSpace(item.UnavailableReason)));
            Equal("Unavailable rows cannot invent a render path", false, listed.Where(item => !item.IsAvailable).Any(item => item.RelativePath != null || item.SourceFolder != null));
            var correctlyKeyed = mapped.Select(item => new AlternateAttire { SourceFolder = item.SourceFolder,
                SourceRelativePath = item.SourceRelativePath, RelativePath = item.RelativePath, DisplayName = item.DisplayName,
                DiscoveryKey = DiscoveryAttireDisplay.ReferenceKey(found.Sources.Single()), IsDefault = item.IsDefault }).ToList();
            Equal("A genuinely available custom mapping never gets a duplicate missing row", 11,
                DiscoveryAttireDisplay.Merge(DiscoveryAttireOptions.Combine(character, character, local, correctlyKeyed, true), found).Count);
            string selectedChild = Path.Combine(character, "312_Default_Attire");
            Equal("A selected Default_Attire child resolves to the complete proven 312 character", character, AutomaticAttire.CharacterRoot(selectedChild, 312));
            Equal("Loading the proven character root reveals all eleven sibling attires", 11,
                AlternateAttires.Read(AutomaticAttire.CharacterRoot(selectedChild, 312)).Count);
            Equal("An unrelated logical owner cannot broaden that child source", selectedChild, AutomaticAttire.CharacterRoot(selectedChild, 1481));

            var cache = new AlternateAttireCatalog();
            var before = await cache.ReadAsync(character, false);
            Equal("The background attire reader initially sees eleven models", 11, before.Models.Count);
            string added = "Added Later/Attire.mcd";
            Copy(sourceModel, Path.Combine(character, added.Replace('/', Path.DirectorySeparatorChar)));
            var refreshed = await cache.ReadAsync(character, true);
            Equal("Refresh discovers a newly added twelfth attire folder", 12, refreshed.Models.Count);
            var after = DiscoveryAttireOptions.Combine(character, character, refreshed.Models, mapped, true);
            Equal("The new local model becomes selectable alongside all older ones", 12, after.Count);
            Equal("Refresh preserves the one real mapped default", 1, after.Count(item => item.IsDefault && item.DiscoveryKey != null));
            Equal("Refresh does not change the selected logical profile", 1481, entry.Id);
            Equal("Refresh does not change the selected physical source", character, entry.Folder);
            cache.Close();
            string deep = "Imported Pack/Costumes/Season/Version/Model/Blue/Attire.mcd";
            Copy(sourceModel, Path.Combine(character, deep.Replace('/', Path.DirectorySeparatorChar)));
            Equal("Nested attire packs deeper than four folders are not silently omitted", true,
                AlternateAttires.Read(character).Any(item => item.RelativePath == deep));
            var oldInventory = DiscoveryAttireInventory.Refresh(new ModelCandidate[0], new[] { Path.Combine(temporary, "Characters") });
            string external = Path.Combine(temporary, "remembered-mod-library");
            Directory.CreateDirectory(external);
            var beforeExternal = DiscoveryAttireInventory.Refresh(oldInventory, new[] { external });
            string externalModel = Path.Combine(external, "Separate New Pack", "Characters", "312_New_Attire", "Default_Attire", "Attire.mcd");
            Copy(sourceModel, externalModel);
            var afterExternal = DiscoveryAttireInventory.Refresh(beforeExternal, new[] { external });
            Equal("A remembered collection root discovers a newly added separate character source", true,
                afterExternal.Any(item => item.Folder == Path.GetDirectoryName(Path.GetDirectoryName(externalModel))));
            Equal("The new source was absent from the old candidate snapshot", false,
                beforeExternal.Any(item => item.Folder == Path.GetDirectoryName(Path.GetDirectoryName(externalModel))));
            Equal("Inventory refresh never changes the selected logical wrestler", 1481, entry.Id);
            string separate = Path.Combine(temporary, "separate-mapped-locations");
            Directory.CreateDirectory(separate);
            var oldMappedInventory = DiscoveryAttireInventory.Refresh(inventory, new[] { separate });
            // These virtual paths were independently joined from the original
            // stock_index.json to the uploaded profile's exact model hashes.
            // Copies exercise routing only; no different outfit art is claimed.
            string[] knownPaths = {
                "312_CM_Punk/312_Mutiny_Faction/312_Attire.mcd",
                "312_CM_Punk/AJ_Lee_Husband/Attire.mcd",
                "312_CM_Punk/Bad_Blood_2024/Attire.mcd",
                "312_CM_Punk/Raw_2026/Attire.mcd",
                "312_CM_Punk/Raw_Netflix_2025/Attire.mcd",
                "312_CM_Punk/WM41_2025/Attire.mcd",
                "312_CM_Punk/WarGames_2025/Attire.mcd"
            };
            for (int index = 0; index < knownPaths.Length; index++)
            {
                string relative = knownPaths[index];
                Equal("Every separated path is an exact active logical-312 hash: " + relative, true,
                    cm.Any(reference => reference.ModelHash == CharacterMappingReader.ModelPathHash("characters/" + relative)));
                Copy(sourceModel, Path.Combine(separate, "Pack " + index, "Characters", relative.Replace('/', Path.DirectorySeparatorChar)));
            }
            var selected312 = new RosterEntry { Id = 312, Name = "CM PUNK", SourceId = 312, Folder = character, RosterTable = fixtureTable };
            Equal("An old source snapshot cannot see newly added separate model packs", 0,
                DiscoveryAttireCatalog.Find(selected312, oldMappedInventory, fixtureTable).Sources.Count);
            var refreshedInventory = DiscoveryAttireInventory.Refresh(oldMappedInventory, new[] { separate });
            var separatedCatalog = DiscoveryAttireCatalog.Find(selected312, refreshedInventory, fixtureTable);
            Equal("Refreshing remembered locations resolves seven exact active CM Punk mappings in separate packs", 7, separatedCatalog.Sources.Count);
            Equal("Each resolved active attire retains its distinct complete source root", 7,
                separatedCatalog.Sources.Select(source => source.SourceFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Equal("Cross-root sources never switch CM Punk to custom logical slot 1481", true,
                separatedCatalog.Sources.All(source => source.LogicalId == 312 && source.MappingId == 312));
            Equal("The unavailable PINK APRIL default is not replaced with another mapped outfit", false,
                separatedCatalog.Sources.Any(source => source.IsDefault));
            Equal("An explicitly chosen local source survives availability of other mapped roots", null,
                DiscoveryAttireDisplay.DefaultSource(separatedCatalog, character, true));
            bool refusedInferred = false;
            try { DiscoveryAttireDisplay.DefaultSource(separatedCatalog, character, false); }
            catch (InvalidDataException) { refusedInferred = true; }
            Equal("An inferred unrelated source cannot silently replace a missing active default", true, refusedInferred);
            Equal("A selected exact mapped source remains selected without a default switch", null,
                DiscoveryAttireDisplay.DefaultSource(separatedCatalog, separatedCatalog.Sources[0].SourceFolder, false));
            Equal("A validated local default remains selectable without a fake Discovery key", expected[0],
                DiscoveryAttireDisplay.LocalDefault(character, character, local).RelativePath);
            var separatedChoices = separatedCatalog.Sources.Select(source => new AlternateAttire {
                SourceFolder = source.SourceFolder, SourceRelativePath = source.RelativePath,
                DiscoveryKey = DiscoveryAttireDisplay.ReferenceKey(source), DiscoveryModelHash = source.ModelHash,
                IsDefault = source.IsDefault, DisplayName = source.DisplayName
            }).ToList();
            var allSeparated = DiscoveryAttireDisplay.Merge(separatedChoices, separatedCatalog);
            Equal("All eleven active Discovery names remain shown across separate packs", 11, allSeparated.Count);
            Equal("Only the seven copied, hash-proven cross-root models are selectable", 7, allSeparated.Count(item => item.IsAvailable));
            Equal("Three absent nonzero assets and the zero-hash mapping remain visibly unavailable", 4, allSeparated.Count(item => !item.IsAvailable));
            Equal("Rebuilding the display does not duplicate missing active references", 11,
                DiscoveryAttireDisplay.Merge(allSeparated, separatedCatalog).Count);
            foreach (DiscoveryAttireSource source in separatedCatalog.Sources)
            {
                Equal("Cross-root selection validates inside its own complete model root: " + source.DisplayName,
                    source.RelativePath, AlternateAttires.Validate(source.SourceFolder, source.RelativePath));
                string[] render = AlternateAttires.Apply("gorilla_render.py", new[] { source.SourceFolder, "roman3",
                    Path.Combine(temporary, "output"), "slot=312", "screens=both" }, source.RelativePath);
                Equal("Cross-root rendering preserves logical 312: " + source.DisplayName, true, render.Contains("slot=312"));
                Equal("Cross-root rendering does not splice the MCD into another source: " + source.DisplayName, source.SourceFolder, render[0]);
            }
            Equal("A different wrestler's missing relative path cannot be selected locally", false,
                DiscoveryAttireOptions.IsLocalChoice(character, character, "../OtherWrestler/Attire.mcd"));
            Equal("A removed local model cannot be rendered silently as the default", false,
                DiscoveryAttireOptions.IsLocalChoice(character, character, "Removed Attire/Attire.mcd"));

            string alias = Path.Combine(temporary, "compatibility-alias", "Characters", "312_CM_Punk");
            Directory.CreateDirectory(alias);
            Directory.CreateSymbolicLink(Path.Combine(alias, "Default_Attire"), character);
            AlternateAttires.RememberWrappedSource(alias, character);
            var wrapped = AlternateAttires.Read(alias);
            var wrappedMapped = mapped.Select(item => new AlternateAttire {
                SourceFolder = item.SourceFolder, SourceRelativePath = item.SourceRelativePath,
                RelativePath = AlternateAttires.AliasRelativePath(alias, item.SourceRelativePath),
                DisplayName = item.DisplayName, DiscoveryKey = item.DiscoveryKey, IsDefault = item.IsDefault
            }).ToList();
            var wrappedChoices = DiscoveryAttireOptions.Combine(alias, character, wrapped, wrappedMapped, true);
            Equal("Compatibility aliases retain every source-local attire", 13, wrappedChoices.Count);
            Equal("Compatibility wrappers do not duplicate the mapped default", 1, wrappedChoices.Count(item => item.DiscoveryKey != null));
            string wrappedAdded = AlternateAttires.AliasRelativePath(alias, added);
            Equal("A wrapped local choice remains selectable from its original source", true,
                DiscoveryAttireOptions.IsLocalChoice(alias, character, wrappedAdded));
            Equal("The wrapped renderer argument retains its required alias prefix", "Default_Attire/" + added,
                DiscoveryAttireOptions.ValidateLocalSelection(alias, character, wrappedAdded));
            string[] wrappedRender = AlternateAttires.Apply("gorilla_render.py", new[] { alias, "roman3", Path.Combine(temporary, "output"), "slot=1481" }, wrappedAdded);
            Equal("A wrapped local choice retains its logical installation ID", true, wrappedRender.Contains("slot=1481"));
            Equal("A wrapped local choice retains the complete body-bearing model root", alias, wrappedRender[0]);
            Directory.Delete(Path.Combine(alias, "Default_Attire"));
            Equal("The uploaded source model remains unchanged", originalModelHash, Sha(sourceModel));
            Equal("The uploaded source profile table remains unchanged", originalTableHash, Sha(table));
            Console.WriteLine("PASS " + passed + " local Discovery attire checks");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static string Relative(AlternateAttire item) { return item.SourceRelativePath ?? item.RelativePath; }
    private static void Copy(string source, string target)
    { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, true); }
    private static string Sha(string path)
    { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)); }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++;
    }
}
