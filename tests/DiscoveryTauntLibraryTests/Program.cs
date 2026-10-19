using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using GorillaStudio.NameFolders;

namespace GorillaStudio.NameFolders
{
    // Exercise real library generation on Linux using directory symlinks in
    // place of the Windows junction implementation. Source assets stay intact.
    internal static class DirectoryLinks
    {
        internal static Action BeforeEnsureJunction;
        internal static string Resolve(string path)
        {
            var directory = new DirectoryInfo(path);
            FileSystemInfo target = directory.ResolveLinkTarget(true);
            return target == null ? directory.FullName : target.FullName;
        }
        internal static void EnsureJunction(string link, string target)
        {
            Action before = BeforeEnsureJunction;
            BeforeEnsureJunction = null;
            if (before != null) before();
            if (Directory.Exists(link))
            {
                if (!string.Equals(Resolve(link), Path.GetFullPath(target), StringComparison.Ordinal))
                    throw new IOException("A test overlay link points to another target.");
                return;
            }
            Directory.CreateSymbolicLink(link, target);
        }
    }
}

internal static class Program
{
    private static int passed;

    private static void Main(string[] args)
    {
        string work = Path.Combine(Path.GetTempPath(), "discovery-library-units-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try { UnitTests(work); }
        finally { Directory.Delete(work, true); }
        if (args.Length == 3) Actual(args[0], args[1], args[2]);
        Console.WriteLine("PASS " + passed + " Discovery taunt library checks");
    }

    private static void UnitTests(string root)
    {
        Equal("Clip names use the actual FNV-1a lookup identifier", 0xD2F60E7B78BF693AUL, DiscoveryMotionAssets.ClipId("62541a000"));
        string animation = Directory.CreateDirectory(Path.Combine(root, "Animation", "Acts")).FullName;
        byte[] clip = Motion("62541a000", 60);
        string container = Path.Combine(animation, "MotionArea.clips");
        Container(container, clip);
        DiscoveryMotionAssets assets = DiscoveryMotionAssets.Read(root);
        Equal("A valid container indexes its actual motion record", 1, assets.Count);
        Equal("A container record is not mislabeled as a loose override", false, assets.Find("62541a000").Loose);
        Equal("An indexed container record reads its exact original bytes", true, assets.Find("62541a000").Read().SequenceEqual(clip));
        Equal("Missing animation identifiers are never substituted", null, assets.Find("62542a000"));

        string loose = Path.Combine(animation, "Edited.clip");
        byte[] overrideClip = Motion("62541a000", 30);
        File.WriteAllBytes(loose, overrideClip);
        assets = DiscoveryMotionAssets.Read(root);
        Equal("A loose edited clip takes precedence over its container", true, assets.Find("62541a000").Loose);
        Equal("The loose edited motion data is read instead of stock data", true, assets.Find("62541a000").Read().SequenceEqual(overrideClip));
        string newer = Path.Combine(animation, "Newest.clip");
        byte[] latest = Motion("62541a000", 24);
        File.WriteAllBytes(newer, latest);
        File.SetLastWriteTimeUtc(loose, DateTime.UtcNow.AddMinutes(-2));
        assets = DiscoveryMotionAssets.Read(root);
        Equal("The newest loose Discovery edit wins deterministically", true, assets.Find("62541a000").Read().SequenceEqual(latest));

        File.WriteAllBytes(Path.Combine(animation, "Invalid.clips"), new byte[24]);
        assets = DiscoveryMotionAssets.Read(root);
        Equal("A broken container does not remove valid motion records", 1, assets.Count);
        Equal("A broken container produces an explanatory warning", true, assets.Warnings.Count > 0);
        File.WriteAllBytes(Path.Combine(animation, "Invalid.clips"), new byte[32]);
        assets = DiscoveryMotionAssets.Read(root);
        Equal("An actual-format empty container does not hide valid records", 1, assets.Count);
        Equal("A valid empty 32-byte container produces no corruption warning", 0, assets.Warnings.Count);

        DecodedDiscoveryMotion decoded = DiscoveryMotionDecoder.Decode(overrideClip);
        string npz = Path.Combine(root, "motion.npz");
        DiscoveryMotionNpz.Write(npz, decoded);
        using (var zip = ZipFile.OpenRead(npz))
        {
            Equal("The engine NPZ contains exactly its five required arrays", 5, zip.Entries.Count);
            foreach (string name in new[] { "ids", "P", "sids", "sc", "hd" })
            {
                ZipArchiveEntry entry = zip.GetEntry(name + ".npy");
                Equal("The " + name + " array is available", true, entry != null);
                using (var reader = new BinaryReader(entry.Open()))
                    Equal("The " + name + " array has the standard NumPy header", true,
                        reader.ReadBytes(8).SequenceEqual(new byte[] { 0x93, 0x4E, 0x55, 0x4D, 0x50, 0x59, 1, 0 }));
            }
        }
        DiscoveryMotionNpz.Write(npz, decoded);
        Equal("An existing NPZ is safely replaceable", true, File.Exists(npz));
        Equal("Successful atomic writes leave no partial temporary file", 0, Directory.GetFiles(root, "*.tmp").Length);
        Equal("An NPZ exactly matches the motion from which it was written", true, DiscoveryMotionNpz.MatchesPoses(npz, decoded));
        decoded.Ids[0] ^= 1;
        Equal("Changed bone identifiers invalidate an existing preview motion", false, DiscoveryMotionNpz.MatchesPoses(npz, decoded));
        decoded = DiscoveryMotionDecoder.Decode(overrideClip); decoded.Fps = 60;
        Equal("Changed frame timing invalidates an existing preview motion", false, DiscoveryMotionNpz.MatchesPoses(npz, decoded));
        decoded = DiscoveryMotionDecoder.Decode(overrideClip); decoded.Frames++;
        Equal("Changed frame counts invalidate an existing preview motion", false, DiscoveryMotionNpz.MatchesPoses(npz, decoded));
        decoded = DiscoveryMotionDecoder.Decode(overrideClip); decoded.Poses[0, 0, 0] = 10;
        Equal("Changed animation poses invalidate an existing preview motion", false, DiscoveryMotionNpz.MatchesPoses(npz, decoded));
        CaptureTests(root);
        RefreshTests(root);
        ScopeTests(root);
        DiagnosticTests(root);
        FallbackTests(root);
        SearchTests();
    }

    private sealed class FallbackFixture
    {
        internal string App, Library, Decoded, Data, Table, Storage, CachedClip;
        internal DiscoveryMotionFallbacks Fallbacks;
    }

    private static FallbackFixture FallbackFixtureAt(string root, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "archive-fallback", name)).FullName;
        var f = new FallbackFixture {
            App = Directory.CreateDirectory(Path.Combine(directory, "app")).FullName,
            Data = Directory.CreateDirectory(Path.Combine(directory, "discovery_data")).FullName,
            Storage = Directory.CreateDirectory(Path.Combine(directory, "private-cache")).FullName
        };
        f.Library = Directory.CreateDirectory(Path.Combine(f.App, "data", "library")).FullName;
        f.Decoded = Directory.CreateDirectory(Path.Combine(f.App, "data", "gamepack", "decoded")).FullName;
        Directory.CreateDirectory(Path.Combine(f.App, "engine"));
        Directory.CreateDirectory(Path.Combine(f.Data, "MoveData"));
        File.WriteAllText(Path.Combine(f.Library, "taunts.json"), "{\"taunts\":[]}");
        f.Table = Path.Combine(f.Data, "MoveData", "CAMMoveData.jsfb");
        byte[] table = CamTable();
        Write(table, 136, 17330u); Write(table, 140, 0u);
        byte[] label = System.Text.Encoding.UTF8.GetBytes("Enigma 4");
        Array.Clear(table, 164, 40); Write(table, 160, (uint)label.Length); Array.Copy(label, 0, table, 164, label.Length);
        File.WriteAllBytes(f.Table, table);
        string privateMotions = Directory.CreateDirectory(Path.Combine(f.Storage, "archive-motion-cache")).FullName;
        f.CachedClip = Path.Combine(privateMotions, "17330a000.clip");
        byte[] clip = Motion("17330a000", 30);
        Array.Copy(BitConverter.GetBytes(10f), 0, clip, 0xA8 + 4, 4);
        File.WriteAllBytes(f.CachedClip, clip);
        f.Fallbacks = new DiscoveryMotionFallbacks { ArchivesSearched = true, ArchiveCount = 2, Status = "Recovered the exact clip from indexed archives." };
        f.Fallbacks.Assets["17330a000"] = FallbackAsset(f.CachedClip, "17330a000");
        return f;
    }

    private static DiscoveryMotionAsset FallbackAsset(string path, string declaredClip)
    {
        return new DiscoveryMotionAsset { Path = path, Offset = 0, Length = checked((int)new FileInfo(path).Length), Id = DiscoveryMotionAssets.ClipId(declaredClip), Loose = false };
    }

    private static void FallbackTests(string root)
    {
        const string clip = "17330a000";
        var f = FallbackFixtureAt(root, "exact-enigma");
        var sources = new[] { Path.Combine(f.Library, "taunts.json"), f.Table, f.CachedClip }.ToDictionary(path => path, Hash);
        DiscoveryTauntImport first = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        string manifest = Path.Combine(first.Library, "taunts.json");
        string npz = Path.Combine(first.Root, "data", "gamepack", "decoded", clip + ".npz");
        Equal("An exact private cached Enigma 4 body clip fills its Discovery definition", 1, first.Imported);
        Equal("Recovered archive motions have an explicit successful-import count", 1, first.ArchiveImported);
        Equal("The recovered Enigma variant uses its complete canonical clip identity", clip,
            JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("taunts")[0].GetProperty("clip").GetString());
        Equal("The recovered clip keeps the actual Discovery label Enigma 4", "Enigma 4",
            JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("taunts")[0].GetProperty("name").GetString());
        Equal("A recovered variant receives a real engine key rather than an unavailable UI key", true,
            ManifestKey(manifest).StartsWith("discovery17330a000_", StringComparison.Ordinal));
        Equal("Recovered motion poses match the exact private clip bytes", true,
            DiscoveryMotionNpz.MatchesPoses(npz, DiscoveryMotionDecoder.Decode(File.ReadAllBytes(f.CachedClip))));
        Equal("Recovered clip import removes the unavailable row", 0, first.Unavailable.Count);
        Equal("The imported catalog preserves archive-search count and status", true,
            first.ArchivesSearched && first.ArchiveCount == 2 && first.ArchiveStatus == f.Fallbacks.Status);
        Equal("Importing recovered motion never changes Discovery or bundled sources", true, sources.All(item => Hash(item.Key) == item.Value));

        string key = ManifestKey(manifest);
        string previews = Directory.CreateDirectory(Path.Combine(first.Library, "previews_game")).FullName;
        string preview = Path.Combine(previews, key + ".png");
        File.WriteAllBytes(preview, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ1kAAAAASUVORK5CYII="));
        var beforePreflight = Directory.GetFiles(f.Storage, "*", SearchOption.AllDirectories).ToDictionary(path => path,
            path => Tuple.Create(Hash(path), File.GetLastWriteTimeUtc(path)));
        string inferredRoot;
        List<string> unresolved = DiscoveryTauntLibrary.FindUnresolvedClips(f.App, f.Table, out inferredRoot);
        Equal("Read-only archive preflight retains the exact unresolved Enigma clip", true, unresolved.SequenceEqual(new[] { clip }));
        Equal("Read-only archive preflight recovers the full data root from the selected CAM table", f.Data, inferredRoot);
        Equal("Read-only preflight does not delete the previous recovered overlay NPZ", true, File.Exists(npz));
        Equal("Read-only preflight preserves every existing recovered cache, manifest, stamp and preview byte and timestamp", true,
            beforePreflight.All(item => File.Exists(item.Key) && Hash(item.Key) == item.Value.Item1 && File.GetLastWriteTimeUtc(item.Key) == item.Value.Item2));
        Equal("Read-only preflight creates no additional overlay or preview files", beforePreflight.Count, Directory.GetFiles(f.Storage, "*", SearchOption.AllDirectories).Length);
        Equal("Read-only preflight leaves all Discovery and bundled source bytes unchanged", true, sources.All(item => Hash(item.Key) == item.Value));

        string firstPoseHash = PoseHash(npz);
        byte[] changed = Motion(clip, 60); Array.Copy(BitConverter.GetBytes(25f), 0, changed, 0xA8 + 4, 4);
        File.WriteAllBytes(f.CachedClip, changed);
        DiscoveryTauntImport changedImport = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("Changed recovered animation bytes receive a different renderer cache key", false, key == ManifestKey(manifest));
        Equal("Changed recovered motion remains a counted archive import", 1, changedImport.ArchiveImported);
        Equal("Changed recovered bytes refresh the actual decoded motion poses", true,
            firstPoseHash != PoseHash(npz) && DiscoveryMotionNpz.MatchesPoses(npz, DiscoveryMotionDecoder.Decode(changed)));
        Equal("Refreshing recovered motion leaves its private source clip unchanged", true, File.ReadAllBytes(f.CachedClip).SequenceEqual(changed));

        f = FallbackFixtureAt(root, "wrong-declared-id");
        f.Fallbacks.Assets[clip] = FallbackAsset(f.CachedClip, "17331a000");
        var rejected = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("A fallback whose declared record ID differs from Enigma 4 is never imported", 0, rejected.ArchiveImported);
        Equal("A declared-ID mismatch cannot create a fake playable engine entry", 0, ManifestCount(Path.Combine(rejected.Library, "taunts.json")));
        Equal("A declared-ID mismatch retains the exact unavailable variant", clip, rejected.Unavailable.Single().Clip);
        Equal("A declared-ID mismatch includes its explicit validation warning", true, rejected.Warnings.Any(warning => warning.Contains("record ID") && warning.Contains(clip)));

        f = FallbackFixtureAt(root, "wrong-header-id");
        File.WriteAllBytes(f.CachedClip, Motion("17331a000", 30));
        rejected = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("A correct declared ID cannot hide a different file-header identity", 0, rejected.ArchiveImported);
        Equal("A mismatched private clip header cannot become a playable engine entry", 0, ManifestCount(Path.Combine(rejected.Library, "taunts.json")));
        Equal("A mismatched private clip header has a source-read failure rather than a format guess", DiscoveryTauntUnavailableKind.ReadFailure, rejected.Unavailable.Single().Kind);
        Equal("A mismatched-header diagnostic identifies the expected clip and cached source", true,
            rejected.Unavailable.Single().Reason.Contains(clip) && rejected.Unavailable.Single().Reason.Contains(f.CachedClip));

        f = FallbackFixtureAt(root, "editable-priority");
        string extracted = Directory.CreateDirectory(Path.Combine(f.Data, "Animation", "Edits")).FullName;
        byte[] edited = Motion(clip, 24); Array.Copy(BitConverter.GetBytes(50f), 0, edited, 0xA8 + 4, 4);
        string editedPath = Path.Combine(extracted, "edited.clip"); File.WriteAllBytes(editedPath, edited);
        string cachedSourceHash = Hash(f.CachedClip);
        var editable = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("An extracted editable clip takes precedence over the archive fallback", 0, editable.ArchiveImported);
        Equal("Extracted motion priority uses the actual edited poses", true,
            DiscoveryMotionNpz.MatchesPoses(Path.Combine(editable.Root, "data", "gamepack", "decoded", clip + ".npz"), DiscoveryMotionDecoder.Decode(edited)));
        Equal("Using extracted motion does not rewrite its fallback cache source", cachedSourceHash, Hash(f.CachedClip));
        Equal("Read-only preflight does not request archive recovery when an extracted exact clip exists", 0,
            DiscoveryTauntLibrary.FindUnresolvedClips(f.App, f.Data, out inferredRoot).Count);

        f = FallbackFixtureAt(root, "bundled-priority");
        File.WriteAllText(Path.Combine(f.Library, "taunts.json"), JsonSerializer.Serialize(new { taunts = new[] {
            new { key = "bundled_enigma", name = "Bundled Enigma 4", clip, cut = new[] { 1, 2 }, seconds = 0.1 }
        } }));
        string bundled = Path.Combine(f.Decoded, clip + ".npz");
        byte[] bundledMotion = Motion(clip, 30); DiscoveryMotionNpz.Write(bundled, DiscoveryMotionDecoder.Decode(bundledMotion));
        string bundledHash = Hash(bundled);
        var stock = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("An existing bundled motion takes precedence over archive recovery", 0, stock.ArchiveImported);
        Equal("Archive fallback cannot replace the bundled renderer key", "bundled_enigma", ManifestKey(Path.Combine(stock.Library, "taunts.json")));
        Equal("Archive fallback leaves the original bundled NPZ unchanged", bundledHash, Hash(bundled));
        Equal("Bundled priority preserves the bundled motion in the engine overlay", bundledHash,
            Hash(Path.Combine(stock.Root, "data", "gamepack", "decoded", clip + ".npz")));
        Equal("Read-only preflight does not request archive recovery for an existing bundled motion", 0,
            DiscoveryTauntLibrary.FindUnresolvedClips(f.App, f.Data, out inferredRoot).Count);

        f = FallbackFixtureAt(root, "conflict-reason"); f.Fallbacks.Assets.Clear();
        const string conflict = "Two indexed archives contain different records for 17330a000; no archive load order was assumed.";
        f.Fallbacks.Issues[clip] = conflict;
        var conflicted = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("An unresolved archive conflict preserves the precise resolver reason", true, conflicted.Unavailable.Single().Reason.Contains(conflict));
        Equal("An unresolved archive conflict cannot fabricate a playable entry", 0, ManifestCount(Path.Combine(conflicted.Library, "taunts.json")));
        Equal("A searched archive conflict is not mislabeled as an unsearched archive", false, conflicted.Unavailable.Single().Reason.Contains("CAK archives were not searched"));
        f.Fallbacks.Incomplete = true;
        const string incomplete = "The installed archive search stopped before MotionArea.cak could be indexed; exact 17330a000 recovery remains unresolved.";
        f.Fallbacks.Issues[clip] = incomplete; f.Fallbacks.Warnings.Add("MotionArea.cak could not be indexed.");
        var unfinished = DiscoveryTauntLibrary.Build(f.App, f.Storage, f.Data, f.Fallbacks);
        Equal("An incomplete archive search retains a separate unavailable classification", DiscoveryTauntUnavailableKind.ScanIncomplete, unfinished.Unavailable.Single().Kind);
        Equal("An incomplete archive search preserves its exact resolver reason", true, unfinished.Unavailable.Single().Reason.Contains(incomplete));
        Equal("An incomplete archive search retains its original warning", true, unfinished.Warnings.Contains("MotionArea.cak could not be indexed."));
    }

    private static void ScopeTests(string root)
    {
        string data = Directory.CreateDirectory(Path.Combine(root, "nested-motion-scope")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(data, "Mods", "Custom", "Animation", "Acts")).FullName;
        Container(Path.Combine(nested, "Nested.clips"), Motion("60001a000", 30));
        string overrides = Directory.CreateDirectory(Path.Combine(data, "MoveData", "Overrides")).FullName;
        File.WriteAllBytes(Path.Combine(overrides, "Exported.clip"), Motion("60002a001", 30));
        DiscoveryMotionAssets assets = DiscoveryMotionAssets.Read(data);
        Equal("A nested mod Animation folder is searched without a direct-root Animation folder", true, assets.Find("60001a000") != null);
        Equal("A loose export outside an Animation folder is searched", true, assets.Find("60002a001") != null);
        Equal("The full data scan records its actual search root", true, assets.SearchRoots.Contains(data));
        Equal("A nested Animation folder is reported as present", true, assets.AnimationRootFound);
        Equal("A readable complete data scan is not marked incomplete", false, assets.ScanIncomplete);
        Equal("Whole-folder search keeps both independent clip identities", 2, assets.Count);
    }

    private static void DiagnosticTests(string root)
    {
        string app = Directory.CreateDirectory(Path.Combine(root, "diagnostic-app")).FullName;
        string library = Directory.CreateDirectory(Path.Combine(app, "data", "library")).FullName;
        Directory.CreateDirectory(Path.Combine(app, "data", "gamepack", "decoded"));
        File.WriteAllText(Path.Combine(library, "taunts.json"), "{\"taunts\":[]}");
        string data = Directory.CreateDirectory(Path.Combine(root, "diagnostic-data")).FullName;
        string moves = Directory.CreateDirectory(Path.Combine(data, "MoveData")).FullName;
        File.WriteAllBytes(Path.Combine(moves, "CAMMoveData.jsfb"), CamTable());
        string storage = Path.Combine(root, "diagnostic-cache");
        DiscoveryTauntImport absent = DiscoveryTauntLibrary.Build(app, storage, data);
        DiscoveryUnavailableTaunt entry = absent.Unavailable.Single();
        Equal("A complete lookup miss remains distinct from an unreadable scan", DiscoveryTauntUnavailableKind.MissingAnimation, entry.Kind);
        Equal("A lookup diagnostic names the exact clip", true, entry.Reason.Contains("62541a000"));
        Equal("A lookup diagnostic names the exact scanned data root", true, entry.Reason.Contains(data));
        Equal("A lookup diagnostic identifies the extracted file formats searched", true, entry.Reason.Contains(".clip/.clips"));
        Equal("A lookup diagnostic states that game archives were not searched", true, entry.Reason.Contains("CAK archives were not searched"));
        Equal("A complete scan without an Animation folder explains that context", true, entry.Reason.Contains("No Animation folder"));
        Equal("A lookup miss does not claim that the animation file is absent", false, entry.Reason.Contains("file is missing"));
        Equal("Import results expose the actual searched roots", true, absent.SearchRoots.Contains(data));

        string nested = Directory.CreateDirectory(Path.Combine(data, "Mods", "Nested")).FullName;
        string malformed = Path.Combine(nested, "Unreadable.clips");
        File.WriteAllBytes(malformed, new byte[24]);
        DiscoveryTauntImport incomplete = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("A malformed container marks the search incomplete", true, incomplete.ScanIncomplete);
        Equal("An incomplete lookup miss has a separate classification", DiscoveryTauntUnavailableKind.ScanIncomplete, incomplete.Unavailable.Single().Kind);
        Equal("Incomplete-scan guidance refers to the load warnings", true, incomplete.Unavailable.Single().Reason.Contains("scan was incomplete"));
        Equal("An incomplete-scan tooltip names its first problematic file immediately", true,
            incomplete.Unavailable.Single().Reason.Contains(malformed) && incomplete.Unavailable.Single().Reason.Contains("invalid record table"));
        Equal("Unreadable scan data is not mislabeled as an unsupported animation", 0, incomplete.Unsupported);
        Equal("The source scan warning is retained", true, incomplete.Warnings.Any(warning => warning.Contains("Unreadable.clips")));
        File.Delete(malformed);

        string raw = Path.Combine(nested, "Exported.clip");
        File.WriteAllBytes(raw, Motion("62541a000", 30));
        string cacheBlock = Directory.CreateDirectory(Path.Combine(storage, "motion-cache-v1", Hash(raw).ToLowerInvariant() + ".npz")).FullName;
        DiscoveryTauntImport cacheFailure = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("A preview-cache write failure has a distinct classification", DiscoveryTauntUnavailableKind.CacheFailure, cacheFailure.Unavailable.Single().Kind);
        Equal("Preview-cache errors have their own count", 1, cacheFailure.CacheFailures);
        Equal("A cache error does not claim the animation format is unsupported", 0, cacheFailure.Unsupported);
        Equal("A cache error does not claim a source clip is missing", 0, cacheFailure.Missing);
        Equal("A cache diagnostic identifies the source file and writable cache", true,
            cacheFailure.Unavailable.Single().Reason.Contains(raw) && cacheFailure.Unavailable.Single().Reason.Contains(storage));
        Directory.Delete(cacheBlock);

        Directory.CreateDirectory(Path.Combine(app, "engine"));
        DirectoryLinks.BeforeEnsureJunction = () => File.Delete(raw);
        DiscoveryTauntImport readFailure;
        try { readFailure = DiscoveryTauntLibrary.Build(app, storage, data); }
        finally { DirectoryLinks.BeforeEnsureJunction = null; }
        Equal("A clip removed after indexing has a source-read classification", DiscoveryTauntUnavailableKind.ReadFailure, readFailure.Unavailable.Single().Kind);
        Equal("Source-read errors have their own count", 1, readFailure.ReadFailures);
        Equal("A source-read failure does not claim an unsupported format", 0, readFailure.Unsupported);
        Equal("A source-read diagnostic names the indexed file and exact clip", true,
            readFailure.Unavailable.Single().Reason.Contains(raw) && readFailure.Unavailable.Single().Reason.Contains("62541a000"));
        Equal("A source-read failure stays out of the engine manifest", 0, ManifestCount(Path.Combine(readFailure.Library, "taunts.json")));

        byte[] unsupported = Motion("62541a000", 30); Write(unsupported, 0x60, 1u); File.WriteAllBytes(raw, unsupported);
        DiscoveryTauntImport formatFailure = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("An actual decoder rejection retains the format classification", DiscoveryTauntUnavailableKind.UnsupportedFormat, formatFailure.Unavailable.Single().Kind);
        Equal("An actual decoder rejection increments only the unsupported count", 1, formatFailure.Unsupported);
        Equal("A decoder rejection does not increment source-read errors", 0, formatFailure.ReadFailures);
        Equal("A decoder rejection does not increment cache errors", 0, formatFailure.CacheFailures);
        Equal("A decoder diagnostic names the found clip and the decoder limitation", true,
            formatFailure.Unavailable.Single().Reason.Contains("62541a000") && formatFailure.Unavailable.Single().Reason.Contains("additional channel sections"));
    }

    private static void RefreshTests(string root)
    {
        string app = Directory.CreateDirectory(Path.Combine(root, "mini-app")).FullName;
        string library = Directory.CreateDirectory(Path.Combine(app, "data", "library")).FullName;
        string decoded = Directory.CreateDirectory(Path.Combine(app, "data", "gamepack", "decoded")).FullName;
        string data = Directory.CreateDirectory(Path.Combine(root, "mini-discovery")).FullName;
        string moves = Directory.CreateDirectory(Path.Combine(data, "MoveData")).FullName;
        string animations = Directory.CreateDirectory(Path.Combine(data, "Animation")).FullName;
        string storage = Path.Combine(root, "mini-cache");
        File.WriteAllText(Path.Combine(library, "taunts.json"), JsonSerializer.Serialize(new { taunts = new[] {
            new { key = "original", name = "Bundled taunt", clip = "62541a000", cut = new[] { 1, 2 }, seconds = 0.1 }
        } }));
        string original = Path.Combine(decoded, "62541a000.npz");
        DiscoveryMotionNpz.Write(original, DiscoveryMotionDecoder.Decode(Motion("62541a000", 30)));
        string originalHash = Hash(original);
        string table = Path.Combine(moves, "CAMMoveData.jsfb");
        File.WriteAllBytes(table, CamTable());
        string raw = Path.Combine(animations, "edited.clip");
        byte[] edited = Motion("62541a000", 60);
        Array.Copy(BitConverter.GetBytes(10f), 0, edited, 0xA8 + 4, 4);
        File.WriteAllBytes(raw, edited);
        DiscoveryTauntImport first = DiscoveryTauntLibrary.Build(app, storage, data);
        string target = Path.Combine(first.Root, "data", "gamepack", "decoded", "62541a000.npz");
        string manifest = Path.Combine(first.Library, "taunts.json");
        Equal("A changed Discovery animation receives its own versioned engine cache key", true, ManifestKey(manifest).Contains("_discovery_"));
        Equal("An override is decoded from the actual edited motion", true,
            DiscoveryMotionNpz.MatchesPoses(target, DiscoveryMotionDecoder.Decode(edited)));
        Equal("An override retains a stamp for later source refresh", true, File.Exists(target + ".sha256"));
        Equal("An edited override leaves the original bundled NPZ untouched", originalHash, Hash(original));

        File.Delete(raw);
        DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("Removing an edited motion restores the original bundled animation", originalHash, Hash(target));
        Equal("Removing an edited motion restores its original cache key", "original", ManifestKey(manifest));
        Equal("Removing an edited motion clears the stale override stamp", false, File.Exists(target + ".sha256"));

        File.WriteAllBytes(raw, edited);
        DiscoveryTauntLibrary.Build(app, storage, data);
        byte[] unsupported = (byte[])edited.Clone(); Write(unsupported, 0x60, 1u);
        File.WriteAllBytes(raw, unsupported);
        DiscoveryTauntImport rejected = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("An unsupported replacement is reported clearly", 1, rejected.Unsupported);
        Equal("An unsupported replacement restores the original animation", originalHash, Hash(target));
        Equal("An unsupported replacement cannot leave a stale preview cache key", "original", ManifestKey(manifest));
        Equal("An unsupported Discovery replacement with a bundled fallback remains playable", 0, rejected.Unavailable.Count);

        File.WriteAllBytes(raw, edited);
        DiscoveryTauntLibrary.Build(app, storage, data);
        byte[] removedDefinition = CamTable(); Write(removedDefinition, 212, 0u);
        File.WriteAllBytes(table, removedDefinition);
        DiscoveryTauntImport removed = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("Removing a Discovery definition refreshes its definition count", 0, removed.Definitions);
        Equal("Removing a Discovery definition clears its stale animation override", originalHash, Hash(target));

        File.Delete(raw);
        byte[] newMissing = CamTable(); Write(newMissing, 136, 60000u); Write(newMissing, 140, 2u);
        File.WriteAllBytes(table, newMissing);
        DiscoveryTauntImport missing = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("A new definition with no motion stays in the searchable unavailable catalog", 1, missing.Unavailable.Count);
        var unavailable = missing.Unavailable.Single();
        Equal("Unavailable definitions preserve their complete variant identity", "60000a002", unavailable.Clip);
        Equal("Missing body clips have an explicit missing-animation reason", DiscoveryTauntUnavailableKind.MissingAnimation, unavailable.Kind);
        Equal("Unavailable keys are reserved for the UI", "discovery_unavailable_60000a002", unavailable.Key);
        Equal("Unavailable records do not become renderable engine entries", 1, ManifestCount(manifest));
        string unchanged = Hash(manifest);
        byte[] renamed = (byte[])newMissing.Clone();
        byte[] renamedText = System.Text.Encoding.UTF8.GetBytes("New Missing"); Write(renamed, 160, (uint)renamedText.Length);
        Array.Clear(renamed, 164, 44); Array.Copy(renamedText, 0, renamed, 164, renamedText.Length); File.WriteAllBytes(table, renamed);
        DiscoveryTauntImport renamedImport = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("Refreshing rereads a renamed missing definition", "New Missing", renamedImport.Unavailable.Single().Name);
        Equal("UI-only missing name updates leave the playable manifest unchanged", unchanged, Hash(manifest));
        byte[] unsupportedMissing = Motion("60000a002", 30); Write(unsupportedMissing, 0x60, 1u); File.WriteAllBytes(raw, unsupportedMissing);
        DiscoveryTauntImport unsupportedImport = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("An unsupported new motion stays searchable with an explicit format reason", DiscoveryTauntUnavailableKind.UnsupportedFormat, unsupportedImport.Unavailable.Single().Kind);
        Equal("Unsupported UI-only motion also stays out of the engine manifest", 1, ManifestCount(manifest));
        File.WriteAllBytes(raw, Motion("60000a002", 30));
        DiscoveryTauntImport nowPlayable = DiscoveryTauntLibrary.Build(app, storage, data);
        Equal("Adding the actual missing motion removes its unavailable entry", 0, nowPlayable.Unavailable.Count);
        Equal("A newly available motion becomes a real second renderable entry", 2, ManifestCount(manifest));
    }

    private static int ManifestCount(string path)
    {
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
            return document.RootElement.GetProperty("taunts").GetArrayLength();
    }

    private static void SearchTests()
    {
        string text = DiscoveryTauntSearch.BuildText("Undisputed 2", new[] { "Some alias" }, new[] { "knee" }, "62541a000");
        foreach (string query in new[] { "undisputed 2", "Undisputed2", "UNDISPUTED-2", "undisputed_2", "62541", "62541a000", "some alias", "knee" })
            Equal("Search supports name, separators and exact IDs: " + query, true, DiscoveryTauntSearch.Matches(text, query));
        Equal("Numeric variant 2 does not match taunt 12", false, DiscoveryTauntSearch.Matches(DiscoveryTauntSearch.BuildText("Undisputed 12", null, null, "62551a000"), "undisputed2"));
        Equal("A numeric ID substring does not become a different move", false, DiscoveryTauntSearch.Matches(text, "2541"));
        Equal("Confidences finds the exact Confidence family", true, DiscoveryTauntSearch.Matches(DiscoveryTauntSearch.BuildText("Confidence 1", null, null, "37367a000"), "confidences"));
        Equal("Fight spirits finds Fighting Spirit", true, DiscoveryTauntSearch.Matches(DiscoveryTauntSearch.BuildText("Fighting Spirit 2", null, null, "37248a000"), "fight spirits"));
        Equal("Existing alpha substring searching is retained", true, DiscoveryTauntSearch.Matches(DiscoveryTauntSearch.BuildText("Angelo Dawkins 1", null, null, "60001a000"), "kin"));
        Equal("Jeff Hardy taunts can be searched as a family", true, DiscoveryTauntSearch.Matches(DiscoveryTauntSearch.BuildText("Jeff Hardy 1", null, null, "62247a000"), "Jeff Hardy taunts"));
        Equal("An empty query keeps every row", true, DiscoveryTauntSearch.Matches(text, "  "));
        Equal("Unavailable identity is recognized before any engine process", true, DiscoveryTauntSearch.IsUnavailableKey("discovery_unavailable_62247a001"));
        Equal("A real playable Discovery key is never disabled", false, DiscoveryTauntSearch.IsUnavailableKey("discovery62541a000_abcd"));
        Equal("The unavailable prefix requires its reserved separator", false, DiscoveryTauntSearch.IsUnavailableKey("discovery_unavailable"));
        Equal("A missing key is safe", false, DiscoveryTauntSearch.IsUnavailableKey(null));
    }

    private static string ManifestKey(string path)
    {
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
            return document.RootElement.GetProperty("taunts")[0].GetProperty("key").GetString();
    }

    private static byte[] CamTable()
    {
        var data = new byte[216];
        Write(data, 0, 32u); Array.Copy(System.Text.Encoding.ASCII.GetBytes("CMov"), 0, data, 4, 4);
        Short(data, 16, 6); Short(data, 18, 8); Short(data, 20, 4);
        Write(data, 32, 16u); Write(data, 36, 12u); Write(data, 48, 1u); Write(data, 52, 76u);
        Short(data, 64, 38); Short(data, 66, 24);
        Short(data, 68, 4); Short(data, 70, 8); Short(data, 72, 12); Short(data, 76, 16); Short(data, 100, 20);
        Write(data, 128, 64u); Write(data, 132, 28u); Write(data, 136, 62541u);
        Write(data, 140, 0u); Write(data, 144, 12345u); Write(data, 148, 60u);
        byte[] name = System.Text.Encoding.UTF8.GetBytes("Undisputed 2");
        Write(data, 160, (uint)name.Length); Array.Copy(name, 0, data, 164, name.Length);
        Write(data, 208, 1u); Write(data, 212, 4u);
        return data;
    }
    private static void Short(byte[] data, int offset, ushort value) { Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 2); }

    private static void CaptureTests(string root)
    {
        string overlay = Directory.CreateDirectory(Path.Combine(root, "overlay")).FullName;
        string previews = Directory.CreateDirectory(Path.Combine(overlay, "data", "library", "previews_game")).FullName;
        string pose = Path.Combine(root, "pose.png"), play = Path.Combine(root, "play.png");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ1kAAAAASUVORK5CYII=");
        File.WriteAllBytes(pose, png);
        File.WriteAllBytes(play, png.Concat(new byte[] { 1, 2, 3, 4 }).ToArray());
        string key = "discovery62541_abcdef";
        string[] args = { "folder", key, "output", "quick=1" };
        Func<double, int, string, string[]> lines = (fps, frames, gmr) => new[] { "RESULT " + JsonSerializer.Serialize(new
        { frames, preview = pose, play, play_fps = fps, gmr }) };
        Equal("A completed new-taunt preview is reused without a render", key,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args, lines(15, 10, null)));
        string target = Path.Combine(previews, key + ".png");
        Equal("The animation browser reuses the completed 15-fps atlas", true, File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(play)));
        Equal("The captured thumbnail leaves its source image unchanged", true, File.ReadAllBytes(pose).SequenceEqual(png));
        Equal("A mismatched atlas speed falls back to its single pose", key,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args, lines(30, 10, null)));
        Equal("The fallback uses the actual single-pose image", true, File.ReadAllBytes(target).SequenceEqual(png));
        Equal("Full-render GMR results cannot become preview thumbnails", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args, lines(15, 10, "render.gmr")));
        Equal("A full MAKE request never writes a browser preview", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", key, "output" }, lines(15, 10, null)));
        Equal("Model preparation never writes a browser preview", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args.Concat(new[] { "prepare=model" }), lines(15, 10, null)));
        string ordinaryKey = "t17330", ordinaryTarget = Path.Combine(previews, ordinaryKey + ".png");
        string[] ordinaryArgs = { "folder", ordinaryKey, "output", "quick=1" };
        Equal("A missing ordinary playable taunt browser atlas is created from its completed preview", ordinaryKey,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", ordinaryArgs, lines(15, 10, null)));
        Equal("An ordinary taunt atlas receives the actual completed preview strip", true,
            File.ReadAllBytes(ordinaryTarget).SequenceEqual(File.ReadAllBytes(play)));
        string ordinaryHash = Hash(ordinaryTarget);
        DateTime ordinaryWrite = File.GetLastWriteTimeUtc(ordinaryTarget);
        byte[] differentStrip = png.Concat(new byte[] { 9, 8, 7, 6, 5 }).ToArray();
        File.WriteAllBytes(play, differentStrip);
        Equal("A second ordinary preview does not replace its existing browser atlas", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", ordinaryArgs, lines(15, 10, null)));
        Equal("The existing ordinary atlas keeps its original SHA-256 despite a different later strip", ordinaryHash, Hash(ordinaryTarget));
        Equal("Skipping an existing ordinary atlas preserves its write timestamp", ordinaryWrite, File.GetLastWriteTimeUtc(ordinaryTarget));
        Equal("A Discovery preview continues to refresh its existing browser atlas", key,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args, lines(15, 10, null)));
        Equal("A refreshed Discovery atlas contains the current preview strip", true,
            File.ReadAllBytes(target).SequenceEqual(differentStrip));
        string nativeAtlas = Path.Combine(previews, "original62543.png");
        File.WriteAllBytes(nativeAtlas, png.Concat(new byte[] { 42, 43 }).ToArray());
        string nativeAtlasHash = Hash(nativeAtlas);
        Equal("Original bundled taunt thumbnails remain untouched", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", "original62543", "output", "quick=1" }, lines(15, 10, null)));
        Equal("A real existing native atlas keeps its exact original SHA-256", nativeAtlasHash, Hash(nativeAtlas));
        Equal("A renamed original motion can store its versioned preview", "original_discovery_abcd",
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", "original_discovery_abcd", "output", "quick=1" }, lines(15, 10, null)));
        Equal("Invalid frame counts cannot create a misleading thumbnail", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args, lines(15, 0, null)));
        Equal("Broken result JSON is ignored safely", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", args, new[] { "RESULT {" }));
        Equal("An unrelated engine script cannot write thumbnails", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "roster_lookup.py", args, lines(15, 10, null)));
        Equal("Preview keys cannot escape their dedicated directory", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", "discovery_../../escape", "output", "quick=1" }, lines(15, 10, null)));
        foreach (string unsafeKey in new[] { "t17330/../../escape", @"t17330\escape", "t17330.png", "t17330 with spaces", "t17330\n", "", ".." })
            Equal("Ordinary playable keys reject unsafe filename syntax: " + unsafeKey.Replace("\n", "<newline>"), null,
                DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", unsafeKey, "output", "quick=1" }, lines(15, 10, null)));
        Equal("A UI-only unavailable taunt identity cannot acquire a playable browser atlas", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", "discovery_unavailable_17330a000", "output", "quick=1" }, lines(15, 10, null)));

        string linkedOverlay = Directory.CreateDirectory(Path.Combine(root, "linked-overlay", "data", "library")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(linkedOverlay, "previews_game"), previews);
        Equal("The capture cannot write through an original-library directory link", null,
            DiscoveryTauntPreviewCapture.Capture(Path.Combine(root, "linked-overlay"), "gorilla_render.py", args, lines(15, 10, null)));
        Equal("An ordinary missing browser atlas cannot be created through a linked previews directory", null,
            DiscoveryTauntPreviewCapture.Capture(Path.Combine(root, "linked-overlay"), "gorilla_render.py", new[] { "folder", "t99998", "output", "quick=1" }, lines(15, 10, null)));
        Equal("Rejected linked-directory capture leaves no ordinary atlas in the original library", false, File.Exists(Path.Combine(previews, "t99998.png")));
    }

    private static void Actual(string application, string discovery, string storage)
    {
        string manifestPath = Path.Combine(application, "data", "library", "taunts.json");
        string table = DiscoveryTaunts.FindTable(discovery);
        string container = Path.Combine(discovery, "Animation", "Acts", "Basic", "MotionArea_62300_63101.clips");
        string originalPng = Directory.GetFiles(Path.Combine(application, "data", "library", "previews_game"), "*.png").First();
        var originals = new[] { manifestPath, table, container, originalPng };
        var before = originals.ToDictionary(path => path, Hash);
        var watch = Stopwatch.StartNew();
        DiscoveryTauntImport first = DiscoveryTauntLibrary.Build(application, storage, discovery);
        watch.Stop();
        long coldMs = watch.ElapsedMilliseconds;
        Equal("The actual Discovery CAM table supplies taunt definitions", true, first.Definitions > 1000);
        Equal("Actual missing taunts are imported from their raw motion assets", true, first.Imported > 0);
        Equal("Imported taunts extend the original renderable library", true, first.Available > 894);
        Equal("All 498 actual unavailable definitions remain searchable", 498, first.Unavailable.Count);
        Equal("Missing and unsupported formats remain distinct", 455, first.Unavailable.Count(item => item.Kind == DiscoveryTauntUnavailableKind.MissingAnimation));
        Equal("Unsupported definitions with no bundled fallback stay unavailable", 43, first.Unavailable.Count(item => item.Kind == DiscoveryTauntUnavailableKind.UnsupportedFormat));
        Equal("The uploaded full data folder can be scanned completely", false, first.ScanIncomplete);
        Equal("Actual source clips have no unreported read failures", 0, first.ReadFailures);
        Equal("Actual imported clips have no preview-cache failures", 0, first.CacheFailures);
        Equal("Every actual lookup miss reports its exact clip and scanned root without claiming the PC file is absent", true,
            first.Unavailable.Where(item => item.Kind == DiscoveryTauntUnavailableKind.MissingAnimation).All(item =>
                item.Reason.Contains(item.Clip) && item.Reason.Contains(Path.GetFullPath(discovery))
                && item.Reason.Contains("CAK archives were not searched") && !item.Reason.Contains("file is missing")));
        foreach (string clip in new[] { "37367a000", "37248a000" }.Concat(Enumerable.Range(1, 7).Select(variant => "62247a" + variant.ToString("000"))))
            Equal("The requested Confidence/Fighting Spirit/Jeff Hardy lookup miss stays searchable: " + clip,
                DiscoveryTauntUnavailableKind.MissingAnimation, first.Unavailable.Single(item => item.Clip == clip).Kind);
        string manifest = Path.Combine(first.Library, "taunts.json");
        string decoded = Path.Combine(first.Root, "data", "gamepack", "decoded");
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(manifest)))
        {
            JsonElement[] entries = json.RootElement.GetProperty("taunts").EnumerateArray().ToArray();
            List<DiscoveryTaunt> allDefinitions = DiscoveryTaunts.Read(table);
            Equal("Every one of the 1,584 valid Discovery variants has a playable or unavailable search row", true,
                allDefinitions.All(definition => entries.Any(entry => entry.GetProperty("clip").GetString() == definition.Clip)
                    || first.Unavailable.Any(item => item.Clip == definition.Clip)));
            Equal("Every Discovery name and alias remains searchable on its own exact variant", true,
                allDefinitions.All(definition => definition.Names.All(name =>
                    entries.Any(entry => entry.GetProperty("clip").GetString() == definition.Clip
                        && (entry.GetProperty("name").GetString() == name
                            || entry.TryGetProperty("also", out JsonElement aliases) && aliases.EnumerateArray().Any(alias => alias.GetString() == name)))
                    || first.Unavailable.Any(item => item.Clip == definition.Clip && item.Names.Contains(name)))));
            Equal("UI-only unavailable identities never enter the engine manifest", true, entries.All(entry => !DiscoveryTauntSearch.IsUnavailableKey(entry.GetProperty("key").GetString())));
            foreach (string name in new[] { "Confidence 2", "Fighting Spirit 1", "Fighting Spirit 3", "Fighting Spirit 4", "Jeff Hardy 1", "Chad Gable 1", "Chad Gable 2", "Chad Gable 3" })
                Equal("Available requested taunt remains a playable engine entry: " + name, true, entries.Any(entry => entry.GetProperty("name").GetString() == name));
            JsonElement jeff = entries.Single(entry => entry.GetProperty("name").GetString() == "Jeff Hardy 1");
            Equal("Jeff Hardy 1 cannot claim to play a separate missing Hardy variant", false,
                DiscoveryTauntSearch.Matches(DiscoveryTauntSearch.BuildText("Jeff Hardy 1", jeff.GetProperty("also").EnumerateArray().Select(item => item.GetString()), null, jeff.GetProperty("clip").GetString()), "Jeff Hardy 2"));
            JsonElement undisputed = entries.Single(entry => entry.GetProperty("clip").GetString() == "62541a000");
            Equal("Undisputed 2 is listed by its actual Discovery name", "Undisputed 2", undisputed.GetProperty("name").GetString());
            Equal("Undisputed 2 uses its own versioned motion clip", true, undisputed.GetProperty("key").GetString().StartsWith("discovery62541a000_", StringComparison.Ordinal));
            Equal("Undisputed 2 reports its actual clip duration", 4.68, undisputed.GetProperty("seconds").GetDouble());
            Equal("Undisputed 2 has a decoded NPZ ready for the original engine", true, File.Exists(Path.Combine(decoded, "62541a000.npz")));
            Equal("Every listed taunt has its corresponding decoded motion", true,
                entries.All(entry => File.Exists(Path.Combine(decoded, entry.GetProperty("clip").GetString() + ".npz"))));
            var parka = Enumerable.Range(0, 3).Select(variant => entries.Single(entry =>
                entry.GetProperty("clip").GetString() == "62757a" + variant.ToString("000"))).ToArray();
            for (int variant = 0; variant < parka.Length; variant++)
            {
                string name = "LA Parka " + (variant + 1);
                Equal(name + " keeps its actual Discovery variant name", name, parka[variant].GetProperty("name").GetString());
                Equal(name + " has its own decoded motion", true,
                    File.Exists(Path.Combine(decoded, "62757a" + variant.ToString("000") + ".npz")));
                Equal(name + " does not advertise another variant as the same motion", true,
                    parka[variant].GetProperty("also").EnumerateArray().All(alias =>
                        !new[] { "LA Parka 1", "LA Parka 2", "LA Parka 3" }.Where(other => other != name).Contains(alias.GetString())));
            }
            Equal("LA Parka variants have three distinct renderer cache keys", 3,
                parka.Select(entry => entry.GetProperty("key").GetString()).Distinct().Count());
            Equal("LA Parka variant zero preserves the original native cache-key prefix", true,
                parka[0].GetProperty("key").GetString().StartsWith("t62757", StringComparison.Ordinal));
            Equal("New LA Parka variants identify their full animation clip in their keys", true,
                parka[1].GetProperty("key").GetString().StartsWith("discovery62757a001_", StringComparison.Ordinal) &&
                parka[2].GetProperty("key").GetString().StartsWith("discovery62757a002_", StringComparison.Ordinal));
            Equal("LA Parka variants decode to three different actual pose arrays", 3,
                Enumerable.Range(0, 3).Select(variant => PoseHash(Path.Combine(decoded,
                    "62757a" + variant.ToString("000") + ".npz"))).Distinct().Count());
            DiscoveryMotionAssets actualAssets = DiscoveryMotionAssets.Read(discovery);
            Equal("LA Parka variants come from three distinct uploaded motion records", 3,
                Enumerable.Range(0, 3).Select(variant => Convert.ToHexString(SHA256.HashData(actualAssets.Find(
                    "62757a" + variant.ToString("000")).Read()))).Distinct().Count());
        }
        string npzPath = Path.Combine(decoded, "62541a000.npz");
        string npzHash = Hash(npzPath);
        DateTime npzWrite = File.GetLastWriteTimeUtc(npzPath);
        DateTime manifestWrite = File.GetLastWriteTimeUtc(manifest);
        string[] cached = Directory.GetFiles(Path.Combine(storage, "motion-cache-v1"), "*.npz");
        var cacheWrites = cached.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        watch.Restart();
        DiscoveryTauntImport second = DiscoveryTauntLibrary.Build(application, storage, discovery);
        watch.Stop();
        Equal("Refreshing the same data keeps the same library", first.Available, second.Available);
        Equal("Refreshing keeps the same Undisputed 2 animation data", npzHash, Hash(npzPath));
        Equal("Refreshing unchanged motion does not rewrite the overlay NPZ", npzWrite, File.GetLastWriteTimeUtc(npzPath));
        Equal("Refreshing an unchanged library does not rewrite its manifest", manifestWrite, File.GetLastWriteTimeUtc(manifest));
        Equal("Refreshing unchanged motion reuses the decoded-motion cache", true,
            cacheWrites.All(item => File.GetLastWriteTimeUtc(item.Key) == item.Value));
        Equal("Original Discovery data and bundled library remain unchanged", true,
            before.All(item => Hash(item.Key) == item.Value));
        var report = new
        {
            first.Definitions, first.Imported, first.Available, first.Missing, first.Unsupported,
            first.ReadFailures, first.CacheFailures, first.ScanIncomplete, first.AnimationRootFound, first.SearchRoots,
            first.Warnings, UnavailableCount = first.Unavailable.Count,
            RequestedUnavailable = first.Unavailable.Where(item => item.Name.StartsWith("Confidence", StringComparison.Ordinal) || item.Name.StartsWith("Fighting Spirit", StringComparison.Ordinal) || item.Name.StartsWith("Jeff Hardy", StringComparison.Ordinal))
                .Select(item => new { item.Id, item.Variant, item.Name, item.Clip, Kind = item.Kind.ToString(), item.Reason, item.Key }),
            ColdBuildMilliseconds = coldMs, CachedBuildMilliseconds = watch.ElapsedMilliseconds,
            OriginalFilesUnchanged = true, Undisputed2Decoded = true,
            Undisputed2Npz = npzPath, Manifest = manifest, ChecksPassed = passed,
            WindowsGuiExecuted = false, BlenderRenderCompleted = false
        };
        string output = Path.Combine(storage, "validation.json");
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(output));
    }

    private static byte[] Motion(string name, float fps)
    {
        var data = new byte[0xA0 + 8 + 48];
        Write(data, 0, DiscoveryMotionAssets.ClipId(name)); Write(data, 0x20, 1u); Write(data, 0x24, 1u);
        Write(data, 0x40, 24u); Write(data, 0x44, 3u); Write(data, 0x78, 6u); Write(data, 0x7C, 6u);
        Array.Copy(BitConverter.GetBytes(fps), 0, data, 0x0C, 4);
        Write(data, 0xA0, 12345678901234567890UL);
        return data;
    }
    private static void Container(string path, byte[] record)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(0UL); writer.Write(0UL); writer.Write(1u); writer.Write(1u);
            writer.Write(BitConverter.ToUInt64(record, 0)); writer.Write((uint)record.Length); writer.Write(40u); writer.Write(record);
        }
    }
    private static void Write(byte[] data, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 4); }
    private static void Write(byte[] data, int offset, ulong value) { Array.Copy(BitConverter.GetBytes(value), 0, data, offset, 8); }
    private static string Hash(string path) { using (var file = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(file)); }
    private static string PoseHash(string path)
    {
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("P.npy").Open()) return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
}
