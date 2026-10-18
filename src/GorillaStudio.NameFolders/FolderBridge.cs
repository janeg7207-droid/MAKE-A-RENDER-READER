using System;
using System.Collections.Generic;
using System.IO;
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
                if (existing.HasValue && !entries.Exists(e => e.Id == existing.Value
                    && string.Equals(e.Name, FolderNames.DisplayName(baseName), StringComparison.OrdinalIgnoreCase)))
                    entries.Add(new RosterEntry { Id = existing.Value, Name = FolderNames.DisplayName(baseName), Folder = source });

                using (var picker = new WrestlerPicker(source, entries, session.ChooseCharacterFolder))
                {
                    if (picker.ShowDialog() != DialogResult.OK || picker.SelectedEntry == null) return null;
                    source = DirectoryLinks.Resolve(picker.SelectedFolder);
                    CharacterFolderPlan plan = CharacterFolders.Plan(source, picker.SelectedEntry);
                    string alias;
                    if (plan.UseOriginal) alias = plan.SourceFolder;
                    else
                    {
                        // One model may be selected against different mod rosters in different sessions.
                        string root = Path.Combine(StorageDirectory, "links", plan.CacheKey);
                        string characters = Path.Combine(root, "Characters");
                        Directory.CreateDirectory(characters);
                        if (plan.RosterDirectory != null) DirectoryLinks.EnsureJunction(Path.Combine(root, "Roster"), plan.RosterDirectory);
                        if (plan.DataRoot != null)
                        {
                            foreach (string peer in new[] { "CAS", "UI" })
                            {
                                string target = Path.Combine(plan.DataRoot, peer);
                                if (Directory.Exists(target)) DirectoryLinks.EnsureJunction(Path.Combine(root, peer), target);
                            }
                        }
                        alias = Path.Combine(characters, plan.AliasName);
                        DirectoryLinks.EnsureJunction(alias, plan.SourceFolder);
                    }
                    SaveRoster(picker.Entries);
                    lastDiscoveryRoot = plan.DataRoot;
                    return alias;
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Gorilla Studio — load wrestler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
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
                File.WriteAllText(temp, json.Serialize(entries), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
