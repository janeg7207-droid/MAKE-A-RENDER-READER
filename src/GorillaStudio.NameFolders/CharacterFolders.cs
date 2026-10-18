using System;
using System.IO;
using System.Linq;

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
            string alias = FolderNames.AliasName(selected.Id, selected.Name);
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
