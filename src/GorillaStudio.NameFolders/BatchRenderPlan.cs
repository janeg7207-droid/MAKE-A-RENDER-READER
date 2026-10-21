using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public sealed class BatchAttireSelection
    {
        public string RelativePath { get; set; }
        public string DisplayName { get; set; }
        public string SourceFolder { get; set; }
        public int SourceId { get; set; }
        public string DiscoveryKey { get; set; }
        public ulong DiscoveryModelHash { get; set; }
        public bool IsDefault { get; set; }
        public string UnavailableReason { get; set; }
        public bool IsAvailable { get { return string.IsNullOrWhiteSpace(UnavailableReason); } }
        internal bool RequiresDiscoverySourceProof { get; set; }
    }

    public sealed class BatchRenderJob
    {
        public RosterEntry Entry { get; internal set; }
        public bool ExtractFromGame { get; internal set; }
        public string Status { get; internal set; }
        public string OutputDirectory { get; internal set; }
        public string RenderPath { get; internal set; }
        public string PublishedRenderPath { get; internal set; }
        public string AttireRelativePath { get; internal set; }
        public string AttireSourceRelativePath { get; internal set; }
        public string AttireName { get; internal set; }
        public string AttireDiscoveryKey { get; internal set; }
        public ulong AttireDiscoveryModelHash { get; internal set; }
        public bool HasExplicitAttire { get; internal set; }
        public bool AttireLocalSourceMustRemainMapped { get; internal set; }
        internal string AttireLocalSourceFolder { get; set; }
        internal int AttireLocalSourceId { get; set; }
        internal int AttireLocalLogicalId { get; set; }
        internal string AttireLocalSourceRelativePath { get; set; }
        public int VariantIndex { get; internal set; }
    }

    /// <summary>Snapshots selected logical slots and resolves models without asking per-job questions.</summary>
    public sealed class BatchRenderPlan
    {
        public List<BatchRenderJob> Jobs { get; private set; } = new List<BatchRenderJob>();
        public List<string> Skipped { get; private set; } = new List<string>();

        public static BatchRenderPlan Create(IEnumerable<RosterEntry> selected, IEnumerable<ModelCandidate> inventory, string rosterTable)
        {
            var plan = new BatchRenderPlan();
            var models = CharacterModels(inventory);
            var rows = (selected ?? Enumerable.Empty<RosterEntry>()).Where(entry => entry != null).Select(CharacterEntry).ToList();
            var unique = rows.GroupBy(entry => entry.Id).Where(group => group.Count() == 1 && group.Key >= 1 && group.Key <= 65535).Select(group => group.First()).ToList();
            var mapped = MappedModelLibrary.FindMatches(unique, models, rosterTable);
            var ready = new HashSet<RosterEntry>(unique.Where(ReadyFolder));
            foreach (RosterEntry entry in unique.Where(entry => !ready.Contains(entry)))
            {
                List<ModelCandidate> matches;
                if (mapped.TryGetValue(entry, out matches) && matches.Count == 1)
                {
                    entry.Folder = matches[0].Folder;
                    if (matches[0].SourceId > 0) entry.SourceId = matches[0].SourceId;
                }
                else if (Directory.Exists(entry.Folder)) entry.Folder = null; // An empty saved folder is not a model association.
            }
            ModelLibraryBindingResult binding = ModelLibraryBinding.Apply(unique.Where(entry => ready.Contains(entry) || !mapped.ContainsKey(entry)).ToList(), models, rosterTable, false);
            foreach (var group in rows.GroupBy(entry => entry.Id))
            {
                RosterEntry original = group.First();
                string label = (original.Name ?? "Wrestler") + " (" + original.Id.ToString(CultureInfo.InvariantCulture) + ")";
                if (original.Id < 1 || original.Id > 65535)
                { plan.Skipped.Add(label + ": choose a valid roster slot first."); continue; }
                if (group.Count() > 1)
                { plan.Skipped.Add(label + ": multiple selected rows use this slot; choose one in the normal roster first."); continue; }
                List<ModelCandidate> matchingModels;
                if (!ready.Contains(original) && ((mapped.TryGetValue(original, out matchingModels) && matchingModels.Count > 1)
                    || binding.Choices.ContainsKey(original)))
                { plan.Skipped.Add(label + ": several models match; select the model once in the normal roster."); continue; }
                RosterEntry entry = Copy(original);
                // Explicit mappings and name matches were indexed for the entire batch above.
                // The existing resolver now verifies each bound model or stock fallback
                // without rescanning the complete inventory for every wrestler.
                RosterModelResolution resolution = RosterModelResolution.Resolve(entry, Enumerable.Empty<ModelCandidate>(), rosterTable);
                if (resolution.State == RosterModelState.Ready)
                {
                    entry.Folder = resolution.Folder;
                    if (resolution.SourceId > 0) entry.SourceId = resolution.SourceId;
                    plan.Jobs.Add(new BatchRenderJob { Entry = entry, Status = "Ready" });
                }
                else if (resolution.State == RosterModelState.InstalledGame)
                    plan.Jobs.Add(new BatchRenderJob { Entry = entry, ExtractFromGame = true, Status = "Load from installed game" });
                else plan.Skipped.Add(label + (resolution.State == RosterModelState.ChooseMatchingModel
                    ? ": several models match; select the model once in the normal roster."
                    : ": extracted model files were not found; set the model library in the normal roster."));
            }
            return plan;
        }

        private static bool ReadyFolder(RosterEntry entry)
        {
            if (!Directory.Exists(entry.Folder) || CharacterFolders.HasAmbiguousChildren(entry.Folder)) return false;
            try { return ModelLibrary.Scan(entry.Folder).Count > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        public static RosterEntry Copy(RosterEntry entry)
        {
            return new RosterEntry { Id = entry.Id, Name = entry.Name, Folder = entry.Folder, RosterTable = entry.RosterTable,
                SourceId = entry.SourceId, IsMod = entry.IsMod, Source = entry.Source, StockFolder = entry.StockFolder };
        }

        /// <summary>Expands an explicitly selected attire child only when its character parent is proven.</summary>
        public static RosterEntry CharacterEntry(RosterEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            RosterEntry result = Copy(entry);
            if (Directory.Exists(result.Folder))
            {
                int physical = CharacterFolders.PhysicalId(result.Folder) ?? (result.SourceId > 0 ? result.SourceId : result.Id);
                if (physical >= 1 && physical <= 65535) result.Folder = AutomaticAttire.CharacterRoot(result.Folder, physical);
            }
            return result;
        }

        private static List<ModelCandidate> CharacterModels(IEnumerable<ModelCandidate> inventory)
        {
            return (inventory ?? Enumerable.Empty<ModelCandidate>()).Where(item => item != null).Select(item => {
                string folder = item.Folder;
                int physical = CharacterFolders.PhysicalId(folder) ?? item.SourceId;
                if (Directory.Exists(folder) && physical >= 1 && physical <= 65535)
                    folder = AutomaticAttire.CharacterRoot(folder, physical);
                return new ModelCandidate { Folder = folder, Name = item.Name, SourceId = item.SourceId };
            }).GroupBy(item => item.Folder ?? "", StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        }

        public static DiscoveryAttireCatalogResult FindAttires(RosterEntry entry, IEnumerable<ModelCandidate> inventory, string rosterTable)
        {
            return DiscoveryAttireCatalog.Find(CharacterEntry(entry), CharacterModels(inventory), rosterTable);
        }

        public static BatchRenderPlan WithAttires(BatchRenderPlan original, IDictionary<int, List<BatchAttireSelection>> selections)
        {
            return ExpandAttires(original, selections, false);
        }

        public static BatchAttireSelection DiscoveryChoice(DiscoveryAttireSource source)
        {
            if (source == null) throw new ArgumentNullException("source");
            return new BatchAttireSelection { SourceFolder = source.SourceFolder, SourceId = source.SourceId,
                RelativePath = source.RelativePath, DisplayName = source.DisplayName, DiscoveryModelHash = source.ModelHash, IsDefault = source.IsDefault,
                DiscoveryKey = source.LogicalId.ToString(CultureInfo.InvariantCulture) + ":" + source.CostumeKey.ToString(CultureInfo.InvariantCulture)
                    + ":" + source.MappingId.ToString(CultureInfo.InvariantCulture) + ":" + source.CostumeIndex.ToString(CultureInfo.InvariantCulture) };
        }

        public static string SelectionIdentity(BatchAttireSelection choice, string fallbackFolder = null)
        {
            if (choice == null) throw new ArgumentNullException("choice");
            string folder = choice.SourceFolder ?? fallbackFolder;
            return (string.IsNullOrWhiteSpace(folder) ? "" : Path.GetFullPath(folder).TrimEnd('\\', '/'))
                + "\n" + (choice.RelativePath ?? "").Replace('\\', '/') + "\n" + (choice.DiscoveryKey ?? "");
        }

        /// <summary>Lists active mapped choices and extra MCDs contained in the same proven character sources.</summary>
        public static List<BatchAttireSelection> AvailableAttires(RosterEntry entry, DiscoveryAttireCatalogResult catalog)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            entry = CharacterEntry(entry);
            var sources = (catalog == null ? Enumerable.Empty<DiscoveryAttireSource>() : catalog.Sources)
                .Where(source => source != null && source.LogicalId == entry.Id && source.Issue == null
                    && Directory.Exists(source.SourceFolder)).ToList();
            var result = new List<BatchAttireSelection>();
            var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveryAttireSource source in sources.OrderBy(source => source.IsDefault ? 0 : 1))
            {
                BatchAttireSelection choice = DiscoveryChoice(source);
                choice.RelativePath = AlternateAttires.Validate(source.SourceFolder, source.RelativePath);
                if (choice.RelativePath == null) continue;
                if (mapped.Add(SelectionIdentity(choice))) result.Add(choice);
                paths.Add(DiscoveryAttireOptions.Key(choice.SourceFolder, choice.RelativePath));
            }
            bool mappedDefault = result.Any(choice => choice.IsDefault);
            var roots = sources.Select(source => source.SourceFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ReadyFolder(entry) && !roots.Any(folder => SameSource(folder, entry.Folder))) roots.Add(entry.Folder);
            foreach (string folder in roots)
            {
                int sourceId = LocalSourceId(entry, folder, sources);
                foreach (AlternateAttire attire in AlternateAttires.Read(folder))
                {
                    string relative;
                    try { relative = DiscoveryAttireOptions.ValidateLocalSelection(folder, folder, attire.RelativePath); }
                    catch (Exception error) when (LocalAttireError(error)) { continue; }
                    if (!paths.Add(DiscoveryAttireOptions.Key(folder, relative))) continue;
                    string label = attire.DisplayName ?? relative;
                    const string defaultSuffix = " (default)";
                    if (mappedDefault && label.EndsWith(defaultSuffix, StringComparison.OrdinalIgnoreCase))
                        label = label.Substring(0, label.Length - defaultSuffix.Length);
                    result.Add(new BatchAttireSelection { SourceFolder = folder, SourceId = sourceId,
                        RelativePath = relative, DisplayName = label + (sources.Count > 0 ? " (local)" : ""),
                        IsDefault = attire.IsDefault && !mappedDefault });
                }
            }
            var known = new HashSet<string>(result.Where(choice => !string.IsNullOrWhiteSpace(choice.DiscoveryKey)).Select(choice => choice.DiscoveryKey), StringComparer.Ordinal);
            foreach (DiscoveryAttireReference reference in catalog == null ? Enumerable.Empty<DiscoveryAttireReference>() : catalog.References)
            {
                if (reference == null || reference.LogicalId != entry.Id) continue;
                string key = reference.LogicalId.ToString(CultureInfo.InvariantCulture) + ":" + reference.CostumeKey.ToString(CultureInfo.InvariantCulture)
                    + ":" + reference.MappingId.ToString(CultureInfo.InvariantCulture) + ":" + reference.CostumeIndex.ToString(CultureInfo.InvariantCulture);
                if (!known.Add(key)) continue;
                result.Add(new BatchAttireSelection { DiscoveryKey = key, DisplayName = reference.DisplayName,
                    DiscoveryModelHash = reference.ModelHash, IsDefault = reference.IsDefault,
                    UnavailableReason = reference.Issue ?? "Its exact mapped MCD was not found in the extracted model folders. Load or extract that model source to use this attire." });
            }
            return result;
        }

        private static bool LocalAttireError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is System.Security.SecurityException || error is NotSupportedException;
        }

        private static int LocalSourceId(RosterEntry entry, string folder, IEnumerable<DiscoveryAttireSource> sources)
        {
            int[] proven = sources.Where(source => source.LogicalId == entry.Id && SameSource(source.SourceFolder, folder))
                .Select(source => source.SourceId).Where(id => id > 0 && id <= 65535).Distinct().ToArray();
            if (proven.Length > 1) throw new InvalidOperationException("The selected character source has different physical model IDs. Choose a specific model folder.");
            if (proven.Length == 1) return proven[0];
            if (!SameSource(folder, entry.Folder))
                throw new InvalidOperationException("The selected local attire no longer belongs to an active Discovery character source. Refresh the attire choices.");
            return CharacterFolders.PhysicalId(folder) ?? (entry.SourceId > 0 ? entry.SourceId : entry.Id);
        }

        /// <summary>Resolves explicitly selected active costume sources before ordinary ambiguous-model handling.</summary>
        public static BatchRenderPlan CreateWithAttires(IEnumerable<RosterEntry> selected, IEnumerable<ModelCandidate> inventory,
            string rosterTable, IDictionary<int, List<BatchAttireSelection>> selections)
        {
            var rows = (selected ?? Enumerable.Empty<RosterEntry>()).Where(entry => entry != null).Select(CharacterEntry).ToList();
            var models = CharacterModels(inventory);
            var normalized = new Dictionary<int, List<BatchAttireSelection>>();
            foreach (var group in rows.GroupBy(entry => entry.Id).Where(group => group.Count() == 1 && group.Key >= 1 && group.Key <= 65535))
            {
                RosterEntry entry = group.First();
                List<BatchAttireSelection> choices;
                if (selections == null || !selections.TryGetValue(entry.Id, out choices) || choices == null || choices.Count == 0) continue;
                DiscoveryAttireCatalogResult catalog = null;
                var safe = new List<BatchAttireSelection>();
                foreach (BatchAttireSelection choice in choices)
                {
                    if (choice == null) throw new ArgumentException("Choose an actual attire model for " + entry.Name + ".");
                    if (!choice.IsAvailable) throw new InvalidOperationException("The selected attire for " + entry.Name + " is unavailable: " + choice.UnavailableReason);
                    if (!string.IsNullOrWhiteSpace(choice.DiscoveryKey))
                    {
                        if (catalog == null) catalog = DiscoveryAttireCatalog.Find(entry, models, rosterTable);
                        safe.Add(VerifyDiscoveryChoice(entry, choice, catalog));
                        if (string.IsNullOrWhiteSpace(entry.RosterTable) && !string.IsNullOrWhiteSpace(rosterTable))
                            entry.RosterTable = Path.GetFullPath(rosterTable);
                    }
                    else
                    {
                        if (catalog == null) catalog = DiscoveryAttireCatalog.Find(entry, models, rosterTable);
                        string folder = choice.SourceFolder ?? entry.Folder;
                        bool currentSource = SameSource(folder, entry.Folder);
                        bool provenSource = catalog.Sources.Any(source => source.LogicalId == entry.Id && SameSource(source.SourceFolder, folder));
                        if (!currentSource && !provenSource)
                            throw new InvalidOperationException("An attire in another model folder requires an active Discovery source for this wrestler. Refresh the attire choices.");
                        string relative = DiscoveryAttireOptions.ValidateLocalSelection(folder, folder, choice.RelativePath);
                        if (relative == null) throw new ArgumentException("Choose an actual attire model for " + entry.Name + ".");
                        safe.Add(new BatchAttireSelection { SourceFolder = folder, SourceId = LocalSourceId(entry, folder, catalog.Sources),
                            RelativePath = relative, DisplayName = choice.DisplayName, IsDefault = choice.IsDefault,
                            RequiresDiscoverySourceProof = !currentSource });
                        if (provenSource && string.IsNullOrWhiteSpace(entry.RosterTable) && !string.IsNullOrWhiteSpace(rosterTable))
                            entry.RosterTable = Path.GetFullPath(rosterTable);
                    }
                }
                normalized[entry.Id] = safe;
                BatchAttireSelection first = safe.First();
                entry.Folder = first.SourceFolder;
                if (first.SourceId > 0) entry.SourceId = first.SourceId;
            }
            return ExpandAttires(Create(rows, models, rosterTable), normalized, true);
        }

        public static DiscoveryAttireCatalogResult RefreshDiscoveryAttires(BatchRenderJob job, IEnumerable<ModelCandidate> inventory, string rosterTable)
        {
            if (job == null || job.Entry == null) throw new ArgumentException("A wrestler job is required.", "job");
            var result = FindAttires(job.Entry, inventory, rosterTable);
            if (!string.IsNullOrWhiteSpace(job.AttireDiscoveryKey))
                VerifyDiscoveryChoice(job.Entry, new BatchAttireSelection { SourceFolder = job.Entry.Folder,
                    SourceId = job.Entry.SourceId, RelativePath = job.AttireSourceRelativePath,
                    DiscoveryKey = job.AttireDiscoveryKey, DiscoveryModelHash = job.AttireDiscoveryModelHash }, result);
            else if (job.HasExplicitAttire)
            {
                // Entry.Folder has already been switched to this job's model.
                // Keep the original validated association instead of treating
                // that rewritten field as new authorization for any source.
                if (job.Entry.Id != job.AttireLocalLogicalId || !SameSource(job.Entry.Folder, job.AttireLocalSourceFolder)
                    || job.Entry.SourceId != job.AttireLocalSourceId
                    || !string.Equals((job.AttireSourceRelativePath ?? "").Replace('\\', '/'),
                        job.AttireLocalSourceRelativePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The scheduled local attire source changed. Choose its attires again.");
                if (job.AttireLocalSourceMustRemainMapped)
                {
                    var proof = result.Sources.Where(source => source.LogicalId == job.Entry.Id
                        && SameSource(source.SourceFolder, job.AttireLocalSourceFolder)).ToList();
                    if (proof.Count == 0 || LocalSourceId(job.Entry, job.Entry.Folder, proof) != job.AttireLocalSourceId)
                        throw new InvalidOperationException("The selected local attire no longer belongs to an active Discovery source for this wrestler. Refresh its attire choices.");
                }
                DiscoveryAttireOptions.ValidateLocalSelection(job.Entry.Folder, job.Entry.Folder, job.AttireSourceRelativePath);
            }
            return result;
        }

        private static BatchAttireSelection VerifyDiscoveryChoice(RosterEntry entry, BatchAttireSelection choice, DiscoveryAttireCatalogResult catalog)
        {
            DiscoveryAttireSource source = catalog.Sources.FirstOrDefault(item => item.LogicalId == entry.Id
                && string.Equals(DiscoveryChoice(item).DiscoveryKey, choice.DiscoveryKey, StringComparison.Ordinal)
                && SameSource(item.SourceFolder, choice.SourceFolder)
                && string.Equals(item.RelativePath.Replace('\\', '/'), (choice.RelativePath ?? "").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                && (choice.SourceId <= 0 || item.SourceId == choice.SourceId)
                && (choice.DiscoveryModelHash == 0 || item.ModelHash == choice.DiscoveryModelHash));
            if (source == null) throw new InvalidOperationException("The selected attire for " + entry.Name
                + " is no longer an active, available Discovery costume at that exact model path. Refresh its attire choices."
                + (catalog.Issues.Count > 0 ? " " + string.Join(" ", catalog.Issues.Take(3)) : ""));
            var verified = DiscoveryChoice(source);
            verified.RelativePath = AlternateAttires.Validate(verified.SourceFolder, verified.RelativePath);
            return verified;
        }

        private static bool SameSource(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static BatchRenderPlan ExpandAttires(BatchRenderPlan original, IDictionary<int, List<BatchAttireSelection>> selections, bool verifiedDiscovery)
        {
            if (original == null) throw new ArgumentNullException("original");
            var plan = new BatchRenderPlan(); plan.Skipped.AddRange(original.Skipped);
            foreach (BatchRenderJob source in original.Jobs)
            {
                List<BatchAttireSelection> selected;
                if (selections == null || !selections.TryGetValue(source.Entry.Id, out selected) || selected == null || selected.Count == 0)
                {
                    plan.Jobs.Add(new BatchRenderJob { Entry = Copy(source.Entry), ExtractFromGame = source.ExtractFromGame, Status = source.ExtractFromGame ? "Load from installed game" : "Ready" });
                    continue;
                }
                if (source.ExtractFromGame || !Directory.Exists(source.Entry.Folder))
                    throw new InvalidOperationException("Load " + source.Entry.Name + " once from the normal roster before choosing its attires.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BatchAttireSelection choice in selected)
                {
                    if (choice != null && !choice.IsAvailable) throw new InvalidOperationException("The selected attire for " + source.Entry.Name + " is unavailable: " + choice.UnavailableReason);
                    if (choice == null || string.IsNullOrWhiteSpace(choice.RelativePath)) throw new ArgumentException("Choose an actual attire model for " + source.Entry.Name + ".");
                    string folder = choice.SourceFolder ?? source.Entry.Folder;
                    if (!verifiedDiscovery && (!string.IsNullOrWhiteSpace(choice.DiscoveryKey) || !SameSource(folder, source.Entry.Folder)))
                        throw new InvalidOperationException("Use CreateWithAttires to verify Discovery choices across model sources.");
                    string relative = AlternateAttires.Validate(folder, choice.RelativePath);
                    if (!seen.Add(SelectionIdentity(choice, source.Entry.Folder))) continue;
                    RosterEntry entry = Copy(source.Entry); entry.Folder = folder;
                    if (verifiedDiscovery && choice.SourceId > 0) entry.SourceId = choice.SourceId;
                    else if (string.IsNullOrWhiteSpace(choice.DiscoveryKey))
                        entry.SourceId = CharacterFolders.PhysicalId(folder) ?? entry.SourceId;
                    plan.Jobs.Add(new BatchRenderJob { Entry = entry, Status = "Ready", HasExplicitAttire = true,
                        AttireSourceRelativePath = relative, AttireName = string.IsNullOrWhiteSpace(choice.DisplayName) ? relative : choice.DisplayName,
                        AttireDiscoveryKey = choice.DiscoveryKey, AttireDiscoveryModelHash = choice.DiscoveryModelHash,
                        AttireLocalSourceMustRemainMapped = choice.RequiresDiscoverySourceProof,
                        AttireLocalSourceFolder = string.IsNullOrWhiteSpace(choice.DiscoveryKey) ? Path.GetFullPath(folder) : null,
                        AttireLocalSourceId = string.IsNullOrWhiteSpace(choice.DiscoveryKey) ? entry.SourceId : 0,
                        AttireLocalLogicalId = string.IsNullOrWhiteSpace(choice.DiscoveryKey) ? entry.Id : 0,
                        AttireLocalSourceRelativePath = string.IsNullOrWhiteSpace(choice.DiscoveryKey) ? relative : null,
                        VariantIndex = seen.Count });
                }
            }
            return plan;
        }

        public static string AliasAttirePath(BatchRenderJob job, bool directModelWrapped)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (!job.HasExplicitAttire) return null;
            string relative = string.IsNullOrWhiteSpace(job.AttireDiscoveryKey)
                ? DiscoveryAttireOptions.ValidateLocalSelection(job.Entry.Folder, job.Entry.Folder, job.AttireSourceRelativePath)
                : AlternateAttires.Validate(job.Entry.Folder, job.AttireSourceRelativePath);
            return directModelWrapped ? "Default_Attire/" + relative : relative;
        }

        /// <summary>Retains the verified mapped seed when a batch uses current/default clothes.</summary>
        public static string LoadedAttire(BatchRenderJob job, bool restoreResult, string explicitAliasPath,
            bool mapped, string mappedSelection, string previousSelection, bool samePreviousModel)
        {
            if (restoreResult && job != null && job.AttireRelativePath != null) return job.AttireRelativePath;
            if (job != null && job.HasExplicitAttire)
            {
                if (string.IsNullOrWhiteSpace(explicitAliasPath)) throw new InvalidOperationException("The explicit batch attire is unavailable.");
                return explicitAliasPath;
            }
            if (mapped)
            {
                if (string.IsNullOrWhiteSpace(mappedSelection)) throw new InvalidOperationException("The current source has no selected active Discovery attire.");
                return mappedSelection;
            }
            return samePreviousModel ? previousSelection : null;
        }

        public static string WorkspaceName(BatchRenderJob job)
        {
            if (job == null || job.Entry == null) throw new ArgumentException("A wrestler job is required.", "job");
            return job.Entry.Id.ToString(CultureInfo.InvariantCulture) + (job.HasExplicitAttire
                ? "_attire" + job.VariantIndex.ToString("00", CultureInfo.InvariantCulture) + "_" + AttireFileLabel(job.AttireName) : "");
        }

        private static string AttireFileLabel(string text)
        {
            string label = new string((text ?? "Attire").Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray()).Trim('_');
            if (label.Length == 0) label = "Attire";
            return label.Substring(0, Math.Min(48, label.Length));
        }

        /// <summary>Copies a verified job into the ordinary output folder without replacing an existing render.</summary>
        public static string PublishCompleted(BatchRenderJob job, string outputDirectory)
        {
            return PublishCompleted(job, outputDirectory, CancellationToken.None);
        }

        public static string PublishCompleted(BatchRenderJob job, string outputDirectory, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (job == null || job.Entry == null) throw new ArgumentException("A completed batch job is required.", "job");
            string reason;
            if (!VerifyCompleted(job.RenderPath, job.Entry.Id, job.OutputDirectory, out reason)) throw new InvalidDataException(reason);
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("The ordinary render output folder is unavailable.", "outputDirectory");
            string root = Path.GetFullPath(outputDirectory);
            string source = Path.GetFullPath(job.RenderPath), name = Path.GetFileName(source);
            if (job.HasExplicitAttire) name = Path.GetFileNameWithoutExtension(name) + "_attire" + job.VariantIndex.ToString("00", CultureInfo.InvariantCulture)
                + "_" + AttireFileLabel(job.AttireName) + ".gmr";
            string prefix = job.Entry.Id.ToString(CultureInfo.InvariantCulture) + "_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !string.Equals(Path.GetExtension(name), ".gmr", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The completed render filename does not match its logical wrestler ID.");
            string temporary = null;
            try
            {
                Directory.CreateDirectory(root);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                {
                    byte[] sourceHash = sha.ComputeHash(input);
                    cancellation.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(job.PublishedRenderPath)
                        && SameOutputFolder(Path.GetDirectoryName(Path.GetFullPath(job.PublishedRenderPath)), root)
                        && File.Exists(job.PublishedRenderPath) && FileHash(job.PublishedRenderPath).SequenceEqual(sourceHash)
                        && VerifyCompleted(job.PublishedRenderPath, job.Entry.Id, root, out reason)) return job.PublishedRenderPath;
                    temporary = Path.Combine(root, ".gorilla-batch-" + Guid.NewGuid().ToString("N") + ".tmp");
                    input.Position = 0;
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, count);
                        }
                        output.Flush(true);
                    }
                    input.Position = 0;
                    if (!sha.ComputeHash(input).SequenceEqual(sourceHash) || !FileHash(temporary).SequenceEqual(sourceHash)
                        || !VerifyCompleted(temporary, job.Entry.Id, root, out reason))
                        throw new IOException("The completed render changed while it was being saved. Its workspace files were kept.");
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string destination = Path.Combine(root, attempt == 0 ? name : Path.GetFileNameWithoutExtension(name)
                            + "_batch-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".gmr");
                        if (File.Exists(destination) || Directory.Exists(destination)) continue;
                        if (!CommitNewOutput(temporary, destination)) continue;
                        temporary = null;
                        job.PublishedRenderPath = destination;
                        return destination;
                    }
                    throw new IOException("A unique output filename could not be created. The completed render remains in its batch workspace.");
                }
            }
            finally
            {
                if (temporary != null)
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static bool SameOutputFolder(string first, string second)
        {
            return string.Equals(first?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                second?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        // File.Move on some Unix runtimes checks existence before rename, allowing
        // another publisher to win that gap and be replaced. Both native operations
        // below fail atomically when the destination exists. Staging is in the same
        // output directory, so Windows redirected/UNC paths need no hard links.
        private static bool CommitNewOutput(string source, string destination)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (MoveNewWindows(source, destination)) return true;
                int error = Marshal.GetLastWin32Error();
                if (error == 80 || error == 183) return false;
                throw new IOException("The completed output could not be saved without replacing an existing file.", new Win32Exception(error));
            }
            if (LinkNewUnix(source, destination) == 0) { File.Delete(source); return true; }
            int unixError = Marshal.GetLastWin32Error();
            if (unixError == 17) return false;
            throw new IOException("The completed output could not be saved without replacing an existing file.", new Win32Exception(unixError));
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveNewWindows(string source, string destination);

        [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern int LinkNewUnix(string source, string destination);

        private static byte[] FileHash(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return sha.ComputeHash(input);
        }

        /// <summary>A job is complete only when its isolated output contains a valid GMR for its logical slot.</summary>
        public static bool VerifyCompleted(string path, int expectedId, string outputDirectory, out string reason)
        {
            reason = "The renderer did not return a completed file for this job.";
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(outputDirectory) || expectedId < 1 || expectedId > 65535) return false;
            try
            {
                string root = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(root, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                { reason = "The result belongs to another job's output folder."; return false; }
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (stream.Length < 60) return false;
                    stream.Position = stream.Length - 8;
                    uint size = reader.ReadUInt32();
                    if (reader.ReadUInt32() != 0x31524D47 || size == 0 || size > 16 * 1024 * 1024 || size > stream.Length - 60) return false;
                    long videoLength = stream.Length - 8 - size;
                    stream.Position = 0;
                    if (reader.ReadUInt32() != 0x694B4942 || (long)reader.ReadUInt32() + 8 != videoLength) return false;
                    uint frames = reader.ReadUInt32();
                    reader.ReadUInt32();
                    if (frames == 0 || frames != reader.ReadUInt32() || 44L + (frames + 1L) * 4 > videoLength) return false;
                    if (reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 1024) return false;
                    uint fpsNumerator = reader.ReadUInt32(), fpsDenominator = reader.ReadUInt32();
                    if (fpsDenominator == 0 || fpsNumerator != fpsDenominator * 30L || (reader.ReadUInt32() & 1048576) == 0 || reader.ReadUInt32() != 0) return false;
                    uint previousOffset = 0;
                    for (long frame = 0; frame < frames; frame++)
                    {
                        uint encodedOffset = reader.ReadUInt32(), offset = encodedOffset & ~1U;
                        if ((encodedOffset & 1) == 0 || offset < 44 + (frames + 1L) * 4 || offset >= videoLength || offset <= previousOffset) return false;
                        previousOffset = offset;
                    }
                    if ((reader.ReadUInt32() & ~1U) != videoLength) return false;
                    stream.Position = videoLength;
                    Dictionary<string, object> metadata = PreviewJson.Read(new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)size)));
                    object value;
                    if (metadata == null || !metadata.TryGetValue("format", out value) || (value as string) != "GorillaRender"
                        || !metadata.TryGetValue("version", out value) || !NumericEquals(value, 1)) return false;
                    if (!metadata.TryGetValue("wrestler_id", out value) || !NumericEquals(value, expectedId))
                    { reason = "The completed render targets a different wrestler ID."; return false; }
                }
                reason = null;
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is FormatException || error is OverflowException || error is NotSupportedException
                || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is System.Text.Json.JsonException
#endif
                )
            { reason = "The completed render could not be verified: " + error.Message; return false; }
        }

        private static bool NumericEquals(object value, int expected)
        {
            if (value == null || value is string || value is bool || value.GetType().IsEnum) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal:
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture) == expected;
                default: return false;
            }
        }
    }
}
