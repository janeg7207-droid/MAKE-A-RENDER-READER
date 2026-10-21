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
            Equal("Compatibility aliases retain every source-local attire", 12, wrappedChoices.Count);
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
