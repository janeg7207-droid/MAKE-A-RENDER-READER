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
        internal static string Resolve(string path)
        {
            var directory = new DirectoryInfo(path);
            FileSystemInfo target = directory.ResolveLinkTarget(true);
            return target == null ? directory.FullName : target.FullName;
        }
        internal static void EnsureJunction(string link, string target)
        {
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
        SearchTests();
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
        Equal("Original bundled taunt thumbnails remain untouched", null,
            DiscoveryTauntPreviewCapture.Capture(overlay, "gorilla_render.py", new[] { "folder", "original62543", "output", "quick=1" }, lines(15, 10, null)));
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

        string linkedOverlay = Directory.CreateDirectory(Path.Combine(root, "linked-overlay", "data", "library")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(linkedOverlay, "previews_game"), previews);
        Equal("The capture cannot write through an original-library directory link", null,
            DiscoveryTauntPreviewCapture.Capture(Path.Combine(root, "linked-overlay"), "gorilla_render.py", args, lines(15, 10, null)));
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
        foreach (string clip in new[] { "37367a000", "37248a000" }.Concat(Enumerable.Range(1, 7).Select(variant => "62247a" + variant.ToString("000"))))
            Equal("The requested absent Confidence/Fighting Spirit/Jeff Hardy move is searchable with a missing-file reason: " + clip,
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
