using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static readonly string Game = Path.Combine(Path.GetTempPath(), "coordinator-game");
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "coordinator-discovery");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Task<Dictionary<string, object>> benchmark = BlockedArchiveHandoff();
            Ownership();
            await WaitForIdle();
            await Cancellation();
            await PredicateSequencing();
            await ArchivePhaseWait();
            await CapturedContext();
            await ConcurrentSupersession();
            Dictionary<string, object> result = await benchmark;
            result["checks"] = passed;
            if (args.Length > 0) {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
                File.WriteAllText(args[0], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.WriteLine("PASS " + passed + " Discovery data load coordinator checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Ownership()
    {
        var coordinator = new DiscoveryDataLoadCoordinator();
        var first = coordinator.Begin(Game, Data);
        Check(coordinator.IsCurrent(first, Game, Data), "A new load owns its game and Discovery roots");
        Check(coordinator.IsCurrent(first, Game + Path.DirectorySeparatorChar,
            Path.Combine(Data, "child", "..")), "Equivalent full paths preserve the active load");
        Check(!coordinator.IsCurrent(first, Game + "-other", Data), "Changing the game root invalidates a proposed commit");
        Check(!coordinator.IsCurrent(first, Game, Data + "-other"), "Changing the Discovery root invalidates a proposed commit");
        var second = coordinator.Begin(Game, Data + "-other");
        Check(second.Generation > first.Generation, "Every replacement receives a newer generation");
        Check(first.Token.IsCancellationRequested && !coordinator.IsCurrent(first, Game, Data), "A replacement cancels and invalidates its predecessor");
        first.Dispose(); first.Dispose();
        Check(coordinator.IsCurrent(second, Game, Data + "-other") && !second.Token.IsCancellationRequested,
            "Disposing a superseded lease cannot cancel its replacement");
        using (var other = new DiscoveryDataLoadCoordinator()) {
            var foreign = other.Begin(Game, Data + "-other");
            Check(!coordinator.IsCurrent(foreign, Game, Data + "-other"), "A different window cannot supply the active lease");
        }
        coordinator.CancelCurrent(); coordinator.CancelCurrent();
        Check(second.Token.IsCancellationRequested && !coordinator.IsCurrent(second, Game, Data + "-other"),
            "CancelCurrent is idempotent and invalidates the current generation");
        var third = coordinator.Begin(null, Data);
        Check(third.GameRoot == "" && coordinator.IsCurrent(third, "", Data), "Discovery metadata can load before a game path is configured");
        third.Dispose();
        Check(third.Token.IsCancellationRequested && !coordinator.IsCurrent(third, null, Data), "Disposing the active lease cancels and releases it");
        var fourth = coordinator.Begin(Game, Data);
        coordinator.Dispose(); coordinator.Dispose(); second.Dispose(); fourth.Dispose();
        Check(fourth.Token.IsCancellationRequested && !coordinator.IsCurrent(fourth, Game, Data), "Window disposal cancels all retained leases safely");
        Throws<ObjectDisposedException>(() => coordinator.Begin(Game, Data), "A disposed window cannot start another load");
    }

    private static async Task WaitForIdle()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        using (var lease = coordinator.Begin(Game, Data))
        {
            int idleReads = 0, busy = 1;
            Task waiting = coordinator.WaitForCommitAsync(lease, () => { Interlocked.Increment(ref idleReads); return Volatile.Read(ref busy) == 0; });
            await Task.Delay(90);
            Check(!waiting.IsCompleted && idleReads >= 2, "A busy render keeps metadata waiting without an early commit");
            Check(Volatile.Read(ref busy) == 1, "The coordinator never changes the renderer's busy state");
            Volatile.Write(ref busy, 0);
            await Before(waiting);
            Check(coordinator.IsCurrent(lease, Game, Data), "The current load can commit when the render becomes idle");
            bool called = false;
            await coordinator.WaitForCommitAsync(lease, () => { called = true; return true; });
            Check(called, "An idle current load proceeds immediately");
        }
    }

    private static async Task Cancellation()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        {
            var old = coordinator.Begin(Game, Data);
            int reads = 0;
            Task waiting = coordinator.WaitForCommitAsync(old, () => { reads++; return false; });
            var next = coordinator.Begin(Game, Data + "-next");
            await Canceled(waiting, "Supersession promptly cancels an idle wait");
            int stopped = reads; await Task.Delay(60);
            Check(reads == stopped, "A superseded wait stops polling the window");
            using (var cancellation = new CancellationTokenSource()) {
                Task external = coordinator.WaitForCommitAsync(next, () => false, cancellation.Token);
                cancellation.Cancel();
                await Canceled(external, "Caller cancellation stops the wait");
                Check(coordinator.IsCurrent(next, Game, Data + "-next") && !next.Token.IsCancellationRequested,
                    "Canceling one waiter does not cancel the active metadata load");
            }
            Task closing = coordinator.WaitForCommitAsync(next, () => false);
            coordinator.Dispose();
            await Canceled(closing, "Window disposal stops an outstanding wait");
        }
    }

    private static async Task PredicateSequencing()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        {
            var lease = coordinator.Begin(Game, Data);
            bool called = false;
            using (var cancellation = new CancellationTokenSource()) {
                cancellation.Cancel();
                await Canceled(coordinator.WaitForCommitAsync(lease, () => { called = true; return true; }, cancellation.Token),
                    "An already canceled waiter is rejected");
                Check(!called, "An already canceled waiter never touches UI fields");
            }
            Task replacedInPredicate = coordinator.WaitForCommitAsync(lease, () => {
                coordinator.Begin(Game, Data + "-replacement"); return true;
            });
            await Canceled(replacedInPredicate, "A generation replaced inside the idle predicate cannot commit");
            var current = coordinator.Begin(Game, Data);
            using (var cancellation = new CancellationTokenSource()) {
                Task canceledInPredicate = coordinator.WaitForCommitAsync(current, () => { cancellation.Cancel(); return true; }, cancellation.Token);
                await Canceled(canceledInPredicate, "Cancellation inside the idle predicate cannot commit");
            }
            await Canceled(coordinator.WaitForCommitAsync(lease, () => { throw new Exception("Stale predicate must not run"); }),
                "A stale load is rejected before reading UI state");
        }
    }

    private static async Task CapturedContext()
    {
        using (var context = new ThreadContext())
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        using (var lease = coordinator.Begin(Game, Data))
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threads = new List<int>();
            context.Post(async state => {
                try {
                    await coordinator.WaitForCommitAsync(lease, () => {
                        threads.Add(Thread.CurrentThread.ManagedThreadId); return threads.Count >= 3;
                    });
                    completion.SetResult(true);
                }
                catch (Exception error) { completion.SetException(error); }
            }, null);
            await Before(completion.Task);
            Check(threads.Count >= 3 && threads.All(id => id == context.ThreadId),
                "Every delayed idle predicate stays on the caller's UI synchronization context");
        }
    }

    private static async Task ArchivePhaseWait()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        {
            var lease = coordinator.Begin(Game, Data);
            int rosterPending = 1, picturePending = 1, archiveCalls = 0;
            Task archive = StartArchiveAfterIdle(coordinator, lease,
                () => Volatile.Read(ref rosterPending) == 0 && Volatile.Read(ref picturePending) == 0,
                () => Interlocked.Increment(ref archiveCalls));
            await Task.Delay(65);
            Check(archiveCalls == 0 && !archive.IsCompleted,
                "The archive phase stays pending while roster and picture preparation own the reader");
            Volatile.Write(ref rosterPending, 0);
            await Task.Delay(65);
            Check(archiveCalls == 0 && !archive.IsCompleted,
                "Finishing the roster alone does not start archive work while its picture is pending");
            Volatile.Write(ref picturePending, 0);
            await Before(archive);
            Check(archiveCalls == 1, "The archive phase starts once after both interactive preparations clear");
            var canceledLease = coordinator.Begin(Game, Data + "-cancel-archive");
            int canceledCalls = 0;
            Task canceledArchive = StartArchiveAfterIdle(coordinator, canceledLease, () => false,
                () => Interlocked.Increment(ref canceledCalls));
            coordinator.CancelCurrent();
            await Canceled(canceledArchive, "Canceling before the reader is idle cancels the deferred archive phase");
            Check(canceledCalls == 0, "A canceled pending archive phase never opens the archive reader");
        }
    }

    private static async Task StartArchiveAfterIdle(DiscoveryDataLoadCoordinator coordinator,
        DiscoveryDataLoadCoordinator.Lease lease, Func<bool> idle, Action archive)
    {
        await coordinator.WaitForCommitAsync(lease, idle, lease.Token);
        lease.Token.ThrowIfCancellationRequested();
        archive();
    }

    private static async Task ConcurrentSupersession()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        {
            var leases = new ConcurrentBag<DiscoveryDataLoadCoordinator.Lease>();
            await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() => leases.Add(coordinator.Begin(Game, Data)))));
            var newest = leases.OrderByDescending(lease => lease.Generation).First();
            Check(leases.Count(lease => coordinator.IsCurrent(lease, Game, Data)) == 1 && coordinator.IsCurrent(newest, Game, Data),
                "Concurrent starts leave exactly the newest generation current");
            Check(leases.Where(lease => !ReferenceEquals(lease, newest)).All(lease => lease.Token.IsCancellationRequested),
                "Concurrent replacements cancel every older generation");
        }
    }

    private static async Task<Dictionary<string, object>> BlockedArchiveHandoff()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        {
            var archiveLease = coordinator.Begin(Game, Data);
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var archiveTime = Stopwatch.StartNew();
            Task archive = Task.Run(async () => {
                started.SetResult(true);
                // Deliberately models an archive reader that cannot honor cancellation.
                await Task.Delay(8000);
            });
            await started.Task;
            var handoff = Stopwatch.StartNew();
            var metadata = coordinator.Begin(Game, Data + "-fresh");
            bool metadataCallback = false;
            await coordinator.WaitForCommitAsync(metadata, () => true);
            if (coordinator.IsCurrent(metadata, Game, Data + "-fresh")) metadataCallback = true;
            handoff.Stop();
            Check(metadataCallback && handoff.ElapsedMilliseconds < 5000 && !archive.IsCompleted,
                "Fresh metadata commits under five seconds while the old archive remains blocked for eight seconds");
            Check(archiveLease.Token.IsCancellationRequested && !coordinator.IsCurrent(archiveLease, Game, Data),
                "A blocked archive loses commit ownership immediately on handoff");
            await archive;
            archiveTime.Stop();
            Check(!coordinator.IsCurrent(archiveLease, Game, Data) && coordinator.IsCurrent(metadata, Game, Data + "-fresh"),
                "Late completion of the blocked archive cannot replace the fresh metadata");
            return new Dictionary<string, object> {
                { "fakeArchiveBlockMs", 8000 }, { "oldArchiveElapsedMs", archiveTime.ElapsedMilliseconds },
                { "newMetadataHandoffMs", handoff.ElapsedMilliseconds }, { "metadataCommittedBeforeArchiveFinished", metadataCallback }
            };
        }
    }

    private static async Task Before(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(4000)) != task) throw new TimeoutException("The expected cancellation or idle transition did not complete.");
        await task;
    }
    private static async Task Canceled(Task task, string message)
    {
        try { await Before(task); }
        catch (OperationCanceledException) { Check(true, message); return; }
        throw new Exception("Expected cancellation: " + message);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Interlocked.Increment(ref passed); Console.WriteLine("PASS " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { Check(true, message); return; } throw new Exception("Expected " + typeof(T).Name + ": " + message); }

    private sealed class ThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread thread;
        internal int ThreadId { get; private set; }
        internal ThreadContext()
        {
            using (var ready = new ManualResetEventSlim()) {
                thread = new Thread(() => {
                    ThreadId = Thread.CurrentThread.ManagedThreadId;
                    SetSynchronizationContext(this); ready.Set();
                    foreach (Action action in queue.GetConsumingEnumerable()) action();
                });
                thread.IsBackground = true; thread.Start(); ready.Wait();
            }
        }
        public override void Post(SendOrPostCallback callback, object state) { queue.Add(() => callback(state)); }
        public void Dispose() { queue.CompleteAdding(); thread.Join(); queue.Dispose(); }
    }
}
