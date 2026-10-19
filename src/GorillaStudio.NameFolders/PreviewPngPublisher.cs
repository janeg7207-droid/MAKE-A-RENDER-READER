using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>Writes a single straight-alpha BGRA frame as an ordinary RGBA PNG.</summary>
    public static class PreviewPngPublisher
    {
        public const int MaximumDimension = 16384;
        public const long MaximumPixels = 64L * 1024 * 1024;
        public const int MaximumBufferBytes = 256 * 1024 * 1024;
        private const int MaximumStemLength = 96;
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static string Publish(string outputDirectory, string descriptiveStem, byte[] bgra,
            int width, int height, int stride, int cropX = 0, int cropY = 0,
            int cropWidth = 0, int cropHeight = 0)
        {
            Validate(bgra, width, height, stride, cropX, cropY, ref cropWidth, ref cropHeight);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("An output folder is required.", nameof(outputDirectory));
            string directory = Path.GetFullPath(outputDirectory);
            string stem = SafeStem(descriptiveStem);
            Directory.CreateDirectory(directory);

            string temporary = Path.Combine(directory, ".preview-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Encode completely before reserving the user-visible output name.
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WritePng(stream, bgra, stride, cropX, cropY, cropWidth, cropHeight);
                    stream.Flush(true);
                }
                for (int attempt = 0; attempt < 10000; attempt++)
                {
                    string suffix = attempt == 0 ? "" : " (" + (attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    string destination = Path.Combine(directory, stem + suffix + ".png");
                    FileStream published;
                    try
                    {
                        // CreateNew is exclusive across processes. File.Move's no-overwrite
                        // check can race with another Move on some supported runtimes.
                        published = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(destination) && !Directory.Exists(destination)) throw;
                        continue;
                    }
                    try
                    {
                        // Final visibility is not atomic during this copy. The returned path
                        // is complete and flushed; a failed copy removes only our own file.
                        using (published)
                        using (var complete = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            complete.CopyTo(published, 65536);
                            published.Flush(true);
                        }
                        return destination;
                    }
                    catch
                    {
                        try { File.Delete(destination); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        throw;
                    }
                }
                throw new IOException("The output folder already contains too many previews with this name.");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void Validate(byte[] pixels, int width, int height, int stride,
            int cropX, int cropY, ref int cropWidth, ref int cropHeight)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || width > MaximumDimension) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > MaximumDimension) throw new ArgumentOutOfRangeException(nameof(height));
            if ((long)width * height > MaximumPixels) throw new ArgumentOutOfRangeException(nameof(width), "The frame contains too many pixels.");
            if (stride < (long)width * 4) throw new ArgumentOutOfRangeException(nameof(stride));
            long required = (long)(height - 1) * stride + (long)width * 4;
            if (required > MaximumBufferBytes || pixels.LongLength > MaximumBufferBytes || pixels.LongLength < required)
                throw new ArgumentException("The BGRA buffer does not fit the frame dimensions and row stride.", nameof(pixels));
            if (cropX < 0 || cropX >= width) throw new ArgumentOutOfRangeException(nameof(cropX));
            if (cropY < 0 || cropY >= height) throw new ArgumentOutOfRangeException(nameof(cropY));
            if (cropWidth == 0) cropWidth = width - cropX;
            if (cropHeight == 0) cropHeight = height - cropY;
            if (cropWidth < 1 || (long)cropX + cropWidth > width) throw new ArgumentOutOfRangeException(nameof(cropWidth));
            if (cropHeight < 1 || (long)cropY + cropHeight > height) throw new ArgumentOutOfRangeException(nameof(cropHeight));
        }

        private static string SafeStem(string value)
        {
            var text = new StringBuilder();
            foreach (char character in value ?? "")
            {
                if (character < 32 || character == 127 || "<>:\"/\\|?*".IndexOf(character) >= 0)
                    text.Append('_');
                else text.Append(character);
            }
            string stem = text.ToString().Trim(' ', '.');
            // Replace unpaired UTF-16 surrogates, keeping valid Unicode names intact.
            text.Clear();
            for (int index = 0; index < stem.Length && text.Length < MaximumStemLength; index++)
            {
                char character = stem[index];
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < stem.Length && char.IsLowSurrogate(stem[index + 1]))
                    {
                        if (text.Length + 2 > MaximumStemLength) break;
                        text.Append(character).Append(stem[++index]);
                    }
                    else text.Append('_');
                }
                else text.Append(char.IsLowSurrogate(character) ? '_' : character);
            }
            stem = text.ToString().TrimEnd(' ', '.');
            if (stem.Length == 0) stem = "Preview";
            string device = stem.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" ||
                device == "CONIN$" || device == "CONOUT$" || device == "CLOCK$" ||
                (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                 "123456789\u00b9\u00b2\u00b3".IndexOf(device[3]) >= 0))
                stem = "_" + stem;
            return stem;
        }

        private static void WritePng(Stream output, byte[] bgra, int stride, int cropX, int cropY, int width, int height)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            output.Write(signature, 0, signature.Length);
            byte[] header = new byte[13];
            PutUInt32(header, 0, (uint)width); PutUInt32(header, 4, (uint)height);
            header[8] = 8; header[9] = 6; // Eight-bit, straight RGBA, no interlace.
            WriteChunk(output, "IHDR", header, header.Length);
            using (var idat = new IdatStream(output))
            {
                // DeflateStream supplies raw DEFLATE on net48; PNG needs the RFC1950 wrapper.
                idat.WriteByte(0x78); idat.WriteByte(0x9c);
                uint adlerA = 1, adlerB = 0;
                byte[] row = new byte[checked(width * 4 + 1)];
                using (var deflate = new DeflateStream(idat, CompressionLevel.Optimal, true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        int source = checked((cropY + y) * stride + cropX * 4);
                        row[0] = 0; // PNG filter None, independent of preceding rows.
                        for (int x = 0, target = 1; x < width; x++, source += 4, target += 4)
                        {
                            row[target] = bgra[source + 2]; row[target + 1] = bgra[source + 1];
                            row[target + 2] = bgra[source]; row[target + 3] = bgra[source + 3];
                        }
                        UpdateAdler(row, ref adlerA, ref adlerB);
                        deflate.Write(row, 0, row.Length);
                    }
                }
                byte[] checksum = new byte[4];
                PutUInt32(checksum, 0, (adlerB << 16) | adlerA);
                idat.Write(checksum, 0, checksum.Length);
            }
            WriteChunk(output, "IEND", new byte[0], 0);
        }

        private static void UpdateAdler(byte[] bytes, ref uint a, ref uint b)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int end = Math.Min(offset + 5552, bytes.Length);
                for (; offset < end; offset++) { a += bytes[offset]; b += a; }
                a %= 65521; b %= 65521;
            }
        }

        private static void WriteChunk(Stream output, string kind, byte[] data, int count)
        {
            byte[] size = new byte[4]; PutUInt32(size, 0, (uint)count); output.Write(size, 0, 4);
            byte[] type = Encoding.ASCII.GetBytes(kind); output.Write(type, 0, 4);
            uint crc = 0xffffffff;
            for (int index = 0; index < type.Length; index++) crc = CrcTable[(crc ^ type[index]) & 255] ^ (crc >> 8);
            output.Write(data, 0, count);
            for (int index = 0; index < count; index++) crc = CrcTable[(crc ^ data[index]) & 255] ^ (crc >> 8);
            PutUInt32(size, 0, crc ^ 0xffffffff); output.Write(size, 0, 4);
        }

        private static void PutUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24); data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8); data[offset + 3] = (byte)value;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint crc = index;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
                table[index] = crc;
            }
            return table;
        }

        private sealed class IdatStream : Stream
        {
            private readonly Stream output;
            private readonly byte[] buffer = new byte[65536];
            private int count;
            internal IdatStream(Stream output) { this.output = output; }
            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Write(byte[] data, int offset, int length)
            {
                while (length > 0)
                {
                    int take = Math.Min(buffer.Length - count, length);
                    Buffer.BlockCopy(data, offset, buffer, count, take);
                    count += take; offset += take; length -= take;
                    if (count == buffer.Length) Flush();
                }
            }
            public override void WriteByte(byte value)
            {
                buffer[count++] = value;
                if (count == buffer.Length) Flush();
            }
            public override void Flush()
            {
                if (count == 0) return;
                WriteChunk(output, "IDAT", buffer, count); count = 0;
            }
            protected override void Dispose(bool disposing) { if (disposing) Flush(); base.Dispose(disposing); }
            public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }
    }
}
