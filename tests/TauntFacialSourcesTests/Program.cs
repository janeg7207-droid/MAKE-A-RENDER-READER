using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root;
    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-facial-sources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string app = Path.Combine(root, "app");
            Write(app, "data/faces/male/motion.npz", "male motion");
            Write(app, "data/faces/female/motion.npz", "female motion");
            Write(app, "data/library/faces/facial_6368.npz", "generic face");
            Write(app, "data/library/faces/other.npz", "other face");
            var record = Record();
            var index = new Dictionary<string, object> { { "motion", record }, { "unrelated", Record() } };
            Index(app, index);
            var entry = Entry();
            string male = Signature(app, entry), female = Signature(app, entry, "female");
            Equal("A deterministic content signature is SHA256", 64, male.Length);
            Equal("Gender-specific files change the effective source", false, male == female);
            Equal("Missing explicit gender defaults to male", male, Signature(app, entry, null));
            Equal("Unknown native gender falls back to male", male, Signature(app, entry, "other"));
            string originalIndex = File.ReadAllText(Path.Combine(app, "data/faces/index.json"));
            Dictionary<string, object> alias;
            Equal("A complete indexed face record can be copied privately", true, TauntFacialSources.TryReadAlias(app, "motion", out alias));
            Equal("The index offset is retained in frames", 156d, Convert.ToDouble(alias["offset"]));
            Equal("Male paths stay relative to the face tree", "male/motion.npz", (string)alias["male"]);
            alias["male"] = "changed.npz";
            Equal("Returned record mutation cannot overwrite the source index", originalIndex, File.ReadAllText(Path.Combine(app, "data/faces/index.json")));
            Equal("A parsed index avoids rereading its JSON", true, TauntFacialSources.TryReadAlias(app, index, "motion", out alias));
            alias["offset"] = 999;
            Equal("Parsed index records are copied rather than mutated", 156, record["offset"]);
            Equal("Exact key lookup never guesses a variant", false, TauntFacialSources.TryReadAlias(app, "motion_variant", out alias));
            entry["key"] = "motion_variant";
            string fallback = Signature(app, entry);
            Equal("A renamed key does not silently borrow another variant's face", false, male == fallback);
            entry["key"] = "motion";
            entry["face_offset"] = 1;
            Equal("A nonzero library offset disables own-face selection", false, male == Signature(app, entry));
            entry["face_offset"] = 0;
            entry["face"] = "other.npz";
            string other = Signature(app, entry);
            Equal("Another library face keeps its explicit selection", false, other == male);
            entry["face"] = "FACIAL_6368.npz";
            Equal("The native default-face gate uses exact case", false, male == Signature(app, entry));
            entry["face"] = "facial_6368.npz";
            entry.Remove("face_offset");
            Equal("The native missing face_offset default is zero", male, Signature(app, entry));
            entry["face_offset"] = 0;
            DateTime time = File.GetLastWriteTimeUtc(Path.Combine(app, "data/faces/male/motion.npz"));
            Write(app, "data/faces/male/motion.npz", "MALE MOTION");
            File.SetLastWriteTimeUtc(Path.Combine(app, "data/faces/male/motion.npz"), time);
            Equal("Same-size same-time male edits invalidate the male cache", false, male == Signature(app, entry));
            Equal("Male edits do not invalidate the unused female source", female, Signature(app, entry, "female"));
            Write(app, "data/faces/male/motion.npz", "male motion");
            Write(app, "data/faces/female/motion.npz", "FEMALE MOTION");
            Equal("Female edits do not invalidate a male preview", male, Signature(app, entry));
            Equal("Female edits invalidate a female preview", false, female == Signature(app, entry, "female"));
            record["events"] = 77; index["unrelated"] = new[] { 1, 2, 3 }; Index(app, index);
            Equal("Unrelated records and log-only event counts do not invalidate motion", male, Signature(app, entry));
            File.WriteAllText(Path.Combine(app, "data/faces/index.json"), "\n " + JsonSerializer.Serialize(index).Replace(":", " : ") + "\n");
            Equal("Formatting changes are not facial source changes", male, Signature(app, entry));
            string copy = Path.Combine(root, "fresh-overlay");
            CopyTree(app, copy);
            Equal("Identical private overlay roots reuse the facial signature", male, Signature(copy, entry));
            File.SetLastWriteTimeUtc(Path.Combine(copy, "data/faces/male/motion.npz"), DateTime.UtcNow.AddDays(2));
            Equal("A copied NPZ timestamp does not invalidate unchanged content", male, Signature(copy, entry));
            record["offset"] = 157; Index(app, index);
            Equal("A frame offset edit invalidates the effective face", false, male == Signature(app, entry));
            record["offset"] = 156; record.Remove("female"); Index(app, index);
            Equal("An absent female field follows the native male fallback", male, Signature(app, entry, "female"));
            Equal("Alias publication requires both gender paths", false, TauntFacialSources.TryReadAlias(app, "motion", out alias));
            record["female"] = ""; Index(app, index);
            Equal("An empty female field also falls back to male", male, Signature(app, entry, "female"));
            record["female"] = "female/missing.npz"; Index(app, index);
            Equal("A specified but missing female file uses the library fallback", fallback, Signature(app, entry, "female"));
            Equal("Missing own-file fallback matches an absent exact-key fallback", fallback, Signature(app, Entry("absent")));
            record["female"] = "female/motion.npz";
            foreach (object invalid in new object[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1000001, -1000001, "156", true, null })
            {
                record["offset"] = invalid;
                Equal("An invalid offset cannot be aliased: " + (invalid ?? "null"), false,
                    TauntFacialSources.TryReadAlias(app, index, "motion", out alias));
            }
            record["offset"] = -156;
            Equal("A bounded negative frame offset is retained", true, TauntFacialSources.TryReadAlias(app, index, "motion", out alias));
            record.Remove("offset");
            Equal("An absent index offset uses the native zero default", true, TauntFacialSources.TryReadAlias(app, index, "motion", out alias));
            Equal("Missing index offset is copied as zero frames", 0d, (double)alias["offset"]);
            record["offset"] = 156;
            foreach (string invalid in new[] { "../outside.npz", "male/../motion.npz", "/tmp/outside.npz", @"C:\outside.npz", @"\\server\share\outside.npz", "C:outside.npz", "male//motion.npz", "./male/motion.npz", "male/motion.npz:stream", "male/motion.png", "male/motion.npz\n", "male\0/motion.npz", "male./motion.npz", "male /motion.npz", "missing.npz" })
            {
                record["male"] = invalid;
                Equal("Unsafe or missing paths cannot be aliased: " + invalid.Replace('\0', '?').Replace('\n', '?'), false,
                    TauntFacialSources.TryReadAlias(app, index, "motion", out alias));
                Index(app, index);
                Equal("Unsafe indexed paths retain the effective library fallback", fallback, Signature(app, entry));
            }
            record["male"] = @"male\motion.npz"; Index(app, index);
            Equal("Safe Windows separators normalize to the same source", male, Signature(app, entry));
            Equal("Alias paths normalize Windows separators", true, TauntFacialSources.TryReadAlias(app, "motion", out alias));
            Equal("Private alias paths are normalized", "male/motion.npz", (string)alias["male"]);
            record["male"] = "male/motion.npz";
            foreach (string malformed in new[] { "[1,2]", "{broken", "{\"motion\":5}", "{\"motion\":[]}", "null" })
            {
                Write(app, "data/faces/index.json", malformed);
                Equal("Malformed index safely retains fallback", fallback, Signature(app, entry));
                Equal("Malformed index cannot publish an alias", false, TauntFacialSources.TryReadAlias(app, "motion", out alias));
            }
            Write(app, "data/faces/index.json", new string(' ', 4 * 1024 * 1024 + 1));
            Equal("Oversized index is rejected before parsing", false, TauntFacialSources.TryReadAlias(app, "motion", out alias));
            Equal("Oversized index falls back safely", fallback, Signature(app, entry));
            File.WriteAllBytes(Path.Combine(app, "data/faces/index.json"), new byte[] { 0xFF, 0xFE, 0xFA });
            Equal("Invalid UTF8 is rejected", false, TauntFacialSources.TryReadAlias(app, "motion", out alias));
            Equal("Invalid UTF8 retains fallback", fallback, Signature(app, entry));
            File.Delete(Path.Combine(app, "data/faces/index.json"));
            Equal("Absent index retains fallback", fallback, Signature(app, entry));
            Write(app, "data/library/faces/facial_6368.npz", "GENERIC FACE");
            Equal("Generic fallback content changes invalidate fallback previews", false, fallback == Signature(app, entry));
            var linked = Path.Combine(root, "linked-overlay");
            Directory.CreateDirectory(Path.Combine(linked, "data"));
            Directory.CreateSymbolicLink(Path.Combine(linked, "data/faces"), Path.Combine(copy, "data/faces"));
            Directory.CreateSymbolicLink(Path.Combine(linked, "data/library"), Path.Combine(copy, "data/library"));
            Equal("Factory-linked facial trees match their source semantics", male, Signature(linked, entry));
            Equal("Signature accepts a missing root without throwing", 64, Signature(Path.Combine(root, "missing"), entry).Length);
            Equal("Signature accepts invalid root without throwing", 64, Signature(null, entry).Length);
            if (args.Length > 0) Actual(args[0]);
            Console.WriteLine("PASS " + passed + " facial-source checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Actual(string app)
    {
        string path = Path.Combine(app, "data/faces/index.json");
        string before = Hash(path);
        Dictionary<string, object> alias;
        Equal("Actual Roman facial alias validates", true, TauntFacialSources.TryReadAlias(app, "roman3", out alias));
        Equal("Actual Roman index phase is 156 frames", 156d, (double)alias["offset"]);
        var entry = Entry("roman3");
        string actual = Signature(app, entry);
        entry["face_offset"] = 18;
        Equal("Actual Roman legacy offset retains its distinct library face", false, actual == Signature(app, entry));
        entry = Entry("t37060");
        Equal("Actual Bobby's gender facial files are distinguished", false, Signature(app, entry) == Signature(app, entry, "female"));
        Equal("Actual source index remains unchanged", before, Hash(path));
    }
    private static Dictionary<string, object> Entry(string key = "motion") => new Dictionary<string, object> { { "key", key }, { "face", "facial_6368.npz" }, { "face_offset", 0 } };
    private static Dictionary<string, object> Record() => new Dictionary<string, object> { { "male", "male/motion.npz" }, { "female", "female/motion.npz" }, { "offset", 156 }, { "events", 3 } };
    private static string Signature(string app, Dictionary<string, object> entry, string gender = "male") => TauntFacialSources.Signature(app, entry, gender);
    private static void Index(string app, Dictionary<string, object> index) => Write(app, "data/faces/index.json", JsonSerializer.Serialize(index));
    private static void Write(string app, string relative, string text) { string path = Path.Combine(app, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private static void CopyTree(string source, string destination) { foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) { string path = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(file, path); } }
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Equal<T>(string name, T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", got " + actual); passed++; }
}
