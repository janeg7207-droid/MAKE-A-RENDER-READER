using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    public sealed class CompletedRenderVersionGroup
    {
        public int WrestlerId { get; internal set; }
        public string Name { get; internal set; }
        public List<CompletedRenderEntry> Entries { get; } = new List<CompletedRenderEntry>();
        public CompletedRenderEntry Suggested { get; internal set; }
    }

    public sealed class CompletedRenderVersionSelectionResult
    {
        public List<CompletedRenderEntry> Entries { get; } = new List<CompletedRenderEntry>();
        public List<CompletedRenderVersionGroup> Conflicts { get; } = new List<CompletedRenderVersionGroup>();
        public List<string> Failed { get; } = new List<string>();
    }

    /// <summary>
    /// Plans a one-movie-per-wrestler selection without changing checks, files or
    /// installation state. A newest suggestion is never an installation choice.
    /// The existing installer must still freshly validate the chosen movies.
    /// </summary>
    public static class CompletedRenderVersionSelection
    {
        private const int MaximumSelection = 4096;
        private static readonly StringComparer Paths = Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static CompletedRenderVersionSelectionResult Inspect(IEnumerable<CompletedRenderEntry> selected)
        {
            var result = new CompletedRenderVersionSelectionResult();
            try
            {
                var rows = (selected ?? Enumerable.Empty<CompletedRenderEntry>()).Take(MaximumSelection + 1).ToList();
                if (rows.Count > MaximumSelection)
                    throw new InvalidDataException("Choose no more than 4096 completed renders at once.");
                var locations = new HashSet<string>(Paths);
                foreach (CompletedRenderEntry entry in rows)
                {
                    if (entry == null || !entry.IsValid || entry.WrestlerId < 1 || entry.WrestlerId > 65535)
                        throw new InvalidDataException("A checked render is unavailable or has an invalid wrestler ID. Refresh the completed renders list.");
                    string path = Location(entry.Path);
                    if (!locations.Add(path))
                        throw new InvalidDataException("The same saved file is checked more than once. Refresh the completed renders list.");
                }
                foreach (var group in rows.GroupBy(entry => entry.WrestlerId).Where(group => group.Count() > 1).OrderBy(group => group.Key))
                {
                    var conflict = new CompletedRenderVersionGroup { WrestlerId = group.Key,
                        Name = group.Select(entry => entry.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                            ?? "Wrestler " + group.Key.ToString(CultureInfo.InvariantCulture) };
                    conflict.Entries.AddRange(group.OrderByDescending(entry => entry.LastWriteTimeUtc)
                        .ThenBy(entry => Location(entry.Path), Paths).ThenBy(entry => Location(entry.Path), StringComparer.Ordinal));
                    conflict.Suggested = conflict.Entries[0];
                    result.Conflicts.Add(conflict);
                }
                result.Entries.AddRange(rows);
            }
            catch (Exception error) when (Expected(error))
            {
                result.Entries.Clear(); result.Conflicts.Clear(); result.Failed.Add(error.Message);
            }
            return result;
        }

        public static CompletedRenderVersionSelectionResult Resolve(IEnumerable<CompletedRenderEntry> selected,
            IDictionary<int, string> chosenVersions)
        {
            CompletedRenderVersionSelectionResult result = Inspect(selected);
            if (result.Failed.Count != 0) return result;
            var rows = result.Entries.ToList(); result.Entries.Clear();
            try
            {
                var choices = chosenVersions == null ? new Dictionary<int, string>() : new Dictionary<int, string>(chosenVersions);
                var conflicts = result.Conflicts.ToDictionary(group => group.WrestlerId);
                if (choices.Keys.Any(id => !conflicts.ContainsKey(id)))
                    throw new InvalidDataException("A version choice no longer belongs to a checked duplicate wrestler. Reopen Choose versions.");
                var retained = new Dictionary<int, CompletedRenderEntry>();
                foreach (CompletedRenderVersionGroup group in result.Conflicts)
                {
                    string chosen;
                    if (!choices.TryGetValue(group.WrestlerId, out chosen) || string.IsNullOrWhiteSpace(chosen))
                        throw new InvalidDataException("Choose one checked version for " + group.Name + " (" + group.WrestlerId.ToString(CultureInfo.InvariantCulture) + ").");
                    string location = Location(chosen);
                    CompletedRenderEntry entry = group.Entries.SingleOrDefault(candidate => Paths.Equals(Location(candidate.Path), location));
                    if (entry == null)
                        throw new InvalidDataException("The chosen version is no longer checked for " + group.Name + " (" + group.WrestlerId.ToString(CultureInfo.InvariantCulture) + "). Reopen Choose versions.");
                    retained.Add(group.WrestlerId, entry);
                }
                foreach (CompletedRenderEntry entry in rows)
                {
                    CompletedRenderEntry chosen;
                    if (!retained.TryGetValue(entry.WrestlerId, out chosen) || ReferenceEquals(entry, chosen))
                        result.Entries.Add(entry);
                }
                result.Conflicts.Clear();
            }
            catch (Exception error) when (Expected(error))
            { result.Entries.Clear(); result.Failed.Add(error.Message); }
            return result;
        }

        private static string Location(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
                || !string.Equals(Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A checked version needs its full saved .gmr file path. Refresh the completed renders list.");
            return Path.GetFullPath(path);
        }

        private static bool Expected(Exception error)
        { return error is ArgumentException || error is InvalidOperationException || error is IOException || error is InvalidDataException || error is NotSupportedException
            || error is System.Security.SecurityException; }
    }
}
