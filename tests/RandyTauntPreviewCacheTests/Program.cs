using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static string root, app, overlay, model, cache, good;
    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-randy-atlas-" + Guid.NewGuid().ToString("N"));
        try
        {
            app = Path.Combine(root, "app"); overlay = Path.Combine(root, "Discovery"); model = Path.Combine(root, "shared-randy"); cache = Path.Combine(root, "cache");
            foreach (string target in new[] { app, overlay })
            {
                Directory.CreateDirectory(Path.Combine(target, "data/library/previews_game"));
                Directory.CreateDirectory(Path.Combine(target, "data/library/faces"));
                Directory.CreateDirectory(Path.Combine(target, "data/gamepack/decoded"));
                File.WriteAllBytes(Path.Combine(target, "data/gamepack/decoded/62247a000.npz"), Encoding.UTF8.GetBytes("exact body motion"));
                File.WriteAllBytes(Path.Combine(target, "data/library/faces/facial.npz"), Encoding.UTF8.GetBytes("exact face motion"));
                Manifest(target, Row("tgood"));
                File.WriteAllBytes(Path.Combine(target, "data/library/previews_game/tgood.png"), Png(2, 1));
            }
            Directory.CreateDirectory(Path.Combine(app, "engine")); File.WriteAllText(Path.Combine(app, "engine/gr_render.py"), "renderer v1");
            Directory.CreateDirectory(Path.Combine(model, "BaseModel")); Directory.CreateDirectory(Path.Combine(model, "161_Default_Attire"));
            File.WriteAllText(Path.Combine(model, "BaseModel/baseModel.mcd"), "Randy base");
            File.WriteAllText(Path.Combine(model, "161_Default_Attire/161_Attire.mcd"), "Randy clothes");
            good = Path.Combine(app, "data/library/previews_game/tgood.png");
            Bundled(); AtlasValidation(); Private(); FreshCapturedRead(); NativeResult(); FacialCache();
            if (args.Length > 0) Actual(args[0]);
            Console.WriteLine("PASS " + checks + " Randy taunt preview cache checks");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Bundled()
    {
        string path;
        Check(RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "Proven stock Randy atlas is immediately reusable");
        Check(path == good, "An overlay atlas captured on another wrestler never replaces original stock Randy");
        var row = Row("tgood"); row["name"] = "Changed display label"; Manifest(overlay, row);
        Check(RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "A display-only name edit preserves the same verified motion preview");
        row["cut"] = new object[] { 1, 2 }; Manifest(overlay, row);
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "A changed animation segment cannot reuse its old stock atlas");
        Manifest(overlay, Row("tgood"));
        string motion = Path.Combine(overlay, "data/gamepack/decoded/62247a000.npz"); DateTime stamp = File.GetLastWriteTimeUtc(motion);
        File.WriteAllText(motion, "changed body data"); File.SetLastWriteTimeUtc(motion, stamp);
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "Actual motion bytes are checked despite preserved timestamps");
        File.WriteAllText(motion, "exact body motion");
        string face = Path.Combine(overlay, "data/library/faces/facial.npz"); File.WriteAllText(face, "changed face");
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "Changed facial motion cannot reuse a stock preview"); File.WriteAllText(face, "exact face motion");
        row = Row("tgood"); row["face_offset"] = 12; Manifest(overlay, row);
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "Facial timing remains part of motion identity"); Manifest(overlay, Row("tgood"));
        row = Row("tgood"); row["tested_on"] = new[] { "368 Roman Reigns" }; Manifest(app, row);
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "A known other-wrestler atlas is never reused as Randy");
        foreach (string key in new[] { "roman3", "straightedge4", "cena2", "cody3", "wetheone1" })
        {
            Manifest(app, Row(key)); Manifest(overlay, Row(key));
            File.WriteAllBytes(Path.Combine(app, "data/library/previews_game", key + ".png"), Png(2, 1));
            Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, key, out path), "Ambiguous bundled actor remains excluded: " + key);
        }
        Manifest(app, Row("tgood")); Manifest(overlay, Row("tgood"), Row("tgood"));
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "tgood", out path), "Duplicate animation keys do not select an arbitrary record");
        Manifest(overlay, Row("tgood"));
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "../tgood", out path), "An animation key cannot escape its atlas folder");
    }
    private static void AtlasValidation()
    {
        Check(RandyTauntPreviewCache.ValidAtlas(good), "A valid horizontal PNG passes full chunk CRC checks");
        foreach (var fixture in new[] { Tuple.Create("vertical", Png(1, 2)), Tuple.Create("uneven", Png(3, 2)), Tuple.Create("short", new byte[12]) })
        { string p = Path.Combine(root, fixture.Item1 + ".png"); File.WriteAllBytes(p, fixture.Item2); Check(!RandyTauntPreviewCache.ValidAtlas(p), "Invalid strip rejected: " + fixture.Item1); }
        byte[] bad = Png(2, 1); bad[29] ^= 1;
        string broken = Path.Combine(root, "bad-crc.png"); File.WriteAllBytes(broken, bad);
        Check(!RandyTauntPreviewCache.ValidAtlas(broken), "A damaged PNG chunk is rejected before caching");
        File.WriteAllBytes(broken, Png(2, 1).Concat(new byte[] { 1 }).ToArray());
        Check(!RandyTauntPreviewCache.ValidAtlas(broken), "Trailing data after the PNG terminator is rejected");
    }
    private static void Private()
    {
        var context = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, "blender=5.2;game=A");
        Check(context.Fingerprint.Length == 64 && context.IsCurrent(), "Only current selected-motion/Randy inputs produce an immutable cache identity");
        string path, before = Hash(good);
        Check(RandyTauntPreviewCache.Publish(cache, context, good, out path), "A successful dedicated Randy atlas is published");
        Check(path != good && path.StartsWith(cache, StringComparison.Ordinal), "Published atlases are immutable private copies");
        Check(Hash(good) == before, "Publishing never changes a bundled source image");
        Check(RandyTauntPreviewCache.TryRead(cache, context, out path), "The exact same Randy context reuses its completed image");
        string refresh = Path.Combine(root, "refreshed-overlay");
        foreach (string original in Directory.GetFiles(overlay, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(refresh, Path.GetRelativePath(overlay, original));
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(original, target);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(original).AddMinutes(2));
        }
        var refreshed = RandyTauntPreviewCache.CaptureContext(app, refresh, "tgood", model, "blender=5.2;game=A");
        Check(refreshed.Fingerprint == context.Fingerprint, "An unchanged Discovery refresh reuses its motion identity despite new paths and timestamps");
        Check(RandyTauntPreviewCache.TryRead(cache, refreshed, out path), "A generated Randy preview survives an unchanged Discovery refresh without rendering again");
        string changedMotion = Path.Combine(refresh, "data/gamepack/decoded/62247a000.npz");
        File.WriteAllText(changedMotion, "a genuinely different animation");
        Check(!refreshed.IsCurrent() && !RandyTauntPreviewCache.TryRead(cache, refreshed, out path), "A real motion change after refresh still rejects the saved preview");
        Check(context.IsCurrent(), "Changing another refresh does not invalidate this context's unchanged source");
        string modelPath = Path.Combine(model, "BaseModel/baseModel.mcd"); DateTime stamp = File.GetLastWriteTimeUtc(modelPath);
        File.WriteAllText(modelPath, "Other base"); File.SetLastWriteTimeUtc(modelPath, stamp);
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.TryRead(cache, context, out path), "Changed base-model bytes invalidate a saved preview even with retained mtime");
        File.WriteAllText(modelPath, "Randy base");
        context = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, "new context");
        var other = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, "different game or Blender");
        Check(context.Fingerprint != other.Fingerprint, "Blender/game/producer settings isolate preview identities");
        string config = Path.Combine(root, "blender.cfg"); File.WriteAllText(config, "a");
        var extra = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, null, new[] { config }); File.WriteAllText(config, "b");
        Check(!extra.IsCurrent(), "Extra producer inputs participate in staleness checks");
        string shared = Path.Combine(app, "data/gamepack/stock/Characters/_Global/Textures/eye.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)); File.WriteAllText(shared, "shared eye pixels");
        var sharedContext = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model);
        File.WriteAllText(shared, "new shared eye pixels");
        Check(!sharedContext.IsCurrent(), "Shared eye and teeth resources participate without scanning other wrestlers");
        context = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, "new context");
        Check(RandyTauntPreviewCache.Publish(cache, context, good, out path), "Another current context publishes independently");
        string published = path; byte[] altered = File.ReadAllBytes(published); altered[altered.Length - 1] ^= 1; File.WriteAllBytes(published, altered);
        Check(!RandyTauntPreviewCache.TryRead(cache, context, out path), "Corrupted private atlas data never appears as a cached success");
        Check(RandyTauntPreviewCache.Publish(cache, context, good, out path), "A fresh Randy render repairs a corrupted cache entry");
        Check(Directory.GetDirectories(cache, ".randy-corrupt-*").Length == 1, "Corrupted prior bytes remain preserved separately for diagnosis");
        var missing = Row("tgood"); missing["clip"] = "missing"; Manifest(overlay, missing);
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.Publish(cache, context, good, out path), "A stale source context cannot publish after an animation change");
        Manifest(overlay, Row("tgood"));
        context = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, "manifest tamper");
        Check(RandyTauntPreviewCache.Publish(cache, context, good, out path), "Current context remains publishable after an unrelated old entry is damaged");
        string manifest = Path.Combine(Path.GetDirectoryName(path), "manifest.json"); var decoded = PreviewJson.Read(File.ReadAllText(manifest)); decoded["model"] = "Adam Cole";
        File.WriteAllText(manifest, PreviewJson.Write(decoded));
        Check(!RandyTauntPreviewCache.TryRead(cache, context, out path), "A saved image for another actor cannot masquerade as the dedicated Randy cache");
    }
    private static void FreshCapturedRead()
    {
        string storage = Path.Combine(root, "fresh-captured-cache"), path;
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, null, out path) && path == null,
            "A null freshly captured context cannot return a cache image");
        var context = RandyTauntPreviewCache.CaptureContext(app, overlay, "tgood", model, "fresh captured lookup");
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
            "A fresh-context cache miss returns no image");
        Check(RandyTauntPreviewCache.Publish(storage, context, good, out path),
            "The captured-lookup fixture publishes a validated private atlas");
        string published = path, manifest = Path.Combine(Path.GetDirectoryName(path), "manifest.json");
        Check(RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == published,
            "An immediately captured context reads the same validated private atlas");
        Check(RandyTauntPreviewCache.TryRead(storage, context, out path) && path == published,
            "The public guarded lookup retains the same successful behavior");

        string body = Path.Combine(model, "BaseModel/baseModel.mcd");
        byte[] originalBody = File.ReadAllBytes(body); DateTime bodyTime = File.GetLastWriteTimeUtc(body);
        byte[] changedBody = (byte[])originalBody.Clone(); changedBody[0] ^= 1;
        File.WriteAllBytes(body, changedBody); File.SetLastWriteTimeUtc(body, bodyTime);
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
            "Final source validation rejects an already-stale captured context despite preserved size and mtime");
        Check(!RandyTauntPreviewCache.TryRead(storage, context, out path) && path == null,
            "The public double-guard path continues to reject the same stale source");
        File.WriteAllBytes(body, originalBody); File.SetLastWriteTimeUtc(body, bodyTime);
        Check(RandyTauntPreviewCache.TryReadCaptured(storage, context, out path),
            "Restoring the exact source makes its original captured identity current again");

        byte[] originalAtlas = File.ReadAllBytes(published);
        byte[] corrupted = (byte[])originalAtlas.Clone(); corrupted[corrupted.Length - 1] ^= 1;
        File.WriteAllBytes(published, corrupted);
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
            "Captured lookup still rejects a malformed private PNG CRC");
        File.WriteAllBytes(published, Png(3, 1));
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
            "Captured lookup rejects a valid PNG whose bytes do not match its saved manifest");
        File.WriteAllBytes(published, originalAtlas);

        string originalManifest = File.ReadAllText(manifest);
        var changedManifest = PreviewJson.Read(originalManifest); changedManifest["model"] = "Adam Cole";
        File.WriteAllText(manifest, PreviewJson.Write(changedManifest));
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
            "Captured lookup cannot reuse another actor's saved image");
        File.WriteAllText(manifest, "{ malformed manifest");
        Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
            "Captured lookup rejects malformed cache metadata");
        File.WriteAllText(manifest, originalManifest);

        string moved = Path.Combine(root, "captured-linked-atlas.png");
        File.Move(published, moved);
        try
        {
            File.CreateSymbolicLink(published, moved);
            Check(!RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == null,
                "Captured lookup preserves the private-atlas link guard");
        }
        finally
        {
            if (File.Exists(published)) File.Delete(published);
            File.Move(moved, published);
        }
        Check(RandyTauntPreviewCache.TryReadCaptured(storage, context, out path) && path == published,
            "All captured-lookup validation cases preserve the recoverable original cache");
    }
    private static void NativeResult()
    {
        string path;
        var result = new Dictionary<string, object> { { "id", 161 }, { "play", good }, { "play_fps", 15 }, { "frames", 30 } };
        Func<string[]> lines = () => new[] { "STAGE 9/9 Ready", "RESULT " + PreviewJson.Write(result) };
        Check(RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path) && path == good, "A completed native 15-fps playback strip is accepted");
        result["id"] = 695; Check(!RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path), "A selected mod wrestler's result cannot populate Randy's preview cache"); result["id"] = 161;
        result["id"] = 160.6; Check(!RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path), "A fractional result target cannot round into Randy's identity"); result["id"] = 161;
        result["play_fps"] = 30; Check(!RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path), "A different frame rate is not mislabeled as 15fps"); result["play_fps"] = 15;
        result["gmr"] = "someone.gmr"; Check(!RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path), "A full wrestler render cannot populate the Randy preview cache"); result.Remove("gmr");
        result["frames"] = 0; Check(!RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path), "No-frame results cannot publish a preview"); result["frames"] = 30;
        result.Remove("play"); result["preview"] = good; Check(!RandyTauntPreviewCache.TryGetPlayAtlas(lines(), out path), "A contact sheet is never substituted for a timed playback atlas");
    }
    private static void FacialCache()
    {
        string faceApp = Path.Combine(root, "facial-app"), faceOverlay = Path.Combine(root, "facial-overlay");
        string faceCache = Path.Combine(root, "facial-private-cache"), path;
        byte[] originalMale = FacialNpz(0.25f), editedMale = FacialNpz(0.5f);
        foreach (string target in new[] { faceApp, faceOverlay })
        {
            foreach (string directory in new[] { "data/library/previews_game", "data/library/faces", "data/gamepack/decoded", "data/faces/male", "data/faces/female" })
                Directory.CreateDirectory(Path.Combine(target, directory));
            File.WriteAllBytes(Path.Combine(target, "data/gamepack/decoded/62247a000.npz"), Encoding.UTF8.GetBytes("exact body motion"));
            File.WriteAllBytes(Path.Combine(target, "data/library/faces/facial_6368.npz"), FacialNpz(0));
            File.WriteAllBytes(Path.Combine(target, "data/library/previews_game/tface.png"), Png(2, 1));
            File.WriteAllBytes(Path.Combine(target, "data/faces/male/tface.npz"), originalMale);
            File.WriteAllBytes(Path.Combine(target, "data/faces/female/tface.npz"), FacialNpz(0.75f));
            var row = Row("tface"); row["face"] = "facial_6368.npz"; Manifest(target, row);
            FacialIndex(target, FacialRecord());
        }
        string atlas = Path.Combine(faceApp, "data/library/previews_game/tface.png");
        string male = Path.Combine(faceOverlay, "data/faces/male/tface.npz");
        string female = Path.Combine(faceOverlay, "data/faces/female/tface.npz");
        string index = Path.Combine(faceOverlay, "data/faces/index.json");
        Check(RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path) && path == atlas,
            "Unchanged per-taunt male facial data preserves the instant stock Randy atlas");
        var context = RandyTauntPreviewCache.CaptureContext(faceApp, faceOverlay, "tface", model, "indexed facial case");
        Check(RandyTauntPreviewCache.Publish(faceCache, context, atlas, out path)
            && RandyTauntPreviewCache.TryRead(faceCache, context, out path),
            "A completed Randy preview records its indexed male facial source");
        string refreshedFaces = Path.Combine(root, "facial-refreshed-overlay");
        foreach (string original in Directory.GetFiles(faceOverlay, "*", SearchOption.AllDirectories))
        {
            string copied = Path.Combine(refreshedFaces, Path.GetRelativePath(faceOverlay, original));
            Directory.CreateDirectory(Path.GetDirectoryName(copied)); File.Copy(original, copied);
            File.SetLastWriteTimeUtc(copied, File.GetLastWriteTimeUtc(original).AddMinutes(5));
        }
        var refreshedContext = RandyTauntPreviewCache.CaptureContext(faceApp, refreshedFaces, "tface", model, "indexed facial case");
        Check(refreshedContext.Fingerprint == context.Fingerprint && RandyTauntPreviewCache.TryRead(faceCache, refreshedContext, out path),
            "An unchanged indexed facial source survives fresh overlay paths and copied timestamps");

        File.WriteAllBytes(female, FacialNpz(1));
        Check(context.IsCurrent() && RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "Female facial bytes do not invalidate a male Randy preview");
        var femaleAlias = FacialRecord(); femaleAlias["female"] = "female/other.npz";
        File.WriteAllBytes(Path.Combine(faceOverlay, "data/faces/female/other.npz"), FacialNpz(0.9f));
        FacialIndex(faceOverlay, femaleAlias);
        Check(context.IsCurrent() && RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "An unused female index alias does not invalidate male Randy playback");
        FacialIndex(faceOverlay, FacialRecord(), true);
        File.WriteAllText(index, "\n  " + File.ReadAllText(index).Replace(",", ",\n  ") + "\n");
        Check(context.IsCurrent() && RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "Unrelated facial index rows and JSON formatting do not invalidate this taunt");
        FacialIndex(faceOverlay, FacialRecord());

        DateTime stamp = File.GetLastWriteTimeUtc(male);
        Check(originalMale.Length == editedMale.Length, "The facial mutation fixture preserves its file size");
        File.WriteAllBytes(male, editedMale); File.SetLastWriteTimeUtc(male, stamp);
        Check(!RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "Changed male facial bytes reject a stock preview despite preserved size and mtime");
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, context, out path),
            "Changed male facial bytes invalidate a private Randy preview despite preserved size and mtime");
        File.WriteAllBytes(male, originalMale); File.SetLastWriteTimeUtc(male, stamp);
        Check(context.IsCurrent(), "Restoring the exact male facial source restores its original identity");

        var shifted = FacialRecord(); shifted["offset"] = 2; FacialIndex(faceOverlay, shifted);
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && !RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "A changed per-taunt facial offset invalidates both stock and private previews");
        FacialIndex(faceOverlay, FacialRecord());

        File.WriteAllText(index, "{}");
        var absent = RandyTauntPreviewCache.CaptureContext(faceApp, faceOverlay, "tface", model, "absent facial alias");
        Check(RandyTauntPreviewCache.Publish(faceCache, absent, atlas, out path)
            && RandyTauntPreviewCache.TryRead(faceCache, absent, out path),
            "A generated fallback preview records that its per-taunt facial alias is absent");
        Check(!RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "An absent indexed facial alias cannot reuse a stock atlas made with that facial motion");
        FacialIndex(faceOverlay, FacialRecord());
        Check(!absent.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, absent, out path),
            "Repairing an absent facial alias invalidates the older fallback preview");

        File.WriteAllText(index, "{ malformed index");
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && !RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "A malformed facial index falls back and cannot reuse an indexed facial preview");
        var malformed = RandyTauntPreviewCache.CaptureContext(faceApp, faceOverlay, "tface", model, "malformed facial alias");
        Check(RandyTauntPreviewCache.Publish(faceCache, malformed, atlas, out path),
            "A generated preview may record the native fallback for a malformed facial index");
        FacialIndex(faceOverlay, FacialRecord());
        Check(!malformed.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, malformed, out path),
            "Repairing a malformed index invalidates its previous fallback preview");

        File.WriteAllBytes(Path.Combine(faceOverlay, "data/escaped.npz"), originalMale);
        var escaped = FacialRecord(); escaped["male"] = "../escaped.npz"; FacialIndex(faceOverlay, escaped);
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && !RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "A facial path outside the face directory cannot reuse an indexed preview");
        var outOfBounds = FacialRecord(); outOfBounds["offset"] = 1000000000; FacialIndex(faceOverlay, outOfBounds);
        Check(!context.IsCurrent() && !RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && !RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "An out-of-bounds facial offset falls back and cannot reuse indexed playback");
        FacialIndex(faceOverlay, FacialRecord());
        Check(context.IsCurrent() && RandyTauntPreviewCache.TryRead(faceCache, context, out path)
            && RandyTauntPreviewCache.TryGetBundled(faceApp, faceOverlay, "tface", out path),
            "Restoring the valid index restores the original stock and private preview identities");
    }

    private static Dictionary<string, object> FacialRecord()
    { return new Dictionary<string, object> { { "male", "male/tface.npz" }, { "female", "female/tface.npz" }, { "offset", 0 }, { "events", 3 } }; }
    private static void FacialIndex(string target, Dictionary<string, object> row, bool unrelated = false)
    {
        var document = new Dictionary<string, object> { { "tface", row } };
        if (unrelated)
        {
            document["another_taunt"] = new Dictionary<string, object> { { "male", "male/another.npz" }, { "female", "female/another.npz" }, { "offset", 1 }, { "events", 8 } };
            File.WriteAllBytes(Path.Combine(target, "data/faces/male/another.npz"), FacialNpz(0.1f));
            File.WriteAllBytes(Path.Combine(target, "data/faces/female/another.npz"), FacialNpz(0.2f));
        }
        File.WriteAllText(Path.Combine(target, "data/faces/index.json"), PreviewJson.Write(document));
    }
    private static byte[] FacialNpz(float value)
    {
        using (var output = new MemoryStream())
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                FacialArray(archive, "ids", "<u4", "(1,)", BitConverter.GetBytes((uint)123));
                FacialArray(archive, "values", "<f4", "(1, 4)", new[] { value, 0f, 0f, value }.SelectMany(BitConverter.GetBytes).ToArray());
            }
            return output.ToArray();
        }
    }
    private static void FacialArray(ZipArchive archive, string name, string type, string shape, byte[] values)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name + ".npy", CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string descriptor = "{'descr': '" + type + "', 'fortran_order': False, 'shape': " + shape + ", }";
        byte[] header = Encoding.ASCII.GetBytes(descriptor + new string(' ', (64 - ((10 + descriptor.Length + 1) % 64)) % 64) + "\n");
        using (var writer = new BinaryWriter(entry.Open(), Encoding.ASCII))
        { writer.Write(new byte[] { 0x93, 78, 85, 77, 80, 89, 1, 0 }); writer.Write((ushort)header.Length); writer.Write(header); writer.Write(values); }
    }
    private static void Actual(string actual)
    {
        string path;
        Check(RandyTauntPreviewCache.TryGetBundled(actual, actual, "t62247", out path), "Actual shipped Jeff Hardy 1 atlas is proven Randy and immediately reusable");
        Check(RandyTauntPreviewCache.TryGetBundled(actual, actual, "orton3", out path), "Actual shipped Randy Orton 3 atlas is verified");
        foreach (string key in new[] { "roman3", "straightedge4", "cena2", "cody3", "wetheone1" })
            Check(!RandyTauntPreviewCache.TryGetBundled(actual, actual, key, out path), "Actual non-Randy or ambiguous atlas rejected: " + key);
    }
    private static Dictionary<string, object> Row(string key) { return new Dictionary<string, object> { { "key", key }, { "name", "Taunt" }, { "clip", "62247a000" }, { "cut", null }, { "blend", 0.6 }, { "face", "facial.npz" }, { "face_offset", 0 }, { "tested_on", new[] { "preview on Randy Orton" } } }; }
    private static void Manifest(string target, params Dictionary<string, object>[] rows) { File.WriteAllText(Path.Combine(target, "data/library/taunts.json"), PreviewJson.Write(new Dictionary<string, object> { { "taunts", rows } })); }
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(stream)); }
    private static byte[] Png(int width, int height)
    {
        using (var output = new MemoryStream())
        {
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var header = new byte[13]; Put(header, 0, (uint)width); Put(header, 4, (uint)height); header[8] = 8; header[9] = 6; Chunk(output, "IHDR", header);
            using (var compressed = new MemoryStream()) { using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) z.Write(new byte[height * (1 + width * 4)]); Chunk(output, "IDAT", compressed.ToArray()); }
            Chunk(output, "IEND", new byte[0]); return output.ToArray();
        }
    }
    private static void Chunk(Stream output, string name, byte[] bytes) { var count = new byte[4]; Put(count, 0, (uint)bytes.Length); output.Write(count); byte[] type = Encoding.ASCII.GetBytes(name); output.Write(type); output.Write(bytes); uint crc = 0xFFFFFFFF; foreach (byte value in type.Concat(bytes)) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1; } Put(count, 0, crc ^ 0xFFFFFFFF); output.Write(count); }
    private static void Put(byte[] target, int offset, uint value) { for (int index = 0; index < 4; index++) target[offset + index] = (byte)(value >> (24 - index * 8)); }
}
