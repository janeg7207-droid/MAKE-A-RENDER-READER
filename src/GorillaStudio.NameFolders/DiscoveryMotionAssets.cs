using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>Exact animation records recovered from sources outside the extracted Discovery tree.</summary>
    public sealed class DiscoveryMotionFallbacks
    {
        public Dictionary<string, DiscoveryMotionAsset> Assets { get; } = new Dictionary<string, DiscoveryMotionAsset>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Issues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new List<string>();
        public bool ArchivesSearched { get; internal set; }
        public int ArchiveCount { get; internal set; }
        public bool Incomplete { get; internal set; }
        public string Status { get; internal set; }
    }

    public sealed class DiscoveryMotionAsset
    {
        public string Path { get; internal set; }
        public long Offset { get; internal set; }
        public int Length { get; internal set; }
        public ulong Id { get; internal set; }
        public bool Loose { get; internal set; }

        public byte[] Read()
        {
            using (var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (Offset < 0 || Length < 160 || Offset > file.Length - Length)
                    throw new InvalidDataException("A Discovery animation changed while it was being read.");
                file.Position = Offset;
                var bytes = new byte[Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = file.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException("A Discovery animation is truncated.");
                    read += count;
                }
                if (BitConverter.ToUInt64(bytes, 0) != Id)
                    throw new InvalidDataException("A Discovery animation identifier changed while it was being read.");
                return bytes;
            }
        }
    }

    /// <summary>Reads the actual clip lookup table, including loose mod overrides.</summary>
    public sealed class DiscoveryMotionAssets
    {
        private readonly Dictionary<ulong, DiscoveryMotionAsset> assets = new Dictionary<ulong, DiscoveryMotionAsset>();
        private readonly List<string> searchRoots = new List<string>();
        public IReadOnlyList<string> SearchRoots { get { return searchRoots.AsReadOnly(); } }
        public List<string> Warnings { get; } = new List<string>();
        public bool ScanIncomplete { get; private set; }
        public bool AnimationRootFound { get; private set; }
        public bool HasLinkedSources { get; private set; }
        public int Count { get { return assets.Count; } }

        public DiscoveryMotionAsset Find(string clip)
        {
            DiscoveryMotionAsset result;
            return assets.TryGetValue(ClipId(clip), out result) ? result : null;
        }

        public static ulong ClipId(string clip)
        {
            if (string.IsNullOrEmpty(clip)) throw new ArgumentException("A clip name is required.", nameof(clip));
            ulong hash = 14695981039346656037UL;
            foreach (byte value in Encoding.UTF8.GetBytes(clip)) hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }

        public static DiscoveryMotionAssets Read(string discoveryRoot)
        {
            var result = new DiscoveryMotionAssets();
            string root = System.IO.Path.GetFullPath(discoveryRoot);
            result.searchRoots.Add(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root);
            var containers = new List<string>();
            var loose = new List<string>();
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                if (string.Equals(System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar)), "Animation", StringComparison.OrdinalIgnoreCase))
                    result.AnimationRootFound = true;
                string resolved;
                try {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) result.HasLinkedSources = true;
                    resolved = DirectoryLinks.Resolve(directory);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { result.Warn(directory, error); continue; }
                if (!seen.Add(resolved)) continue;
                try
                {
                    foreach (string path in Directory.GetFiles(directory))
                    {
                        if ((string.Equals(System.IO.Path.GetExtension(path), ".clip", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(System.IO.Path.GetExtension(path), ".clips", StringComparison.OrdinalIgnoreCase))
                            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) result.HasLinkedSources = true;
                        if (string.Equals(System.IO.Path.GetExtension(path), ".clips", StringComparison.OrdinalIgnoreCase)) containers.Add(path);
                        else if (string.Equals(System.IO.Path.GetExtension(path), ".clip", StringComparison.OrdinalIgnoreCase)) loose.Add(path);
                    }
                    foreach (string path in Directory.GetDirectories(directory)) pending.Push(path);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { result.Warn(directory, error); }
            }
            foreach (string path in containers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)) result.AddFile(path, false);
            // Discovery exports edited individual records beside the original
            // containers. The newest loose record takes precedence over stock.
            var dated = new List<KeyValuePair<string, DateTime>>();
            foreach (string path in loose)
            {
                try { dated.Add(new KeyValuePair<string, DateTime>(path, File.GetLastWriteTimeUtc(path))); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { result.Warn(path, error); }
            }
            foreach (string path in dated.OrderBy(value => value.Value).ThenBy(value => value.Key, StringComparer.OrdinalIgnoreCase).Select(value => value.Key))
                result.AddFile(path, true);
            return result;
        }

        private void Warn(string path, Exception error)
        {
            ScanIncomplete = true;
            Warnings.Add(path + ": " + error.Message);
        }

        private void AddFile(string path, bool loose)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length < 24) throw new InvalidDataException("A Discovery motion file is truncated.");
                    if (loose)
                    {
                        if (file.Length > 128 * 1024 * 1024 || file.Length < 160) throw new InvalidDataException("A loose Discovery clip has an invalid size.");
                        ulong id = reader.ReadUInt64();
                        assets[id] = new DiscoveryMotionAsset { Id = id, Path = path, Offset = 0, Length = (int)file.Length, Loose = true };
                        return;
                    }
                    if (reader.ReadUInt64() != 0 || reader.ReadUInt64() != 0) throw new InvalidDataException("A Discovery motion container has an invalid header.");
                    uint count = reader.ReadUInt32();
                    uint repeated = reader.ReadUInt32();
                    if (count == 0 && repeated == 0 && file.Length == 32 && reader.ReadUInt64() == 0) return;
                    if (count == 0 || count > 100000 || count != repeated || 24L + count * 16L > file.Length)
                        throw new InvalidDataException("A Discovery motion container has an invalid record table.");
                    var entries = new List<DiscoveryMotionAsset>();
                    for (uint index = 0; index < count; index++)
                    {
                        ulong id = reader.ReadUInt64();
                        uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
                        if (length < 160 || length > 128 * 1024 * 1024 || offset < 24L + count * 16L || offset > file.Length - length)
                            throw new InvalidDataException("A Discovery motion record is outside its container.");
                        entries.Add(new DiscoveryMotionAsset { Id = id, Path = path, Offset = offset, Length = (int)length });
                    }
                    foreach (DiscoveryMotionAsset entry in entries) assets[entry.Id] = entry;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException)
            { Warn(path, error); }
        }
    }
}
