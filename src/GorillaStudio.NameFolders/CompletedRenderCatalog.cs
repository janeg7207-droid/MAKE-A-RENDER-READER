using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public enum CompletedRenderInstallState { NotInstalled, InstalledIdentical, InstalledDifferent, NeedsComparison }

    public sealed class CompletedRenderEntry
    {
        public string Path { get; internal set; }
        public string OutputDirectory { get; internal set; }
        public string PublishedPath { get; internal set; }
        public int WrestlerId { get; internal set; }
        public string Name { get; internal set; }
        public string Taunt { get; internal set; }
        public string AttireName { get; internal set; }
        public string Screens { get; internal set; }
        public string Ending { get; internal set; }
        public int Frames { get; internal set; }
        public long Bytes { get; internal set; }
        public DateTime LastWriteTimeUtc { get; internal set; }
        public string Error { get; internal set; }
        public bool IsValid { get { return string.IsNullOrEmpty(Error); } }
        public CompletedRenderInstallState InstalledState { get; internal set; }
        public string InstalledPath { get; internal set; }
        public string InstallationDetails { get; internal set; }
        public bool HasRosterPictureMap { get; internal set; }
        public bool HasSelectionLoopPolicy { get; internal set; }
        public string SourceDescription { get; internal set; }
        internal List<int> NativeIds = new List<int>();
    }

    public sealed class CompletedRenderCatalogReport
    {
        public List<CompletedRenderEntry> Entries { get; } = new List<CompletedRenderEntry>();
        public List<string> Warnings { get; } = new List<string>();
        public bool Complete { get; internal set; } = true;
        public long ComparisonBytes { get; internal set; }
    }

    /// <summary>
    /// Rediscovers saved full movies independently of the current wrestler/model or
    /// taunt. This is read-only inventory, not proof of native acceptance/playback.
    /// Every chosen source must still pass the installer's fresh validation.
    /// </summary>
    public static class CompletedRenderCatalog
    {
        private const int MaximumEntries = 8192, MaximumDirectories = 1024, MaximumDepth = 10;
        private const int MaximumMetadataBytes = 65536, MaximumManifestBytes = 1024 * 1024;
        private const long MetadataBudget = 64L * 1024 * 1024, ComparisonBudget = 128L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly StringComparer Paths = System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private sealed class ScanState
        {
            internal readonly CompletedRenderCatalogReport Report = new CompletedRenderCatalogReport();
            internal readonly Dictionary<string, CompletedRenderEntry> Files = new Dictionary<string, CompletedRenderEntry>(Paths);
            internal readonly HashSet<string> SourceRoots = new HashSet<string>(Paths), ExplicitFiles = new HashSet<string>(Paths);
            internal readonly HashSet<string> ManifestRoots = new HashSet<string>(Paths), SuppressedCopies = new HashSet<string>(Paths);
            internal bool CacheSkipNoted;
            internal long MetadataRemaining = MetadataBudget, ComparisonRemaining = ComparisonBudget;
            internal bool InstalledInventoryComplete = true;
            internal string Game;
            internal CancellationToken Cancellation;
        }

        public static CompletedRenderCatalogReport Scan(IEnumerable<string> directoriesOrFiles, string gameRoot, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var state = new ScanState { Cancellation = cancellation };
            if (!string.IsNullOrWhiteSpace(gameRoot))
                try { state.Game = System.IO.Path.GetFullPath(gameRoot); }
                catch (Exception error) when (Expected(error)) { Warn(state, "The game folder could not be resolved: " + error.Message); }
            if (state.Game == null || !Directory.Exists(state.Game)) state.InstalledInventoryComplete = false;
            var inputs = new List<string>();
            foreach (string input in directoriesOrFiles ?? Enumerable.Empty<string>())
            {
                cancellation.ThrowIfCancellationRequested();
                if (inputs.Count >= MaximumEntries) { Warn(state, "Too many requested source locations; remaining locations were not scanned."); break; }
                if (string.IsNullOrWhiteSpace(input)) continue;
                try
                {
                    string full = System.IO.Path.GetFullPath(input);
                    if (state.Game != null && Within(state.Game, full, true))
                    { Warn(state, "Saved sources inside the installed game are not recovery inputs: " + full); continue; }
                    if (!SafePath(full)) { Warn(state, "A linked source location was not followed: " + full); continue; }
                    if (Directory.Exists(full)) state.SourceRoots.Add(full);
                    else if (File.Exists(full)) state.ExplicitFiles.Add(full);
                    else continue;
                    if (!inputs.Contains(full, Paths)) inputs.Add(full);
                }
                catch (Exception error) when (Expected(error)) { Warn(state, "A source location could not be read: " + error.Message); }
            }
            Enumerate(state, inputs);
            var installed = ReadInstalled(state);
            foreach (CompletedRenderEntry entry in state.Files.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.IsValid) CompareInstallation(state, entry, installed);
                state.Report.Entries.Add(entry);
            }
            state.Report.Entries.Sort((left, right) => {
                int order = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                if (order == 0) order = left.WrestlerId.CompareTo(right.WrestlerId);
                if (order == 0) order = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                return order == 0 ? Paths.Compare(left.Path, right.Path) : order;
            });
            state.Report.ComparisonBytes = ComparisonBudget - state.ComparisonRemaining;
            return state.Report;
        }

        private static void Enumerate(ScanState state, IEnumerable<string> inputs)
        {
            var pending = new Stack<Tuple<string, int>>();
            foreach (string input in inputs.Where(File.Exists)) AddSource(state, input);
            // A normal output root comes before the private batch tree. Preserve
            // caller order despite the directory stack, so frame caches cannot
            // exhaust the scan before ordinary completed outputs are discovered.
            foreach (string input in inputs.Where(Directory.Exists).Reverse()) pending.Push(Tuple.Create(input, 0));
            var visited = new HashSet<string>(Paths);
            int entries = 0;
            while (pending.Count != 0)
            {
                state.Cancellation.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (!visited.Add(directory.Item1)) continue;
                if (visited.Count > MaximumDirectories) { Warn(state, "The saved-render scan reached its directory limit."); break; }
                if (GeneratedBatchCache(state, directory.Item1)) { NoteCacheSkip(state); continue; }
                try
                {
                    if (!SafePath(directory.Item1)) { Warn(state, "A linked source directory was skipped: " + directory.Item1); continue; }
                    foreach (string item in Directory.EnumerateFileSystemEntries(directory.Item1))
                    {
                        state.Cancellation.ThrowIfCancellationRequested();
                        if (++entries > MaximumEntries) { Warn(state, "The saved-render scan reached its entry limit."); return; }
                        if (state.Game != null && Within(state.Game, item, true)) continue;
                        if (!SafePath(item)) { Warn(state, "A linked source entry was skipped: " + item); continue; }
                        if (Directory.Exists(item))
                        {
                            if (GeneratedBatchCache(state, item)) { NoteCacheSkip(state); continue; }
                            if (directory.Item2 < MaximumDepth) pending.Push(Tuple.Create(item, directory.Item2 + 1));
                            else Warn(state, "A deeply nested source directory was skipped: " + item);
                        }
                        else if (string.Equals(System.IO.Path.GetFileName(item), "batch-results.json", StringComparison.OrdinalIgnoreCase)) RecoverManifest(state, item);
                        else AddSource(state, item);
                    }
                }
                catch (Exception error) when (Expected(error)) { Warn(state, "A saved-render directory could not be scanned: " + directory.Item1 + " (" + error.Message + ")"); }
            }
        }

        private static CompletedRenderEntry AddSource(ScanState state, string path)
        {
            if (!string.Equals(System.IO.Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase)) return null;
            if (state.Game != null && Within(state.Game, path, true)) return null;
            if (state.SuppressedCopies.Contains(path)) return null;
            CompletedRenderEntry entry;
            if (state.Files.TryGetValue(path, out entry)) return entry;
            if (state.Files.Count >= MaximumEntries) { Warn(state, "The saved-render catalog reached its file limit."); return null; }
            entry = Read(state, path, true);
            state.Files[path] = entry;
            return entry;
        }

        private static CompletedRenderEntry Read(ScanState state, string path, bool requireFilename)
        {
            var entry = new CompletedRenderEntry { Path = path, OutputDirectory = System.IO.Path.GetDirectoryName(path),
                Name = System.IO.Path.GetFileNameWithoutExtension(path), SourceDescription = "Saved render", InstalledState = CompletedRenderInstallState.NeedsComparison };
            try
            {
                state.Cancellation.ThrowIfCancellationRequested();
                if (!SafePath(path)) throw new InvalidDataException("Linked source files are not followed.");
                var info = new FileInfo(path);
                entry.Bytes = info.Length; entry.LastWriteTimeUtc = info.LastWriteTimeUtc;
                if (entry.Bytes < 64 || entry.Bytes >= 2147483648L) throw new InvalidDataException("The file size is outside the original plugin's supported range.");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Utf8, true))
                {
                    stream.Position = stream.Length - 8;
                    uint size = reader.ReadUInt32();
                    if (reader.ReadUInt32() != 0x31524D47 || size == 0 || size > MaximumMetadataBytes || size > stream.Length - 60)
                        throw new InvalidDataException("This is not a completed GMR with supported metadata.");
                    long videoLength = stream.Length - 8 - size;
                    stream.Position = 0;
                    if (reader.ReadUInt32() != 0x694B4942 || reader.ReadUInt32() + 8L != videoLength)
                        throw new InvalidDataException("The completed video boundary is invalid.");
                    uint frames = reader.ReadUInt32(), largest = reader.ReadUInt32(), repeated = reader.ReadUInt32();
                    if (frames == 0 || frames > 100000 || repeated != frames || 44L + (frames + 1L) * 4 > videoLength)
                        throw new InvalidDataException("The completed frame index is invalid.");
                    if (reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 30 || reader.ReadUInt32() != 1
                        || (reader.ReadUInt32() & 1048576) == 0 || reader.ReadUInt32() != 0)
                        throw new InvalidDataException("Installable GMRs require 1024×1024 video, 30/1 fps, alpha and no audio.");
                    long cost = 52L + (frames + 1L) * 4 + size;
                    if (cost > state.MetadataRemaining) { Warn(state, "The saved-render scan reached its metadata byte limit."); throw new InvalidDataException("Metadata inspection budget reached; this file was not verified."); }
                    state.MetadataRemaining -= cost;
                    uint previous = reader.ReadUInt32();
                    if ((previous & 1) == 0 || (previous & ~1U) != 44L + (frames + 1L) * 4)
                        throw new InvalidDataException("The completed movie does not contain the expected full-render keyframe index.");
                    for (uint frame = 0; frame < frames; frame++)
                    {
                        if ((frame & 255) == 0) state.Cancellation.ThrowIfCancellationRequested();
                        uint next = reader.ReadUInt32(), offset = next & ~1U, old = previous & ~1U;
                        if (offset <= old || offset - old > largest || offset > videoLength || (frame + 1 < frames && (next & 1) == 0))
                            throw new InvalidDataException("The completed movie frame offsets are invalid or are not full-render keyframes.");
                        previous = next;
                    }
                    if ((previous & ~1U) != videoLength) throw new InvalidDataException("The final frame boundary is invalid.");
                    stream.Position = videoLength;
                    byte[] bytes = reader.ReadBytes((int)size);
                    if (bytes.Length != size || stream.Length != entry.Bytes) throw new InvalidDataException("The completed file changed during inspection.");
                    var metadata = Json(Utf8.GetString(bytes));
                    int id, version;
                    if (metadata == null || Text(metadata, "format") != "GorillaRender" || !Number(metadata, "version", out version) || version != 1
                        || !Number(metadata, "wrestler_id", out id) || id < 1 || id > 65535)
                        throw new InvalidDataException("The completed file has no supported format and logical wrestler ID.");
                    entry.WrestlerId = id; entry.NativeIds.Add(id); entry.Frames = (int)frames;
                    entry.Name = Text(metadata, "name") ?? entry.Name; entry.Taunt = Text(metadata, "taunt");
                    object taunt;
                    if (entry.Taunt == null && metadata.TryGetValue("taunt", out taunt) && taunt is Dictionary<string, object>)
                        entry.Taunt = Text((Dictionary<string, object>)taunt, "name");
                    entry.Screens = Text(metadata, "screens") ?? "both"; entry.Ending = Text(metadata, "end") ?? "loop";
                    object preview;
                    if (metadata.TryGetValue("preview", out preview) && preview is bool && (bool)preview) throw new InvalidDataException("A preview is not a completed installable render.");
                    object aliases;
                    if (metadata.TryGetValue("extra_ids", out aliases) && aliases is IList)
                        foreach (object alias in (IList)aliases)
                        {
                            int other;
                            if (Integer(alias, out other) && other >= 1 && other <= 65535 && entry.NativeIds.Count < 4) entry.NativeIds.Add(other);
                        }
                    if (requireFilename)
                    {
                        Match prefix = Regex.Match(System.IO.Path.GetFileName(path), @"^(\d+)_.*\.gmr$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        int namedId;
                        if (!prefix.Success || !int.TryParse(prefix.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out namedId) || namedId != id)
                            throw new InvalidDataException("The filename must begin with its logical wrestler ID, " + id.ToString(CultureInfo.InvariantCulture) + "_, before the existing installer can use it.");
                    }
                }
                info.Refresh();
                if (!info.Exists || info.Length != entry.Bytes || info.LastWriteTimeUtc != entry.LastWriteTimeUtc) throw new InvalidDataException("The completed file changed during inspection.");
                entry.HasRosterPictureMap = File.Exists(System.IO.Path.Combine(entry.OutputDirectory, "_roster_pictures.txt"));
                entry.HasSelectionLoopPolicy = File.Exists(path + ".selection-loop.json");
            }
            catch (Exception error) when (Expected(error)) { entry.Error = error.Message; }
            return entry;
        }

        private static void RecoverManifest(ScanState state, string path)
        {
            try
            {
                state.Cancellation.ThrowIfCancellationRequested();
                if (!SafePath(path) || new FileInfo(path).Length > MaximumManifestBytes) { Warn(state, "A batch manifest is linked or exceeds the read limit: " + path); return; }
                long manifestLength = new FileInfo(path).Length;
                if (manifestLength > state.MetadataRemaining) { Warn(state, "The saved-render scan reached its manifest/metadata byte limit."); return; }
                state.MetadataRemaining -= manifestLength;
                var document = Json(Utf8.GetString(ReadBounded(path, MaximumManifestBytes)));
                int version;
                if (document == null || !Number(document, "version", out version) || version != 2) return;
                string parent = System.IO.Path.GetDirectoryName(path), declared = Full(Text(document, "workspaceDirectory"));
                if (declared == null || !Paths.Equals(parent, declared)) { Warn(state, "A batch manifest's workspace does not match its own directory: " + path); return; }
                state.ManifestRoots.Add(parent);
                object jobs;
                if (!document.TryGetValue("jobs", out jobs) || !(jobs is IList)) return;
                if (((IList)jobs).Count > MaximumEntries) { Warn(state, "A batch manifest has too many jobs: " + path); return; }
                foreach (object row in (IList)jobs)
                {
                    state.Cancellation.ThrowIfCancellationRequested();
                    var job = row as Dictionary<string, object>;
                    int id;
                    if (job == null || !Number(job, "id", out id) || id < 1 || id > 65535) continue;
                    // Closing/canceling a batch can leave its last saved status
                    // stale. Only the movie's completed bytes establish readiness.
                    string workspace = Full(Text(job, "workspaceDirectory")), original = Full(Text(job, "gmr")), published = Full(Text(job, "publishedGmr"));
                    if (workspace == null || original == null) continue;
                    if (!Within(parent, workspace, false) || !Within(workspace, original, false)
                        || (state.Game != null && Within(state.Game, original, true))
                        || !SafePath(workspace) || !SafePath(original))
                    { Warn(state, "A completed job points outside its proven batch workspace: " + path); continue; }
                    CompletedRenderEntry source = null, copy = null;
                    if (File.Exists(original)) source = AddSource(state, original);
                    if (published != null && File.Exists(published))
                    {
                        if (AllowedPublished(state, published) && SafePath(published)) copy = AddSource(state, published);
                        else Warn(state, "A published batch file is outside the selected source locations. Add its folder to recover it: " + published);
                    }
                    if (source != null && source.IsValid && source.WrestlerId != id)
                        Warn(state, "A saved manifest ID does not match its movie metadata; the movie's own logical ID is retained: " + source.Path);
                    if (source != null && source.IsValid && source.WrestlerId == id)
                    {
                        source.SourceDescription = "Recovered batch"; source.AttireName = Text(job, "attireName");
                        if (copy != null && copy.IsValid && copy.WrestlerId == id && Unchanged(source) && Unchanged(copy))
                        {
                            Comparison same = Compare(state, source.Path, copy.Path);
                            if (same == Comparison.Equal)
                            {
                                source.PublishedPath = copy.Path;
                                if (!Paths.Equals(source.Path, copy.Path)) { state.Files.Remove(copy.Path); state.SuppressedCopies.Add(copy.Path); }
                            }
                            else copy.SourceDescription = same == Comparison.Unknown
                                ? "Referenced published copy (exact match not verified)" : "Referenced published copy (different movie bytes)";
                        }
                    }
                    else if (copy != null && copy.IsValid && copy.WrestlerId == id)
                    {
                        copy.SourceDescription = "Recovered published batch (original workspace unavailable)";
                        copy.AttireName = Text(job, "attireName");
                        if (!copy.HasRosterPictureMap) Warn(state, "The original roster picture map is unavailable for " + System.IO.Path.GetFileName(copy.Path) + "; existing embedded picture links are retained, but shared-picture ownership cannot be recovered from this manifest.");
                    }
                }
            }
            catch (Exception error) when (Expected(error)) { Warn(state, "A saved batch manifest could not be read: " + path + " (" + error.Message + ")"); }
        }

        private static bool AllowedPublished(ScanState state, string path)
        {
            return (state.Game == null || !Within(state.Game, path, true))
                && (state.ExplicitFiles.Contains(path) || state.SourceRoots.Any(root => Within(root, path, false)));
        }

        private static bool GeneratedBatchCache(ScanState state, string path)
        {
            return state.ManifestRoots.Any(root => Within(root, path, false))
                && new[] { "work", "cache", "frames", "helper_models", "tex_png", "textures", "previews" }
                    .Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        }

        private static void NoteCacheSkip(ScanState state)
        {
            if (!state.CacheSkipNoted && state.Report.Warnings.Count < 50)
                state.Report.Warnings.Add("Generated work/cache/frame folders inside recovered batches were skipped. Completed files referenced by their manifest are read directly; use Add files for any nonstandard saved location.");
            state.CacheSkipNoted = true;
        }

        private static List<CompletedRenderEntry> ReadInstalled(ScanState state)
        {
            var installed = new List<CompletedRenderEntry>();
            if (state.Game == null) return installed;
            string directory = System.IO.Path.Combine(state.Game, "Plugins", "GorillaRenders");
            try
            {
                if (!Directory.Exists(directory)) return installed;
                if (!SafePath(directory)) { state.InstalledInventoryComplete = false; Warn(state, "The installed library is linked and was not inspected."); return installed; }
                int count = 0;
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    state.Cancellation.ThrowIfCancellationRequested();
                    if (++count > MaximumEntries) { state.InstalledInventoryComplete = false; Warn(state, "The installed library reached its scan limit."); break; }
                    if (!string.Equals(System.IO.Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = Read(state, path, false);
                    if (entry.WrestlerId == 0)
                    {
                        Match prefix = Regex.Match(System.IO.Path.GetFileName(path), @"^(\d+)_", RegexOptions.CultureInvariant);
                        int id;
                        if (prefix.Success && int.TryParse(prefix.Groups[1].Value, out id) && id >= 1 && id <= 65535) entry.NativeIds.Add(id);
                    }
                    // A filename can suggest a primary slot but cannot recover
                    // unreadable metadata's additional alias claims.
                    if (entry.Error != null) state.InstalledInventoryComplete = false;
                    installed.Add(entry);
                }
            }
            catch (Exception error) when (Expected(error)) { state.InstalledInventoryComplete = false; Warn(state, "The installed render library could not be read: " + error.Message); }
            return installed;
        }

        private static void CompareInstallation(ScanState state, CompletedRenderEntry source, List<CompletedRenderEntry> installed)
        {
            if (!Unchanged(source)) { source.Error = "The saved movie changed after inspection. Refresh before installing it."; return; }
            var matches = installed.Where(item => item.NativeIds.Contains(source.WrestlerId)
                || string.Equals(System.IO.Path.GetFileName(item.Path), System.IO.Path.GetFileName(source.Path), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                source.InstalledState = state.InstalledInventoryComplete ? CompletedRenderInstallState.NotInstalled : CompletedRenderInstallState.NeedsComparison;
                source.InstallationDetails = state.InstalledInventoryComplete ? "No installed root GMR claims this logical wrestler ID." : "The installed inventory is incomplete; installed status is unknown.";
                return;
            }
            source.InstalledPath = matches[0].Path;
            if (matches.Count > 1)
            { source.InstalledState = CompletedRenderInstallState.NeedsComparison; source.InstallationDetails = "Multiple installed GMRs claim this wrestler ID; duplicate-ID conflicts need attention before playback can be established."; return; }
            Comparison result = Compare(state, source.Path, matches[0].Path);
            source.InstalledState = result == Comparison.Equal ? CompletedRenderInstallState.InstalledIdentical
                : result == Comparison.Different ? CompletedRenderInstallState.InstalledDifferent : CompletedRenderInstallState.NeedsComparison;
            source.InstallationDetails = result == Comparison.Equal ? "Exact movie bytes are installed; this does not verify native acceptance or playback."
                : result == Comparison.Different ? "The installed movie differs from this saved result." : "Exact installed-file comparison could not finish within the bounded check.";
        }

        private enum Comparison { Equal, Different, Unknown }
        private static Comparison Compare(ScanState state, string left, string right)
        {
            try
            {
                state.Cancellation.ThrowIfCancellationRequested();
                if (!SafePath(left) || !SafePath(right)) return Comparison.Unknown;
                var leftInfo = new FileInfo(left); var rightInfo = new FileInfo(right);
                DateTime leftTime = leftInfo.LastWriteTimeUtc, rightTime = rightInfo.LastWriteTimeUtc;
                using (var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (a.Length != b.Length) return Comparison.Different;
                    if (Paths.Equals(left, right)) return Comparison.Equal;
                    if (a.Length * 2 > state.ComparisonRemaining) { Warn(state, "The exact movie comparison byte limit was reached; remaining matches are unverified."); return Comparison.Unknown; }
                    long length = a.Length;
                    byte[] aa = new byte[81920], bb = new byte[81920];
                    long compared = 0;
                    while (compared < length)
                    {
                        state.Cancellation.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(aa.Length, length - compared);
                        int ac = ReadChunk(a, aa, count), bc = ReadChunk(b, bb, count);
                        state.ComparisonRemaining -= ac + bc;
                        if (ac != count || bc != count) return Comparison.Unknown;
                        for (int n = 0; n < count; n++) if (aa[n] != bb[n]) return Comparison.Different;
                        compared += count;
                    }
                    leftInfo.Refresh(); rightInfo.Refresh();
                    if (a.Length != length || b.Length != length || !leftInfo.Exists || !rightInfo.Exists
                        || leftInfo.Length != length || rightInfo.Length != length || leftInfo.LastWriteTimeUtc != leftTime || rightInfo.LastWriteTimeUtc != rightTime) return Comparison.Unknown;
                    return Comparison.Equal;
                }
            }
            catch (Exception error) when (Expected(error)) { Warn(state, "An installed/published movie could not be compared: " + error.Message); return Comparison.Unknown; }
        }

        private static int ReadChunk(Stream stream, byte[] buffer, int count)
        { int read = 0, next; while (read < count && (next = stream.Read(buffer, read, count - read)) > 0) read += next; return read; }
        private static bool Unchanged(CompletedRenderEntry entry)
        {
            try { var info = new FileInfo(entry.Path); return info.Exists && info.Length == entry.Bytes && info.LastWriteTimeUtc == entry.LastWriteTimeUtc; }
            catch (Exception error) when (Expected(error)) { return false; }
        }
        private static byte[] ReadBounded(string path, int maximum)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maximum) throw new InvalidDataException("The manifest exceeds its read limit.");
                byte[] bytes = new byte[(int)stream.Length];
                if (ReadChunk(stream, bytes, bytes.Length) != bytes.Length || stream.ReadByte() != -1) throw new InvalidDataException("The manifest changed during reading.");
                return bytes;
            }
        }
        private static bool SafePath(string path)
        {
            string current = System.IO.Path.GetFullPath(path);
            while (current != null)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                current = System.IO.Path.GetDirectoryName(current);
            }
            return true;
        }
        private static bool Within(string root, string path, bool allowRoot)
        {
            if (allowRoot && Paths.Equals(root, path)) return true;
            string prefix = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        private static string Full(string path) { return string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path); }
        private static void Warn(ScanState state, string message)
        { state.Report.Complete = false; if (state.Report.Warnings.Count < 50 && !state.Report.Warnings.Contains(message)) state.Report.Warnings.Add(message); }
        private static string Text(Dictionary<string, object> value, string key)
        { object item; string text = value.TryGetValue(key, out item) ? item as string : null; return text != null && text.Length > 2048 ? text.Substring(0, 2048) : text; }
        private static bool Number(Dictionary<string, object> value, string key, out int result)
        { object item; result = 0; return value.TryGetValue(key, out item) && Integer(item, out result); }
        private static bool Integer(object value, out int result)
        {
            result = 0;
            if (value == null) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                    try { result = Convert.ToInt32(value, CultureInfo.InvariantCulture); return true; } catch (OverflowException) { return false; }
                default: return false;
            }
        }
        private static Dictionary<string, object> Json(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json)) return ConvertJson(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumManifestBytes, RecursionLimit = 100 }.DeserializeObject(json) as Dictionary<string, object>;
#endif
        }
#if NET8_0_OR_GREATER
        private static object ConvertJson(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: var map = new Dictionary<string, object>(); foreach (var p in value.EnumerateObject()) map.Add(p.Name, ConvertJson(p.Value)); return map;
                case JsonValueKind.Array: return value.EnumerateArray().Select(ConvertJson).ToList();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number:
                    long number; return value.TryGetInt64(out number) && value.GetRawText().IndexOfAny(new[] { '.', 'e', 'E' }) < 0 ? (object)number : value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
        private static bool Expected(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException
                || error is FormatException || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                ;
        }
    }
}
