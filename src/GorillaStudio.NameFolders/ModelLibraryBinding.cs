using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class ModelLibraryBindingResult
    {
        public int BoundCount { get; internal set; }
        public int AddedCount { get; internal set; }
        public Dictionary<RosterEntry, List<ModelCandidate>> Choices { get; private set; }
        public List<ModelCandidate> Unassigned { get; internal set; }

        internal ModelLibraryBindingResult()
        {
            Choices = new Dictionary<RosterEntry, List<ModelCandidate>>();
            Unassigned = new List<ModelCandidate>();
        }
    }

    /// <summary>Associates extracted models with existing roster slots without inventing IDs.</summary>
    public static class ModelLibraryBinding
    {
        public static ModelLibraryBindingResult Apply(List<RosterEntry> entries, IEnumerable<ModelCandidate> candidates,
            string rosterTable, bool addUnassigned = true)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            List<ModelCandidate> available = Available(candidates);
            var names = new Dictionary<string, List<ModelCandidate>>(StringComparer.Ordinal);
            var sources = new Dictionary<int, List<ModelCandidate>>();
            foreach (ModelCandidate candidate in available.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Folder, StringComparer.OrdinalIgnoreCase))
            {
                string key = MatchingKey(candidate.Name);
                List<ModelCandidate> group;
                if (key.Length > 0)
                {
                    if (!names.TryGetValue(key, out group)) names.Add(key, group = new List<ModelCandidate>());
                    group.Add(candidate);
                }
                if (candidate.SourceId > 0 && candidate.SourceId <= 65535)
                {
                    if (!sources.TryGetValue(candidate.SourceId, out group)) sources.Add(candidate.SourceId, group = new List<ModelCandidate>());
                    group.Add(candidate);
                }
            }
            var result = new ModelLibraryBindingResult();
            foreach (RosterEntry entry in entries)
            {
                if (entry == null || entry.Id < 1 || entry.Id > 65535 || Directory.Exists(entry.Folder)) continue;
                if (!string.IsNullOrWhiteSpace(rosterTable) && !string.IsNullOrWhiteSpace(entry.RosterTable)
                    && !SamePath(entry.RosterTable, rosterTable)) continue;

                // A profile's base ID alone does not prove which installed model it uses.
                // Only a verified stock mapping can supply a physical stock-ID fallback.
                int sourceId = entry.SourceId;
                if (!string.IsNullOrWhiteSpace(entry.StockFolder)) sourceId = FolderNames.ExistingId(entry.StockFolder) ?? 0;
                else if (string.Equals(entry.Source, "Profile", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Source, "Found in models folder", StringComparison.OrdinalIgnoreCase)
                    || (entry.Source != null && entry.Source.StartsWith("Chosen", StringComparison.OrdinalIgnoreCase))) sourceId = 0;
                List<ModelCandidate> matches;
                if (!names.TryGetValue(MatchingKey(entry.Name), out matches)
                    && (sourceId < 1 || sourceId > 65535 || !sources.TryGetValue(sourceId, out matches))) continue;
                if (matches.Count > 1)
                {
                    result.Choices[entry] = new List<ModelCandidate>(matches);
                    continue;
                }
                if (matches.Count == 0) continue;
                ModelCandidate selected = matches[0];
                entry.Folder = selected.Folder;
                if (selected.SourceId > 0 && selected.SourceId <= 65535) entry.SourceId = selected.SourceId;
                if (string.IsNullOrWhiteSpace(entry.RosterTable)) entry.RosterTable = rosterTable;
                entry.Source = "Found in models folder";
                entry.IsMod = true;
                result.BoundCount++;
            }

            var assigned = Folders(entries);
            result.Unassigned = available.Where(candidate => !assigned.Contains(candidate.Folder)).ToList();
            if (addUnassigned) result.AddedCount = AddUnassigned(entries, result.Unassigned, rosterTable);
            return result;
        }

        public static int AddUnassigned(List<RosterEntry> entries, IEnumerable<ModelCandidate> candidates, string rosterTable)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            var represented = Folders(entries);
            int added = 0;
            foreach (ModelCandidate candidate in Available(candidates))
            {
                if (!represented.Add(candidate.Folder)) continue;
                entries.Add(new RosterEntry {
                    Id = 0,
                    Name = string.IsNullOrWhiteSpace(candidate.Name) ? FolderNames.DisplayName(new DirectoryInfo(candidate.Folder).Name) : candidate.Name,
                    Folder = candidate.Folder,
                    SourceId = candidate.SourceId > 0 && candidate.SourceId <= 65535 ? candidate.SourceId : 0,
                    RosterTable = rosterTable,
                    Source = "Choose roster slot",
                    IsMod = true
                });
                added++;
            }
            return added;
        }

        private static List<ModelCandidate> Available(IEnumerable<ModelCandidate> candidates)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ModelCandidate>();
            foreach (ModelCandidate candidate in candidates ?? Enumerable.Empty<ModelCandidate>())
            {
                if (candidate == null || !Directory.Exists(candidate.Folder)) continue;
                string path = FullPath(candidate.Folder);
                if (path == null || !seen.Add(path)) continue;
                result.Add(new ModelCandidate { Folder = path, Name = candidate.Name, SourceId = candidate.SourceId });
            }
            return result;
        }

        private static HashSet<string> Folders(IEnumerable<RosterEntry> entries)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RosterEntry entry in entries)
            {
                string path = entry == null ? null : FullPath(entry.Folder);
                if (path != null) result.Add(path);
            }
            return result;
        }

        private static bool SamePath(string first, string second)
        {
            string normalized = FullPath(first);
            return normalized != null && string.Equals(normalized, FullPath(second), StringComparison.OrdinalIgnoreCase);
        }

        private static string MatchingKey(string name)
        {
            // Match ModelLibrary.Match's spelling rules, once per candidate and row.
            var output = new StringBuilder();
            foreach (char character in (name ?? "").Normalize(NormalizationForm.FormC))
                if (char.IsLetterOrDigit(character)) output.Append(char.ToUpperInvariant(character));
            return output.ToString();
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                return full.Length == Path.GetPathRoot(full).Length ? full
                    : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (IOException) { return null; }
        }
    }
}
