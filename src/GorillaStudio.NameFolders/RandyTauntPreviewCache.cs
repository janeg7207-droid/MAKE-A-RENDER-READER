using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    /// <summary>An immutable selected-motion/Randy-source fingerprint; never reads the selected wrestler.</summary>
    public sealed class RandyTauntPreviewContext
    {
        internal string ApplicationRoot, EngineRoot, Key, ModelRoot, Extra;
        internal string[] ExtraFiles;
        public string Fingerprint { get; internal set; }
        public bool IsCurrent()
        {
            try { return Fingerprint == RandyTauntPreviewCache.CaptureContext(ApplicationRoot, EngineRoot, Key, ModelRoot, Extra, ExtraFiles).Fingerprint; }
            catch { return false; }
        }
    }

    /// <summary>Reuses only proven bundled Randy atlases or previews published by the dedicated Randy renderer.</summary>
    public static class RandyTauntPreviewCache
    {
        private const long MaximumPngBytes = 512L * 1024 * 1024;
        private static readonly string[] DisplayFields = { "name", "also", "preview", "tags", "tested_on", "seconds" };
        private static readonly HashSet<string> ExcludedBundled = new HashSet<string>(new[] { "roman3", "straightedge4", "cena2", "cody3", "wetheone1" }, StringComparer.Ordinal);
        private static readonly uint[] CrcTable = MakeCrcTable();

        public static bool TryGetBundled(string applicationRoot, string engineRoot, string key, out string atlasPath)
        {
            atlasPath = null;
            try
            {
                ValidateKey(key);
                if (ExcludedBundled.Contains(key)) return false;
                string app = Full(applicationRoot), engine = string.IsNullOrWhiteSpace(engineRoot) ? app : Full(engineRoot);
                Dictionary<string, object> original = Entry(app, key), current = Entry(engine, key);
                object tested;
                if (!original.TryGetValue("tested_on", out tested) || !RandyLabel(tested)) return false;
                if (Semantic(original) != Semantic(current)) return false;
                string originalFacial = TauntFacialSources.Signature(app, original, "male");
                string currentFacial = TauntFacialSources.Signature(engine, current, "male");
                if (originalFacial != currentFacial) return false;
                string[] left = MotionFiles(app, original), right = MotionFiles(engine, current);
                if (left.Length != right.Length) return false;
                for (int index = 0; index < left.Length; index++)
                    if (Hash(left[index]) != Hash(right[index])
                        && (index != 0 || !MotionNpzSemanticComparer.Equivalent(left[index], right[index]))) return false;
                string path = Path.Combine(app, "data", "library", "previews_game", key + ".png");
                if (!ValidAtlas(path)) return false;
                // Source changes during an atlas lookup must never display an older motion.
                if (Semantic(Entry(app, key)) != Semantic(original) || Semantic(Entry(engine, key)) != Semantic(current)) return false;
                if (TauntFacialSources.Signature(app, original, "male") != originalFacial
                    || TauntFacialSources.Signature(engine, current, "male") != currentFacial) return false;
                atlasPath = path; return true;
            }
            catch { return false; }
        }

        public static RandyTauntPreviewContext CaptureContext(string applicationRoot, string engineRoot, string key,
            string randyModelFolder, string extraSignature = null, IEnumerable<string> extraInputFiles = null)
        {
            ValidateKey(key);
            string app = Full(applicationRoot), engine = string.IsNullOrWhiteSpace(engineRoot) ? app : Full(engineRoot);
            string model = Full(randyModelFolder);
            if (!File.Exists(Path.Combine(model, "BaseModel", "baseModel.mcd"))
                || !File.Exists(Path.Combine(model, "161_Default_Attire", "161_Attire.mcd")))
                throw new InvalidDataException("The shared Randy preview model needs its exact base and default attire files.");
            string[] extras = (extraInputFiles ?? new string[0]).Select(Full).Distinct(PathComparer).OrderBy(path => path, PathComparer).ToArray();
            Dictionary<string, object> entry = Entry(engine, key);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
            {
                writer.Write("Randy-taunt-atlas-v3;model=161_Randy_Orton_2019;ring;hair=black;quick=1;15fps");
                // Discovery builds a new private overlay directory on refresh.
                // Its temporary path and copied motion timestamps do not change
                // Randy's rendered animation. The context retains the actual
                // source paths for freshness checks, but the key follows bytes.
                writer.Write(app); writer.Write(model); writer.Write(key); writer.Write(extraSignature ?? "");
                writer.Write(Semantic(entry));
                writer.Write(TauntFacialSources.Signature(engine, entry, "male"));
                foreach (string path in MotionFiles(engine, entry)) WriteMotion(writer, path);
                foreach (string path in ModelFiles(model)) WriteFile(writer, path, path.EndsWith(".mcd", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length <= 1024 * 1024);
                foreach (string directory in SharedModelDirectories(app, model))
                    foreach (string path in ModelFiles(directory)) WriteFile(writer, path, new FileInfo(path).Length <= 1024 * 1024);
                foreach (string directory in new[] { Path.Combine(app, "engine"), Path.Combine(app, "engine", "blender") })
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (string path in Directory.GetFiles(directory).OrderBy(path => path, PathComparer)) WriteFile(writer, path, new FileInfo(path).Length <= 1024 * 1024);
                }
                foreach (string path in extras) WriteFile(writer, path, new FileInfo(path).Length <= 1024 * 1024);
                writer.Flush(); stream.Position = 0;
                using (SHA256 hash = SHA256.Create())
                    return new RandyTauntPreviewContext { ApplicationRoot = app, EngineRoot = engine, Key = key, ModelRoot = model,
                        Extra = extraSignature, ExtraFiles = extras, Fingerprint = Hex(hash.ComputeHash(stream)) };
            }
        }

        public static bool TryRead(string storageRoot, RandyTauntPreviewContext context, out string atlasPath)
        {
            atlasPath = null;
            if (context == null || !context.IsCurrent()) return false;
            return TryReadCaptured(storageRoot, context, out atlasPath);
        }

        // Only for a context captured immediately by the same cache lookup.
        // The final complete source check still runs before any successful
        // return, including when this context has already become stale.
        internal static bool TryReadCaptured(string storageRoot, RandyTauntPreviewContext context, out string atlasPath)
        {
            atlasPath = null;
            if (context == null) return false;
            try
            {
                string folder = CacheFolder(storageRoot, context), path = Path.Combine(folder, "atlas.png");
                string manifestPath = Path.Combine(folder, "manifest.json");
                if (Linked(folder) || Linked(path) || Linked(manifestPath)) return false;
                Dictionary<string, object> manifest = ReadObject(manifestPath);
                if (Number(manifest, "version") != 1 || Text(manifest, "fingerprint") != context.Fingerprint
                    || Text(manifest, "model") != "161_Randy_Orton_2019" || Number(manifest, "fps") != 15
                    || Text(manifest, "key") != context.Key || !ValidAtlas(path)
                    || Number(manifest, "bytes") != new FileInfo(path).Length || Text(manifest, "sha256") != Hash(path)) return false;
                if (!context.IsCurrent()) return false;
                atlasPath = path; return true;
            }
            catch { return false; }
        }

        public static bool Publish(string storageRoot, RandyTauntPreviewContext context, string source15FpsAtlas, out string atlasPath)
        {
            atlasPath = null;
            string temporary = null;
            try
            {
                if (context == null || !context.IsCurrent() || !ValidAtlas(source15FpsAtlas) || Linked(source15FpsAtlas)) return false;
                if (TryRead(storageRoot, context, out atlasPath)) return true;
                string root = Full(storageRoot), destination = CacheFolder(root, context);
                Directory.CreateDirectory(root);
                if (Linked(root)) return false;
                temporary = Path.Combine(root, ".randy-writing-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                string image = Path.Combine(temporary, "atlas.png");
                File.Copy(source15FpsAtlas, image, false);
                if (!ValidAtlas(image) || !context.IsCurrent()) return false;
                var manifest = new Dictionary<string, object> { { "version", 1 }, { "fingerprint", context.Fingerprint },
                    { "model", "161_Randy_Orton_2019" }, { "fps", 15 }, { "key", context.Key },
                    { "bytes", new FileInfo(image).Length }, { "sha256", Hash(image) } };
                File.WriteAllText(Path.Combine(temporary, "manifest.json"), PreviewJson.Write(manifest), new UTF8Encoding(false));
                if (!context.IsCurrent()) return false;
                if (Directory.Exists(destination))
                {
                    // A competing successful publisher wins. Preserve a corrupt
                    // private entry separately, then publish the verified image.
                    if (TryRead(root, context, out atlasPath)) return true;
                    if (Linked(destination)) return false;
                    Directory.Move(destination, Path.Combine(root, ".randy-corrupt-" + context.Fingerprint + "-" + Guid.NewGuid().ToString("N")));
                }
                Directory.Move(temporary, destination); temporary = null;
                return TryRead(root, context, out atlasPath);
            }
            catch (IOException) { return context != null && TryRead(storageRoot, context, out atlasPath); }
            catch { return false; }
            finally { if (temporary != null) try { Directory.Delete(temporary, true); } catch { } }
        }

        public static bool TryGetPlayAtlas(IEnumerable<string> engineLines, out string atlasPath)
        {
            atlasPath = null;
            try
            {
                string line = (engineLines ?? new string[0]).LastOrDefault(value => value != null && value.StartsWith("RESULT ", StringComparison.Ordinal));
                if (line == null) return false;
                Dictionary<string, object> result = PreviewJson.Read(line.Substring(7));
                double fps = result.ContainsKey("play_fps") ? Convert.ToDouble(result["play_fps"], CultureInfo.InvariantCulture) : 0;
                if (!string.IsNullOrWhiteSpace(Text(result, "gmr")) || Number(result, "frames") <= 0
                    || !result.ContainsKey("id") || Convert.ToDecimal(result["id"], CultureInfo.InvariantCulture) != 161m
                    || double.IsNaN(fps) || double.IsInfinity(fps) || Math.Abs(fps - 15.0) > 0.000001) return false;
                string path = Text(result, "play");
                if (!ValidAtlas(path)) return false;
                atlasPath = Path.GetFullPath(path); return true;
            }
            catch { return false; }
        }

        public static bool ValidAtlas(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 57 || stream.Length > MaximumPngBytes) return false;
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    foreach (byte value in signature) if (stream.ReadByte() != value) return false;
                    bool first = true, data = false;
                    byte[] buffer = new byte[64 * 1024];
                    while (stream.Position < stream.Length)
                    {
                        uint length = ReadU32(stream);
                        if (length > MaximumPngBytes || length > stream.Length - stream.Position - 8) return false;
                        byte[] type = new byte[4]; ReadExactly(stream, type, 0, 4);
                        string name = Encoding.ASCII.GetString(type);
                        uint crc = Crc(0xFFFFFFFF, type, 4);
                        if (first)
                        {
                            if (name != "IHDR" || length != 13) return false;
                            byte[] header = new byte[13]; ReadExactly(stream, header, 0, 13);
                            crc = Crc(crc, header, header.Length);
                            uint width = BigU32(header, 0), height = BigU32(header, 4);
                            if (height < 1 || height > 8192 || width < height || width % height != 0
                                || width > 2000000 || (ulong)width * height > 268435456) return false;
                            if (header[10] != 0 || header[11] != 0 || header[12] > 1) return false;
                            int depth = header[8], colour = header[9];
                            if (!(colour == 0 && new[] { 1, 2, 4, 8, 16 }.Contains(depth)
                                || colour == 3 && new[] { 1, 2, 4, 8 }.Contains(depth)
                                || new[] { 2, 4, 6 }.Contains(colour) && new[] { 8, 16 }.Contains(depth))) return false;
                            first = false;
                        }
                        else
                        {
                            if (name == "IHDR") return false;
                            uint remaining = length;
                            while (remaining > 0)
                            {
                                int count = (int)Math.Min((uint)buffer.Length, remaining);
                                ReadExactly(stream, buffer, 0, count); crc = Crc(crc, buffer, count); remaining -= (uint)count;
                            }
                        }
                        if ((crc ^ 0xFFFFFFFF) != ReadU32(stream)) return false;
                        if (name == "IDAT") data = true;
                        if (name == "IEND") return length == 0 && data && stream.Position == stream.Length;
                    }
                }
            }
            catch { }
            return false;
        }

        private static Dictionary<string, object> Entry(string root, string key)
        {
            Dictionary<string, object> document = ReadObject(Path.Combine(root, "data", "library", "taunts.json"));
            object rows;
            if (!document.TryGetValue("taunts", out rows) || !(rows is IEnumerable)) throw new InvalidDataException("The taunt library has no animation list.");
            var matches = ((IEnumerable)rows).Cast<object>().OfType<Dictionary<string, object>>().Where(row => Text(row, "key") == key).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("The requested taunt has no unique playable animation record: " + key);
            return matches[0];
        }

        private static string[] MotionFiles(string root, Dictionary<string, object> entry)
        {
            string clip = Text(entry, "clip"); ValidateKey(clip);
            var result = new List<string> { Path.Combine(root, "data", "gamepack", "decoded", clip + ".npz") };
            string face = Text(entry, "face");
            if (!string.IsNullOrWhiteSpace(face))
            {
                if (Path.GetFileName(face) != face || face.IndexOf('\\') >= 0 || face.IndexOf('/') >= 0) throw new InvalidDataException("The taunt facial animation path is unsupported.");
                result.Add(Path.Combine(root, "data", "library", "faces", face));
            }
            foreach (string path in result) if (!File.Exists(path)) throw new FileNotFoundException("The requested taunt needs its extracted motion data.", path);
            return result.ToArray();
        }

        private static IEnumerable<string> ModelFiles(string root)
        {
            var files = new List<string>();
            var pending = new Stack<Tuple<string, int>>(); pending.Push(Tuple.Create(root, 0));
            int directories = 0;
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (next.Item2 > 32 || ++directories > 4096 || Linked(next.Item1)) throw new InvalidDataException("The shared Randy model tree is unsupported.");
                files.AddRange(Directory.GetFiles(next.Item1));
                if (files.Count > 8192) throw new InvalidDataException("The shared Randy model contains too many dependency files.");
                foreach (string child in Directory.GetDirectories(next.Item1)) pending.Push(Tuple.Create(child, next.Item2 + 1));
            }
            return files.OrderBy(path => path, PathComparer);
        }

        private static IEnumerable<string> SharedModelDirectories(string applicationRoot, string modelRoot)
        {
            var stocks = new List<string> { Path.Combine(applicationRoot, "data", "gamepack", "stock") };
            DirectoryInfo parent = Directory.GetParent(modelRoot);
            if (parent != null && string.Equals(parent.Name, "Characters", StringComparison.OrdinalIgnoreCase) && parent.Parent != null)
                stocks.Add(parent.Parent.FullName);
            return stocks.SelectMany(stock => new[] { Path.Combine(stock, "Characters", "_Global"), Path.Combine(stock, "CAS", "Teeth") })
                .Where(Directory.Exists).Distinct(PathComparer).OrderBy(path => path, PathComparer);
        }

        private static bool RandyLabel(object value)
        {
            var labels = value as IEnumerable;
            if (labels == null || value is string) return false;
            string[] rows = labels.Cast<object>().Select(row => Convert.ToString(row, CultureInfo.InvariantCulture)).ToArray();
            return rows.Length > 0 && rows.All(row => string.Equals(row, "preview on Randy Orton", StringComparison.OrdinalIgnoreCase)
                || string.Equals(row, "161 Randy Orton", StringComparison.OrdinalIgnoreCase));
        }

        private static string Semantic(Dictionary<string, object> row)
        {
            return Canonical(row.Where(pair => !DisplayFields.Contains(pair.Key, StringComparer.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        private static string Canonical(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null) return "{" + string.Join(",", dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => PreviewJson.Write(pair.Key) + ":" + Canonical(pair.Value))) + "}";
            var values = value as IEnumerable;
            if (values != null && !(value is string)) return "[" + string.Join(",", values.Cast<object>().Select(Canonical)) + "]";
            return PreviewJson.Write(value);
        }
        private static void WriteFile(BinaryWriter writer, string path, bool content)
        {
            var file = new FileInfo(path); long length = file.Length, ticks = file.LastWriteTimeUtc.Ticks;
            writer.Write(Full(path)); writer.Write(length); writer.Write(ticks);
            if (content) writer.Write(Hash(path));
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks) throw new IOException("A Randy preview input changed while it was read.");
        }
        private static void WriteMotion(BinaryWriter writer, string path)
        {
            var file = new FileInfo(path); long length = file.Length, ticks = file.LastWriteTimeUtc.Ticks;
            writer.Write(Path.GetFileName(path)); writer.Write(length); writer.Write(Hash(path));
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks)
                throw new IOException("A Randy preview motion changed while it was read.");
        }
        private static string CacheFolder(string root, RandyTauntPreviewContext context)
        {
            if (!Regex.IsMatch(context.Fingerprint ?? "", "\\A[0-9a-f]{64}\\z")) throw new InvalidDataException("Invalid Randy preview fingerprint.");
            return Path.Combine(Full(root), context.Fingerprint);
        }
        private static Dictionary<string, object> ReadObject(string path)
        {
            var file = new FileInfo(path); if (!file.Exists || file.Length > 16 * 1024 * 1024) throw new InvalidDataException("The taunt metadata file is unavailable or too large.");
            return PreviewJson.Read(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid taunt metadata.");
        }
        private static void ValidateKey(string key)
        { if (!Regex.IsMatch(key ?? "", "\\A[A-Za-z0-9_]+\\z")) throw new InvalidDataException("Invalid taunt animation key."); }
        private static string Full(string path) { return Path.GetFullPath(path ?? throw new ArgumentNullException("path")); }
        private static StringComparer PathComparer { get { return Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal; } }
        private static bool Linked(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        private static string Text(IDictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? value as string : null; }
        private static long Number(IDictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0; }
        private static string Hash(string path)
        {
            var file = new FileInfo(path); long length = file.Length, ticks = file.LastWriteTimeUtc.Ticks;
            string result;
            using (var stream = File.OpenRead(path)) using (SHA256 hash = SHA256.Create()) result = Hex(hash.ComputeHash(stream));
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks) throw new IOException("A Randy preview input changed while it was hashed.");
            return result;
        }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static uint ReadU32(Stream stream) { byte[] bytes = new byte[4]; ReadExactly(stream, bytes, 0, 4); return BigU32(bytes, 0); }
        private static uint BigU32(byte[] bytes, int offset) { return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]; }
        private static void ReadExactly(Stream stream, byte[] bytes, int offset, int count) { while (count > 0) { int read = stream.Read(bytes, offset, count); if (read == 0) throw new EndOfStreamException(); offset += read; count -= read; } }
        private static uint Crc(uint value, byte[] bytes, int count) { for (int index = 0; index < count; index++) value = CrcTable[(value ^ bytes[index]) & 255] ^ (value >> 8); return value; }
        private static uint[] MakeCrcTable() { var table = new uint[256]; for (uint index = 0; index < table.Length; index++) { uint value = index; for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1; table[index] = value; } return table; }
    }
}
