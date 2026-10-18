using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;

    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-model-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Test("A missing PAC profile binds its extracted plain-name model and keeps the roster slot", () => {
                string library = Collection("pac-name"); string model = Model(library, "PAC"); Model(library, "215_Neville");
                var entry = Profile(204, "PAC", 215); var entries = new List<RosterEntry> { entry };
                var result = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), Table("PAC"));
                Equal(1, result.BoundCount); Equal(model, entry.Folder); Equal(204, entry.Id); Equal("PAC", entry.Name); Equal(215, entry.SourceId);
                Equal("Found in models folder", entry.Source); Equal(true, entry.IsMod);
                Equal(1, result.AddedCount); Equal(0, entries.Single(e => e.Name == "Neville").Id);
            });
            Test("A profile base ID cannot silently bind a replacement mod to the stock wrestler", () => {
                string library = Collection("stock-replacement"); string stock = Model(library, "215_Neville");
                var entry = Profile(204, "PAC", 215); var entries = new List<RosterEntry> { entry };
                var result = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), Table("PAC"));
                Equal(0, result.BoundCount); Equal((string)null, entry.Folder); Equal("PAC", entry.Name); Equal(204, entry.Id); Equal("Profile", entry.Source);
                Equal(stock, result.Unassigned.Single().Folder); Equal(0, entries.Single(e => e.Folder == stock).Id);
            });
            Test("Rescanning after a plain-name mod disappears cannot promote its profile base ID into a stock match", () => {
                string first = Collection("pac-before-move"); string original = Model(first, "PAC");
                var entry = Profile(204, "PAC", 215); var entries = new List<RosterEntry> { entry };
                Equal(1, ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(first), Table("PAC")).BoundCount);
                Equal(215, entry.SourceId); Equal("Found in models folder", entry.Source);
                Directory.Delete(original, true);
                string next = Collection("pac-after-move"); Model(next, "215_Neville");
                var unmatched = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(next), Table("PAC"));
                Equal(0, unmatched.BoundCount); Equal(original, entry.Folder); Equal(204, entry.Id); Equal("PAC", entry.Name);
                string repaired = Model(next, "PAC");
                Equal(1, ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(next), Table("PAC")).BoundCount);
                Equal(repaired, entry.Folder); Equal(215, entry.SourceId);
            });
            Test("A moved manually chosen plain-name model cannot fall back to an inherited stock ID", () => {
                string library = Collection("chosen-model-rescan"); Model(library, "215_Neville");
                foreach (string source in new[] { "Chosen model folder", "Chosen folder" })
                {
                    string original = Model(Collection(source.Replace(' ', '-')), "PAC");
                    var entry = Profile(204, "PAC", 215); entry.Folder = original; entry.Source = source;
                    Directory.Delete(original, true);
                    var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, ModelLibrary.Scan(library), Table("PAC"), false);
                    Equal(0, result.BoundCount); Equal(original, entry.Folder); Equal(204, entry.Id); Equal("PAC", entry.Name); Equal(215, entry.SourceId); Equal(source, entry.Source);
                }
            });
            Test("A verified stock mapping supplies the physical ID instead of the profile base ID", () => {
                string library = Collection("verified-stock"); string model = Model(library, "215_Neville");
                var entry = Profile(204, "Custom Display Name", 204); entry.StockFolder = "215_Neville";
                var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, ModelLibrary.Scan(library), Table("PAC"));
                Equal(1, result.BoundCount); Equal(model, entry.Folder); Equal(215, entry.SourceId); Equal(204, entry.Id); Equal("Custom Display Name", entry.Name);
            });
            Test("An exact custom name takes priority over a verified stock-body fallback", () => {
                string library = Collection("name-priority"); string pac = Model(library, "368_PAC"); Model(library, "215_Neville");
                var entry = Profile(204, "PAC", 215); entry.StockFolder = "215_Neville";
                var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, ModelLibrary.Scan(library), Table("PAC"), false);
                Equal(1, result.BoundCount); Equal(pac, entry.Folder); Equal(368, entry.SourceId); Equal(204, entry.Id);
            });
            Test("A plain AJ folder matches punctuation differences without adding an ID prefix", () => {
                string library = Collection("aj-plain"); string model = Model(library, "AJ_Styles");
                var entry = Profile(100, "A.J. Styles", 0); var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, ModelLibrary.Scan(library), Table("AJ"));
                Equal(1, result.BoundCount); Equal(model, entry.Folder); Equal(100, entry.Id); Equal("A.J. Styles", entry.Name); Equal(0, entry.SourceId);
            });
            Test("Two distinct PAC models require a choice and both remain visible without roster IDs", () => {
                string library = Collection("duplicate-pac"); Model(library, "First/PAC"); Model(library, "Second/PAC");
                var entry = Profile(204, "PAC", 215); var entries = new List<RosterEntry> { entry };
                var result = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), Table("PAC"));
                Equal(0, result.BoundCount); Equal((string)null, entry.Folder); Equal(2, result.Choices[entry].Count);
                Equal(2, result.AddedCount); Equal(3, entries.Count); Equal(2, entries.Count(e => e.Id == 0));
                var again = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), Table("PAC"));
                Equal(0, again.AddedCount); Equal(2, again.Choices[entry].Count); Equal(3, entries.Count);
            });
            Test("A valid existing model association stays authoritative", () => {
                string current = Model(Collection("authoritative-current"), "312_PAC");
                string library = Collection("authoritative-new"); string replacement = Model(library, "368_PAC");
                var entry = new RosterEntry { Id = 204, Name = "PAC", Folder = current, SourceId = 312, Source = "Explicit selection", IsMod = false };
                var entries = new List<RosterEntry> { entry }; var result = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), Table("PAC"));
                Equal(0, result.BoundCount); Equal(current, entry.Folder); Equal(312, entry.SourceId); Equal("Explicit selection", entry.Source); Equal(false, entry.IsMod);
                Equal(replacement, entries.Single(e => e.Id == 0).Folder);
            });
            Test("A moved library can repair a stale folder using a unique exact name", () => {
                string library = Collection("moved-library"); string model = Model(library, "368_PAC");
                var entry = Profile(204, "PAC", 215); entry.Folder = Path.Combine(root, "old-library", "PAC");
                var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, ModelLibrary.Scan(library), Table("PAC"));
                Equal(1, result.BoundCount); Equal(model, entry.Folder); Equal(368, entry.SourceId); Equal(204, entry.Id);
            });
            Test("Duplicate spellings of one folder do not create an ambiguous match", () => {
                string folder = Model(Collection("dedup-folder"), "368_PAC");
                var candidates = new[] {
                    new ModelCandidate { Folder = folder, Name = "PAC", SourceId = 368 },
                    new ModelCandidate { Folder = folder + Path.DirectorySeparatorChar, Name = "PAC", SourceId = 368 },
                    new ModelCandidate { Folder = Path.Combine(folder, "."), Name = "PAC", SourceId = 368 }
                };
                var entry = Profile(204, "PAC", 215); var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, candidates, Table("PAC"));
                Equal(1, result.BoundCount); Equal(0, result.Choices.Count); Equal(0, result.AddedCount); Equal(folder, entry.Folder);
            });
            Test("An unmatched extracted wrestler remains visible for an explicit roster-slot choice", () => {
                string library = Collection("unassigned"); string folder = Model(library, "2 Cold Scorpio"); string table = Table("Unknown");
                var entries = new List<RosterEntry>(); var result = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), table);
                Equal(0, result.BoundCount); Equal(1, result.AddedCount); var entry = entries.Single();
                Equal(0, entry.Id); Equal(0, entry.SourceId); Equal("2 Cold Scorpio", entry.Name); Equal(folder, entry.Folder); Equal(table, entry.RosterTable);
                Equal("Choose roster slot", entry.Source); Equal(true, entry.IsMod);
                var again = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), table); Equal(0, again.BoundCount); Equal(0, again.AddedCount); Equal(0, entry.Id);
            });
            Test("A folder already represented by a known roster row is not added again", () => {
                string library = Collection("represented"); string folder = Model(library, "368_PAC");
                var entry = new RosterEntry { Id = 204, Name = "PAC", Folder = folder, SourceId = 368 };
                var entries = new List<RosterEntry> { entry }; var result = ModelLibraryBinding.Apply(entries, ModelLibrary.Scan(library), Table("PAC"));
                Equal(0, result.BoundCount); Equal(0, result.AddedCount); Equal(0, result.Unassigned.Count); Equal(1, entries.Count);
            });
            Test("The caller can defer adding unassigned models without losing the choices", () => {
                string library = Collection("deferred"); Model(library, "Unknown Wrestler"); var candidates = ModelLibrary.Scan(library);
                var entries = new List<RosterEntry>(); var result = ModelLibraryBinding.Apply(entries, candidates, Table("Unknown"), false);
                Equal(0, result.AddedCount); Equal(0, entries.Count); Equal(1, result.Unassigned.Count);
                Equal(1, ModelLibraryBinding.AddUnassigned(entries, result.Unassigned, Table("Unknown")));
                Equal(0, ModelLibraryBinding.AddUnassigned(entries, candidates, Table("Unknown"))); Equal(1, entries.Count);
            });
            Test("Models from the current library do not overwrite another explicit roster context", () => {
                string library = Collection("other-context"); string model = Model(library, "368_PAC");
                var entry = Profile(204, "PAC", 215); entry.RosterTable = Table("Other");
                var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, ModelLibrary.Scan(library), Table("Current"), false);
                Equal(0, result.BoundCount); Equal((string)null, entry.Folder); Equal(Table("Other"), entry.RosterTable); Equal(model, result.Unassigned.Single().Folder);
            });
            Test("Folders without MCD models never become candidates or automatic bindings", () => {
                string library = Collection("no-mcd"); string folder = Directory.CreateDirectory(Path.Combine(library, "PAC")).FullName;
                File.WriteAllText(Path.Combine(folder, "readme.txt"), "No extracted model here.");
                var entry = Profile(204, "PAC", 215); var candidates = ModelLibrary.Scan(library); Equal(0, candidates.Count);
                var result = ModelLibraryBinding.Apply(new List<RosterEntry> { entry }, candidates, Table("PAC"));
                Equal(0, result.BoundCount); Equal(0, result.AddedCount); Equal((string)null, entry.Folder);
            });
            Test("Missing candidate folders cannot bind or appear as extracted models", () => {
                var entry = Profile(204, "PAC", 215); var candidates = new[] { null, new ModelCandidate { Name = "PAC", Folder = Path.Combine(root, "missing-model"), SourceId = 368 } };
                var entries = new List<RosterEntry> { entry }; var result = ModelLibraryBinding.Apply(entries, candidates, Table("PAC"));
                Equal(0, result.BoundCount); Equal(0, result.AddedCount); Equal(1, entries.Count); Equal((string)null, entry.Folder);
            });
            Console.WriteLine(passed + " model-library binding checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static RosterEntry Profile(int id, string name, int sourceId)
    {
        return new RosterEntry { Id = id, Name = name, SourceId = sourceId, Source = "Profile" };
    }

    private static string Collection(string name) { return Directory.CreateDirectory(Path.Combine(root, name)).FullName; }
    private static string Table(string name) { return Path.Combine(root, name, "Roster", "CharProfileTable.jsfb"); }
    private static string Model(string collection, string name)
    {
        string folder = Directory.CreateDirectory(Path.Combine(collection, name.Replace('/', Path.DirectorySeparatorChar))).FullName;
        string attire = Directory.CreateDirectory(Path.Combine(folder, "Default_Attire")).FullName;
        File.WriteAllBytes(Path.Combine(attire, "attire.mcd"), new byte[] { 1, 2, 3 });
        return folder;
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { throw new Exception("FAIL " + name, error); }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'.");
    }
}
