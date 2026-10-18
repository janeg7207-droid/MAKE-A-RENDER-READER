using System;
using System.IO;
using Pfim;

namespace GorillaStudio.NameFolders
{
    /// <summary>A tightly packed, straight-alpha BGRA8 roster picture.</summary>
    public sealed class RosterPicturePreviewImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Bgra { get; private set; }

        internal RosterPicturePreviewImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Bgra = pixels;
        }
    }

    /// <summary>
    /// Decodes the first 2D DDS image without launching Blender or the game engine.
    /// Only the selected image's first level is read; unused mipmaps are never allocated.
    /// </summary>
    public static class RosterPicturePreview
    {
        public const int MaximumDimension = 16384;
        public const int MaximumPixelBytes = 64 * 1024 * 1024;
        private const int MaximumDecoderBytes = MaximumPixelBytes + MaximumDimension * 32;
        private const uint Dx10 = 0x30315844;

        public static RosterPicturePreviewImage Decode(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a DDS picture.", "path");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return DecodeStream(stream);
        }

        public static RosterPicturePreviewImage Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            using (var stream = new MemoryStream(bytes, false)) return DecodeStream(stream);
        }

        private static RosterPicturePreviewImage DecodeStream(Stream stream)
        {
            byte[] header = ReadExactly(stream, 128);
            if (UInt(header, 0) != 0x20534444 || UInt(header, 4) != 124 || UInt(header, 76) != 32)
                throw new InvalidDataException("This is not a supported DDS header.");
            uint widthValue = UInt(header, 16), heightValue = UInt(header, 12);
            if (widthValue < 1 || heightValue < 1 || widthValue > MaximumDimension || heightValue > MaximumDimension ||
                (long)widthValue * heightValue * 4 > MaximumPixelBytes)
                throw new InvalidDataException("The DDS picture dimensions exceed the preview limit.");
            int width = (int)widthValue, height = (int)heightValue;
            if (UInt(header, 24) > 1 || (UInt(header, 112) & 0x20fe00u) != 0)
                throw new InvalidDataException("Choose a 2D DDS picture rather than a volume or cube map.");

            uint flags = UInt(header, 80), fourCc = UInt(header, 84);
            uint dxgi = 0;
            if ((flags & 4) != 0 && fourCc == Dx10)
            {
                byte[] extended = ReadExactly(stream, 20);
                if (UInt(extended, 4) != 3 || UInt(extended, 12) != 1 || (UInt(extended, 8) & 4) != 0)
                    throw new InvalidDataException("Choose a single 2D DDS picture rather than an array or cube map.");
                dxgi = UInt(extended, 0);
                Array.Resize(ref header, 148);
                Buffer.BlockCopy(extended, 0, header, 128, 20);
            }

            int blockBytes = BlockBytes(fourCc, dxgi, flags);
            if (blockBytes != 0)
            {
                int payloadBytes = checked(((width + 3) / 4) * ((height + 3) / 4) * blockBytes);
                byte[] payload = ReadExactly(stream, payloadBytes);
                // Pfim normally reads and allocates every mipmap. Present a complete,
                // bounded, first-level-only DDS instead of trusting the source mip count.
                WriteUInt(header, 28, 1);
                WriteUInt(header, 108, 0x1000);
                WriteUInt(header, 112, 0);
                WriteUInt(header, 8, UInt(header, 8) & ~0x20000u);
                byte[] firstLevel = new byte[header.Length + payloadBytes];
                Buffer.BlockCopy(header, 0, firstLevel, 0, header.Length);
                Buffer.BlockCopy(payload, 0, firstLevel, header.Length, payloadBytes);
                using (var image = Dds.Create(firstLevel, new PfimConfig(allocator: new BoundedAllocator())))
                {
                    if (image.Width != width || image.Height != height)
                        throw new InvalidDataException("The DDS decoder returned unexpected picture dimensions.");
                    return CopyPixels(image, width, height);
                }
            }
            return DecodeUncompressed(stream, header, width, height, flags, fourCc, dxgi);
        }

        private static int BlockBytes(uint fourCc, uint dxgi, uint flags)
        {
            if ((flags & 4) == 0) return 0;
            if (fourCc == Dx10)
            {
                if (dxgi >= 70 && dxgi <= 72 || dxgi >= 79 && dxgi <= 81) return 8;
                if (dxgi >= 73 && dxgi <= 78 || dxgi >= 82 && dxgi <= 84 || dxgi >= 97 && dxgi <= 99) return 16;
                return 0;
            }
            switch (fourCc)
            {
                case 0x31545844: // DXT1
                case 0x31495441: // ATI1
                case 0x55344342: // BC4U
                case 0x53344342: return 8; // BC4S
                case 0x33545844: // DXT3
                case 0x35545844: // DXT5
                case 0x32495441: // ATI2
                case 0x55354342: // BC5U
                case 0x53354342: return 16; // BC5S
                default: return 0;
            }
        }

        private static RosterPicturePreviewImage CopyPixels(IImage image, int width, int height)
        {
            int bytesPerPixel;
            switch (image.Format)
            {
                case Pfim.ImageFormat.Rgba32: bytesPerPixel = 4; break;
                case Pfim.ImageFormat.Rgb24: bytesPerPixel = 3; break;
                case Pfim.ImageFormat.Rgb8: bytesPerPixel = 1; break;
                default: throw new InvalidDataException("This DDS pixel format cannot be displayed as a roster picture.");
            }
            if (image.Stride < width * bytesPerPixel || image.Data == null ||
                (long)(height - 1) * image.Stride + width * bytesPerPixel > image.Data.Length)
                throw new InvalidDataException("The DDS decoder returned incomplete picture pixels.");
            byte[] output = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int source = y * image.Stride, destination = y * width * 4;
                if (bytesPerPixel == 4) Buffer.BlockCopy(image.Data, source, output, destination, width * 4);
                else for (int x = 0; x < width; x++, source += bytesPerPixel, destination += 4)
                {
                    output[destination] = image.Data[source];
                    output[destination + 1] = image.Data[source + (bytesPerPixel == 1 ? 0 : 1)];
                    output[destination + 2] = image.Data[source + (bytesPerPixel == 1 ? 0 : 2)];
                    output[destination + 3] = 255;
                }
            }
            return new RosterPicturePreviewImage(width, height, output);
        }

        private static RosterPicturePreviewImage DecodeUncompressed(Stream stream, byte[] header, int width, int height,
            uint flags, uint fourCc, uint dxgi)
        {
            int bits = (int)UInt(header, 88);
            uint red = UInt(header, 92), green = UInt(header, 96), blue = UInt(header, 100), alpha = UInt(header, 104);
            bool luminance = (flags & 0x20000) != 0;
            if ((flags & 4) != 0)
            {
                if (fourCc != Dx10) throw new InvalidDataException("This DDS compression format is not supported for roster previews.");
                bits = 32;
                if (dxgi >= 27 && dxgi <= 29) { red = 0xff; green = 0xff00; blue = 0xff0000; alpha = 0xff000000; }
                else if (dxgi == 87 || dxgi == 90 || dxgi == 91) { red = 0xff0000; green = 0xff00; blue = 0xff; alpha = 0xff000000; }
                else if (dxgi == 88 || dxgi == 92 || dxgi == 93) { red = 0xff0000; green = 0xff00; blue = 0xff; alpha = 0; }
                else throw new InvalidDataException("This DDS DXGI format is not supported for roster previews.");
            }
            else if ((flags & 0x40) == 0 && !luminance)
                throw new InvalidDataException("This DDS pixel format is not supported for roster previews.");
            if (bits != 8 && bits != 16 && bits != 24 && bits != 32)
                throw new InvalidDataException("This DDS pixel size is not supported for roster previews.");
            if (luminance) green = blue = red;
            uint used = red | green | blue | alpha;
            if (red == 0 || green == 0 || blue == 0 || !Contiguous(red) || !Contiguous(green) || !Contiguous(blue) ||
                alpha != 0 && !Contiguous(alpha) || bits < 32 && (used >> bits) != 0 ||
                !luminance && ((red & green) != 0 || (red & blue) != 0 || (green & blue) != 0) ||
                (alpha & (red | green | blue)) != 0)
                throw new InvalidDataException("This DDS has invalid color channel masks.");

            int bytesPerPixel = bits / 8, packedRow = width * bytesPerPixel;
            long pitch = (UInt(header, 8) & 8) != 0 ? UInt(header, 20) : (uint)packedRow;
            if (pitch < packedRow || pitch > MaximumPixelBytes || pitch * height > MaximumPixelBytes)
                throw new InvalidDataException("This DDS has an invalid or oversized row pitch.");
            byte[] data = ReadExactly(stream, (int)(pitch * height));
            byte[] output = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int source = (int)(y * pitch) + x * bytesPerPixel, destination = (y * width + x) * 4;
                uint value = 0;
                for (int c = 0; c < bytesPerPixel; c++) value |= (uint)data[source + c] << (c * 8);
                output[destination] = Channel(value, blue);
                output[destination + 1] = Channel(value, green);
                output[destination + 2] = Channel(value, red);
                output[destination + 3] = alpha == 0 ? (byte)255 : Channel(value, alpha);
            }
            return new RosterPicturePreviewImage(width, height, output);
        }

        private static bool Contiguous(uint mask)
        {
            if (mask == 0) return false;
            while ((mask & 1) == 0) mask >>= 1;
            return (mask & (mask + 1)) == 0;
        }

        private static byte Channel(uint pixel, uint mask)
        {
            while ((mask & 1) == 0) { mask >>= 1; pixel >>= 1; }
            return (byte)(((ulong)(pixel & mask) * 255 + mask / 2) / mask);
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            if (count < 0 || count > MaximumPixelBytes || stream.Length - stream.Position < count)
                throw new InvalidDataException("The DDS picture data is incomplete or exceeds the preview limit.");
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) throw new InvalidDataException("The DDS picture data is incomplete.");
                offset += read;
            }
            return buffer;
        }

        private static uint UInt(byte[] bytes, int offset) { return BitConverter.ToUInt32(bytes, offset); }
        private static void WriteUInt(byte[] bytes, int offset, uint value)
        {
            for (int index = 0; index < 4; index++) bytes[offset + index] = (byte)(value >> (index * 8));
        }

        private sealed class BoundedAllocator : IImageAllocator
        {
            public byte[] Rent(int size)
            {
                if (size < 0 || size > MaximumDecoderBytes)
                    throw new InvalidDataException("The DDS decoder requested an oversized preview buffer.");
                return new byte[size];
            }
            public void Return(byte[] data) { }
        }
    }
}
