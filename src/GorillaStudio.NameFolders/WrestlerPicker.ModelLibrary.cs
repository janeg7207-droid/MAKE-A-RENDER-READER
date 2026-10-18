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
        private Dictionary<RosterEntry, List<ModelCandidate>> modelChoices = new Dictionary<RosterEntry, List<ModelCandidate>>();
        private CancellationTokenSource modelScanCancellation;
        private Task modelScanTask = Task.CompletedTask;
        private int modelScanVersion;
        private bool modelSelectionBusy;
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
            scanModels.Text = "Scan models folder…";
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
            var unique = roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return unique.Where(path => !unique.Any(other => !string.Equals(path, other, StringComparison.OrdinalIgnoreCase)
                && path.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private async Task RefreshModelInventoryAsync()
        {
            if (modelScanCancellation != null) modelScanCancellation.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(modelLifetime.Token);
            modelScanCancellation = cancellation;
            int version = ++modelScanVersion;
            scanModels.Enabled = false;
            modelsInfo.Text = "Looking for existing models…";
            try
            {
                List<string> roots = SearchRoots();
                List<ModelCandidate> found = await Task.Run(() =>
                {
                    var result = new List<ModelCandidate>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string root in roots)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        foreach (ModelCandidate model in ModelLibrary.Scan(root, cancellation.Token))
                            if (seen.Add(model.Folder)) result.Add(model);
                    }
                    return result;
                }, cancellation.Token);
                if (IsDisposed || version != modelScanVersion) return;
                modelInventory = found;
                ModelLibraryBindingResult binding = ModelLibraryBinding.Apply(Entries, found, currentRosterTable);
                modelChoices = binding.Choices;
                RosterEntry selected = wrestlers.SelectedItems.Count == 1 ? (RosterEntry)wrestlers.SelectedItems[0].Tag : null;
                RefreshList(selected);
                modelsInfo.Text = found.Count + " model folders found";
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
            if (!forceChoice && Directory.Exists(entry.Folder)) return entry.Folder;
            if (modelInventory.Count == 0 || (!forceChoice && modelRoots.Count == 0))
            {
                if (!await AddModelsFolderAsync()) return null;
                if (!forceChoice && Directory.Exists(entry.Folder)) return entry.Folder;
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
                    if (!forceChoice && Directory.Exists(entry.Folder)) return entry.Folder;
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
    }
}
