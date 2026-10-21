using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GorillaStudio.NameFolders
{
    // NumPy's documented NPY v1 layout inside a standard ZIP archive. The
    // unmodified engine reads these arrays with numpy.load; Python is not needed
    // to import a Discovery animation on the user's computer.
    public static class DiscoveryMotionNpz
    {
        internal static bool MatchesTiming(string path, int frames, float fps)
        {
            if (!File.Exists(path) || frames <= 0 || float.IsNaN(fps) || float.IsInfinity(fps) || fps <= 0) return false;
            try
            {
                using (var file = File.OpenRead(path))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry metadata = archive.GetEntry("hd.npy");
                    if (metadata == null || metadata.Length > 32768) return false;
                    using (var reader = new BinaryReader(metadata.Open(), Encoding.ASCII))
                    {
                        string header = ReadHeader(reader);
                        if (header == null || !header.Contains("'<U") || !header.Contains("'shape': ()")) return false;
                        var json = new StringBuilder();
                        while (json.Length < 4096)
                        {
                            byte[] character = reader.ReadBytes(4);
                            if (character.Length == 0) break;
                            if (character.Length != 4 || character[1] != 0 || character[2] != 0 || character[3] != 0) return false;
                            json.Append((char)character[0]);
                        }
                        if (reader.BaseStream.ReadByte() != -1) return false;
                        var info = DiscoveryTauntLibrary.JsonObject(json.ToString());
                        double originalFps = info.ContainsKey("fps") ? Convert.ToDouble(info["fps"], CultureInfo.InvariantCulture) : double.NaN;
                        return info.ContainsKey("frames") && Convert.ToInt32(info["frames"], CultureInfo.InvariantCulture) == frames
                            && !double.IsNaN(originalFps) && !double.IsInfinity(originalFps) && Math.Abs(originalFps - fps) <= 0.000001;
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException
                || error is OverflowException || error is FormatException || error is InvalidCastException
#if NET8_0_OR_GREATER
                || error is System.Text.Json.JsonException
#endif
                ) { return false; }
        }

        public static bool MatchesPoses(string path, DecodedDiscoveryMotion motion)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var file = File.OpenRead(path))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry identifiers = archive.GetEntry("ids.npy"), metadata = archive.GetEntry("hd.npy");
                    if (identifiers == null || metadata == null) return false;
                    using (var reader = new BinaryReader(identifiers.Open(), Encoding.ASCII))
                    {
                        string header = ReadHeader(reader);
                        if (header == null || !header.Contains("'<u8'") || !header.Contains("'shape': " + Shape(motion.Ids.Length))) return false;
                        foreach (ulong id in motion.Ids) if (reader.ReadUInt64() != id) return false;
                        if (reader.BaseStream.ReadByte() != -1) return false;
                    }
                    using (var reader = new BinaryReader(metadata.Open(), Encoding.ASCII))
                    {
                        string header = ReadHeader(reader);
                        if (header == null || !header.Contains("'<U") || !header.Contains("'shape': ()")) return false;
                        var json = new StringBuilder();
                        while (json.Length < 4096)
                        {
                            byte[] character = reader.ReadBytes(4);
                            if (character.Length == 0) break;
                            if (character.Length != 4 || character[1] != 0 || character[2] != 0 || character[3] != 0) return false;
                            json.Append((char)character[0]);
                        }
                        var info = DiscoveryTauntLibrary.JsonObject(json.ToString());
                        if (!info.ContainsKey("frames") || !info.ContainsKey("fps") ||
                            Convert.ToInt32(info["frames"], CultureInfo.InvariantCulture) != motion.Frames ||
                            Math.Abs(Convert.ToDouble(info["fps"], CultureInfo.InvariantCulture) - motion.Fps) > 0.000001) return false;
                    }
                    ZipArchiveEntry entry = archive.GetEntry("P.npy");
                    if (entry == null) return false;
                    using (var reader = new BinaryReader(entry.Open(), Encoding.ASCII))
                    {
                        string header = ReadHeader(reader);
                        if (header == null || !header.Contains("'<f4'") || !header.Contains("'fortran_order': False") ||
                            !header.Contains("'shape': " + Shape(motion.Ids.Length, motion.Frames, 7))) return false;
                        for (int bone = 0; bone < motion.Ids.Length; bone++)
                            for (int frame = 0; frame < motion.Frames; frame++)
                                for (int channel = 0; channel < 7; channel++)
                                {
                                    float original = reader.ReadSingle(), actual = motion.Poses[bone, frame, channel];
                                    if (float.IsNaN(original) || float.IsInfinity(original) || Math.Abs(original - actual) > 0.00001) return false;
                                }
                        return reader.BaseStream.ReadByte() == -1;
                    }
                }
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        private static string ReadHeader(BinaryReader reader)
        {
            byte[] magic = reader.ReadBytes(8);
            if (magic.Length != 8 || magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY" || magic[6] != 1) return null;
            int length = reader.ReadUInt16();
            byte[] header = reader.ReadBytes(length);
            return header.Length == length ? Encoding.ASCII.GetString(header) : null;
        }

        public static void Write(string path, DecodedDiscoveryMotion motion)
        {
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            if (motion.Ids == null || motion.Poses == null || motion.ScaleIds == null || motion.Scales == null ||
                motion.Frames <= 0 || motion.Poses.GetLength(0) != motion.Ids.Length ||
                motion.Poses.GetLength(1) != motion.Frames || motion.Poses.GetLength(2) != 7 ||
                motion.Scales.GetLength(0) != motion.ScaleIds.Length || motion.Scales.GetLength(1) != motion.Frames)
                throw new InvalidDataException("The decoded animation arrays do not agree.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    Array(archive, "ids", "<u8", Shape(motion.Ids.Length), writer =>
                    { foreach (ulong id in motion.Ids) writer.Write(id); });
                    Array(archive, "P", "<f4", Shape(motion.Ids.Length, motion.Frames, 7), writer =>
                    {
                        for (int bone = 0; bone < motion.Ids.Length; bone++)
                            for (int frame = 0; frame < motion.Frames; frame++)
                                for (int channel = 0; channel < 7; channel++) writer.Write(motion.Poses[bone, frame, channel]);
                    });
                    Array(archive, "sids", "<u8", Shape(motion.ScaleIds.Length), writer =>
                    { foreach (ulong id in motion.ScaleIds) writer.Write(id); });
                    Array(archive, "sc", "<f4", Shape(motion.ScaleIds.Length, motion.Frames), writer =>
                    {
                        for (int bone = 0; bone < motion.ScaleIds.Length; bone++)
                            for (int frame = 0; frame < motion.Frames; frame++) writer.Write(motion.Scales[bone, frame]);
                    });
                    string header = "{\"fps\":" + motion.Fps.ToString("R", CultureInfo.InvariantCulture) +
                        ",\"frames\":" + motion.Frames.ToString(CultureInfo.InvariantCulture) +
                        ",\"u32_0x40\":" + motion.Flags.ToString(CultureInfo.InvariantCulture) + "}";
                    Array(archive, "hd", "<U" + header.Length.ToString(CultureInfo.InvariantCulture), "()", writer =>
                    { foreach (char value in header) writer.Write((uint)value); });
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Shape(params int[] dimensions)
        {
            return "(" + string.Join(", ", System.Array.ConvertAll(dimensions,
                value => value.ToString(CultureInfo.InvariantCulture))) + (dimensions.Length == 1 ? ",)" : ")");
        }

        private static void Array(ZipArchive archive, string name, string type, string shape, Action<BinaryWriter> write)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name + ".npy", CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                string descriptor = "{'descr': '" + type + "', 'fortran_order': False, 'shape': " + shape + ", }";
                int padding = (64 - ((10 + descriptor.Length + 1) % 64)) % 64;
                byte[] header = Encoding.ASCII.GetBytes(descriptor + new string(' ', padding) + "\n");
                if (header.Length > ushort.MaxValue) throw new InvalidDataException("An animation array header is too long.");
                writer.Write(new byte[] { 0x93, 0x4e, 0x55, 0x4d, 0x50, 0x59, 1, 0 });
                writer.Write((ushort)header.Length);
                writer.Write(header);
                write(writer);
            }
        }
    }
}
