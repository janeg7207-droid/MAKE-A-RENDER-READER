using System;
using System.Collections.Generic;
using System.IO;

namespace GorillaStudio.NameFolders
{
    /// <summary>Resolves an existing remembered Discovery location without opening a dialog.</summary>
    public static class RosterAccess
    {
        public static string FindRoot(string preferred, string setupFolder, IEnumerable<RosterEntry> saved)
        {
            string root = RootFor(preferred) ?? RootFor(setupFolder);
            if (root != null) return root;
            if (saved != null)
            {
                foreach (RosterEntry entry in saved)
                {
                    if (entry == null) continue;
                    root = RootFor(entry.RosterTable);
                    if (root != null) return root;
                }
            }
            return null;
        }

        public static string RootFor(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string source = path;
                if (File.Exists(source))
                {
                    if (!string.Equals(Path.GetFileName(source), "CharProfileTable.jsfb", StringComparison.OrdinalIgnoreCase))
                        return null;
                    source = Path.GetDirectoryName(Path.GetFullPath(source));
                }
                if (!Directory.Exists(source)) return null;
                string table = CharacterFolders.FindDiscoveryTable(source);
                if (table == null) return null;
                var containing = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(table)));
                return string.Equals(containing.Name, "Roster", StringComparison.OrdinalIgnoreCase)
                    ? (containing.Parent == null ? null : containing.Parent.FullName) : containing.FullName;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
