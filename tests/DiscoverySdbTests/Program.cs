using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int checks;

    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-sdb-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ValidNames();
            IsolatedMalformedRows();
            DuplicateKeys();
            StructuralFailures();
            ProfileIdentitySurvives();
            foreach (string path in args) ActualSdb(path);
            Console.WriteLine("PASS " + checks + " Discovery SDB diagnostic checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ValidNames()
    {
        string file = Write("valid.sdb", Sdb(Row(uint.MaxValue, "André 東京 💥"), Row(2, ""), Row(3, "First\nSecond")));
        var warnings = new List<string>();
        var names = DiscoveryProfiles.ReadStrings(file, warnings);
        Equal("Unsigned keys and complete UTF-8 byte lengths retain exact Unicode names", "André 東京 💥", names[uint.MaxValue]);
        Equal("A valid zero-length localized string remains valid", "", names[2]);
        Equal("The SDB reader preserves complete declared text rather than truncating it", "First\nSecond", names[3]);
        Equal("Valid strings generate no recovery warning", 0, warnings.Count);
        Equal("The strict API remains compatible", true, Same(names, DiscoveryProfiles.ReadStrings(file)));
    }

    private static void IsolatedMalformedRows()
    {
        byte[] bytes = Sdb(Row(1, "Roman Reigns"), Row(2, "bad offset"), Row(3, "bad terminator"),
            new KeyValuePair<uint, byte[]>(4, new byte[] { 0xC3, 0x28 }), Row(5, "Zoë"), Row(6, "large length"));
        Put32(bytes, 8 + 12, 8); // An index address cannot serve as text.
        int end = checked((int)(Get32(bytes, 8 + 24) + Get32(bytes, 12 + 24)));
        bytes[end] = 0x7F; // Do not search for another NUL after the declared length.
        Put32(bytes, 12 + 60, uint.MaxValue);
        string file = Write("isolated.sdb", bytes);
        string before = Hash(file);
        var warnings = new List<string>();
        var names = DiscoveryProfiles.ReadStrings(file, warnings);
        Equal("Isolated damaged entries preserve every fully validated neighbor", 2, names.Count);
        Equal("The earlier valid name survives later malformed rows", "Roman Reigns", names[1]);
        Equal("A later valid Unicode name survives malformed rows", "Zoë", names[5]);
        Equal("Malformed offsets, terminators, UTF-8 and lengths never produce labels", false, new uint[] { 2, 3, 4, 6 }.Any(names.ContainsKey));
        Equal("Recovery reports the exact malformed and total row counts", true, warnings.Single().Contains("rejected 4 malformed string row(s) of 6"));
        Equal("Recovery reports unavailable keys and validated names", true, warnings.Single().Contains("4 key(s) are unavailable; 2 validated name(s) remain"));
        Equal("A malformed row diagnostic identifies its row and unsigned key", true, warnings[0].Contains("Row 2, key 0x00000002"));
        Equal("Malformed UTF-8 is explicitly diagnosed", true, warnings[0].Contains("Invalid UTF-8"));
        Equal("Recovery never changes the supplied SDB", before, Hash(file));
        Reject("The strict API still rejects out-of-text offsets", () => DiscoveryProfiles.ReadStrings(file), "outside the text section");

        foreach (var defect in new[] { "offset", "terminator", "utf8", "overflow" })
        {
            byte[] one = Sdb(defect == "utf8" ? new KeyValuePair<uint, byte[]>(7, new byte[] { 0xC3, 0x28 }) : Row(7, "Name"));
            if (defect == "offset") Put32(one, 8, uint.MaxValue);
            if (defect == "terminator") one[one.Length - 1] = 1;
            if (defect == "overflow") Put32(one, 12, uint.MaxValue);
            string path = Write("single-" + defect + ".sdb", one);
            var diagnostics = new List<string>();
            Equal("An entirely invalid " + defect + " key has no fabricated name", 0, DiscoveryProfiles.ReadStrings(path, diagnostics).Count);
            Equal("An entirely invalid " + defect + " SDB remains an explicit warning", true, diagnostics.Single().Contains("1 malformed string row(s) of 1"));
            Reject("A null warning collector retains strict " + defect + " behavior", () => DiscoveryProfiles.ReadStrings(path, null), defect == "utf8" ? "UTF-8" : defect == "terminator" ? "terminator" : "outside the text section");
        }

        byte[] many = Sdb(Enumerable.Range(1, 30).Select(value => Row((uint)value, "bad")).ToArray());
        for (int index = 0; index < 30; index++) Put32(many, 8 + 12 * index, 0);
        var widespread = new List<string>();
        Equal("Widespread corruption never becomes plausible labels", 0, DiscoveryProfiles.ReadStrings(Write("widespread.sdb", many), widespread).Count);
        Equal("Widespread damage retains the full exact count", true, widespread.Single().Contains("30 malformed string row(s) of 30"));
        Equal("Large failure reports are bounded to five representative rows", 5, Count(widespread[0], "Row "));
        Equal("Widespread damage explicitly reports that no validated names remain", true, widespread[0].Contains("0 validated name(s) remain"));
    }

    private static void DuplicateKeys()
    {
        string file = Write("conflicting.sdb", Sdb(Row(10, "First wrestler"), Row(11, "Unaffected"), Row(10, "Different wrestler"), Row(10, "First wrestler")));
        var warnings = new List<string>();
        var strings = DiscoveryProfiles.ReadStrings(file, warnings);
        Equal("A conflicting key never chooses one wrestler name", false, strings.ContainsKey(10));
        Equal("A conflicting key preserves unrelated labels", "Unaffected", strings[11]);
        Equal("Conflicting-key diagnostics distinguish valid rows from malformed rows", true, warnings.Single().Contains("0 malformed string row(s) of 4; 1 conflicting localization key(s)"));
        Equal("The strict API retains its established last-row semantics", "First wrestler", DiscoveryProfiles.ReadStrings(file)[10]);
        var identicalWarnings = new List<string>();
        Equal("Identical duplicate keys are unambiguous", "Same", DiscoveryProfiles.ReadStrings(Write("identical.sdb", Sdb(Row(10, "Same"), Row(10, "Same"))), identicalWarnings)[10]);
        Equal("Identical duplicates produce no damage warning", 0, identicalWarnings.Count);
        foreach (int broken in new[] { 0, 1 })
        {
            byte[] bytes = Sdb(Row(10, "Same"), Row(10, "Same"), Row(11, "Other"));
            Put32(bytes, 8 + 12 * broken, 0);
            var diagnostics = new List<string>();
            var result = DiscoveryProfiles.ReadStrings(Write("duplicate-invalid-" + broken + ".sdb", bytes), diagnostics);
            Equal("A malformed alias cannot be replaced by a plausible duplicate in order " + broken, false, result.ContainsKey(10));
            Equal("A malformed duplicate leaves other names intact in order " + broken, "Other", result[11]);
            Equal("A malformed duplicate row is counted exactly in order " + broken, true, diagnostics.Single().Contains("1 malformed string row(s) of 3"));
        }
    }

    private static void StructuralFailures()
    {
        foreach (int length in new[] { 0, 4, 7 })
        {
            string path = Write("header-" + length + ".sdb", new byte[length]);
            var warnings = new List<string>();
            Equal("A truncated " + length + "-byte header preserves a usable unnamed roster", 0, DiscoveryProfiles.ReadStrings(path, warnings).Count);
            Equal("A truncated header clearly reports unavailable localization", true, warnings.Single().Contains("Header is truncated") && warnings[0].Contains("Localization names are unavailable"));
            Reject("The strict API rejects a truncated header", () => DiscoveryProfiles.ReadStrings(path), "Header is truncated");
        }
        foreach (uint count in new[] { 1U, uint.MaxValue })
        {
            byte[] bytes = new byte[12]; Put32(bytes, 4, count);
            string path = Write("index-" + count + ".sdb", bytes);
            var warnings = new List<string>();
            Equal("A truncated declared index is not reinterpreted as another layout", 0, DiscoveryProfiles.ReadStrings(path, warnings).Count);
            Equal("A truncated index reports its exact declared entry count", true, warnings.Single().Contains("declared " + count + " entries"));
            Reject("The strict API still rejects a truncated index", () => DiscoveryProfiles.ReadStrings(path), "index is truncated");
        }
        var missingWarnings = new List<string>();
        Equal("A missing SDB produces empty localization instead of blocking profile IDs", 0, DiscoveryProfiles.ReadStrings(Path.Combine(root, "missing.sdb"), missingWarnings).Count);
        Equal("A missing SDB is an explicit diagnostic", true, missingWarnings.Single().Contains("could not be read"));
        var emptyWarnings = new List<string>();
        Equal("A valid empty SDB remains empty", 0, DiscoveryProfiles.ReadStrings(Write("empty.sdb", Sdb()), emptyWarnings).Count);
        Equal("An empty SDB does not silently appear to have complete names", true, emptyWarnings.Single().Contains("contains no string entries"));
    }

    private static void ProfileIdentitySurvives()
    {
        string data = Path.Combine(root, "profile-data");
        string table = Path.Combine(data, "Roster", "CharProfileTable.jsfb");
        Directory.CreateDirectory(Path.GetDirectoryName(table));
        File.WriteAllBytes(table, ProfileTable(new ushort[] { 122, 924, 65535 }, new uint[] { 1, 2, 3 }));
        string sdb = Path.Combine(data, "Sdb", "ENG.sdb"); Directory.CreateDirectory(Path.GetDirectoryName(sdb));
        byte[] bytes = Sdb(Row(1, "Avery Styles"), Row(2, "Bad PAC label"), Row(3, "New character"));
        Put32(bytes, 20, 0); File.WriteAllBytes(sdb, bytes);
        var warnings = new List<string>();
        var profiles = DiscoveryProfiles.Read(table, null, warnings);
        Equal("An isolated SDB failure preserves every profile ID", "122,924,65535", string.Join(",", profiles.Select(profile => profile.Id)));
        Equal("An isolated SDB failure preserves each actual base ID", "122,924,65535", string.Join(",", profiles.Select(profile => profile.BaseId)));
        Equal("An isolated SDB failure preserves exact name-reference keys", "1,2,3", string.Join(",", profiles.Select(profile => profile.NameKey)));
        Equal("Validated roster labels remain available", "Avery Styles", profiles[0].Name);
        Equal("A damaged key leaves the profile unnamed for Wrestler ID fallback", null, profiles[1].Name);
        Equal("A later new character keeps its exact current SDB name", "New character", profiles[2].Name);
        Equal("Profile loading surfaces exact recovery diagnostics", true, warnings.Single().Contains("1 malformed string row(s) of 3"));
        Reject("Legacy profile reading remains strict without a warning collector", () => DiscoveryProfiles.Read(table), "outside the text section");
        File.WriteAllBytes(sdb, new byte[4]); warnings.Clear();
        profiles = DiscoveryProfiles.Read(table, null, warnings);
        Equal("A structural SDB failure still preserves all profiles", 3, profiles.Count);
        Equal("Structural SDB damage never restores obsolete names", true, profiles.All(profile => profile.Name == null));
        Equal("Structural SDB damage reaches the caller diagnostic collector", true, warnings.Single().Contains("Header is truncated"));
        File.Delete(sdb); warnings.Clear();
        profiles = DiscoveryProfiles.Read(table, null, warnings);
        Equal("A missing companion SDB still loads all profile IDs", 3, profiles.Count);
        Equal("A missing companion SDB is diagnosed", true, warnings.Single().Contains("was not found"));
        File.WriteAllBytes(table, new byte[100]);
        Reject("SDB recovery does not disguise an invalid profile FlatBuffer", () => DiscoveryProfiles.Read(table, null, new List<string>()), "JSFB");
    }

    private static void ActualSdb(string path)
    {
        string before = Hash(path); var warnings = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var strings = DiscoveryProfiles.ReadStrings(path, warnings); stopwatch.Stop();
        Equal("The actual supplied SDB has no rejected rows: " + path, 0, warnings.Count);
        Equal("The actual supplied SDB retains all strict-reader keys and exact bytes", true, Same(strings, DiscoveryProfiles.ReadStrings(path)));
        Equal("The supplied SDB was audited without modification", before, Hash(path));
        Check("The actual supplied SDB exercises the measured large index", strings.Count >= 117000);
        Console.WriteLine("Actual SDB: " + path + "; " + new FileInfo(path).Length + " bytes; " + strings.Count
            + " keys; " + stopwatch.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " ms; SHA256 " + before);
    }

    private static byte[] ProfileTable(ushort[] ids, uint[] keys)
    {
        int start = 28 + ids.Length * 4; var bytes = new byte[start + ids.Length * 68];
        Put32(bytes, 0, 16); Encoding.ASCII.GetBytes("Char").CopyTo(bytes, 4);
        Put16(bytes, 8, 6); Put16(bytes, 10, 8); Put16(bytes, 12, 4);
        Put32(bytes, 16, 8); Put32(bytes, 20, 4); Put32(bytes, 24, (uint)ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            int vt = start + index * 68, wrapper = vt + 8, profileVt = vt + 16, profile = vt + 52;
            Put32(bytes, 28 + index * 4, (uint)(wrapper - (28 + index * 4)));
            Put16(bytes, vt, 8); Put16(bytes, vt + 2, 8); Put16(bytes, vt + 6, 4);
            Put32(bytes, wrapper, 8); Put32(bytes, wrapper + 4, (uint)(profile - wrapper - 4));
            Put16(bytes, profileVt, 36); Put16(bytes, profileVt + 2, 16);
            Put16(bytes, profileVt + 8, 4); Put16(bytes, profileVt + 10, 6); Put16(bytes, profileVt + 18, 8);
            Put32(bytes, profile, 36); Put16(bytes, profile + 4, ids[index]); Put16(bytes, profile + 6, ids[index]); Put32(bytes, profile + 8, keys[index]);
        }
        return bytes;
    }

    private static KeyValuePair<uint, byte[]> Row(uint key, string text) { return new KeyValuePair<uint, byte[]>(key, Encoding.UTF8.GetBytes(text)); }
    private static byte[] Sdb(params KeyValuePair<uint, byte[]>[] rows)
    {
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output, new UTF8Encoding(false), true))
        {
            writer.Write(0U); writer.Write((uint)rows.Length); uint offset = checked(8U + 12U * (uint)rows.Length);
            foreach (var row in rows) { writer.Write(offset); writer.Write((uint)row.Value.Length); writer.Write(row.Key); offset = checked(offset + (uint)row.Value.Length + 1); }
            foreach (var row in rows) { writer.Write(row.Value); writer.Write((byte)0); }
            return output.ToArray();
        }
    }
    private static string Write(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
    private static bool Same(Dictionary<uint, string> first, Dictionary<uint, string> second) { return first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out string value) && value == pair.Value); }
    private static void Put16(byte[] bytes, int offset, ushort value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
    private static void Put32(byte[] bytes, int offset, uint value) { for (int index = 0; index < 4; index++) bytes[offset + index] = (byte)(value >> (index * 8)); }
    private static uint Get32(byte[] bytes, int offset) { return BitConverter.ToUInt32(bytes, offset); }
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static int Count(string text, string word) { int count = 0, position = 0; while ((position = text.IndexOf(word, position, StringComparison.Ordinal)) >= 0) { count++; position += word.Length; } return count; }
    private static void Equal<T>(string label, T expected, T actual) { if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual); checks++; }
    private static void Check(string label, bool value) { Equal(label, true, value); }
    private static void Reject(string label, Action action, string expected)
    { try { action(); } catch (InvalidDataException error) { if (error.Message.Contains(expected)) { checks++; return; } throw; } throw new Exception(label + ": expected InvalidDataException containing " + expected); }
}
