using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "gorilla-version-selection-fixtures-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Date = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        var kingOld = Entry(1448, "King old", "king/old.gmr", -3);
        var kingNew = Entry(1448, "King new", "king/new.gmr", -1);
        var hardyOld = Entry(112, "Hardy old", "hardy/old.gmr", -4);
        var hardyNew = Entry(112, "Hardy new", "hardy/new.gmr", -2);
        var punk = Entry(312, "Punk", "punk/complete.gmr", -5);
        var hidden = Entry(695, "Hidden selected", "hidden/complete.gmr", -7);
        var selected = new[] { kingOld, punk, hardyNew, hidden, kingNew, hardyOld };
        var before = selected.Select(Snapshot).ToArray();
        var inspected = CompletedRenderVersionSelection.Inspect(selected);
        Check(inspected.Failed.Count == 0 && inspected.Entries.SequenceEqual(selected), "Inspection retains every checked source and its order before any explicit choice");
        Check(inspected.Conflicts.Select(group => group.WrestlerId).SequenceEqual(new[] { 112, 1448 }), "Only duplicate wrestler IDs 112 and 1448 need version choices");
        Check(inspected.Conflicts.All(group => group.Entries.Count == 2), "Each conflict contains all checked versions for exactly that wrestler");
        Check(inspected.Conflicts.Single(group => group.WrestlerId == 1448).Suggested == kingNew
            && inspected.Conflicts.Single(group => group.WrestlerId == 112).Suggested == hardyNew, "The newest timestamp supplies a visible suggestion for each duplicate ID");
        Check(inspected.Entries.Count(entry => entry.WrestlerId == 1448) == 2, "A suggestion alone never silently removes an older checked outfit");
        Check(before.SequenceEqual(selected.Select(Snapshot)), "Inspect does not mutate source entries, labels or check state held by the caller");

        var chosen = new Dictionary<int, string> { { 1448, kingOld.Path }, { 112, hardyNew.Path } };
        var result = CompletedRenderVersionSelection.Resolve(selected, chosen);
        Check(result.Failed.Count == 0 && result.Conflicts.Count == 0, "Explicit choices resolve all current duplicate groups");
        Check(result.Entries.SequenceEqual(new[] { kingOld, punk, hardyNew, hidden }), "The exact chosen versions and all nonconflicting checked wrestlers retain their input order");
        Check(result.Entries.Contains(hidden) && result.Entries.Contains(punk), "Hidden and unrelated checked wrestlers remain selected");
        Check(result.Entries.Count(entry => entry.WrestlerId == 1448) == 1 && result.Entries.First() == kingOld, "An explicit older outfit wins over the newest suggestion without changing its wrestler ID");
        Check(result.Entries.Select(entry => entry.WrestlerId).Distinct().Count() == result.Entries.Count, "The final selection contains one saved movie per logical wrestler ID");
        Check(chosen.Count == 2 && chosen[1448] == kingOld.Path && before.SequenceEqual(selected.Select(Snapshot)), "Resolving a choice preserves the caller's dictionary and all original entry fields");
        Check(!Directory.Exists(Root), "Planning uses metadata only and does not create fixture folders or read movie files");

        Reject(selected, null, "Duplicate versions require explicit choices rather than automatically using newest");
        Reject(selected, new Dictionary<int, string> { { 1448, kingOld.Path } }, "A missing choice for the other duplicate ID keeps the plan unresolved");
        Reject(selected, new Dictionary<int, string> { { 1448, kingOld.Path }, { 112, " " } }, "Blank choices cannot clear a conflicting wrestler implicitly");
        Reject(selected, new Dictionary<int, string> { { 1448, hardyOld.Path }, { 112, hardyNew.Path } }, "A checked movie for another ID cannot be chosen as this wrestler's version");
        Reject(selected, new Dictionary<int, string> { { 1448, Path.Combine(Root, "king/unselected.gmr") }, { 112, hardyNew.Path } }, "An unselected file in the same folder cannot enter the installation plan");
        Reject(selected, new Dictionary<int, string> { { 1448, kingOld.Path }, { 112, hardyNew.Path }, { 312, punk.Path } }, "A choice for a nonconflicting checked wrestler is rejected as stale dialog data");
        Reject(selected, new Dictionary<int, string> { { 1448, kingOld.Path }, { 112, hardyNew.Path }, { 999, kingOld.Path } }, "A forged unselected wrestler ID cannot add or redirect a choice");
        Reject(new[] { kingNew, punk, hardyNew, hidden }, chosen, "A removed duplicate changes the current group set and invalidates older dialog choices");
        Check(before.SequenceEqual(selected.Select(Snapshot)), "All rejected choice attempts leave the original checked metadata untouched");

        var simple = new[] { hidden, punk, kingOld };
        result = CompletedRenderVersionSelection.Resolve(simple, null);
        Check(result.Failed.Count == 0 && result.Entries.SequenceEqual(simple), "A selection that already has one render per ID passes through without a chooser");
        Check(CompletedRenderVersionSelection.Inspect(null).Entries.Count == 0
            && CompletedRenderVersionSelection.Resolve(Array.Empty<CompletedRenderEntry>(), null).Failed.Count == 0, "An empty inventory remains an empty plan without inventing a default wrestler");
        Reject(simple, new Dictionary<int, string> { { 1448, kingOld.Path } }, "Choice dictionaries cannot silently retarget a nonconflicting inventory");

        var tieA = Entry(1448, "Tie A", "tie/A.gmr", 0); var tieB = Entry(1448, "Tie B", "tie/B.gmr", 0);
        var tie = CompletedRenderVersionSelection.Inspect(new[] { tieB, tieA }).Conflicts.Single();
        Check(tie.Suggested == tieA && tie.Entries.SequenceEqual(new[] { tieA, tieB }), "Equal timestamps use stable full-path ordering, independent of input enumeration order");
        result = CompletedRenderVersionSelection.Resolve(new[] { tieB, tieA }, new Dictionary<int, string> { { 1448, tieB.Path } });
        Check(result.Failed.Count == 0 && result.Entries.Single() == tieB, "A user can choose the other tied version explicitly");
        var fileA = Entry(112, "Same file name", "first/112_same.gmr", 0);
        var fileB = Entry(112, "Same file name", "second/112_same.gmr", 0);
        result = CompletedRenderVersionSelection.Resolve(new[] { fileA, fileB }, new Dictionary<int, string> { { 112, fileB.Path } });
        Check(result.Failed.Count == 0 && result.Entries.Single() == fileB, "Identical base filenames in separate folders remain distinct choices by full path");
        var alias = Path.Combine(Path.GetDirectoryName(fileB.Path), "..", "second", Path.GetFileName(fileB.Path));
        result = CompletedRenderVersionSelection.Resolve(new[] { fileA, fileB }, new Dictionary<int, string> { { 112, alias } });
        Check(result.Failed.Count == 0 && result.Entries.Single() == fileB, "A normalized path still refers to the exact checked movie, without another file lookup");

        Bad(new[] { kingOld, null }, "Null checked rows cannot produce a partial install selection");
        var invalid = Entry(0, "Invalid", "bad/zero.gmr", 0); Bad(new[] { kingOld, invalid }, "An invalid logical wrestler ID blocks the entire plan");
        invalid = Entry(65536, "Invalid", "bad/too-large.gmr", 0); Bad(new[] { invalid }, "Out-of-range IDs cannot be made installable by the chooser");
        invalid = Entry(112, "Unavailable", "bad/incomplete.gmr", 0); invalid.Error = "incomplete movie";
        Bad(new[] { kingOld, invalid }, "Unavailable checked movies cannot be resolved into valid installation entries");
        invalid = Entry(112, "Missing path", "bad/unused.gmr", 0); invalid.Path = null;
        Bad(new[] { kingOld, invalid }, "Missing saved file paths fail without silently dropping the row");
        invalid.Path = "relative.gmr"; Bad(new[] { invalid }, "Relative saved paths cannot depend on the process working directory");
        invalid.Path = Path.Combine(Root, "bad/not-a-render.txt"); Bad(new[] { invalid }, "Non-GMR paths cannot become a chosen render");
        Bad(new[] { kingOld, kingOld }, "The same saved file cannot appear twice in a checked selection");
        var samePathOtherId = Entry(112, "Wrong owner", "unused.gmr", 0); samePathOtherId.Path = kingOld.Path;
        Bad(new[] { kingOld, samePathOtherId }, "One file cannot be treated as two different logical wrestler slots");
        var bound = Enumerable.Range(1, 4096).Select(id => Entry(id, "Bound " + id, "bound/" + id + ".gmr", 0)).ToArray();
        Check(CompletedRenderVersionSelection.Resolve(bound, null).Entries.Count == 4096, "The supported maximum group of unique checked wrestlers remains intact");
        Bad(bound.Concat(new[] { Entry(5000, "Over bound", "bound/5000.gmr", 0) }), "Oversized selections are bounded before grouping or producing partial results");
        Reject(new[] { fileA, fileB }, new Dictionary<int, string> { { 112, "112_same.gmr" } }, "A filename alone cannot ambiguously select an outfit from several folders");
        Console.WriteLine(checks + " completed render version selection checks passed."); return 0;
    }

    private static CompletedRenderEntry Entry(int id, string name, string relative, int days)
    { return new CompletedRenderEntry { WrestlerId = id, Name = name, Path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)), Taunt = "Same taunt", AttireName = name, Screens = "both", LastWriteTimeUtc = Date.AddDays(days), Bytes = 1234, Frames = 100 }; }
    private static string Snapshot(CompletedRenderEntry entry)
    { return string.Join("|", entry.Path, entry.WrestlerId, entry.Name, entry.Taunt, entry.AttireName, entry.Screens, entry.LastWriteTimeUtc.Ticks, entry.Bytes, entry.Frames, entry.Error); }
    private static void Reject(IEnumerable<CompletedRenderEntry> selected, IDictionary<int, string> chosen, string label)
    { var result = CompletedRenderVersionSelection.Resolve(selected, chosen); Check(result.Failed.Count != 0 && result.Entries.Count == 0, label); }
    private static void Bad(IEnumerable<CompletedRenderEntry> selected, string label)
    { var result = CompletedRenderVersionSelection.Inspect(selected); Check(result.Failed.Count != 0 && result.Entries.Count == 0 && result.Conflicts.Count == 0, label); }
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAILED: " + label); checks++; Console.WriteLine("PASS " + label); }
}
