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
        public static void BindImportedModels(IEnumerable<RosterEntry> imported, IEnumerable<RosterEntry> current, string table)
        {
            if (string.IsNullOrWhiteSpace(table)) return;
            var models = current.Where(e => SameTable(e.RosterTable, table)).ToList();
            foreach (RosterEntry entry in imported)
            {
                if (string.IsNullOrWhiteSpace(entry.RosterTable)) entry.RosterTable = table;
                if (!SameTable(entry.RosterTable, table)) continue;
                List<RosterEntry> matches = models.Where(e => e.Id == entry.Id).ToList();
                if (matches.Count != 1) continue; // A JSON slot cannot select between different models by itself.
                RosterEntry model = matches[0];
                if (string.IsNullOrWhiteSpace(entry.Folder) && Directory.Exists(model.Folder)) entry.Folder = model.Folder;
                if (entry.SourceId == 0) entry.SourceId = model.SourceId;
                if (entry.StockFolder == null) entry.StockFolder = model.StockFolder;
                if (entry.Source == null) entry.Source = model.Source;
                entry.IsMod = entry.IsMod || model.IsMod;
            }
        }

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
                if (!SameTable(entry.RosterTable, table)) continue;
                // Earlier builds saved folder prefixes as roster IDs. Replace those
                // directory-derived rows with the freshly decoded mapping and SDB name.
                if (entry.SourceId == 0 && entry.Source == null && !string.IsNullOrWhiteSpace(entry.Folder)
                    && entries.Any(e => !string.IsNullOrWhiteSpace(e.Folder)
                        && string.Equals(Path.GetFullPath(e.Folder), Path.GetFullPath(entry.Folder), StringComparison.OrdinalIgnoreCase))) continue;
                RosterEntry fresh = entries.FirstOrDefault(e => string.Equals(Identity(e), Identity(entry), StringComparison.OrdinalIgnoreCase));
                if (fresh != null && string.IsNullOrWhiteSpace(fresh.Folder) && Directory.Exists(entry.Folder))
                {
                    fresh.Folder = entry.Folder;
                    fresh.SourceId = entry.SourceId;
                    fresh.IsMod = entry.IsMod;
                    fresh.Source = entry.Source;
                }
                if (identities.Add(Identity(entry))) entries.Add(entry);
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
            int? physical = CharacterFolders.PhysicalId(selectedFolder);
            if (physical.HasValue) entry.SourceId = physical.Value;
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
