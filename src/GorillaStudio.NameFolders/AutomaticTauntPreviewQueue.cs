using System;

namespace GorillaStudio.NameFolders
{
    public sealed class AutomaticTauntPreviewRequest
    {
        public string Identity { get; internal set; }
        public long Generation { get; internal set; }
        internal long ReadyAt;
    }

    /// <summary>
    /// Debounces explicit selections without interrupting a native render. Busy
    /// work delays the latest request; refreshes and closing cancel queued work.
    /// Time is supplied by the caller's monotonic clock, not the system clock.
    /// </summary>
    public sealed class AutomaticTauntPreviewQueue
    {
        private readonly long debounceMilliseconds;
        private AutomaticTauntPreviewRequest pending;
        private AutomaticTauntPreviewRequest active;
        private long generation;
        private bool closed;

        public AutomaticTauntPreviewQueue(long debounceMilliseconds = 650)
        {
            if (debounceMilliseconds < 0 || debounceMilliseconds > 10000) throw new ArgumentOutOfRangeException("debounceMilliseconds");
            this.debounceMilliseconds = debounceMilliseconds;
        }

        public bool HasWork { get { return pending != null || active != null; } }
        public bool IsRunning { get { return active != null; } }

        public void Enqueue(string identity, long nowMilliseconds)
        {
            if (closed) return;
            if (string.IsNullOrEmpty(identity)) throw new ArgumentException("A selected preview is required.", "identity");
            if (pending != null && pending.Identity == identity) return;
            pending = new AutomaticTauntPreviewRequest
            {
                Identity = identity, Generation = ++generation,
                ReadyAt = checked(nowMilliseconds + debounceMilliseconds)
            };
        }

        public AutomaticTauntPreviewRequest TryBegin(string currentIdentity, bool blocked, long nowMilliseconds)
        {
            if (closed || active != null || pending == null) return null;
            if (pending.Identity != currentIdentity) { pending = null; return null; }
            if (blocked || nowMilliseconds < pending.ReadyAt) return null;
            active = pending; pending = null;
            return active;
        }

        public void Complete(AutomaticTauntPreviewRequest request)
        {
            if (ReferenceEquals(active, request)) active = null;
        }

        public void CancelPending() { pending = null; generation++; }
        public void Close() { closed = true; pending = null; generation++; }
    }
}
