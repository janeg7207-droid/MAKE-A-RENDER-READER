using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryArchiveMatchupAsset
    {
        public ulong Hash { get; internal set; }
        public string Path { get; internal set; }
        // Null for an explicitly hash-named export whose original path is unknown.
        // Such a file is readable, but cannot safely be installed by a guessed name.
        public string AssetPath { get; internal set; }
        public string ArchivePath { get; internal set; }
        public string SourcePath { get; internal set; }
        public bool IsLoose { get; internal set; }
        public string SourceSha256 { get; internal set; }
        internal string[] LooseSourcePaths, LooseSourceRoots;
    }

    public sealed class DiscoveryArchiveMatchupResult
    {
        public Dictionary<ulong, DiscoveryArchiveMatchupAsset> Assets { get; } = new Dictionary<ulong, DiscoveryArchiveMatchupAsset>();
        public List<ulong> Conflicts { get; } = new List<ulong>();
        public List<string> Warnings { get; } = new List<string>();
        // Proven export copies must not be patched at their wrapper locations,
        // including copies shadowed by an active canonical edit or in conflict.
        public List<string> WrappedSourcePaths { get; } = new List<string>();
        public int ArchiveCount { get; internal set; }
        public string Status { get; internal set; }
    }

    /// <summary>Reads the exact screen projects referenced by Discovery's matchup records.</summary>
    public static class DiscoveryArchiveMatchups
    {
        private const int MaximumScreenBytes = 64 * 1024 * 1024;
        private sealed class IndexedAsset
        {
            internal DiscoveryArchiveLookupIndex Index;
            internal string Path;
        }
        private sealed class LooseAsset
        {
            internal DiscoveryArchiveMatchupAsset Asset;
            internal string Root;
            internal bool Wrapped, ActiveCanonical, AmbiguousSuffix;
        }

        public static Task<DiscoveryArchiveMatchupResult> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<ulong> requiredHashes, string cacheRoot, Action<string> progress, CancellationToken cancellationToken)
        {
            return FindAsync(appRoot, gameRoot, discoveryRoot, requiredHashes, cacheRoot, progress,
                DiscoveryArchiveRenders.AssetRunner, cancellationToken);
        }

        public static Task<DiscoveryArchiveMatchupResult> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<ulong> requiredHashes, string cacheRoot, Action<string> progress)
        {
            return FindAsync(appRoot, gameRoot, discoveryRoot, requiredHashes, cacheRoot, progress, CancellationToken.None);
        }

        public static Task<DiscoveryArchiveMatchupResult> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<ulong> requiredHashes, string cacheRoot, Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken cancellationToken)
        {
            if (requiredHashes == null) throw new ArgumentNullException(nameof(requiredHashes));
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            string cache = DiscoveryArchiveRenders.ValidateAssetCache(cacheRoot, gameRoot, discoveryRoot);
            var required = new HashSet<ulong>(requiredHashes.Where(value => value != 0));
            return Task.Run(async () =>
            {
                await DiscoveryArchiveRenders.AssetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await FindCore(appRoot, gameRoot, discoveryRoot, required, cache, progress, runner, cancellationToken).ConfigureAwait(false); }
                finally { DiscoveryArchiveRenders.AssetGate.Release(); }
            }, cancellationToken);
        }

        private static async Task<DiscoveryArchiveMatchupResult> FindCore(string appRoot, string gameRoot, string discoveryRoot,
            HashSet<ulong> required, string cache, Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken token)
        {
            var result = new DiscoveryArchiveMatchupResult();
            if (required.Count == 0) { result.Status = "No nonzero Discovery matchup references need resolving."; return result; }
            // The user's selected extracted Discovery folder contains their active edits.
            // Do not replace an unambiguous edited screen with an older archived copy.
            var loose = new Dictionary<ulong, List<LooseAsset>>();
            var canonical = new HashSet<ulong>();
            var invalidCanonical = new HashSet<ulong>();
            var seenFiles = new HashSet<string>(System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            ReadLoose(discoveryRoot, required, cache, result, loose, canonical, invalidCanonical, seenFiles, true, token);
            var otherLoose = new HashSet<ulong>(required.Where(hash => !canonical.Contains(hash)));
            if (otherLoose.Count > 0 && !string.IsNullOrWhiteSpace(gameRoot)
                && !string.Equals(gameRoot, discoveryRoot, StringComparison.OrdinalIgnoreCase))
                ReadLoose(gameRoot, otherLoose, cache, result, loose, canonical, invalidCanonical, seenFiles, false, token);
            CacheLoose(loose, invalidCanonical, cache, result, token);
            var missing = new HashSet<ulong>(required.Where(hash => !result.Assets.ContainsKey(hash)
                && !result.Conflicts.Contains(hash) && !invalidCanonical.Contains(hash)));
            if (missing.Count > 0)
            {
                var lookup = await DiscoveryArchiveRenders.ReadAssetIndexesAsync(appRoot, gameRoot, discoveryRoot, cache, progress, runner, token).ConfigureAwait(false);
                result.ArchiveCount = lookup.ArchiveCount;
                result.Warnings.AddRange(lookup.Warnings);
                var found = new Dictionary<ulong, List<IndexedAsset>>();
                foreach (var index in lookup.Indexes)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (string line in File.ReadLines(index.IndexPath))
                        {
                            if (line.Length > 8192 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                            string[] fields = line.Split('|'); ulong hash; string asset;
                            if (fields.Length < 3 || fields[0].Length != 16
                                || !ulong.TryParse(fields[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash)) continue;
                            if (!missing.Contains(hash))
                            {
                                // The shipped Bakery hashes UTF16 chars, whereas the
                                // original renderer hashes UTF8 bytes. Never pretend
                                // that a Unicode index disagreement proves an asset.
                                if (fields[2].Any(c => c > 127) && TryAssetPath(fields[2], out asset) && missing.Contains(HashPath(asset)))
                                    result.Warnings.Add("The archive reader reports a different hash for non-ASCII matchup path " + asset
                                        + "; extract that Scre project into Discovery data to resolve its actual UTF8 reference.");
                                continue;
                            }
                            if (!TryAssetPath(fields[2], out asset) || HashPath(asset) != hash)
                            { result.Warnings.Add("Ignored an unsafe or mismatched matchup path in " + System.IO.Path.GetFileName(index.ArchivePath) + "."); continue; }
                            List<IndexedAsset> matches;
                            if (!found.TryGetValue(hash, out matches)) found.Add(hash, matches = new List<IndexedAsset>());
                            if (!matches.Any(value => string.Equals(value.Index.ArchivePath, index.ArchivePath, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(value.Path, asset, StringComparison.OrdinalIgnoreCase))) matches.Add(new IndexedAsset { Index = index, Path = asset });
                        }
                    }
                    catch (Exception error) when (Expected(error)) { result.Warnings.Add("Could not read a matchup archive index: " + error.Message); }
                }
                foreach (var pair in found)
                {
                    token.ThrowIfCancellationRequested();
                    if (pair.Value.Count != 1)
                    {
                        result.Conflicts.Add(pair.Key);
                        result.Warnings.Add("Matchup reference " + pair.Key.ToString("X16", CultureInfo.InvariantCulture)
                            + " has multiple archive sources; their active game load order is not known.");
                        continue;
                    }
                    var match = pair.Value[0];
                    try
                    {
                        if (!DiscoveryArchiveRenders.AssetSourceUnchanged(match.Index)) throw new IOException("The archive changed after its index was read.");
                        string directory = System.IO.Path.Combine(cache, "matchup-projects", Key(match.Index.Fingerprint + "\n" + match.Index.ToolKey + "\n" + match.Path));
                        Directory.CreateDirectory(directory);
                        string output = System.IO.Path.Combine(directory, pair.Key.ToString("x16", CultureInfo.InvariantCulture) + ".jsfb");
                        if (!CachedScreen(output))
                        {
                            string staging = System.IO.Path.Combine(directory, "extract-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(staging);
                            try
                            {
                                if (progress != null) progress("Reading Discovery matchup project " + match.Path + "…");
                                var execution = await DiscoveryArchiveRenders.ExtractAssetAsync(match.Index, match.Path, staging, runner, token).ConfigureAwait(false);
                                string extracted = System.IO.Path.Combine(staging, match.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                                if (execution == null || execution.ExitCode != 0 || !IsScre(extracted) || DiscoveryArchiveRenders.AssetPathLinked(extracted, staging))
                                    throw new IOException("The referenced asset was not decoded as a complete Scre project. " + ShortOutput(execution));
                                if (!DiscoveryArchiveRenders.AssetSourceUnchanged(match.Index)) throw new IOException("The archive changed during matchup extraction.");
                                AtomicCopy(extracted, output); File.WriteAllText(output + ".sha256", FileHash(output));
                            }
                            finally { RemoveDirectory(staging); }
                        }
                        result.Assets.Add(pair.Key, new DiscoveryArchiveMatchupAsset { Hash = pair.Key, Path = output,
                            AssetPath = match.Path, ArchivePath = match.Index.ArchivePath, SourcePath = match.Index.ArchivePath, IsLoose = false });
                    }
                    catch (Exception error) when (Expected(error)) { result.Warnings.Add("Matchup " + pair.Key.ToString("X16", CultureInfo.InvariantCulture) + ": " + error.Message); }
                }
            }
            // Other archive lookups can take time. Recheck every loose source
            // that established equivalence, rather than publishing stale cache
            // bytes after an export was changed or removed during that work.
            foreach (var pair in result.Assets.Where(pair => pair.Value.IsLoose).ToList())
                if (!LooseSourceUnchanged(pair.Value))
                {
                    result.Assets.Remove(pair.Key);
                    if (!result.Conflicts.Contains(pair.Key)) result.Conflicts.Add(pair.Key);
                    result.Warnings.Add("Matchup " + pair.Key.ToString("X16", CultureInfo.InvariantCulture)
                        + ": A contributing loose matchup source changed while references were being resolved: " + pair.Value.SourcePath);
                }
            result.Status = result.Assets.Count + " of " + required.Count + " referenced Discovery matchup project(s) are readable."
                + (result.Conflicts.Count == 0 ? "" : " " + result.Conflicts.Count + " have ambiguous sources.")
                + (required.Count > result.Assets.Count + result.Conflicts.Count ? " Unresolved references require their extracted projects or readable game/mod archives." : "")
                + (result.Assets.Values.Any(value => value.AssetPath == null) ? " A hash-named export has no proven original installation path." : "");
            return result;
        }

        private static void ReadLoose(string root, HashSet<ulong> required, string cache, DiscoveryArchiveMatchupResult result,
            Dictionary<ulong, List<LooseAsset>> files, HashSet<ulong> canonical, HashSet<ulong> invalidCanonical,
            HashSet<string> seenFiles, bool selectedData, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || required.Count == 0) return;
            string full = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            foreach (string path in Walk(full, cache, result.Warnings, token))
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(System.IO.Path.GetExtension(path), ".jsfb", StringComparison.OrdinalIgnoreCase) || !seenFiles.Add(path)) continue;
                string relative = path.Substring(full.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Replace('\\', '/');
                string normalized;
                // Validate wrapper components too: a valid UI suffix cannot
                // make an unsafe prefix or an alternate stream into a source.
                if (!TryAssetPath(relative, out normalized)) continue;
                ulong directHash = HashPath(normalized);
                bool direct = required.Contains(directHash);
                var claims = new List<KeyValuePair<ulong, string>>();
                if (direct) claims.Add(new KeyValuePair<ulong, string>(directHash, normalized));
                else
                {
                    string[] parts = normalized.Split('/');
                    for (int index = 1; index < parts.Length - 1; index++)
                        if (string.Equals(parts[index], "UI", StringComparison.OrdinalIgnoreCase))
                        {
                            string suffix = string.Join("/", parts.Skip(index));
                            ulong hash = HashPath(suffix);
                            if (required.Contains(hash)) claims.Add(new KeyValuePair<ulong, string>(hash, suffix));
                        }
                    if (claims.Count == 0)
                    {
                        string stem = System.IO.Path.GetFileNameWithoutExtension(path); ulong hash;
                        if (stem.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(2);
                        if (stem.Length != 16 || !ulong.TryParse(stem, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash) || !required.Contains(hash)) continue;
                        claims.Add(new KeyValuePair<ulong, string>(hash, null));
                    }
                }
                bool active = selectedData && direct;
                if (active) canonical.Add(directHash);
                try
                {
                    if (!IsScre(path))
                    {
                        if (active) invalidCanonical.Add(directHash);
                        result.Warnings.Add((active ? "The active canonical Discovery matchup is invalid and was left unchanged; fallback copies were not promoted: "
                            : "Referenced loose matchup is not a valid Scre project: ") + relative);
                        continue;
                    }
                    bool wrapped = !direct && claims.Any(claim => claim.Value != null);
                    if (wrapped && !result.WrappedSourcePaths.Contains(path)) result.WrappedSourcePaths.Add(path);
                    foreach (var claim in claims)
                    {
                        List<LooseAsset> matches;
                        if (!files.TryGetValue(claim.Key, out matches)) files.Add(claim.Key, matches = new List<LooseAsset>());
                        matches.Add(new LooseAsset { Root = full, Asset = new DiscoveryArchiveMatchupAsset { Hash = claim.Key,
                            AssetPath = claim.Value, SourcePath = path, IsLoose = true }, Wrapped = wrapped,
                            ActiveCanonical = active, AmbiguousSuffix = claims.Count > 1 });
                    }
                }
                catch (Exception error) when (Expected(error))
                { if (active) invalidCanonical.Add(directHash); result.Warnings.Add(error.Message); }
            }
        }

        private static void CacheLoose(Dictionary<ulong, List<LooseAsset>> files, HashSet<ulong> invalidCanonical,
            string cache, DiscoveryArchiveMatchupResult result, CancellationToken token)
        {
            foreach (var pair in files)
            {
                token.ThrowIfCancellationRequested();
                if (invalidCanonical.Contains(pair.Key)) continue;
                try
                {
                    // The selected dataset's canonical edit controls the game.
                    // Export wrappers provide recovery sources, never overrides.
                    List<LooseAsset> candidates = pair.Value.Any(value => value.ActiveCanonical)
                        ? pair.Value.Where(value => !value.Wrapped).ToList() : pair.Value;
                    if (candidates.Any(value => value.AmbiguousSuffix))
                    { result.Conflicts.Add(pair.Key); result.Warnings.Add("A loose export has multiple matching UI suffixes for required matchup references; no virtual root was guessed: "
                        + candidates.First(value => value.AmbiguousSuffix).Asset.SourcePath); continue; }
                    var hashes = candidates.Select(value => FileHash(value.Asset.SourcePath)).Distinct(StringComparer.Ordinal).ToList();
                    if (hashes.Count > 1)
                    { result.Conflicts.Add(pair.Key); result.Warnings.Add("Different loose Scre projects claim matchup reference " + pair.Key.ToString("X16", CultureInfo.InvariantCulture) + "."); continue; }
                    var selected = candidates.OrderBy(value => value.ActiveCanonical ? 0 : 1).ThenBy(value => value.Asset.AssetPath == null ? 1 : 0)
                        .ThenBy(value => value.Asset.SourcePath, StringComparer.Ordinal).First().Asset;
                    string output = System.IO.Path.Combine(cache, "loose-matchup-projects", hashes[0], pair.Key.ToString("x16", CultureInfo.InvariantCulture) + ".jsfb");
                    if (!CachedScreen(output))
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
                        AtomicCopy(selected.SourcePath, output);
                        if (FileHash(output) != hashes[0] || !IsScre(output)) throw new IOException("The loose matchup project changed while being cached.");
                        File.WriteAllText(output + ".sha256", hashes[0]);
                    }
                    selected.Path = output; selected.SourceSha256 = hashes[0];
                    selected.LooseSourcePaths = candidates.Select(value => value.Asset.SourcePath).ToArray();
                    selected.LooseSourceRoots = candidates.Select(value => value.Root).ToArray();
                    if (!LooseSourceUnchanged(selected)) throw new IOException("A contributing loose matchup source changed while being cached.");
                    result.Assets.Add(pair.Key, selected);
                }
                catch (Exception error) when (Expected(error)) { result.Warnings.Add("Could not cache referenced matchup: " + error.Message); }
            }
        }

        // This is also checked immediately before setup promotes a wrapped
        // export into its proven canonical game path. Shadowed wrappers are
        // deliberately absent from the snapshot of an active canonical edit.
        public static bool LooseSourceUnchanged(DiscoveryArchiveMatchupAsset asset)
        {
            if (asset == null || !asset.IsLoose || string.IsNullOrEmpty(asset.SourceSha256)
                || asset.LooseSourcePaths == null || asset.LooseSourceRoots == null
                || asset.LooseSourcePaths.Length == 0 || asset.LooseSourcePaths.Length != asset.LooseSourceRoots.Length) return false;
            try
            {
                if (!File.Exists(asset.Path) || FileHash(asset.Path) != asset.SourceSha256 || !IsScre(asset.Path)) return false;
                for (int index = 0; index < asset.LooseSourcePaths.Length; index++)
                    if (DiscoveryArchiveRenders.AssetPathLinked(asset.LooseSourcePaths[index], asset.LooseSourceRoots[index])
                        || !File.Exists(asset.LooseSourcePaths[index]) || FileHash(asset.LooseSourcePaths[index]) != asset.SourceSha256) return false;
                return true;
            }
            catch (Exception error) when (Expected(error)) { return false; }
        }

        private static IEnumerable<string> Walk(string root, string cache, List<string> warnings, CancellationToken token)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string directory = pending.Pop();
                if (directory.Equals(cache, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(cache + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                string name = System.IO.Path.GetFileName(directory);
                if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase) && (name.Equals("backup", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("backups", StringComparison.OrdinalIgnoreCase) || name.Equals("Gorilla Studio Layout Backups", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("GorillaRenderBatchBackups", StringComparison.OrdinalIgnoreCase))) continue;
                string[] files, children;
                try
                {
                    if (DiscoveryArchiveRenders.AssetPathLinked(directory, root)) continue;
                    files = Directory.GetFiles(directory); children = Directory.GetDirectories(directory);
                }
                catch (Exception error) when (Expected(error)) { warnings.Add("Could not inspect matchup projects in " + directory + ": " + error.Message); continue; }
                foreach (string file in files)
                {
                    bool linked;
                    try { linked = (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0; }
                    catch (Exception error) when (Expected(error)) { warnings.Add(error.Message); continue; }
                    if (!linked) yield return file;
                }
                foreach (string child in children) pending.Push(child);
            }
        }

        /// <summary>Normalizes an indexed JSFB path without guessing a folder, arena name or wrestler ID.</summary>
        public static bool TryAssetPath(string input, out string asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(input) || input.Length > 1024) return false;
            string normalized = input.Replace('\\', '/');
            string[] parts = normalized.Split('/');
            if (parts.Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal)
                || part.EndsWith(".", StringComparison.Ordinal) || part.Any(c => char.IsControl(c) || ":\"<>|*?".IndexOf(c) >= 0))
                || !string.Equals(System.IO.Path.GetExtension(normalized), ".jsfb", StringComparison.OrdinalIgnoreCase)) return false;
            asset = normalized; return true;
        }

        public static ulong HashPath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            ulong hash = 14695981039346656037UL;
            // Matches the original gs_paths.fnv1a64 / hashes.fnv1a64 renderer:
            // lowercase normalized path encoded to UTF8, then FNV over bytes.
            foreach (byte value in Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant()))
                hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }

        public static bool IsScre(string path)
        {
            if (!File.Exists(path)) return false;
            var file = new FileInfo(path); if (file.Length < 16 || file.Length > MaximumScreenBytes) return false;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var screen = new FlatBufferFile(bytes, "Scre");
                var roots = screen.Tables(screen.Root, 3).Take(30001).ToList();
                if (roots.Count == 0 || roots.Count > 30000) return false;
                int count = 0;
                foreach (int node in roots) ValidateNode(screen, bytes, node, new HashSet<int>(), 0, ref count);
                return count > 0;
            }
            catch (Exception error) when (Expected(error)) { return false; }
        }

        private static void ValidateNode(FlatBufferFile screen, byte[] bytes, int table, HashSet<int> parents, int depth, ref int count)
        {
            if (depth > 64 || ++count > 30000 || !parents.Add(table)) throw new InvalidDataException("Cyclic or oversized Scre hierarchy.");
            int field = screen.Field(table, 1);
            if (field != 0)
            {
                uint distance = screen.U32(field); long target = (long)field + distance;
                if (distance < 4 || target > int.MaxValue) throw new InvalidDataException("Invalid Scre node name.");
                uint length = screen.U32((int)target);
                if (length > 4096 || target + 4 + length >= bytes.Length || bytes[target + 4 + length] != 0) throw new InvalidDataException("Invalid Scre node name.");
                new UTF8Encoding(false, true).GetString(bytes, checked((int)target + 4), (int)length);
            }
            foreach (int child in screen.Tables(table, 55)) ValidateNode(screen, bytes, child, parents, depth + 1, ref count);
            parents.Remove(table);
        }

        private static bool CachedScreen(string path)
        {
            try { return File.Exists(path) && File.Exists(path + ".sha256") && new FileInfo(path + ".sha256").Length < 128
                && File.ReadAllText(path + ".sha256").Trim() == FileHash(path) && IsScre(path); }
            catch (Exception error) when (Expected(error)) { return false; }
        }
        private static void AtomicCopy(string input, string output)
        {
            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.Copy(input, temporary); if (File.Exists(output)) File.Delete(output); File.Move(temporary, output); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        private static string FileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static string Key(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static string ShortOutput(DiscoveryArchiveProcessResult value) { string output = value?.Output ?? ""; return output.Length > 600 ? output.Substring(output.Length - 600) : output; }
        private static bool Expected(Exception error) { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
            || error is NotSupportedException || error is OverflowException || error is System.Security.SecurityException || error is TimeoutException || error is System.ComponentModel.Win32Exception; }
        private static void RemoveDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
