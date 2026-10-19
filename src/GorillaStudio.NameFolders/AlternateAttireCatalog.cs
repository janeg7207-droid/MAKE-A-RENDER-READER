using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class AlternateAttireCatalogResult
    {
        public string Folder { get; internal set; }
        public IReadOnlyList<AlternateAttire> Models { get; internal set; }
        public Exception Error { get; internal set; }
        internal long Generation;
    }

    /// <summary>Reads one wrestler's attire choices without blocking its UI or accepting an older wrestler's scan.</summary>
    public sealed class AlternateAttireCatalog
    {
        private readonly object gate = new object();
        private readonly Func<string, List<AlternateAttire>> read;
        private string folder;
        private bool hasFolder;
        private bool closed;
        private long generation;
        private IReadOnlyList<AlternateAttire> models;
        private Task<AlternateAttireCatalogResult> pending;

        public AlternateAttireCatalog() : this(AlternateAttires.Read) { }

        public AlternateAttireCatalog(Func<string, List<AlternateAttire>> read)
        { this.read = read ?? throw new ArgumentNullException(nameof(read)); }

        public bool IsLoading
        { get { lock (gate) return pending != null && !pending.IsCompleted; } }

        public Task<AlternateAttireCatalogResult> ReadAsync(string characterFolder, bool refresh)
        {
            lock (gate)
            {
                if (closed) return Task.FromResult<AlternateAttireCatalogResult>(null);
                bool same = hasFolder && Same(folder, characterFolder);
                if (same && pending != null && !pending.IsCompleted) return pending;
                if (same && !refresh && models != null)
                    return Task.FromResult(new AlternateAttireCatalogResult {
                        Folder = folder, Generation = generation, Models = models
                    });
                folder = characterFolder;
                hasFolder = true;
                if (!same) models = null;
                long request = ++generation;
                pending = ReadCoreAsync(characterFolder, request);
                return pending;
            }
        }

        private async Task<AlternateAttireCatalogResult> ReadCoreAsync(string characterFolder, long request)
        {
            IReadOnlyList<AlternateAttire> found = null;
            Exception failure = null;
            try { found = (await Task.Run(() => read(characterFolder)).ConfigureAwait(false)).ToArray(); }
            catch (Exception error) { failure = error; }
            lock (gate)
            {
                if (closed || generation != request || !Same(folder, characterFolder)) return null;
                if (failure == null) models = found;
                return new AlternateAttireCatalogResult {
                    Folder = characterFolder, Generation = request, Models = found, Error = failure
                };
            }
        }

        public bool IsCurrent(AlternateAttireCatalogResult result)
        {
            lock (gate) return result != null && !closed && result.Generation == generation && Same(result.Folder, folder);
        }

        public void Close()
        {
            lock (gate) { closed = true; generation++; models = null; }
        }

        private static bool Same(string left, string right)
        {
            return string.Equals(left == null ? null : left.TrimEnd('\\', '/'),
                right == null ? null : right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
