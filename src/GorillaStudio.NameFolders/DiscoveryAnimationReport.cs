using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryAnimationReportEntry
    {
        public string Name { get; set; }
        public List<string> Names { get; } = new List<string>();
        public int Id { get; set; }
        public int Variant { get; set; }
        public string Clip { get; set; }
        public string Kind { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>A copyable snapshot of an animation lookup, independent of its disabled UI rows.</summary>
    public sealed class DiscoveryAnimationReport
    {
        public string HelperVersion { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string GameRoot { get; set; }
        public string DiscoveryRoot { get; set; }
        public string SavedPath { get; set; }
        public int Definitions { get; set; }
        public int Available { get; set; }
        public int Imported { get; set; }
        public int ArchiveImported { get; set; }
        public int ArchiveCount { get; set; }
        public int Missing { get; set; }
        public int Unsupported { get; set; }
        public int ReadFailures { get; set; }
        public int CacheFailures { get; set; }
        public bool ArchivesSearched { get; set; }
        public bool ScanIncomplete { get; set; }
        public bool HasImport { get; set; }
        public string ArchiveStatus { get; set; }
        public List<string> SearchRoots { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<DiscoveryAnimationReportEntry> Unavailable { get; } = new List<DiscoveryAnimationReportEntry>();

        public string Format(string query)
        {
            var text = new StringBuilder();
            text.AppendLine("GORILLA STUDIO — ANIMATION DETAILS");
            text.AppendLine("Helper version: " + Value(HelperVersion));
            text.AppendLine("Captured UTC: " + (CapturedUtc == default(DateTime) ? "Not refreshed" : CapturedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
            text.AppendLine("Game folder: " + Value(GameRoot));
            text.AppendLine("Discovery folder: " + Value(DiscoveryRoot));
            if (!string.IsNullOrWhiteSpace(SavedPath)) text.AppendLine("Saved report: " + SavedPath);
            text.AppendLine();
            if (!HasImport)
            {
                text.AppendLine("No completed Discovery animation lookup is available yet. Load Discovery data to create the report.");
                return text.ToString();
            }
            text.AppendLine("Playable library entries: " + Available + "; Discovery definitions: " + Definitions
                + "; imported: " + Imported + "; recovered from archives: " + ArchiveImported + ".");
            text.AppendLine("Not located: " + Missing + "; decoder rejections: " + Unsupported
                + "; source read errors: " + ReadFailures + "; preview cache errors: " + CacheFailures + ".");
            text.AppendLine("Scan complete: " + (!ScanIncomplete ? "yes" : "no"));
            text.AppendLine("Archive lookup performed: " + (ArchivesSearched ? "yes" : "no") + "; archives found: " + ArchiveCount + ".");
            if (!string.IsNullOrWhiteSpace(ArchiveStatus)) text.AppendLine("Archive result: " + ArchiveStatus);
            if (SearchRoots.Count > 0)
            {
                text.AppendLine("Extracted animation search roots:");
                foreach (string root in SearchRoots) text.AppendLine("  " + root);
            }

            DiscoveryAnimationReportEntry[] entries = Unavailable.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id).ThenBy(entry => entry.Variant).ToArray();
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matches = entries.Where(entry => DiscoveryTauntSearch.Matches(
                    DiscoveryTauntSearch.BuildText(entry.Name, entry.Names, null, entry.Clip), query)).ToArray();
                text.AppendLine();
                text.AppendLine("MATCHING UNAVAILABLE ANIMATIONS — " + query.Trim() + " (" + matches.Length + ")");
                if (matches.Length == 0) text.AppendLine("No unavailable animation matches this search. Playable animations are not listed as failures.");
                foreach (DiscoveryAnimationReportEntry entry in matches) AppendEntry(text, entry);
            }

            text.AppendLine();
            text.AppendLine("COMPLETE LOOKUP WARNINGS (" + Warnings.Count + ")");
            if (Warnings.Count == 0) text.AppendLine("None.");
            for (int index = 0; index < Warnings.Count; index++) text.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture) + ". " + Warnings[index]);
            text.AppendLine();
            text.AppendLine("ALL UNAVAILABLE ANIMATIONS (" + entries.Length + ")");
            if (entries.Length == 0) text.AppendLine("None.");
            foreach (DiscoveryAnimationReportEntry entry in entries) AppendEntry(text, entry);
            return text.ToString();
        }

        public static string ShortReason(string kind, string reason)
        {
            // This is an exact conflict reported by the resolver, rather than
            // guessing which member of a generic list of possible causes applies.
            if (!string.IsNullOrEmpty(reason) && reason.IndexOf("Different motion bytes claim this exact clip ID", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Conflicting archive animations";
            switch (kind)
            {
                case "ScanIncomplete": return "Animation search incomplete";
                case "UnsupportedFormat": return "Animation format could not be decoded";
                case "ReadFailure": return "Animation file could not be read";
                case "CacheFailure": return "Preview cache could not be prepared";
                default: return "Animation not located";
            }
        }

        private static void AppendEntry(StringBuilder text, DiscoveryAnimationReportEntry entry)
        {
            text.AppendLine();
            text.AppendLine(Value(entry.Name) + " — " + ShortReason(entry.Kind, entry.Reason));
            text.AppendLine("  ID: " + entry.Id + "; variant: " + entry.Variant + "; clip: " + Value(entry.Clip) + "; classification: " + Value(entry.Kind));
            if (entry.Names.Count > 1) text.AppendLine("  Names: " + string.Join(" / ", entry.Names));
            text.AppendLine("  Reason: " + Value(entry.Reason));
        }

        private static string Value(string value) { return string.IsNullOrWhiteSpace(value) ? "(not available)" : value; }
    }
}
