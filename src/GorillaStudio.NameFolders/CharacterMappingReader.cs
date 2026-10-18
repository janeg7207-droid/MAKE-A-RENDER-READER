using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>Joins real physical MCD paths to explicit logical roster IDs in CharacterMapping.</summary>
    public static class CharacterMappingReader
    {
        public static Dictionary<ulong, List<int>> Read(string path)
        {
            var file = new FlatBufferFile(File.ReadAllBytes(path), "Char");
            var result = new Dictionary<ulong, List<int>>();
            foreach (int row in file.Tables(file.Root, 0))
            {
                int idField = file.Field(row, 0), modelField = file.Field(row, 7);
                if (idField == 0 || modelField == 0) continue;
                int id = file.U16(idField);
                ulong hash = file.U64(modelField);
                if (id < 1 || hash == 0) continue;
                List<int> ids;
                if (!result.TryGetValue(hash, out ids)) result[hash] = ids = new List<int>();
                if (!ids.Contains(id)) ids.Add(id);
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
