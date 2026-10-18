using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>A named taunt definition in Discovery's create-a-moveset table.</summary>
    public sealed class DiscoveryTaunt
    {
        public int Id { get; set; }
        // Clips use the logical CAM ID and a separately numbered variant.
        // DataMoveIds are a separate game identifier and must not replace it.
        public int AnimationId { get { return Id; } }
        public int Variant { get; set; }
        public string Name { get; set; }
        public List<string> Names { get; set; } = new List<string>();
        public List<int> Variants { get; set; } = new List<int>();
        public List<int> DataMoveIds { get; set; } = new List<int>();
        public string Clip { get { return Id.ToString(CultureInfo.InvariantCulture) + "a" + Variant.ToString("000", CultureInfo.InvariantCulture); } }
    }

    /// <summary>
    /// Reads actual taunt definitions. A definition alone does not establish that
    /// its animation asset exists: callers must join Clip to their asset index.
    /// </summary>
    public static class DiscoveryTaunts
    {
        public static List<DiscoveryTaunt> Read(string tablePath)
        {
            byte[] bytes = File.ReadAllBytes(tablePath);
            var file = new FlatBufferFile(bytes, "CMov");
            var rows = new List<TauntRow>();
            foreach (int table in file.Tables(file.Root, 0))
            {
                int categories = file.Field(table, 16);
                if (categories == 0 || !UIntVector(bytes, categories).Contains(4U)) continue;

                int idField = file.Field(table, 1), nameField = file.Field(table, 0);
                if (idField == 0 || nameField == 0) continue;
                uint id = file.U32(idField);
                // 65535 denotes an unused CAM entry, not a playable move.
                if (id == 0 || id == 65535 || id > int.MaxValue) continue;
                string name = String(bytes, nameField).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (name.Length == 0 || string.Equals(name, "TBD", StringComparison.OrdinalIgnoreCase)) continue;

                int variantField = file.Field(table, 2), moveField = file.Field(table, 4);
                uint variant = variantField == 0 ? 0 : file.U32(variantField);
                uint dataMove = moveField == 0 ? 0 : file.U32(moveField);
                if (variant > int.MaxValue || dataMove > int.MaxValue)
                    throw Invalid("A taunt identifier exceeds the supported range.");
                rows.Add(new TauntRow { Id = (int)id, Name = name, Variant = (int)variant, DataMoveId = (int)dataMove });
            }

            return rows.GroupBy(row => new { row.Id, row.Variant }).Select(group =>
            {
                List<TauntRow> ordered = group.OrderBy(row => row.Variant).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> names = ordered.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new DiscoveryTaunt { Id = group.Key.Id, Variant = group.Key.Variant, Name = names[0], Names = names,
                    Variants = ordered.Select(row => row.Variant).Distinct().OrderBy(value => value).ToList(),
                    DataMoveIds = ordered.Select(row => row.DataMoveId).Where(value => value > 0).Distinct().OrderBy(value => value).ToList() };
            }).OrderBy(taunt => taunt.Name, StringComparer.OrdinalIgnoreCase).ThenBy(taunt => taunt.Id).ThenBy(taunt => taunt.Variant).ToList();
        }

        /// <summary>Finds the CAM table without recursively scanning a game installation.</summary>
        public static string FindTable(string selectedPath)
        {
            if (string.IsNullOrWhiteSpace(selectedPath)) return null;
            string full = Path.GetFullPath(selectedPath);
            if (File.Exists(full))
            {
                if (string.Equals(Path.GetFileName(full), "CAMMoveData.jsfb", StringComparison.OrdinalIgnoreCase)) return full;
                full = Path.GetDirectoryName(full);
            }
            for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(full); depth++)
            {
                if (Directory.Exists(full))
                {
                    string direct = ChildFile(full, "CAMMoveData.jsfb");
                    if (direct != null) return direct;
                    string moves = Directory.GetDirectories(full).FirstOrDefault(path =>
                        string.Equals(Path.GetFileName(path), "MoveData", StringComparison.OrdinalIgnoreCase));
                    if (moves != null)
                    {
                        string found = ChildFile(moves, "CAMMoveData.jsfb");
                        if (found != null) return found;
                    }
                }
                DirectoryInfo parent = Directory.GetParent(full);
                full = parent == null ? null : parent.FullName;
            }
            return null;
        }

        private static string ChildFile(string root, string name)
        {
            return Directory.GetFiles(root).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<uint> UIntVector(byte[] bytes, int field)
        {
            int vector = Target(bytes, field);
            uint count = UInt(bytes, vector);
            if (count > (bytes.Length - (long)vector - 4) / 4) throw Invalid("A category vector is truncated.");
            for (uint index = 0; index < count; index++) yield return UInt(bytes, checked(vector + 4 + (int)index * 4));
        }

        private static string String(byte[] bytes, int field)
        {
            int value = Target(bytes, field);
            uint count = UInt(bytes, value);
            if (count > 16384 || (long)value + 4 + count >= bytes.Length || bytes[value + 4 + (long)count] != 0)
                throw Invalid("A taunt name is truncated, too long, or has no terminator.");
            try { return new UTF8Encoding(false, true).GetString(bytes, value + 4, (int)count); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid Discovery taunt name UTF-8.", error); }
        }

        private static int Target(byte[] bytes, int field)
        {
            uint offset = UInt(bytes, field);
            long target = (long)field + offset;
            if (offset < 4 || target > int.MaxValue || target > bytes.Length - 4) throw Invalid("A field offset is outside the file.");
            return (int)target;
        }

        private static uint UInt(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4) throw Invalid("A field is truncated.");
            return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static InvalidDataException Invalid(string reason) { return new InvalidDataException("Invalid Discovery taunt JSFB: " + reason); }
        private sealed class TauntRow { public int Id, Variant, DataMoveId; public string Name; }
    }
}
