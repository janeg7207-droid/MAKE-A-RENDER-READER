using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    /// <summary>Keeps every active Discovery costume visible without inventing an available model.</summary>
    public static class DiscoveryAttireDisplay
    {
        public static DiscoveryAttireSource DefaultSource(DiscoveryAttireCatalogResult catalog, string selectedFolder, bool explicitSource)
        {
            if (catalog == null || catalog.Sources.Count == 0 || explicitSource) return null;
            string selected = Path.GetFullPath(selectedFolder).TrimEnd('\\', '/');
            if (catalog.Sources.Any(item => string.Equals(Path.GetFullPath(item.SourceFolder).TrimEnd('\\', '/'), selected, StringComparison.OrdinalIgnoreCase)))
                return null;
            var defaults = catalog.Sources.Where(item => item.IsDefault)
                .GroupBy(item => Path.GetFullPath(item.SourceFolder).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase).ToList();
            if (defaults.Count != 1)
                throw new InvalidDataException("The selected inferred model is not an active Discovery attire, and its active default model is missing or ambiguous. Choose an active model or explicitly select a character source.");
            return defaults[0].First();
        }

        public static AlternateAttire LocalDefault(string alias, string source, IEnumerable<AlternateAttire> local)
        {
            return (local ?? new AlternateAttire[0]).Where(item => item != null && item.IsAvailable
                && DiscoveryAttireOptions.IsLocalChoice(alias, source, item.RelativePath))
                .OrderBy(item => item.IsDefault ? 0 : 1).FirstOrDefault();
        }

        public static string ReferenceKey(DiscoveryAttireReference reference)
        {
            if (reference == null) throw new ArgumentNullException("reference");
            return reference.LogicalId.ToString(CultureInfo.InvariantCulture) + ":" + reference.CostumeKey.ToString(CultureInfo.InvariantCulture)
                + ":" + reference.MappingId.ToString(CultureInfo.InvariantCulture) + ":" + reference.CostumeIndex.ToString(CultureInfo.InvariantCulture);
        }

        public static IReadOnlyList<AlternateAttire> Merge(IEnumerable<AlternateAttire> available, DiscoveryAttireCatalogResult catalog)
        {
            var models = (available ?? new AlternateAttire[0]).Where(item => item != null && item.IsAvailable).ToList();
            if (catalog == null || catalog.References.Count == 0) return models.AsReadOnly();
            var result = new List<AlternateAttire>();
            var used = new HashSet<AlternateAttire>();
            foreach (DiscoveryAttireReference reference in catalog.References)
            {
                string key = ReferenceKey(reference);
                List<AlternateAttire> matching = models.Where(item => item.DiscoveryKey == key && item.IsAvailable).ToList();
                if (matching.Count > 0)
                {
                    foreach (AlternateAttire item in matching) if (used.Add(item)) result.Add(item);
                    continue;
                }
                string reason = reference.Issue;
                if (string.IsNullOrWhiteSpace(reason)) reason = reference.ModelHash == 0
                    ? "This active Discovery attire has no usable model mapping."
                    : "Its exact mapped MCD was not found in the indexed character sources. Refresh after adding its model location.";
                result.Add(new AlternateAttire { DiscoveryKey = key, DiscoveryModelHash = reference.ModelHash,
                    DisplayName = reference.DisplayName + (reference.IsDefault ? " (Discovery default)" : "") + " · model unavailable",
                    IsDefault = reference.IsDefault, UnavailableReason = reason });
            }
            foreach (AlternateAttire item in models) if (used.Add(item)) result.Add(item);
            return result.AsReadOnly();
        }
    }
}
