using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    /// <summary>Recovers exact requested motion records from indexed game/mod archives, into the application cache only.</summary>
    public static class DiscoveryArchiveMotions
    {
        private const long MaximumFileBytes = 256L * 1024 * 1024;
        private const long MaximumRetainedBytes = 256L * 1024 * 1024;
        private const int MaximumCandidates = 4096;
        private const int MaximumRecordBytes = 128 * 1024 * 1024;
        private static readonly Regex Canonical = new Regex(@"\A[0-9]{1,10}a[0-9]{3,6}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Area = new Regex(@"(?:^|/)MotionArea_([0-9]+)_([0-9]+)\.(?:clips|clip)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private sealed class Candidate
        {
            internal DiscoveryArchiveLookupIndex Index;
            internal string Path;
            internal long AdvertisedBytes;
            internal int Priority;
        }
        private sealed class Entry
        {
            internal ulong Id;
            internal int Length;
            internal long Offset;
        }
        private sealed class Accepted
        {
            internal DiscoveryMotionAsset Asset;
            internal string Sha;
            internal DiscoveryArchiveLookupIndex Index;
        }

        public static Task<DiscoveryMotionFallbacks> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<string> requiredClips, string cacheRoot, Action<string> progress, CancellationToken cancellationToken)
        {
            return FindAsync(appRoot, gameRoot, discoveryRoot, requiredClips, cacheRoot, progress,
                DiscoveryArchiveRenders.AssetRunner, cancellationToken);
        }

        public static Task<DiscoveryMotionFallbacks> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<string> requiredClips, string cacheRoot, Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken cancellationToken)
        {
            if (requiredClips == null) throw new ArgumentNullException(nameof(requiredClips));
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            string cache = DiscoveryArchiveRenders.ValidateAssetCache(cacheRoot, gameRoot, discoveryRoot);
            var clips = requiredClips.ToArray();
            foreach (string clip in clips)
            {
                uint number;
                if (clip == null || !Canonical.IsMatch(clip) || !uint.TryParse(clip.Substring(0, clip.IndexOf('a')), out number))
                    throw new ArgumentException("Motion references must use their canonical numeric clip names, for example 17330a000.", nameof(requiredClips));
            }
            clips = clips.Distinct(StringComparer.Ordinal).ToArray();
            return Task.Run(async () =>
            {
                await DiscoveryArchiveRenders.AssetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await FindCore(appRoot, gameRoot, discoveryRoot, clips, cache, progress, runner, cancellationToken).ConfigureAwait(false); }
                finally { DiscoveryArchiveRenders.AssetGate.Release(); }
            }, cancellationToken);
        }

        private static async Task<DiscoveryMotionFallbacks> FindCore(string appRoot, string gameRoot, string discoveryRoot,
            string[] clips, string cache, Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken token)
        {
            var result = new DiscoveryMotionFallbacks();
            if (clips.Length == 0) { result.Status = "No unresolved Discovery motions need archive lookup."; return result; }
            var requested = clips.ToDictionary(clip => clip, DiscoveryMotionAssets.ClipId, StringComparer.OrdinalIgnoreCase);
            var accepted = new Dictionary<string, Accepted>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lookup = await DiscoveryArchiveRenders.ReadAssetIndexesAsync(appRoot, gameRoot, discoveryRoot, cache, progress, runner, token).ConfigureAwait(false);
            result.ArchivesSearched = true;
            result.ArchiveCount = lookup.ArchiveCount;
            result.Warnings.AddRange(lookup.Warnings);
            result.Incomplete = lookup.Warnings.Count > 0 || lookup.Indexes.Count < lookup.ArchiveCount;
            var candidates = ReadCandidates(lookup.Indexes, clips, result, token);
            long retainedBytes = 0;
            var retainedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!DiscoveryArchiveRenders.AssetSourceUnchanged(candidate.Index)) throw new IOException("The archive changed after its index was read.");
                    string identity = "motion-v1\n" + candidate.Index.Fingerprint + "\n" + candidate.Index.ToolKey + "\n" + candidate.Path;
                    string directory = Path.Combine(cache, "archive-motions", Hash(identity));
                    Directory.CreateDirectory(directory);
                    if (DiscoveryArchiveRenders.AssetPathLinked(directory, cache)) throw new IOException("The motion cache contains a linked directory.");
                    string catalogPath = Path.Combine(directory, "records.catalog");
                    List<Entry> catalog = ReadCatalog(catalogPath);
                    bool needExtraction = catalog == null;
                    if (catalog != null)
                    {
                        var catalogById = catalog.ToLookup(entry => entry.Id);
                        foreach (var pair in requested)
                        {
                            if (conflicts.Contains(pair.Key) || !catalogById.Contains(pair.Value)) continue;
                            DiscoveryMotionAsset asset;
                            // Multiple distinct table entries for one ID need their
                            // bytes compared again; a single cached record cannot
                            // establish that all copies were identical.
                            bool distinctEntries = catalogById[pair.Value]
                                .Select(entry => entry.Offset + ":" + entry.Length).Distinct().Skip(1).Any();
                            if (!distinctEntries && ReadCachedRecord(directory, pair.Key, pair.Value, identity, out asset))
                            {
                                Retain(asset.Path, asset.Length, retainedPaths, ref retainedBytes);
                                Accept(pair.Key, asset, FileHash(asset.Path), candidate.Index, accepted, conflicts, result);
                            }
                            else needExtraction = true;
                        }
                    }
                    if (!needExtraction) continue;
                    if (candidate.AdvertisedBytes > MaximumFileBytes)
                    { result.Incomplete = true; result.Warnings.Add("Motion extraction budget skipped " + candidate.Path + " (256 MiB per file). Extract its required .clip into Discovery to use it."); continue; }
                    string staging = Path.Combine(directory, "extract-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    var writtenRecords = new List<string>();
                    bool committed = false;
                    try
                    {
                        if (progress != null) progress("Reading archive motions: " + candidate.Path + "…");
                        string source = Path.Combine(staging, candidate.Path.Replace('/', Path.DirectorySeparatorChar));
                        long limit = MaximumFileBytes;
                        var execution = await ExtractBounded(candidate, staging, source, limit, runner, token).ConfigureAwait(false);
                        if (execution == null || execution.ExitCode != 0 || !File.Exists(source) || DiscoveryArchiveRenders.AssetPathLinked(source, staging))
                            throw new IOException("The archive reader did not produce the indexed motion file. " + ShortOutput(execution));
                        long length = new FileInfo(source).Length;
                        if (length > limit) throw new IOException("The extracted motion exceeds the extraction budget.");
                        catalog = Inspect(source, string.Equals(Path.GetExtension(candidate.Path), ".clip", StringComparison.OrdinalIgnoreCase), token);
                        var catalogById = catalog.ToLookup(entry => entry.Id);
                        var local = new Dictionary<string, Tuple<DiscoveryMotionAsset, string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in requested)
                        {
                            if (conflicts.Contains(pair.Key)) continue;
                            foreach (Entry entry in catalogById[pair.Value]
                                .GroupBy(value => value.Offset + ":" + value.Length).Select(group => group.First()))
                            {
                                token.ThrowIfCancellationRequested();
                                var raw = new DiscoveryMotionAsset { Id = pair.Value, Path = source, Offset = entry.Offset, Length = entry.Length };
                                byte[] bytes = raw.Read(); // Includes the actual record-header ID, not just an index-table claim.
                                string sha = HashBytes(bytes);
                                Tuple<DiscoveryMotionAsset, string> previous;
                                if (local.TryGetValue(pair.Key, out previous) && previous.Item2 != sha)
                                { Conflict(pair.Key, accepted, conflicts, result); local.Remove(pair.Key); break; }
                                string output = Path.Combine(directory, pair.Key + ".clip");
                                Retain(output, bytes.Length, retainedPaths, ref retainedBytes);
                                writtenRecords.Add(output);
                                WriteAtomic(output, bytes);
                                WriteAtomic(output + ".source", Encoding.UTF8.GetBytes(identity + "\n" + sha));
                                local[pair.Key] = Tuple.Create(new DiscoveryMotionAsset { Id = pair.Value, Path = output, Offset = 0, Length = bytes.Length, Loose = true }, sha);
                            }
                        }
                        if (!DiscoveryArchiveRenders.AssetSourceUnchanged(candidate.Index)) throw new IOException("The archive changed during motion extraction.");
                        SaveCatalog(catalogPath, catalog);
                        foreach (var pair in local)
                            if (!conflicts.Contains(pair.Key)) Accept(pair.Key, pair.Value.Item1, pair.Value.Item2, candidate.Index, accepted, conflicts, result);
                        committed = true;
                    }
                    finally
                    {
                        if (!committed) foreach (string output in writtenRecords) { Delete(output); Delete(output + ".source"); }
                        RemoveDirectory(staging);
                    }
                }
                catch (Exception error) when (Expected(error))
                { result.Incomplete = true; result.Warnings.Add("Could not read archive motion " + candidate.Path + ": " + error.Message); }
            }
            // Negative catalogs and byte-identical aliases are evidence too. If
            // any indexed source changed after its scan, it may now contain an
            // active override even when the selected positive source is stable.
            foreach (var index in lookup.Indexes.GroupBy(value => value.ArchivePath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                token.ThrowIfCancellationRequested();
                bool unchanged;
                try { unchanged = DiscoveryArchiveRenders.AssetSourceUnchanged(index); }
                catch (Exception error) when (Expected(error)) { unchanged = false; }
                if (!unchanged)
                { result.Incomplete = true; result.Warnings.Add("The archive changed after its motion scan: " + Path.GetFileName(index.ArchivePath) + ". Refresh Discovery data and retry."); }
            }
            // A skipped or unreadable motion source may contain a second payload
            // for any requested ID. Archive load order is unknown: uncertainty is
            // not permission to install the first apparently matching stock clip.
            if (result.Incomplete)
            {
                foreach (string clip in accepted.Keys)
                    if (!result.Issues.ContainsKey(clip)) result.Issues[clip] = "The motion matched, but another motion source was unreadable, ambiguous or budget-limited. Its active override cannot be ruled out; export the active .clip into Discovery data or resolve the archive warning and retry.";
                accepted.Clear();
            }
            token.ThrowIfCancellationRequested();
            foreach (var pair in accepted) result.Assets[pair.Key] = pair.Value.Asset;
            foreach (string clip in clips)
                if (!result.Assets.ContainsKey(clip) && !result.Issues.ContainsKey(clip))
                    result.Issues[clip] = lookup.ArchiveCount == 0
                        ? "No game/mod CAK archives were available to search. Select the installed game or extract this .clip/.clips into Discovery data."
                        : result.Incomplete
                            ? "Archive lookup was incomplete (unreadable, ambiguous or budget-limited sources). Its motion was not recovered; extract its .clip/.clips into Discovery data or resolve the archive warning and retry."
                            : "This exact motion record was not recovered from the readable configured archives. Other game/mod sources may contain it; extract its .clip/.clips into Discovery data.";
            result.Status = result.Assets.Count + " of " + clips.Length + " requested archive motion(s) recovered from " + result.ArchiveCount + " archive(s)."
                + (result.Incomplete ? " Lookup was incomplete; see archive warnings." : "")
                + (result.Issues.Count > 0 ? " Unresolved clips need their extracted motion or another readable source." : "");
            return result;
        }

        private static List<Candidate> ReadCandidates(IEnumerable<DiscoveryArchiveLookupIndex> indexes, string[] clips,
            DiscoveryMotionFallbacks result, CancellationToken token)
        {
            var groups = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
            var numbers = clips.Select(clip => uint.Parse(clip.Substring(0, clip.IndexOf('a')), CultureInfo.InvariantCulture)).ToArray();
            bool candidateLimit = false;
            foreach (var index in indexes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (string line in File.ReadLines(index.IndexPath))
                    {
                        token.ThrowIfCancellationRequested();
                        if (line.Length > 8192 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                        string[] fields = line.Split('|');
                        if (fields.Length < 3 || !(fields[2].EndsWith(".clip", StringComparison.OrdinalIgnoreCase) || fields[2].EndsWith(".clips", StringComparison.OrdinalIgnoreCase))) continue;
                        string path; ulong hash;
                        if (fields[0].Length != 16 || !ulong.TryParse(fields[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash)
                            || !TryAssetPath(fields[2], out path) || HashPath(path) != hash)
                        { result.Incomplete = true; result.Warnings.Add("Ignored an unsafe or hash-mismatched motion index row in " + Path.GetFileName(index.ArchivePath) + ". Extract non-ASCII custom paths directly if their archive hash differs."); continue; }
                        long size = 0;
                        foreach (string field in fields.Skip(3))
                            if (field.StartsWith("ZSize:0x", StringComparison.OrdinalIgnoreCase)) long.TryParse(field.Substring(8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size);
                        int priority = 2;
                        string stem = Path.GetFileNameWithoutExtension(path);
                        if (clips.Any(clip => string.Equals(stem, clip, StringComparison.OrdinalIgnoreCase))) priority = 0;
                        else
                        {
                            Match range = Area.Match(path); uint start, end;
                            if (range.Success && uint.TryParse(range.Groups[1].Value, out start) && uint.TryParse(range.Groups[2].Value, out end) && start <= end)
                                priority = numbers.Any(value => value >= start && value <= end) ? 1 : 3;
                        }
                        List<Candidate> matches;
                        if (!groups.TryGetValue(path, out matches))
                        {
                            if (groups.Count >= MaximumCandidates) { candidateLimit = true; continue; }
                            groups[path] = matches = new List<Candidate>();
                        }
                        if (!matches.Any(value => string.Equals(value.Index.ArchivePath, index.ArchivePath, StringComparison.OrdinalIgnoreCase)))
                            matches.Add(new Candidate { Index = index, Path = path, Priority = priority, AdvertisedBytes = size });
                    }
                }
                catch (Exception error) when (Expected(error))
                { result.Incomplete = true; result.Warnings.Add("Could not inspect an archive motion index: " + error.Message); }
            }
            var candidates = new List<Candidate>();
            if (candidateLimit)
            { result.Incomplete = true; result.Warnings.Add("The archive motion scan exceeded 4,096 candidate files. Extract the active requested .clip into Discovery data or narrow the configured archive roots."); }
            foreach (var pair in groups)
                if (pair.Value.Count == 1) candidates.Add(pair.Value[0]);
                else
                { result.Incomplete = true; result.Warnings.Add("Motion path " + pair.Key + " exists in multiple CAKs; their active load order is unknown, so no archive was chosen for this path."); }
            return candidates.OrderBy(value => value.Priority).ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Index.ArchivePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static async Task<DiscoveryArchiveProcessResult> ExtractBounded(Candidate candidate, string staging, string source,
            long limit, DiscoveryArchiveRunner runner, CancellationToken token)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<DiscoveryArchiveProcessResult> operation = DiscoveryArchiveRenders.ExtractAssetAsync(candidate.Index, candidate.Path, staging, runner, linked.Token);
                try
                {
                    while (!operation.IsCompleted)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(source) && new FileInfo(source).Length > limit)
                            throw new IOException("Motion extraction exceeded the 256 MiB per-file budget; extract the required .clip separately.");
                        await Task.WhenAny(operation, Task.Delay(100, token)).ConfigureAwait(false);
                    }
                    return await operation.ConfigureAwait(false);
                }
                finally
                {
                    if (!operation.IsCompleted)
                    {
                        linked.Cancel();
                        try { await operation.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    }
                }
            }
        }

        private static List<Entry> Inspect(string path, bool loose, CancellationToken token)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(file))
            {
                if (file.Length > MaximumFileBytes) throw new InvalidDataException("The motion file exceeds the 256 MiB supported limit.");
                if (loose)
                {
                    if (file.Length < 160 || file.Length > MaximumRecordBytes) throw new InvalidDataException("The loose motion has an invalid length.");
                    return new List<Entry> { new Entry { Id = reader.ReadUInt64(), Length = (int)file.Length, Offset = 0 } };
                }
                if (file.Length < 24 || reader.ReadUInt64() != 0 || reader.ReadUInt64() != 0) throw new InvalidDataException("The motion container header is invalid.");
                uint count = reader.ReadUInt32(), repeated = reader.ReadUInt32();
                if (count == 0 && repeated == 0 && file.Length == 32 && reader.ReadUInt64() == 0) return new List<Entry>();
                if (count == 0 || count > 100000 || count != repeated || 24L + count * 16L > file.Length) throw new InvalidDataException("The motion record table is invalid.");
                var records = new List<Entry>();
                for (uint i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    ulong id = reader.ReadUInt64(); uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
                    if (length < 160 || length > MaximumRecordBytes || offset < 24L + count * 16L || offset > file.Length - length)
                        throw new InvalidDataException("A motion record is outside its container.");
                    records.Add(new Entry { Id = id, Length = (int)length, Offset = offset });
                }
                // Distinct overlapping payloads could multiply the bounded file into
                // unbounded work or claim another record's bytes. Exact aliases are safe.
                long end = 0, previousOffset = -1; int previousLength = 0;
                foreach (Entry entry in records.OrderBy(value => value.Offset).ThenBy(value => value.Length))
                {
                    if (entry.Offset < end && !(entry.Offset == previousOffset && entry.Length == previousLength))
                        throw new InvalidDataException("The motion table contains overlapping records.");
                    end = entry.Offset + entry.Length; previousOffset = entry.Offset; previousLength = entry.Length;
                }
                return records;
            }
        }

        private static void Accept(string clip, DiscoveryMotionAsset asset, string sha, DiscoveryArchiveLookupIndex index,
            Dictionary<string, Accepted> accepted, HashSet<string> conflicts, DiscoveryMotionFallbacks result)
        {
            if (conflicts.Contains(clip)) return;
            Accepted previous;
            if (accepted.TryGetValue(clip, out previous) && previous.Sha != sha) { Conflict(clip, accepted, conflicts, result); return; }
            if (previous == null) accepted[clip] = new Accepted { Asset = asset, Sha = sha, Index = index };
        }

        private static void Retain(string path, int length, HashSet<string> retained, ref long bytes)
        {
            if (retained.Contains(path)) return;
            if (bytes + length > MaximumRetainedBytes) throw new IOException("The requested-record cache budget exceeds 256 MiB. Import fewer motions at a time or extract their active records into Discovery data.");
            retained.Add(path); bytes += length;
        }

        private static void Conflict(string clip, Dictionary<string, Accepted> accepted, HashSet<string> conflicts, DiscoveryMotionFallbacks result)
        {
            accepted.Remove(clip); conflicts.Add(clip);
            result.Issues[clip] = "Different motion bytes claim this exact clip ID in the searched sources; the active mod load order is unknown. Export the active clip from Discovery instead of choosing an archive by date.";
        }

        private static bool ReadCachedRecord(string directory, string clip, ulong id, string identity, out DiscoveryMotionAsset asset)
        {
            asset = null; string path = Path.Combine(directory, clip + ".clip");
            try
            {
                if (!File.Exists(path) || DiscoveryArchiveRenders.AssetPathLinked(path, directory) || !File.Exists(path + ".source")
                    || new FileInfo(path + ".source").Length > 16384) return false;
                long length = new FileInfo(path).Length;
                if (length < 160 || length > MaximumRecordBytes || File.ReadAllText(path + ".source") != identity + "\n" + FileHash(path)) return false;
                using (var reader = new BinaryReader(File.OpenRead(path))) if (reader.ReadUInt64() != id) return false;
                asset = new DiscoveryMotionAsset { Path = path, Id = id, Offset = 0, Length = (int)length, Loose = true };
                return true;
            }
            catch (Exception error) when (Expected(error)) { return false; }
        }

        private static List<Entry> ReadCatalog(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 2000008 || DiscoveryArchiveRenders.AssetPathLinked(path, Path.GetDirectoryName(path))
                    || !File.Exists(path + ".sha256") || new FileInfo(path + ".sha256").Length > 128 || File.ReadAllText(path + ".sha256") != FileHash(path)) return null;
                using (var file = File.OpenRead(path)) using (var reader = new BinaryReader(file))
                {
                    if (reader.ReadUInt32() != 0x31434d47) return null;
                    int count = reader.ReadInt32();
                    if (count < 0 || count > 100000 || file.Length != 8L + count * 20L) return null;
                    var entries = new List<Entry>();
                    for (int i = 0; i < count; i++)
                    {
                        var entry = new Entry { Id = reader.ReadUInt64(), Length = reader.ReadInt32(), Offset = reader.ReadInt64() };
                        if (entry.Length < 160 || entry.Length > MaximumRecordBytes || entry.Offset < 0 || entry.Offset > MaximumFileBytes - entry.Length) return null;
                        entries.Add(entry);
                    }
                    return entries;
                }
            }
            catch (Exception error) when (Expected(error)) { return null; }
        }

        private static void SaveCatalog(string path, List<Entry> entries)
        {
            using (var bytes = new MemoryStream()) using (var writer = new BinaryWriter(bytes))
            {
                writer.Write(0x31434d47u); writer.Write(entries.Count);
                foreach (Entry entry in entries) { writer.Write(entry.Id); writer.Write(entry.Length); writer.Write(entry.Offset); }
                writer.Flush(); WriteAtomic(path, bytes.ToArray());
            }
            WriteAtomic(path + ".sha256", Encoding.ASCII.GetBytes(FileHash(path)));
        }

        public static bool TryAssetPath(string input, out string asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(input) || input.Length > 1024) return false;
            string normalized = input.Replace('\\', '/');
            string extension = Path.GetExtension(normalized);
            if (!string.Equals(extension, ".clip", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".clips", StringComparison.OrdinalIgnoreCase)) return false;
            if (normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal)
                || part.EndsWith(".", StringComparison.Ordinal) || part.Any(c => char.IsControl(c) || ":\"<>|*?".IndexOf(c) >= 0))) return false;
            asset = normalized; return true;
        }

        public static ulong HashPath(string path)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte value in Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant())) hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }

        private static string FileHash(string path) { using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(file)); }
        private static string Hash(string value) { return HashBytes(Encoding.UTF8.GetBytes(value)); }
        private static string HashBytes(byte[] value) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(value)); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static string ShortOutput(DiscoveryArchiveProcessResult result) { string value = result == null ? "" : result.Output ?? ""; return value.Length > 500 ? value.Substring(0, 500) : value; }
        private static bool Expected(Exception error) { return error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is TimeoutException || error is System.ComponentModel.Win32Exception; }
        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); if (File.Exists(path)) File.Delete(path); File.Move(temporary, path); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        }
        private static void RemoveDirectory(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
