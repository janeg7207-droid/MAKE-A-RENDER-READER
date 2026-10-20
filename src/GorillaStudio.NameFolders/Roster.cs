using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class RosterEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }
        public string RosterTable { get; set; }
        public int SourceId { get; set; }
        public bool IsMod { get; set; }
        public string Source { get; set; }
        public string StockFolder { get; set; }
    }

    /// <summary>Imports explicit wrestler IDs and readable names, never render IDs or guessed binary fields.</summary>
    public static class RosterImport
    {
        private static readonly string[] IdKeys = { "id", "wrestlerid", "characterid", "slotid" };
        private static readonly string[] NameKeys = { "name", "wrestlername", "displayname", "charactername", "fullname", "superstarname", "wrestler", "superstar" };
        private static readonly string[] FolderKeys = { "folder", "directory", "folderpath" };
        private static readonly string[] RosterTableKeys = { "rostertable", "charprofiletablepath" };
        private static readonly string[] WrapperKeys = { "roster", "wrestlers", "superstars", "characters", "entries", "data", "items", "results", "charprofiletable", "charprofilelist", "profiles" };
        private static readonly Regex NumberedFolder = new Regex(@"^(\d+)_(.+)$", RegexOptions.CultureInvariant);

        public static List<RosterEntry> LoadFile(string path)
        {
            return LoadFileCore(path, false, null);
        }

        // Roster names and exact stock references are enough to open Studio.
        // Extracted model discovery is deliberately left to the picker's indexer.
        public static List<RosterEntry> LoadFileQuick(string path, ICollection<string> warnings = null)
        {
            return LoadFileCore(path, true, warnings);
        }

        private static List<RosterEntry> LoadFileCore(string path, bool quick, ICollection<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a roster file.", "path");
            if (!File.Exists(path)) throw new FileNotFoundException("The roster file does not exist.", path);
            List<RosterEntry> entries;
            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".json": entries = ReadJson(File.ReadAllText(path)); break;
                    case ".jsfb": entries = quick ? LoadDiscoveryQuick(path, null, null, warnings) : ReadDiscoveryCompanion(path); break;
                    default: throw new InvalidDataException("Choose a JSON roster or CharProfileTable.jsfb with its companion Discovery Characters folder.");
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is InvalidOperationException
#if NET8_0_OR_GREATER
                || ex is JsonException
#endif
                )
            {
                throw new InvalidDataException("Could not read the roster: " + ex.Message, ex);
            }
            return Finish(entries);
        }

        public static List<RosterEntry> LoadDirectory(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Choose a roster folder.", "root");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The roster folder does not exist: " + root);
            string fullRoot = new DirectoryInfo(root).FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var entries = new List<RosterEntry>();
            AddDirectory(entries, fullRoot);
            if (entries.Count == 0)
            {
                string scanRoot = FindChildDirectory(root, "Characters") ?? root;
                foreach (string folder in Directory.GetDirectories(scanRoot).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    AddDirectory(entries, folder);
            }
            return Finish(entries);
        }

        private static string FindChildDirectory(string root, string name)
        {
            return Directory.GetDirectories(root).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }

        public static List<RosterEntry> LoadDiscovery(string path, string sdbPath = null, string stockIndexPath = null)
        {
            return LoadDiscoveryCore(path, sdbPath, stockIndexPath, false, null);
        }

        public static List<RosterEntry> LoadDiscoveryQuick(string path, string sdbPath = null, string stockIndexPath = null,
            ICollection<string> warnings = null)
        {
            return LoadDiscoveryCore(path, sdbPath, stockIndexPath, true, warnings);
        }

        private static List<RosterEntry> LoadDiscoveryCore(string path, string sdbPath, string stockIndexPath,
            bool quick, ICollection<string> warnings)
        {
            if (!string.Equals(Path.GetFileName(path), "CharProfileTable.jsfb", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only CharProfileTable.jsfb can locate a Discovery roster. Other .jsfb files are binary game data, not supported roster imports.");
            ValidateProfileTable(path);
            string root = DiscoveryProfiles.DataRoot(path);
            string characters = FindChildDirectory(root, "Characters");
            if (characters == null)
                throw new InvalidDataException("Keep CharProfileTable.jsfb in its Discovery Roster folder alongside the companion Characters folder. Keep Sdb/ENG.sdb beside them to load the game's character names.");
            List<DiscoveryProfile> profiles = warnings == null ? DiscoveryProfiles.Read(path, sdbPath)
                : DiscoveryProfiles.Read(path, sdbPath, warnings);
            var entries = new List<RosterEntry>();
            if (!quick)
            {
                DiscoveryScanResult scan = DiscoveryScanner.Scan(root, profiles, path);
                if (scan.Warnings.Any(w => w.StartsWith("Could not read character mapping", StringComparison.Ordinal)))
                    throw new InvalidDataException(string.Join(Environment.NewLine, scan.Warnings));
                entries = scan.Entries;
            }
            stockIndexPath = stockIndexPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "gamepack", "stock_index.json");
            Dictionary<int, string> stock = File.Exists(stockIndexPath) ? StockModels.Read(root, stockIndexPath) : new Dictionary<int, string>();
            var byId = entries.GroupBy(entry => entry.Id).ToDictionary(group => group.Key, group => group.ToList());
            foreach (DiscoveryProfile profile in profiles)
            {
                List<RosterEntry> models;
                if (!byId.TryGetValue(profile.Id, out models))
                {
                    var entry = new RosterEntry { Id = profile.Id, SourceId = profile.BaseId, Name = profile.Name ?? "Wrestler " + profile.Id.ToString(CultureInfo.InvariantCulture), Source = "Profile" };
                    entries.Add(entry);
                    byId.Add(profile.Id, models = new List<RosterEntry> { entry });
                }
                string key;
                if (!stock.TryGetValue(profile.Id, out key)) continue;
                foreach (RosterEntry model in models)
                {
                    model.StockFolder = key;
                    if (model.Folder == null) model.SourceId = FolderNames.ExistingId(key) ?? model.SourceId;
                }
            }
            foreach (RosterEntry entry in entries) entry.RosterTable = Path.GetFullPath(path);
            return Finish(entries);
        }

        private static List<RosterEntry> ReadDiscoveryCompanion(string path) { return LoadDiscovery(path); }

        // Check the actual FlatBuffer identifier and root structure before using the companion folders.
        // FlatBuffers may reuse a vtable after the root, so the vtable displacement is signed.
        private static void ValidateProfileTable(string path)
        {
            using (var input = new BinaryReader(File.OpenRead(path)))
            {
                long length = input.BaseStream.Length;
                if (length < 16) throw InvalidProfile();
                uint root = input.ReadUInt32();
                if (input.ReadByte() != 'C' || input.ReadByte() != 'h' || input.ReadByte() != 'a' || input.ReadByte() != 'r') throw InvalidProfile();
                if (root < 8 || root > length - 4) throw InvalidProfile();
                input.BaseStream.Position = root;
                long vtable = (long)root - input.ReadInt32();
                if (vtable < 8 || vtable > length - 6) throw InvalidProfile();
                input.BaseStream.Position = vtable;
                ushort tableSize = input.ReadUInt16(), objectSize = input.ReadUInt16(), field = input.ReadUInt16();
                if (tableSize < 6 || tableSize % 2 != 0 || vtable + tableSize > length || objectSize < 8 || (long)root + objectSize > length || field < 4 || field > objectSize - 4) throw InvalidProfile();
                input.BaseStream.Position = (long)root + field;
                uint vectorDisplacement = input.ReadUInt32();
                long vector = (long)root + field + vectorDisplacement;
                if (vectorDisplacement < 4 || vector > length - 4) throw InvalidProfile();
                input.BaseStream.Position = vector;
                uint count = input.ReadUInt32();
                if (count > (length - vector - 4) / 4) throw InvalidProfile();
                for (uint i = 0; i < count; i++)
                {
                    long element = input.BaseStream.Position;
                    uint displacement = input.ReadUInt32();
                    if (displacement < 4 || element + displacement > length - 4) throw InvalidProfile();
                }
            }
        }

        private static InvalidDataException InvalidProfile()
        {
            return new InvalidDataException("This file is not a valid CharProfileTable.jsfb: the Char identifier or FlatBuffer root/vector structure is missing or damaged.");
        }

        private static void AddDirectory(List<RosterEntry> entries, string path)
        {
            Match match = NumberedFolder.Match(Path.GetFileName(path));
            if (!match.Success) return;
            entries.Add(Create(match.Groups[1].Value, match.Groups[2].Value.Replace('_', ' '), Path.GetFullPath(path), "Folder " + Path.GetFileName(path)));
        }

        private static List<RosterEntry> Finish(List<RosterEntry> entries)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<RosterEntry>();
            foreach (RosterEntry entry in entries)
            {
                string key = entry.Id.ToString(CultureInfo.InvariantCulture) + "\0" + entry.Name + "\0" + (entry.Folder ?? "") + "\0" + (entry.RosterTable ?? "");
                if (seen.Add(key)) result.Add(entry);
            }
            if (result.Count == 0)
                throw new InvalidDataException("No roster entries were found. Supply JSON names with actual wrestler/character IDs (1–65535), or keep CharProfileTable.jsfb with its companion Characters and Sdb/ENG.sdb folders. Plain names alone do not identify the game's wrestler IDs.");
            return result;
        }

        private static RosterEntry Create(object id, object name, object folder, string source, object rosterTable = null)
        {
            int numericId;
            if (!TryId(id, out numericId))
                throw new InvalidDataException(source + ": the wrestler/character ID must be a whole number from 1 to 65535. IDs cannot be inferred from names or render filenames.");
            string displayName = name is string ? ((string)name).Trim() : "";
            if (displayName.Length == 0) throw new InvalidDataException(source + ": a readable wrestler name is required for ID " + numericId.ToString(CultureInfo.InvariantCulture) + ". A numeric stringname localization key is not a wrestler display name.");
            string folderName = Scalar(folder).Trim();
            string tablePath = rosterTable as string;
            return new RosterEntry { Id = numericId, Name = displayName, Folder = folderName.Length == 0 ? null : folderName, RosterTable = string.IsNullOrWhiteSpace(tablePath) ? null : tablePath };
        }

        private static bool TryId(object value, out int id)
        {
            if (value is decimal || value is double || value is float)
            {
                decimal number;
                if (decimal.TryParse(Scalar(value), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                    && number == decimal.Truncate(number) && number >= 1 && number <= 65535)
                {
                    id = (int)number;
                    return true;
                }
                id = 0;
                return false;
            }
            return int.TryParse(Scalar(value).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id >= 1 && id <= 65535;
        }

        private static string Scalar(object value)
        {
            if (value == null) return "";
            if (value is string) return (string)value;
            if (value is bool || value is IDictionary || value is IEnumerable) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static string Normalize(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static List<RosterEntry> ReadJson(string content)
        {
            object root;
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(content.TrimStart('\uFEFF')))
                root = JsonValue(document.RootElement);
#else
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
            root = serializer.DeserializeObject(content.TrimStart('\uFEFF'));
#endif
            var entries = new List<RosterEntry>();
            AddJson(entries, root, "JSON roster", 0, null, null);
            return entries;
        }

#if NET8_0_OR_GREATER
        private static object JsonValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>();
                    foreach (JsonProperty property in element.EnumerateObject()) map[property.Name] = JsonValue(property.Value);
                    return map;
                case JsonValueKind.Array: return element.EnumerateArray().Select(JsonValue).ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    decimal number;
                    return element.TryGetDecimal(out number) ? (object)number : element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif

        private static object Property(IDictionary<string, object> map, string[] names)
        {
            foreach (KeyValuePair<string, object> item in map)
                if (names.Contains(Normalize(item.Key), StringComparer.Ordinal)) return item.Value;
            return null;
        }

        private static bool HasProperty(IDictionary<string, object> map, string[] names)
        {
            return map.Keys.Any(key => names.Contains(Normalize(key), StringComparer.Ordinal));
        }

        private static void AddJson(List<RosterEntry> entries, object node, string source, int depth, object fallbackId, object fallbackName)
        {
            if (depth > 32) throw new InvalidDataException("The JSON roster is nested too deeply.");
            var map = node as IDictionary<string, object>;
            if (map != null)
            {
                if (HasProperty(map, IdKeys) || HasProperty(map, NameKeys))
                {
                    var entry = Create(Property(map, IdKeys) ?? fallbackId, Property(map, NameKeys) ?? fallbackName, Property(map, FolderKeys), source, Property(map, RosterTableKeys));
                    int sourceId;
                    if (TryId(Property(map, new[] { "sourceid" }), out sourceId)) entry.SourceId = sourceId;
                    entry.IsMod = object.Equals(Property(map, new[] { "ismod" }), true);
                    entry.Source = Property(map, new[] { "source" }) as string;
                    entry.StockFolder = Property(map, new[] { "stockfolder" }) as string;
                    entries.Add(entry);
                    return;
                }
                if (HasProperty(map, new[] { "stringname" }))
                    throw new InvalidDataException("This JSON contains stringname localization keys rather than readable wrestler names. Import a JSON name-to-ID roster, or select CharProfileTable.jsfb with its companion Characters folder.");
                bool hasWrapper = false;
                foreach (KeyValuePair<string, object> item in map)
                {
                    if (!WrapperKeys.Contains(Normalize(item.Key), StringComparer.Ordinal)) continue;
                    hasWrapper = true;
                    AddJson(entries, item.Value, source + "." + item.Key, depth + 1, null, null);
                }
                if (hasWrapper) return;
                foreach (KeyValuePair<string, object> item in map)
                {
                    int id;
                    if (TryId(item.Key, out id))
                    {
                        if (item.Value is IDictionary<string, object>) AddJson(entries, item.Value, source + "[" + item.Key + "]", depth + 1, id, null);
                        else entries.Add(Create(id, item.Value, null, source + "[" + item.Key + "]"));
                    }
                    else if (item.Value is IDictionary<string, object>)
                        AddJson(entries, item.Value, source + "[" + item.Key + "]", depth + 1, null, item.Key);
                    else entries.Add(Create(item.Value, item.Key, null, source + "[" + item.Key + "]"));
                }
                return;
            }
            var array = node as IEnumerable;
            if (array != null && !(node is string))
            {
                int index = 0;
                foreach (object item in array) AddJson(entries, item, source + "[" + index++ + "]", depth + 1, null, null);
                return;
            }
            throw new InvalidDataException(source + ": expected wrestler objects with IDs and names, or a map of names to IDs (or IDs to names).");
        }
    }
}
