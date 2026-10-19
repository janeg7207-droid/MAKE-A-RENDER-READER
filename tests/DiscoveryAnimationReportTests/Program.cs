using System;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static void Main()
    {
        var report = new DiscoveryAnimationReport {
            HelperVersion = "1.6.9", CapturedUtc = new DateTime(2026, 10, 19, 18, 20, 0, DateTimeKind.Utc),
            GameRoot = @"D:\Steam Library\WWE 2K26", DiscoveryRoot = @"E:\Mods\discovery_data",
            SavedPath = @"C:\Users\Test\animation-diagnostics\latest.txt", HasImport = true,
            Definitions = 1584, Available = 1088, Imported = 194, Missing = 455, Unsupported = 45,
            ArchiveCount = 17, ArchiveImported = 2, ArchivesSearched = true, ScanIncomplete = true,
            ArchiveStatus = "2 of 455 requested motions recovered. Lookup incomplete; see archive warnings."
        };
        report.SearchRoots.Add(report.DiscoveryRoot);
        for (int index = 1; index <= 20; index++) report.Warnings.Add("Unreadable archive " + index + @": E:\CAKs\pack_" + index + ".cak");
        var enigma = new DiscoveryAnimationReportEntry {
            Name = "Enigma 4", Id = 17330, Variant = 0, Clip = "17330a000", Kind = "ScanIncomplete",
            Reason = "Clip 17330a000 was not recovered. Archive lookup incomplete: the required archive index could not be read."
        };
        enigma.Names.Add("Enigma 4"); enigma.Names.Add("Custom Hardy Taunt");
        report.Unavailable.Add(enigma);
        report.Unavailable.Add(new DiscoveryAnimationReportEntry {
            Name = "Confidence 1", Id = 37367, Variant = 0, Clip = "37367a000", Kind = "MissingAnimation",
            Reason = "Exact animation record was not located in extracted files or the archives searched."
        });
        report.Unavailable.Add(new DiscoveryAnimationReportEntry {
            Name = "Arms Raised 12", Id = 60001, Variant = 2, Clip = "60001a002", Kind = "UnsupportedFormat",
            Reason = "The found animation has additional channel sections that this decoder cannot read."
        });

        string complete = report.Format(null);
        Check("The visible report identifies the actual helper version", complete.Contains("Helper version: 1.6.9"));
        Check("The visible report identifies the selected game folder", complete.Contains(report.GameRoot));
        Check("The visible report identifies the selected Discovery folder", complete.Contains(report.DiscoveryRoot));
        Check("The visible report identifies its saved copy", complete.Contains(report.SavedPath));
        Check("The lookup time has an unambiguous UTC representation", complete.Contains("2026-10-19T18:20:00.0000000Z"));
        Check("The report exposes archive counts and lookup status", complete.Contains("archives found: 17") && complete.Contains(report.ArchiveStatus));
        Check("An incomplete scan is visible before individual failures", complete.IndexOf("Scan complete: no", StringComparison.Ordinal) < complete.IndexOf("ALL UNAVAILABLE", StringComparison.Ordinal));
        Check("All warnings are retained beyond the former eight-warning limit", report.Warnings.All(complete.Contains));
        Check("Every unresolved ID, variant, and exact clip is included", report.Unavailable.All(entry =>
            complete.Contains("ID: " + entry.Id + "; variant: " + entry.Variant + "; clip: " + entry.Clip)));
        Check("Every complete original reason is retained without truncation", report.Unavailable.All(entry => complete.Contains(entry.Reason)));
        Check("Aliases remain available in the diagnostic text", complete.Contains("Custom Hardy Taunt"));

        string searched = report.Format("Enigma4");
        int focus = searched.IndexOf("MATCHING UNAVAILABLE ANIMATIONS", StringComparison.Ordinal);
        int warnings = searched.IndexOf("COMPLETE LOOKUP WARNINGS", StringComparison.Ordinal);
        Check("The current compact-name search appears before the warning list", focus >= 0 && focus < warnings);
        Check("The matching unavailable entry is readable immediately", searched.Substring(focus, warnings - focus).Contains(enigma.Reason));
        Check("Focused search does not silently omit other unresolved entries", report.Unavailable.All(entry => searched.Contains(entry.Reason)));
        Check("Focused search does not silently omit any warning", report.Warnings.All(searched.Contains));
        Check("Exact clip searches find the intended unavailable variant", report.Format("17330a000").Contains("17330a000 (1)"));
        Check("Alias searches find their exact unavailable clip", report.Format("Custom Hardy").Contains("Custom Hardy (1)"));
        Check("Searching a different numeric variant never presents another motion as its failure", report.Format("Arms Raised 2").Contains("Arms Raised 2 (0)"));

        Check("Incomplete lookup rows identify the known classification first", DiscoveryAnimationReport.ShortReason("ScanIncomplete", enigma.Reason) == "Animation search incomplete");
        Check("Source-read failures remain distinct from format limitations", DiscoveryAnimationReport.ShortReason("ReadFailure", null) == "Animation file could not be read");
        Check("Cache failures remain distinct from missing source files", DiscoveryAnimationReport.ShortReason("CacheFailure", null) == "Preview cache could not be prepared");
        Check("Only explicit distinct-byte conflict evidence yields a conflict label", DiscoveryAnimationReport.ShortReason("MissingAnimation", "Different motion bytes claim this exact clip ID in the searched sources") == "Conflicting archive animations");
        Check("A generic list of possible archive problems does not invent a specific cause", DiscoveryAnimationReport.ShortReason("ScanIncomplete", "Archive lookup incomplete (unreadable, ambiguous or budget-limited sources)") == "Animation search incomplete");

        string empty = new DiscoveryAnimationReport { HelperVersion = "1.6.9" }.Format("Enigma4");
        Check("A report opened before a refresh explains that no lookup has completed", empty.Contains("No completed Discovery animation lookup"));
        Check("A report without an import does not claim that an archive search ran", !empty.Contains("Archive lookup performed: yes"));
        Console.WriteLine("PASS " + passed + " animation diagnostic report checks");
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException(name);
        passed++; Console.WriteLine("PASS " + name);
    }
}
