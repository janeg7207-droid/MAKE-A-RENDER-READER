using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryMatchupEntry
    {
        public int Id { get; internal set; }
        public int ReferenceId { get; internal set; }
        public string Name { get; internal set; }
        public List<string> Names { get; } = new List<string>();
        public List<string> TemplateNames { get; } = new List<string>();
        public HashSet<ulong> AssetHashes { get; } = new HashSet<ulong>();
        public List<string> AssetPaths { get; } = new List<string>();
        public List<string> SourceFiles { get; } = new List<string>();
        public List<int> ShowLoadIds { get; } = new List<int>();
    }

    public sealed class DiscoveryMatchupCatalogResult
    {
        public List<DiscoveryMatchupEntry> Entries { get; } = new List<DiscoveryMatchupEntry>();
        public HashSet<ulong> RequiredHashes { get; } = new HashSet<ulong>();
        public List<string> ScreenFiles { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public int TableCount { get; internal set; }
    }

    /// <summary>
    /// Rereads current Discovery show and arena references. A display/template name
    /// never becomes a guessed file path; only an exact FNV match establishes one.
    /// </summary>
    public static class DiscoveryMatchupCatalog
    {
        private const int MaximumTableBytes = 128 * 1024 * 1024;

        public static DiscoveryMatchupCatalogResult Read(string discoveryRoot)
        {
            var result = new DiscoveryMatchupCatalogResult();
            if (string.IsNullOrWhiteSpace(discoveryRoot) || !Directory.Exists(discoveryRoot))
            { result.Warnings.Add("Choose an existing Discovery data folder."); return result; }
            string root = Path.GetFullPath(discoveryRoot);
            var entries = new Dictionary<int, DiscoveryMatchupEntry>();
            var strings = new Dictionary<uint, string>();
            try
            {
                string sdb = Locate(root, "Sdb", "ENG.sdb");
                if (sdb != null) strings = DiscoveryProfiles.ReadStrings(sdb, result.Warnings);
            }
            catch (Exception error) when (FileError(error)) { result.Warnings.Add("Discovery names: " + error.Message); }

            ReadTable(root, "CreateShow", "MatchUpScreenAsset.jsfb", "StCr", 1, result, (file, row, path) =>
            {
                int idField = Sized(file, row, 0, 4);
                int id = idField == 0 ? 0 : PositiveId(file.U32(idField));
                if (id == 0) return;
                var entry = Entry(entries, id, path);
                int reference = Sized(file, row, 1, 4);
                if (reference != 0) entry.ReferenceId = PositiveId(file.U32(reference));
                foreach (int field in new[] { 2, 3 }) Add(entry.TemplateNames, ReadString(file, row, field));
                int hashField = Sized(file, row, 4, 8);
                if (hashField != 0 && file.U64(hashField) != 0) entry.AssetHashes.Add(file.U64(hashField));
                foreach (string candidate in entry.TemplateNames)
                {
                    string normalized = NormalizeAssetPath(candidate);
                    if (normalized != null && entry.AssetHashes.Contains(HashPath(normalized))) Add(entry.AssetPaths, normalized);
                }
            });
            // These menu fields are localization keys and preview/UI assets, not
            // match-card project hashes. Join names by ID without requesting them.
            foreach (string menu in new[] { "MatchupMenuAssetData.jsfb", "ShowLoadMenuAssetData.jsfb" })
                ReadTable(root, "CreateShow", menu, "StCr", 0, result, (file, row, path) =>
                {
                    int idField = Sized(file, row, 0, 4), nameField = Sized(file, row, 1, 4);
                    int id = idField == 0 ? 0 : PositiveId(file.U32(idField));
                    if (id == 0) return;
                    var entry = Entry(entries, id, path);
                    string name;
                    if (nameField != 0 && strings.TryGetValue(file.U32(nameField), out name)) Add(entry.Names, name);
                });
            ReadTable(root, "CreateShow", "ShowLoadData.jsfb", "StCr", 0, result, (file, row, path) =>
            {
                int field = Sized(file, row, 0, 4), load = Sized(file, row, 2, 4);
                int id = field == 0 ? 0 : PositiveId(file.U32(field));
                if (id == 0) return;
                var entry = Entry(entries, id, path);
                if (load != 0) { int value = PositiveId(file.U32(load)); if (value != 0 && !entry.ShowLoadIds.Contains(value)) entry.ShowLoadIds.Add(value); }
            });
            ReadTable(root, "Arena", "ArenaData.jsfb", "Aren", 0, result, (file, row, path) =>
            {
                int idField = Sized(file, row, 2, 2), nameField = Sized(file, row, 0, 4);
                int id = idField == 0 ? 0 : file.U16(idField);
                if (id == 0) return;
                var entry = Entry(entries, id, path);
                string name;
                if (nameField != 0 && strings.TryGetValue(file.U32(nameField), out name)) Add(entry.Names, name);
                // Arena second_id is retained separately by its source. It cannot
                // replace first_id: actual double-ring Survivor uses 625 and 525.
            });
            ReadArenaJson(root, entries, strings, result);

            foreach (var entry in entries.Values)
                foreach (ulong hash in entry.AssetHashes) result.RequiredHashes.Add(hash);
            // Discover actual Scre projects across the entire data root, regardless
            // of a mod's directory and filename. No asset is inferred from a label.
            foreach (string file in ProjectFiles(root, result.Warnings))
            {
                result.ScreenFiles.Add(file);
                string relative = Relative(root, file).Replace(Path.DirectorySeparatorChar, '/');
                ulong hash = HashPath(relative);
                foreach (var entry in entries.Values.Where(item => item.AssetHashes.Contains(hash))) Add(entry.AssetPaths, relative);
            }
            foreach (var entry in entries.Values.OrderBy(item => item.Id))
            {
                entry.Name = entry.Names.FirstOrDefault() ?? entry.TemplateNames.FirstOrDefault() ?? "Matchup " + entry.Id.ToString(CultureInfo.InvariantCulture);
                result.Entries.Add(entry);
            }
            return result;
        }

        private static void ReadTable(string root, string folder, string name, string identifier, int vector,
            DiscoveryMatchupCatalogResult result, Action<FlatBufferFile, int, string> read)
        {
            string path = Locate(root, folder, name);
            if (path == null) return;
            try
            {
                if (new FileInfo(path).Length > MaximumTableBytes) throw new InvalidDataException("Table is too large.");
                var file = new FlatBufferFile(File.ReadAllBytes(path), identifier);
                Sized(file, file.Root, vector, 4);
                foreach (int row in file.Tables(file.Root, vector)) read(file, row, path);
                result.TableCount++;
            }
            catch (Exception error) when (FileError(error)) { result.Warnings.Add(Relative(root, path) + ": " + error.Message); }
        }

        private static void ReadArenaJson(string root, Dictionary<int, DiscoveryMatchupEntry> entries,
            Dictionary<uint, string> strings, DiscoveryMatchupCatalogResult result)
        {
            string path = Locate(root, "Arena", "ArenaData.json");
            if (path == null) return;
            try
            {
                if (new FileInfo(path).Length > MaximumTableBytes) throw new InvalidDataException("Arena JSON is too large.");
                var document = Deserialize(File.ReadAllText(path));
                object rows;
                if (document == null || !document.TryGetValue("arenas", out rows) || !(rows is IList)) throw new InvalidDataException("Expected an arenas array.");
                foreach (object item in (IList)rows)
                {
                    var row = item as Dictionary<string, object>;
                    object value; uint id;
                    if (row == null || !row.TryGetValue("first_id", out value) || !UInt(value, out id) || id == 0 || id > int.MaxValue) continue;
                    var entry = Entry(entries, (int)id, path);
                    if (row.TryGetValue("arena_name", out value))
                    {
                        uint key; string name;
                        if (UInt(value, out key) && strings.TryGetValue(key, out name)) Add(entry.Names, name);
                        else if (value is string && !string.IsNullOrWhiteSpace((string)value)) Add(entry.Names, (string)value);
                    }
                }
                result.TableCount++;
            }
            catch (Exception error) when (FileError(error)) { result.Warnings.Add(Relative(root, path) + ": " + error.Message); }
        }

        private static IEnumerable<string> ProjectFiles(string root, List<string> warnings)
        {
            var result = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string child in Directory.GetDirectories(directory))
                    {
                        string name = Path.GetFileName(child);
                        if (name.Equals("Gorilla Studio Layout Backups", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Gorilla Studio Backups", StringComparison.OrdinalIgnoreCase) || name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                        pending.Push(child);
                    }
                    foreach (string file in Directory.GetFiles(directory))
                    {
                        if (!string.Equals(Path.GetExtension(file), ".jsfb", StringComparison.OrdinalIgnoreCase)
                            || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                        try
                        {
                            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                var header = new byte[8]; int count = 0;
                                while (count < 8) { int read = stream.Read(header, count, 8 - count); if (read == 0) break; count += read; }
                                if (count == 8 && Encoding.ASCII.GetString(header, 4, 4) == "Scre") result.Add(Path.GetFullPath(file));
                            }
                        }
                        catch (Exception error) when (FileError(error)) { warnings.Add(Relative(root, file) + ": " + error.Message); }
                    }
                }
                catch (Exception error) when (FileError(error)) { warnings.Add(Relative(root, directory) + ": " + error.Message); }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase); return result;
        }

        public static ulong HashPath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            ulong value = 14695981039346656037;
            foreach (byte item in Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant())) { value ^= item; value *= 1099511628211; }
            return value;
        }

        private static DiscoveryMatchupEntry Entry(Dictionary<int, DiscoveryMatchupEntry> entries, int id, string source)
        { DiscoveryMatchupEntry entry; if (!entries.TryGetValue(id, out entry)) entries[id] = entry = new DiscoveryMatchupEntry { Id = id }; Add(entry.SourceFiles, source); return entry; }
        private static void Add(List<string> values, string value) { if (!string.IsNullOrWhiteSpace(value)) { value = value.Trim(); if (!values.Contains(value)) values.Add(value); } }
        private static int PositiveId(uint value) { return value > 0 && value <= int.MaxValue ? (int)value : 0; }
        private static int Sized(FlatBufferFile file, int table, int index, int width)
        {
            int field = file.Field(table, index); if (field == 0) return 0;
            long vt = (long)table - file.I32(table); int size = file.U16(checked((int)vt + 2));
            if ((long)field + width > (long)table + size) throw new InvalidDataException("Matchup field extends beyond its table.");
            return field;
        }
        private static string ReadString(FlatBufferFile file, int table, int index)
        {
            int field = Sized(file, table, index, 4); if (field == 0) return null;
            uint offset = file.U32(field); if (offset < 4 || offset > int.MaxValue - field) throw new InvalidDataException("Invalid matchup string reference.");
            int at = field + (int)offset; uint count = file.U32(at);
            if (count > 16384) throw new InvalidDataException("Oversized matchup string.");
            var bytes = new byte[(int)count]; for (int i = 0; i < bytes.Length; i++) bytes[i] = file.U8(checked(at + 4 + i));
            if (file.U8(checked(at + 4 + bytes.Length)) != 0) throw new InvalidDataException("Unterminated matchup string.");
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        private static string NormalizeAssetPath(string input)
        {
            string path = input.Replace('\\', '/');
            if (!path.StartsWith("UI/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".jsfb", StringComparison.OrdinalIgnoreCase)
                || path.Split('/').Any(part => part == ".." || part == "." || part.Length == 0) || path.IndexOf(':') >= 0) return null;
            return path;
        }
        private static string Locate(string root, string folder, string name)
        {
            string directory = Directory.GetDirectories(root).FirstOrDefault(path => Path.GetFileName(path).Equals(folder, StringComparison.OrdinalIgnoreCase));
            return directory == null ? null : Directory.GetFiles(directory).FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        private static string Relative(string root, string file) { return file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1) : file; }
        private static bool UInt(object value, out uint result) { result = 0; if (value == null || value is bool) return false; return uint.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out result); }
        private static bool FileError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                ;
        }
        private static Dictionary<string, object> Deserialize(string text)
        {
#if NET8_0_OR_GREATER
            using (var doc = JsonDocument.Parse(text)) return JsonValue(doc.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumTableBytes, RecursionLimit = 100 }.DeserializeObject(text) as Dictionary<string, object>;
#endif
        }
#if NET8_0_OR_GREATER
        private static object JsonValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>(); foreach (JsonProperty property in value.EnumerateObject()) map.Add(property.Name, JsonValue(property.Value)); return map;
                case JsonValueKind.Array:
                    var list = new List<object>(); foreach (JsonElement item in value.EnumerateArray()) list.Add(JsonValue(item)); return list;
                case JsonValueKind.Number:
                    ulong number; return value.TryGetUInt64(out number) ? (object)number : value.GetDecimal();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
