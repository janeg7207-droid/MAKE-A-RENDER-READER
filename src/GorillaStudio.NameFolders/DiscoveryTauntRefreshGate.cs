using System;
using System.IO;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryTauntRefreshRequest
    {
        public string Root { get; internal set; }
        public string Folder { get; internal set; }
        public long Generation { get; internal set; }
    }

    /// <summary>
    /// Coalesces model-load refresh requests. A busy renderer or unfinished model
    /// preparation delays the request; it must not silently discard it. Only the
    /// most recently selected folder is allowed to start a pending refresh.
    /// </summary>
    public sealed class DiscoveryTauntRefreshGate
    {
        private DiscoveryTauntRefreshRequest pending;
        private DiscoveryTauntRefreshRequest active;
        private long generation;

        public bool HasWork { get { return pending != null || active != null; } }

        public void Enqueue(string root, string folder)
        {
            string normalizedRoot = Normalize(root), normalizedFolder = Normalize(folder);
            if (pending != null && Same(pending.Root, normalizedRoot) && Same(pending.Folder, normalizedFolder)) return;
            pending = new DiscoveryTauntRefreshRequest
            {
                Root = normalizedRoot, Folder = normalizedFolder, Generation = ++generation
            };
        }

        public DiscoveryTauntRefreshRequest TryBegin(string currentFolder, bool blocked)
        {
            if (active != null || pending == null || blocked) return null;
            if (string.IsNullOrWhiteSpace(currentFolder) || !Same(pending.Folder, Normalize(currentFolder)))
            {
                pending = null;
                return null;
            }
            active = pending;
            pending = null;
            return active;
        }

        public void Complete(DiscoveryTauntRefreshRequest request)
        {
            if (ReferenceEquals(active, request)) active = null;
        }

        public void CancelPending() { pending = null; generation++; }

        private static bool Same(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A model or Discovery folder is required.", "path");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
