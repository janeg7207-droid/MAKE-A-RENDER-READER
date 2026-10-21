using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public sealed class RandyTauntPreviewResult
    {
        public string AtlasPath { get; private set; }
        public bool Cached { get; private set; }
        public RandyTauntPreviewResult(string atlasPath, bool cached = false)
        {
            if (string.IsNullOrWhiteSpace(atlasPath)) throw new ArgumentException("A Randy preview image is required.", "atlasPath");
            AtlasPath = Path.GetFullPath(atlasPath);
            Cached = cached;
        }
    }

    /// <summary>Opens cached motion immediately, then debounces one optional render.</summary>
    public sealed class RandyTauntPreviewSchedule
    {
        private readonly bool manual;
        private bool generation;
        public long SelectedAtMilliseconds { get; private set; }
        public bool Completed { get; private set; }
        public bool AllowRender { get { return manual || generation; } }

        public RandyTauntPreviewSchedule(bool manual, long selectedAtMilliseconds)
        {
            this.manual = manual;
            SelectedAtMilliseconds = selectedAtMilliseconds;
        }

        public bool QueueGeneration(bool foregroundBusy)
        {
            if (foregroundBusy) { Completed = true; return false; }
            if (Completed || manual || generation) return false;
            generation = true;
            return true;
        }

        public void Complete() { Completed = true; }
    }

    public sealed class RandyTauntPreviewIntent : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        public string Key { get; internal set; }
        public string Context { get; internal set; }
        public long Generation { get; internal set; }
        public CancellationToken Token { get; private set; }

        internal RandyTauntPreviewIntent(string key, string context, long generation, CancellationToken closing)
        {
            Key = key; Context = context; Generation = generation;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(closing);
            Token = cancellation.Token;
        }

        internal void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // An active producer releases its source only after its processes have
        // stopped. Disposing this request never touches a later selection.
        public void Dispose() { cancellation.Dispose(); }
    }

    /// <summary>Tracks taunt/library choices without depending on the selected wrestler.</summary>
    public sealed class RandyTauntPreviewState
    {
        private long generation;
        private bool closed;
        private RandyTauntPreviewIntent current;

        public RandyTauntPreviewIntent Select(string key, string context, CancellationToken closing = default(CancellationToken))
        {
            if (closed) return null;
            if (!ValidKey(key)) { Cancel(); return null; }
            current?.Cancel();
            current = new RandyTauntPreviewIntent(key, context ?? "", ++generation, closing);
            return current;
        }

        public bool IsCurrent(RandyTauntPreviewIntent intent, string key, string context)
        {
            return !closed && intent != null && !intent.Token.IsCancellationRequested && ReferenceEquals(current, intent)
                && intent.Generation == generation && string.Equals(intent.Key, key, StringComparison.Ordinal)
                && string.Equals(intent.Context, context ?? "", StringComparison.Ordinal);
        }

        public void Cancel() { current?.Cancel(); current = null; generation++; }
        public void Close() { Cancel(); closed = true; }
        public static bool ValidKey(string key)
        {
            return key != null && Regex.IsMatch(key, @"\A[A-Za-z0-9_]+\z")
                && !key.StartsWith("discovery_unavailable_", StringComparison.Ordinal);
        }
        public static string Identity(string key, string context) { return (context ?? "") + "\n" + (key ?? ""); }
    }
}
