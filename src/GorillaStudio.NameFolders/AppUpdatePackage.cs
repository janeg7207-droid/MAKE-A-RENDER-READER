using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public sealed class AppUpdateManifest
    {
        public int Schema { get; internal set; }
        public int MinimumUpdaterVersion { get; internal set; }
        public Version Version { get; internal set; }
        public string VersionText { get; internal set; }
        public Uri DownloadUri { get; internal set; }
        public long Size { get; internal set; }
        public string Sha256 { get; internal set; }
    }
    public sealed class AppUpdatePayloadFile
    {
        public string Name { get; internal set; }
        public long Length { get; internal set; }
        public string Sha256 { get; internal set; }
    }
    public sealed class AppUpdatePayload
    {
        public AppUpdateManifest Manifest { get; internal set; }
        public string PackagePath { get; internal set; }
        public IReadOnlyList<AppUpdatePayloadFile> Files { get; internal set; }
    }

    /// <summary>Validates the small update format without extracting or executing it.</summary>
    public static class AppUpdatePackage
    {
        public const int UpdaterVersion = 1;
        public const int MaximumManifestBytes = 64 * 1024;
        public const long MaximumPackageBytes = 32L * 1024 * 1024;
        public const long MaximumExpandedBytes = 64L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly string[] Required = {
            "GorillaStudio.exe", "GorillaStudio.NameFolders.dll", "GorillaStudio.Updater.exe", "Pfim.dll",
            "README_DISCOVERY.txt", "TEST_RESULTS.json", "BUILD_SHA256.json", "rosters/Discovery_roster.json",
            "third_party/Pfim/LICENSE.txt", "third_party/Pfim/NOTICE.txt", "engine/gr_render.py"
        };
        public static IReadOnlyList<string> ApprovedFiles { get; } = Array.AsReadOnly(Required);
        private static readonly string[] CriticalHashes = {
            "GorillaStudio.exe", "GorillaStudio.NameFolders.dll", "GorillaStudio.Updater.exe", "Pfim.dll", "rosters/Discovery_roster.json", "engine/gr_render.py"
        };

        public static AppUpdateManifest ParseManifest(byte[] utf8)
        {
            if (utf8 == null || utf8.Length == 0 || utf8.Length > MaximumManifestBytes) throw Invalid("The update feed is empty or too large.");
            var data = Json(Decode(utf8));
            var allowed = new HashSet<string>(new[] { "schema", "version", "minimumUpdaterVersion", "url", "size", "sha256" }, StringComparer.Ordinal);
            if (data.Count != allowed.Count || data.Keys.Any(key => !allowed.Contains(key)) || Number(data, "schema") != 1)
                throw Invalid("The update feed schema is not supported.");
            long minimum = Number(data, "minimumUpdaterVersion");
            if (minimum < 1 || minimum > int.MaxValue) throw Invalid("The minimum updater version is invalid.");
            return CreateManifest(Text(data, "version"), Text(data, "url"), Number(data, "size"), Text(data, "sha256"), (int)minimum);
        }

        public static AppUpdateManifest CreateManifest(string version, string url, long size, string sha256, int minimumUpdaterVersion = 1)
        {
            Version parsed = ParseVersion(version);
            if (minimumUpdaterVersion < 1 || minimumUpdaterVersion > UpdaterVersion)
                throw new NotSupportedException("This update requires a newer updater. Download the full app ZIP.");
            if (size < 1 || size > MaximumPackageBytes) throw Invalid("The update download size is outside the supported limit.");
            if (!ValidHash(sha256)) throw Invalid("The update SHA-256 is invalid.");
            if (url == null || !Regex.IsMatch(url,
                @"\Ahttps://raw\.githubusercontent\.com/janeg7207-droid/MAKE-A-RENDER-READER/[0-9a-fA-F]{40}/downloads/[A-Za-z0-9][A-Za-z0-9._-]*_Update\.zip\z", RegexOptions.CultureInvariant))
                throw Invalid("Update downloads must use an immutable commit in the app's GitHub repository.");
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw Invalid("The update URL is invalid.");
            return new AppUpdateManifest { Schema = 1, MinimumUpdaterVersion = minimumUpdaterVersion, Version = parsed,
                VersionText = version, DownloadUri = uri, Size = size, Sha256 = sha256.ToLowerInvariant() };
        }

        public static Version ParseVersion(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)) throw Invalid("The update version is invalid.");
            int[] parts = value.Split('.').Select(part => { int number; if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number > 65535) throw Invalid("The update version is outside the supported range."); return number; }).ToArray();
            return new Version(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : 0);
        }

        public static int CompareVersions(Version first, Version second)
        {
            if (first == null || second == null) throw new ArgumentNullException(first == null ? nameof(first) : nameof(second));
            return new Version(first.Major, first.Minor, Math.Max(0, first.Build), Math.Max(0, first.Revision))
                .CompareTo(new Version(second.Major, second.Minor, Math.Max(0, second.Build), Math.Max(0, second.Revision)));
        }

        public static AppUpdatePayload Validate(string zipPath, AppUpdateManifest manifest, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest = CreateManifest(manifest.VersionText, manifest.DownloadUri == null ? null : manifest.DownloadUri.OriginalString,
                manifest.Size, manifest.Sha256, manifest.MinimumUpdaterVersion);
            string path = Path.GetFullPath(zipPath);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Invalid("An update ZIP cannot be a linked file.");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length != manifest.Size || Hash(file, manifest.Size, cancellationToken) != manifest.Sha256) throw Invalid("The update ZIP does not match its advertised size and SHA-256.");
                file.Position = 0;
                var files = new List<AppUpdatePayloadFile>();
                var metadata = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read, true))
                {
                    if (archive.Entries.Count != Required.Length) throw Invalid("The update ZIP must contain exactly the eleven approved files.");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long total = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string name = entry.FullName;
                        if (!Required.Contains(name, StringComparer.Ordinal) || !seen.Add(name)) throw Invalid("The update ZIP contains an unapproved or duplicate path: " + name);
                        uint mode = (uint)entry.ExternalAttributes >> 16;
                        if (((mode & 0xf000) != 0 && (mode & 0xf000) != 0x8000) || (entry.ExternalAttributes & 0x400) != 0)
                            throw Invalid("Linked or special ZIP entries are not supported.");
                        if (entry.Length < 1 || entry.Length > MaximumExpandedBytes || total > MaximumExpandedBytes - entry.Length) throw Invalid("The expanded update is too large or contains an empty file.");
                        total += entry.Length;
                        bool isMetadata = name == "BUILD_SHA256.json" || name == "TEST_RESULTS.json";
                        if (isMetadata && entry.Length > 4 * 1024 * 1024) throw Invalid("Update metadata is too large.");
                        string digest;
                        using (Stream stream = entry.Open())
                        {
                            if (isMetadata)
                            {
                                byte[] bytes = ReadBounded(stream, entry.Length, cancellationToken);
                                metadata.Add(name, bytes);
                                using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(bytes));
                            }
                            else digest = Hash(stream, entry.Length, cancellationToken);
                        }
                        files.Add(new AppUpdatePayloadFile { Name = name, Length = entry.Length, Sha256 = digest });
                    }
                }
                var test = Json(Decode(metadata["TEST_RESULTS.json"]));
                if (CompareVersions(ParseVersion(Text(test, "helper_version")), manifest.Version) != 0) throw Invalid("The packaged helper version differs from the update feed.");
                var build = Json(Decode(metadata["BUILD_SHA256.json"]));
                var byName = files.ToDictionary(item => item.Name, StringComparer.Ordinal);
                foreach (var hash in build)
                {
                    string expected = hash.Value as string;
                    if (!ValidHash(expected)) throw Invalid("The build hash list contains an invalid SHA-256.");
                    if (hash.Key == "engine/gs_engine.exe") continue; // Full-package provenance only; never extract/apply it.
                    AppUpdatePayloadFile payload;
                    if (!byName.TryGetValue(hash.Key, out payload) || !string.Equals(payload.Sha256, expected, StringComparison.OrdinalIgnoreCase)) throw Invalid("The build hash does not match an approved payload file: " + hash.Key);
                }
                if (CriticalHashes.Any(name => !build.ContainsKey(name))) throw Invalid("The update build hash list is incomplete.");
                file.Position = 0;
                if (Hash(file, manifest.Size, cancellationToken) != manifest.Sha256) throw Invalid("The update ZIP changed during validation.");
                return new AppUpdatePayload { Manifest = manifest, PackagePath = path, Files = files.AsReadOnly() };
            }
        }

        private static string Hash(Stream stream, long expected, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); if (count > expected - total) throw Invalid("An update file exceeds its declared length."); total += count; sha.TransformBlock(buffer, 0, count, buffer, 0); }
                if (total != expected) throw Invalid("An update file is truncated.");
                sha.TransformFinalBlock(new byte[0], 0, 0); return Hex(sha.Hash);
            }
        }
        private static byte[] ReadBounded(Stream stream, long expected, CancellationToken token)
        { using (var output = new MemoryStream()) { var buffer = new byte[8192]; int count; while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); if (count > expected - output.Length) throw Invalid("Update metadata exceeds its declared size."); output.Write(buffer, 0, count); } if (output.Length != expected) throw Invalid("Update metadata is truncated."); return output.ToArray(); } }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static bool ValidHash(string value) { return value != null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant); }
        private static string Decode(byte[] bytes) { int skip = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0; return Utf8.GetString(bytes, skip, bytes.Length - skip); }
        private static string Text(Dictionary<string, object> map, string key) { object value; if (!map.TryGetValue(key, out value) || !(value is string)) throw Invalid("The update metadata is missing " + key + "."); return (string)value; }
        private static long Number(Dictionary<string, object> map, string key) { object value; if (!map.TryGetValue(key, out value) || !(value is decimal) || (decimal)value != decimal.Truncate((decimal)value) || (decimal)value < long.MinValue || (decimal)value > long.MaxValue) throw Invalid("The update metadata has an invalid " + key + "."); return (long)(decimal)value; }
        private static InvalidDataException Invalid(string message) { return new InvalidDataException(message); }
        // One parser is used on .NET Framework and .NET 8. In particular, repeated
        // JSON keys cannot silently select different versions/hashes on each runtime.
        private static Dictionary<string, object> Json(string text) { var parsed = new StrictJson(text).Parse() as Dictionary<string, object>; if (parsed == null) throw Invalid("Update metadata must be a JSON object."); return parsed; }
        private sealed class StrictJson
        {
            private readonly string source; private int at;
            internal StrictJson(string text) { source = text; }
            internal object Parse() { object result = Value(0); Space(); if (at != source.Length) throw Invalid("Trailing update JSON data."); return result; }
            private object Value(int depth)
            {
                if (depth > 64) throw Invalid("Update JSON is nested too deeply."); Space(); if (at == source.Length) throw Invalid("Truncated update JSON.");
                char character = source[at];
                if (character == '{')
                {
                    at++; var map = new Dictionary<string, object>(StringComparer.Ordinal); Space(); if (Take('}')) return map;
                    do { Space(); if (at == source.Length || source[at] != '"') throw Invalid("Invalid update JSON property."); string name = String(); Space(); Require(':'); if (map.ContainsKey(name)) throw Invalid("Repeated update JSON property: " + name); map.Add(name, Value(depth + 1)); Space(); if (Take('}')) return map; Require(','); } while (true);
                }
                if (character == '[')
                { at++; var list = new List<object>(); Space(); if (Take(']')) return list; do { list.Add(Value(depth + 1)); Space(); if (Take(']')) return list; Require(','); } while (true); }
                if (character == '"') return String();
                foreach (string word in new[] { "true", "false", "null" })
                    if (source.Length - at >= word.Length && string.CompareOrdinal(source, at, word, 0, word.Length) == 0) { at += word.Length; return word == "null" ? null : (object)(word == "true"); }
                int begin = at; while (at < source.Length && "-+0123456789.eE".IndexOf(source[at]) >= 0) at++;
                string number = source.Substring(begin, at - begin); decimal parsed;
                if (!Regex.IsMatch(number, @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z") || !decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) throw Invalid("Invalid update JSON value.");
                return parsed;
            }
            private string String()
            {
                Require('"'); var text = new StringBuilder();
                while (at < source.Length)
                {
                    char character = source[at++]; if (character == '"') return text.ToString();
                    if (character < 32) throw Invalid("Unescaped control character in update JSON.");
                    if (character != '\\') { text.Append(character); continue; }
                    if (at == source.Length) break; char escape = source[at++];
                    switch (escape)
                    {
                        case '"': case '\\': case '/': text.Append(escape); break;
                        case 'b': text.Append('\b'); break; case 'f': text.Append('\f'); break; case 'n': text.Append('\n'); break; case 'r': text.Append('\r'); break; case 't': text.Append('\t'); break;
                        case 'u':
                            if (source.Length - at < 4) throw Invalid("Truncated update JSON escape.");
                            ushort code; if (!ushort.TryParse(source.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw Invalid("Invalid update JSON escape.");
                            text.Append((char)code); at += 4; break;
                        default: throw Invalid("Unknown update JSON escape.");
                    }
                }
                throw Invalid("Unterminated update JSON string.");
            }
            private void Space() { while (at < source.Length && (source[at] == ' ' || source[at] == '\r' || source[at] == '\n' || source[at] == '\t')) at++; }
            private bool Take(char value) { if (at < source.Length && source[at] == value) { at++; return true; } return false; }
            private void Require(char value) { if (!Take(value)) throw Invalid("Invalid update JSON separator."); }
        }
    }
}
