using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    /// <summary>Stages filesystem-backed enrichment on private rows, then merges unchanged rows on their owner thread.</summary>
    public sealed class ModelInventoryBindingStage
    {
        private sealed class Row
        {
            internal RosterEntry Original, Before, Working;
        }

        private readonly List<RosterEntry> originalList;
        private readonly string rosterTable;
        private readonly List<Row> rows;
        private readonly List<RosterEntry> additions = new List<RosterEntry>();
        private readonly Dictionary<RosterEntry, List<ModelCandidate>> choices = new Dictionary<RosterEntry, List<ModelCandidate>>();
        private readonly HashSet<string> indexedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool prepared;

        private ModelInventoryBindingStage(List<RosterEntry> entries, string table)
        {
            originalList = entries; rosterTable = table;
            rows = entries.Where(entry => entry != null).Select(entry => new Row {
                Original = entry, Before = Copy(entry), Working = Copy(entry)
            }).ToList();
        }

        // Capture and Commit belong to the UI thread. Prepare never reads or
        // mutates the original entries after Capture has taken this snapshot.
        public static ModelInventoryBindingStage Capture(List<RosterEntry> entries, string rosterTable)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            return new ModelInventoryBindingStage(entries, rosterTable);
        }

        public static List<string> ExistingRoots(IEnumerable<string> roots)
        {
            var unique = (roots ?? Enumerable.Empty<string>()).Where(Directory.Exists)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return unique.Where(path => !unique.Any(other => !string.Equals(path, other, StringComparison.OrdinalIgnoreCase)
                && path.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        public void Prepare(IEnumerable<ModelCandidate> inventory, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var found = (inventory ?? Enumerable.Empty<ModelCandidate>()).Where(model => model != null).ToList();
            var working = rows.Select(row => row.Working).ToList();
            var mapped = MappedModelLibrary.FindMatches(working, found, rosterTable, cancellation);
            foreach (var mapping in mapped)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Directory.Exists(mapping.Key.Folder) || mapping.Value.Count != 1) continue;
                ModelCandidate model = mapping.Value[0];
                mapping.Key.Folder = model.Folder;
                if (model.SourceId > 0) mapping.Key.SourceId = model.SourceId;
                mapping.Key.Source = "Character mapping";
                mapping.Key.IsMod = true;
            }
            var bindable = working.Where(entry => Directory.Exists(entry.Folder) || !mapped.ContainsKey(entry)).ToList();
            ModelLibraryBindingResult binding = ModelLibraryBinding.Apply(bindable, found, rosterTable, false);
            foreach (var pair in binding.Choices) choices[pair.Key] = pair.Value;
            foreach (var mapping in mapped)
                if (!Directory.Exists(mapping.Key.Folder) && mapping.Value.Count > 1) choices[mapping.Key] = mapping.Value;
            int originalCount = working.Count;
            ModelLibraryBinding.AddUnassigned(working, found, rosterTable);
            additions.AddRange(working.Skip(originalCount));

            // Membership in this snapshot replaces Entries x inventory tree/path
            // checks on every list refresh. Model selection still verifies files.
            foreach (ModelCandidate model in found)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Directory.Exists(model.Folder)) continue;
                string path = FullPath(model.Folder);
                while (path != null && indexedFolders.Add(path))
                {
                    DirectoryInfo parent = Directory.GetParent(path);
                    path = parent == null ? null : FullPath(parent.FullName);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            prepared = true;
        }

        public bool Commit(List<RosterEntry> current, string currentTable,
            out Dictionary<RosterEntry, List<ModelCandidate>> currentChoices, out HashSet<string> readyFolders)
        {
            currentChoices = new Dictionary<RosterEntry, List<ModelCandidate>>();
            readyFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!prepared || !ReferenceEquals(current, originalList)
                || !string.Equals(rosterTable, currentTable, StringComparison.OrdinalIgnoreCase)) return false;
            var present = new HashSet<RosterEntry>(current);
            foreach (Row row in rows)
            {
                if (!present.Contains(row.Original) || !Same(row.Original, row.Before)) continue;
                row.Original.Folder = row.Working.Folder;
                row.Original.SourceId = row.Working.SourceId;
                row.Original.RosterTable = row.Working.RosterTable;
                row.Original.Source = row.Working.Source;
                row.Original.IsMod = row.Working.IsMod;
                List<ModelCandidate> available;
                if (choices.TryGetValue(row.Working, out available)) currentChoices[row.Original] = available;
            }
            var represented = new HashSet<string>(current.Select(entry => FullPath(entry == null ? null : entry.Folder))
                .Where(path => path != null), StringComparer.OrdinalIgnoreCase);
            foreach (RosterEntry addition in additions)
            {
                string path = FullPath(addition.Folder);
                if (path != null && represented.Add(path)) current.Add(Copy(addition));
            }
            foreach (RosterEntry entry in current)
            {
                string path = FullPath(entry == null ? null : entry.Folder);
                if (path != null && indexedFolders.Contains(path)) readyFolders.Add(path);
            }
            return true;
        }

        private static RosterEntry Copy(RosterEntry entry)
        {
            return new RosterEntry { Id = entry.Id, Name = entry.Name, Folder = entry.Folder, RosterTable = entry.RosterTable,
                SourceId = entry.SourceId, Source = entry.Source, IsMod = entry.IsMod, StockFolder = entry.StockFolder };
        }

        private static bool Same(RosterEntry first, RosterEntry second)
        {
            return first.Id == second.Id && first.Name == second.Name && first.Folder == second.Folder
                && first.RosterTable == second.RosterTable && first.SourceId == second.SourceId
                && first.Source == second.Source && first.IsMod == second.IsMod && first.StockFolder == second.StockFolder;
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
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException) { return null; }
        }
    }
}
