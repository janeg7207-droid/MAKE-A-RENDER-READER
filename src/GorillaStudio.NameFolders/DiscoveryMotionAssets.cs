using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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
        private readonly List<DirectorySnapshot> directories = new List<DirectorySnapshot>();
        private readonly List<MotionSnapshot> sources = new List<MotionSnapshot>();
        private bool snapshotComparable = true;
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
        { return Read(discoveryRoot, CancellationToken.None); }

        public static DiscoveryMotionAssets Read(string discoveryRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new DiscoveryMotionAssets();
            string root = System.IO.Path.GetFullPath(discoveryRoot);
            result.searchRoots.Add(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<PendingDirectory>();
            pending.Push(new PendingDirectory { Path = root, BoundPath = root });
            var containers = new List<MotionSnapshot>();
            var loose = new List<MotionSnapshot>();
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingDirectory next = pending.Pop();
                string directory = next.Path;
                if (string.Equals(System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar)), "Animation", StringComparison.OrdinalIgnoreCase))
                    result.AnimationRootFound = true;
                DirectorySnapshot snapshot;
                try {
                    FileAttributes attributes = File.GetAttributes(next.BoundPath);
                    string resolved = DirectoryLinks.Resolve(next.BoundPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || !SamePath(directory, resolved)) result.HasLinkedSources = true;
                    snapshot = new DirectorySnapshot { Path = directory, Resolved = resolved, Attributes = attributes };
                    result.directories.Add(snapshot);
                }
                catch (Exception error) when (SourceError(error))
                { result.snapshotComparable = false; result.Warn(directory, error); continue; }
                if (!seen.Add(snapshot.Resolved)) continue;
                try
                {
                    long before = Directory.GetLastWriteTimeUtc(snapshot.Resolved).Ticks;
                    snapshot.Children = CollectNames(Directory.EnumerateDirectories(snapshot.Resolved), cancellationToken, false);
                    var motionNames = new List<string>();
                    foreach (string boundFile in Directory.EnumerateFiles(snapshot.Resolved))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsMotion(boundFile)) continue;
                        string name = System.IO.Path.GetFileName(boundFile), logical = System.IO.Path.Combine(directory, name);
                        motionNames.Add(name);
                        MotionSnapshot source = MotionSnapshot.Capture(logical, boundFile);
                        if ((source.Attributes & FileAttributes.ReparsePoint) != 0 || !SamePath(logical, source.Resolved)) result.HasLinkedSources = true;
                        result.sources.Add(source);
                        if (string.Equals(System.IO.Path.GetExtension(name), ".clips", StringComparison.OrdinalIgnoreCase)) containers.Add(source);
                        else loose.Add(source);
                    }
                    snapshot.Motions = motionNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
                    snapshot.Enumerated = true;
                    foreach (string name in snapshot.Children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pending.Push(new PendingDirectory { Path = System.IO.Path.Combine(directory, name),
                            BoundPath = System.IO.Path.Combine(snapshot.Resolved, name) });
                    }
                    if (before != Directory.GetLastWriteTimeUtc(snapshot.Resolved).Ticks) result.snapshotComparable = false;
                }
                catch (Exception error) when (SourceError(error))
                { result.snapshotComparable = false; result.Warn(directory, error); }
            }
            foreach (MotionSnapshot source in containers.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
            { cancellationToken.ThrowIfCancellationRequested(); result.AddFile(source, false, cancellationToken); }
            // Discovery exports edited individual records beside the original
            // containers. The newest loose record takes precedence over stock.
            foreach (MotionSnapshot source in loose.OrderBy(value => value.Written).ThenBy(value => value.Path, StringComparer.OrdinalIgnoreCase))
            { cancellationToken.ThrowIfCancellationRequested(); result.AddFile(source, true, cancellationToken); }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>Audits the captured paths without reopening any animation table or payload.</summary>
        public bool IsCurrent(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!snapshotComparable) return false;
            try
            {
                foreach (DirectorySnapshot directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!SamePath(directory.Resolved, DirectoryLinks.Resolve(directory.Path))
                        || File.GetAttributes(directory.Path) != directory.Attributes) return false;
                    if (!directory.Enumerated) continue; // A second alias/cycle still has its target audited above.
                    if (!NamesMatch(directory.Children, Directory.EnumerateDirectories(directory.Resolved), cancellationToken, false)
                        || !NamesMatch(directory.Motions, Directory.EnumerateFiles(directory.Resolved), cancellationToken, true)) return false;
                }
                foreach (MotionSnapshot source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!source.Matches(true)) return false;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception error) when (SourceError(error)) { return false; }
        }

        private static bool NamesMatch(string[] expected, IEnumerable<string> current, CancellationToken cancellationToken, bool motionOnly)
        { return expected.SequenceEqual(CollectNames(current, cancellationToken, motionOnly), StringComparer.Ordinal); }

        private static string[] CollectNames(IEnumerable<string> current, CancellationToken cancellationToken, bool motionOnly)
        {
            var names = new List<string>();
            foreach (string path in current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!motionOnly || IsMotion(path)) names.Add(System.IO.Path.GetFileName(path));
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        private static bool IsMotion(string path)
        {
            string extension = System.IO.Path.GetExtension(path);
            return string.Equals(extension, ".clip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".clips", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string first, string second)
        { return string.Equals(System.IO.Path.GetFullPath(first).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }

        private static bool SourceError(Exception error)
        { return error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is System.ComponentModel.Win32Exception; }

        private sealed class PendingDirectory { internal string Path, BoundPath; }
        private sealed class DirectorySnapshot
        {
            internal string Path, Resolved;
            internal FileAttributes Attributes;
            internal bool Enumerated;
            internal string[] Children, Motions;
        }

        private sealed class MotionSnapshot
        {
            internal string Path, Resolved;
            internal long Length, Created, Written;
            internal FileAttributes Attributes, TargetAttributes;

            internal static MotionSnapshot Capture(string logical, string bound)
            {
                var entry = new FileInfo(bound);
                FileAttributes attributes = entry.Attributes;
                bool link = (attributes & FileAttributes.ReparsePoint) != 0;
                // The directory walk has already resolved this physical parent.
                // Normal files need no additional Windows handle/path lookup.
                string resolved = link ? DirectoryLinks.Resolve(bound) : System.IO.Path.GetFullPath(bound);
                var file = link ? new FileInfo(resolved) : entry;
                if (!file.Exists) throw new FileNotFoundException("A Discovery motion file disappeared during its scan.", logical);
                return new MotionSnapshot { Path = logical, Resolved = resolved, Length = file.Length,
                    Created = file.CreationTimeUtc.Ticks, Written = file.LastWriteTimeUtc.Ticks,
                    Attributes = attributes, TargetAttributes = file.Attributes };
            }

            internal bool Matches(bool logical)
            {
                bool link = (Attributes & FileAttributes.ReparsePoint) != 0;
                if (logical && link && (File.GetAttributes(Path) != Attributes || !SamePath(Resolved, DirectoryLinks.Resolve(Path)))) return false;
                var file = new FileInfo(logical && !link ? Path : Resolved);
                return file.Exists && file.Length == Length && file.CreationTimeUtc.Ticks == Created
                    && file.LastWriteTimeUtc.Ticks == Written && file.Attributes == TargetAttributes
                    && (!logical || link || file.Attributes == Attributes);
            }
        }

        private void Warn(string path, Exception error)
        {
            ScanIncomplete = true;
            Warnings.Add(path + ": " + error.Message);
        }

        private void AddFile(MotionSnapshot source, bool loose, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!source.Matches(false)) { snapshotComparable = false; throw new IOException("A Discovery motion file changed during its scan."); }
                var entries = new List<DiscoveryMotionAsset>();
                using (var file = new FileStream(source.Resolved, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length < 24) throw new InvalidDataException("A Discovery motion file is truncated.");
                    if (loose)
                    {
                        if (file.Length > 128 * 1024 * 1024 || file.Length < 160) throw new InvalidDataException("A loose Discovery clip has an invalid size.");
                        ulong id = reader.ReadUInt64();
                        entries.Add(new DiscoveryMotionAsset { Id = id, Path = source.Path, Offset = 0, Length = (int)file.Length, Loose = true });
                    }
                    else
                    {
                        if (reader.ReadUInt64() != 0 || reader.ReadUInt64() != 0) throw new InvalidDataException("A Discovery motion container has an invalid header.");
                        uint count = reader.ReadUInt32(), repeated = reader.ReadUInt32();
                        bool empty = count == 0 && repeated == 0 && file.Length == 32 && reader.ReadUInt64() == 0;
                        if (!empty)
                        {
                            if (count == 0 || count > 100000 || count != repeated || 24L + count * 16L > file.Length)
                                throw new InvalidDataException("A Discovery motion container has an invalid record table.");
                            for (uint index = 0; index < count; index++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                ulong id = reader.ReadUInt64();
                                uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
                                if (length < 160 || length > 128 * 1024 * 1024 || offset < 24L + count * 16L || offset > file.Length - length)
                                    throw new InvalidDataException("A Discovery motion record is outside its container.");
                                entries.Add(new DiscoveryMotionAsset { Id = id, Path = source.Path, Offset = offset, Length = (int)length });
                            }
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!source.Matches(false)) { snapshotComparable = false; throw new IOException("A Discovery motion file changed during its scan."); }
                foreach (DiscoveryMotionAsset entry in entries)
                { cancellationToken.ThrowIfCancellationRequested(); assets[entry.Id] = entry; }
            }
            catch (Exception error) when (SourceError(error) || error is InvalidDataException)
            {
                if (!(error is InvalidDataException)) snapshotComparable = false;
                else
                {
                    try { if (!source.Matches(false)) snapshotComparable = false; }
                    catch (Exception stampError) when (SourceError(stampError)) { snapshotComparable = false; }
                }
                Warn(source.Path, error);
            }
        }
    }
}
