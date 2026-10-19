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
        public bool HasExplicitAttire { get; internal set; }
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

        public static BatchRenderPlan WithAttires(BatchRenderPlan original, IDictionary<int, List<BatchAttireSelection>> selections)
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
                    if (choice == null || string.IsNullOrWhiteSpace(choice.RelativePath)) throw new ArgumentException("Choose an actual attire model for " + source.Entry.Name + ".");
                    string relative = AlternateAttires.Validate(source.Entry.Folder, choice.RelativePath);
                    if (!seen.Add(relative)) continue;
                    plan.Jobs.Add(new BatchRenderJob { Entry = Copy(source.Entry), Status = "Ready", HasExplicitAttire = true,
                        AttireSourceRelativePath = relative, AttireName = string.IsNullOrWhiteSpace(choice.DisplayName) ? relative : choice.DisplayName,
                        VariantIndex = seen.Count });
                }
            }
            return plan;
        }

        public static string AliasAttirePath(BatchRenderJob job, bool directModelWrapped)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (!job.HasExplicitAttire) return null;
            string relative = AlternateAttires.Validate(job.Entry.Folder, job.AttireSourceRelativePath);
            return directModelWrapped ? "Default_Attire/" + relative : relative;
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
