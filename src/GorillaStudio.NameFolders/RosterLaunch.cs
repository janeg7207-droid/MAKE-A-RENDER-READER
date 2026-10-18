using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    /// <summary>Opens the roster before selecting a wrestler or locating its model.</summary>
    public static class RosterLaunch
    {
        public static RosterSessionData Open(string discoveryRoot, IEnumerable<RosterEntry> saved,
            string rosterFile, string bundledRosterFile)
        {
            // Work on copies: opening a picker must not change a different remembered
            // roster, and cancelling it must not modify any saved model choices.
            List<RosterEntry> remembered = saved == null ? new List<RosterEntry>()
                : saved.Where(ValidEntry).Select(Copy).ToList();

            // An explicitly selected JSON file owns its current names and membership.
            // Reload it on every opening, including additions made since the last run.
            if (!string.IsNullOrWhiteSpace(rosterFile) && File.Exists(rosterFile))
            {
                string currentRoot = RosterAccess.RootFor(discoveryRoot);
                string table = currentRoot == null ? CommonRosterTable(remembered)
                    : CharacterFolders.FindDiscoveryTable(currentRoot);
                List<RosterEntry> entries = ReadCurrent(rosterFile, remembered, table);
                if (string.Equals(Path.GetExtension(rosterFile), ".jsfb", StringComparison.OrdinalIgnoreCase))
                    table = Path.GetFullPath(rosterFile);
                else
                {
                    BindWithoutDiscovery(entries, remembered);
                    table = CommonRosterTable(entries);
                }
                return Session(entries, table);
            }

            // A missing remembered file is recoverable. A discovered but damaged table
            // still reports its error instead of silently showing an obsolete roster.
            string root = RosterAccess.RootFor(discoveryRoot);
            if (root != null)
            {
                RosterSessionData session = RosterSession.Open(root, remembered);
                session.ChooseCharacterFolder = true;
                return session;
            }

            if (remembered.Count != 0)
                return Session(remembered, CommonRosterTable(remembered));

            if (!string.IsNullOrWhiteSpace(bundledRosterFile) && File.Exists(bundledRosterFile))
                return Session(ReadCurrent(bundledRosterFile, remembered, null), null);

            // The empty picker can offer JSON/JSFB import without requiring the user
            // to locate a character model before they can even choose a wrestler.
            return Session(new List<RosterEntry>(), null);
        }

        public static string CommonRosterTable(IEnumerable<RosterEntry> entries)
        {
            string common = null;
            if (entries == null) return null;
            foreach (RosterEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.RosterTable)) continue;
                string path;
                try { path = Path.GetFullPath(entry.RosterTable); }
                catch (ArgumentException) { return null; }
                catch (NotSupportedException) { return null; }
                catch (PathTooLongException) { return null; }
                if (common != null && !string.Equals(common, path, StringComparison.OrdinalIgnoreCase)) return null;
                common = path;
            }
            return common;
        }

        private static List<RosterEntry> ReadCurrent(string file, List<RosterEntry> saved, string table)
        {
            try { return RosterSession.Refresh(file, saved, table); }
            catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                throw new InvalidDataException("Could not open the roster '" + file
                    + "'. Load a valid JSON roster or CharProfileTable.jsfb with its companion Discovery folders.\n\n"
                    + error.Message, error);
            }
        }

        private static void BindWithoutDiscovery(IEnumerable<RosterEntry> entries, List<RosterEntry> saved)
        {
            foreach (RosterEntry entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.RosterTable)) continue;
                // With no Discovery context, only an exact remembered name and ID
                // can reuse a model. A renamed JSON slot cannot inherit another
                // wrestler's old model merely because its numerical ID is unchanged.
                List<RosterEntry> matches = saved.Where(model => string.IsNullOrWhiteSpace(model.RosterTable)
                    && model.Id == entry.Id
                    && string.Equals(model.Name.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(model.Folder)).ToList();
                if (matches.Count != 1) continue;
                RosterEntry match = matches[0];
                if (string.IsNullOrWhiteSpace(entry.Folder)) entry.Folder = match.Folder;
                if (entry.SourceId == 0) entry.SourceId = match.SourceId;
                if (entry.Source == null) entry.Source = match.Source;
                if (entry.StockFolder == null) entry.StockFolder = match.StockFolder;
                entry.IsMod = entry.IsMod || match.IsMod;
            }
        }

        private static bool ValidEntry(RosterEntry entry)
        {
            return entry != null && entry.Id >= 1 && entry.Id <= 65535 && !string.IsNullOrWhiteSpace(entry.Name);
        }

        private static RosterEntry Copy(RosterEntry entry)
        {
            return new RosterEntry
            {
                Id = entry.Id, Name = entry.Name, Folder = entry.Folder, RosterTable = entry.RosterTable,
                SourceId = entry.SourceId, IsMod = entry.IsMod, Source = entry.Source, StockFolder = entry.StockFolder
            };
        }

        private static RosterSessionData Session(List<RosterEntry> entries, string table)
        {
            return new RosterSessionData { Entries = entries, ChooseCharacterFolder = true, DiscoveryTable = table };
        }
    }
}
