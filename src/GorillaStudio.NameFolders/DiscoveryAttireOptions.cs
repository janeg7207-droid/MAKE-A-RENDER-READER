using System;
using System.Collections.Generic;
using System.IO;

namespace GorillaStudio.NameFolders
{
    /// <summary>Combines proven mapped sources with the loaded source's manual choices.</summary>
    public static class DiscoveryAttireOptions
    {
        public static IReadOnlyList<AlternateAttire> Combine(string alias, string currentSource,
            IEnumerable<AlternateAttire> local, IEnumerable<AlternateAttire> mapped, bool authoritative = false)
        {
            var choices = new List<AlternateAttire>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AlternateAttire item in mapped ?? new AlternateAttire[0])
            {
                if (item == null || string.IsNullOrWhiteSpace(item.SourceFolder) || string.IsNullOrWhiteSpace(item.SourceRelativePath)) continue;
                string path = Key(item.SourceFolder, item.SourceRelativePath);
                seen.Add(path);
                if (references.Add(path + "\n" + (item.DiscoveryKey ?? ""))) choices.Add(item);
            }
            if (authoritative) return choices.AsReadOnly();
            foreach (AlternateAttire item in local ?? new AlternateAttire[0])
            {
                if (item == null || string.IsNullOrWhiteSpace(item.RelativePath)) continue;
                string sourceRelative = AlternateAttires.SourceRelativePath(alias, item.RelativePath);
                if (seen.Add(Key(currentSource, sourceRelative))) choices.Add(item);
            }
            return choices.AsReadOnly();
        }

        public static string Key(string source, string relative)
        {
            return (string.IsNullOrWhiteSpace(source) ? "" : Path.GetFullPath(source).TrimEnd('\\', '/'))
                + "\n" + (relative ?? "").Replace('\\', '/');
        }
    }
}
