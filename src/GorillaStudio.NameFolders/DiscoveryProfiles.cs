using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryProfile
    {
        public int Id { get; set; }
        public int BaseId { get; set; }
        public string Name { get; set; }
        public uint NameKey { get; set; }
        public bool Female { get; set; }
    }

    public static class DiscoveryProfiles
    {
        public static List<DiscoveryProfile> Read(string tablePath, string sdbPath = null)
        {
            return Read(tablePath, sdbPath, null);
        }

        // A diagnostic collector opts into per-key SDB recovery. Callers that do
        // not surface warnings retain the strict behavior of the original API.
        public static List<DiscoveryProfile> Read(string tablePath, string sdbPath, ICollection<string> warnings)
        {
            var file = new FlatBufferFile(File.ReadAllBytes(tablePath), "Char");
            string root = DataRoot(tablePath);
            if (sdbPath == null)
            {
                try { sdbPath = FindEnglishSdb(root); }
                catch (Exception error) when (warnings != null && (error is IOException || error is UnauthorizedAccessException
                    || error is System.Security.SecurityException))
                { warnings.Add("Discovery SDB location could not be inspected: " + error.Message + NamesUnavailable); }
            }
            Dictionary<uint, string> strings = sdbPath == null ? new Dictionary<uint, string>() : ReadStrings(sdbPath, warnings);
            if (sdbPath == null && warnings != null)
                warnings.Add("Discovery SDB: Sdb/ENG.sdb was not found. Localization names are unavailable; roster IDs are preserved and missing names use Wrestler <ID>.");
            var result = new List<DiscoveryProfile>();
            var ids = new HashSet<int>();
            foreach (int wrapper in file.Tables(file.Root, 0))
            {
                int profileField = file.Field(wrapper, 1);
                if (profileField == 0) continue;
                uint displacement = file.U32(profileField);
                if (displacement < 4 || displacement > int.MaxValue - profileField)
                    throw new InvalidDataException("Invalid Discovery profile table reference.");
                int row = profileField + (int)displacement;
                file.FieldCount(row);
                int idField = file.Field(row, 2);
                if (idField == 0) continue;
                int id = file.U16(idField);
                if (id < 1 || !ids.Add(id)) continue;
                int sourceField = file.Field(row, 3);
                int nameField = file.Field(row, 7);
                uint key = nameField == 0 ? 0 : file.U32(nameField);
                string name;
                strings.TryGetValue(key, out name);
                int femaleField = file.Field(row, 15);
                result.Add(new DiscoveryProfile { Id = id, BaseId = sourceField == 0 ? id : file.U16(sourceField),
                    Name = string.IsNullOrWhiteSpace(name) ? null : name.Replace('\n', ' ').Replace('\r', ' ').Trim(),
                    NameKey = key, Female = femaleField != 0 && file.U8(femaleField) == 1 });
            }
            return result;
        }

        public static Dictionary<uint, string> ReadStrings(string path)
        {
            return ReadStrings(path, null);
        }

        /// <summary>
        /// Reads the measured absolute-offset, UTF-8 byte-length SDB layout. A
        /// non-null collector permits validated rows to survive isolated damage;
        /// it never guesses another layout, searches for a later terminator, or
        /// decodes damaged text with replacement characters.
        /// </summary>
        public static Dictionary<uint, string> ReadStrings(string path, ICollection<string> warnings)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception error) when (warnings != null && (error is IOException || error is UnauthorizedAccessException
                || error is System.Security.SecurityException))
            {
                warnings.Add(SdbName(path) + " could not be read: " + error.Message + NamesUnavailable);
                return new Dictionary<uint, string>();
            }
            if (bytes.Length < 8) return UnreadableStrings(path, "Header is truncated.", warnings);
            uint count = Read32(bytes, 4);
            long textStart = 8L + count * 12L;
            if (textStart > bytes.Length) return UnreadableStrings(path, "The string index is truncated (declared "
                + count.ToString(CultureInfo.InvariantCulture) + " entries).", warnings);
            var encoding = new UTF8Encoding(false, true);
            var strings = new Dictionary<uint, string>();
            var rejectedKeys = new HashSet<uint>();
            var conflictingKeys = new HashSet<uint>();
            var examples = new List<string>();
            uint invalidRows = 0;
            for (uint i = 0; i < count; i++)
            {
                int position = checked(8 + (int)i * 12);
                uint offset = Read32(bytes, position), length = Read32(bytes, position + 4), key = Read32(bytes, position + 8);
                string text = null, problem = null;
                if (offset < textStart || (long)offset + length >= bytes.Length)
                    problem = "A string points outside the text section or has no terminator.";
                else if (bytes[(long)offset + length] != 0)
                    problem = "A string has no terminator at its declared UTF-8 byte length.";
                else
                {
                    try { text = encoding.GetString(bytes, checked((int)offset), checked((int)length)); }
                    catch (DecoderFallbackException error)
                    {
                        if (warnings == null) throw new InvalidDataException("Invalid Discovery SDB UTF-8 text.", error);
                        problem = "Invalid UTF-8 text.";
                    }
                }
                if (problem != null)
                {
                    if (warnings == null) throw InvalidSdb(problem);
                    invalidRows++; rejectedKeys.Add(key); strings.Remove(key);
                    if (examples.Count < 5) examples.Add(RowProblem(i, key, problem));
                    continue;
                }
                // Strict legacy consumers retain their established last-row
                // behavior. Recovery cannot choose between conflicting labels
                // for one key or reuse a valid alias of a malformed key.
                if (warnings == null) { strings[key] = text; continue; }
                if (rejectedKeys.Contains(key)) continue;
                string previous;
                if (strings.TryGetValue(key, out previous) && !string.Equals(previous, text, StringComparison.Ordinal))
                {
                    conflictingKeys.Add(key); rejectedKeys.Add(key); strings.Remove(key);
                    if (examples.Count < 5) examples.Add(RowProblem(i, key, "Duplicate localization key has conflicting text."));
                    continue;
                }
                strings[key] = text;
            }
            if (warnings != null && (invalidRows > 0 || conflictingKeys.Count > 0))
                warnings.Add(SdbName(path) + ": rejected " + invalidRows.ToString(CultureInfo.InvariantCulture) + " malformed string row(s) of "
                    + count.ToString(CultureInfo.InvariantCulture) + "; " + conflictingKeys.Count.ToString(CultureInfo.InvariantCulture)
                    + " conflicting localization key(s). " + rejectedKeys.Count.ToString(CultureInfo.InvariantCulture)
                    + " key(s) are unavailable; " + strings.Count.ToString(CultureInfo.InvariantCulture) + " validated name(s) remain. "
                    + string.Join(" ", examples) + " Missing names use Wrestler <ID>; roster IDs are unchanged.");
            else if (warnings != null && count == 0)
                warnings.Add(SdbName(path) + " contains no string entries." + NamesUnavailable);
            return strings;
        }

        private const string NamesUnavailable = " Localization names are unavailable; roster IDs are preserved and missing names use Wrestler <ID>.";
        private static Dictionary<uint, string> UnreadableStrings(string path, string problem, ICollection<string> warnings)
        {
            if (warnings == null) throw InvalidSdb(problem);
            warnings.Add(SdbName(path) + ": " + problem + NamesUnavailable);
            return new Dictionary<uint, string>();
        }
        private static string SdbName(string path) { return "Discovery SDB '" + path + "'"; }
        private static string RowProblem(uint index, uint key, string problem)
        { return "Row " + (index + 1).ToString(CultureInfo.InvariantCulture) + ", key 0x" + key.ToString("X8", CultureInfo.InvariantCulture) + ": " + problem; }

        public static string DataRoot(string tablePath)
        {
            var containing = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(tablePath)));
            return string.Equals(containing.Name, "Roster", StringComparison.OrdinalIgnoreCase) && containing.Parent != null
                ? containing.Parent.FullName : containing.FullName;
        }

        public static string FindEnglishSdb(string root)
        {
            string sdb = Directory.GetDirectories(root).FirstOrDefault(p => string.Equals(Path.GetFileName(p), "Sdb", StringComparison.OrdinalIgnoreCase));
            if (sdb == null) return null;
            return Directory.GetFiles(sdb).FirstOrDefault(p => string.Equals(Path.GetFileName(p), "ENG.sdb", StringComparison.OrdinalIgnoreCase));
        }

        private static uint Read32(byte[] bytes, int position)
        {
            return (uint)(bytes[position] | bytes[position + 1] << 8 | bytes[position + 2] << 16 | bytes[position + 3] << 24);
        }
        private static InvalidDataException InvalidSdb(string text) { return new InvalidDataException("Invalid Discovery SDB: " + text); }
    }
}
