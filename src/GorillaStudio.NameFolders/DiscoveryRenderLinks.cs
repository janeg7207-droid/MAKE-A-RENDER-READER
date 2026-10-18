using System;
using System.Collections.Generic;
using System.IO;

namespace GorillaStudio.NameFolders
{
    /// <summary>Reads the roster picture path hashes stored in the actual Discovery profiles.</summary>
    public static class DiscoveryRenderLinks
    {
        /// <summary>
        /// Returns every positive logical roster ID, including profiles with no picture links.
        /// Hashes are read from each attire's TypeA picture field; they do not require a known
        /// stock filename, a matching image file, an SDB entry, or a physical model folder ID.
        /// </summary>
        public static Dictionary<int, List<ulong>> Read(string tablePath)
        {
            var file = new FlatBufferFile(File.ReadAllBytes(tablePath), "Char");
            var result = new Dictionary<int, List<ulong>>();
            try
            {
                SizedField(file, file.Root, 0, 4);
                foreach (int wrapper in file.Tables(file.Root, 0))
                {
                    int profileField = SizedField(file, wrapper, 1, 4);
                    if (profileField == 0) continue;
                    uint displacement = file.U32(profileField);
                    if (displacement < 4 || displacement > int.MaxValue - profileField)
                        throw Invalid("Invalid profile reference.");
                    int profile = profileField + (int)displacement;
                    int idField = SizedField(file, profile, 2, 2);
                    if (idField == 0) continue;
                    int id = file.U16(idField);
                    if (id == 0) continue;
                    List<ulong> hashes;
                    if (!result.TryGetValue(id, out hashes)) result[id] = hashes = new List<ulong>();
                    SizedField(file, profile, 4, 4);
                    foreach (int attire in file.Tables(profile, 4))
                    {
                        int pictureField = SizedField(file, attire, 6, 8);
                        if (pictureField == 0) continue;
                        ulong hash = file.U64(pictureField);
                        if (hash != 0 && !hashes.Contains(hash)) hashes.Add(hash);
                    }
                }
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException("Invalid Discovery roster picture links: an offset is out of range.", error);
            }
            return result;
        }

        private static int SizedField(FlatBufferFile file, int table, int index, int width)
        {
            int location = file.Field(table, index);
            if (location == 0) return 0;
            long vtable = (long)table - file.I32(table);
            int size = file.U16(checked((int)vtable + 2));
            if ((long)location + width > (long)table + size)
                throw Invalid("A field extends beyond its table.");
            return location;
        }

        private static InvalidDataException Invalid(string message)
        {
            return new InvalidDataException("Invalid Discovery roster picture links: " + message);
        }
    }
}
