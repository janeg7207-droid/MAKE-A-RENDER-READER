using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;

    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-render-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ulong hash = DiscoveryRenderImages.HashStem("Custom_Avery_Styles");
            string canonical = Path.Combine("UI", "Assets", "Roster", "TypeA");
            string picture = Write(Path.Combine(canonical, "Custom_Avery_Styles.dds"), Dds(23));
            Equal("A named DDS uses the exact canonical TypeA path hash", picture, DiscoveryRenderImages.Find(root, new[] { hash })[hash]);
            Equal("Canonical path hashing is case independent", hash, DiscoveryRenderImages.HashStem("CUSTOM_AVERY_STYLES"));
            Equal("Unrelated profile hashes do not receive an image", 0, DiscoveryRenderImages.Find(root, new[] { hash + 1 }).Count);
            Equal("A zero picture hash is never resolved", 0, DiscoveryRenderImages.Find(root, new[] { 0UL }).Count);
            Equal("Repeated profile hashes require only one image entry", 1, DiscoveryRenderImages.Find(root, new[] { hash, hash }).Count);
            Equal("No requested profiles skips indexing", 0, DiscoveryRenderImages.Find(root, new ulong[0]).Count);
            Equal("A missing data root remains an empty index", 0, DiscoveryRenderImages.Find(Path.Combine(root, "absent"), new[] { hash }).Count);
            Equal("A blank data root remains an empty index", 0, DiscoveryRenderImages.Find("", new[] { hash }).Count);

            string fallback = Write(Path.Combine("Exported renders", "Custom_Avery_Styles.dds"), Dds(30));
            Equal("A canonical location outranks conflicting copies elsewhere", picture, DiscoveryRenderImages.Find(root, new[] { hash })[hash]);
            File.Delete(picture);
            Equal("Named renders exported elsewhere remain discoverable", fallback, DiscoveryRenderImages.Find(root, new[] { hash })[hash]);
            picture = Write(Path.Combine(canonical, "Custom_Avery_Styles.dds"), Dds(23));
            string duplicate = Write(Path.Combine("Other", canonical, "CUSTOM_AVERY_STYLES.dds"), Dds(23));
            var same = DiscoveryRenderImages.Scan(root, new[] { hash });
            Equal("Identical canonical duplicates are accepted", 1, same.Images.Count);
            Equal("Identical duplicates are not conflicts", 0, same.Conflicts.Count);
            Equal("Duplicate choice remains deterministic", same.Images[hash], DiscoveryRenderImages.Find(root, new[] { hash })[hash]);
            File.WriteAllBytes(duplicate, Dds(24));
            var conflict = DiscoveryRenderImages.Scan(root, new[] { hash });
            Equal("Different equally preferred pictures cannot be silently selected", 0, conflict.Images.Count);
            Equal("The conflicting exact hash is reported", hash, conflict.Conflicts.Single());
            Equal("Conflicting canonical pictures do not fall back to a third image", false, conflict.Images.ContainsValue(fallback));
            File.Delete(duplicate);

            const ulong explicitHash = 0xfedcba9876543210UL;
            string explicitPath = Write("FEDCBA9876543210.DDS", Dds(16));
            Equal("A complete sixteen-hex-digit filename preserves the unsigned hash", explicitPath,
                DiscoveryRenderImages.Find(root, new[] { explicitHash })[explicitHash]);
            File.Delete(explicitPath);
            explicitPath = Write("0xfedcba9876543210.png", Png());
            Equal("An explicitly prefixed hexadecimal PNG is supported", explicitPath,
                DiscoveryRenderImages.Find(root, new[] { explicitHash })[explicitHash]);
            Write("6543210.dds", Dds(16));
            Write("122.dds", Dds(16));
            Equal("Short hex-looking stems never become an inferred profile hash", 0, DiscoveryRenderImages.Find(root, new[] { 0x6543210UL }).Count);
            Equal("A numeric filename never becomes a wrestler ID", 0, DiscoveryRenderImages.Find(root, new[] { 122UL }).Count);
            Equal("A numeric filename can still match an exact canonical path hash", 1,
                DiscoveryRenderImages.Find(root, new[] { DiscoveryRenderImages.HashStem("122") }).Count);
            Write("arbitrary.json", Dds(16));
            Equal("Other file extensions are never images", 0,
                DiscoveryRenderImages.Find(root, new[] { DiscoveryRenderImages.HashStem("arbitrary") }).Count);

            string png = Write("Added_Character.PNG", Png());
            ulong pngHash = DiscoveryRenderImages.HashStem("Added_Character");
            Equal("A new character's PNG can be loaded without a stock filename list", png,
                DiscoveryRenderImages.Find(root, new[] { pngHash })[pngHash]);
            string tex = Write("Actual_DDS.tex", Dds(20));
            ulong texHash = DiscoveryRenderImages.HashStem("Actual_DDS");
            Equal("DDS payloads exported with a tex extension are supported", tex,
                DiscoveryRenderImages.Find(root, new[] { texHash })[texHash]);
            Write("Opaque_Wrapper.tex", new byte[128]);
            Equal("An opaque tex wrapper is never passed off as DDS", 0,
                DiscoveryRenderImages.Find(root, new[] { DiscoveryRenderImages.HashStem("Opaque_Wrapper") }).Count);
            Invalid("Fake_DDS", new byte[128], ".dds");
            Invalid("Truncated_DDS", Dds(21).Take(127).ToArray(), ".dds");
            byte[] invalid = Dds(21); invalid[4] = 0;
            Invalid("Bad_DDS_Header", invalid, ".dds");
            invalid = Dds(21); invalid[76] = 0;
            Invalid("Bad_DDS_Pixel_Format_Header", invalid, ".dds");
            invalid = Dds(21); invalid[12] = 0;
            Invalid("Zero_DDS_Height", invalid, ".dds");
            Invalid("Fake_PNG", new byte[128], ".png");
            Invalid("Truncated_PNG", Png().Take(32).ToArray(), ".png");
            invalid = Png(); invalid[19] = 0;
            Invalid("Zero_PNG_Width", invalid, ".png");
            invalid = Png(); invalid[12] = (byte)'J';
            Invalid("Wrong_PNG_First_Chunk", invalid, ".png");
            Write(Path.Combine(canonical, "Actual_DDS.dds"), new byte[128]);
            Equal("An invalid canonical file cannot hide a valid exported picture", tex,
                DiscoveryRenderImages.Find(root, new[] { texHash })[texHash]);

            const string added = "Later_Mod_Character";
            ulong addedHash = DiscoveryRenderImages.HashStem(added);
            var before = DiscoveryRenderImages.Find(root, new[] { addedHash });
            string addedPath = Write(added + ".dds", Dds(99));
            Equal("Refresh discovers newly added render files", addedPath,
                DiscoveryRenderImages.Find(root, new[] { addedHash })[addedHash]);
            Equal("An earlier snapshot stays unchanged", 0, before.Count);
            File.Delete(addedPath);
            Equal("Refresh removes deleted render files", 0, DiscoveryRenderImages.Find(root, new[] { addedHash }).Count);
            Equal("Indexing does not modify source image contents", true, File.ReadAllBytes(picture).SequenceEqual(Dds(23)));
            Symlinks(hash);

            if (args.Length > 0)
            {
                var actual = DiscoveryRenderImages.Scan(args[0], new[] { hash, explicitHash });
                Equal("Uploaded Discovery data contains no pictures for synthetic profile hashes", 0, actual.Images.Count);
                Console.WriteLine("Uploaded image candidates: " + string.Join(", ", Directory.GetFiles(args[0], "*", SearchOption.AllDirectories)
                    .Where(path => new[] { ".dds", ".png", ".tex" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    .Select(path => path.Substring(args[0].Length).TrimStart(Path.DirectorySeparatorChar))));
            }
            Console.WriteLine("PASS " + passed + " Discovery render image checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Symlinks(ulong hash)
    {
        string outside = Path.Combine(Path.GetTempPath(), "gorilla-render-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string linkedFile = Path.Combine(root, "symlink-only.dds");
        string linkedDirectory = Path.Combine(root, "linked-external-renders");
        string loop = Path.Combine(root, "loop");
        try
        {
            string target = Path.Combine(outside, "symlink-only.dds");
            File.WriteAllBytes(target, Dds(12));
            ulong linkedHash = DiscoveryRenderImages.HashStem("symlink-only");
            Directory.CreateSymbolicLink(linkedDirectory, outside);
            Directory.CreateSymbolicLink(loop, root);
            File.CreateSymbolicLink(linkedFile, target);
            Equal("Directory and file symlinks cannot introduce an external render", 0,
                DiscoveryRenderImages.Find(root, new[] { linkedHash }).Count);
            Equal("A reparse-point root is not traversed", 0,
                DiscoveryRenderImages.Find(linkedDirectory, new[] { linkedHash }).Count);
            Equal("A cycle cannot prevent normal local pictures from resolving", 1,
                DiscoveryRenderImages.Find(root, new[] { hash }).Count);
        }
        finally
        {
            if (File.Exists(linkedFile)) File.Delete(linkedFile);
            if (Directory.Exists(linkedDirectory)) Directory.Delete(linkedDirectory);
            if (Directory.Exists(loop)) Directory.Delete(loop);
            Directory.Delete(outside, true);
        }
    }

    private static byte[] Dds(byte marker)
    {
        var bytes = new byte[132];
        bytes[0] = (byte)'D'; bytes[1] = (byte)'D'; bytes[2] = (byte)'S'; bytes[3] = (byte)' ';
        bytes[4] = 124; bytes[12] = 1; bytes[16] = 1; bytes[76] = 32; bytes[128] = marker;
        return bytes;
    }

    private static byte[] Png()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jAbcAAAAASUVORK5CYII=");
    }

    private static void Invalid(string name, byte[] bytes, string extension)
    {
        Write(name + extension, bytes);
        Equal("Reject invalid image " + name, 0, DiscoveryRenderImages.Find(root, new[] { DiscoveryRenderImages.HashStem(name) }).Count);
    }

    private static string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); return path;
    }

    private static void Equal<T>(string message, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + message);
    }
}
