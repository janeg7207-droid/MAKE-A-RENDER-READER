using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root;
    private const string Avery = "1709AEF076130352";

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-render-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string complete = Fixture("complete", Metadata());
            byte[] video = VideoOf(complete);
            Check(GorillaRenderMetadata.SetRosterHashes(complete, 122, new[] { Avery, "ffffffffffffffff", Avery.ToLowerInvariant(), "0000000000000001" }), "Avery's actual profile hash is accepted without a bundled texture name");
            Check(video.SequenceEqual(VideoOf(complete)), "Every video byte survives the metadata update");
            using (JsonDocument metadata = Read(complete))
            {
                JsonElement value = metadata.RootElement;
                Check(value.GetProperty("render_hashes").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { Avery, "FFFFFFFFFFFFFFFF", "0000000000000001" }), "Full uint64 hashes retain precision, uppercase, order, and leading zeroes");
                Check(!value.TryGetProperty("render_names", out _), "Stale names cannot add a second hash when the engine rewrites a GMR");
                Check(value.GetProperty("wrestler_id").GetInt32() == 122, "The logical wrestler ID is retained");
                Check(value.GetProperty("extra_ids")[0].GetInt32() == 610, "Match card extra IDs are retained");
                Check(value.GetProperty("screens").GetString() == "both", "Screen selection is retained");
                Check(value.GetProperty("shared_hashes")[0].GetString() == "1234567890ABCDEF", "Other shared-picture metadata is retained");
                Check(value.GetProperty("name").GetString() == "AVERY STYLES – 雪", "Unicode wrestler names survive");
                Check(value.GetProperty("taunt").GetProperty("clip").GetString() == "62541a000", "Nested taunt metadata is retained");
                Check(value.GetProperty("custom")[0].GetUInt64() == ulong.MaxValue, "Unrelated uint64 metadata keeps its exact value");
                Check(value.GetProperty("custom")[1].GetDecimal() == 0.125m && value.GetProperty("custom")[2].ValueKind == JsonValueKind.Null, "Decimal and null metadata survive");
            }

            Check(GorillaRenderMetadata.SetRosterHashes(complete, 122, new[] { "0000000000000012" }), "An existing output can be relinked");
            using (JsonDocument metadata = Read(complete))
                Check(metadata.RootElement.GetProperty("render_hashes").GetArrayLength() == 1 && metadata.RootElement.GetProperty("render_hashes")[0].GetString() == "0000000000000012", "New roster keys replace old keys instead of accumulating them");
            Check(video.SequenceEqual(VideoOf(complete)), "Relinking again still preserves the video");

            string cleared = Fixture("clear", Metadata());
            Check(GorillaRenderMetadata.SetRosterHashes(cleared, 122, new string[0]), "An explicitly empty desired key list is supported");
            using (JsonDocument metadata = Read(cleared))
                Check(metadata.RootElement.GetProperty("render_hashes").GetArrayLength() == 0 && !metadata.RootElement.TryGetProperty("render_names", out _), "Clearing picture keys does not leave a stale named link");

            string shared = Fixture("shared-current-profile", Metadata());
            Check(GorillaRenderMetadata.SetRosterHashes(shared, 122, new[] { Avery }, new[] { 1481, 122, 610, 1481 }, new string[0]), "Current profile hashes and shared owners can replace synthetic intermediate metadata together");
            using (JsonDocument metadata = Read(shared))
            {
                JsonElement value = metadata.RootElement;
                Check(value.GetProperty("render_hashes").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { Avery }), "Authoritative shared update restores the actual Avery picture hash");
                Check(value.GetProperty("extra_ids").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual(new[] { 1481, 610 }), "Shared owner IDs are deduplicated and exclude the selected wrestler");
                Check(value.GetProperty("shared_hashes").GetArrayLength() == 0, "An explicit empty shared key list clears intermediate shared hashes");
                Check(!value.TryGetProperty("render_names", out _) && value.GetProperty("screens").GetString() == "both", "Shared updates remove stale names while preserving screen options");
            }
            Check(video.SequenceEqual(VideoOf(shared)), "Replacing shared associations still preserves every video byte");
            string clearShared = Fixture("clear-shared", Metadata());
            Check(GorillaRenderMetadata.SetRosterHashes(clearShared, 122, new[] { Avery }, new int[0], new[] { "ffffffffffffffff", "FFFFFFFFFFFFFFFF", "0000000000000001" }), "A complete shared key replacement accepts exact uint64 values");
            using (JsonDocument metadata = Read(clearShared))
            {
                JsonElement value = metadata.RootElement;
                Check(value.GetProperty("extra_ids").GetArrayLength() == 0, "Stale match card owners can be cleared explicitly");
                Check(value.GetProperty("shared_hashes").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "FFFFFFFFFFFFFFFF", "0000000000000001" }), "Shared hashes normalize and deduplicate without losing precision");
            }
            foreach (int owner in new[] { 0, -1, 65536, int.MaxValue })
                RejectShared("bad-owner-" + owner, new[] { 610, owner }, new string[0], "Invalid shared owner leaves the complete original render unchanged: " + owner);
            RejectShared("bad-shared-hash", new[] { 610 }, new[] { Avery, "1234" }, "Invalid shared hash cannot partially replace metadata");
            RejectShared("null-owners", null, new string[0], "The shared overload requires an explicit owner collection");
            RejectShared("null-shared-hashes", new[] { 610 }, null, "The shared overload requires an explicit shared key collection");

            foreach (string invalid in new[] { "", "1", "1709AEF07613035", "1709AEF0761303520", "1709AEF07613035G", " 709AEF076130352", "1709AEF076130352 ", "0x09AEF076130352", null })
                Reject("hash-" + passed, Metadata(), 122, new[] { invalid }, "Invalid hash is rejected without changing the output: " + (invalid ?? "null"));
            Reject("null-hashes", Metadata(), 122, null, "A null collection cannot clear links accidentally");
            foreach (int id in new[] { 0, -1, 65536, int.MaxValue, 123 })
                Reject("id-" + id, Metadata(), id, new[] { Avery }, "Invalid or mismatched wrestler ID leaves the render untouched: " + id);
            foreach (string id in new[] { "\"122\"", "122.5", "true", "null", "18446744073709551615" })
                Reject("metadata-id-" + passed, Metadata().Replace("\"wrestler_id\":122", "\"wrestler_id\":" + id), 122, new[] { Avery }, "Nonmatching or nonnumeric metadata wrestler ID is rejected: " + id);
            Reject("absent-id", "{\"render_hashes\":[]}", 122, new[] { Avery }, "Metadata without wrestler_id is never associated by filename");
            Reject("array-json", "[]", 122, new[] { Avery }, "A JSON array is not a GMR metadata object");
            Reject("malformed-json", "{\"wrestler_id\":122,", 122, new[] { Avery }, "Truncated JSON does not overwrite a completed render");

            Mutated("footer-magic", bytes => bytes[bytes.Length - 1] = (byte)'2', "Unknown footer formats are rejected");
            Mutated("zero-footer", bytes => WriteU32(bytes, bytes.Length - 8, 0), "An empty metadata footer is rejected");
            Mutated("oversize-footer", bytes => WriteU32(bytes, bytes.Length - 8, uint.MaxValue), "An overflowing metadata length is rejected");
            Mutated("footer-overlap", bytes => WriteU32(bytes, bytes.Length - 8, (uint)bytes.Length - 8), "Metadata may not overlap the video header");
            Mutated("bad-video-magic", bytes => bytes[0] = (byte)'X', "Only a Bink 1 revision i prefix is accepted");
            Mutated("bad-video-size", bytes => WriteU32(bytes, 4, 1), "The video header size must match the untouched video prefix");
            Mutated("zero-frames", bytes => WriteU32(bytes, 8, 0), "A zero-frame video is not rewritten");
            Mutated("unequal-frames", bytes => WriteU32(bytes, 16, 2), "Inconsistent repeated frame counts are rejected");
            Mutated("huge-frames", bytes => { WriteU32(bytes, 8, uint.MaxValue); WriteU32(bytes, 16, uint.MaxValue); }, "The complete frame index must fit before the metadata");
            string shortFile = Path.Combine(root, "short.gmr"); File.WriteAllBytes(shortFile, Encoding.ASCII.GetBytes("GMR1"));
            byte[] beforeShort = File.ReadAllBytes(shortFile);
            Check(!GorillaRenderMetadata.SetRosterHashes(shortFile, 122, new[] { Avery }) && beforeShort.SequenceEqual(File.ReadAllBytes(shortFile)), "A truncated file is unchanged");
            Check(!GorillaRenderMetadata.SetRosterHashes(Path.Combine(root, "missing.gmr"), 122, new[] { Avery }), "Missing output is a safe failure");
            Check(!GorillaRenderMetadata.SetRosterHashes(null, 122, new[] { Avery }), "A null path is a safe failure");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "No temporary output remains after success or validation failures");

            // Optional inspection fixture for the unmodified Python engine's gmr.read.
            if (args.Length > 0)
            {
                string inspection = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(inspection));
                File.Copy(shared, inspection, true);
                Console.WriteLine("Original-engine inspection fixture: " + inspection);
            }
            Console.WriteLine(passed + " Gorilla Render metadata checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static string Metadata()
    {
        return "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":122,\"name\":\"AVERY STYLES – 雪\","
            + "\"render_names\":[\"12200_WRONG_NAME\"],\"render_hashes\":[\"DEADBEEFDEADBEEF\"],\"extra_ids\":[610],"
            + "\"screens\":\"both\",\"shared_hashes\":[\"1234567890ABCDEF\"],\"frames\":1,\"fps\":30,\"end\":\"freeze\","
            + "\"taunt\":{\"name\":\"Undisputed 2\",\"clip\":\"62541a000\"},\"custom\":[18446744073709551615,0.125,null,true]}";
    }

    private static string Fixture(string name, string json)
    {
        string path = Path.Combine(root, name + ".gmr");
        byte[] metadata = Encoding.UTF8.GetBytes(json);
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint field in new uint[] { 48, 1, 4, 1, 1024, 1024, 30, 1, 1048576, 0, 53, 56 }) writer.Write(field);
            writer.Write(new byte[] { 1, 2, 3, 4 }); // Synthetic one-frame payload, not a decodable image.
            writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }

    private static JsonDocument Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path); int size = checked((int)BitConverter.ToUInt32(bytes, bytes.Length - 8));
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, bytes.Length - 8 - size, size));
    }

    private static byte[] VideoOf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path); int size = checked((int)BitConverter.ToUInt32(bytes, bytes.Length - 8));
        return bytes.Take(bytes.Length - 8 - size).ToArray();
    }

    private static void Reject(string name, string json, int id, IEnumerable<string> hashes, string description)
    {
        string path = Fixture(name, json); byte[] original = File.ReadAllBytes(path);
        Check(!GorillaRenderMetadata.SetRosterHashes(path, id, hashes) && original.SequenceEqual(File.ReadAllBytes(path)), description);
    }

    private static void Mutated(string name, Action<byte[]> mutate, string description)
    {
        string path = Fixture(name, Metadata()); byte[] bytes = File.ReadAllBytes(path); mutate(bytes); File.WriteAllBytes(path, bytes);
        Check(!GorillaRenderMetadata.SetRosterHashes(path, 122, new[] { Avery }) && bytes.SequenceEqual(File.ReadAllBytes(path)), description);
    }

    private static void RejectShared(string name, IEnumerable<int> extraIds, IEnumerable<string> sharedHashes, string description)
    {
        string path = Fixture(name, Metadata()); byte[] original = File.ReadAllBytes(path);
        Check(!GorillaRenderMetadata.SetRosterHashes(path, 122, new[] { Avery }, extraIds, sharedHashes)
            && original.SequenceEqual(File.ReadAllBytes(path)), description);
    }

    private static void WriteU32(byte[] bytes, int position, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, position, 4); }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        passed++; Console.WriteLine("PASS: " + description);
    }
}
