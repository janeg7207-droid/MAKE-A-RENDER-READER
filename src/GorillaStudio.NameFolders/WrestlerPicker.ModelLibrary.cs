using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    internal sealed partial class WrestlerPicker
    {
        private readonly Button scanModels = new Button();
        private readonly Label modelsInfo = new Label();
        private List<string> modelRoots = new List<string>();
        private List<ModelCandidate> modelInventory = new List<ModelCandidate>();
        private HashSet<string> readyModelFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<RosterEntry, List<ModelCandidate>> modelChoices = new Dictionary<RosterEntry, List<ModelCandidate>>();
        private CancellationTokenSource modelScanCancellation;
        private Task modelScanTask = Task.CompletedTask;
        private int modelScanVersion;
        private bool modelSelectionBusy;
        private bool offeredModelLibrarySetup;
        internal List<string> CurrentModelSearchRoots
        { get { return SearchRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); } }
        private FlowLayoutPanel rosterActions;
        private readonly CancellationTokenSource modelLifetime = new CancellationTokenSource();

        private string ModelsContext { get { return currentRosterTable ?? Path.GetFullPath(sourceFolder); } }
        private string ModelsSettingsPath
        {
            get { return Path.Combine(FolderBridge.StorageDirectory, "models-" + FolderNames.SourceKey(ModelsContext.ToUpperInvariant()) + ".json"); }
        }

        private void InitializeModelLibrary(FlowLayoutPanel bar)
        {
            rosterActions = bar;
            scanModels.Text = "Set model library…";
            scanModels.AutoSize = true;
            scanModels.Padding = new Padding(8, 2, 8, 2);
            scanModels.Click += async (sender, args) => { if (!modelSelectionBusy) await AddModelsFolderAsync(); };
            bar.Controls.Add(scanModels);
            modelsInfo.AutoSize = true;
            modelsInfo.Margin = new Padding(10, 8, 0, 0);
            bar.Controls.Add(modelsInfo);
            Shown += (sender, args) => ResetModelLibrary();
            FormClosed += (sender, args) => modelLifetime.Cancel();
        }

        private void ResetModelLibrary()
        {
            modelRoots = new List<string>();
            try
            {
                if (File.Exists(ModelsSettingsPath))
                    modelRoots = new JavaScriptSerializer().Deserialize<List<string>>(File.ReadAllText(ModelsSettingsPath)) ?? modelRoots;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException) { }
            modelRoots = modelRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            modelScanTask = RefreshModelInventoryAsync();
        }

        private void SaveModelRoots()
        {
            Directory.CreateDirectory(FolderBridge.StorageDirectory);
            string path = ModelsSettingsPath;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(modelRoots), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private List<string> SearchRoots()
        {
            var roots = new List<string>(modelRoots);
            roots.AddRange(Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Folder)).Select(entry => entry.Folder));
            string data = currentRosterTable == null ? sourceFolder : DiscoveryProfiles.DataRoot(currentRosterTable);
            roots.Add(data);
            DirectoryInfo parent = Directory.GetParent(data);
            if (parent != null)
            {
                roots.Add(Path.Combine(parent.FullName, "BakeMe_Characters", "Characters"));
                roots.Add(Path.Combine(parent.FullName, "Characters"));
                roots.Add(Path.Combine(parent.FullName, "BakeMe", "Characters"));
                roots.Add(Path.Combine(parent.FullName, "discovery_data", "Characters"));
            }
            return roots;
        }

        private async Task RefreshModelInventoryAsync()
        {
            if (modelScanCancellation != null) modelScanCancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(modelLifetime.Token);
            modelScanCancellation = cancellation;
            int version = ++modelScanVersion;
            scanModels.Enabled = false;
            modelsInfo.Text = "Finding models for your roster…";
            try
            {
                List<string> roots = SearchRoots();
                List<RosterEntry> scanEntries = Entries;
                string scanTable = currentRosterTable;
                ModelInventoryBindingStage staged = ModelInventoryBindingStage.Capture(scanEntries, scanTable);
                List<ModelCandidate> found = await Task.Run(() =>
                {
                    var result = new List<ModelCandidate>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string root in ModelInventoryBindingStage.ExistingRoots(roots))
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        foreach (ModelCandidate model in ModelLibrary.Scan(root, cancellation.Token))
                            if (seen.Add(model.Folder)) result.Add(model);
                    }
                    staged.Prepare(result, cancellation.Token);
                    return result;
                }, cancellation.Token);
                if (IsDisposed || cancellation.IsCancellationRequested || version != modelScanVersion) return;
                Dictionary<RosterEntry, List<ModelCandidate>> choices;
                HashSet<string> ready;
                if (!staged.Commit(Entries, currentRosterTable, out choices, out ready)) return;
                modelInventory = found;
                modelChoices = choices;
                readyModelFolders = ready;
                RosterEntry selected = wrestlers.SelectedItems.Count == 1 ? (RosterEntry)wrestlers.SelectedItems[0].Tag : null;
                RefreshList(selected);
                modelsInfo.Text = found.Count + " model folders indexed · locations remembered";
                // Save discovered locations even when the user closes the roster without
                // loading a wrestler. The next session should not repeat the folder hunt.
                FolderBridge.SaveRoster(Entries);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsDisposed && version == modelScanVersion)
                {
                    modelsInfo.Text = "Could not scan models";
                    MessageBox.Show(this, error.Message, "Scan models folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                if (!IsDisposed && version == modelScanVersion) scanModels.Enabled = true;
                cancellation.Dispose();
                if (ReferenceEquals(modelScanCancellation, cancellation)) modelScanCancellation = null;
            }
        }

        private async Task<bool> AddModelsFolderAsync()
        {
            string initial = modelRoots.LastOrDefault() ?? (currentRosterTable == null ? sourceFolder : DiscoveryProfiles.DataRoot(currentRosterTable));
            string root = FolderLocationDialog.Choose(this, "Find your existing models",
                "Choose the main folder containing your extracted wrestler mods. Subfolders are scanned and this location is remembered.", initial);
            if (root == null) return false;
            return await AddModelsRootAsync(root);
        }

        private async Task<bool> AddModelsRootAsync(string root)
        {
            if (!modelRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) modelRoots.Add(root);
            try { SaveModelRoots(); }
            catch (Exception error)
            {
                MessageBox.Show(this, "The folder will be scanned, but its location could not be saved.\n\n" + error.Message,
                    "Models folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            modelScanTask = RefreshModelInventoryAsync();
            await modelScanTask;
            return !IsDisposed;
        }

        private async Task<string> ChooseExistingModelAsync(RosterEntry entry, bool forceChoice = false)
        {
            await modelScanTask;
            if (IsDisposed) return null;
            if (!forceChoice) return await ResolveSelectedModelAsync(entry);
            if (modelInventory.Count == 0)
            {
                if (!await AddModelsFolderAsync()) return null;
            }
            List<ModelCandidate> choices = modelInventory;
            string initial = entry.Folder ?? modelRoots.LastOrDefault() ?? sourceFolder;
            while (!IsDisposed)
            {
                string chosen = ModelChoiceDialog.Choose(this, entry, choices, initial);
                if (chosen == null) return null;
                // A pasted/dropped collection root is useful too. Every manual result
                // goes through this same validation, including a second folder choice.
                List<ModelCandidate> inside = await Task.Run(() => ModelLibrary.Scan(chosen, modelLifetime.Token), modelLifetime.Token);
                if (IsDisposed) return null;
                if (inside.Count == 0)
                {
                    MessageBox.Show(this, "No extracted .mcd character models were found in that folder. Choose the folder containing your extracted mods.",
                        "Models folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    initial = chosen;
                    continue;
                }
                if (inside.Count > 1)
                {
                    if (!await AddModelsRootAsync(chosen)) return null;
                    choices = inside;
                    initial = chosen;
                    continue;
                }
                ModelCandidate candidate = inside[0];
                if (candidate.SourceId > 0) entry.SourceId = candidate.SourceId;
                entry.Source = "Chosen model folder";
                entry.IsMod = true;
                return candidate.Folder;
            }
            return null;
        }

        private bool HasIndexedModel(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            string path = Path.GetFullPath(folder);
            if (path.Length != Path.GetPathRoot(path).Length)
                path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return readyModelFolders.Contains(path);
        }

        private static bool SameFolderOrChild(string candidate, string parent)
        {
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string path = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string> ResolveSelectedModelAsync(RosterEntry entry)
        {
            await modelScanTask;
            if (IsDisposed) return null;
            RosterModelResolution resolution = await Task.Run(() => RosterModelResolution.Resolve(entry, modelInventory, currentRosterTable));
            if (IsDisposed) return null;
            if (resolution.State == RosterModelState.Ready)
            {
                if (!resolution.ExistingAssociation)
                {
                    entry.Source = "Found in models folder";
                    entry.IsMod = true;
                }
                if (resolution.SourceId > 0) entry.SourceId = resolution.SourceId;
                return resolution.Folder;
            }
            if (resolution.State == RosterModelState.ChooseMatchingModel)
            {
                string chosen = ModelChoiceDialog.Choose(this, entry, resolution.Choices, entry.Folder, true);
                if (chosen == null) return null;
                ModelCandidate selected = resolution.Choices.First(candidate => string.Equals(candidate.Folder, chosen, StringComparison.OrdinalIgnoreCase));
                if (selected.SourceId > 0) entry.SourceId = selected.SourceId;
                entry.Source = "Chosen model folder";
                entry.IsMod = true;
                return selected.Folder;
            }
            if (resolution.State == RosterModelState.InstalledGame)
            {
                try
                {
                    Enabled = false;
                    ControlBox = false;
                    UseWaitCursor = true;
                    status.Text = "Loading " + entry.Name + " from the installed game…";
                    return await FolderBridge.RequestGameModelAsync(entry, entry.StockFolder, line =>
                    {
                        Action update = () => status.Text = line.Length > 150 ? line.Substring(0, 150) : line;
                        if (!IsDisposed && IsHandleCreated) { if (InvokeRequired) BeginInvoke(update); else update(); }
                    });
                }
                finally { if (!IsDisposed) { Enabled = true; ControlBox = true; UseWaitCursor = false; } }
            }

            string notice = "The roster contains " + entry.Name + ", but its extracted model files were not found.\n\n"
                + "Set your model library to the main Characters or mods folder once. The app finds each wrestler inside it automatically.";
            string marker = ModelsSettingsPath + ".setup-shown";
            if (modelRoots.Count == 0 && !offeredModelLibrarySetup && !File.Exists(marker))
            {
                offeredModelLibrarySetup = true;
                try { Directory.CreateDirectory(FolderBridge.StorageDirectory); File.WriteAllText(marker, "Model library setup offered."); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (MessageBox.Show(this, notice + "\n\nSet your model library now?", "Model library setup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes && await AddModelsFolderAsync())
                    return await ResolveSelectedModelAsync(entry);
                return null;
            }
            MessageBox.Show(this, notice + "\n\nUse Set model library… to add another collection, then load this wrestler again.",
                "Model files not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
    }
}
