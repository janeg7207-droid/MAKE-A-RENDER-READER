using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryRenderImageIndex
    {
        public Dictionary<ulong, string> Images { get; } = new Dictionary<ulong, string>();
        public List<ulong> Conflicts { get; } = new List<ulong>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>Finds extracted pictures by the exact TypeA path hash stored in a profile.</summary>
    public static class DiscoveryRenderImages
    {
        private sealed class Candidate
        {
            public string Path;
            public int Priority;
        }

        public static Dictionary<ulong, string> Find(string dataRoot, IEnumerable<ulong> profileHashes)
        {
            return Scan(dataRoot, profileHashes).Images;
        }

        /// <summary>
        /// A canonical image filename represents ui/assets/roster/typea/&lt;stem&gt;.tex.
        /// An explicit sixteen-digit hexadecimal filename can instead name the profile hash.
        /// Numeric wrestler IDs are never used to infer a picture. Equally preferred different
        /// images are reported as conflicts instead of silently assigning the wrong render.
        /// </summary>
        public static DiscoveryRenderImageIndex Scan(string dataRoot, IEnumerable<ulong> profileHashes)
        {
            if (profileHashes == null) throw new ArgumentNullException(nameof(profileHashes));
            var required = new HashSet<ulong>(profileHashes.Where(hash => hash != 0));
            var result = new DiscoveryRenderImageIndex();
            if (required.Count == 0 || string.IsNullOrWhiteSpace(dataRoot)) return result;

            var candidates = new Dictionary<ulong, List<Candidate>>();
            var pending = new Stack<string>();
            pending.Push(dataRoot);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                try
                {
                    // Do not follow junctions, symlinks, or other reparse points outside the
                    // selected data folder, including a linked root and linked picture files.
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string path in Directory.GetFiles(directory))
                    {
                        string extension = Path.GetExtension(path);
                        if (!string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(extension, ".tex", StringComparison.OrdinalIgnoreCase)) continue;
                        ulong hash;
                        string stem = Path.GetFileNameWithoutExtension(path);
                        // A hash-labeled export is explicit. Do not reinterpret an unrelated
                        // hexadecimal filename as a decimal wrestler ID or truncate its hash.
                        if (!TryLiteralHash(stem, out hash) || !required.Contains(hash)) hash = HashStem(stem);
                        if (!required.Contains(hash)) continue;
                        try
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                            if (!HasImageHeader(path, extension)) continue;
                            List<Candidate> matches;
                            if (!candidates.TryGetValue(hash, out matches)) candidates[hash] = matches = new List<Candidate>();
                            matches.Add(new Candidate { Path = path, Priority = IsCanonicalDirectory(directory) ? 0 : 1 });
                        }
                        catch (Exception error) when (IsFileError(error))
                        {
                            result.Warnings.Add("Could not read roster picture " + path + ": " + error.Message);
                        }
                    }
                    foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
                }
                catch (Exception error) when (IsFileError(error))
                {
                    result.Warnings.Add("Could not scan roster pictures in " + directory + ": " + error.Message);
                }
            }

            foreach (var entry in candidates.OrderBy(pair => pair.Key))
            {
                int best = entry.Value.Min(value => value.Priority);
                var matches = entry.Value.Where(value => value.Priority == best)
                    .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.Path, StringComparer.Ordinal).ToList();
                bool conflict = false;
                for (int i = 1; i < matches.Count; i++)
                {
                    try { if (!SameBytes(matches[0].Path, matches[i].Path)) conflict = true; }
                    catch (Exception error) when (IsFileError(error))
                    {
                        conflict = true;
                        result.Warnings.Add("Could not compare duplicate roster pictures: " + error.Message);
                    }
                    if (conflict) break;
                }
                if (conflict) result.Conflicts.Add(entry.Key);
                else result.Images.Add(entry.Key, matches[0].Path);
            }
            return result;
        }

        public static ulong HashStem(string stem)
        {
            if (string.IsNullOrEmpty(stem)) throw new ArgumentException("A roster picture filename is required.", nameof(stem));
            ulong hash = 14695981039346656037UL;
            foreach (byte value in Encoding.UTF8.GetBytes("ui/assets/roster/typea/" + stem.ToLowerInvariant() + ".tex"))
                hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }

        private static bool TryLiteralHash(string stem, out ulong hash)
        {
            if (stem.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(2);
            hash = 0;
            return stem.Length == 16 && ulong.TryParse(stem, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash);
        }

        private static bool IsCanonicalDirectory(string directory)
        {
            var current = new DirectoryInfo(directory);
            foreach (string component in new[] { "TypeA", "Roster", "Assets", "UI" })
            {
                if (current == null || !string.Equals(current.Name, component, StringComparison.OrdinalIgnoreCase)) return false;
                current = current.Parent;
            }
            return true;
        }

        internal static bool HasImageHeader(string path, string extension)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool png = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
                int length = png ? 33 : 128;
                if (file.Length < length) return false;
                var bytes = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int count = file.Read(bytes, read, length - read);
                    if (count == 0) return false;
                    read += count;
                }
                if (png)
                {
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
                    for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) return false;
                    return Big32(bytes, 16) > 0 && Big32(bytes, 16) <= 65536 &&
                           Big32(bytes, 20) > 0 && Big32(bytes, 20) <= 65536;
                }
                // Raw .tex wrappers are not DDS files. Only a DDS payload is passed to the
                // engine; discovering an opaque wrapper cannot establish its texture format.
                return bytes[0] == 'D' && bytes[1] == 'D' && bytes[2] == 'S' && bytes[3] == ' ' &&
                    Little32(bytes, 4) == 124 && Little32(bytes, 76) == 32 &&
                    Little32(bytes, 12) > 0 && Little32(bytes, 12) <= 65536 &&
                    Little32(bytes, 16) > 0 && Little32(bytes, 16) <= 65536;
            }
        }

        private static uint Big32(byte[] bytes, int offset)
        {
            return (uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 |
                (uint)bytes[offset + 2] << 8 | bytes[offset + 3];
        }

        private static uint Little32(byte[] bytes, int offset)
        {
            return bytes[offset] | (uint)bytes[offset + 1] << 8 |
                (uint)bytes[offset + 2] << 16 | (uint)bytes[offset + 3] << 24;
        }

        internal static bool SameBytes(string first, string second)
        {
            using (var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (a.Length != b.Length) return false;
                var left = new byte[16384];
                var right = new byte[16384];
                while (true)
                {
                    int count = a.Read(left, 0, left.Length);
                    if (count == 0) return b.ReadByte() == -1;
                    int read = 0;
                    while (read < count)
                    {
                        int amount = b.Read(right, read, count - read);
                        if (amount == 0) return false;
                        read += amount;
                    }
                    for (int i = 0; i < count; i++) if (left[i] != right[i]) return false;
                }
            }
        }

        private static bool IsFileError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException ||
                error is NotSupportedException || error is System.Security.SecurityException;
        }
    }
}
