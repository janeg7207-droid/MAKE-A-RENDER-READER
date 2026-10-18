using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "gorilla-dds-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] pixels = { 11, 22, 33, 0, 55, 66, 77, 88, 99, 110, 121, 132, 143, 154, 165, 255 };
            string file = Path.Combine(root, "roster.dds");
            RosterPictureDds.Write(file, 2, 2, pixels);
            byte[] dds = File.ReadAllBytes(file);
            var image = RosterPicturePreview.Decode(file);
            Equal("DDS preview width", 2, image.Width);
            Equal("DDS preview height", 2, image.Height);
            True("BGRA rows and straight transparent alpha are preserved", pixels.SequenceEqual(image.Bgra));
            True("DDS decoding leaves its source intact", dds.SequenceEqual(File.ReadAllBytes(file)));
            True("Byte-buffer decoder agrees", pixels.SequenceEqual(RosterPicturePreview.Decode(dds).Bgra));

            Reject("Short headers", new byte[127]);
            Bad("Incorrect magic", dds, 0, 0);
            Bad("Incorrect header size", dds, 4, 123);
            Bad("Incorrect pixel format size", dds, 76, 31);
            Bad("Zero width", dds, 16, 0);
            Bad("Zero height", dds, 12, 0);
            Bad("Overflowing width", dds, 16, uint.MaxValue);
            Bad("Excessive height", dds, 12, 16385);
            byte[] huge = Change(Change(dds, 16, 4097), 12, 4096);
            Reject("Oversized output is rejected before allocation", huge);
            Bad("Volume textures", dds, 24, 2);
            Bad("Cube maps", dds, 112, 0x200);
            Bad("Oversized row pitch", dds, 20, 0x7fffffff);
            Bad("Short row pitch", dds, 20, 7);
            Bad("Unsupported pixel bit size", dds, 88, 7);
            Bad("Missing red mask", dds, 92, 0);
            Bad("Overlapping color masks", dds, 92, 0xff);
            Bad("Non-contiguous color masks", dds, 92, 0x00f70000);
            Bad("Overlapping alpha mask", dds, 104, 0xff);
            Reject("Truncated raw pixels", dds.Take(dds.Length - 1).ToArray());
            Throws<ArgumentNullException>("Null file", () => RosterPicturePreview.Decode((string)null));
            Throws<ArgumentNullException>("Null buffer", () => RosterPicturePreview.Decode((byte[])null));
            Throws<ArgumentException>("Empty filename", () => RosterPicturePreview.Decode(" "));

            byte[] noAlpha = Change(dds, 104, 0);
            True("Missing alpha is fully opaque", RosterPicturePreview.Decode(noAlpha).Bgra.Where((value, i) => i % 4 == 3).All(value => value == 255));
            byte[] reordered = Change(Change(dds, 92, 0xff), 100, 0xff0000);
            var swapped = RosterPicturePreview.Decode(reordered);
            Equal("RGBA red mask is applied", pixels[0], swapped.Bgra[2]);
            Equal("RGBA blue mask is applied", pixels[2], swapped.Bgra[0]);
            byte[] padded = dds.Take(128).Concat(pixels.Take(8)).Concat(new byte[4]).Concat(pixels.Skip(8)).Concat(new byte[4]).ToArray();
            padded = Change(padded, 20, 12);
            True("Padded rows do not shift pixels", pixels.SequenceEqual(RosterPicturePreview.Decode(padded).Bgra));
            byte[] mipCount = Change(dds, 28, uint.MaxValue);
            True("Unused uncompressed mip count is never allocated", pixels.SequenceEqual(RosterPicturePreview.Decode(mipCount).Bgra));

            byte[] compressed = Dxt1();
            var red = RosterPicturePreview.Decode(compressed);
            Equal("DXT1 width", 4, red.Width);
            Equal("DXT1 height", 4, red.Height);
            True("DXT1 output is BGRA red", Enumerable.Range(0, 16).All(i => red.Bgra[i * 4] == 0 && red.Bgra[i * 4 + 1] == 0 && red.Bgra[i * 4 + 2] == 255 && red.Bgra[i * 4 + 3] == 255));
            True("Unused compressed mip counts are bounded", red.Bgra.SequenceEqual(RosterPicturePreview.Decode(Change(compressed, 28, uint.MaxValue)).Bgra));
            Reject("Truncated block data", compressed.Take(compressed.Length - 1).ToArray());
            Bad("Unsupported compression", compressed, 84, 0x32545844);
            byte[] transparent = (byte[])compressed.Clone();
            transparent[128] = transparent[129] = transparent[130] = transparent[131] = 0;
            for (int i = 132; i < 136; i++) transparent[i] = 255;
            True("DXT1 fully transparent pixels retain zero alpha", RosterPicturePreview.Decode(transparent).Bgra.All(value => value == 0));
            var odd = RosterPicturePreview.Decode(Change(Change(compressed, 16, 1), 12, 1));
            Equal("Compressed partial blocks are cropped to logical dimensions", 4, odd.Bgra.Length);
            True("Compressed partial block keeps its actual pixel", odd.Bgra.SequenceEqual(new byte[] { 0, 0, 255, 255 }));

            byte[] dx10 = Dx10Picture(99, new byte[16]);
            True("Reserved BC7 mode decodes as transparent black", RosterPicturePreview.Decode(dx10).Bgra.All(value => value == 0));
            Reject("Truncated DX10 header", dx10.Take(147).ToArray());
            Reject("Truncated BC7 data", dx10.Take(dx10.Length - 1).ToArray());
            Bad("DX10 volume textures", dx10, 132, 4);
            Bad("DX10 arrays", dx10, 140, 2);
            Bad("DX10 cube maps", dx10, 136, 4);
            Bad("DX10 unsupported HDR formats", dx10, 128, 95);
            True("Unused BC7 mip counts are bounded", RosterPicturePreview.Decode(Change(dx10, 28, uint.MaxValue)).Bgra.All(value => value == 0));

            byte[] rgba = Dx10Picture(28, new byte[] { 11, 22, 33, 44 }, 1, 1);
            True("DX10 RGBA channels and alpha are applied", RosterPicturePreview.Decode(rgba).Bgra.SequenceEqual(new byte[] { 33, 22, 11, 44 }));
            byte[] bgra = Dx10Picture(87, new byte[] { 11, 22, 33, 44 }, 1, 1);
            True("DX10 BGRA channels and alpha are applied", RosterPicturePreview.Decode(bgra).Bgra.SequenceEqual(new byte[] { 11, 22, 33, 44 }));
            True("DX10 BGRX ignores unused alpha bytes", RosterPicturePreview.Decode(Change(bgra, 128, 88)).Bgra.SequenceEqual(new byte[] { 11, 22, 33, 255 }));

            if (args.Length > 0) CompareFixtures(args[0]);
            Console.WriteLine("PASS " + passed + " roster picture preview checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CompareFixtures(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.dds");
        True("Independent DDS fixtures are present", files.Length >= 6);
        foreach (string path in files.OrderBy(path => path, StringComparer.Ordinal))
        {
            byte[] reference = File.ReadAllBytes(path + ".bgra");
            var timer = Stopwatch.StartNew();
            var image = RosterPicturePreview.Decode(path);
            timer.Stop();
            Equal(Path.GetFileName(path) + " reference pixel count", reference.Length, image.Bgra.Length);
            int maxError = 0;
            for (int i = 0; i < reference.Length; i++) maxError = Math.Max(maxError, Math.Abs(reference[i] - image.Bgra[i]));
            // DXT implementations differ by one RGB code value in endpoint interpolation.
            uint format = BitConverter.ToUInt32(File.ReadAllBytes(path), 84);
            int allowed = format == 0x31545844 || format == 0x33545844 || format == 0x35545844 ? 1 : 0;
            True(Path.GetFileName(path) + " independent Pillow comparison (max error " + maxError + ")", maxError <= allowed);
            True(Path.GetFileName(path) + " preserves reference alpha exactly", Enumerable.Range(0, reference.Length / 4).All(i => reference[i * 4 + 3] == image.Bgra[i * 4 + 3]));
            Console.WriteLine("Decoded " + Path.GetFileName(path) + ": " + image.Width + "x" + image.Height + " in " + timer.ElapsedMilliseconds + " ms");
        }
        var alpha = RosterPicturePreview.Decode(Path.Combine(directory, "bc7-all-modes.dds"));
        True("BC7 reference contains transparent pixels", alpha.Bgra.Where((b, i) => i % 4 == 3).Any(b => b == 0));
        True("BC7 reference contains partial alpha", alpha.Bgra.Where((b, i) => i % 4 == 3).Any(b => b > 0 && b < 255));
        True("BC7 reference contains opaque pixels", alpha.Bgra.Where((b, i) => i % 4 == 3).Any(b => b == 255));
    }

    private static byte[] Dxt1()
    {
        byte[] bytes = new byte[136];
        Put(bytes, 0, 0x20534444); Put(bytes, 4, 124); Put(bytes, 8, 0x81007);
        Put(bytes, 12, 4); Put(bytes, 16, 4); Put(bytes, 20, 8);
        Put(bytes, 76, 32); Put(bytes, 80, 4); Put(bytes, 84, 0x31545844); Put(bytes, 108, 0x1000);
        bytes[128] = 0; bytes[129] = 0xf8;
        return bytes;
    }

    private static byte[] Dx10Picture(uint format, byte[] pixels, int width = 4, int height = 4)
    {
        byte[] bytes = new byte[148 + pixels.Length];
        Buffer.BlockCopy(Dxt1(), 0, bytes, 0, 128);
        Put(bytes, 12, (uint)height); Put(bytes, 16, (uint)width); Put(bytes, 84, 0x30315844);
        Put(bytes, 128, format); Put(bytes, 132, 3); Put(bytes, 140, 1);
        Buffer.BlockCopy(pixels, 0, bytes, 148, pixels.Length);
        return bytes;
    }

    private static byte[] Change(byte[] bytes, int offset, uint value)
    {
        byte[] result = (byte[])bytes.Clone(); Put(result, offset, value); return result;
    }
    private static void Put(byte[] bytes, int offset, uint value)
    {
        for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (i * 8));
    }
    private static void Bad(string label, byte[] bytes, int offset, uint value) { Reject(label, Change(bytes, offset, value)); }
    private static void Reject(string label, byte[] bytes) { Throws<InvalidDataException>(label, () => RosterPicturePreview.Decode(bytes)); }
    private static void True(string label, bool actual) { Equal(label, true, actual); }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
    private static void Throws<T>(string label, Action action) where T : Exception
    {
        try { action(); }
        catch (T) { passed++; Console.WriteLine("PASS " + label); return; }
        throw new Exception(label + ": expected " + typeof(T).Name);
    }
}
