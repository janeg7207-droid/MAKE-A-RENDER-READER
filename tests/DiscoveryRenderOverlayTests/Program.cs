using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;
    private const ulong Custom = 0x123456789abcdef0UL;
    private const ulong Other = 0xfedcba9876543210UL;

    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-render-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string table = Write("source/CharProfileTable.jsfb", Build(new Row(122, Custom, Other, Custom, 0),
                new Row(9000, Custom), new Row(9001), new Row(0, Custom)));
            string dds = Write("source/actual.dds", Dds(18));
            byte[] source = File.ReadAllBytes(table);
            var images = new Dictionary<ulong, string> { { Custom, dds }, { 77, "unused-missing.dds" } };
            string cache = Path.Combine(root, "cache");
            var result = DiscoveryRenderOverlay.Prepare(cache, table, images);
            Equal("The compatibility root is explicit", cache, result.Root);
            Equal("The table is copied to the native engine's expected path", Path.Combine(cache, "Roster", "CharProfileTable.jsfb"), result.TablePath);
            Equal("The source table path is retained", table, result.OriginalTable);
            Equal("Only a supplied image referenced by an actual profile creates an alias", 1, result.OriginalToAlias.Count);
            ulong alias = result.OriginalToAlias[Custom];
            Equal("The alias filename is deterministic and contains the entire source hash", "discovery_123456789abcdef0", result.AliasStems[Custom]);
            Equal("The alias hash matches the canonical TypeA picture name", DiscoveryRenderImages.HashStem(result.AliasStems[Custom]), alias);
            Equal("Installation can reverse every temporary alias to the real original picture hash", Custom, result.AliasToOriginal[alias]);
            var links = DiscoveryRenderLinks.Read(result.TablePath);
            Equal("All attires sharing a picture hash use the same compatibility picture", alias, links[122][0]);
            Equal("Unresolved picture hashes remain unchanged", Other, links[122][1]);
            Equal("The same picture on another logical profile follows its exact link", alias, links[9000].Single());
            Equal("Profiles without pictures remain unchanged", 0, links[9001].Count);
            Equal("Only the three actual positive-profile picture fields are patched", 3, result.PatchedFields);
            Equal("Prepared DDS bytes are copied unchanged", true, File.ReadAllBytes(result.ImagePaths[Custom]).SequenceEqual(Dds(18)));
            Equal("The source table is never modified", true, File.ReadAllBytes(table).SequenceEqual(source));
            Equal("The source image is never modified", true, File.ReadAllBytes(dds).SequenceEqual(Dds(18)));
            Equal("The FlatBuffer length remains unchanged", source.Length, new FileInfo(result.TablePath).Length);
            Equal("Only exact positive-profile picture hash fields change", true, OnlyPictureFieldsChanged(source, File.ReadAllBytes(result.TablePath), Custom, alias));
            Equal("A plain marker lets later processes recover the original roster table", table,
                File.ReadAllText(Path.Combine(cache, "original-roster.txt")));
            Equal("The exact temporary keys are recorded before the table is used", Custom.ToString("X16") + " " + alias.ToString("X16"),
                File.ReadAllText(Path.Combine(cache, "picture-aliases.txt")));
            Equal("Every generated directory is real", true, new[] { cache, Path.Combine(cache, "Roster"),
                Path.Combine(cache, "UI", "Assets", "Roster", "TypeA") }.All(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0));
            Equal("The native image index resolves the prepared alias by its real filename", result.ImagePaths[Custom],
                DiscoveryRenderImages.Find(cache, new[] { alias })[alias]);

            DateTime sentinel = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            foreach (string path in new[] { result.TablePath, result.ImagePaths[Custom], Path.Combine(cache, "original-roster.txt") }) File.SetLastWriteTimeUtc(path, sentinel);
            var repeated = DiscoveryRenderOverlay.Prepare(cache, table, images);
            Equal("An unchanged refresh retains the same alias", alias, repeated.OriginalToAlias[Custom]);
            Equal("An unchanged refresh does not rewrite the native table", sentinel, File.GetLastWriteTimeUtc(result.TablePath));
            Equal("An unchanged refresh does not rewrite the image", sentinel, File.GetLastWriteTimeUtc(result.ImagePaths[Custom]));
            Equal("An unchanged refresh does not rewrite the original-roster marker", sentinel, File.GetLastWriteTimeUtc(Path.Combine(cache, "original-roster.txt")));
            File.WriteAllBytes(dds, Dds(19));
            DiscoveryRenderOverlay.Prepare(cache, table, images);
            Equal("A changed modded render refreshes the compatibility image", true, File.ReadAllBytes(result.ImagePaths[Custom]).SequenceEqual(Dds(19)));
            File.WriteAllBytes(table, Build(new Row(122, Other), new Row(9999, Custom)));
            var grown = DiscoveryRenderOverlay.Prepare(cache, table, images);
            var newLinks = DiscoveryRenderLinks.Read(grown.TablePath);
            Equal("Newly added roster profiles receive their own exact render link", alias, newLinks[9999].Single());
            Equal("Changed roster profiles lose obsolete render associations", Other, newLinks[122].Single());
            Equal("Removed roster profiles disappear from the compatibility table", false, newLinks.ContainsKey(9000));
            var empty = DiscoveryRenderOverlay.Prepare(cache, table, new Dictionary<ulong, string>());
            Equal("Removing a picture removes its temporary alias from the current table", Custom, DiscoveryRenderLinks.Read(empty.TablePath)[9999].Single());
            Equal("No supplied picture means no aliases", 0, empty.OriginalToAlias.Count);
            Equal("A context without decoded pictures has no intermediate keys to install", "", File.ReadAllText(Path.Combine(cache, "picture-aliases.txt")));

            string collisionTable = Write("source/collision.jsfb", Build(new Row(1, Custom, DiscoveryRenderImages.HashStem("discovery_123456789abcdef0"))));
            var collision = DiscoveryRenderOverlay.Prepare(Path.Combine(root, "collision"), collisionTable, images);
            Equal("Alias generation avoids colliding with another real profile picture", "discovery_123456789abcdef0_1", collision.AliasStems[Custom]);
            Equal("The colliding real hash is not altered", DiscoveryRenderImages.HashStem("discovery_123456789abcdef0"),
                DiscoveryRenderLinks.Read(collision.TablePath)[1][1]);

            string bad = Write("source/not-dds.dds", new byte[128]);
            Throws<InvalidDataException>("A prepared non-DDS cannot reach the native engine", () => DiscoveryRenderOverlay.Prepare(
                Path.Combine(root, "bad-image"), table, new Dictionary<ulong, string> { { Custom, bad } }));
            Equal("Image validation happens before creating an output root", false, Directory.Exists(Path.Combine(root, "bad-image")));
            string invalidTable = Write("source/invalid.jsfb", new byte[128]);
            Throws<InvalidDataException>("Invalid profiles fail before any writes", () => DiscoveryRenderOverlay.Prepare(
                Path.Combine(root, "bad-table"), invalidTable, images));
            Equal("Invalid profiles do not create a compatibility cache", false, Directory.Exists(Path.Combine(root, "bad-table")));
            Throws<IOException>("The source Discovery root cannot be used as its own writable overlay", () => DiscoveryRenderOverlay.Prepare(
                Path.GetDirectoryName(table), table, images));
            string ownImage = Write("contains-source/prepared.dds", Dds(40));
            Throws<IOException>("Prepared source images must remain outside the writable overlay", () => DiscoveryRenderOverlay.Prepare(
                Path.Combine(root, "contains-source"), table, new Dictionary<ulong, string> { { Custom, ownImage } }));
            string blockedRoot = Write("file-root", new byte[] { 1 });
            Throws<IOException>("A cache root occupied by a file is rejected", () => DiscoveryRenderOverlay.Prepare(blockedRoot, table, images));
            Symlinks(table, images);

            if (args.Length > 0)
            {
                byte[] before = File.ReadAllBytes(args[0]);
                var actualLinks = DiscoveryRenderLinks.Read(args[0]);
                ulong actual = actualLinks.Values.SelectMany(values => values).First();
                var actualOverlay = DiscoveryRenderOverlay.Prepare(Path.Combine(root, "actual"), args[0],
                    new Dictionary<ulong, string> { { actual, dds } });
                Equal("The uploaded table retains all real profiles", actualLinks.Count, DiscoveryRenderLinks.Read(actualOverlay.TablePath).Count);
                Equal("The uploaded source table bytes remain untouched", true, File.ReadAllBytes(args[0]).SequenceEqual(before));
                Equal("An exact uploaded profile image field can be adapted", true, actualOverlay.PatchedFields > 0);
                Equal("The actual uploaded table keeps the same byte length", before.Length, new FileInfo(actualOverlay.TablePath).Length);
                if (args.Length > 1) ExportNativeFixture(args[1], args[0], before, actualLinks);
            }
            Console.WriteLine("PASS " + passed + " Discovery render overlay checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ExportNativeFixture(string output, string table, byte[] original, Dictionary<int, List<ulong>> links)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Use a new output directory for the native roster picture fixture.");
        string inputRoot = output + "-inputs";
        if (Directory.Exists(inputRoot) || File.Exists(inputRoot)) throw new IOException("The native roster picture fixture's input directory already exists.");
        string input = Path.Combine(inputRoot, "synthetic-brown-roster-picture.dds");
        const int width = 256, height = 256;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20; pixels[i + 1] = 40; pixels[i + 2] = 60; pixels[i + 3] = 255;
        }
        RosterPictureDds.Write(input, width, height, pixels);
        ulong actual = links[122].First();
        var fixture = DiscoveryRenderOverlay.Prepare(output, table, new Dictionary<ulong, string> { { actual, input } });
        File.WriteAllText(Path.Combine(output, "fixture.json"), JsonSerializer.Serialize(new
        {
            id = 122,
            original_hash = actual.ToString("X16"),
            alias_hash = fixture.OriginalToAlias[actual].ToString("X16"),
            source_table = Path.GetFullPath(table),
            table = fixture.TablePath,
            image = fixture.ImagePaths[actual],
            source_image = input,
            width,
            height,
            note = "Synthetic brown DDS for engine lookup tests; not the user's Avery Styles render."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Equal("The exported native fixture uses the actual Avery Styles logical profile 122", fixture.OriginalToAlias[actual],
            DiscoveryRenderLinks.Read(fixture.TablePath)[122].First());
        Equal("Exporting a native fixture keeps the actual uploaded table unchanged", true, File.ReadAllBytes(table).SequenceEqual(original));
        Equal("The exported source DDS remains unchanged by the overlay", true, File.ReadAllBytes(input).SequenceEqual(File.ReadAllBytes(fixture.ImagePaths[actual])));
        Console.WriteLine("Native picture fixture: " + output);
    }

    private static void Symlinks(string table, Dictionary<ulong, string> images)
    {
        string outside = Path.Combine(root, "symlink-target"); Directory.CreateDirectory(outside);
        string sourceMarker = Write("symlink-target/sentinel", new byte[] { 71 });
        string linked = Path.Combine(root, "linked-root");
        Directory.CreateSymbolicLink(linked, outside);
        Throws<IOException>("A linked output root is rejected", () => DiscoveryRenderOverlay.Prepare(linked, table, images));
        Throws<IOException>("A linked output ancestor is rejected", () => DiscoveryRenderOverlay.Prepare(Path.Combine(linked, "child"), table, images));
        Directory.Delete(linked);
        string cache = Path.Combine(root, "linked-roster"); Directory.CreateDirectory(cache);
        linked = Path.Combine(cache, "Roster"); Directory.CreateSymbolicLink(linked, outside);
        Throws<IOException>("An existing Roster junction is never followed", () => DiscoveryRenderOverlay.Prepare(cache, table, images));
        Directory.Delete(linked);
        cache = Path.Combine(root, "linked-table"); Directory.CreateDirectory(Path.Combine(cache, "Roster"));
        linked = Path.Combine(cache, "Roster", "CharProfileTable.jsfb"); File.CreateSymbolicLink(linked, table);
        byte[] before = File.ReadAllBytes(table);
        Throws<IOException>("A linked output table cannot overwrite the real source", () => DiscoveryRenderOverlay.Prepare(cache, table, images));
        Equal("The source behind a rejected table link remains untouched", true, File.ReadAllBytes(table).SequenceEqual(before));
        File.Delete(linked);
        linked = Path.Combine(cache, "original-roster.txt"); File.CreateSymbolicLink(linked, sourceMarker);
        Throws<IOException>("A linked metadata marker cannot overwrite an external file", () => DiscoveryRenderOverlay.Prepare(cache, table, images));
        File.Delete(linked);
        Directory.CreateDirectory(Path.Combine(cache, "UI", "Assets", "Roster", "TypeA"));
        linked = Path.Combine(cache, "UI", "Assets", "Roster", "TypeA", "discovery_123456789abcdef0.dds");
        File.CreateSymbolicLink(linked, sourceMarker);
        Throws<IOException>("A linked target image cannot overwrite an external file", () => DiscoveryRenderOverlay.Prepare(cache, table, images));
        Equal("The image link's source bytes remain untouched", (byte)71, File.ReadAllBytes(sourceMarker).Single());
        File.Delete(linked);
    }

    private static bool OnlyPictureFieldsChanged(byte[] before, byte[] after, ulong original, ulong alias)
    {
        // This fixture has unique positive profiles and all field data is eight-byte aligned
        // within each attire. Locate the actual typed fields, never arbitrary byte sequences.
        var file = new FlatBufferFile(before, "Char"); var allowed = new HashSet<int>();
        foreach (int wrapper in file.Tables(file.Root, 0))
        {
            int field = file.Field(wrapper, 1); int profile = field + (int)file.U32(field);
            if (file.U16(file.Field(profile, 2)) == 0) continue;
            foreach (int attire in file.Tables(profile, 4))
            {
                int p = file.Field(attire, 6);
                if (file.U64(p) == original)
                {
                    if (BitConverter.ToUInt64(after, p) != alias) return false;
                    for (int i = 0; i < 8; i++) allowed.Add(p + i);
                }
            }
        }
        return before.Length == after.Length && Enumerable.Range(0, before.Length).All(i => allowed.Contains(i) || before[i] == after[i]);
    }

    private static byte[] Build(params Row[] rows)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
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
                int[] attires = new int[rows[i].Hashes.Length];
                for (int a = 0; a < attires.Length; a++) { attires[a] = (int)stream.Position; writer.Write(0); }
                Patch(writer, profile + 8, vector - profile - 8);
                for (int a = 0; a < attires.Length; a++)
                {
                    int target = Table(writer, 16, 0, 0, 0, 0, 0, 0, 8); Patch(writer, attires[a], target - attires[a]);
                    end = stream.Position; stream.Position = target + 8; writer.Write(rows[i].Hashes[a]); stream.Position = end;
                }
            }
            return stream.ToArray();
        }
    }

    private static int Table(BinaryWriter writer, int size, params int[] fields)
    {
        int vtable = (int)writer.BaseStream.Position;
        writer.Write((ushort)(4 + fields.Length * 2)); writer.Write((ushort)size);
        foreach (int field in fields) writer.Write((ushort)field);
        int table = (int)writer.BaseStream.Position; writer.Write(table - vtable); writer.Write(new byte[size - 4]); return table;
    }

    private static void Patch(BinaryWriter writer, int position, int value)
    {
        long end = writer.BaseStream.Position; writer.BaseStream.Position = position; writer.Write(value); writer.BaseStream.Position = end;
    }

    private static byte[] Dds(byte marker)
    {
        var bytes = new byte[132]; bytes[0] = (byte)'D'; bytes[1] = (byte)'D'; bytes[2] = (byte)'S'; bytes[3] = (byte)' ';
        bytes[4] = 124; bytes[12] = 1; bytes[16] = 1; bytes[76] = 32; bytes[128] = marker; return bytes;
    }

    private static string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); return path;
    }

    private static void Equal<T>(string message, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + message);
    }

    private static void Throws<T>(string message, Action action) where T : Exception
    {
        try { action(); } catch (T) { passed++; Console.WriteLine("PASS " + message); return; }
        throw new Exception(message + ": expected " + typeof(T).Name);
    }

    private sealed class Row
    {
        public readonly int Id; public readonly ulong[] Hashes;
        public Row(int id, params ulong[] hashes) { Id = id; Hashes = hashes; }
    }
}
