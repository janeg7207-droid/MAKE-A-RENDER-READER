using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static string temporary;

    private static int Main()
    {
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-preview-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Classifier();
            Fingerprints();
            ImmutableArtifacts();
            CorruptionAndUnsafeResults();
            UnwritableStore();
            ConcurrentWritesAndPruning();
            Console.WriteLine(checks + " preview cache checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static void Classifier()
    {
        Equal(true, PreviewCache.IsPreviewRequest(@"C:\GorillaStudio\pipeline\gorilla_render.py", new[] { "folder=PAC", "quick=1" }));
        Equal(true, PreviewCache.IsPreviewRequest("/app/pipeline/gorilla_render.py", new[] { "quick=0", "quick=1" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new[] { "quick=1", "quick=0" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new[] { "quick=1", "prepare=taunt" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new[] { "prepare=model", "quick=1" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new[] { "quick=0" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new[] { "quick=10" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new string[0]));
        Equal(false, PreviewCache.IsPreviewRequest("other.py", new[] { "quick=1" }));
        Equal(false, PreviewCache.IsPreviewRequest(null, new[] { "quick=1" }));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", null));
        Equal(false, PreviewCache.IsPreviewRequest("gorilla_render.py", new[] { "quick=1", "prepare=0" }));
    }

    private static void Fingerprints()
    {
        string model = Write("model.mcd", new byte[] { 1, 2, 3 });
        string texture = Write("face.dds", new byte[] { 4, 5, 6 });
        string[] args = { "folder=PAC", "taunt=CruiserKing3", "hair=auto", "quick=1" };
        string[] files = { model, texture };
        string original = PreviewCache.Key(args, true, files);
        Equal(64, original.Length);
        Equal(original, PreviewCache.Key(args, true, files.Reverse()));
        Equal(original, PreviewCache.Key(args, true, new[] { model, model, texture }));
        Different(original, PreviewCache.Key(args, false, files));
        Different(original, PreviewCache.Key(args, true, files, "renderer-v2"));
        Different(original, PreviewCache.Key(new[] { "folder=PAC", "taunt=CruiserKing3", "hair=black", "quick=1" }, true, files));
        Different(original, PreviewCache.Key(args.Reverse(), true, files));
        DateTime stamp = File.GetLastWriteTimeUtc(texture);
        File.SetLastWriteTimeUtc(texture, stamp.AddSeconds(3));
        Different(original, PreviewCache.Key(args, true, files));
        File.SetLastWriteTimeUtc(texture, stamp);
        Equal(original, PreviewCache.Key(args, true, files));
        File.AppendAllText(model, "new mesh");
        Different(original, PreviewCache.Key(args, true, files));
        File.Delete(texture);
        Equal((string)null, PreviewCache.Key(args, true, files));
        Equal((string)null, PreviewCache.Key(args, true, new[] { temporary }));
        Equal((string)null, PreviewCache.Key(args, true, null));
        Equal((string)null, PreviewCache.Key(null, true, new string[0]));
        string another = Write("added-model.mcd", new byte[] { 9 });
        Different(PreviewCache.Key(args, true, new[] { model }), PreviewCache.Key(args, true, new[] { model, another }));
    }

    private static void ImmutableArtifacts()
    {
        var store = new PreviewCacheStore(Path.Combine(temporary, "cache"));
        string preview = Write("shared-preview.png", Png(255, 0, 0));
        string play = Write("shared-play.png", Png(0, 255, 0));
        string game = Write("shared-game.png", Png(0, 0, 255));
        string first = Key("hair=red");
        Equal(true, store.Save(first, Result(preview, play, game)));
        List<string> lines;
        Equal(true, store.TryRead(first, out lines));
        Equal("STAGE 9/9 Cached preview", lines[0]);
        Dictionary<string, JsonElement> a = Decode(lines);
        string firstPreview = a["preview"].GetString();
        string firstPlay = a["play"].GetString();
        string firstGame = a["game_picture"].GetString();
        Equal(true, firstPreview.StartsWith(Path.Combine(temporary, "cache", first), StringComparison.Ordinal));
        Bytes(Png(255, 0, 0), File.ReadAllBytes(firstPreview));
        Bytes(Png(0, 255, 0), File.ReadAllBytes(firstPlay));
        Bytes(Png(0, 0, 255), File.ReadAllBytes(firstGame));
        Equal("PAC", a["name"].GetString());
        Equal(924, a["id"].GetInt32());
        Equal("freeze", a["end"].GetString());
        Equal("123", a["linked"][0].GetString());

        // The engine reuses these original names for later hair/gear settings.
        File.WriteAllBytes(preview, Png(12, 34, 56));
        File.WriteAllBytes(play, Png(65, 43, 21));
        File.WriteAllBytes(game, Png(90, 80, 70));
        string second = Key("hair=black");
        Equal(true, store.Save(second, Result(preview, play, game)));
        Equal(true, store.TryRead(second, out lines));
        Dictionary<string, JsonElement> b = Decode(lines);
        Different(firstPreview, b["preview"].GetString());
        Bytes(Png(12, 34, 56), File.ReadAllBytes(b["preview"].GetString()));
        Equal(true, store.TryRead(first, out lines));
        Bytes(Png(255, 0, 0), File.ReadAllBytes(firstPreview));
        Bytes(Png(0, 255, 0), File.ReadAllBytes(firstPlay));
        Bytes(Png(0, 0, 255), File.ReadAllBytes(firstGame));
        Equal(true, store.Save(first, Result(preview, play, game)));
        Bytes(Png(255, 0, 0), File.ReadAllBytes(firstPreview));
        File.Delete(preview); File.Delete(play); File.Delete(game);
        Equal(true, store.TryRead(first, out lines));
        File.Delete(firstPlay);
        Equal(false, store.TryRead(first, out lines));
        Equal((List<string>)null, lines);

        // Still previews have no film strip and need no play_fps field.
        string still = Write("still.png", Png(11, 22, 33));
        string stillKey = Key("end=still");
        Equal(true, store.Save(stillKey, Result(still)));
        Equal(true, store.TryRead(stillKey, out lines));
        Equal(false, Decode(lines).ContainsKey("play"));
    }

    private static void CorruptionAndUnsafeResults()
    {
        string cache = Path.Combine(temporary, "bad-cache");
        var store = new PreviewCacheStore(cache);
        string preview = Write("valid.png", Png(1, 2, 3));
        List<string> lines;
        Equal(false, store.TryRead("../escape", out lines));
        Equal(false, store.Save("../escape", Result(preview)));
        Equal(false, store.Save(Key("no-result"), new[] { "STAGE 6/9 Rendering" }));
        Equal(false, store.Save(Key("bad-json"), new[] { "RESULT {bad" }));
        Equal(false, store.Save(Key("bad-shape"), new[] { "RESULT []" }));
        Equal(false, store.Save(Key("no-images"), new[] { "RESULT {\"name\":\"PAC\",\"id\":924,\"frames\":59}" }));
        var final = BaseResult(preview);
        final["gmr"] = "PAC_924.gmr";
        Equal(false, store.Save(Key("final"), new[] { "RESULT " + JsonSerializer.Serialize(final) }));
        final.Remove("gmr"); final["GMR"] = null;
        Equal(false, store.Save(Key("final-case"), new[] { "RESULT " + JsonSerializer.Serialize(final) }));
        string fake = Write("fake.png", Encoding.UTF8.GetBytes("not a PNG"));
        Equal(false, store.Save(Key("fake"), Result(fake)));
        string truncated = Write("truncated.png", Png(1, 2, 3).Take(37).ToArray());
        Equal(false, store.Save(Key("truncated"), Result(truncated)));
        Equal(false, store.Save(Key("missing"), Result(Path.Combine(temporary, "gone.png"))));
        var noFps = BaseResult(preview); noFps["play"] = preview;
        Equal(false, store.Save(Key("no-fps"), new[] { "RESULT " + JsonSerializer.Serialize(noFps) }));
        noFps["play_fps"] = 0;
        Equal(false, store.Save(Key("zero-fps"), new[] { "RESULT " + JsonSerializer.Serialize(noFps) }));
        Equal(0, Directory.Exists(cache) ? Directory.GetDirectories(cache).Count(directory => Path.GetFileName(directory).StartsWith(".writing-")) : 0);

        string key = Key("corrupt-image");
        Equal(true, store.Save(key, Result(preview)));
        Equal(true, store.TryRead(key, out lines));
        string savedImage = Decode(lines)["preview"].GetString();
        DateTime previousStamp = File.GetLastWriteTimeUtc(savedImage);
        byte[] damage = File.ReadAllBytes(savedImage); damage[damage.Length - 16] ^= 1;
        File.WriteAllBytes(savedImage, damage); File.SetLastWriteTimeUtc(savedImage, previousStamp);
        Equal(false, store.TryRead(key, out lines));
        // A broken existing entry may be replaced after a successful fresh render.
        Equal(true, store.Save(key, Result(preview)));
        Equal(true, store.TryRead(key, out lines));
        string manifest = Path.Combine(cache, key, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"version\":1", "\"version\":99"));
        Equal(false, store.TryRead(key, out lines));
        Equal(true, store.Save(key, Result(preview)));
        File.WriteAllText(manifest, "{broken");
        Equal(false, store.TryRead(key, out lines));
    }

    private static void UnwritableStore()
    {
        string file = Write("cache-is-file", new byte[] { 1 });
        var store = new PreviewCacheStore(file);
        string preview = Write("fallback-preview.png", Png(2, 3, 4));
        Equal(false, store.Save(Key("unwritable"), Result(preview)));
        List<string> lines;
        Equal(false, store.TryRead(Key("unwritable"), out lines));
    }

    private static void ConcurrentWritesAndPruning()
    {
        string root = Path.Combine(temporary, "bounded-cache");
        var store = new PreviewCacheStore(root);
        string preview = Write("bounded-preview.png", Png(17, 18, 19));
        string shared = Key("concurrent");
        Task<bool>[] writes = Enumerable.Range(0, 8).Select(index => Task.Run(() => store.Save(shared, Result(preview)))).ToArray();
        Task.WaitAll(writes);
        Equal(true, writes.Any(task => task.Result));
        List<string> lines;
        Equal(true, store.TryRead(shared, out lines));
        Equal(0, Directory.GetDirectories(root).Count(directory => Path.GetFileName(directory).StartsWith(".writing-")));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, shared), DateTime.UtcNow.AddDays(-1));
        for (int index = 0; index < 64; index++)
            if (!store.Save(Key("bounded-" + index), Result(preview))) throw new Exception("Could not populate bounded cache");
        Equal(64, Directory.GetDirectories(root).Length);
        Equal(false, store.TryRead(shared, out lines));
        Equal(true, store.TryRead(Key("bounded-63"), out lines));
    }

    private static Dictionary<string, object> BaseResult(string preview)
    {
        return new Dictionary<string, object> { { "name", "PAC" }, { "id", 924 }, { "frames", 59 }, { "end", "freeze" }, { "preview", preview }, { "linked", new[] { "123" } } };
    }

    private static string[] Result(string preview, string play = null, string game = null)
    {
        var result = BaseResult(preview);
        if (play != null) { result["play"] = play; result["play_fps"] = 15; }
        if (game != null) result["game_picture"] = game;
        return new[] { "STAGE 8/9 Finishing preview", "RESULT " + JsonSerializer.Serialize(result) };
    }

    private static Dictionary<string, JsonElement> Decode(List<string> lines)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines.Single(line => line.StartsWith("RESULT ")).Substring(7));
    }

    private static string Key(string mode) { return PreviewCache.Key(new[] { "quick=1", mode }, true, new string[0]); }
    private static string Write(string name, byte[] bytes) { string path = Path.Combine(temporary, name); File.WriteAllBytes(path, bytes); return path; }
    private static void Equal<T>(T expected, T actual) { checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + "; got " + actual); }
    private static void Different(string a, string b) { checks++; if (a == b || b == null) throw new Exception("Expected distinct non-null fingerprints/paths"); }
    private static void Bytes(byte[] a, byte[] b) { checks++; if (!a.SequenceEqual(b)) throw new Exception("Cached pixels changed"); }

    // A real, CRC-correct 1x1 RGB PNG with a different pixel for each test artifact.
    private static byte[] Png(byte red, byte green, byte blue)
    {
        using (var output = new MemoryStream())
        {
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            Chunk(output, "IHDR", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0 });
            using (var data = new MemoryStream())
            {
                using (var compressor = new ZLibStream(data, CompressionLevel.Fastest, true)) compressor.Write(new byte[] { 0, red, green, blue });
                Chunk(output, "IDAT", data.ToArray());
            }
            Chunk(output, "IEND", new byte[0]);
            return output.ToArray();
        }
    }

    private static void Chunk(Stream output, string name, byte[] data)
    {
        BigEndian(output, (uint)data.Length);
        byte[] kind = Encoding.ASCII.GetBytes(name); output.Write(kind); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in kind.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        BigEndian(output, ~crc);
    }

    private static void BigEndian(Stream output, uint number)
    {
        output.Write(new[] { (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number });
    }
}
