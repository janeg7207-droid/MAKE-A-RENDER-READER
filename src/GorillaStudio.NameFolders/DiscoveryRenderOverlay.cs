using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryRenderOverlayResult
    {
        public string Root { get; internal set; }
        public string TablePath { get; internal set; }
        public string OriginalTable { get; internal set; }
        public Dictionary<ulong, ulong> OriginalToAlias { get; } = new Dictionary<ulong, ulong>();
        public Dictionary<ulong, ulong> AliasToOriginal { get; } = new Dictionary<ulong, ulong>();
        public Dictionary<ulong, string> AliasStems { get; } = new Dictionary<ulong, string>();
        public Dictionary<ulong, string> ImagePaths { get; } = new Dictionary<ulong, string>();
        public int PatchedFields { get; internal set; }
    }

    /// <summary>
    /// Adapts exact Discovery image hashes to the original engine's named-DDS lookup without
    /// changing the user's profiles, images, or install hashes. The caller restores alias hashes
    /// to the original profile hashes when completing a render for installation.
    /// </summary>
    public static class DiscoveryRenderOverlay
    {
        public static DiscoveryRenderOverlayResult Prepare(string root, string originalTable, IDictionary<ulong, string> preparedDds)
        {
            if (preparedDds == null) throw new ArgumentNullException(nameof(preparedDds));
            string fullRoot = Path.GetFullPath(root);
            string fullTable = Path.GetFullPath(originalTable);
            string tablePath = Path.Combine(fullRoot, "Roster", "CharProfileTable.jsfb");
            if (Inside(fullRoot, fullTable))
                throw new IOException("The roster picture compatibility cache must not overwrite the original profile table.");

            byte[] bytes = File.ReadAllBytes(fullTable);
            var offsets = PictureFields(bytes);
            var result = new DiscoveryRenderOverlayResult { Root = fullRoot, TablePath = tablePath, OriginalTable = fullTable };
            var used = new HashSet<ulong>(offsets.Keys);
            var sources = new Dictionary<ulong, string>();
            foreach (ulong original in preparedDds.Keys.Where(value => offsets.ContainsKey(value)).OrderBy(value => value))
            {
                string source = Path.GetFullPath(preparedDds[original]);
                if (Inside(fullRoot, source))
                    throw new IOException("Prepared source images must be stored outside the roster picture compatibility cache.");
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A prepared roster picture cannot be a symbolic link: " + source);
                if (!DiscoveryRenderImages.HasImageHeader(source, ".dds"))
                    throw new InvalidDataException("A prepared roster picture is not a supported DDS file: " + source);
                string stem = "discovery_" + original.ToString("x16", CultureInfo.InvariantCulture);
                string aliasStem = stem;
                ulong alias = DiscoveryRenderImages.HashStem(aliasStem);
                int suffix = 0;
                while (!used.Add(alias))
                {
                    aliasStem = stem + "_" + (++suffix).ToString(CultureInfo.InvariantCulture);
                    alias = DiscoveryRenderImages.HashStem(aliasStem);
                }
                string destination = Path.Combine(fullRoot, "UI", "Assets", "Roster", "TypeA", aliasStem + ".dds");
                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The roster picture compatibility cache must not overwrite a source image.");
                result.OriginalToAlias.Add(original, alias);
                result.AliasToOriginal.Add(alias, original);
                result.AliasStems.Add(original, aliasStem);
                result.ImagePaths.Add(original, destination);
                sources.Add(original, source);
            }

            // Resolve and validate every destination before writing anything. A previous native
            // Roster junction is never followed or silently replaced with a writable directory.
            EnsureRealDirectory(fullRoot);
            EnsureRealDirectory(Path.Combine(fullRoot, "Roster"));
            EnsureRealDirectory(Path.Combine(fullRoot, "UI"));
            EnsureRealDirectory(Path.Combine(fullRoot, "UI", "Assets"));
            EnsureRealDirectory(Path.Combine(fullRoot, "UI", "Assets", "Roster"));
            EnsureRealDirectory(Path.Combine(fullRoot, "UI", "Assets", "Roster", "TypeA"));
            EnsureRealFile(tablePath);
            string markerPath = Path.Combine(fullRoot, "original-roster.txt");
            EnsureRealFile(markerPath);
            foreach (string destination in result.ImagePaths.Values) EnsureRealFile(destination);

            foreach (var entry in result.OriginalToAlias)
            {
                foreach (int offset in offsets[entry.Key])
                {
                    ulong value = entry.Value;
                    for (int i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (i * 8));
                    result.PatchedFields++;
                }
                CopyAtomic(sources[entry.Key], result.ImagePaths[entry.Key]);
            }
            WriteAtomic(bytes, tablePath);
            WriteAtomic(Encoding.UTF8.GetBytes(fullTable), markerPath);
            return result;
        }

        private static bool Inside(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<ulong, List<int>> PictureFields(byte[] bytes)
        {
            var file = new FlatBufferFile(bytes, "Char");
            var result = new Dictionary<ulong, List<int>>();
            try
            {
                SizedField(file, file.Root, 0, 4);
                foreach (int wrapper in file.Tables(file.Root, 0))
                {
                    int reference = SizedField(file, wrapper, 1, 4);
                    if (reference == 0) continue;
                    uint displacement = file.U32(reference);
                    if (displacement < 4 || displacement > int.MaxValue - reference)
                        throw new InvalidDataException("Invalid Discovery roster picture profile reference.");
                    int profile = reference + (int)displacement;
                    int id = SizedField(file, profile, 2, 2);
                    if (id == 0 || file.U16(id) == 0) continue;
                    SizedField(file, profile, 4, 4);
                    foreach (int attire in file.Tables(profile, 4))
                    {
                        int location = SizedField(file, attire, 6, 8);
                        if (location == 0) continue;
                        ulong hash = file.U64(location);
                        if (hash == 0) continue;
                        List<int> locations;
                        if (!result.TryGetValue(hash, out locations)) result[hash] = locations = new List<int>();
                        if (!locations.Contains(location)) locations.Add(location);
                    }
                }
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException("Invalid Discovery roster picture field offset.", error);
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
                throw new InvalidDataException("A Discovery roster picture field extends beyond its table.");
            return location;
        }

        private static void EnsureRealDirectory(string path)
        {
            // Also inspect existing ancestors when creating a new cache root: a linked parent
            // must not redirect any write into an unexpected or original Discovery folder.
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("A roster picture cache directory cannot be a symbolic link or junction: " + current);
                    if ((attributes & FileAttributes.Directory) == 0)
                        throw new IOException("A roster picture cache directory is occupied by a file: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
            Directory.CreateDirectory(path);
        }

        private static void EnsureRealFile(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("A roster picture cache file cannot be a symbolic link, junction, or directory: " + path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private static void CopyAtomic(string source, string destination)
        {
            EnsureRealFile(destination);
            if (File.Exists(destination) && DiscoveryRenderImages.SameBytes(source, destination)) return;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary);
                Commit(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void WriteAtomic(byte[] bytes, string destination)
        {
            EnsureRealFile(destination);
            if (File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(bytes)) return;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                Commit(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void Commit(string temporary, string destination)
        {
            EnsureRealFile(destination);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
    }
}
