using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class BatchRenderJob
    {
        public RosterEntry Entry { get; internal set; }
        public bool ExtractFromGame { get; internal set; }
        public string Status { get; internal set; }
        public string OutputDirectory { get; internal set; }
        public string RenderPath { get; internal set; }
        public string AttireRelativePath { get; internal set; }
    }

    /// <summary>Snapshots selected logical slots and resolves models without asking per-job questions.</summary>
    public sealed class BatchRenderPlan
    {
        public List<BatchRenderJob> Jobs { get; private set; } = new List<BatchRenderJob>();
        public List<string> Skipped { get; private set; } = new List<string>();

        public static BatchRenderPlan Create(IEnumerable<RosterEntry> selected, IEnumerable<ModelCandidate> inventory, string rosterTable)
        {
            var plan = new BatchRenderPlan();
            var models = (inventory ?? Enumerable.Empty<ModelCandidate>()).ToList();
            var rows = (selected ?? Enumerable.Empty<RosterEntry>()).Where(entry => entry != null).Select(Copy).ToList();
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
