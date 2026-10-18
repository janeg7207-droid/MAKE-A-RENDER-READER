using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private const ulong AveryHash = 0x1709AEF076130352UL;
    private const string Avery = "1709AEF076130352";
    private const string NativeWarning = "no render picture found for this wrestler: the match card works, the select screen card cannot be linked";
    private static string root;
    private static int passed;

    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-render-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = Build(new Row(122, AveryHash, 1, AveryHash), new Row(9000, AveryHash, 9),
                new Row(8001, 1), new Row(4000));
            string table = Write("CharProfileTable.jsfb", original);
            string gmr = Gmr("full", 122);
            byte[] video = Video(gmr);
            var result = Result(122, gmr);
            object unrelated = result["custom"];
            Check(DiscoveryRenderResult.Complete(result, table), "A native full render is finalized with the actual profile links");
            Check(Strings(result, "linked") == "0000000000000001," + Avery, "Linked hashes use exact 16-digit unsigned values instead of synthetic names");
            Check(Strings(result, "shared") == "", "Default native sharing keeps all selected pictures linked");
            Check(Strings(result, "also_for") == "8001,9000", "All other shared picture owners are deduplicated and sorted");
            Check(!result.ContainsKey("warning"), "Only the resolved native missing-picture warning is removed");
            Check(ReferenceEquals(unrelated, result["custom"]) && (string)result["name"] == "AVERY STYLES", "Unrelated result data is preserved");
            Check(result["linked"] is ArrayList && result["shared"] is ArrayList && result["also_for"] is ArrayList, "Result collections retain the native WPF ArrayList contract");
            Check(((ArrayList)result["also_for"]).Cast<object>().All(owner => owner is string), "Native shared owner display values are strings");
            using (JsonDocument metadata = Read(gmr))
            {
                JsonElement value = metadata.RootElement;
                Check(JsonStrings(value, "render_hashes") == "0000000000000001," + Avery, "The GMR receives the actual hashes in the result");
                Check(JsonNumbers(value, "extra_ids") == "8001,9000", "The GMR receives current shared owners instead of stale native IDs");
                Check(value.GetProperty("shared_hashes").GetArrayLength() == 0 && !value.TryGetProperty("render_names", out _), "Intermediate shared hashes and synthetic names cannot leak into installation");
                Check(value.GetProperty("screens").GetString() == "both" && value.GetProperty("taunt").GetString() == "AJ Styles 1", "Screen selection and taunt metadata remain unchanged");
            }
            Check(video.SequenceEqual(Video(gmr)), "Every byte of the original Bink video is preserved");
            string map = Map(gmr);
            const string expected = "0000000000000001 122 8001\n0000000000000009 9000\n1709AEF076130352 122 9000\n";
            Check(File.ReadAllText(map) == expected, "The installation map includes every profile with hash and owner IDs sorted");
            Check(File.ReadAllBytes(map).SequenceEqual(Encoding.UTF8.GetBytes(expected)), "The native installation map has no BOM and uses exact space-separated LF lines");
            Check(original.SequenceEqual(File.ReadAllBytes(table)), "Finalizing leaves the user's Discovery profile table byte-identical");

            var preview = Result(122, null);
            Check(DiscoveryRenderResult.Complete(preview, table) && Strings(preview, "linked").Contains(Avery), "A preview can show its actual roster linkage");
            Check(File.ReadAllText(map) == expected && Directory.GetFiles(root, "_roster_pictures.txt", SearchOption.AllDirectories).Length == 1, "A preview does not create or rewrite any installation map");
            preview = Result(122, null); preview["warning"] = "Texture extraction was incomplete";
            Check(DiscoveryRenderResult.Complete(preview, table) && (string)preview["warning"] == "Texture extraction was incomplete", "Unrelated native warnings remain visible");
            preview = Result(122, null); preview["warning"] = NativeWarning + "; another issue";
            Check(DiscoveryRenderResult.Complete(preview, table) && preview.ContainsKey("warning"), "A combined warning is never removed by substring matching");

            string emptyGmr = Gmr("empty-profile", 4000);
            var emptyResult = Result(4000, emptyGmr);
            Check(DiscoveryRenderResult.Complete(emptyResult, table), "A known profile without picture links still finalizes");
            Check(Strings(emptyResult, "linked") == "" && Strings(emptyResult, "shared") == "" && Strings(emptyResult, "also_for") == "", "A known empty profile clears all stale displayed links");
            using (JsonDocument metadata = Read(emptyGmr))
                Check(JsonStrings(metadata.RootElement, "render_hashes") == "" && JsonNumbers(metadata.RootElement, "extra_ids") == "" && JsonStrings(metadata.RootElement, "shared_hashes") == "", "A known empty profile clears stale GMR picture and sharing keys");
            Check(emptyResult.ContainsKey("warning"), "A real missing-picture warning remains when the profile has no picture");

            File.WriteAllBytes(table, Build(new Row(122, AveryHash), new Row(9999, AveryHash, 123)));
            Check(DiscoveryRenderResult.Complete(result, table) && Strings(result, "also_for") == "9999", "Refreshing replaces removed owners with newly added roster owners");
            Check(File.ReadAllText(map) == "000000000000007B 9999\n1709AEF076130352 122 9999\n", "Refreshing replaces the full installation map instead of accumulating deleted entries");
            string addedGmr = Gmr("added-character", 9999);
            var addedResult = Result(9999, addedGmr);
            Check(DiscoveryRenderResult.Complete(addedResult, table) && Strings(addedResult, "also_for") == "122", "A newly added logical roster character can immediately produce a linked render");
            File.WriteAllBytes(table, Build(new Row(9999, AveryHash)));
            Reject(result, table, "A deleted logical profile does not inherit another character's picture");
            File.WriteAllBytes(table, original);

            foreach (object id in new object[] { "122", true, 122.5, 122.00000000000001, 122.00001f, 0, -1, 65536, ulong.MaxValue, double.NaN, double.PositiveInfinity })
                Reject(Result(id, gmr), table, "Invalid or nonnumeric result IDs leave everything untouched: " + id);
            foreach (object id in new object[] { (ushort)122, 122L, 122m, 122d })
                Check(DiscoveryRenderResult.Complete(Result(id, null), table), "Integral numeric result IDs are accepted: " + id.GetType().Name);
            var absentId = Result(122, gmr); absentId.Remove("id"); Reject(absentId, table, "A filename is never used to guess a missing logical ID");
            Reject(Result(122, gmr), Path.Combine(root, "missing-table.jsfb"), "A missing profile table leaves the native render and result untouched");
            Reject(Result(122, gmr), Write("corrupt.jsfb", new byte[] { 1, 2, 3 }), "A malformed profile table cannot partially finalize a render");
            Reject(Result(122, gmr), null, "An absent original profile path is a safe failure");
            Check(!DiscoveryRenderResult.Complete(null, table), "A missing result is a safe failure");
            Check(!DiscoveryRenderResult.Complete(new ReadOnlyDictionary<string, object>(Result(122, gmr)), table), "A read-only result cannot trigger an invisible installation update");
            Reject(Result(122, ""), table, "An empty full-render path is not treated as a preview");
            Reject(Result(122, Path.Combine(root, "missing.gmr")), table, "A missing full render is not advertised as linked");
            var wrongType = Result(122, null); wrongType["gmr"] = true; Reject(wrongType, table, "A non-string full-render path is rejected");

            string invalid = Gmr("invalid-gmr", 122);
            byte[] badBytes = File.ReadAllBytes(invalid); badBytes[badBytes.Length - 1] = (byte)'2'; File.WriteAllBytes(invalid, badBytes);
            File.WriteAllText(Map(invalid), "prior map\n");
            Reject(Result(122, invalid), table, "An invalid GMR leaves its original bytes, UI data, and prior map untouched");
            File.Delete(Map(invalid));
            Reject(Result(122, invalid), table, "An invalid GMR cannot leave a newly created map behind");
            string mismatch = Gmr("mismatched-id", 610);
            Reject(Result(122, mismatch), table, "A logical ID mismatch between result and GMR cannot update another wrestler");
            string mapBlocked = Gmr("map-write-failure", 122);
            Directory.CreateDirectory(Map(mapBlocked));
            Reject(Result(122, mapBlocked), table, "A failed map write prevents any GMR or result rewrite");
            Check(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "Successful operations and rejected inputs leave no temporary files");
            if (args.Length > 0) ActualData(args[0], args.Length > 1 ? args[1] : null);
            Console.WriteLine("PASS " + passed + " Discovery render result checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ActualData(string table, string export)
    {
        byte[] before = File.ReadAllBytes(table);
        string gmr = Gmr("actual-avery", 122); byte[] video = Video(gmr);
        var result = Result(122, gmr);
        Check(DiscoveryRenderResult.Complete(result, table), "The uploaded Discovery roster finalizes AVERY STYLES successfully");
        Check(Strings(result, "linked") == Avery && Strings(result, "also_for") == "", "The real AVERY profile links its one exact picture without invented owners");
        using (JsonDocument metadata = Read(gmr))
            Check(JsonStrings(metadata.RootElement, "render_hashes") == Avery && JsonNumbers(metadata.RootElement, "extra_ids") == "", "The exported AVERY GMR contains the exact original profile hash");
        string[] lines = File.ReadAllLines(Map(gmr));
        Check(lines.Length == 1139 && lines.Contains(Avery + " 122"), "The actual installation map includes all 1139 real picture hashes");
        Check(lines.SequenceEqual(lines.OrderBy(line => line.Substring(0, 16), StringComparer.Ordinal)), "The actual installation map is sorted by full uint64 hash");
        var index = DiscoveryRenderLinks.Read(table);
        bool exact = true;
        foreach (string line in lines)
        {
            string[] columns = line.Split(' ');
            ulong hash = ulong.Parse(columns[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string expectedIds = string.Join(" ", index.Where(pair => pair.Value.Contains(hash)).Select(pair => pair.Key).OrderBy(id => id));
            exact &= string.Join(" ", columns.Skip(1)) == expectedIds;
        }
        Check(exact, "Every actual map line has exactly the profile owners recorded in Discovery");
        Check(video.SequenceEqual(Video(gmr)) && before.SequenceEqual(File.ReadAllBytes(table)), "Actual-data completion preserves video and all user table bytes");
        if (export != null)
        {
            Directory.CreateDirectory(export);
            string destination = Path.Combine(export, "122_AVERY_STYLES.gmr");
            File.Copy(gmr, destination, true); File.Copy(Map(gmr), Path.Combine(export, "_roster_pictures.txt"), true);
            Console.WriteLine("Original-engine inspection fixture: " + destination);
        }
    }

    private static Dictionary<string, object> Result(object id, string gmr)
    {
        var value = new Dictionary<string, object> {
            { "id", id }, { "name", "AVERY STYLES" }, { "linked", new ArrayList(new[] { "synthetic overlay link" }) },
            { "shared", new ArrayList(new[] { "synthetic shared hash" }) }, { "also_for", new ArrayList(new[] { "610" }) },
            { "warning", NativeWarning }, { "frames", 1 }, { "custom", new object() }
        };
        if (gmr != null) value["gmr"] = gmr;
        return value;
    }

    private static void Reject(Dictionary<string, object> result, string table, string label)
    {
        var prior = new Dictionary<string, object>(result);
        string gmr = result.TryGetValue("gmr", out object value) ? value as string : null;
        bool exists = !string.IsNullOrEmpty(gmr) && File.Exists(gmr);
        byte[] render = exists ? File.ReadAllBytes(gmr) : null;
        string map = exists ? Map(gmr) : null;
        byte[] previousMap = map != null && File.Exists(map) ? File.ReadAllBytes(map) : null;
        bool mapDirectory = map != null && Directory.Exists(map);
        bool completed = DiscoveryRenderResult.Complete(result, table);
        bool same = prior.Count == result.Count && prior.All(pair => result.ContainsKey(pair.Key) && object.Equals(pair.Value, result[pair.Key]));
        if (exists) same &= render.SequenceEqual(File.ReadAllBytes(gmr));
        if (map != null && !mapDirectory) same &= previousMap == null ? !File.Exists(map) : File.ReadAllBytes(map).SequenceEqual(previousMap);
        Check(!completed && same, label);
    }

    private static string Gmr(string name, int id)
    {
        string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".gmr");
        byte[] metadata = Encoding.UTF8.GetBytes("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":" + id
            + ",\"name\":\"AVERY STYLES\",\"render_names\":[\"native_synthetic_alias\"],\"render_hashes\":[\"DEADBEEFDEADBEEF\"],"
            + "\"extra_ids\":[610],\"shared_hashes\":[\"000000000000DEAD\"],\"screens\":\"both\",\"frames\":1,\"fps\":30,\"end\":\"freeze\",\"taunt\":\"AJ Styles 1\"}");
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint field in new uint[] { 48, 1, 4, 1, 1024, 1024, 30, 1, 1048576, 0, 53, 56 }) writer.Write(field);
            writer.Write(new byte[] { 1, 2, 3, 4 }); // A structural one-frame fixture, not a decodable video.
            writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static string Map(string gmr) { return Path.Combine(Path.GetDirectoryName(gmr), "_roster_pictures.txt"); }
    private static string Strings(Dictionary<string, object> result, string key) { return string.Join(",", ((ArrayList)result[key]).Cast<object>()); }
    private static string JsonStrings(JsonElement metadata, string key) { return string.Join(",", metadata.GetProperty(key).EnumerateArray().Select(value => value.GetString())); }
    private static string JsonNumbers(JsonElement metadata, string key) { return string.Join(",", metadata.GetProperty(key).EnumerateArray().Select(value => value.GetInt32())); }
    private static JsonDocument Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path); int length = checked((int)BitConverter.ToUInt32(bytes, bytes.Length - 8));
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, bytes.Length - 8 - length, length));
    }
    private static byte[] Video(string path)
    {
        byte[] bytes = File.ReadAllBytes(path); int length = checked((int)BitConverter.ToUInt32(bytes, bytes.Length - 8));
        return bytes.Take(bytes.Length - 8 - length).ToArray();
    }
    private static byte[] Build(params Row[] rows)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(16); writer.Write(Encoding.ASCII.GetBytes("Char"));
            writer.Write((ushort)6); writer.Write((ushort)8); writer.Write((ushort)4); writer.Write((ushort)0);
            writer.Write(8); writer.Write(4); writer.Write(rows.Length);
            int[] entries = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) { entries[i] = (int)stream.Position; writer.Write(0); }
            for (int i = 0; i < rows.Length; i++)
            {
                int wrapper = Table(writer, 8, 0, 4); Patch(writer, entries[i], wrapper - entries[i]);
                int profile = Table(writer, 12, 0, 0, 4, 0, 8); Patch(writer, wrapper + 4, profile - wrapper - 4);
                long end = stream.Position; stream.Position = profile + 4; writer.Write((ushort)rows[i].Id); stream.Position = end;
                int vector = (int)stream.Position; writer.Write(rows[i].Hashes.Length);
                int[] entries2 = new int[rows[i].Hashes.Length];
                for (int j = 0; j < entries2.Length; j++) { entries2[j] = (int)stream.Position; writer.Write(0); }
                Patch(writer, profile + 8, vector - profile - 8);
                for (int j = 0; j < entries2.Length; j++)
                {
                    int attire = Table(writer, 16, 0, 0, 0, 0, 0, 0, 8); Patch(writer, entries2[j], attire - entries2[j]);
                    end = stream.Position; stream.Position = attire + 8; writer.Write(rows[i].Hashes[j]); stream.Position = end;
                }
            }
            writer.Flush(); return stream.ToArray();
        }
    }
    private static int Table(BinaryWriter writer, int size, params int[] fields)
    {
        int vtable = (int)writer.BaseStream.Position; writer.Write((ushort)(4 + fields.Length * 2)); writer.Write((ushort)size);
        foreach (int field in fields) writer.Write((ushort)field);
        int table = (int)writer.BaseStream.Position; writer.Write(table - vtable); writer.Write(new byte[size - 4]); return table;
    }
    private static void Patch(BinaryWriter writer, int position, int value)
    {
        long end = writer.BaseStream.Position; writer.BaseStream.Position = position; writer.Write(value); writer.BaseStream.Position = end;
    }
    private static string Write(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private sealed class Row
    {
        public readonly int Id; public readonly ulong[] Hashes;
        public Row(int id, params ulong[] hashes) { Id = id; Hashes = hashes; }
    }
}
