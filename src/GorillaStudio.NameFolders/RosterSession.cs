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
        public List<string> Warnings = new List<string>();
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
                bool nameBasedModel = string.Equals(model.Source, "Found in models folder", StringComparison.OrdinalIgnoreCase)
                    || (model.Source != null && model.Source.StartsWith("Chosen", StringComparison.OrdinalIgnoreCase));
                if (nameBasedModel && string.IsNullOrWhiteSpace(model.StockFolder)
                    && !string.Equals((entry.Name ?? "").Trim(), (model.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(entry.Folder) && Directory.Exists(model.Folder)) entry.Folder = model.Folder;
                if (entry.SourceId == 0) entry.SourceId = model.SourceId;
                if (entry.StockFolder == null) entry.StockFolder = model.StockFolder;
                if (entry.Source == null) entry.Source = model.Source;
                entry.IsMod = entry.IsMod || model.IsMod;
            }
        }

        public static List<RosterEntry> Refresh(string rosterFile, IEnumerable<RosterEntry> saved, string rosterTable = null)
        {
            return RefreshCore(rosterFile, saved, rosterTable, false, null);
        }

        public static List<RosterEntry> RefreshQuick(string rosterFile, IEnumerable<RosterEntry> saved,
            string rosterTable = null, ICollection<string> warnings = null)
        {
            return RefreshCore(rosterFile, saved, rosterTable, true, warnings);
        }

        private static List<RosterEntry> RefreshCore(string rosterFile, IEnumerable<RosterEntry> saved,
            string rosterTable, bool quick, ICollection<string> warnings)
        {
            List<RosterEntry> entries = quick ? RosterImport.LoadFileQuick(rosterFile, warnings) : RosterImport.LoadFile(rosterFile);
            var remembered = quick ? QuickRemembered(saved) : saved == null ? new List<RosterEntry>() : saved.Where(e => e != null).ToList();
            if (string.Equals(Path.GetExtension(rosterFile), ".jsfb", StringComparison.OrdinalIgnoreCase))
                return MergeDiscovery(entries, Path.GetFullPath(rosterFile), remembered, quick);
            BindImportedModels(entries, remembered, rosterTable);
            return entries;
        }

        public static RosterSessionData Open(string source, IEnumerable<RosterEntry> saved)
        {
            return OpenCore(source, saved, false);
        }

        public static RosterSessionData OpenQuick(string source, IEnumerable<RosterEntry> saved)
        {
            return OpenCore(source, saved, true);
        }

        private static RosterSessionData OpenCore(string source, IEnumerable<RosterEntry> saved, bool quick)
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

            var remembered = quick ? QuickRemembered(saved) : saved == null ? new List<RosterEntry>() : saved.Where(e => e != null).ToList();
            if (table == null)
                return new RosterSessionData { Entries = remembered, ChooseCharacterFolder = false, DiscoveryTable = null };

            var warnings = new List<string>();
            List<RosterEntry> imported = quick ? RosterImport.LoadFileQuick(table, warnings) : RosterImport.LoadFile(table);
            List<RosterEntry> entries = MergeDiscovery(imported, table, remembered, quick);
            return new RosterSessionData { Entries = entries, ChooseCharacterFolder = chooseCharacterFolder,
                DiscoveryTable = Path.GetFullPath(table), Warnings = warnings };
        }

        private static List<RosterEntry> MergeDiscovery(List<RosterEntry> entries, string table, List<RosterEntry> remembered, bool quick = false)
        {
            var identities = new HashSet<string>(entries.Select(Identity), StringComparer.OrdinalIgnoreCase);
            var freshById = entries.Where(e => e.Id > 0).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.ToList());
            var sameRoster = new List<RosterEntry>();
            foreach (RosterEntry entry in remembered)
            {
                // Never bring a different mod roster into this Discovery directory.
                if (!SameTable(entry.RosterTable, table)) continue;
                // Earlier builds saved folder prefixes as roster IDs. Replace those
                // directory-derived rows with the freshly decoded mapping and SDB name.
                if (entry.SourceId == 0 && entry.Source == null && !string.IsNullOrWhiteSpace(entry.Folder)
                    && entries.Any(e => !string.IsNullOrWhiteSpace(e.Folder)
                        && string.Equals(Path.GetFullPath(e.Folder), Path.GetFullPath(entry.Folder), StringComparison.OrdinalIgnoreCase))) continue;
                if (string.Equals(entry.Source, "Folder ID", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(entry.Folder))
                {
                    // Earlier imports treated physical folder prefixes as logical
                    // roster IDs. Do not restore that guessed association after a
                    // fresh scan leaves the model unassigned or maps it elsewhere.
                    List<RosterEntry> physical = entries.Where(e => !string.IsNullOrWhiteSpace(e.Folder)
                        && string.Equals(Path.GetFullPath(e.Folder), Path.GetFullPath(entry.Folder), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (physical.Count > 0 && !physical.Any(e => e.Id > 0 && e.Id == entry.Id)) continue;
                }
                sameRoster.Add(entry);
            }
            var rememberedBindings = sameRoster.Where(e => Directory.Exists(e.Folder))
                .GroupBy(Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.GroupBy(BindingIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(binding => binding.First()).ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (RosterEntry entry in sameRoster)
            {
                List<RosterEntry> freshMatches;
                if (freshById.TryGetValue(entry.Id, out freshMatches))
                {
                    // Current profiles and SDB names own existing logical IDs. A renamed
                    // slot must not keep an obsolete row or silently reuse its old model.
                    List<RosterEntry> bindings;
                    RosterEntry fresh = freshMatches.Count == 1 ? freshMatches[0] : null;
                    if (fresh != null && string.Equals(Identity(fresh), Identity(entry), StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(fresh.Folder)
                        && rememberedBindings.TryGetValue(Identity(entry), out bindings) && bindings.Count == 1)
                    {
                        RosterEntry binding = bindings[0];
                        fresh.Folder = binding.Folder;
                        fresh.SourceId = binding.SourceId > 0 ? binding.SourceId
                            : quick ? fresh.SourceId : CharacterFolders.PhysicalId(binding.Folder) ?? fresh.SourceId;
                        fresh.IsMod = binding.IsMod;
                        fresh.Source = binding.Source;
                    }
                    continue;
                }
                if (identities.Add(Identity(entry))) entries.Add(entry);
            }
            return entries;
        }

        // Inferred model paths are evidence from an older inventory. A quick
        // metadata read must not present them as current after profiles/mappings
        // change. Keep row membership, and let the fresh background index bind
        // models again. Explicit user choices still require the same roster/name.
        internal static List<RosterEntry> QuickRemembered(IEnumerable<RosterEntry> saved)
        {
            var result = new List<RosterEntry>();
            foreach (RosterEntry entry in saved ?? Enumerable.Empty<RosterEntry>())
            {
                if (entry == null) continue;
                bool chosen = entry.Source != null && entry.Source.StartsWith("Chosen", StringComparison.OrdinalIgnoreCase);
                result.Add(new RosterEntry { Id = entry.Id, Name = entry.Name, RosterTable = entry.RosterTable,
                    Folder = chosen ? entry.Folder : null, SourceId = chosen ? entry.SourceId : 0,
                    // A stock association is derived from the previous mapping,
                    // even when the local folder was explicitly chosen. Fresh
                    // metadata supplies it; it must not bypass a renamed slot's
                    // name check when importing JSON.
                    StockFolder = null, Source = entry.Source, IsMod = chosen && entry.IsMod });
            }
            return result;
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

        private static string BindingIdentity(RosterEntry entry)
        {
            return Path.GetFullPath(entry.Folder) + "\0" + entry.SourceId.ToString(CultureInfo.InvariantCulture);
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
