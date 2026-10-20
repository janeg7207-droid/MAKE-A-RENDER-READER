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
            Bundled(); AtlasValidation(); Private(); NativeResult();
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
