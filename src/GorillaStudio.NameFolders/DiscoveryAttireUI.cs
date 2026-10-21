using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        private sealed class DiscoveryAttireContext
        {
            internal RosterEntry Entry;
            internal ModelCandidate[] Inventory;
            internal string[] ModelRoots;
            internal DiscoveryAttireCatalogResult Result;
        }

        private sealed class DiscoveryAttireBinding
        {
            internal DiscoveryAttireContext Context;
            internal string SourceFolder;
        }

        private sealed class PreparedDiscoveryAttire
        {
            internal CharacterFolderPlan Plan;
            internal Dictionary<ulong, string> Pictures;
            internal string Alias;
            internal string Relative;
        }

        private static readonly Dictionary<string, DiscoveryAttireBinding> discoveryAttires =
            new Dictionary<string, DiscoveryAttireBinding>(StringComparer.OrdinalIgnoreCase);

        // Register the complete selected-roster catalog, rather than just the
        // root that happened to be loaded first. Alias switches reuse it.
        internal static void RegisterDiscoveryAttires(string alias, RosterEntry entry,
            IEnumerable<ModelCandidate> inventory, DiscoveryAttireCatalogResult result, string sourceFolder = null,
            IEnumerable<string> modelRoots = null)
        {
            if (string.IsNullOrWhiteSpace(alias) || entry == null || result == null) return;
            var context = new DiscoveryAttireContext {
                Entry = CopyRosterEntry(entry), Result = result,
                ModelRoots = (modelRoots ?? new string[0]).Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Inventory = (inventory ?? new ModelCandidate[0]).Where(item => item != null)
                    .Select(item => new ModelCandidate { Folder = item.Folder, Name = item.Name, SourceId = item.SourceId }).ToArray()
            };
            discoveryAttires[SelectionPath(alias)] = new DiscoveryAttireBinding {
                Context = context, SourceFolder = sourceFolder ?? entry.Folder
            };
        }

        // Run on the preparation worker after hash matching. Resolve only
        // proven matched roots, so junction ancestors cannot split one source
        // into logical and physical identities at the dispatcher boundary.
        internal static void ResolveDiscoveryAttireSources(DiscoveryAttireCatalogResult result, CancellationToken token)
        {
            if (result == null) return;
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveryAttireSource source in result.Sources.ToArray())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string key = SelectionPath(source.SourceFolder), physical;
                    if (!resolved.TryGetValue(key, out physical))
                        resolved[key] = physical = DirectoryLinks.Resolve(source.SourceFolder);
                    AlternateAttires.Validate(physical, source.RelativePath);
                    source.SourceFolder = physical;
                    if (!seen.Add(DiscoveryAttireOptions.Key(physical, source.RelativePath) + "\n" + DiscoveryAttireReferenceKey(source)))
                        result.Sources.Remove(source);
                }
                catch (Exception error) when (AttireError(error) || error is System.ComponentModel.Win32Exception)
                {
                    result.Sources.Remove(source);
                    string note = source.DisplayName + ": its matched character source could not be resolved: " + error.Message;
                    if (!result.Issues.Contains(note)) result.Issues.Add(note);
                }
            }
        }

        private static DiscoveryAttireBinding DiscoveryAttireFor(object owner, string alias)
        {
            DiscoveryAttireBinding binding;
            if (string.IsNullOrWhiteSpace(alias) || !discoveryAttires.TryGetValue(SelectionPath(alias), out binding)) return null;
            string target = AttireField(owner, "TargetSlot") as string;
            return target == binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture) ? binding : null;
        }

        private static IReadOnlyList<AlternateAttire> DiscoveryAttireChoices(object owner, string alias, IReadOnlyList<AlternateAttire> local)
        {
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            if (binding == null) return local;
            var mapped = binding.Context.Result.Sources.Select(source => new AlternateAttire {
                SourceFolder = source.SourceFolder, SourceRelativePath = source.RelativePath,
                DiscoveryKey = DiscoveryAttireReferenceKey(source),
                DiscoveryModelHash = source.ModelHash,
                RelativePath = SameAttireFolder(source.SourceFolder, binding.SourceFolder)
                    ? AlternateAttires.AliasRelativePath(alias, source.RelativePath) : null,
                IsDefault = source.IsDefault,
                DisplayName = source.DisplayName + (source.IsDefault ? " (default)" : "")
            });
            // Keep exact mapped labels/defaults and add explicit local clothes
            // from the loaded character. Local choices do not create mappings
            // or change the selected wrestler's logical roster slot.
            return DiscoveryAttireDisplay.Merge(DiscoveryAttireOptions.Combine(alias, binding.SourceFolder, local, mapped,
                binding.Context.Result.References.Count > 0), binding.Context.Result);
        }

        private static bool HasDiscoveryAttireChoices(object owner, string alias)
        {
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            return binding != null && binding.Context.Result.Sources.Count > 0;
        }

        private static object DiscoveryAttireContextIdentity(object owner, string alias)
        { return DiscoveryAttireFor(owner, alias)?.Context; }

        internal static void SeedDiscoveryAttireSelection(object owner, string alias)
        {
            AttireControls controls;
            DiscoveryAttireBinding binding;
            if (owner == null || !attireWindows.TryGetValue(owner, out controls) || controls.Closed
                || string.IsNullOrWhiteSpace(alias) || !discoveryAttires.TryGetValue(SelectionPath(alias), out binding)
                || binding.Context.Result.Sources.Count == 0) return;
            if (SameAttireFolder(controls.Folder, alias) && controls.Selected != null
                && (binding.Context.Result.Sources.Any(item => SameAttireFolder(item.SourceFolder, binding.SourceFolder)
                    && AlternateAttires.AliasRelativePath(alias, item.RelativePath) == controls.Selected
                    && (controls.SelectedDiscoveryKey == null || DiscoveryAttireReferenceKey(item) == controls.SelectedDiscoveryKey))
                    || (controls.SelectedDiscoveryKey == null
                        && DiscoveryAttireOptions.IsLocalChoice(alias, binding.SourceFolder, controls.Selected))))
            { controls.Mapped = true; controls.Target = binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture); return; }
            DiscoveryAttireSource source = binding.Context.Result.Sources
                .Where(item => SameAttireFolder(item.SourceFolder, binding.SourceFolder))
                .OrderBy(item => item.IsDefault ? 0 : 1).FirstOrDefault();
            AlternateAttire localDefault = source == null ? DiscoveryAttireDisplay.LocalDefault(alias, binding.SourceFolder, AlternateAttires.Read(alias)) : null;
            if (source == null && localDefault == null)
                throw new InvalidOperationException("The selected character source has no available attire. Select one of its mapped sources.");
            string relative = AlternateAttires.Validate(alias, source == null ? localDefault.RelativePath : AlternateAttires.AliasRelativePath(alias, source.RelativePath));
            controls.Generation++; controls.Folder = alias; controls.Selected = relative;
            controls.SelectedDiscoveryKey = source == null ? null : DiscoveryAttireReferenceKey(source);
            controls.Target = binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture);
            controls.Mapped = true;
            ResetAttireChoices(controls);
        }

        static partial void SeedDiscoveryAttireForSelection(object owner, string current, int logicalId)
        {
            SeedDiscoveryAttireSelection(owner, current);
            AttireControls controls;
            string target = AttireField(owner, "TargetSlot") as string;
            if (attireWindows.TryGetValue(owner, out controls) && Directory.Exists(current))
                controls.SourceState.ConfirmLoaded(current, AttireField(owner, "Folder") as string, logicalId, target);
        }

        internal static string DiscoveryAttireReferenceKey(DiscoveryAttireSource source)
        {
            return DiscoveryAttireDisplay.ReferenceKey(source);
        }

        internal static DiscoveryAttireSource SelectedDiscoveryAttireSource(object owner)
        {
            AttireControls controls;
            if (owner == null || !attireWindows.TryGetValue(owner, out controls) || controls.Closed) return null;
            string alias = AttireField(owner, "Folder") as string;
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            if (binding == null) return null;
            DiscoveryAttireSource source = binding.Context.Result.Sources.FirstOrDefault(item =>
                SameAttireFolder(item.SourceFolder, binding.SourceFolder)
                && DiscoveryAttireReferenceKey(item) == controls.SelectedDiscoveryKey
                && AlternateAttires.AliasRelativePath(alias, item.RelativePath) == controls.Selected);
            return source == null ? null : new DiscoveryAttireSource { LogicalId = source.LogicalId, MappingId = source.MappingId,
                CostumeKey = source.CostumeKey, CostumeIndex = source.CostumeIndex, ModelHash = source.ModelHash,
                DisplayName = source.DisplayName, Issue = source.Issue, Warning = source.Warning, IsDefault = source.IsDefault,
                SourceFolder = source.SourceFolder, RelativePath = source.RelativePath, SourceId = source.SourceId };
        }

        internal static bool SeedDiscoveryAttireChoice(object owner, string alias, string sourceRelative, string discoveryKey)
        {
            AttireControls controls;
            DiscoveryAttireBinding binding;
            if (owner == null || !attireWindows.TryGetValue(owner, out controls) || controls.Closed) return false;
            if (string.IsNullOrWhiteSpace(alias) || !discoveryAttires.TryGetValue(SelectionPath(alias), out binding))
                throw new InvalidOperationException("Load the Discovery attire catalog before choosing its model.");
            if (string.IsNullOrWhiteSpace(discoveryKey))
            {
                string local = DiscoveryAttireOptions.ValidateLocalSelection(alias, binding.SourceFolder,
                    AlternateAttires.AliasRelativePath(alias, sourceRelative));
                controls.Generation++; controls.Folder = alias; controls.Selected = local;
                controls.SelectedDiscoveryKey = null;
                controls.Target = binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture);
                controls.Mapped = binding.Context.Result.Sources.Count > 0;
                ResetAttireChoices(controls);
                return true;
            }
            DiscoveryAttireSource source = binding.Context.Result.Sources.FirstOrDefault(item =>
                SameAttireFolder(item.SourceFolder, binding.SourceFolder) && DiscoveryAttireReferenceKey(item) == discoveryKey
                && string.Equals(item.RelativePath.Replace('\\', '/'), (sourceRelative ?? "").Replace('\\', '/'), StringComparison.Ordinal));
            if (source == null) throw new InvalidOperationException("The requested costume is no longer an active, available Discovery attire for this source.");
            string relative = AlternateAttires.Validate(alias, AlternateAttires.AliasRelativePath(alias, source.RelativePath));
            controls.Generation++; controls.Folder = alias; controls.Selected = relative;
            controls.SelectedDiscoveryKey = DiscoveryAttireReferenceKey(source);
            controls.Target = binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture); controls.Mapped = true;
            ResetAttireChoices(controls);
            return true;
        }

        private static void ReconcileDiscoveryAttireSelection(object owner, AttireControls controls, string alias, string relative)
        {
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            if (binding == null || binding.Context.Result.Sources.Count == 0) { controls.SelectedDiscoveryKey = null; return; }
            var candidates = binding.Context.Result.Sources.Where(source => SameAttireFolder(source.SourceFolder, binding.SourceFolder)
                && string.Equals(AlternateAttires.AliasRelativePath(alias, source.RelativePath), relative, StringComparison.Ordinal)).ToList();
            if (candidates.Any(source => DiscoveryAttireReferenceKey(source) == controls.SelectedDiscoveryKey)) return;
            controls.SelectedDiscoveryKey = candidates.Count == 0 ? null : DiscoveryAttireReferenceKey(candidates[0]);
        }

        private static void ValidateDiscoveryAttireSelection(object owner, AttireControls controls, string alias)
        {
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            if (binding == null || binding.Context.Result.Sources.Count == 0) return;
            if (!binding.Context.Result.Sources.Any(source => SameAttireFolder(source.SourceFolder, binding.SourceFolder)
                && string.Equals(AlternateAttires.AliasRelativePath(alias, source.RelativePath), controls.Selected, StringComparison.Ordinal)
                && (controls.SelectedDiscoveryKey == null || DiscoveryAttireReferenceKey(source) == controls.SelectedDiscoveryKey)))
            {
                if (controls.SelectedDiscoveryKey == null)
                {
                    DiscoveryAttireOptions.ValidateLocalSelection(alias, binding.SourceFolder, controls.Selected);
                    return;
                }
                throw new InvalidOperationException("Choose an available attire before rendering. Refresh the list if the mappings or model files changed.");
            }
        }

        private static string DiscoveryAttireIssueNote(object owner, string alias)
        {
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            if (binding == null || binding.Context.Result.References.Count == 0) return null;
            var result = binding.Context.Result;
            int available = result.References.Count(reference => result.Sources.Any(source =>
                source.LogicalId == reference.LogicalId && source.CostumeKey == reference.CostumeKey));
            string note = result.References.Count.ToString(CultureInfo.InvariantCulture) + " active Discovery attires · "
                + available.ToString(CultureInfo.InvariantCulture) + " available";
            if (result.Issues.Count > 0) note += ". Discovery attire notes: " + string.Join("\n", result.Issues);
            return note;
        }

        private static async Task RefreshDiscoveryAttireContextAsync(object owner, AttireControls controls, string alias)
        {
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, alias);
            if (binding == null || string.IsNullOrWhiteSpace(binding.Context.Entry.RosterTable)) return;
            try
            {
                controls.Note.Text = "Reading Discovery's active attire mappings…";
                ModelCandidate[] inventory = null;
                DiscoveryAttireCatalogResult updated = await Task.Run(() => {
                    inventory = DiscoveryAttireInventory.Refresh(binding.Context.Inventory,
                        binding.Context.ModelRoots.Concat(new[] { binding.SourceFolder }), CancellationToken.None).ToArray();
                    var found = DiscoveryAttireCatalog.Find(binding.Context.Entry, inventory,
                        binding.Context.Entry.RosterTable, CancellationToken.None);
                    ResolveDiscoveryAttireSources(found, CancellationToken.None);
                    return found;
                });
                if (!controls.Closed && !controls.Switching && SameAttireFolder(alias, AttireField(owner, "Folder") as string)
                    && ReferenceEquals(binding, DiscoveryAttireFor(owner, alias)))
                { binding.Context.Inventory = inventory; binding.Context.Result = updated; }
            }
            catch (Exception error) when (AttireError(error))
            {
                if (!controls.Closed) controls.Note.Text = "Discovery attire refresh stopped: " + error.Message;
            }
        }

        private static async Task LoadDiscoveryAttireSourceAsync(Window owner, AttireControls controls, AlternateAttire item)
        {
            if (controls.Closed || controls.Switching || AttireBusy(owner) || rosterWindowsBusy.Contains(owner)) return;
            string previousFolder = AttireField(owner, "Folder") as string;
            string previousRelative = controls.Selected;
            string previousReference = controls.SelectedDiscoveryKey;
            DiscoveryAttireBinding binding = DiscoveryAttireFor(owner, previousFolder);
            if (binding == null) { FillAttires(owner, controls, false); return; }
            DiscoveryAttireSource source = binding.Context.Result.Sources.FirstOrDefault(candidate =>
                string.Equals(DiscoveryAttireOptions.Key(candidate.SourceFolder, candidate.RelativePath),
                    DiscoveryAttireOptions.Key(item.SourceFolder, item.SourceRelativePath), StringComparison.OrdinalIgnoreCase));
            if (item.DiscoveryKey != null) source = binding.Context.Result.Sources.FirstOrDefault(candidate =>
                DiscoveryAttireReferenceKey(candidate) == item.DiscoveryKey
                && string.Equals(DiscoveryAttireOptions.Key(candidate.SourceFolder, candidate.RelativePath),
                    DiscoveryAttireOptions.Key(item.SourceFolder, item.SourceRelativePath), StringComparison.OrdinalIgnoreCase));
            if (source == null || source.LogicalId != binding.Context.Entry.Id)
            { controls.Note.Text = "This attire is no longer linked to the selected wrestler. Refresh the list."; return; }
            if (SameAttireFolder(binding.SourceFolder, source.SourceFolder))
            {
                try { controls.SelectedDiscoveryKey = item.DiscoveryKey; SetAlternateAttireSelection(owner, previousFolder, AlternateAttires.AliasRelativePath(previousFolder, source.RelativePath)); }
                catch (Exception error) when (AttireError(error)) { controls.Note.Text = error.Message; SelectExistingAttire(controls); }
                return;
            }

            controls.Switching = true; controls.SwitchCommitted = false;
            var cancellation = new CancellationTokenSource();
            controls.SwitchCancellation = cancellation;
            int generation = ++controls.Generation;
            bool ownsBusy = false, committed = false;
            var buttonStates = new Dictionary<Button, bool>();
            PreparedRenderSelection oldPictures;
            preparedPictures.TryGetValue(SelectionPath(previousFolder), out oldPictures);
            rosterWindowsBusy.Add(owner);
            try
            {
                StopAutomaticMake(owner);
                SetAttireField(owner, "Busy", true); ownsBusy = true;
                foreach (string name in new[] { "PreviewButton", "MakeButton", "InstallButton" })
                {
                    var button = AttireField(owner, name) as Button;
                    if (button != null) { buttonStates[button] = button.IsEnabled; button.IsEnabled = false; }
                }
                controls.Note.Text = "Loading " + source.DisplayName + " from its linked character source…";
                UpdateAttireAvailability(owner, controls);
                Task preparation = AttireField(owner, "PrepTask") as Task;
                if (preparation != null) await AwaitAttireTaskAsync(preparation, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                PreparedDiscoveryAttire prepared = await Task.Run(() => PrepareDiscoveryAttire(binding.Context, source,
                    oldPictures == null ? null : new Dictionary<ulong, string>(oldPictures.Pictures), cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!CurrentDiscoveryAttireSwitch(owner, controls, generation, previousFolder, binding.Context)) return;
                var setFolder = owner.GetType().GetMethod("SetFolder", InstanceMembers, null, new[] { typeof(string) }, null);
                if (setFolder == null) throw new MissingMethodException("This Gorilla Studio version cannot load the linked attire source.");

                // Commit once. Seed the explicit choice before native SetFolder's
                // final Prepare reads the attire. No default warm-up is launched.
                controls.SwitchCommitted = true; committed = true;
                UpdateAttireAvailability(owner, controls);
                discoveryAttires[SelectionPath(prepared.Alias)] = new DiscoveryAttireBinding {
                    Context = binding.Context, SourceFolder = prepared.Plan.SourceFolder
                };
                RememberRenderTable(prepared.Alias, binding.Context.Entry.RosterTable);
                RememberPreparedRender(prepared.Alias, prepared.Plan, binding.Context.Entry, prepared.Pictures);
                CopyDiscoveryAttirePictures(oldPictures, prepared.Alias);
                RegisterSelection(prepared.Alias, binding.Context.Entry);
                controls.Folder = prepared.Alias; controls.Selected = prepared.Relative;
                controls.SelectedDiscoveryKey = item.DiscoveryKey;
                controls.Target = binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture);
                ClearDiscoveryAttireResult(owner);
                foreach (Button button in buttonStates.Keys.ToArray())
                    if (ReferenceEquals(button, AttireField(owner, "InstallButton"))) buttonStates[button] = false;
                preparedForLoad = prepared.Alias;
                SetAttireField(owner, "Busy", false); ownsBusy = false;
                Task load;
                try
                {
                    load = setFolder.Invoke(owner, new object[] { prepared.Alias }) as Task;
                    if (load != null && !load.IsCompleted) { SetAttireField(owner, "Busy", true); ownsBusy = true; }
                    if (load != null) await load;
                    ApplySelection(owner);
                }
                finally { preparedForLoad = null; pendingRosterSelection = null; }
                if (!controls.Closed && (AttireField(owner, "TargetSlot") as string) != binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidOperationException("The linked attire did not retain the selected logical wrestler slot.");
                controls.Note.Text = source.DisplayName + " loaded. Press Preview to check it.";
            }
            catch (OperationCanceledException)
            { if (!controls.Closed) controls.Note.Text = "Attire change canceled; the previous model is still selected."; }
            catch (Exception error)
            {
                var invocation = error as TargetInvocationException;
                if (invocation?.InnerException != null) error = invocation.InnerException;
                if (!controls.Closed) controls.Note.Text = "Could not load the linked attire: " + error.Message;
                if (committed)
                {
                    ClearDiscoveryAttireResult(owner);
                    foreach (Button button in buttonStates.Keys.ToArray())
                        if (ReferenceEquals(button, AttireField(owner, "InstallButton"))) buttonStates[button] = false;
                    if (!controls.Closed)
                    {
                        try
                        {
                            // Restore using the native loader and logical-ID
                            // hook, rather than relabeling a half-loaded model.
                            var prep = AttireField(owner, "PrepTask") as Task;
                            if (prep != null) try { await prep; } catch { }
                            RegisterSelection(previousFolder, binding.Context.Entry);
                            controls.Folder = previousFolder; controls.Selected = previousRelative;
                            controls.SelectedDiscoveryKey = previousReference;
                            preparedForLoad = previousFolder;
                            SetAttireField(owner, "Busy", false); ownsBusy = false;
                            var restore = owner.GetType().GetMethod("SetFolder", InstanceMembers, null, new[] { typeof(string) }, null);
                            if (restore == null) throw new MissingMethodException("The previous model cannot be restored by this application version.");
                            Task restoring = restore.Invoke(owner, new object[] { previousFolder }) as Task;
                            if (restoring != null && !restoring.IsCompleted) { SetAttireField(owner, "Busy", true); ownsBusy = true; }
                            if (restoring != null) await restoring;
                            ApplySelection(owner);
                            if (!SameAttireFolder(previousFolder, AttireField(owner, "Folder") as string)
                                || (AttireField(owner, "TargetSlot") as string) != binding.Context.Entry.Id.ToString(CultureInfo.InvariantCulture))
                                throw new InvalidOperationException("The previous model or its logical wrestler slot was not restored.");
                            committed = false;
                            controls.Note.Text = "Could not load the linked attire: " + error.Message + " The previous model was restored.";
                        }
                        catch (Exception restoreError)
                        {
                            controls.SourceState.RestoreFailed(restoreError.Message);
                            if (controls.SourceRecoveryButtons == null)
                                controls.SourceRecoveryButtons = new Dictionary<Button, bool>(buttonStates);
                            controls.Note.Text += " Previous-model restore also stopped: " + restoreError.Message + ". Select the wrestler again before rendering.";
                        }
                    }
                }
            }
            finally
            {
                pendingRosterSelection = null; preparedForLoad = null;
                if (ownsBusy) SetAttireField(owner, "Busy", false);
                rosterWindowsBusy.Remove(owner);
                controls.Switching = false; controls.SwitchCommitted = false; controls.SwitchCancellation = null;
                cancellation.Dispose();
                if (!controls.Closed)
                {
                    if (!committed) { controls.Folder = previousFolder; controls.Selected = previousRelative; controls.SelectedDiscoveryKey = previousReference; }
                    foreach (var button in buttonStates) button.Key.IsEnabled = button.Value && controls.SourceState.CanRender;
                    string message = controls.Note.Text;
                    FillAttires(owner, controls, committed);
                    controls.Note.Text = message;
                    UpdateAttireAvailability(owner, controls);
                    QueueSelectedPicture(owner);
                }
            }
        }

        private static bool CurrentDiscoveryAttireSwitch(Window owner, AttireControls controls, int generation,
            string previousFolder, DiscoveryAttireContext context)
        {
            return !controls.Closed && owner.IsLoaded && controls.Generation == generation
                && SameAttireFolder(previousFolder, AttireField(owner, "Folder") as string)
                && (AttireField(owner, "TargetSlot") as string) == context.Entry.Id.ToString(CultureInfo.InvariantCulture);
        }

        private static PreparedDiscoveryAttire PrepareDiscoveryAttire(DiscoveryAttireContext context,
            DiscoveryAttireSource source, Dictionary<ulong, string> pictures, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string root = DirectoryLinks.Resolve(source.SourceFolder);
            AlternateAttires.Validate(root, source.RelativePath);
            var entry = CopyRosterEntry(context.Entry); entry.Folder = root; entry.SourceId = source.SourceId;
            CharacterFolderPlan plan = CharacterFolders.Plan(root, entry);
            int physical = CharacterFolders.PhysicalId(root) ?? source.SourceId;
            DirectAttirePlan direct = AutomaticAttire.Plan(root, physical);
            pictures = pictures ?? PrepareRenderImages(plan, entry.Id);
            token.ThrowIfCancellationRequested();
            string alias = PrepareCharacterAlias(plan, direct, pictures);
            string relative = AlternateAttires.Validate(alias, AlternateAttires.AliasRelativePath(alias, source.RelativePath));
            return new PreparedDiscoveryAttire { Plan = plan, Pictures = pictures, Alias = alias, Relative = relative };
        }

        private static void CopyDiscoveryAttirePictures(PreparedRenderSelection previous, string alias)
        {
            PreparedRenderSelection next;
            if (previous == null || !preparedPictures.TryGetValue(SelectionPath(alias), out next)
                || !string.Equals(previous.Table, next.Table, StringComparison.OrdinalIgnoreCase)) return;
            foreach (int id in previous.Resolved) next.Resolved.Add(id);
            foreach (var note in previous.Notes) next.Notes[note.Key] = note.Value;
        }

        private static async Task AwaitAttireTaskAsync(Task task, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, canceled.Task) != task) token.ThrowIfCancellationRequested();
                try { await task; } catch { /* Native preparation failure must not block an explicit replacement. */ }
            }
        }

        private static void ClearDiscoveryAttireResult(object owner)
        {
            SetAttireField(owner, "LastGmr", null);
            (AttireField(owner, "ResultTimer") as DispatcherTimer)?.Stop();
            (AttireField(owner, "ResultFrames") as System.Collections.IList)?.Clear();
            var image = AttireField(owner, "ResultImage") as Image; if (image != null) image.Source = null;
            var install = AttireField(owner, "InstallButton") as Button; if (install != null) install.IsEnabled = false;
            var info = AttireField(owner, "ResultInfo") as TextBlock;
            if (info != null) info.Text = "Attire source changed. Press Preview to check the model, then MAKE to install it.";
        }
    }
}
