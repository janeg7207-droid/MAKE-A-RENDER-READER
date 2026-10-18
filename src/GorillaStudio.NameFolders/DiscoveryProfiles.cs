using System;
using System.Collections.Generic;
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
            var file = new FlatBufferFile(File.ReadAllBytes(tablePath), "Char");
            string root = DataRoot(tablePath);
            sdbPath = sdbPath ?? FindEnglishSdb(root);
            Dictionary<uint, string> strings = sdbPath == null ? new Dictionary<uint, string>() : ReadStrings(sdbPath);
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
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8) throw InvalidSdb("Header is truncated.");
            uint count = Read32(bytes, 4);
            long textStart = 8L + count * 12L;
            if (textStart > bytes.Length) throw InvalidSdb("The string index is truncated.");
            var encoding = new UTF8Encoding(false, true);
            var strings = new Dictionary<uint, string>();
            for (uint i = 0; i < count; i++)
            {
                int position = checked(8 + (int)i * 12);
                uint offset = Read32(bytes, position), length = Read32(bytes, position + 4), key = Read32(bytes, position + 8);
                if (offset < textStart || (long)offset + length >= bytes.Length || bytes[(long)offset + length] != 0)
                    throw InvalidSdb("A string points outside the text section or has no terminator.");
                try { strings[key] = encoding.GetString(bytes, checked((int)offset), checked((int)length)); }
                catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid Discovery SDB UTF-8 text.", error); }
            }
            return strings;
        }

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
