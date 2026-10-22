using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public enum CompletedRenderScreenMode { KeepOriginal, RosterSelection, Both }

    public sealed class CompletedRenderScreenPlanReport
    {
        public List<CompletedRenderEntry> Entries { get; } = new List<CompletedRenderEntry>();
        public List<string> Failed { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public string StagingDirectory { get; internal set; }
        internal readonly List<string> OwnedFiles = new List<string>(), OwnedDirectories = new List<string>();
    }

    /// <summary>Prepares saved movies for selection without rendering or editing saved sources.</summary>
    public static class CompletedRenderScreenPlan
    {
        private const int MaximumMapBytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly StringComparer Paths = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static CompletedRenderScreenPlanReport Prepare(IEnumerable<CompletedRenderEntry> chosen, string stagingRoot,
            CompletedRenderScreenMode mode, CancellationToken cancellation)
        {
            var report = new CompletedRenderScreenPlanReport();
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (mode != CompletedRenderScreenMode.KeepOriginal && mode != CompletedRenderScreenMode.RosterSelection && mode != CompletedRenderScreenMode.Both)
                    throw new InvalidDataException("Choose a supported screen setting.");
                var rows = (chosen ?? Enumerable.Empty<CompletedRenderEntry>()).ToList();
                if (rows.Count == 0 || rows.Count > 4096 || rows.Any(row => row == null)) throw new InvalidDataException("Choose one or more completed renders (at most 4096 per install).");
                if (rows.GroupBy(row => row.WrestlerId).Any(group => group.Count() > 1)) throw new InvalidDataException("Choose one completed render for each logical wrestler ID.");
                var fresh = new List<CompletedRenderEntry>();
                foreach (CompletedRenderEntry requested in rows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    CompletedRenderEntry entry = CompletedRenderCatalog.Scan(new[] { requested.Path }, null, cancellation).Entries.SingleOrDefault();
                    if (entry == null || !entry.IsValid || entry.WrestlerId != requested.WrestlerId)
                        throw new InvalidDataException("A saved result changed or is not installable: " + (requested.Path ?? "unknown source") + ". " + entry?.Error);
                    entry.AttireName = requested.AttireName; fresh.Add(entry);
                }
                if (mode == CompletedRenderScreenMode.KeepOriginal) { report.Entries.AddRange(fresh); return report; }
                if (string.IsNullOrWhiteSpace(stagingRoot)) throw new InvalidDataException("A private staging folder is required.");
                string root = Path.GetFullPath(stagingRoot);
                if (!SafePath(root)) throw new InvalidDataException("The private staging root must not be linked.");
                Directory.CreateDirectory(root);
                report.StagingDirectory = Path.Combine(root, "selection-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(report.StagingDirectory) || File.Exists(report.StagingDirectory)) throw new IOException("The private staging location already exists.");
                Directory.CreateDirectory(report.StagingDirectory); report.OwnedDirectories.Add(report.StagingDirectory);
                int index = 0;
                foreach (CompletedRenderEntry source in fresh)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string folder = Path.Combine(report.StagingDirectory, (++index).ToString("D4", CultureInfo.InvariantCulture) + "_" + source.WrestlerId.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(folder); report.OwnedDirectories.Add(folder);
                    long videoLength;
                    Dictionary<string, object> metadata;
                    if (!GorillaRenderMetadata.InspectCompleted(source.Path, source.WrestlerId, out videoLength, out metadata))
                        throw new InvalidDataException("The saved movie changed before screen preparation: " + source.Path);
                    var sourceInfo = new FileInfo(source.Path);
                    long sourceLength = sourceInfo.Length; DateTime sourceTime = sourceInfo.LastWriteTimeUtc;
                    if (sourceLength != source.Bytes || sourceTime != source.LastWriteTimeUtc)
                        throw new InvalidDataException("The saved source changed after it was selected. Refresh the completed results and retry.");
                    bool embedded;
                    if (!DirectPictures(metadata, out embedded)) throw new InvalidDataException("The saved movie has malformed roster picture links: " + source.Path);
                    string mapPath = Path.Combine(source.OutputDirectory, "_roster_pictures.txt");
                    byte[] map = null;
                    bool mapRoute = false;
                    DateTime mapTime = default(DateTime);
                    if (File.Exists(mapPath))
                    {
                        if (!SafePath(mapPath)) throw new InvalidDataException("The saved roster picture map is linked: " + mapPath);
                        mapTime = File.GetLastWriteTimeUtc(mapPath);
                        map = ReadBounded(mapPath, MaximumMapBytes, cancellation);
                        mapRoute = MapContains(map, source.WrestlerId, cancellation);
                    }
                    if (!embedded && !mapRoute)
                        throw new InvalidDataException(source.Name + " (" + source.WrestlerId.ToString(CultureInfo.InvariantCulture)
                            + ") has no usable saved roster picture names/hashes or own-map association within the first four owner slots. Its movie can be kept, but selection linking cannot be guessed. Restore its original saved picture map or use Keep file settings.");
                    string staged = Path.Combine(folder, Path.GetFileName(source.Path));
                    report.OwnedFiles.Add(staged);
                    Copy(source.Path, staged, cancellation);
                    if (!GorillaRenderMetadata.SetScreens(staged, source.WrestlerId, mode == CompletedRenderScreenMode.RosterSelection ? "select" : "both"))
                        throw new InvalidDataException("The saved movie's screen setting could not be prepared: " + source.Path);
                    long stagedVideoLength;
                    Dictionary<string, object> stagedMetadata;
                    if (!GorillaRenderMetadata.InspectCompleted(staged, source.WrestlerId, out stagedVideoLength, out stagedMetadata)
                        || videoLength != stagedVideoLength || !GorillaRenderMetadata.SameExceptScreens(metadata, stagedMetadata)
                        || !SameVideo(source.Path, staged, videoLength, cancellation))
                        throw new InvalidDataException("The prepared movie's video differs from its saved source; nothing was installed.");
                    bool stagedEmbedded;
                    if (!DirectPictures(stagedMetadata, out stagedEmbedded) || (!stagedEmbedded && !mapRoute))
                        throw new InvalidDataException("The saved roster picture links changed during preparation.");
                    if (map != null)
                    {
                        if (File.GetLastWriteTimeUtc(mapPath) != mapTime || new FileInfo(mapPath).Length != map.Length)
                            throw new InvalidDataException("The saved roster picture map changed during preparation.");
                        string targetMap = Path.Combine(folder, "_roster_pictures.txt"); report.OwnedFiles.Add(targetMap);
                        Write(targetMap, map, cancellation);
                    }
                    PreservePolicy(source, staged, report, cancellation);
                    sourceInfo.Refresh();
                    if (!sourceInfo.Exists || sourceInfo.Length != sourceLength || sourceInfo.LastWriteTimeUtc != sourceTime)
                        throw new InvalidDataException("The saved source changed during preparation. Refresh the completed results and retry.");
                    var prepared = CompletedRenderCatalog.Scan(new[] { staged }, null, cancellation).Entries.SingleOrDefault();
                    if (prepared == null || !prepared.IsValid || prepared.WrestlerId != source.WrestlerId)
                        throw new InvalidDataException("The prepared result failed final verification.");
                    prepared.AttireName = source.AttireName;
                    prepared.SourceDescription = "Prepared saved result for " + (mode == CompletedRenderScreenMode.RosterSelection ? "roster selection" : "both screens");
                    report.Entries.Add(prepared);
                }
            }
            catch (OperationCanceledException) { report.Entries.Clear(); Cleanup(report); throw; }
            catch (Exception error) when (Expected(error)) { report.Entries.Clear(); report.Failed.Add(error.Message); Cleanup(report); }
            return report;
        }

        /// <summary>Deletes only paths created by this plan, never a source or arbitrary recursive tree.</summary>
        public static bool Cleanup(CompletedRenderScreenPlanReport report)
        {
            if (report == null || report.StagingDirectory == null) return true;
            try
            {
                foreach (string path in report.OwnedFiles.AsEnumerable().Reverse())
                {
                    if (!Within(report.StagingDirectory, path, false) || !SafePath(path)) return false;
                    if (File.Exists(path)) File.Delete(path);
                }
                foreach (string path in report.OwnedDirectories.AsEnumerable().Reverse())
                {
                    if (!Within(report.StagingDirectory, path, true) || !SafePath(path)) return false;
                    if (Directory.Exists(path)) Directory.Delete(path, false);
                }
                return true;
            }
            catch (Exception error) when (Expected(error)) { return false; }
        }

        private static bool DirectPictures(Dictionary<string, object> metadata, out bool usable)
        {
            usable = false;
            foreach (string field in new[] { "render_hashes", "render_names" })
            {
                object value;
                if (!metadata.TryGetValue(field, out value)) continue;
                var values = value as IList;
                if (values == null || values.Count > 64) return false;
                foreach (object item in values)
                {
                    string text = item as string;
                    if (text == null || text.IndexOf('\0') >= 0) return false;
                    if (field == "render_hashes")
                    {
                        ulong hash;
                        if (text.Length != 16 || !ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash)) return false;
                        if (hash != 0) usable = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(text)) usable = true;
                }
            }
            return true;
        }

        private static bool MapContains(byte[] map, int id, CancellationToken cancellation)
        {
            var routeByHash = new Dictionary<ulong, bool>();
            using (var text = new StringReader(Utf8.GetString(map).TrimStart('\ufeff')))
            {
                string line; int rows = 0;
                while ((line = text.ReadLine()) != null)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (++rows > 65536 || line.Length > 65536) throw new InvalidDataException("The saved roster picture map exceeds the bounded reader limits.");
                    string[] words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    ulong hash;
                    if (words.Length < 2 || words[0].Length != 16 || !ulong.TryParse(words[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash) || hash == 0)
                        throw new InvalidDataException("The saved roster picture map contains an invalid hash row.");
                    bool found = false; int slot = 0;
                    foreach (string word in words.Skip(1))
                    {
                        int owner;
                        if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out owner) || owner < 1 || owner > 65535)
                            throw new InvalidDataException("The saved roster picture map contains an invalid owner ID.");
                        if (slot++ < 4 && owner == id) found = true;
                    }
                    bool previous;
                    // Duplicate-hash precedence is not needed for proof when
                    // every occurrence includes this owner in its native slots.
                    routeByHash[hash] = !routeByHash.TryGetValue(hash, out previous) ? found : previous && found;
                }
            }
            // The installer compares normalized maps but copies their original
            // bytes. Native owner capacity therefore applies in source-file order.
            return routeByHash.Values.Any(found => found);
        }

        private static void PreservePolicy(CompletedRenderEntry source, string staged, CompletedRenderScreenPlanReport report, CancellationToken cancellation)
        {
            string sidecar = SelectionLoopPolicy.SidecarPath(source.Path);
            if (!File.Exists(sidecar)) return;
            cancellation.ThrowIfCancellationRequested();
            SelectionLoopPolicyRecord original;
            if (!SafePath(sidecar) || !SelectionLoopPolicy.Validate(source.Path, sidecar, source.WrestlerId, out original))
            { report.Warnings.Add("The saved selection-loop sidecar is invalid and was not attached to the prepared movie: " + Path.GetFileName(source.Path)); return; }
            string target = SelectionLoopPolicy.SidecarPath(staged);
            report.OwnedFiles.Add(target);
            if (!SelectionLoopPolicy.TryWrite(staged, source.WrestlerId, new Dictionary<string, object> {
                { "id", source.WrestlerId }, { "frames", original.MotionFrames }, { "end", "freeze" }
            })) throw new InvalidDataException("The verified selection-loop policy could not be rebound to the unchanged movie video.");
            SelectionLoopPolicyRecord rebound;
            if (!SelectionLoopPolicy.Validate(staged, target, source.WrestlerId, out rebound) || rebound.MotionFrames != original.MotionFrames
                || rebound.VideoFrames != original.VideoFrames || rebound.Fps != original.Fps || rebound.Ending != original.Ending)
                throw new InvalidDataException("The prepared selection-loop policy changed its authorized frame range.");
            cancellation.ThrowIfCancellationRequested();
        }

        private static bool SameVideo(string left, string right, long length, CancellationToken cancellation)
        {
            using (var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] aa = new byte[81920], bb = new byte[81920];
                while (length > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(length, aa.Length);
                    if (ReadChunk(a, aa, count) != count || ReadChunk(b, bb, count) != count) return false;
                    for (int n = 0; n < count; n++) if (aa[n] != bb[n]) return false;
                    length -= count;
                }
                return true;
            }
        }

        private static void Copy(string source, string target, CancellationToken cancellation)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
        }
        private static void Write(string path, byte[] bytes, CancellationToken cancellation)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { cancellation.ThrowIfCancellationRequested(); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        private static byte[] ReadBounded(string path, int maximum, CancellationToken cancellation)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maximum) throw new InvalidDataException("The saved roster picture map exceeds its read limit.");
                byte[] bytes = new byte[(int)stream.Length]; int read = 0;
                while (read < bytes.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int next = stream.Read(bytes, read, Math.Min(81920, bytes.Length - read));
                    if (next == 0) throw new EndOfStreamException("The saved roster picture map changed during reading."); read += next;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("The saved roster picture map changed during reading.");
                return bytes;
            }
        }
        private static int ReadChunk(Stream stream, byte[] bytes, int count)
        { int read = 0, next; while (read < count && (next = stream.Read(bytes, read, count - read)) > 0) read += next; return read; }
        private static bool SafePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (current != null)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                current = Path.GetDirectoryName(current);
            }
            return true;
        }
        private static bool Within(string root, string path, bool allowRoot)
        {
            if (allowRoot && Paths.Equals(root, path)) return true;
            return path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        private static bool Expected(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException
                || error is FormatException || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException;
        }
    }
}
