using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    public sealed class RosterSessionData
    {
        public List<RosterEntry> Entries;
        public bool ChooseCharacterFolder;
        public string DiscoveryTable;
    }

    public static class RosterSession
    {
        public static RosterSessionData Open(string source, IEnumerable<RosterEntry> saved)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                throw new DirectoryNotFoundException("Choose an existing wrestler or Discovery folder.");
            string table = CharacterFolders.FindDiscoveryTable(source);
            bool chooseCharacterFolder = table != null;
            if (table == null)
            {
                DirectoryInfo parent = new DirectoryInfo(source).Parent;
                if (parent != null && string.Equals(parent.Name, "Characters", StringComparison.OrdinalIgnoreCase))
                    table = CharacterFolders.FindDiscoveryTable(parent.FullName);
            }

            var remembered = saved == null ? new List<RosterEntry>() : saved.Where(e => e != null).ToList();
            if (table == null)
                return new RosterSessionData { Entries = remembered, ChooseCharacterFolder = false, DiscoveryTable = null };

            List<RosterEntry> entries = RosterImport.LoadFile(table);
            var identities = new HashSet<string>(entries.Select(Identity), StringComparer.OrdinalIgnoreCase);
            foreach (RosterEntry entry in remembered)
            {
                // Never bring a different mod roster into this Discovery directory.
                // Fresh directory metadata wins over remembered entries with the same ID and name.
                if (SameTable(entry.RosterTable, table) && identities.Add(Identity(entry))) entries.Add(entry);
            }
            return new RosterSessionData { Entries = entries, ChooseCharacterFolder = chooseCharacterFolder, DiscoveryTable = Path.GetFullPath(table) };
        }

        public static void RememberSelection(RosterEntry entry, string selectedFolder, bool chooseCharacterFolder)
        {
            // Mapping a dropped model to a wrestler must not replace that wrestler's saved model folder.
            if (!chooseCharacterFolder) return;
            if (entry == null) throw new ArgumentNullException("entry");
            if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder))
                throw new DirectoryNotFoundException("Choose an existing character folder.");
            entry.Folder = Path.GetFullPath(selectedFolder);
        }

        private static string Identity(RosterEntry entry)
        {
            return entry.Id.ToString(CultureInfo.InvariantCulture) + "\0" + (entry.Name ?? "").Trim();
        }

        private static bool SameTable(string remembered, string current)
        {
            if (string.IsNullOrWhiteSpace(remembered)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(remembered), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
    }
}
