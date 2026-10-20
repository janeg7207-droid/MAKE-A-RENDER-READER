using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    internal sealed class DiscoveryTauntSourcesChangedException : InvalidOperationException
    {
        internal DiscoveryTauntSourcesChangedException()
            : base("Discovery animation files changed during lookup. Read the current sources again before building the library.") { }
    }

    public enum DiscoveryTauntUnavailableKind { MissingAnimation, UnsupportedFormat, ScanIncomplete, ReadFailure, CacheFailure }

    public sealed class DiscoveryUnavailableTaunt
    {
        public int Id { get; internal set; }
        public int Variant { get; internal set; }
        public string Clip { get; internal set; }
        public string Name { get; internal set; }
        public List<string> Names { get; internal set; }
        public DiscoveryTauntUnavailableKind Kind { get; internal set; }
        public string Reason { get; internal set; }
        // UI-only identity: never put this key in the engine taunts.json manifest.
        public string Key { get { return "discovery_unavailable_" + Clip; } }
    }

    public sealed class DiscoveryTauntImport
    {
        public string Root { get; internal set; }
        public string Library { get; internal set; }
        public int Definitions { get; internal set; }
        public int Imported { get; internal set; }
        public int Available { get; internal set; }
        public int Missing { get; internal set; }
        public int Unsupported { get; internal set; }
        public int ReadFailures { get; internal set; }
        public int CacheFailures { get; internal set; }
        public int ArchiveImported { get; internal set; }
        public int VerifiedImported { get; internal set; }
        public string VerifiedPackHash { get; internal set; }
        public int ArchiveCount { get; internal set; }
        public bool ArchivesSearched { get; internal set; }
        public bool ArchiveLookupPending { get; internal set; }
        public long SourceRevision { get; internal set; }
        public string ArchiveStatus { get; internal set; }
        public bool ScanIncomplete { get; internal set; }
        public bool AnimationRootFound { get; internal set; }
        public bool HasLinkedSources { get; internal set; }
        public IReadOnlyList<string> SearchRoots { get; internal set; } = new string[0];
        public List<string> Warnings { get; } = new List<string>();
        public List<DiscoveryUnavailableTaunt> Unavailable { get; } = new List<DiscoveryUnavailableTaunt>();
        internal List<Dictionary<string, object>> Taunts { get; set; }
    }

    /// <summary>Builds a writable engine data overlay without changing Discovery or the installed application.</summary>
    public static class DiscoveryTauntLibrary
    {
        /// <summary>A read-only lookup shared by archive preflight and the matching library build.</summary>
        public sealed class Preparation
        {
            internal string ApplicationRoot, SelectedRoot, ManifestPath, ManifestText, ManifestHash, Table;
            internal readonly string OverlayRevision = Guid.NewGuid().ToString("N");
            internal SourceStamp ManifestStamp, TableStamp;
            internal List<DiscoveryTaunt> Definitions;
            internal DiscoveryMotionAssets Assets;
            internal VerifiedTauntRecordPack Records;
            internal Func<long> ExternalRevision;
            internal long CapturedRevision;
            public string DataRoot { get; internal set; }
            public IReadOnlyList<string> Missing { get; internal set; }

            public bool IsCurrent(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ExternalRevision != null && ExternalRevision() != CapturedRevision) return false;
                if (!ManifestStamp.IsCurrent()) return false;
                string currentTable = DiscoveryTaunts.FindTable(SelectedRoot);
                if (!string.Equals(Table, currentTable, StringComparison.OrdinalIgnoreCase)) return false;
                bool current = Table == null || (TableStamp.IsCurrent() && Assets.IsCurrent(cancellationToken));
                return current && (ExternalRevision == null || ExternalRevision() == CapturedRevision);
            }
        }

        internal static Preparation ForkPreparation(Preparation prepared)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            // Source snapshots are read-only. A new preparation owns a distinct
            // publication root while keeping the exact same lookup lease.
            return new Preparation {
                ApplicationRoot = prepared.ApplicationRoot, SelectedRoot = prepared.SelectedRoot,
                ManifestPath = prepared.ManifestPath, ManifestText = prepared.ManifestText, ManifestHash = prepared.ManifestHash,
                ManifestStamp = prepared.ManifestStamp, Table = prepared.Table, TableStamp = prepared.TableStamp,
                Definitions = prepared.Definitions, Assets = prepared.Assets, Records = prepared.Records,
                ExternalRevision = prepared.ExternalRevision, CapturedRevision = prepared.CapturedRevision,
                DataRoot = prepared.DataRoot, Missing = prepared.Missing
            };
        }

        internal static DiscoveryTauntImport WithArchiveDiagnostics(DiscoveryTauntImport available,
            DiscoveryMotionFallbacks fallbacks, bool baseScanIncomplete)
        {
            if (available == null) throw new ArgumentNullException(nameof(available));
            if (fallbacks == null) throw new ArgumentNullException(nameof(fallbacks));
            var result = new DiscoveryTauntImport {
                Root = available.Root, Library = available.Library, Taunts = available.Taunts,
                Definitions = available.Definitions, Imported = available.Imported, Available = available.Available,
                Missing = available.Missing, Unsupported = available.Unsupported, ReadFailures = available.ReadFailures,
                CacheFailures = available.CacheFailures, ArchiveImported = available.ArchiveImported,
                VerifiedImported = available.VerifiedImported, VerifiedPackHash = available.VerifiedPackHash,
                ArchiveCount = fallbacks.ArchiveCount, ArchivesSearched = fallbacks.ArchivesSearched,
                ArchiveStatus = fallbacks.Status, ArchiveLookupPending = false, SourceRevision = available.SourceRevision,
                ScanIncomplete = baseScanIncomplete || fallbacks.Incomplete,
                AnimationRootFound = available.AnimationRootFound, HasLinkedSources = available.HasLinkedSources,
                SearchRoots = available.SearchRoots == null ? new string[0] : available.SearchRoots.ToArray()
            };
            const string pendingWarning = "Additional animation archive lookup is pending; missing clips may become available when it finishes.";
            foreach (string warning in available.Warnings.Concat(fallbacks.Warnings))
                if (!string.Equals(warning, pendingWarning, StringComparison.Ordinal)
                    && !result.Warnings.Contains(warning)) result.Warnings.Add(warning);
            foreach (DiscoveryUnavailableTaunt item in available.Unavailable)
            {
                var copy = new DiscoveryUnavailableTaunt { Id = item.Id, Variant = item.Variant, Clip = item.Clip,
                    Name = item.Name, Names = item.Names == null ? new List<string>() : new List<string>(item.Names),
                    Kind = item.Kind, Reason = item.Reason };
                if (item.Kind == DiscoveryTauntUnavailableKind.MissingAnimation || item.Kind == DiscoveryTauntUnavailableKind.ScanIncomplete)
                {
                    copy.Kind = result.ScanIncomplete ? DiscoveryTauntUnavailableKind.ScanIncomplete : DiscoveryTauntUnavailableKind.MissingAnimation;
                    const string notSearched = " Installed-game CAK archives were not searched.";
                    string reason = copy.Reason ?? "Animation clip " + copy.Clip + " was not located.";
                    if (reason.EndsWith(notSearched, StringComparison.Ordinal)) reason = reason.Substring(0, reason.Length - notSearched.Length);
                    string issue;
                    if (fallbacks.Issues.TryGetValue(copy.Clip, out issue) && !string.IsNullOrWhiteSpace(issue))
                    { if (!reason.Contains(issue)) reason += " " + issue; }
                    else if (fallbacks.ArchivesSearched)
                    {
                        const string noMatch = " No unambiguous matching motion was recovered from the available archive search.";
                        if (!reason.Contains(noMatch)) reason += noMatch;
                        if (fallbacks.Incomplete) reason += " The archive search did not finish; see its warnings.";
                    }
                    else reason += notSearched;
                    copy.Reason = reason;
                }
                result.Unavailable.Add(copy);
            }
            return result;
        }

        internal sealed class SourceStamp
        {
            private string path;
            private long length, modified;
            internal string ContentHash;
            internal static SourceStamp Read(string path)
            {
                var file = new FileInfo(path);
                return new SourceStamp { path = file.FullName, length = file.Length, modified = file.LastWriteTimeUtc.Ticks,
                    ContentHash = Hash(File.ReadAllBytes(path)) };
            }
            internal bool IsCurrent()
            {
                try
                {
                    var file = new FileInfo(path);
                    return file.Exists && file.Length == length && file.LastWriteTimeUtc.Ticks == modified
                        && ContentHash == Hash(File.ReadAllBytes(path));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return false; }
            }
        }

        public static Preparation Prepare(string applicationRoot, string discoveryRoot, CancellationToken cancellationToken,
            Func<long> externalRevision = null, VerifiedTauntRecordPack recordPack = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The caller's watcher also covers game/mod CAK membership outside
            // this extracted .clip/.clips tree. Capture its lease before scanning.
            long revision = externalRevision == null ? 0 : externalRevision();
            string manifestPath = Path.Combine(applicationRoot, "data", "library", "taunts.json");
            var result = new Preparation { ApplicationRoot = Path.GetFullPath(applicationRoot), SelectedRoot = discoveryRoot,
                ManifestPath = manifestPath, ManifestStamp = SourceStamp.Read(manifestPath),
                ExternalRevision = externalRevision, CapturedRevision = revision,
                Records = recordPack ?? VerifiedTauntRecordPack.Bundled };
            result.ManifestHash = result.ManifestStamp.ContentHash;
            result.ManifestText = File.ReadAllText(manifestPath);
            var manifest = JsonObject(result.ManifestText);
            object raw;
            if (!manifest.TryGetValue("taunts", out raw) || !(raw is IEnumerable))
                throw new InvalidDataException("The bundled taunt library has no animation list.");
            var bundled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in (IEnumerable)raw)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = item as Dictionary<string, object>;
                if (entry == null) throw new InvalidDataException("A bundled taunt entry is invalid.");
                object clip;
                if (entry.TryGetValue("clip", out clip) && clip is string) bundled.Add((string)clip);
            }
            result.Table = DiscoveryTaunts.FindTable(discoveryRoot);
            result.Definitions = new List<DiscoveryTaunt>();
            result.Missing = new string[0];
            if (result.Table == null) return result;
            result.TableStamp = SourceStamp.Read(result.Table);
            result.DataRoot = SourceRoot(discoveryRoot, result.Table);
            result.Definitions = DiscoveryTaunts.Read(result.Table);
            cancellationToken.ThrowIfCancellationRequested();
            result.Assets = DiscoveryMotionAssets.Read(result.DataRoot, cancellationToken);
            var missing = new List<string>();
            foreach (DiscoveryTaunt definition in result.Definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifiedTauntRecord record;
                if (!bundled.Contains(definition.Clip) && result.Assets.Find(definition.Clip) == null
                    && !result.Records.TryRead(definition.Clip, out record)) missing.Add(definition.Clip);
            }
            result.Missing = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return result;
        }

        /// <summary>Finds lookup misses without changing the current engine overlay or preview files.</summary>
        public static List<string> FindUnresolvedClips(string applicationRoot, string discoveryRoot, out string dataRoot)
        {
            if (DiscoveryTaunts.FindTable(discoveryRoot) == null)
            { dataRoot = null; return new List<string>(); }
            Preparation prepared = Prepare(applicationRoot, discoveryRoot, CancellationToken.None);
            dataRoot = prepared.DataRoot;
            return prepared.Missing.ToList();
        }

        private static string SourceRoot(string selected, string table)
        {
            string source = Path.GetFullPath(selected);
            if (File.Exists(source)) source = Path.GetDirectoryName(source);
            DirectoryInfo parent = Directory.GetParent(table);
            if (parent != null && string.Equals(parent.Name, "MoveData", StringComparison.OrdinalIgnoreCase))
                source = parent.Parent.FullName;
            return source;
        }

        public static DiscoveryTauntImport Build(string applicationRoot, string storageRoot, string discoveryRoot)
        {
            return Build(applicationRoot, storageRoot, discoveryRoot, null);
        }

        public static DiscoveryTauntImport Build(string applicationRoot, string storageRoot, string discoveryRoot,
            DiscoveryMotionFallbacks fallbacks)
        {
            // Retain the historical root for direct/synchronous callers. The
            // interactive prepared-refresh path uses its own revision below.
            return BuildPreparation(Prepare(applicationRoot, discoveryRoot, CancellationToken.None), storageRoot, fallbacks, null, true, false, CancellationToken.None);
        }

        public static DiscoveryTauntImport Build(string applicationRoot, string storageRoot, string discoveryRoot,
            DiscoveryMotionFallbacks fallbacks, CancellationToken cancellationToken)
        {
            return BuildPreparation(Prepare(applicationRoot, discoveryRoot, cancellationToken), storageRoot,
                fallbacks, null, true, true, cancellationToken);
        }

        public static DiscoveryTauntImport BuildPrepared(Preparation prepared, string storageRoot,
            DiscoveryMotionFallbacks fallbacks = null, Action<string> progress = null)
        {
            return BuildPrepared(prepared, storageRoot, fallbacks, progress, CancellationToken.None);
        }

        public static DiscoveryTauntImport BuildPrepared(Preparation prepared, string storageRoot,
            DiscoveryMotionFallbacks fallbacks, Action<string> progress, CancellationToken cancellationToken)
        {
            return BuildPreparation(prepared, storageRoot, fallbacks, progress, true, true, cancellationToken);
        }

        // The refresh bridge has just validated this snapshot while cancellation
        // was still enabled, and validates it again before applying the result.
        // Avoid an identical second directory/metadata sweep at the same boundary.
        internal static DiscoveryTauntImport BuildCurrentPreparation(Preparation prepared, string storageRoot,
            DiscoveryMotionFallbacks fallbacks, Action<string> progress)
        {
            return BuildCurrentPreparation(prepared, storageRoot, fallbacks, progress, CancellationToken.None);
        }

        internal static DiscoveryTauntImport BuildCurrentPreparation(Preparation prepared, string storageRoot,
            DiscoveryMotionFallbacks fallbacks, Action<string> progress, CancellationToken cancellationToken)
        {
            return BuildPreparation(prepared, storageRoot, fallbacks, progress, false, true, cancellationToken);
        }

        private static DiscoveryTauntImport BuildPreparation(Preparation prepared, string storageRoot,
            DiscoveryMotionFallbacks fallbacks, Action<string> progress, bool validate, bool separateOverlay, CancellationToken cancellationToken)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            cancellationToken.ThrowIfCancellationRequested();
            if (validate && !prepared.IsCurrent(cancellationToken))
                throw new DiscoveryTauntSourcesChangedException();
            string applicationRoot = prepared.ApplicationRoot;
            string baseData = Path.Combine(applicationRoot, "data");
            string baseLibrary = Path.Combine(baseData, "library");
            string baseManifest = Path.Combine(baseLibrary, "taunts.json");
            // Parse a fresh copy: building a manifest adds aliases and cache keys;
            // those changes must never mutate this read-only source snapshot.
            var manifest = JsonObject(prepared.ManifestText);
            object raw;
            if (!manifest.TryGetValue("taunts", out raw) || !(raw is IEnumerable))
                throw new InvalidDataException("The bundled taunt library has no animation list.");
            var entries = new List<Dictionary<string, object>>();
            foreach (object value in (IEnumerable)raw)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = value as Dictionary<string, object>;
                if (entry == null) throw new InvalidDataException("A bundled taunt entry is invalid.");
                entries.Add(entry);
            }
            var result = new DiscoveryTauntImport { Library = baseLibrary, Taunts = entries, Available = entries.Count,
                SourceRevision = prepared.CapturedRevision };
            result.VerifiedPackHash = prepared.Records.Sha256;
            string table = prepared.Table;
            if (table == null) { cancellationToken.ThrowIfCancellationRequested(); return result; }

            string sourceRoot = prepared.DataRoot;
            List<DiscoveryTaunt> definitions = prepared.Definitions;
            result.Definitions = definitions.Count;
            DiscoveryMotionAssets assets = prepared.Assets;
            result.ScanIncomplete = assets.ScanIncomplete;
            result.AnimationRootFound = assets.AnimationRootFound;
            result.HasLinkedSources = assets.HasLinkedSources || (File.GetAttributes(table) & FileAttributes.ReparsePoint) != 0;
            result.SearchRoots = assets.SearchRoots;
            result.Warnings.AddRange(assets.Warnings);
            if (fallbacks != null)
            {
                result.ScanIncomplete |= fallbacks.Incomplete;
                result.ArchivesSearched = fallbacks.ArchivesSearched;
                result.ArchiveCount = fallbacks.ArchiveCount;
                result.ArchiveStatus = fallbacks.Status;
                result.Warnings.AddRange(fallbacks.Warnings);
            }
            string identity = Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(applicationRoot) + "\n" + sourceRoot + "\n" + prepared.ManifestHash));
            // Never mutate the active window's previous engine data while an
            // interactive refresh is still being checked for source changes.
            // A failed/stale attempt remains private and is never applied.
            string revision = cancellationToken.CanBeCanceled ? Guid.NewGuid().ToString("N") : prepared.OverlayRevision;
            string overlay = Path.Combine(storageRoot, "taunt-data", identity.Substring(0, 20)
                + (separateOverlay ? "-" + revision.Substring(0, 12) : ""));
            string data = Path.Combine(overlay, "data"), library = Path.Combine(data, "library"), pack = Path.Combine(data, "gamepack");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(data);
            foreach (string directory in Directory.EnumerateDirectories(applicationRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetFileName(directory), "data", StringComparison.OrdinalIgnoreCase))
                    DirectoryLinks.EnsureJunction(Path.Combine(overlay, Path.GetFileName(directory)), directory);
            }
            LinkChildren(baseData, data, new[] { "library", "gamepack", "caps", "start" }, cancellationToken);
            MaterializeDirectory(Path.Combine(baseData, "caps"), Path.Combine(data, "caps"), false, cancellationToken);
            MaterializeDirectory(Path.Combine(baseData, "start"), Path.Combine(data, "start"), false, cancellationToken);
            Directory.CreateDirectory(library);
            LinkChildren(baseLibrary, library, new[] { "previews_game" }, cancellationToken);
            MaterializeDirectory(Path.Combine(baseLibrary, "previews_game"), Path.Combine(library, "previews_game"), true, cancellationToken);
            Directory.CreateDirectory(pack);
            LinkChildren(Path.Combine(baseData, "gamepack"), pack, new[] { "decoded" }, cancellationToken);
            string decoded = Path.Combine(pack, "decoded");
            Directory.CreateDirectory(decoded);
            foreach (string original in Directory.EnumerateFiles(Path.Combine(baseData, "gamepack", "decoded")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(decoded, Path.GetFileName(original));
                if (!File.Exists(target)) CopyAtomic(original, target, cancellationToken);
            }
            string cache = Path.Combine(storageRoot, "motion-cache-v1");
            Directory.CreateDirectory(cache);

            var byClip = entries.Where(item => item.ContainsKey("clip") && item["clip"] is string)
                .GroupBy(item => (string)item["clip"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int completed = 0;
            foreach (DiscoveryTaunt definition in definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (completed++ % 32 == 0)
                    progress?.Invoke("Preparing taunt " + completed.ToString(CultureInfo.InvariantCulture) + " of "
                        + definitions.Count.ToString(CultureInfo.InvariantCulture) + "…");
                cancellationToken.ThrowIfCancellationRequested();
                List<Dictionary<string, object>> matches;
                byClip.TryGetValue(definition.Clip, out matches);
                DiscoveryMotionAsset asset = assets.Find(definition.Clip);
                VerifiedTauntRecord verified = null;
                bool fromArchive = false;
                // Extracted edits (including unsupported ones) always win. The
                // shipped exact body pack is a bundled fallback, so users who
                // keep a custom motion only inside a CAK must extract its active
                // .clip/.clips into Discovery to override this stock record.
                if (asset == null && matches == null) prepared.Records.TryRead(definition.Clip, out verified);
                cancellationToken.ThrowIfCancellationRequested();
                // Archive lookup fills only definitions with no extracted,
                // bundled or verified shipped record.
                if (asset == null && verified == null && matches == null && fallbacks != null)
                {
                    DiscoveryMotionAsset recovered;
                    if (fallbacks.Assets.TryGetValue(definition.Clip, out recovered) && recovered != null)
                    {
                        if (recovered.Id != DiscoveryMotionAssets.ClipId(definition.Clip))
                            result.Warnings.Add("Ignored a recovered animation whose record ID does not match " + definition.Clip + ".");
                        else { asset = recovered; fromArchive = true; }
                    }
                }
                if (asset == null && verified == null)
                {
                    if (matches == null)
                    {
                        string unsupported;
                        if (prepared.Records.TryGetUnsupported(definition.Clip, out unsupported))
                        {
                            result.Unsupported++;
                            AddUnavailable(result, definition, DiscoveryTauntUnavailableKind.UnsupportedFormat, unsupported);
                        }
                        else
                        {
                            result.Missing++;
                            AddUnavailable(result, definition, assets.ScanIncomplete || (fallbacks != null && fallbacks.Incomplete)
                                    ? DiscoveryTauntUnavailableKind.ScanIncomplete : DiscoveryTauntUnavailableKind.MissingAnimation,
                                LookupMissReason(definition.Clip, assets, sourceRoot, fallbacks));
                        }
                    }
                    else
                    {
                        AddNames(matches, definition);
                        foreach (Dictionary<string, object> item in matches)
                            item["tested_on"] = new[] { "Bundled motion; no matching body clip in Discovery" };
                    }
                    continue;
                }
                MotionImportStage stage = MotionImportStage.ReadSource;
                string sourcePath = verified == null ? asset.Path : "verified taunt pack " + prepared.Records.Sha256 + " · " + definition.Clip;
                try
                {
                    byte[] bytes = verified == null ? asset.Read() : verified.Bytes;
                    cancellationToken.ThrowIfCancellationRequested();
                    string hash = Hash(bytes);
                    cancellationToken.ThrowIfCancellationRequested();
                    string cached = Path.Combine(cache, hash + ".npz");
                    DecodedDiscoveryMotion motion = null;
                    if (!File.Exists(cached))
                    {
                        stage = MotionImportStage.Decode;
                        motion = DiscoveryMotionDecoder.Decode(bytes);
                        cancellationToken.ThrowIfCancellationRequested();
                        stage = MotionImportStage.PrepareCache;
                        DiscoveryMotionNpz.Write(cached, motion);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    stage = MotionImportStage.PrepareCache;
                    int frames = BitConverter.ToInt32(bytes, 0x44);
                    float fps = BitConverter.ToSingle(bytes, 0x0c);
                    string originalClip = Path.Combine(baseData, "gamepack", "decoded", definition.Clip + ".npz");
                    string stockMarker = cached + ".stock-v2-" + identity.Substring(0, 12);
                    bool originalMotion = false;
                    if (matches != null)
                    {
                        if (File.Exists(stockMarker)) originalMotion = File.ReadAllText(stockMarker) == "yes";
                        else
                        {
                            if (motion == null)
                            {
                                stage = MotionImportStage.Decode;
                                motion = DiscoveryMotionDecoder.Decode(bytes);
                                cancellationToken.ThrowIfCancellationRequested();
                                stage = MotionImportStage.PrepareCache;
                            }
                            originalMotion = DiscoveryMotionNpz.MatchesPoses(originalClip, motion);
                            cancellationToken.ThrowIfCancellationRequested();
                            WriteAtomic(stockMarker, originalMotion ? "yes" : "no", cancellationToken);
                        }
                    }
                    string destination = Path.Combine(decoded, definition.Clip + ".npz");
                    string stamp = destination + ".sha256";
                    if (!File.Exists(destination) || !File.Exists(stamp) || File.ReadAllText(stamp) != hash ||
                        new FileInfo(destination).Length != new FileInfo(cached).Length)
                    {
                        CopyAtomic(cached, destination, cancellationToken);
                        WriteAtomic(stamp, hash, cancellationToken);
                    }
                    applied.Add(definition.Clip);
                    if (fromArchive) result.ArchiveImported++;
                    if (verified != null) result.VerifiedImported++;
                    if (matches == null)
                    {
                        var item = new Dictionary<string, object> {
                            { "key", "discovery" + definition.Clip + "_" + hash.Substring(0, 12) },
                            { "name", definition.Name }, { "clip", definition.Clip }, { "cut", null },
                            { "blend", 0.6 }, { "face", "facial_6368.npz" }, { "face_offset", 0 },
                            { "seconds", Math.Round(frames / (double)fps, 2) },
                            { "also", definition.Names.Skip(1).ToArray() },
                            { "tags", new[] { verified == null ? "Discovery motion" : "Verified body motion" } },
                            { "drift_fix", false }, { "tested_on", new[] { verified == null ? "Preview on your selected wrestler" : "Verified body record; preview on your selected wrestler" } }
                        };
                        entries.Add(item);
                        matches = new List<Dictionary<string, object>> { item };
                        byClip.Add(definition.Clip, matches);
                        result.Imported++;
                    }
                    else
                    {
                        AddNames(matches, definition);
                        if (!originalMotion)
                            foreach (Dictionary<string, object> item in matches)
                            {
                                // The engine retains loop.npz per taunt key. A
                                // changed motion needs a new key to avoid an old
                                // pose cache surviving a Discovery refresh.
                                item["key"] = Convert.ToString(item["key"], CultureInfo.InvariantCulture) + "_discovery_" + hash.Substring(0, 12);
                                item["tested_on"] = new[] { "Discovery motion; preview on your selected wrestler" };
                                item["cut"] = null;
                                item["seconds"] = Math.Round(frames / (double)fps, 2);
                                item["preview"] = null;
                            }
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is ArgumentException || error is OverflowException)
                {
                    DiscoveryTauntUnavailableKind kind;
                    string reason;
                    if (stage == MotionImportStage.ReadSource)
                    {
                        result.ReadFailures++;
                        kind = DiscoveryTauntUnavailableKind.ReadFailure;
                        reason = "Animation clip " + definition.Clip + " was indexed in " + sourcePath + ", but could not be read: " + error.Message;
                    }
                    else if (stage == MotionImportStage.Decode)
                    {
                        result.Unsupported++;
                        kind = DiscoveryTauntUnavailableKind.UnsupportedFormat;
                        reason = "Animation clip " + definition.Clip + " was found in " + sourcePath + ", but this app could not decode its animation format: " + error.Message;
                    }
                    else
                    {
                        result.CacheFailures++;
                        kind = DiscoveryTauntUnavailableKind.CacheFailure;
                        reason = "Animation clip " + definition.Clip + " was found in " + sourcePath + ", but its preview cache could not be prepared in " + storageRoot + ": " + error.Message;
                    }
                    result.Warnings.Add(definition.Name + ": " + error.Message);
                    if (matches != null)
                    {
                        AddNames(matches, definition);
                        foreach (Dictionary<string, object> item in matches)
                            item["tested_on"] = new[] { kind == DiscoveryTauntUnavailableKind.ReadFailure ? "Bundled motion; Discovery clip could not be read" :
                                kind == DiscoveryTauntUnavailableKind.CacheFailure ? "Bundled motion; Discovery preview cache could not be prepared" :
                                "Bundled motion; Discovery animation format could not be decoded" };
                    }
                    else AddUnavailable(result, definition, kind, reason);
                }
            }
            foreach (string stamp in Directory.EnumerateFiles(decoded, "*.npz.sha256"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string clip = Path.GetFileName(stamp).Substring(0, Path.GetFileName(stamp).Length - ".npz.sha256".Length);
                if (applied.Contains(clip)) continue;
                string original = Path.Combine(baseData, "gamepack", "decoded", clip + ".npz");
                string target = Path.Combine(decoded, clip + ".npz");
                if (File.Exists(original)) CopyAtomic(original, target, cancellationToken);
                else if (File.Exists(target)) File.Delete(target);
                File.Delete(stamp);
            }
            RemoveOtherVariantAliases(entries, definitions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            entries = entries.OrderBy(item => Convert.ToString(item["name"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase).ToList();
            manifest["taunts"] = entries;
            progress?.Invoke("Publishing taunt library…");
            cancellationToken.ThrowIfCancellationRequested();
            WriteAtomic(Path.Combine(library, "taunts.json"), Json(manifest), cancellationToken);
            result.Warnings.AddRange(prepared.Records.Warnings.Where(warning => !result.Warnings.Contains(warning)));
            result.Root = overlay;
            result.Library = library;
            result.Taunts = entries;
            result.Available = entries.Count;
            return result;
        }

        private enum MotionImportStage { ReadSource, Decode, PrepareCache }

        private static string LookupMissReason(string clip, DiscoveryMotionAssets assets, string sourceRoot, DiscoveryMotionFallbacks fallbacks)
        {
            string roots = assets.SearchRoots.Count == 0 ? sourceRoot : string.Join("; ", assets.SearchRoots);
            string reason = "Animation clip " + clip + " was not located in the scanned extracted .clip/.clips files under " + roots + ".";
            if (assets.ScanIncomplete)
                reason += " The scan was incomplete" + (assets.Warnings.Count == 0 ? " because some files or folders could not be read."
                    : ": " + assets.Warnings[0].TrimEnd('.') + ".") + " Load Discovery data again to review the scan warnings.";
            else if (!assets.AnimationRootFound)
                reason += " No Animation folder was found in that scan.";
            string issue;
            if (fallbacks != null && fallbacks.Issues.TryGetValue(clip, out issue))
                return reason + " " + issue;
            if (fallbacks != null && fallbacks.ArchivesSearched)
                return reason + " No unambiguous matching motion was recovered from the available archive search."
                    + (fallbacks.Incomplete ? " The archive search did not finish; see its warnings." : "");
            return reason + " Installed-game CAK archives were not searched.";
        }

        private static void AddUnavailable(DiscoveryTauntImport result, DiscoveryTaunt definition,
            DiscoveryTauntUnavailableKind kind, string reason)
        {
            result.Unavailable.Add(new DiscoveryUnavailableTaunt {
                Id = definition.Id, Variant = definition.Variant, Clip = definition.Clip,
                Name = definition.Name, Names = new List<string>(definition.Names), Kind = kind, Reason = reason
            });
        }

        private static void AddNames(IEnumerable<Dictionary<string, object>> items, DiscoveryTaunt definition)
        {
            foreach (Dictionary<string, object> item in items)
            {
                var names = new List<string>(definition.Names);
                object existing;
                if (item.TryGetValue("name", out existing)) names.Add(Convert.ToString(existing, CultureInfo.InvariantCulture));
                if (item.TryGetValue("also", out existing) && existing is IEnumerable)
                    foreach (object name in (IEnumerable)existing) names.Add(Convert.ToString(name, CultureInfo.InvariantCulture));
                item["name"] = definition.Name;
                item["also"] = names.Where(name => !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, definition.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        private static void RemoveOtherVariantAliases(IEnumerable<Dictionary<string, object>> entries,
            IEnumerable<DiscoveryTaunt> definitions, CancellationToken cancellationToken)
        {
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveryTaunt definition in definitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string name in definition.Names)
                {
                    string normalized = name.Trim();
                    HashSet<string> clips;
                    if (!owners.TryGetValue(normalized, out clips))
                        owners.Add(normalized, clips = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    clips.Add(definition.Clip);
                }
            }
            foreach (Dictionary<string, object> entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object value;
                if (!entry.TryGetValue("also", out value) || !(value is IEnumerable) || value is string ||
                    !entry.ContainsKey("clip")) continue;
                string clip = Convert.ToString(entry["clip"], CultureInfo.InvariantCulture);
                var aliases = new List<string>();
                foreach (object alias in (IEnumerable)value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = Convert.ToString(alias, CultureInfo.InvariantCulture).Trim();
                    HashSet<string> clips;
                    // Older Gorilla libraries grouped separate animation
                    // variants as search aliases. Keep an alias only when its
                    // Discovery definition belongs to this exact motion clip.
                    if (owners.TryGetValue(name, out clips) && !clips.Contains(clip)) continue;
                    if (name.Length > 0 && !aliases.Contains(name, StringComparer.OrdinalIgnoreCase)) aliases.Add(name);
                }
                entry["also"] = aliases.ToArray();
            }
        }

        private static void LinkChildren(string source, string destination, string[] excludedDirectories, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string directory in Directory.EnumerateDirectories(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!excludedDirectories.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                    DirectoryLinks.EnsureJunction(Path.Combine(destination, Path.GetFileName(directory)), directory);
            }
            foreach (string file in Directory.EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFileName(file), "taunts.json", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target)) CopyAtomic(file, target, cancellationToken);
            }
        }

        private static void CopyAtomic(string source, string destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = input.Read(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void WriteAtomic(string path, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), value, StringComparison.Ordinal)) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, value, new UTF8Encoding(false));
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static Dictionary<string, object> JsonObject(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json)) return (Dictionary<string, object>)Object(document.RootElement);
#else
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
#endif
        }

        private static void MaterializeDirectory(string source, string target, bool readOnlyFiles, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(source)) { Directory.CreateDirectory(target); return; }
            if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                if (!string.Equals(DirectoryLinks.Resolve(target), DirectoryLinks.Resolve(source), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("An unexpected link occupies the Discovery animation cache.");
                Directory.Delete(target);
            }
            Directory.CreateDirectory(target);
            foreach (string original in Directory.EnumerateFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Combine(target, Path.GetFileName(original));
                if (File.Exists(destination)) continue;
                bool linked = false;
                if (readOnlyFiles && Environment.OSVersion.Platform == PlatformID.Win32NT)
                    linked = CreateHardLink(destination, original, IntPtr.Zero);
                if (!linked) CopyAtomic(original, destination, cancellationToken);
            }
            foreach (string child in Directory.EnumerateDirectories(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                MaterializeDirectory(child, Path.Combine(target, Path.GetFileName(child)), readOnlyFiles, cancellationToken);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);

        internal static string Json(object value)
        {
#if NET8_0_OR_GREATER
            return JsonSerializer.Serialize(value);
#else
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value);
#endif
        }

#if NET8_0_OR_GREATER
        private static object Object(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: return value.EnumerateObject().ToDictionary(property => property.Name, property => Object(property.Value));
                case JsonValueKind.Array: return value.EnumerateArray().Select(Object).ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number: return value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
