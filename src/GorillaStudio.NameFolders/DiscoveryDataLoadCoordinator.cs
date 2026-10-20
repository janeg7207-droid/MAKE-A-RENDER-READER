using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    /// <summary>Owns the current Discovery load for one application window.</summary>
    public sealed class DiscoveryDataLoadCoordinator : IDisposable
    {
        private readonly object gate = new object();
        private readonly HashSet<Lease> leases = new HashSet<Lease>();
        private Lease current;
        private long generation;
        private bool disposed;

        public sealed class Lease : IDisposable
        {
            private readonly DiscoveryDataLoadCoordinator owner;
            private readonly object cancellationGate = new object();
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private int disposed;

            internal Lease(DiscoveryDataLoadCoordinator owner, long generation, string gameRoot, string discoveryRoot)
            {
                this.owner = owner;
                Generation = generation;
                GameRoot = gameRoot;
                DiscoveryRoot = discoveryRoot;
                Token = cancellation.Token;
            }

            public long Generation { get; private set; }
            public string GameRoot { get; private set; }
            public string DiscoveryRoot { get; private set; }
            public CancellationToken Token { get; private set; }
            internal bool IsDisposed { get { return Volatile.Read(ref disposed) != 0; } }

            internal void Cancel()
            {
                lock (cancellationGate)
                {
                    if (IsDisposed) return;
                    try { cancellation.Cancel(); }
                    catch (AggregateException) { /* A failed cancellation callback cannot revive the old load. */ }
                }
            }

            public void Dispose()
            {
                lock (cancellationGate)
                {
                    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                    try { cancellation.Cancel(); }
                    catch (AggregateException) { }
                    finally { cancellation.Dispose(); }
                }
                owner.Release(this);
            }
        }

        public Lease Begin(string gameRoot, string discoveryRoot)
        {
            string game = Normalize(gameRoot), discovery = Normalize(discoveryRoot);
            Lease previous, next;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("DiscoveryDataLoadCoordinator");
                previous = current;
                next = new Lease(this, ++generation, game, discovery);
                leases.Add(next);
                current = next;
            }
            // Swap first. A replacement never waits for the old worker to finish.
            // No user cancellation callback runs while the coordinator lock is held.
            if (previous != null) previous.Cancel();
            return next;
        }

        public bool IsCurrent(Lease lease, string gameRoot, string discoveryRoot)
        {
            if (!Current(lease)) return false;
            return string.Equals(lease.GameRoot, Normalize(gameRoot), StringComparison.OrdinalIgnoreCase)
                && string.Equals(lease.DiscoveryRoot, Normalize(discoveryRoot), StringComparison.OrdinalIgnoreCase)
                && Current(lease);
        }

        public void CancelCurrent()
        {
            Lease previous;
            lock (gate) { previous = current; current = null; ++generation; }
            if (previous != null) previous.Cancel();
        }

        /// <summary>
        /// Waits for this load to be current and the window to be idle. The caller
        /// must recheck its live roots and busy state immediately before committing.
        /// Await deliberately captures the caller's synchronization context: the
        /// idle predicate may read WPF fields and must stay on the UI thread.
        /// </summary>
        public async Task WaitForCommitAsync(Lease lease, Func<bool> isIdle, CancellationToken cancellation = default(CancellationToken))
        {
            if (lease == null) throw new ArgumentNullException("lease");
            if (isIdle == null) throw new ArgumentNullException("isIdle");
            cancellation.ThrowIfCancellationRequested();
            RequireCurrent(lease);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, cancellation))
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    RequireCurrent(lease);
                    bool idle = isIdle();
                    linked.Token.ThrowIfCancellationRequested();
                    RequireCurrent(lease);
                    if (idle) return;
                    await Task.Delay(25, linked.Token);
                }
            }
        }

        private void RequireCurrent(Lease lease)
        {
            if (!Current(lease)) throw new OperationCanceledException("A newer Discovery load replaced this request.", lease.Token);
        }

        private bool Current(Lease lease)
        {
            if (lease == null || lease.IsDisposed || lease.Token.IsCancellationRequested) return false;
            lock (gate)
                return !disposed && ReferenceEquals(current, lease) && lease.Generation == generation
                    && !lease.IsDisposed && !lease.Token.IsCancellationRequested;
        }

        private void Release(Lease lease)
        {
            lock (gate)
            {
                leases.Remove(lease);
                if (ReferenceEquals(current, lease)) { current = null; ++generation; }
            }
        }

        public void Dispose()
        {
            Lease[] pending;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                current = null;
                ++generation;
                pending = new Lease[leases.Count];
                leases.CopyTo(pending);
                leases.Clear();
            }
            foreach (Lease lease in pending) lease.Dispose();
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string full = Path.GetFullPath(path), root = Path.GetPathRoot(full);
            while (full.Length > root.Length && (full[full.Length - 1] == Path.DirectorySeparatorChar
                || full[full.Length - 1] == Path.AltDirectorySeparatorChar)) full = full.Substring(0, full.Length - 1);
            return full;
        }
    }
}
