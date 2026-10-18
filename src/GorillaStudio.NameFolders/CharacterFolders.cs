using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    public sealed class CharacterFolderPlan
    {
        public string SourceFolder { get; set; }
        public string AliasName { get; set; }
        public string RosterDirectory { get; set; }
        public string DataRoot { get; set; }
        public string CacheKey { get; set; }
        public bool UseOriginal { get; set; }
    }

    /// <summary>Plans compatibility paths without creating links or changing character data.</summary>
    public static class CharacterFolders
    {
        public static bool HasAmbiguousChildren(string folder)
        {
            return Directory.Exists(folder) && !PhysicalId(folder).HasValue
                && Directory.GetDirectories(folder).Select(p => FolderNames.ExistingId(new DirectoryInfo(p).Name))
                    .Where(value => value.HasValue).Select(value => value.Value).Distinct().Take(2).Count() > 1;
        }

        public static int? PhysicalId(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            string name = new DirectoryInfo(folder).Name;
            int? prefix = FolderNames.ExistingId(name);
            if (prefix.HasValue) return prefix;
            Match placeholder = Regex.Match(name, @"^placeholder_(\d+)_A\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int id;
            if (placeholder.Success && int.TryParse(placeholder.Groups[1].Value, out id) && id >= 1 && id <= 65535) return id;
            int[] children = Directory.GetDirectories(folder).Select(p => FolderNames.ExistingId(new DirectoryInfo(p).Name))
                .Where(value => value.HasValue).Select(value => value.Value).Distinct().Take(2).ToArray();
            return children.Length == 1 ? (int?)children[0] : null;
        }

        public static string FindDiscoveryTable(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) return null;
            var directory = new DirectoryInfo(source);
            string root = directory.FullName;
            if (string.Equals(directory.Name, "Characters", StringComparison.OrdinalIgnoreCase)
                || string.Equals(directory.Name, "Roster", StringComparison.OrdinalIgnoreCase))
                root = directory.Parent == null ? root : directory.Parent.FullName;
            string roster = ChildDirectory(root, "Roster");
            if (roster == null || ChildDirectory(root, "Characters") == null) return null;
            return Directory.GetFiles(roster).FirstOrDefault(path => string.Equals(Path.GetFileName(path), "CharProfileTable.jsfb", StringComparison.OrdinalIgnoreCase));
        }

        public static CharacterFolderPlan Plan(string source, RosterEntry selected)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                throw new DirectoryNotFoundException("Choose an existing wrestler folder.");
            if (selected == null) throw new ArgumentNullException("selected", "Choose a wrestler.");
            string fullSource = new DirectoryInfo(source).FullName;
            if (HasAmbiguousChildren(fullSource))
                throw new InvalidDataException("This folder contains different model IDs. Choose the specific numbered character or attire folder to load.");
            string sourceRoot = InferDataRoot(fullSource);
            string rosterDirectory = !string.IsNullOrWhiteSpace(selected.RosterTable)
                && File.Exists(selected.RosterTable)
                && string.Equals(Path.GetFileName(selected.RosterTable), "CharProfileTable.jsfb", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(Path.GetFullPath(selected.RosterTable)) : null;
            string platformRoot = null;
            if (rosterDirectory != null)
            {
                var tableParent = new DirectoryInfo(rosterDirectory);
                platformRoot = string.Equals(tableParent.Name, "Roster", StringComparison.OrdinalIgnoreCase)
                    ? (tableParent.Parent == null ? null : tableParent.Parent.FullName) : tableParent.FullName;
            }
            platformRoot = platformRoot ?? InferDataRoot(selected.Folder) ?? sourceRoot;
            if (rosterDirectory == null && platformRoot != null)
                rosterDirectory = ChildDirectory(platformRoot, "Roster");
            string sourceRoster = sourceRoot == null ? null : ChildDirectory(sourceRoot, "Roster");
            // Discovery can place one model over a different roster slot. The original
            // engine needs the physical ID for stock-body extraction; SetTarget carries
            // the selected logical roster ID separately.
            int sourceId = PhysicalId(fullSource) ?? (selected.SourceId > 0 ? selected.SourceId : selected.Id);
            string alias = FolderNames.AliasName(sourceId, selected.Name);
            return new CharacterFolderPlan
            {
                SourceFolder = fullSource,
                AliasName = alias,
                RosterDirectory = rosterDirectory,
                DataRoot = platformRoot,
                CacheKey = FolderNames.SourceKey(fullSource + "\n" + (rosterDirectory ?? "")),
                UseOriginal = string.Equals(new DirectoryInfo(fullSource).Name, alias, StringComparison.Ordinal)
                    && (rosterDirectory == null || string.Equals(sourceRoster, rosterDirectory, StringComparison.OrdinalIgnoreCase))
            };
        }

        private static string InferDataRoot(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            DirectoryInfo parent = new DirectoryInfo(folder).Parent;
            DirectoryInfo root = parent != null && string.Equals(parent.Name, "Characters", StringComparison.OrdinalIgnoreCase)
                ? parent.Parent : parent;
            return root != null && ChildDirectory(root.FullName, "Roster") != null ? root.FullName : null;
        }

        private static string ChildDirectory(string root, string name)
        {
            return Directory.GetDirectories(root).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
