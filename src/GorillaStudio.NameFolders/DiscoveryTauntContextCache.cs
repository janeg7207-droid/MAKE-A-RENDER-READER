using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    /// <summary>Reuses a completed library until its watched source context changes.</summary>
    public sealed class DiscoveryTauntContextCache : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly string game, discovery;
        private long revision, completedRevision = -1;
        private bool healthy = true, disposed;

        public DiscoveryTauntContextCache(string gameRoot, string discoveryRoot)
        {
            game = Normalize(gameRoot); discovery = Normalize(discoveryRoot);
            foreach (string root in new[] { discovery, game }.Where(value => value != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var watcher = new FileSystemWatcher(root) {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                    };
                    watcher.Created += Changed; watcher.Deleted += Changed;
                    watcher.Renamed += Changed; watcher.Changed += Changed;
                    watcher.Error += (sender, args) => { lock (gate) { healthy = false; revision++; } };
                    watchers.Add(watcher); watcher.EnableRaisingEvents = true;
                    string parent = Path.GetDirectoryName(root);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        // A watcher follows an open directory handle. Replacing
                        // the configured folder at the same pathname must not
                        // leave a reusable cache watching its old location.
                        var parentWatcher = new FileSystemWatcher(parent) { NotifyFilter = NotifyFilters.DirectoryName };
                        FileSystemEventHandler replaced = (sender, args) => {
                            var renamed = args as RenamedEventArgs;
                            if (string.Equals(Normalize(args.FullPath), root, StringComparison.OrdinalIgnoreCase)
                                || (renamed != null && string.Equals(Normalize(renamed.OldFullPath), root, StringComparison.OrdinalIgnoreCase)))
                                lock (gate) { healthy = false; revision++; }
                        };
                        parentWatcher.Created += replaced; parentWatcher.Deleted += replaced;
                        parentWatcher.Renamed += (sender, args) => replaced(sender, args);
                        parentWatcher.Error += (sender, args) => { lock (gate) { healthy = false; revision++; } };
                        watchers.Add(parentWatcher); parentWatcher.EnableRaisingEvents = true;
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                    || error is ArgumentException || error is NotSupportedException || error is System.ComponentModel.Win32Exception)
                { healthy = false; }
            }
            if (watchers.Count == 0) healthy = false;
        }

        public bool Matches(string gameRoot, string discoveryRoot)
        {
            return string.Equals(game, Normalize(gameRoot), StringComparison.OrdinalIgnoreCase)
                && string.Equals(discovery, Normalize(discoveryRoot), StringComparison.OrdinalIgnoreCase);
        }

        public bool CanReuse { get { lock (gate) return !disposed && healthy && completedRevision == revision; } }
        public long BeginRefresh() { lock (gate) return revision; }
        public void CompleteRefresh(long startedRevision, bool sourcesFullyWatched = true)
        {
            lock (gate) if (!disposed) completedRevision = sourcesFullyWatched ? startedRevision : -1;
        }
        public void Invalidate() { lock (gate) revision++; }

        private void Changed(object sender, FileSystemEventArgs args)
        {
            // Creation, removal and renaming may change a directory tree, even
            // when its name has a file extension. Never ignore those events.
            if (args.ChangeType != WatcherChangeTypes.Changed || Relevant(args.FullPath)) Invalidate();
        }

        private static bool Relevant(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Length == 0 || new[] { ".clip", ".clips", ".cak", ".jsfb", ".json", ".sdb" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; healthy = false; }
            foreach (FileSystemWatcher watcher in watchers) watcher.Dispose();
            watchers.Clear();
        }
    }
}
