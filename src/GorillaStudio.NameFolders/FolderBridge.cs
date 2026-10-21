using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
                RosterSessionData session = RosterSession.OpenQuick(source, ReadRoster());
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
            RosterSessionData session = RosterLaunch.OpenQuick(discoveryRoot, ReadRoster(), rosterFile, bundled);
            string source = RosterAccess.RootFor(session.DiscoveryTable) ?? RosterAccess.RootFor(discoveryRoot)
                ?? AppDomain.CurrentDomain.BaseDirectory;
            return ChooseRosterModel(source, session, rosterFile);
        }

        // The roster button is already asynchronous. Keep its folder scans, profile
        // parsing and picture conversion away from the dispatcher that owns the UI.
        internal static async Task<string> PrepareRosterAsync(string discoveryRoot, string rosterFile, Func<bool> isCurrent = null,
            Action<string> metadataReady = null)
        {
            string warning = null;
            var loaded = await Task.Run(() =>
            {
                string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rosters", "Discovery_roster.json");
                RosterSessionData session = RosterLaunch.OpenQuick(discoveryRoot, ReadRosterFile(out warning), rosterFile, bundled);
                string source = RosterAccess.RootFor(session.DiscoveryTable) ?? RosterAccess.RootFor(discoveryRoot)
                    ?? AppDomain.CurrentDomain.BaseDirectory;
                return new RosterModelSession { Source = source, Session = session };
            });
            if (isCurrent != null && !isCurrent()) return null;
            metadataReady?.Invoke(loaded.Source);
            ShowRosterWarning(warning);
            return await ChooseRosterModelAsync(loaded.Source, loaded.Session, rosterFile, isCurrent);
        }

        private sealed class RosterModelSession
        {
            internal string Source;
            internal RosterSessionData Session;
        }

        private sealed class RosterModelChoice
        {
            internal string Source;
            internal string RosterFile;
            internal RosterEntry Entry;
            internal List<RosterEntry> Entries;
            internal List<ModelCandidate> Models;
            internal List<string> ModelRoots;
            internal string ModelsContext;
            internal bool RememberRoster;
        }

        private sealed class PreparedRosterModel
        {
            internal RosterEntry Entry;
            internal CharacterFolderPlan Plan;
            internal Dictionary<ulong, string> Pictures;
            internal string Alias;
            internal List<ModelCandidate> Models;
            internal List<string> ModelRoots;
            internal string ModelsContext;
            internal DiscoveryAttireCatalogResult Attires;
        }

        private static string ChooseRosterModel(string source, RosterSessionData session, string rosterFile = null)
        {
            RosterModelChoice choice = PickRosterModel(source, session, rosterFile);
            return choice == null ? null : CommitRosterModel(PrepareRosterModel(choice));
        }

        private static async Task<string> ChooseRosterModelAsync(string source, RosterSessionData session,
            string rosterFile, Func<bool> isCurrent)
        {
            RosterModelChoice choice = PickRosterModel(source, session, rosterFile);
            if (choice == null || (isCurrent != null && !isCurrent())) return null;
            PreparedRosterModel prepared = await Task.Run(() => PrepareRosterModel(choice));
            if (isCurrent != null && !isCurrent()) return null;
            return CommitRosterModel(prepared);
        }

        private static RosterModelChoice PickRosterModel(string source, RosterSessionData session, string rosterFile)
        {
            using (var picker = new WrestlerPicker(source, session.Entries, session.ChooseCharacterFolder, rosterFile))
            {
                picker.SetMetadataWarnings(session.Warnings);
                DialogResult result = picker.ShowDialog();
                if (picker.DataRefreshed) InvalidateTauntContexts();
                if (result != DialogResult.OK || picker.SelectedEntry == null) return null;
                // Snapshot before disposing the picker; its canceled background model
                // scan must not mutate the row or saved roster while files are prepared.
                return new RosterModelChoice {
                    Source = picker.SelectedFolder, RosterFile = picker.CurrentRosterFile,
                    Entry = CopyRosterEntry(picker.SelectedEntry),
                    Entries = picker.Entries.Select(CopyRosterEntry).ToList(),
                    Models = picker.CurrentModelInventory,
                    ModelRoots = picker.CurrentModelSearchRoots,
                    ModelsContext = picker.CurrentModelsContext,
                    RememberRoster = session.ChooseCharacterFolder
                };
            }
        }

        private static RosterEntry CopyRosterEntry(RosterEntry entry)
        {
            return new RosterEntry { Id = entry.Id, Name = entry.Name, Folder = entry.Folder,
                RosterTable = entry.RosterTable, SourceId = entry.SourceId, IsMod = entry.IsMod,
                Source = entry.Source, StockFolder = entry.StockFolder };
        }

        private static PreparedRosterModel PrepareRosterModel(RosterModelChoice choice)
        {
            string source = DirectoryLinks.Resolve(choice.Source);
            int physicalId = CharacterFolders.PhysicalId(source) ?? (choice.Entry.SourceId > 0
                ? choice.Entry.SourceId : choice.Entry.Id);
            string narrowSource = source;
            source = AutomaticAttire.CharacterRoot(source, physicalId);
            if (!string.Equals(narrowSource, source, StringComparison.OrdinalIgnoreCase))
            {
                choice.Entry.Folder = source; choice.Entry.SourceId = physicalId;
                foreach (RosterEntry remembered in choice.Entries.Where(entry => entry.Id == choice.Entry.Id
                    && string.Equals(entry.RosterTable, choice.Entry.RosterTable, StringComparison.OrdinalIgnoreCase)))
                { remembered.Folder = source; remembered.SourceId = physicalId; }
            }
            var models = (choice.Models ?? new List<ModelCandidate>())
                .Where(model => model != null && !string.IsNullOrWhiteSpace(model.Folder)).ToList();
            if (!string.Equals(narrowSource, source, StringComparison.OrdinalIgnoreCase))
                models.RemoveAll(model => Path.GetFullPath(model.Folder).StartsWith(source.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (!models.Any(model => string.Equals(Path.GetFullPath(model.Folder), source, StringComparison.OrdinalIgnoreCase)))
                models.Add(new ModelCandidate { Folder = source, Name = choice.Entry.Name, SourceId = physicalId });
            DiscoveryAttireCatalogResult attires = DiscoveryAttireCatalog.Find(choice.Entry, models,
                choice.Entry.RosterTable, System.Threading.CancellationToken.None);
            ResolveDiscoveryAttireSources(attires, System.Threading.CancellationToken.None);
            bool explicitSource = !choice.RememberRoster || (choice.Entry.Source != null
                && choice.Entry.Source.StartsWith("Chosen", StringComparison.OrdinalIgnoreCase));
            DiscoveryAttireSource activeDefault = DiscoveryAttireDisplay.DefaultSource(attires, source, explicitSource);
            if (activeDefault != null)
            {
                // A mapping table can retain inactive costumes alongside the
                // profile's active list. Start from its proven default instead
                // of warming an inactive source selected by older folder logic.
                source = DirectoryLinks.Resolve(activeDefault.SourceFolder);
                physicalId = activeDefault.SourceId;
                choice.Entry.Folder = source; choice.Entry.SourceId = physicalId;
                foreach (RosterEntry remembered in choice.Entries.Where(entry => entry.Id == choice.Entry.Id
                    && string.Equals(entry.RosterTable, choice.Entry.RosterTable, StringComparison.OrdinalIgnoreCase)))
                { remembered.Folder = source; remembered.SourceId = physicalId; }
            }
            CharacterFolderPlan plan = CharacterFolders.Plan(source, choice.Entry);
            DirectAttirePlan attire = AutomaticAttire.Plan(source, physicalId);
            Dictionary<ulong, string> pictures = PrepareRenderImages(plan, choice.Entry.Id);
            string alias = PrepareCharacterAlias(plan, attire, pictures);
            SaveRoster(choice.Entries);
            if (choice.RememberRoster && !string.IsNullOrWhiteSpace(choice.RosterFile))
                RememberRosterFile(choice.RosterFile);
            return new PreparedRosterModel { Entry = choice.Entry, Plan = plan, Pictures = pictures, Alias = alias,
                Models = models, ModelRoots = choice.ModelRoots, ModelsContext = choice.ModelsContext, Attires = attires };
        }

        private static string CommitRosterModel(PreparedRosterModel prepared)
        {
            // These registries belong to the dispatcher. RegisterSelection also uses
            // thread-local state consumed by the original native SetFolder method.
            lastDiscoveryRoot = prepared.Plan.DataRoot;
            RememberRenderTable(prepared.Alias, prepared.Entry.RosterTable);
            RememberPreparedRender(prepared.Alias, prepared.Plan, prepared.Entry, prepared.Pictures);
            RegisterDiscoveryAttires(prepared.Alias, prepared.Entry, prepared.Models, prepared.Attires, prepared.Plan.SourceFolder, prepared.ModelRoots, prepared.ModelsContext);
            RegisterSelection(prepared.Alias, prepared.Entry);
            QueueModelDiscoveryTaunts(prepared.Alias, prepared.Plan.DataRoot);
            return prepared.Alias;
        }

        private static string PrepareCharacterAlias(CharacterFolderPlan plan, DirectAttirePlan attire, Dictionary<ulong, string> pictures)
        {
            bool pictureContext = plan.RosterDirectory != null;
            if (plan.UseOriginal && !attire.WrapDirectModel && !pictureContext) return plan.SourceFolder;
            string root = Path.Combine(StorageDirectory, "links", plan.CacheKey
                + (attire.WrapDirectModel ? "-" + attire.CacheSuffix : "") + (pictureContext ? "-pictures-v2" : ""));
            string characters = Path.Combine(root, "Characters");
            Directory.CreateDirectory(characters);
            if (pictureContext)
                DiscoveryRenderOverlay.Prepare(root, Path.Combine(plan.RosterDirectory, "CharProfileTable.jsfb"), pictures);
            if (plan.DataRoot != null)
                foreach (string peer in new[] { "CAS", "UI" })
                {
                    if (pictureContext && peer == "UI") continue;
                    string target = Path.Combine(plan.DataRoot, peer);
                    if (Directory.Exists(target)) DirectoryLinks.EnsureJunction(Path.Combine(root, peer), target);
                }
            string alias = Path.Combine(characters, plan.AliasName);
            if (attire.WrapDirectModel)
            {
                Directory.CreateDirectory(alias);
                if ((File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A different model link already occupies this compatibility folder.");
                DirectoryLinks.EnsureJunction(Path.Combine(alias, "Default_Attire"), plan.SourceFolder);
                RememberDirectAttireSource(alias, plan.SourceFolder);
                if (attire.BaseModelFolder != null) DirectoryLinks.EnsureJunction(Path.Combine(alias, "BaseModel"), attire.BaseModelFolder);
            }
            else DirectoryLinks.EnsureJunction(alias, plan.SourceFolder);
            return alias;
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
            string warning;
            List<RosterEntry> entries = ReadRosterFile(out warning);
            ShowRosterWarning(warning);
            return entries;
        }

        private static List<RosterEntry> ReadRosterFile(out string warning)
        {
            warning = null;
            string path = Path.Combine(StorageDirectory, "roster.json");
            if (!File.Exists(path)) return new List<RosterEntry>();
            try { return RosterImport.LoadFile(path); }
            catch (Exception error)
            {
                warning = error.Message;
                return new List<RosterEntry>();
            }
        }

        private static void ShowRosterWarning(string warning)
        {
            if (warning != null)
                MessageBox.Show("The saved roster could not be loaded. You can import it again in the picker.\n\n" + warning,
                    "Gorilla Studio — roster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
