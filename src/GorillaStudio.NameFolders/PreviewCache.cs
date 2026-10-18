using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    /// <summary>Only completed quick previews may bypass the rendering engine.</summary>
    public static class PreviewCache
    {
        public static bool IsPreviewRequest(string script, IEnumerable<string> args)
        {
            if (string.IsNullOrWhiteSpace(script) || args == null) return false;
            if (!string.Equals(Path.GetFileNameWithoutExtension(script.Replace('\\', '/')), "gorilla_render", StringComparison.OrdinalIgnoreCase)) return false;
            string quick = null;
            foreach (string argument in args)
            {
                if (argument == null) continue;
                if (argument.StartsWith("prepare=", StringComparison.Ordinal) || argument == "prepare") return false;
                if (argument.StartsWith("quick=", StringComparison.Ordinal)) quick = argument.Substring(6);
            }
            return quick == "1";
        }

        /// <summary>Returns null if an input cannot be inspected; callers must render normally in that case.</summary>
        public static string Key(IEnumerable<string> args, bool fast, IEnumerable<string> inputFiles, string extraSignature = null)
        {
            if (args == null || inputFiles == null) return null;
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), true))
                {
                    writer.Write("GorillaStudio-preview-cache-v1");
                    writer.Write(fast);
                    writer.Write(extraSignature ?? "");
                    string[] arguments = args.ToArray();
                    writer.Write(arguments.Length);
                    foreach (string argument in arguments) { writer.Write(argument != null); if (argument != null) writer.Write(argument); }
                    StringComparer comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    string[] paths = inputFiles.Select(path => Path.GetFullPath(path)).Distinct(comparer).OrderBy(path => path, comparer).ToArray();
                    writer.Write(paths.Length);
                    foreach (string path in paths)
                    {
                        var file = new FileInfo(path);
                        if (!file.Exists) return null;
                        long length = file.Length;
                        long ticks = file.LastWriteTimeUtc.Ticks;
                        file.Refresh();
                        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != ticks) return null;
                        writer.Write(path); writer.Write(length); writer.Write(ticks);
                    }
                    writer.Flush();
                    stream.Position = 0;
                    using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException || error is System.Security.SecurityException)
            {
                return null;
            }
        }

        internal static string Hex(byte[] bytes)
        {
            var result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }

    /// <summary>Immutable preview artifacts; cache damage or contention always falls back to rendering.</summary>
    public sealed class PreviewCacheStore
    {
        private const int FormatVersion = 1;
        private static readonly string[] ArtifactNames = { "preview", "play", "game_picture" };
        private readonly string root;

        public PreviewCacheStore(string root)
        {
            this.root = Path.GetFullPath(root);
        }

        public bool TryRead(string key, out List<string> lines)
        {
            lines = null;
            if (!ValidKey(key)) return false;
            try
            {
                string folder = Path.Combine(root, key);
                var manifest = PreviewJson.Read(File.ReadAllText(Path.Combine(folder, "manifest.json")));
                if (manifest == null || Number(manifest, "version") != FormatVersion || Text(manifest, "key") != key) return false;
                string result = Text(manifest, "result");
                Dictionary<string, object> decoded = PreviewJson.Read(result);
                if (!ValidResult(decoded)) return false;
                object artifactsValue;
                var artifacts = manifest.TryGetValue("artifacts", out artifactsValue) ? artifactsValue as Dictionary<string, object> : null;
                if (artifacts == null || artifacts.Count == 0) return false;
                foreach (string name in ArtifactNames)
                {
                    string path = Text(decoded, name);
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    object artifactValue;
                    var artifact = artifacts.TryGetValue(name, out artifactValue) ? artifactValue as Dictionary<string, object> : null;
                    string expected = Path.Combine(folder, name + ".png");
                    if (artifact == null || !string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase)) return false;
                    var file = new FileInfo(expected);
                    if (!file.Exists || file.Length != Number(artifact, "length") || file.LastWriteTimeUtc.Ticks != Number(artifact, "mtime") || !ValidPng(expected) || Hash(expected) != Text(artifact, "sha256")) return false;
                }
                lines = new List<string> { "STAGE 9/9 Cached preview", "RESULT " + result };
                return true;
            }
            catch { return false; }
        }

        /// <summary>Call only after a successful engine exit. Full .gmr render results are never stored.</summary>
        public bool Save(string key, IEnumerable<string> engineLines)
        {
            if (!ValidKey(key) || engineLines == null) return false;
            string temporary = null;
            try
            {
                string line = engineLines.LastOrDefault(value => value != null && value.StartsWith("RESULT ", StringComparison.Ordinal));
                if (line == null) return false;
                Dictionary<string, object> result = PreviewJson.Read(line.Substring(7));
                if (!ValidResult(result)) return false;
                List<string> existing;
                if (TryRead(key, out existing)) return true;
                Directory.CreateDirectory(root);
                string destination = Path.Combine(root, key);
                temporary = Path.Combine(root, ".writing-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                var artifacts = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string name in ArtifactNames)
                {
                    string source = Text(result, name);
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    if (!File.Exists(source) || !ValidPng(source)) return false;
                    string copy = Path.Combine(temporary, name + ".png");
                    File.Copy(source, copy, false);
                    if (!ValidPng(copy)) return false;
                    var file = new FileInfo(copy);
                    artifacts.Add(name, new Dictionary<string, object> {
                        { "length", file.Length }, { "mtime", file.LastWriteTimeUtc.Ticks }, { "sha256", Hash(copy) }
                    });
                    result[name] = Path.Combine(destination, name + ".png");
                }
                var manifest = new Dictionary<string, object> {
                    { "version", FormatVersion }, { "key", key }, { "result", PreviewJson.Write(result) }, { "artifacts", artifacts }
                };
                File.WriteAllText(Path.Combine(temporary, "manifest.json"), PreviewJson.Write(manifest), new UTF8Encoding(false));
                if (Directory.Exists(destination))
                {
                    if (TryRead(key, out existing)) return true;
                    Directory.Delete(destination, true);
                }
                Directory.Move(temporary, destination);
                temporary = null;
                Prune(key);
                return true;
            }
            catch { return false; }
            finally
            {
                if (temporary != null) try { Directory.Delete(temporary, true); } catch { }
            }
        }

        private void Prune(string keep)
        {
            try
            {
                var folders = new DirectoryInfo(root).GetDirectories().Where(directory => ValidKey(directory.Name))
                    .OrderByDescending(directory => directory.Name == keep).ThenByDescending(directory => directory.LastWriteTimeUtc).ToList();
                long total = 0;
                int count = 0;
                foreach (DirectoryInfo directory in folders)
                {
                    long size = directory.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                    if (directory.Name == keep || (count < 64 && total + size <= 512L * 1024 * 1024)) { count++; total += size; }
                    else try { directory.Delete(true); } catch { }
                }
            }
            catch { }
        }

        private static bool ValidKey(string key)
        {
            return key != null && key.Length == 64 && key.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
        }

        private static bool ValidResult(Dictionary<string, object> result)
        {
            if (result == null || result.Keys.Any(key => string.Equals(key, "gmr", StringComparison.OrdinalIgnoreCase))) return false;
            if (string.IsNullOrWhiteSpace(Text(result, "name")) || !result.ContainsKey("id") || Number(result, "frames") < 1) return false;
            if (string.IsNullOrWhiteSpace(Text(result, "preview")) && string.IsNullOrWhiteSpace(Text(result, "play"))) return false;
            if (!string.IsNullOrWhiteSpace(Text(result, "play")) && DecimalNumber(result, "play_fps") <= 0) return false;
            return true;
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value as string : null;
        }

        private static long Number(Dictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) && value != null ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : -1;
        }

        private static double DecimalNumber(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : -1;
        }

        private static string Hash(string path)
        {
            using (var file = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create()) return PreviewCache.Hex(sha.ComputeHash(file));
        }

        private static bool ValidPng(string path)
        {
            using (var file = File.OpenRead(path))
            using (var reader = new BinaryReader(file))
            {
                byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                if (file.Length < 45 || !reader.ReadBytes(8).SequenceEqual(signature)) return false;
                bool first = true, hasData = false;
                while (file.Position <= file.Length - 12)
                {
                    uint size = BigEndian(reader.ReadBytes(4));
                    string kind = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    if ((long)size > file.Length - file.Position - 4) return false;
                    if (first)
                    {
                        if (kind != "IHDR" || size != 13) return false;
                        if (BigEndian(reader.ReadBytes(4)) == 0 || BigEndian(reader.ReadBytes(4)) == 0) return false;
                        file.Position += size - 8;
                        first = false;
                    }
                    else file.Position += size;
                    if (kind == "IDAT" && size > 0) hasData = true;
                    file.Position += 4;
                    if (kind == "IEND") return hasData && size == 0 && file.Position == file.Length;
                }
                return false;
            }
        }

        private static uint BigEndian(byte[] bytes)
        {
            if (bytes.Length != 4) throw new EndOfStreamException();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
    }

    internal static class PreviewJson
    {
        internal static Dictionary<string, object> Read(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json)) return ConvertJson(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
#endif
        }

        internal static string Write(object value)
        {
#if NET8_0_OR_GREATER
            return JsonSerializer.Serialize(value);
#else
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(value);
#endif
        }

#if NET8_0_OR_GREATER
        private static object ConvertJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object: return element.EnumerateObject().ToDictionary(property => property.Name, property => ConvertJson(property.Value));
                case JsonValueKind.Array: return element.EnumerateArray().Select(ConvertJson).ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number: long integer; return element.TryGetInt64(out integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
