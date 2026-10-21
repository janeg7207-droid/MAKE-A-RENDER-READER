using System;
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
    /// <summary>Resolves the native taunt-key facial source without changing animation data.</summary>
    public static class TauntFacialSources
    {
        private const int MaximumIndexBytes = 4 * 1024 * 1024;
        private const double MaximumOffsetFrames = 1000000;

        public static bool UsesIndexedFace(IDictionary<string, object> entry)
        {
            double offset;
            return Text(entry, "face") == "facial_6368.npz" && Offset(entry, "face_offset", out offset) && offset == 0;
        }

        /// <summary>
        /// Fingerprints the effective selected facial file and frame offset.
        /// Unused index records, paths outside the selected gender, formatting,
        /// timestamps and private overlay directory names do not affect it.
        /// </summary>
        public static string Signature(string root, IDictionary<string, object> entry, string gender)
        {
            string face = Text(entry, "face");
            double offset;
            bool validOffset = Offset(entry, "face_offset", out offset);
            if (face == "facial_6368.npz" && validOffset && offset == 0)
            {
                try
                {
                    Dictionary<string, object> index;
                    object value;
                    string key = Text(entry, "key");
                    if (key != null && TryReadIndex(root, out index) && index.TryGetValue(key, out value))
                    {
                        var record = value as IDictionary<string, object>;
                        string requested = string.IsNullOrEmpty(gender) ? "male" : gender;
                        string relative = Text(record, requested);
                        if (string.IsNullOrEmpty(relative)) relative = Text(record, "male");
                        string normalized, full;
                        double ownOffset;
                        if (record != null && Offset(record, "offset", out ownOffset)
                            && FacialPath(root, relative, out normalized, out full))
                            return Fingerprint("own", normalized, ownOffset, HashFile(full));
                    }
                }
                catch (Exception error) when (SourceError(error)) { }
            }
            // The native engine retains this library face when own-face lookup
            // is gated off, absent, malformed or points to a missing file.
            try
            {
                string normalized, full;
                if (validOffset && RelativePath(Path.Combine(Path.GetFullPath(root), "data", "library", "faces"),
                    face, out normalized, out full))
                    return Fingerprint("library", normalized, offset, HashFile(full));
            }
            catch (Exception error) when (SourceError(error)) { }
            return Fingerprint("unavailable", face ?? "", validOffset ? offset : 0, validOffset ? "missing" : "invalid-offset");
        }

        public static bool TryReadAlias(string root, string sourceKey, out Dictionary<string, object> record)
        {
            record = null;
            Dictionary<string, object> index;
            return TryReadIndex(root, out index) && TryReadAlias(root, index, sourceKey, out record);
        }

        /// <summary>Validates a record from an already bounded, parsed facial index.</summary>
        public static bool TryReadAlias(string root, IDictionary<string, object> index, string sourceKey,
            out Dictionary<string, object> record)
        {
            record = null;
            try
            {
                object value;
                if (index == null || sourceKey == null || !index.TryGetValue(sourceKey, out value)) return false;
                var source = value as IDictionary<string, object>;
                double offset;
                string male, female, full;
                if (source == null || !Offset(source, "offset", out offset)
                    || !FacialPath(root, Text(source, "male"), out male, out full)
                    || !FacialPath(root, Text(source, "female"), out female, out full)) return false;
                record = new Dictionary<string, object>(source, StringComparer.Ordinal);
                record["male"] = male; record["female"] = female; record["offset"] = offset;
                return true;
            }
            catch (Exception error) when (SourceError(error)) { return false; }
        }

        private static bool TryReadIndex(string root, out Dictionary<string, object> index)
        {
            index = null;
            try
            {
                string path = Path.Combine(Path.GetFullPath(root), "data", "faces", "index.json");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length <= 0 || stream.Length > MaximumIndexBytes) return false;
                    using (var data = new MemoryStream())
                    {
                        var buffer = new byte[16384];
                        int length;
                        while ((length = stream.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            if (data.Length + length > MaximumIndexBytes) return false;
                            data.Write(buffer, 0, length);
                        }
                        string json = new UTF8Encoding(false, true).GetString(data.ToArray()).TrimStart('\uFEFF');
                        index = ReadObject(json);
                        return index != null;
                    }
                }
            }
            catch (Exception error) when (SourceError(error)) { return false; }
        }

        private static bool FacialPath(string root, string relative, out string normalized, out string full)
        {
            return RelativePath(Path.Combine(Path.GetFullPath(root), "data", "faces"), relative, out normalized, out full);
        }

        private static bool RelativePath(string directory, string relative, out string normalized, out string full)
        {
            normalized = full = null;
            if (string.IsNullOrWhiteSpace(relative)) return false;
            string path = relative.Replace('\\', '/');
            string[] parts = path.Split('/');
            if (Path.IsPathRooted(path) || path.IndexOf(':') >= 0 || path.Any(character => char.IsControl(character) || character == '"')
                || parts.Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal))
                || !string.Equals(Path.GetExtension(path), ".npz", StringComparison.OrdinalIgnoreCase)) return false;
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string file = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || !File.Exists(file)) return false;
            normalized = path; full = file; return true;
        }

        private static bool Offset(IDictionary<string, object> values, string key, out double offset)
        {
            offset = 0;
            object value;
            if (values == null) return false;
            if (!values.TryGetValue(key, out value)) return true;
            if (value == null || value is bool || value is string || value is char) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal:
                    offset = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return !double.IsNaN(offset) && !double.IsInfinity(offset) && Math.Abs(offset) <= MaximumOffsetFrames;
                default: return false;
            }
        }

        private static string Text(IDictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value as string : null;
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        private static string Fingerprint(string mode, string relative, double offset, string hash)
        {
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, new UTF8Encoding(false), true))
            {
                writer.Write("taunt-facial-source-v1"); writer.Write(mode); writer.Write(relative);
                writer.Write(offset.ToString("R", CultureInfo.InvariantCulture)); writer.Write(hash);
                writer.Flush(); data.Position = 0;
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
            }
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static Dictionary<string, object> ReadObject(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json))
                return JsonValue(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumIndexBytes }.DeserializeObject(json) as Dictionary<string, object>;
#endif
        }

#if NET8_0_OR_GREATER
        private static object JsonValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: return value.EnumerateObject().ToDictionary(item => item.Name, item => JsonValue(item.Value));
                case JsonValueKind.Array: return value.EnumerateArray().Select(JsonValue).ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number: long integer; return value.TryGetInt64(out integer) ? (object)integer : value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif

        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static bool SourceError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is System.Security.SecurityException || error is NotSupportedException
                || error is FormatException || error is OverflowException
#if NET8_0_OR_GREATER
                || error is System.Text.Json.JsonException
#endif
                ;
        }
    }
}
