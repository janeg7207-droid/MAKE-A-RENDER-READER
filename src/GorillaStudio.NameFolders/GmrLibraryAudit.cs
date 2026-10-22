using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public sealed class GmrLibraryRecord
    {
        public string Path { get; internal set; }
        public int? WrestlerId { get; internal set; }
        public List<int> NativeIds { get; } = new List<int>();
        public List<int> ShadowedIds { get; } = new List<int>();
        public List<string> Problems { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public bool HeaderCompatible { get { return Problems.Count == 0 && WrestlerId.HasValue; } }
    }

    public sealed class GmrLibraryAuditReport
    {
        public bool Original421Verified { get; internal set; }
        public int RootFiles { get; internal set; }
        public int NestedFiles { get; internal set; }
        public bool Complete { get; internal set; } = true;
        public List<GmrLibraryRecord> Records { get; } = new List<GmrLibraryRecord>();
        public List<string> Problems { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public int HeaderCompatibleFiles { get { return Records.Count(item => item.HeaderCompatible); } }
    }

    /// <summary>
    /// Read-only diagnostics for the measured original MovingRenders 4.21 scan.
    /// Header compatibility cannot prove that the native video decoder or hook runs.
    /// </summary>
    public static class GmrLibraryAudit
    {
        private const string Original421Sha256 = "b752da3c9a9dc48dc377972a7183db0194c2b3b551074f7d0f0c9766432bf75c";
        private const int MaximumEntries = 8192, MaximumDirectories = 512, MaximumDepth = 8;
        private const int MaximumMetadataBytes = 65536;
        private const long MaximumInspectionBytes = 32L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static GmrLibraryAuditReport Inspect(string directory, string pluginPath, CancellationToken cancellation)
        {
            var report = new GmrLibraryAuditReport();
            long budget = MaximumInspectionBytes;
            cancellation.ThrowIfCancellationRequested();
            try { report.Original421Verified = Original421(pluginPath, cancellation); }
            catch (Exception error) when (Expected(error))
            { report.Notes.Add("The installed plugin identity could not be verified: " + error.Message); }
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                { report.Notes.Add("The installed GorillaRenders folder does not exist."); return report; }
                string root = System.IO.Path.GetFullPath(directory);
                var pending = new Stack<Tuple<string, int>>(); pending.Push(Tuple.Create(root, 0));
                int entries = 0, directories = 0;
                while (pending.Count != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var current = pending.Pop();
                    if (++directories > MaximumDirectories)
                    { Incomplete(report, "The installed library has too many subfolders for this bounded check."); break; }
                    if ((File.GetAttributes(current.Item1) & FileAttributes.ReparsePoint) != 0)
                    { Incomplete(report, "A linked library folder was not followed: " + Relative(root, current.Item1)); continue; }
                    try
                    {
                        foreach (string entry in Directory.EnumerateFileSystemEntries(current.Item1))
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (++entries > MaximumEntries)
                            { Incomplete(report, "The installed library check reached its file-count limit."); pending.Clear(); break; }
                            FileAttributes attributes;
                            try { attributes = File.GetAttributes(entry); }
                            catch (Exception error) when (Expected(error))
                            { Incomplete(report, "A library entry could not be inspected: " + Relative(root, entry) + " (" + error.Message + ")"); continue; }
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            { Incomplete(report, "A linked library entry was not followed: " + Relative(root, entry)); continue; }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (current.Item2 < MaximumDepth) pending.Push(Tuple.Create(entry, current.Item2 + 1));
                                else Incomplete(report, "A deeply nested library folder was not inspected: " + Relative(root, entry));
                                continue;
                            }
                            if (!string.Equals(System.IO.Path.GetExtension(entry), ".gmr", StringComparison.OrdinalIgnoreCase)) continue;
                            if (current.Item2 != 0) { report.NestedFiles++; continue; }
                            report.RootFiles++;
                            if (budget <= 0)
                            { Incomplete(report, "The installed library check reached its header/metadata byte limit."); continue; }
                            report.Records.Add(Read(entry, ref budget, cancellation));
                            if (budget <= 0) Incomplete(report, "The installed library check reached its header/metadata byte limit.");
                        }
                    }
                    catch (Exception error) when (Expected(error))
                    { Incomplete(report, "A library folder could not be inspected: " + Relative(root, current.Item1) + " (" + error.Message + ")"); }
                }
                if (report.RootFiles > 256)
                    report.Problems.Add("The installed root contains " + report.RootFiles.ToString(CultureInfo.InvariantCulture)
                        + " GMR files. Original MovingRenders 4.21 stops after 256 accepted records; additional files may not load. The native directory scan order is unknown here.");
                if (report.NestedFiles != 0)
                    report.Problems.Add(report.NestedFiles.ToString(CultureInfo.InvariantCulture)
                        + " GMR file(s) are in subfolders. Original MovingRenders 4.21 scans only files directly inside Plugins/GorillaRenders.");
                // Native sorting and duplicate suppression happen after its capacity
                // cutoff. Never claim the same active set when that cutoff is possible.
                var claimed = new Dictionary<int, string>();
                foreach (GmrLibraryRecord record in report.Records.Where(item => item.HeaderCompatible)
                    .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (int id in record.NativeIds.Distinct())
                    {
                        string earlier;
                        if (claimed.TryGetValue(id, out earlier))
                        {
                            record.ShadowedIds.Add(id);
                            report.Problems.Add("Wrestler ID " + id.ToString(CultureInfo.InvariantCulture) + " is claimed by both "
                                + System.IO.Path.GetFileName(earlier) + " and " + System.IO.Path.GetFileName(record.Path)
                                + ". Original 4.21 suppresses later ID claims before checking selection/match-card settings.");
                        }
                        else claimed[id] = record.Path;
                    }
                }
                if (report.RootFiles > 256)
                    report.Notes.Add("Duplicate findings identify conflicting files; they do not predict which files survive the native 256-record cutoff.");
                report.Notes.Add("One installed moving render is selected per logical wrestler ID; separate saved attire files do not automatically switch with the game's attire selection.");
                report.Notes.Add("Header checks do not decode the movies or confirm the running plugin/game hook. Inspect WWE2K26_MovingRenders.log and GorillaRenders/_scan_ids.txt for the native scan result.");
            }
            catch (Exception error) when (Expected(error))
            { Incomplete(report, "The installed GMR library could not be inspected: " + error.Message); }
            return report;
        }

        private static GmrLibraryRecord Read(string path, ref long budget, CancellationToken cancellation)
        {
            var record = new GmrLibraryRecord { Path = path };
            try
            {
                if (path.Length >= 260) record.Problems.Add("The path exceeds the original plugin's 259-character path buffer.");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new BinaryReader(stream, Utf8, true))
                {
                    long beforeLength = stream.Length;
                    if (beforeLength < 64 || beforeLength >= 2147483648L)
                    { record.Problems.Add("The file size is outside the original plugin's supported range."); return record; }
                    stream.Position = beforeLength - 8;
                    uint size = reader.ReadUInt32();
                    if (reader.ReadUInt32() != 0x31524d47)
                    { record.Problems.Add("Missing GMR1 footer; this may be incomplete output or a different format."); return record; }
                    if (size < 1 || size > MaximumMetadataBytes || size > beforeLength - 52)
                    { record.Problems.Add("Metadata is missing or exceeds the original plugin's 65,536-byte limit."); return record; }
                    long videoLength = beforeLength - 8 - size;
                    stream.Position = 0;
                    if (reader.ReadUInt32() != 0x694b4942 || reader.ReadUInt32() + 8L != videoLength)
                    { record.Problems.Add("The original plugin requires a complete BIKi video before the metadata."); return record; }
                    uint frames = reader.ReadUInt32(), largest = reader.ReadUInt32(), repeated = reader.ReadUInt32();
                    if (frames < 1 || frames > 100000 || repeated != frames || 44L + (frames + 1L) * 4 > videoLength)
                    { record.Problems.Add("The frame count or frame-index boundary is invalid for the original plugin."); return record; }
                    if (reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 30 || reader.ReadUInt32() != 1
                        || (reader.ReadUInt32() & 1048576) == 0 || reader.ReadUInt32() != 0)
                    { record.Problems.Add("The original plugin requires 1024×1024 BIKi at exactly 30/1 fps, alpha and no audio."); return record; }
                    long inspected = size + 52L + (frames + 1L) * 4;
                    if (inspected > budget)
                    { budget = 0; record.Problems.Add("Header/metadata byte budget reached; this file was not fully inspected."); return record; }
                    budget -= inspected;
                    uint previous = reader.ReadUInt32(), first = previous & ~1U;
                    bool allKeys = (previous & 1) != 0;
                    if (first != 44L + (frames + 1L) * 4)
                    { record.Problems.Add("The first video packet does not follow its complete frame index."); return record; }
                    for (uint frame = 0; frame < frames; frame++)
                    {
                        if ((frame & 255) == 0) cancellation.ThrowIfCancellationRequested();
                        uint current = reader.ReadUInt32(), offset = current & ~1U;
                        uint previousOffset = previous & ~1U;
                        if (offset <= previousOffset || offset - previousOffset > largest || offset > videoLength)
                        { record.Problems.Add("A video packet offset or packet length is invalid."); return record; }
                        if (frame + 1 < frames && (current & 1) == 0) allKeys = false;
                        previous = current;
                    }
                    if ((previous & ~1U) != videoLength)
                    { record.Problems.Add("The final packet index does not match the video boundary."); return record; }
                    if (!allKeys) record.Notes.Add("This video has dependent frames; original 4.21 may not include it in the combined selection cache.");
                    stream.Position = videoLength;
                    byte[] bytes = reader.ReadBytes((int)size);
                    if (bytes.Length != size || stream.Length != beforeLength)
                    { record.Problems.Add("The render changed during inspection."); return record; }
                    var metadata = MatchCardReadiness.Deserialize(Utf8.GetString(bytes));
                    object value; int id;
                    if (metadata == null || !metadata.TryGetValue("format", out value) || (value as string) != "GorillaRender"
                        || !metadata.TryGetValue("wrestler_id", out value) || !CanonicalInteger(value, out id) || id < 1 || id > 65535)
                    { record.Problems.Add("Metadata has no valid GorillaRender format and logical wrestler ID."); return record; }
                    record.WrestlerId = id; record.NativeIds.Add(id);
                    if (metadata.TryGetValue("extra_ids", out value))
                    {
                        var aliases = value as IList;
                        if (aliases == null) { record.Problems.Add("The extra_ids metadata is not a numeric array."); return record; }
                        if (aliases.Count > 64) { record.Problems.Add("The extra_ids array exceeds the original parser's 64-entry limit."); return record; }
                        int ignored = 0;
                        foreach (object alias in aliases)
                        {
                            ulong other;
                            if (!CanonicalNonnegativeInteger(alias, out other))
                            { record.Problems.Add("The extra_ids array contains a non-integer entry."); return record; }
                            if (other < 1 || other > 65535 || record.NativeIds.Count == 4) { ignored++; continue; }
                            record.NativeIds.Add((int)other); // Native callback does not deduplicate within one record.
                        }
                        if (ignored != 0) record.Notes.Add(ignored.ToString(CultureInfo.InvariantCulture)
                            + " extra ID(s) are not consumed by the original plugin; only three valid aliases fit after the primary ID.");
                    }
                    foreach (string field in new[] { "render_hashes", "render_names", "shared_hashes" })
                        if (metadata.TryGetValue(field, out value) && value is IList && ((IList)value).Count > 64)
                            record.Problems.Add(field + " exceeds the original parser's 64-entry array limit.");
                }
            }
            catch (Exception error) when (Expected(error))
            { record.Problems.Add("Could not inspect this file: " + error.Message); }
            return record;
        }

        private static bool Original421(string path, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || new FileInfo(path).Length != 69632
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            cancellation.ThrowIfCancellationRequested();
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), Original421Sha256, StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanonicalInteger(object value, out int result)
        {
            // Native fields are decimal digit tokens, not arbitrary JSON numbers.
            // Do not claim the same ID for a fractional/exponent spelling that a
            // JSON implementation normalizes but the native strtol parses differently.
            ulong number;
            result = 0;
            if (!CanonicalNonnegativeInteger(value, out number) || number > int.MaxValue) return false;
            result = (int)number;
            return true;
        }

        private static bool CanonicalNonnegativeInteger(object value, out ulong result)
        {
            result = 0;
            if (value == null) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                    try { result = Convert.ToUInt64(value, CultureInfo.InvariantCulture); return true; }
                    catch (OverflowException) { return false; }
                default: return false;
            }
        }

        private static string Relative(string root, string path)
        { return path.Length > root.Length ? path.Substring(root.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : "."; }
        private static void Incomplete(GmrLibraryAuditReport report, string message)
        { report.Complete = false; if (report.Notes.Count < 40 && !report.Notes.Contains(message)) report.Notes.Add(message); }
        private static bool Expected(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException
                || error is FormatException || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is System.Text.Json.JsonException
#endif
                ;
        }
    }
}
