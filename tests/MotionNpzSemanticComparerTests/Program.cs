using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks, sequence;
    private static string root;

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-motion-semantic-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            EquivalentContainers();
            QuaternionRounding();
            ChangedMotion();
            InvalidInputs();
            if (args.Length == 2)
            {
                Check(MotionNpzSemanticComparer.Equivalent(args[0], args[1]),
                    "The actual bundled Undisputed 3 and production-decoder NPZ are render equivalent");
                ActualRandyCache(args[0], args[1]);
            }
            Console.WriteLine("PASS " + checks + " motion NPZ semantic checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void ActualRandyCache(string original, string reencoded)
    {
        string app = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(original))).Parent.Parent.FullName;
        string overlay = Path.Combine(root, "actual-discovery-overlay");
        string library = Path.Combine(overlay, "data", "library");
        string decoded = Path.Combine(overlay, "data", "gamepack", "decoded");
        Directory.CreateDirectory(library); Directory.CreateDirectory(decoded);
        string manifest = Path.Combine(app, "data", "library", "taunts.json");
        File.Copy(manifest, Path.Combine(library, "taunts.json"));
        Dictionary<string, object> document = PreviewJson.Read(File.ReadAllText(manifest));
        var row = ((IEnumerable)document["taunts"]).Cast<Dictionary<string, object>>().Single(item => (string)item["key"] == "t62542");
        string clip = (string)row["clip"];
        string overlayMotion = Path.Combine(decoded, clip + ".npz");
        File.Copy(reencoded, overlayMotion);
        object face;
        if (row.TryGetValue("face", out face) && face is string && !string.IsNullOrWhiteSpace((string)face))
        {
            string faceDirectory = Path.Combine(library, "faces"); Directory.CreateDirectory(faceDirectory);
            File.Copy(Path.Combine(app, "data", "library", "faces", (string)face), Path.Combine(faceDirectory, (string)face));
        }
        string atlas;
        Check(RandyTauntPreviewCache.TryGetBundled(app, overlay, "t62542", out atlas),
            "A real Discovery-reencoded Undisputed 3 reuses its bundled Randy preview without Blender");
        Check(atlas == Path.Combine(app, "data", "library", "previews_game", "t62542.png"),
            "The reused atlas is the immutable original Randy PNG");
        var changed = ReadEntries(reencoded);
        int offset = Payload(changed["P.npy"]);
        SetFloat(changed["P.npy"], 0, BitConverter.ToSingle(changed["P.npy"], offset) + 0.001f);
        File.Copy(Write(changed), overlayMotion, true);
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "t62542", out atlas),
            "A real pose edit still rejects the bundled Undisputed 3 preview");
        File.Copy(reencoded, overlayMotion, true);
        row["cut"] = new object[] { 1, 2 };
        File.WriteAllText(Path.Combine(library, "taunts.json"), PreviewJson.Write(document));
        Check(!RandyTauntPreviewCache.TryGetBundled(app, overlay, "t62542", out atlas),
            "A changed cut still rejects the stock atlas even when decoded poses match");
    }

    private static Dictionary<string, byte[]> ReadEntries(string path)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var file = File.OpenRead(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            foreach (ZipArchiveEntry entry in archive.Entries)
                using (var memory = new MemoryStream())
                using (var source = entry.Open())
                { source.CopyTo(memory); result.Add(entry.FullName, memory.ToArray()); }
        return result;
    }

    private static void EquivalentContainers()
    {
        var original = Motion();
        string plain = Write(original, CompressionLevel.NoCompression, false, 2020);
        string compressed = Write(original, CompressionLevel.Optimal, true, 2026);
        Check(MotionNpzSemanticComparer.Equivalent(plain, compressed),
            "ZIP compression, entry order, and timestamps do not change a motion");
        Check(MotionNpzSemanticComparer.Equivalent(compressed, plain), "Motion equivalence is symmetric");
        Check(MotionNpzSemanticComparer.Equivalent(plain, plain), "A valid motion equals itself");
        string padded = Write(Motion(128), CompressionLevel.Optimal, false, 2021);
        Check(MotionNpzSemanticComparer.Equivalent(plain, padded), "NPY header padding is ignored");
        var metadata = Motion();
        metadata["hd.npy"] = TextArray("{\"u32_0x40\":24.0, \"frames\":3.0, \"fps\":60.0}", 64);
        Check(MotionNpzSemanticComparer.Equivalent(plain, Write(metadata)),
            "Metadata JSON whitespace, key order, and equal numeric forms do not change a motion");
        var signedZero = Motion(); SetFloat(signedZero["P.npy"], 1, -0.0f);
        Check(MotionNpzSemanticComparer.Equivalent(plain, Write(signedZero)),
            "Signed zero represents the same translation");
        var withUnknown = Motion(); withUnknown["notes.txt"] = Encoding.UTF8.GetBytes("unchanged provenance");
        Check(MotionNpzSemanticComparer.Equivalent(Write(withUnknown), Write(withUnknown, CompressionLevel.NoCompression, true, 2025)),
            "Identical unknown entries remain equal across ZIP encodings");
    }

    private static void ChangedMotion()
    {
        string original = Write(Motion());
        var ids = Motion(); SetU64(ids["ids.npy"], 0, 999);
        Reject(original, ids, "Different bone IDs cannot reuse a stock preview");
        var translated = Motion(); SetFloat(translated["P.npy"], 0, 0.00000001f);
        Reject(original, translated, "Even a small real translation change is rejected");
        var rotated = Motion(); SetFloat(rotated["P.npy"], 3, 0.01f);
        Reject(original, rotated, "A changed quaternion is rejected");
        var scales = Motion(); SetFloat(scales["sc.npy"], 1, 1.001f);
        Reject(original, scales, "A scale-only change is rejected");
        var scaleIds = Motion(); SetU64(scaleIds["sids.npy"], 0, 202);
        Reject(original, scaleIds, "Different scale-track IDs are rejected");
        foreach (var metadata in new[] {
            Tuple.Create("{\"fps\":30,\"frames\":3,\"u32_0x40\":24}", "Frame-rate changes are rejected"),
            Tuple.Create("{\"fps\":60,\"frames\":4,\"u32_0x40\":24}", "Frame-count metadata changes are rejected"),
            Tuple.Create("{\"fps\":60,\"frames\":3,\"u32_0x40\":25}", "Animation flag changes are rejected"),
            Tuple.Create("{\"fps\":60,\"frames\":3,\"u32_0x40\":24,\"future\":1}", "Unknown metadata is not silently discarded") })
        {
            var data = Motion(); data["hd.npy"] = TextArray(metadata.Item1, 64); Reject(original, data, metadata.Item2);
        }
        var extraLeft = Motion(); extraLeft["notes.txt"] = Encoding.UTF8.GetBytes("left");
        var extraRight = Motion(); extraRight["notes.txt"] = Encoding.UTF8.GetBytes("right");
        Check(!MotionNpzSemanticComparer.Equivalent(Write(extraLeft), Write(extraRight)),
            "Different unknown entry content is rejected");
        Check(!MotionNpzSemanticComparer.Equivalent(original, Write(extraLeft)),
            "Added unknown entries are not ignored");
    }

    private static void QuaternionRounding()
    {
        string original = Write(Motion());
        var rounded = Motion(); SetFloat(rounded["P.npy"], 6, 1.0000001192092896f);
        Check(MotionNpzSemanticComparer.Equivalent(original, Write(rounded)),
            "One float32 ULP of unit-quaternion decoder rounding preserves the same preview");
        var within = Motion(); SetFloat(within["P.npy"], 3, 0.00000012f);
        Check(MotionNpzSemanticComparer.Equivalent(original, Write(within)),
            "Finite unit-quaternion rounding below 1.25e-7 is accepted");
        var beyond = Motion(); SetFloat(beyond["P.npy"], 3, 0.00000013f);
        Check(!MotionNpzSemanticComparer.Equivalent(original, Write(beyond)),
            "Quaternion changes above the narrow decoder-rounding bound are rejected");
        var nonUnit = Motion(); SetFloat(nonUnit["P.npy"], 6, 2f);
        var nonUnitChanged = Motion(); SetFloat(nonUnitChanged["P.npy"], 6, 2f); SetFloat(nonUnitChanged["P.npy"], 3, 0.00000012f);
        Check(!MotionNpzSemanticComparer.Equivalent(Write(nonUnit), Write(nonUnitChanged)),
            "The rounding allowance cannot make a differing non-unit quaternion equivalent");
        var scaleRounding = Motion(); SetFloat(scaleRounding["sc.npy"], 0, 1.0000001192092896f);
        Check(!MotionNpzSemanticComparer.Equivalent(original, Write(scaleRounding)),
            "Quaternion rounding never grants a tolerance to scale tracks");
        var translated = Motion(); SetFloat(translated["P.npy"], 7, 1.0000001192092896f);
        Check(!MotionNpzSemanticComparer.Equivalent(original, Write(translated)),
            "Quaternion rounding never grants a tolerance to translation tracks");
    }

    private static void InvalidInputs()
    {
        string valid = Write(Motion());
        Check(!MotionNpzSemanticComparer.Equivalent(valid, Path.Combine(root, "missing.npz")), "A missing input fails closed");
        Check(!MotionNpzSemanticComparer.Equivalent(null, valid), "A null input fails closed");
        string notZip = Path.Combine(root, "not-a-zip.npz"); File.WriteAllText(notZip, "not a ZIP archive");
        Check(!MotionNpzSemanticComparer.Equivalent(valid, notZip), "Non-ZIP input fails closed");
        var missing = Motion(); missing.Remove("sc.npy");
        Reject(valid, missing, "Required scale arrays cannot be missing even if a reader might ignore them");
        var truncated = Motion(); truncated["P.npy"] = truncated["P.npy"].Take(truncated["P.npy"].Length - 1).ToArray();
        Reject(valid, truncated, "Truncated array payloads are rejected");
        var trailing = Motion(); trailing["ids.npy"] = trailing["ids.npy"].Concat(new byte[] { 1 }).ToArray();
        Reject(valid, trailing, "Trailing array payload bytes are rejected");
        var malformed = Motion(); malformed["P.npy"][0] = 0;
        Reject(valid, malformed, "Malformed NPY magic is rejected");
        var impossible = Motion(); impossible["P.npy"] = Npy("<f4", "(2147483647, 2147483647, 7)", new byte[4], 64);
        Reject(valid, impossible, "Huge declared dimensions are rejected without allocating their payload");
        var wrongShape = Motion(); wrongShape["P.npy"] = Npy("<f4", "(2, 7, 3)", Floats(Poses()), 64);
        Reject(valid, wrongShape, "Array dimensions are meaningful even when the byte count matches");
        var badHeader = Motion(); byte[] header = (byte[])badHeader["P.npy"].Clone(); header[8] = 255; header[9] = 255; badHeader["P.npy"] = header;
        Reject(valid, badHeader, "A declared header beyond the entry is rejected");
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var nonFinite = Motion(); SetFloat(nonFinite["P.npy"], 3, value); string path = Write(nonFinite);
            Check(!MotionNpzSemanticComparer.Equivalent(path, path), "Non-finite pose data never equals itself: " + value);
        }
        var invalidScale = Motion(); SetFloat(invalidScale["sc.npy"], 0, float.NaN); string scalePath = Write(invalidScale);
        Check(!MotionNpzSemanticComparer.Equivalent(scalePath, scalePath), "Non-finite scale data is rejected even in identical files");
        var tooMany = Motion();
        for (int i = tooMany.Count; i < 33; i++) tooMany["future-" + i + ".txt"] = new byte[] { 1 };
        string manyPath = Write(tooMany);
        Check(!MotionNpzSemanticComparer.Equivalent(manyPath, manyPath), "The 32-entry bound prevents unbounded ZIP catalogs");
        string duplicate = Write(Motion(), duplicate: true);
        Check(!MotionNpzSemanticComparer.Equivalent(valid, duplicate), "Duplicate NPZ entry names cannot pick an arbitrary array");
        string expansion = Write(Motion()); byte[] zip = File.ReadAllBytes(expansion);
        for (int i = 0; i + 28 < zip.Length; i++)
            if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
            { Array.Copy(BitConverter.GetBytes(0x7fffffffu), 0, zip, i + 24, 4); break; }
        File.WriteAllBytes(expansion, zip);
        Check(!MotionNpzSemanticComparer.Equivalent(valid, expansion), "Oversized declared ZIP expansion is rejected before reading it");
    }

    private static void Reject(string valid, Dictionary<string, byte[]> data, string message)
    { Check(!MotionNpzSemanticComparer.Equivalent(valid, Write(data)), message); }

    private static Dictionary<string, byte[]> Motion(int padding = 64)
    {
        return new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            { "ids.npy", Npy("<u8", "(2,)", U64(101, 202), padding) },
            { "P.npy", Npy("<f4", "(2, 3, 7)", Floats(Poses()), padding) },
            { "sids.npy", Npy("<u8", "(1,)", U64(101), padding) },
            { "sc.npy", Npy("<f4", "(1, 3)", Floats(new[] { 1f, 1f, 1f }), padding) },
            { "hd.npy", TextArray("{\"fps\":60,\"frames\":3,\"u32_0x40\":24}", padding) }
        };
    }

    private static float[] Poses()
    {
        var values = new float[2 * 3 * 7];
        for (int bone = 0; bone < 2; bone++)
            for (int frame = 0; frame < 3; frame++)
            { int start = (bone * 3 + frame) * 7; values[start] = bone * 10 + frame; values[start + 6] = 1; }
        return values;
    }

    private static byte[] TextArray(string text, int padding)
    {
        var data = new byte[text.Length * 4];
        for (int i = 0; i < text.Length; i++) Array.Copy(BitConverter.GetBytes((uint)text[i]), 0, data, i * 4, 4);
        return Npy("<U" + text.Length, "()", data, padding);
    }

    private static byte[] Npy(string type, string shape, byte[] payload, int alignment)
    {
        string descriptor = "{'descr': '" + type + "', 'fortran_order': False, 'shape': " + shape + ", }";
        int padding = (alignment - ((10 + descriptor.Length + 1) % alignment)) % alignment;
        byte[] header = Encoding.ASCII.GetBytes(descriptor + new string(' ', padding) + "\n");
        using (var memory = new MemoryStream())
        using (var writer = new BinaryWriter(memory))
        {
            writer.Write(new byte[] { 0x93, 78, 85, 77, 80, 89, 1, 0 }); writer.Write((ushort)header.Length);
            writer.Write(header); writer.Write(payload); return memory.ToArray();
        }
    }

    private static string Write(Dictionary<string, byte[]> data, CompressionLevel level = CompressionLevel.Optimal,
        bool reverse = false, int year = 2024, bool duplicate = false)
    {
        string path = Path.Combine(root, "motion-" + (++sequence) + ".npz");
        using (var file = new FileStream(path, FileMode.CreateNew))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            IEnumerable<KeyValuePair<string, byte[]>> entries = data;
            if (reverse) entries = entries.Reverse();
            foreach (var pair in entries)
            {
                var entry = zip.CreateEntry(pair.Key, level); entry.LastWriteTime = new DateTimeOffset(year, 1, 2, 3, 4, 6, TimeSpan.Zero);
                using (var stream = entry.Open()) stream.Write(pair.Value, 0, pair.Value.Length);
            }
            if (duplicate)
                using (var stream = zip.CreateEntry("P.npy", level).Open()) stream.Write(data["P.npy"], 0, data["P.npy"].Length);
        }
        return path;
    }

    private static byte[] Floats(float[] values)
    { return values.SelectMany(BitConverter.GetBytes).ToArray(); }
    private static byte[] U64(params ulong[] values)
    { return values.SelectMany(BitConverter.GetBytes).ToArray(); }
    private static int Payload(byte[] array) { return 10 + BitConverter.ToUInt16(array, 8); }
    private static void SetFloat(byte[] array, int index, float value)
    { Array.Copy(BitConverter.GetBytes(value), 0, array, Payload(array) + index * 4, 4); }
    private static void SetU64(byte[] array, int index, ulong value)
    { Array.Copy(BitConverter.GetBytes(value), 0, array, Payload(array) + index * 8, 8); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
}
