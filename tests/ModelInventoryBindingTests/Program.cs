using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root;

    private static int Main(string[] args)
    {
        root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-model-stage-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            SnapshotsAndManualChanges();
            AmbiguityAndContext();
            if (args.Length > 0) UploadedMapping(args[0]);
            Console.WriteLine("PASS " + passed + " model inventory staging checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void SnapshotsAndManualChanges()
    {
        ModelCandidate pac = Model("collection/1173_PAC", "PAC", 1173), mjf = Model("collection/MJF", "MJF", 0);
        RosterEntry entry = Profile(924, "PAC"), other = Profile(122, "AVERY STYLES");
        var entries = new List<RosterEntry> { entry, other };
        var stage = ModelInventoryBindingStage.Capture(entries, null);
        Task.Run(() => stage.Prepare(new[] { pac, mjf }, CancellationToken.None)).GetAwaiter().GetResult();
        Check(entry.Folder == null && entry.SourceId == 0 && entries.Count == 2, "Worker staging never mutates live roster rows or adds live entries");
        var manual = new RosterEntry { Id = 4000, Name = "MY MJF", Folder = mjf.Folder, Source = "Chosen folder", SourceId = 99 };
        entries.Add(manual);
        Dictionary<RosterEntry, List<ModelCandidate>> choices;
        HashSet<string> ready;
        Check(stage.Commit(entries, null, out choices, out ready), "A matching completed snapshot commits on its owner context");
        Check(entry.Folder == pac.Folder && entry.Id == 924 && entry.SourceId == 1173 && entry.Source == "Found in models folder", "Staged name binding preserves the logical slot and records the physical source");
        Check(entries.Contains(manual) && manual.SourceId == 99 && manual.Name == "MY MJF", "Manual rows added while scanning retain their identity and chosen association");
        Check(entries.Count == 3 && entries.Count(row => row.Folder == mjf.Folder) == 1, "An added manual association prevents a duplicate unassigned-folder row");
        Check(ready.Contains(pac.Folder) && ready.Contains(mjf.Folder), "The ready snapshot includes both staged and manually retained indexed models");
        Check(other.Folder == null && other.Source == "Profile", "An unrelated row never receives a guessed physical model");

        var edited = Profile(925, "PAC"); var editedRows = new List<RosterEntry> { edited };
        var editedStage = ModelInventoryBindingStage.Capture(editedRows, null);
        editedStage.Prepare(new[] { pac }, CancellationToken.None);
        edited.Name = "User renamed PAC"; edited.Folder = mjf.Folder; edited.Source = "Chosen model folder"; edited.SourceId = 194;
        Check(editedStage.Commit(editedRows, null, out choices, out ready), "An intervening row edit does not invalidate unrelated staged data");
        Check(edited.Name == "User renamed PAC" && edited.Folder == mjf.Folder && edited.SourceId == 194 && edited.Source == "Chosen model folder", "A pending worker cannot overwrite a later chosen model or renamed wrestler");
        Check(!choices.ContainsKey(edited), "Stale staged ambiguity choices do not attach to an edited row");

        var removed = Profile(926, "PAC"); var removedRows = new List<RosterEntry> { removed };
        var removedStage = ModelInventoryBindingStage.Capture(removedRows, null); removedStage.Prepare(new[] { pac }, CancellationToken.None);
        removedRows.Clear(); removedStage.Commit(removedRows, null, out choices, out ready);
        Check(!removedRows.Contains(removed) && removed.Folder == null && removedRows.All(row => row.Id != 926), "A removed roster row is not restored or mutated by an older scan");
        Check(ModelInventoryBindingStage.ExistingRoots(new[] { pac.Folder, Path.Combine(root, "collection"), Path.Combine(root, "missing") }).SequenceEqual(new[] { Path.Combine(root, "collection") }), "Worker root pruning keeps one existing parent without redundant subtree scans");
    }

    private static void AmbiguityAndContext()
    {
        ModelCandidate first = Model("ambiguous/one/PAC", "PAC", 1173), second = Model("ambiguous/two/PAC", "PAC", 1173);
        var row = Profile(924, "PAC"); var rows = new List<RosterEntry> { row };
        var stage = ModelInventoryBindingStage.Capture(rows, null); stage.Prepare(new[] { first, second }, CancellationToken.None);
        Dictionary<RosterEntry, List<ModelCandidate>> choices; HashSet<string> ready;
        stage.Commit(rows, null, out choices, out ready);
        Check(row.Folder == null && choices.ContainsKey(row) && choices[row].Count == 2, "Ambiguous matches remain explicit choices keyed by the live row");
        Check(rows.Count(entry => entry.Id == 0) == 2 && rows.Where(entry => entry.Id == 0).All(entry => entry.Source == "Choose roster slot"), "Unassigned models retain their original no-guessed-slot semantics");

        var old = Profile(924, "PAC"); var oldRows = new List<RosterEntry> { old };
        var stale = ModelInventoryBindingStage.Capture(oldRows, "table-A"); stale.Prepare(new[] { first }, CancellationToken.None);
        Check(!stale.Commit(new List<RosterEntry>(oldRows), "table-A", out choices, out ready) && old.Folder == null, "A newer roster-list instance rejects all older staging results");
        Check(!stale.Commit(oldRows, "table-B", out choices, out ready) && old.Folder == null, "A changed roster source rejects old bindings even when row references remain");
        Check(choices.Count == 0 && ready.Count == 0, "Rejected context commits expose no stale choices or ready state");

        var canceled = ModelInventoryBindingStage.Capture(oldRows, null);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { canceled.Prepare(new[] { first }, cancellation.Token); throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { Check(true, "Cancellation prevents the staged filesystem work"); }
        }
        Check(!canceled.Commit(oldRows, null, out choices, out ready) && old.Folder == null, "A canceled or unfinished stage cannot commit a partial binding");
    }

    private static void UploadedMapping(string data)
    {
        string table = Path.Combine(data, "Roster", "CharProfileTable.jsfb");
        var entries = DiscoveryProfiles.Read(table).Select(profile => new RosterEntry { Id = profile.Id,
            Name = "Intentionally unrelated display name", Source = "Profile", RosterTable = table }).ToList();
        var inventory = ModelLibrary.Scan(data);
        var stage = ModelInventoryBindingStage.Capture(entries, table);
        Task.Run(() => stage.Prepare(inventory, CancellationToken.None)).GetAwaiter().GetResult();
        Check(entries.All(entry => entry.Folder == null), "Actual uploaded roster rows stay untouched until owner-thread commit");
        Dictionary<RosterEntry, List<ModelCandidate>> choices; HashSet<string> ready;
        Check(stage.Commit(entries, table, out choices, out ready), "Actual uploaded model-path mappings commit in their original Discovery context");
        Check(entries.Single(entry => entry.Id == 1481).SourceId == 312 && entries.Single(entry => entry.Id == 1481).Source == "Character mapping", "The uploaded CM Punk physical312 mapping retains logical1481 without name matching");
        Check(entries.Single(entry => entry.Id == 910).SourceId == 123, "The uploaded Rey physical123 mapping retains logical910");
        Check(entries.Where(entry => entry.Id > 0).Select(entry => entry.Folder).Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8, "The uploaded mapping still proves exactly eight of ten extracted models");
        Check(entries.Count(entry => entry.Id == 0) == 2, "The two unreferenced uploaded models stay unassigned rather than inheriting IDs");
    }

    private static RosterEntry Profile(int id, string name) { return new RosterEntry { Id = id, Name = name, Source = "Profile" }; }
    private static ModelCandidate Model(string path, string name, int physical)
    { string folder = Path.Combine(root, path); Directory.CreateDirectory(Path.Combine(folder, "Default_Attire")); File.WriteAllText(Path.Combine(folder, "Default_Attire", "Attire.mcd"), "fixture"); return new ModelCandidate { Folder = folder, Name = name, SourceId = physical }; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
}
