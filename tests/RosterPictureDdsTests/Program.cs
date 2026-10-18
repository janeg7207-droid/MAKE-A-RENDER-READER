using System;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "gorilla-roster-dds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Distinct channels and alpha in every corner expose channel swaps,
            // premultiplication, row flipping and accidental alpha removal.
            byte[] pixels = { 11, 22, 33, 44, 55, 66, 77, 88, 99, 110, 121, 132, 143, 154, 165, 176 };
            string path = Path.Combine(root, "nested", "asymmetric.dds");
            RosterPictureDds.Write(path, 2, 2, pixels);
            byte[] file = File.ReadAllBytes(path);
            Equal("DDS includes its full header and pixel buffer", 128 + pixels.Length, file.Length);
            Equal("DDS magic is present", 0x20534444u, UInt(file, 0));
            Equal("DDS header size is 124 bytes", 124u, UInt(file, 4));
            Equal("DDS declares its pitch and pixel format", 0x100fu, UInt(file, 8));
            Equal("Height is preserved", 2u, UInt(file, 12));
            Equal("Width is preserved", 2u, UInt(file, 16));
            Equal("Row pitch is width times four", 8u, UInt(file, 20));
            Equal("Depth remains zero for a 2D picture", 0u, UInt(file, 24));
            Equal("DDS has no mipmaps", 0u, UInt(file, 28));
            True("Reserved header values are zero", file.Skip(32).Take(44).All(value => value == 0));
            Equal("Pixel format header size is 32 bytes", 32u, UInt(file, 76));
            Equal("Pixel format includes straight alpha", 0x41u, UInt(file, 80));
            Equal("Uncompressed image uses no FourCC", 0u, UInt(file, 84));
            Equal("Each pixel uses 32 bits", 32u, UInt(file, 88));
            Equal("Red mask matches BGRA input", 0x00ff0000u, UInt(file, 92));
            Equal("Green mask matches BGRA input", 0x0000ff00u, UInt(file, 96));
            Equal("Blue mask matches BGRA input", 0x000000ffu, UInt(file, 100));
            Equal("Alpha mask preserves all alpha bits", 0xff000000u, UInt(file, 104));
            Equal("Texture capability is present", 0x1000u, UInt(file, 108));
            True("Remaining capabilities are zero", file.Skip(112).Take(16).All(value => value == 0));
            True("Pixels preserve channels, order and straight alpha", pixels.SequenceEqual(file.Skip(128)));
            True("Input pixels are not mutated", pixels.SequenceEqual(new byte[] { 11, 22, 33, 44, 55, 66, 77, 88, 99, 110, 121, 132, 143, 154, 165, 176 }));
            True("Successful writes leave no temporary file", !Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Any());

            // An existing image is replaced only after a complete new image is written.
            byte[] replacement = { 1, 2, 3, 0, 4, 5, 6, 255, 7, 8, 9, 127 };
            RosterPictureDds.Write(path, 3, 1, replacement);
            byte[] replaced = File.ReadAllBytes(path);
            Equal("Replacement updates the dimensions", 3u, UInt(replaced, 16));
            Equal("Replacement removes old trailing pixels", 140, replaced.Length);
            True("Replacement keeps transparent and opaque pixels", replacement.SequenceEqual(replaced.Skip(128)));
            True("Replacement leaves no temporary file", !Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Any());

            Throws<ArgumentException>("Incomplete pixel rows are rejected", () => RosterPictureDds.Write(path, 3, 1, new byte[11]));
            Throws<ArgumentException>("Surplus pixel bytes are rejected", () => RosterPictureDds.Write(path, 3, 1, new byte[13]));
            Throws<ArgumentNullException>("Null pixels are rejected", () => RosterPictureDds.Write(path, 3, 1, null));
            Throws<ArgumentOutOfRangeException>("Zero width is rejected", () => RosterPictureDds.Write(path, 0, 1, new byte[0]));
            Throws<ArgumentOutOfRangeException>("Negative height is rejected", () => RosterPictureDds.Write(path, 1, -1, new byte[0]));
            Throws<ArgumentOutOfRangeException>("Excessive width is rejected", () => RosterPictureDds.Write(path, RosterPictureDds.MaximumDimension + 1, 1, new byte[0]));
            Throws<ArgumentOutOfRangeException>("Excessive height is rejected", () => RosterPictureDds.Write(path, 1, RosterPictureDds.MaximumDimension + 1, new byte[0]));
            Throws<ArgumentOutOfRangeException>("Integer overflow dimensions are rejected", () => RosterPictureDds.Write(path, int.MaxValue, int.MaxValue, new byte[0]));
            Throws<ArgumentOutOfRangeException>("Oversized images fail before allocating pixels", () => RosterPictureDds.Write(path, 4097, 4096, new byte[0]));
            Throws<ArgumentNullException>("Null paths are rejected", () => RosterPictureDds.Write(null, 1, 1, new byte[4]));
            Throws<ArgumentException>("Blank paths are rejected", () => RosterPictureDds.Write(" ", 1, 1, new byte[4]));
            True("Invalid input preserves the existing picture", replaced.SequenceEqual(File.ReadAllBytes(path)));
            True("Invalid input leaves no temporary file", !Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Any());

            string missing = Path.Combine(root, "not-created", "invalid.dds");
            Throws<ArgumentException>("Validation runs before creating destination folders", () => RosterPictureDds.Write(missing, 1, 1, new byte[3]));
            True("Invalid input does not create destination folders", !Directory.Exists(Path.GetDirectoryName(missing)));
            string directory = Path.Combine(root, "directory.dds");
            Directory.CreateDirectory(directory);
            Throws<IOException>("A directory cannot be overwritten as a picture", () => RosterPictureDds.Write(directory, 1, 1, new byte[4]));
            True("A failed atomic move leaves the directory intact", Directory.Exists(directory));
            True("A failed atomic move removes its temporary file", !Directory.GetFiles(root, "*.tmp").Any());

            // Optional fixtures allow an independent image decoder and the actual
            // packaged engine to inspect the writer's output after this process exits.
            if (args.Length > 0)
            {
                string fixtures = Path.GetFullPath(args[0]);
                RosterPictureDds.Write(Path.Combine(fixtures, "asymmetric.dds"), 2, 2, pixels);
                byte[] hair = new byte[80 * 100 * 4];
                for (int index = 0; index < hair.Length; index += 4)
                {
                    hair[index] = 30;
                    hair[index + 1] = 45;
                    hair[index + 2] = 60;
                    hair[index + 3] = 255;
                }
                RosterPictureDds.Write(Path.Combine(fixtures, "hair-source.dds"), 80, 100, hair);
                Console.WriteLine("DDS fixtures: " + fixtures);
            }
            Console.WriteLine("PASS " + passed + " roster picture DDS checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static uint UInt(byte[] bytes, int offset) { return BitConverter.ToUInt32(bytes, offset); }
    private static void True(string label, bool actual) { Equal(label, true, actual); }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++;
        Console.WriteLine("PASS " + label);
    }
    private static void Throws<T>(string label, Action action) where T : Exception
    {
        try { action(); }
        catch (T) { passed++; Console.WriteLine("PASS " + label); return; }
        throw new Exception(label + ": expected " + typeof(T).Name);
    }
}
