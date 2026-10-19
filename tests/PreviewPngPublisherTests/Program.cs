using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static void Main(string[] arguments)
    {
        string root = Path.Combine(Path.GetTempPath(), "gorilla-png-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] pixels = {
                10,20,30,0, 40,50,60,128, 70,80,90,255, 231,232,233,234,
                100,110,120,1, 130,140,150,254, 160,170,180,64, 235,236,237,238
            };
            byte[] original = (byte[])pixels.Clone();
            string full = PreviewPngPublisher.Publish(root, "PAC pose", pixels, 3, 2, 16);
            Image image = Decode(full);
            Check("A published PNG is an absolute path in the requested folder", Path.IsPathRooted(full) && Path.GetDirectoryName(full) == root);
            Check("The output is a single ordinary non-interlaced eight-bit RGBA image", image.Width == 3 && image.Height == 2);
            Check("Straight BGRA channels become RGBA without changing colors or alpha", image.Rgba.SequenceEqual(new byte[] {
                30,20,10,0, 60,50,40,128, 90,80,70,255,
                120,110,100,1, 150,140,130,254, 180,170,160,64
            }));
            Check("Fully transparent pixels retain their original RGB values", image.Rgba.Take(4).SequenceEqual(new byte[] {30,20,10,0}));
            Check("Partially transparent pixels remain straight alpha", image.Rgba.Skip(4).Take(4).SequenceEqual(new byte[] {60,50,40,128}));
            Check("The source buffer including row padding is unchanged", pixels.SequenceEqual(original));

            string crop = PreviewPngPublisher.Publish(root, "Cropped pose", pixels, 3, 2, 16, 1, 0, 2, 2);
            image = Decode(crop);
            Check("A rectangular crop has exactly the requested dimensions", image.Width == 2 && image.Height == 2);
            Check("A rectangular crop contains only the requested pose pixels", image.Rgba.SequenceEqual(new byte[] {
                60,50,40,128, 90,80,70,255, 150,140,130,254, 180,170,160,64
            }));
            image = Decode(PreviewPngPublisher.Publish(root, "Remaining", pixels, 3, 2, 16, 1, 1));
            Check("Omitted crop sizes use the remaining frame after the crop origin", image.Width == 2 && image.Height == 1 && image.Rgba.SequenceEqual(new byte[] {150,140,130,254,180,170,160,64}));
            byte[] minimal = pixels.Take(28).ToArray();
            Check("The last row need not contain unused stride padding", Decode(PreviewPngPublisher.Publish(root, "Minimal buffer", minimal, 3, 2, 16)).Rgba.Length == 24);
            Check("Cropping never modifies the input", pixels.SequenceEqual(original));

            byte[] large = new byte[640 * 480 * 4];
            new Random(762614).NextBytes(large);
            image = Decode(PreviewPngPublisher.Publish(root, "Large image", large, 640, 480, 640 * 4));
            byte[] expected = new byte[large.Length];
            for (int offset = 0; offset < large.Length; offset += 4)
            {
                expected[offset] = large[offset + 2]; expected[offset + 1] = large[offset + 1];
                expected[offset + 2] = large[offset]; expected[offset + 3] = large[offset + 3];
            }
            Check("A multi-chunk PNG independently decompresses to every original pixel", image.Rgba.SequenceEqual(expected));
            Check("Large compressed data is streamed across multiple PNG chunks", image.IdatChunks > 1);

            string existing = Path.Combine(root, "Existing.png");
            byte[] sentinel = Encoding.UTF8.GetBytes("A previously saved user render");
            File.WriteAllBytes(existing, sentinel);
            string next = PreviewPngPublisher.Publish(root, "Existing", pixels, 3, 2, 16);
            Check("Existing renders are never overwritten", File.ReadAllBytes(existing).SequenceEqual(sentinel) && next != existing);
            Directory.CreateDirectory(Path.Combine(root, "Directory collision.png"));
            string afterDirectory = PreviewPngPublisher.Publish(root, "Directory collision", pixels, 3, 2, 16);
            Check("A directory with the desired filename is also preserved", Directory.Exists(Path.Combine(root, "Directory collision.png")) && File.Exists(afterDirectory));
            string[] parallel = Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                PreviewPngPublisher.Publish(root, "Concurrent pose", pixels, 3, 2, 16)))).GetAwaiter().GetResult();
            Check("Concurrent publications each receive a distinct filename", parallel.Distinct(StringComparer.OrdinalIgnoreCase).Count() == parallel.Length);
            Check("Every concurrent publication contains a complete independently decoded PNG", parallel.All(path => Decode(path).Rgba.SequenceEqual(Decode(full).Rgba)));
            Check("Successful publications leave no temporary files", !Directory.EnumerateFiles(root, "*.tmp").Any());

            string unicode = PreviewPngPublisher.Publish(root, "ジョン・モクスリー — Élite 😀", pixels, 3, 2, 16);
            Check("Unicode wrestler names are preserved in the output name", Path.GetFileName(unicode).StartsWith("ジョン・モクスリー — Élite 😀", StringComparison.Ordinal));
            string[] reserved = { "CON", "con.png", "PRN", "AUX", "NUL", "COM1", "LPT9", "COM\u00b9", "LPT\u00b2", "CONIN$", "CONOUT$", "CLOCK$" };
            foreach (string name in reserved)
            {
                string path = PreviewPngPublisher.Publish(root, name, pixels, 3, 2, 16);
                Check("A Windows device name is made safe: " + name, Path.GetFileName(path).StartsWith("_", StringComparison.Ordinal));
            }
            string[] unsafeNames = { "../..\\PAC:<\"|?*\0>", " .. ", "", null, new string('x', 300), "Broken \ud800name\udc00", new string('x', 95) + "😀" };
            foreach (string name in unsafeNames)
            {
                string path = PreviewPngPublisher.Publish(root, name, pixels, 3, 2, 16);
                string filename = Path.GetFileName(path);
                Check("An unsafe or long name stays bounded inside the chosen folder", Path.GetDirectoryName(path) == root && filename.Length <= 105 &&
                    !filename.Any(character => character < 32 || "<>:\"/\\|?*".IndexOf(character) >= 0) && !filename.EndsWith(" ", StringComparison.Ordinal) && !filename.EndsWith(".", StringComparison.Ordinal));
            }

            string invalid = Path.Combine(root, "must-not-be-created");
            Reject("A missing pixel buffer is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", null, 3, 2, 16));
            Reject("A zero width is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 0, 2, 16));
            Reject("A negative height is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, -1, 16));
            Reject("Oversized dimensions are rejected before allocating output", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, PreviewPngPublisher.MaximumDimension + 1, 1, int.MaxValue));
            Reject("An oversized pixel count is rejected before allocating output", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 8193, 8193, 8193 * 4));
            Reject("An undersized row stride is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 11));
            Reject("A negative row stride is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, -16));
            Reject("An overflowing row stride is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, int.MaxValue));
            Reject("A truncated buffer is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels.Take(27).ToArray(), 3, 2, 16));
            Reject("A crop before the left edge is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, -1, 0));
            Reject("A crop before the top edge is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 0, -1));
            Reject("A crop beginning beyond the right edge is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 3, 0));
            Reject("A crop beginning beyond the bottom edge is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 0, 2));
            Reject("A crop extending beyond the right edge is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 1, 0, 3, 1));
            Reject("A crop extending beyond the bottom edge is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 0, 1, 1, 2));
            Reject("A negative crop size is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 0, 0, -1, 1));
            Reject("An overflowing crop size is rejected", () => PreviewPngPublisher.Publish(invalid, "Invalid", pixels, 3, 2, 16, 1, 0, int.MaxValue, 1));
            Reject("An empty output directory is rejected", () => PreviewPngPublisher.Publish(" ", "Invalid", pixels, 3, 2, 16));
            Check("Rejected inputs create neither a folder nor a partial output", !Directory.Exists(invalid));
            string blocked = Path.Combine(root, "Not a folder");
            File.WriteAllBytes(blocked, sentinel);
            RejectIo("An invalid output location fails without replacing the existing file", () => PreviewPngPublisher.Publish(blocked, "Invalid", pixels, 3, 2, 16));
            Check("A failed publication preserves the existing output-location file", File.ReadAllBytes(blocked).SequenceEqual(sentinel));
            Check("Failed publications leave no temporary files", !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
            Check("All successful and rejected calls leave the source bytes unchanged", pixels.SequenceEqual(original));

            if (arguments.Length == 2 && arguments[0] == "--fixture")
            {
                string fixture = PreviewPngPublisher.Publish(arguments[1], "alpha-crop", pixels, 3, 2, 16, 1, 0, 2, 2);
                Console.WriteLine("Fixture: " + fixture);
            }
            Console.WriteLine("PASS " + passed + " PNG publisher checks");
        }
        finally { Directory.Delete(root, true); }
    }

    // Independent decoder: platform RFC1950 decompression and bitwise CRC, with no encoder helpers.
    private static Image Decode(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        if (!png.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new InvalidDataException("Not a PNG");
        int cursor = 8, width = 0, height = 0, idatCount = 0;
        bool ended = false;
        using (var data = new MemoryStream())
        {
            while (cursor < png.Length)
            {
                int size = checked((int)ReadUInt32(png, cursor));
                string type = Encoding.ASCII.GetString(png, cursor + 4, 4);
                int start = cursor + 8;
                uint crc = 0xffffffff;
                for (int index = cursor + 4; index < start + size; index++)
                {
                    crc ^= png[index];
                    for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
                }
                if ((crc ^ 0xffffffff) != ReadUInt32(png, start + size)) throw new InvalidDataException("Bad " + type + " CRC");
                if (type == "IHDR")
                {
                    if (cursor != 8 || size != 13) throw new InvalidDataException("Invalid image header");
                    width = checked((int)ReadUInt32(png, start)); height = checked((int)ReadUInt32(png, start + 4));
                    if (!png.Skip(start + 8).Take(5).SequenceEqual(new byte[] {8,6,0,0,0})) throw new InvalidDataException("Not straight RGBA8");
                }
                else if (type == "IDAT") { data.Write(png, start, size); idatCount++; }
                else if (type == "IEND") { if (size != 0) throw new InvalidDataException("Invalid end marker"); ended = true; }
                else throw new InvalidDataException("Unexpected PNG chunk: " + type);
                cursor = checked(start + size + 4);
                if (ended && cursor != png.Length) throw new InvalidDataException("Trailing content");
            }
            if (!ended || idatCount == 0 || width <= 0 || height <= 0) throw new InvalidDataException("Incomplete image");
            data.Position = 0;
            using (var inflated = new MemoryStream())
            {
                using (var zlib = new ZLibStream(data, CompressionMode.Decompress, true)) zlib.CopyTo(inflated);
                byte[] raw = inflated.ToArray();
                int rowSize = checked(width * 4);
                if (raw.Length != checked(height * (rowSize + 1))) throw new InvalidDataException("Wrong pixel count");
                byte[] rgba = new byte[checked(width * height * 4)];
                for (int y = 0; y < height; y++)
                {
                    if (raw[y * (rowSize + 1)] != 0) throw new InvalidDataException("Unsupported test filter");
                    Buffer.BlockCopy(raw, y * (rowSize + 1) + 1, rgba, y * rowSize, rowSize);
                }
                return new Image {Width = width, Height = height, Rgba = rgba, IdatChunks = idatCount};
            }
        }
    }

    private static uint ReadUInt32(byte[] bytes, int index)
    { return ((uint)bytes[index] << 24) | ((uint)bytes[index + 1] << 16) | ((uint)bytes[index + 2] << 8) | bytes[index + 3]; }

    private static void Reject(string name, Action action)
    {
        try { action(); } catch (ArgumentException) { Check(name, true); return; }
        throw new InvalidOperationException(name);
    }

    private static void RejectIo(string name, Action action)
    {
        try { action(); } catch (IOException) { Check(name, true); return; }
        throw new InvalidOperationException(name);
    }

    private static void Check(string name, bool condition)
    {
        if (!condition) throw new InvalidOperationException(name);
        passed++; Console.WriteLine("PASS " + name);
    }

    private sealed class Image
    {
        public int Width, Height, IdatChunks;
        public byte[] Rgba;
    }
}
