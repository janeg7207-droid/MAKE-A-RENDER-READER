using System;
using System.IO;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>Writes a straight-alpha BGRA8 image in the DDS format read by the original engine.</summary>
    public static class RosterPictureDds
    {
        public const int MaximumDimension = 16384;
        public const int MaximumPixelBytes = 64 * 1024 * 1024;

        /// <summary>Writes tightly packed BGRA pixels without resampling, premultiplying or dropping alpha.</summary>
        public static void Write(string path, int width, int height, byte[] bgraPixels)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a DDS destination.", "path");
            if (width < 1 || width > MaximumDimension)
                throw new ArgumentOutOfRangeException("width", "The image width must be between 1 and " + MaximumDimension + ".");
            if (height < 1 || height > MaximumDimension)
                throw new ArgumentOutOfRangeException("height", "The image height must be between 1 and " + MaximumDimension + ".");
            long pixelBytes = (long)width * height * 4;
            if (pixelBytes > MaximumPixelBytes)
                throw new ArgumentOutOfRangeException("width", "The image must contain no more than " + (MaximumPixelBytes / 4) + " pixels.");
            if (bgraPixels == null) throw new ArgumentNullException("bgraPixels");
            if (bgraPixels.LongLength != pixelBytes)
                throw new ArgumentException("Supply exactly width × height × 4 tightly packed BGRA bytes.", "bgraPixels");

            string destination = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
                        writer.Write(0x20534444u); // "DDS "
                        writer.Write(124u); // DDS_HEADER size, excluding the magic.
                        writer.Write(0x100fu); // CAPS | HEIGHT | WIDTH | PITCH | PIXELFORMAT.
                        writer.Write((uint)height);
                        writer.Write((uint)width);
                        writer.Write((uint)width * 4); // No row padding or compression.
                        writer.Write(0u); // Depth: this is a 2D image.
                        writer.Write(0u); // No mipmaps.
                        for (int index = 0; index < 11; index++) writer.Write(0u);
                        writer.Write(32u); // DDS_PIXELFORMAT size.
                        writer.Write(0x41u); // DDPF_RGB | DDPF_ALPHAPIXELS.
                        writer.Write(0u); // No FourCC / DX10 header.
                        writer.Write(32u); // Bits per pixel.
                        writer.Write(0x00ff0000u); // R.
                        writer.Write(0x0000ff00u); // G.
                        writer.Write(0x000000ffu); // B.
                        writer.Write(0xff000000u); // A.
                        writer.Write(0x1000u); // DDSCAPS_TEXTURE.
                        writer.Write(0u); // Caps2.
                        writer.Write(0u); // Caps3.
                        writer.Write(0u); // Caps4.
                        writer.Write(0u); // Reserved.
                        writer.Write(bgraPixels);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
