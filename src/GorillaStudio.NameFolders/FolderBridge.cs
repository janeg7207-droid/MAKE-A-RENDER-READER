using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        internal static string StorageDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GorillaStudio", "NameFolders");
            }
        }

        // Called only when a character is loaded. Sign-in, rendering and installation remain in the original application.
        public static string PrepareFolder(string folder)
        {
            try
            {
                if (string.Equals(preparedForLoad, folder, StringComparison.OrdinalIgnoreCase))
                {
                    preparedForLoad = null;
                    return folder;
                }
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    throw new DirectoryNotFoundException("Choose an existing wrestler folder.");
                string source = DirectoryLinks.Resolve(folder);
                string baseName = new DirectoryInfo(source).Name;
                RosterSessionData session = RosterSession.Open(source, ReadRoster());
                List<RosterEntry> entries = session.Entries;
                int? existing = FolderNames.ExistingId(baseName);
                if (session.DiscoveryTable == null && existing.HasValue && !entries.Exists(e => e.Id == existing.Value
                    && string.Equals(e.Name, FolderNames.DisplayName(baseName), StringComparison.OrdinalIgnoreCase)))
                    entries.Add(new RosterEntry { Id = existing.Value, Name = FolderNames.DisplayName(baseName), Folder = source });

                return ChooseRosterModel(source, session);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Gorilla Studio — load wrestler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        internal static string PrepareRoster(string discoveryRoot, string rosterFile)
        {
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rosters", "Discovery_roster.json");
            RosterSessionData session = RosterLaunch.Open(discoveryRoot, ReadRoster(), rosterFile, bundled);
            string source = RosterAccess.RootFor(session.DiscoveryTable) ?? RosterAccess.RootFor(discoveryRoot)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            return ChooseRosterModel(source, session, rosterFile);
        }

        private static string ChooseRosterModel(string source, RosterSessionData session, string rosterFile = null)
        {
                using (var picker = new WrestlerPicker(source, session.Entries, session.ChooseCharacterFolder, rosterFile))
                {
                    if (picker.ShowDialog() != DialogResult.OK || picker.SelectedEntry == null) return null;
                    source = DirectoryLinks.Resolve(picker.SelectedFolder);
                    CharacterFolderPlan plan = CharacterFolders.Plan(source, picker.SelectedEntry);
                    int physicalId = CharacterFolders.PhysicalId(source) ?? (picker.SelectedEntry.SourceId > 0
                        ? picker.SelectedEntry.SourceId : picker.SelectedEntry.Id);
                    DirectAttirePlan attire = AutomaticAttire.Plan(source, physicalId);
                    Dictionary<ulong, string> pictures = PrepareRenderImages(plan);
                    string alias;
                    if (plan.UseOriginal && !attire.WrapDirectModel && pictures.Count == 0) alias = plan.SourceFolder;
                    else
                    {
                        // One model may be selected against different mod rosters in different sessions.
                        string root = Path.Combine(StorageDirectory, "links", plan.CacheKey
                            + (attire.WrapDirectModel ? "-" + attire.CacheSuffix : "")
                            + (pictures.Count > 0 ? "-pictures-v1" : ""));
                        string characters = Path.Combine(root, "Characters");
                        Directory.CreateDirectory(characters);
                        if (pictures.Count > 0)
                            DiscoveryRenderOverlay.Prepare(root, Path.Combine(plan.RosterDirectory, "CharProfileTable.jsfb"), pictures);
                        else if (plan.RosterDirectory != null) DirectoryLinks.EnsureJunction(Path.Combine(root, "Roster"), plan.RosterDirectory);
                        if (plan.DataRoot != null)
                        {
                            foreach (string peer in new[] { "CAS", "UI" })
                            {
                                if (pictures.Count > 0 && peer == "UI") continue;
                                string target = Path.Combine(plan.DataRoot, peer);
                                if (Directory.Exists(target)) DirectoryLinks.EnsureJunction(Path.Combine(root, peer), target);
                            }
                        }
                        alias = Path.Combine(characters, plan.AliasName);
                        if (attire.WrapDirectModel)
                        {
                            Directory.CreateDirectory(alias);
                            if ((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0)
                                throw new IOException("A different model link already occupies this compatibility folder.");
                            DirectoryLinks.EnsureJunction(Path.Combine(alias, "Default_Attire"), plan.SourceFolder);
                            if (attire.BaseModelFolder != null)
                                DirectoryLinks.EnsureJunction(Path.Combine(alias, "BaseModel"), attire.BaseModelFolder);
                        }
                        else DirectoryLinks.EnsureJunction(alias, plan.SourceFolder);
                    }
                    SaveRoster(picker.Entries);
                    if (session.ChooseCharacterFolder && !string.IsNullOrWhiteSpace(picker.CurrentRosterFile))
                        RememberRosterFile(picker.CurrentRosterFile);
                    lastDiscoveryRoot = plan.DataRoot;
                    RememberRenderTable(alias, picker.SelectedEntry.RosterTable);
                    RegisterSelection(alias, picker.SelectedEntry);
                    return alias;
                }
        }

        private static string RememberedRosterFile()
        {
            try
            {
                string path = Path.Combine(StorageDirectory, "roster-source.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void RememberRosterFile(string rosterFile)
        {
            Directory.CreateDirectory(StorageDirectory);
            string path = Path.Combine(StorageDirectory, "roster-source.txt");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Path.GetFullPath(rosterFile), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static List<RosterEntry> ReadRoster()
        {
            string path = Path.Combine(StorageDirectory, "roster.json");
            if (!File.Exists(path)) return new List<RosterEntry>();
            try { return RosterImport.LoadFile(path); }
            catch (Exception error)
            {
                MessageBox.Show("The saved roster could not be loaded. You can import it again in the picker.\n\n" + error.Message,
                    "Gorilla Studio — roster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return new List<RosterEntry>();
            }
        }

        internal static void SaveRoster(List<RosterEntry> entries)
        {
            Directory.CreateDirectory(StorageDirectory);
            string path = Path.Combine(StorageDirectory, "roster.json");
            string temp = Path.Combine(StorageDirectory, "roster-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                File.WriteAllText(temp, json.Serialize(entries.Where(e => e.Id > 0).ToList()), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
