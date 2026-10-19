using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class CharacterMappingRecord
    {
        public int LogicalId { get; set; }
        public uint CostumeKey { get; set; }
        public int CostumeIndex { get; set; }
        public ulong ModelHash { get; set; }
    }

    /// <summary>Joins real physical MCD paths to explicit logical roster IDs in CharacterMapping.</summary>
    public static class CharacterMappingReader
    {
        public static Dictionary<ulong, List<int>> Read(string path)
        {
            var result = new Dictionary<ulong, List<int>>();
            foreach (CharacterMappingRecord row in ReadRecords(path))
            {
                List<int> ids;
                if (!result.TryGetValue(row.ModelHash, out ids)) result[row.ModelHash] = ids = new List<int>();
                if (!ids.Contains(row.LogicalId)) ids.Add(row.LogicalId);
            }
            return result;
        }

        // Preserve costume references as well as the path hash. A profile can list
        // only some of its mapping rows, and its active attires can use different
        // source folders. Read remains the original hash-to-ID compatibility API.
        public static List<CharacterMappingRecord> ReadRecords(string path)
        {
            var file = new FlatBufferFile(File.ReadAllBytes(path), "Char");
            var result = new List<CharacterMappingRecord>();
            foreach (int row in file.Tables(file.Root, 0))
            {
                int idField = file.Field(row, 0), modelField = file.Field(row, 7);
                if (idField == 0 || modelField == 0) continue;
                int id = file.U16(idField);
                ulong hash = file.U64(modelField);
                if (id < 1 || hash == 0) continue;
                int keyField = file.Field(row, 1), indexField = file.Field(row, 2);
                result.Add(new CharacterMappingRecord { LogicalId = id, ModelHash = hash,
                    CostumeKey = keyField == 0 ? 0 : file.U32(keyField),
                    CostumeIndex = indexField == 0 ? 0 : file.U8(indexField) });
            }
            return result;
        }

        public static ulong ModelPathHash(string rootRelativePath)
        {
            if (rootRelativePath == null) throw new ArgumentNullException("rootRelativePath");
            string normalized = rootRelativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            ulong value = 14695981039346656037UL;
            foreach (byte item in Encoding.UTF8.GetBytes(normalized))
            {
                value ^= item;
                value = unchecked(value * 1099511628211UL);
            }
            return value;
        }
    }
}
