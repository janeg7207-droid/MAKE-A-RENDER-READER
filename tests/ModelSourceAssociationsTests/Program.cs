using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

// Only the roster DTO is stubbed. Model indexing, persistence, source ownership
// and multi-pack attire enumeration all execute production code.
namespace GorillaStudio.NameFolders
{
    public sealed class RosterEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string RosterTable { get; set; }
        public string Folder { get; set; }
        public string Source { get; set; }
        public string StockFolder { get; set; }
        public int SourceId { get; set; }
    }
}

internal static class Program
{
    private static int checks;
    private static void Check(bool result, string message)
    { checks++; if (!result) throw new Exception(message); }
    private static void Throws(Action action, string message)
    { bool failed = false; try { action(); } catch (InvalidDataException) { failed = true; } Check(failed, message); }
    private static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "selected-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Routing fixtures with independent bodies; these bytes do not
            // represent game models or a visual render-quality test.
            string first = Pack(root, "Will ospreay all in26", 101, "all-in-body");
            string second = Pack(root, "WillOspreayDEATHRIDERS", 202, "deathriders-body");
            var entry = new RosterEntry { Id = 1811, Name = "WILL OSPREAY" };
            string roster = Path.Combine(root, "my-roster");
            var selected = ModelLibrary.Scan(first).Concat(ModelLibrary.Scan(second)).ToList();
            Check(selected.Count == 2 && selected[0].SourceId == 101 && selected[1].SourceId == 202, "two independent physical packs indexed");
            var records = ModelSourceAssociations.Replace(null, entry, roster, selected);
            var sources = ModelSourceAssociations.Sources(records, entry, roster);
            Check(sources.Count == 2, "both explicit roots retained for one logical wrestler");
            Check(entry.Id == 1811 && entry.Name == "WILL OSPREAY", "selection leaves logical identity unchanged");
            Check(sources[0].Folder == first && sources[1].Folder == second, "explicit source order retained");
            Check(sources[0].Name == selected[0].Name && sources[1].Name == selected[1].Name, "original model-pack names retained");
            Check(ModelSourceAssociations.IsAssociated(records, entry, roster, first), "first pack authorized");
            Check(ModelSourceAssociations.IsAssociated(records, entry, roster, second), "second pack authorized");
            Check(!ModelSourceAssociations.IsAssociated(records, entry, Path.Combine(root, "other-roster"), first), "no cross-roster association");
            Check(ModelSourceAssociations.Sources(records, new RosterEntry { Id = 1812, Name = entry.Name }, roster).Count == 0, "no cross-slot association");
            Check(ModelSourceAssociations.Sources(records, new RosterEntry { Id = 1811, Name = "OTHER PERSONA" }, roster).Count == 0, "changed slot name invalidates stale owner");
            Check(ModelSourceAssociations.Sources(null, entry, null).Count == 0, "legacy no-record/no-context path works");
            Check(ModelSourceAssociations.Sources(records, entry, null).Count == 0, "no ambient roster inference");
            sources[0].Folder = "changed-copy";
            Check(ModelSourceAssociations.Sources(records, entry, roster)[0].Folder == first, "fresh source snapshots are independent");
            var duplicates = ModelSourceAssociations.Replace(records, entry, roster, selected.Concat(selected));
            Check(ModelSourceAssociations.Sources(duplicates, entry, roster).Count == 2, "duplicate roots not duplicated");
            var other = new RosterEntry { Id = 19, Name = "OTHER WRESTLER" };
            var owners = ModelSourceAssociations.Replace(records, other, roster, selected.Take(1));
            var revoked = ModelSourceAssociations.Replace(owners, entry, roster, new ModelCandidate[0]);
            Check(ModelSourceAssociations.Sources(revoked, entry, roster).Count == 0, "empty explicit replacement revokes this owner");
            Check(ModelSourceAssociations.Sources(revoked, other, roster).Count == 1, "revocation leaves other owner intact");
            string file = Path.Combine(root, "settings", "selected-model-sources.json");
            ModelSourceAssociations.Save(file, records);
            ModelSourceAssociations.Save(file, owners);
            var roundtrip = ModelSourceAssociations.Load(file);
            Check(ModelSourceAssociations.Sources(roundtrip, entry, roster).Count == 2, "atomic saved choices roundtrip");
            Check(Directory.GetFiles(Path.GetDirectoryName(file)).Length == 1, "atomic replacement leaves no temporary files");
            Check(ModelSourceAssociations.Load(file + ".missing").Count == 0, "missing preferences are empty");
            var outfits = ModelSourceAttires.Read(roundtrip, entry, roster, first, first);
            Check(outfits.Count == 4, "two packs expose all four local outfits");
            Check(outfits.Count(item => item.SourceFolder == first) == 2 && outfits.Count(item => item.SourceFolder == second) == 2, "outfits preserve whole source identity");
            Check(outfits.Where(item => item.SourceFolder == first).All(item => item.RelativePath != null), "current source relative paths are selectable");
            Check(outfits.Where(item => item.SourceFolder == second).All(item => item.RelativePath == null), "remote paths require a whole-source switch");
            Check(outfits.All(item => item.DiscoveryKey == null && item.DiscoveryModelHash == 0 && item.ExplicitSourceKey != null), "explicit selections never invent Discovery mappings");
            Check(outfits.Any(item => item.DisplayName.Contains("Will ospreay all in26")) && outfits.Any(item => item.DisplayName.Contains("WillOspreayDEATHRIDERS")), "attire labels identify each selected pack");
            Check(outfits.Select(item => item.ExplicitSourceKey).Distinct().Count() == 4, "same attire names across packs keep separate identities");
            string alias = Path.Combine(root, "alias"); Directory.CreateDirectory(alias);
            AlternateAttires.RememberWrappedSource(alias, first);
            var wrapped = ModelSourceAttires.Read(records, entry, roster, first, alias);
            Check(wrapped.Where(item => item.SourceFolder == first).All(item => item.RelativePath.StartsWith("Default_Attire/")), "factory wrapper translated only for current pack");
            Check(wrapped.Where(item => item.SourceFolder == second).All(item => item.RelativePath == null), "remote pack never grafted under current alias");
            var tableEntry = new RosterEntry { Id = 1811, Name = entry.Name, RosterTable = Path.Combine(root, "Data", "Roster", "CharProfileTable.jsfb") };
            var tableRecords = ModelSourceAssociations.Replace(records, tableEntry, roster, selected.Take(1));
            Check(ModelSourceAssociations.Sources(tableRecords, tableEntry, "ignored-fallback").Count == 1, "explicit table context has priority over fallback");
            Check(ModelSourceAssociations.Sources(tableRecords, entry, roster).Count == 2, "table and plain-roster owners remain independent");
            string unnumbered = Path.Combine(root, "Unnumbered model pack");
            Directory.CreateDirectory(Path.Combine(unnumbered, "BaseModel"));
            Directory.CreateDirectory(Path.Combine(unnumbered, "Default_Attire"));
            File.WriteAllText(Path.Combine(unnumbered, "BaseModel", "baseModel.mcd"), "independent unnumbered body");
            File.WriteAllText(Path.Combine(unnumbered, "Default_Attire", "Attire.mcd"), "unnumbered clothes");
            var noNumbers = ModelLibrary.Scan(unnumbered);
            Check(noNumbers.Count == 1 && noNumbers[0].SourceId == 0, "plain complete pack needs no numeric folder or filename");
            var unnumberedRecords = ModelSourceAssociations.Replace(records, entry, roster, noNumbers.Concat(selected.Take(1)));
            Check(ModelSourceAssociations.Sources(unnumberedRecords, entry, roster).Count == 2
                && ModelSourceAssociations.Sources(unnumberedRecords, entry, roster)[0].SourceId == 0, "explicit unnumbered pack retained without invented physical ID");
            Check(ModelSourceAttires.Read(unnumberedRecords, entry, roster).Any(item => item.SourceFolder == unnumbered
                && item.SourceRelativePath == "Default_Attire/Attire.mcd"), "unnumbered selected pack exposes its existing attire");
            Throws(() => ModelSourceAssociations.Replace(records, entry, roster, new[] {
                new ModelCandidate { Folder = unnumbered, SourceId = 500 } }), "unproven physical ID cannot be injected into plain pack association");
            Throws(() => ModelSourceAssociations.Replace(records, entry, roster, new[] { new ModelCandidate { Folder = root } }), "collection root cannot become a character pack");
            Directory.Move(second, second + ".missing");
            Throws(() => ModelSourceAssociations.Sources(records, entry, roster), "missing second pack blocks silent two-to-one reduction");
            Directory.Move(second + ".missing", second);
            Check(ModelSourceAssociations.Sources(records, entry, roster).Count == 2, "restored source revalidates");
            string link = Path.Combine(root, "linked-pack");
            Directory.CreateSymbolicLink(link, first);
            Throws(() => ModelSourceAssociations.Replace(records, entry, roster, new[] { new ModelCandidate { Folder = link } }), "linked root cannot bypass physical source validation");
            Check(File.ReadAllText(Path.Combine(first, "BaseModel", "baseModel.mcd")) == "all-in-body", "first independent body unchanged");
            Check(File.ReadAllText(Path.Combine(second, "BaseModel", "baseModel.mcd")) == "deathriders-body", "second independent body unchanged");
            Console.WriteLine("PASS " + checks + " selected-model association and main-attire checks (routing fixtures; no visual rendering)");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Pack(string root, string name, int id, string body)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(folder, "BaseModel"));
        File.WriteAllText(Path.Combine(folder, "BaseModel", "baseModel.mcd"), body);
        Directory.CreateDirectory(Path.Combine(folder, id + "_Default_Attire"));
        File.WriteAllText(Path.Combine(folder, id + "_Default_Attire", id + "_Attire.mcd"), "default routing fixture");
        Directory.CreateDirectory(Path.Combine(folder, id + "_Attire_2"));
        File.WriteAllText(Path.Combine(folder, id + "_Attire_2", id + "_Attire.mcd"), "alternate routing fixture");
        return folder;
    }
}
