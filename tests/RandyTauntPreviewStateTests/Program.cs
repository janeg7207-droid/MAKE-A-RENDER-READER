using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static async Task<int> Main()
    {
        try
        {
            ExplicitSelections();
            LibraryChanges();
            CancellationAndClose();
            LinkedWindowCancellationAndRelease();
            OldCleanupCannotCancelNewSelection();
            InvalidKeys();
            AtlasResult();
            await CanceledProducerDrainsBeforeLatestSelection();
            await ClosedWindowRejectsLateSuccess();
            Console.WriteLine("PASS " + passed + " Randy taunt preview state checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void ExplicitSelections()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent first = state.Select("t62247", "library-A");
        Check(first != null && state.IsCurrent(first, "t62247", "library-A"),
            "An explicit taunt selection creates a current library-scoped intent");
        RandyTauntPreviewIntent second = state.Select("discovery_62247a001", "library-A");
        Check(first.Token.IsCancellationRequested && !second.Token.IsCancellationRequested,
            "Selecting another taunt requests cancellation of only the superseded producer");
        Check(!state.IsCurrent(first, "t62247", "library-A"),
            "A completed older taunt cannot replace the newly selected taunt");
        Check(state.IsCurrent(second, "discovery_62247a001", "library-A"),
            "A new Discovery taunt can own the same Randy preview workflow");
        RandyTauntPreviewIntent repeat = state.Select("discovery_62247a001", "library-A");
        Check(second.Token.IsCancellationRequested && !repeat.Token.IsCancellationRequested,
            "A same-taunt retry cancels the older attempt rather than inheriting its token");
        Check(repeat.Generation > second.Generation && !ReferenceEquals(repeat, second),
            "Repeating the same taunt creates a distinct retry generation");
        Check(!state.IsCurrent(second, "discovery_62247a001", "library-A") && state.IsCurrent(repeat, repeat.Key, repeat.Context),
            "A late success from the first attempt cannot satisfy a newer retry of the same taunt");
        Check(!state.IsCurrent(repeat, "discovery_62247a002", "library-A"),
            "A current intent cannot publish against another selected variant");
        Check(!state.IsCurrent(null, repeat.Key, repeat.Context),
            "A missing request cannot own the displayed preview");
        string identity = RandyTauntPreviewState.Identity("t62247", "library-A");
        Check(identity != RandyTauntPreviewState.Identity("t62247", "library-B")
            && identity != RandyTauntPreviewState.Identity("t62248", "library-A"),
            "The shared queue distinguishes motion changes and library changes");
        Check(identity == RandyTauntPreviewState.Identity(first.Key, first.Context),
            "A repeated motion in one library retains a shared queue identity");
        state.Close(); first.Dispose(); second.Dispose(); repeat.Dispose();
    }

    private static void LibraryChanges()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent original = state.Select("t62247", "overlay-before");
        Check(!state.IsCurrent(original, original.Key, "overlay-after"),
            "Replacing the active Discovery library rejects an in-flight old result");
        RandyTauntPreviewIntent replacement = state.Select("t62247", "overlay-after");
        Check(original.Token.IsCancellationRequested && !replacement.Token.IsCancellationRequested,
            "A new library selection cancels native work owned by the old library");
        Check(!state.IsCurrent(original, original.Key, original.Context)
            && state.IsCurrent(replacement, replacement.Key, replacement.Context),
            "A matching name in the replacement library gets its own current intent");
        RandyTauntPreviewIntent restored = state.Select("t62247", "overlay-before");
        Check(!state.IsCurrent(original, original.Key, original.Context)
            && state.IsCurrent(restored, restored.Key, restored.Context),
            "Returning to an older library does not reactivate its earlier request object");
        RandyTauntPreviewIntent bundled = state.Select("orton3", null);
        Check(bundled.Context == "" && state.IsCurrent(bundled, "orton3", ""),
            "Bundled-only use has an explicit empty context rather than a missing intent");
        Check(state.IsCurrent(bundled, "orton3", null),
            "The bundled empty context remains stable when the caller has no Discovery root");
        state.Close(); original.Dispose(); replacement.Dispose(); restored.Dispose(); bundled.Dispose();
    }

    private static void CancellationAndClose()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent first = state.Select("t62247", "library");
        state.Cancel();
        Check(first.Token.IsCancellationRequested,
            "Explicit cancellation also signals the producer to stop its registered process");
        Check(!state.IsCurrent(first, first.Key, first.Context),
            "Canceling a pending selection prevents its late result from displaying");
        RandyTauntPreviewIntent next = state.Select("t62247", "library");
        Check(next != null && state.IsCurrent(next, next.Key, next.Context),
            "Cancellation leaves the window able to request another Randy preview");
        state.Close();
        Check(next.Token.IsCancellationRequested,
            "Closing signals active producer cancellation before waiting for cleanup");
        Check(!state.IsCurrent(next, next.Key, next.Context),
            "Closing invalidates the selected taunt immediately");
        Check(state.Select("orton3", "library") == null,
            "A closed window cannot create another preview request");
        state.Cancel(); state.Close();
        Check(!state.IsCurrent(next, next.Key, next.Context),
            "Repeated close and cancellation never revive a completed intent");
        first.Dispose(); next.Dispose();
    }

    private static void LinkedWindowCancellationAndRelease()
    {
        using (var closing = new CancellationTokenSource())
        {
            var state = new RandyTauntPreviewState();
            RandyTauntPreviewIntent old = state.Select("t62247", "library", closing.Token);
            RandyTauntPreviewIntent latest = state.Select("t62248", "library", closing.Token);
            Check(old.Token.IsCancellationRequested && !latest.Token.IsCancellationRequested,
                "Each linked intent owns a distinct cancellation source");
            old.Dispose();
            Check(!latest.Token.IsCancellationRequested && state.IsCurrent(latest, latest.Key, latest.Context),
                "Disposing a superseded linked source leaves the newest intent and window lifetime active");
            int callbacks = 0;
            using (latest.Token.Register(() => callbacks++))
            {
                closing.Cancel();
                Check(latest.Token.IsCancellationRequested && callbacks == 1,
                    "The window lifetime token cancels the latest producer exactly once");
                Check(!state.IsCurrent(latest, latest.Key, latest.Context),
                    "A linked cancellation rejects a late result even before the UI calls Close");
                state.Close();
                Check(callbacks == 1, "Closing state after linked cancellation does not run cleanup callbacks twice");
            }
            latest.Dispose();
            Check(old.Token.IsCancellationRequested && latest.Token.IsCancellationRequested,
                "Disposed intent tokens remain readable by their final cleanup checks");
        }
        using (var alreadyClosed = new CancellationTokenSource())
        {
            alreadyClosed.Cancel();
            var state = new RandyTauntPreviewState();
            RandyTauntPreviewIntent intent = state.Select("orton3", "library", alreadyClosed.Token);
            Check(intent != null && intent.Token.IsCancellationRequested
                && !state.IsCurrent(intent, intent.Key, intent.Context),
                "A selection racing with an already closed window cannot become publishable");
            state.Close(); intent.Dispose();
        }
    }

    private static void OldCleanupCannotCancelNewSelection()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent old = state.Select("t62247", "library");
        RandyTauntPreviewIntent current = state.Select("t62248", "library");
        old.Dispose(); old.Dispose();
        Check(!current.Token.IsCancellationRequested && state.IsCurrent(current, current.Key, current.Context),
            "Delayed and repeated disposal of older work cannot cancel or clear the latest request");
        current.Dispose();
        RandyTauntPreviewIntent next = state.Select("t62249", "library");
        Check(next != null && !next.Token.IsCancellationRequested && state.IsCurrent(next, next.Key, next.Context),
            "A completed intent may release its source before a later user selection without breaking cancellation");
        state.Cancel(); next.Dispose(); state.Close();
        Check(next.Token.IsCancellationRequested,
            "Cancel and Close tolerate a previously disposed source during normal producer cleanup");
    }

    private static void InvalidKeys()
    {
        foreach (string key in new[] { null, "", " ", "../t62247", "t62247/other", "t62247\\other", "t62247\n", "taunt two", "discovery_unavailable_62247a006" })
        {
            var state = new RandyTauntPreviewState();
            RandyTauntPreviewIntent previous = state.Select("orton3", "library");
            Check(!RandyTauntPreviewState.ValidKey(key) && state.Select(key, "library") == null
                && !state.IsCurrent(previous, previous.Key, previous.Context),
                "An unusable or path-like taunt key cancels rather than reusing the previous preview: " + (key ?? "<null>"));
            Check(previous.Token.IsCancellationRequested,
                "An unusable key stops the previously queued producer: " + (key ?? "<null>"));
            previous.Dispose(); state.Close();
        }
        foreach (string key in new[] { "orton3", "t62247", "discovery_62247a001", "t62247_discovery_01" })
            Check(RandyTauntPreviewState.ValidKey(key), "Playable bundled and imported keys remain eligible: " + key);
    }

    private static void AtlasResult()
    {
        string relative = Path.Combine("preview fixtures", "Randy.png");
        var cached = new RandyTauntPreviewResult(relative, true);
        Check(Path.IsPathRooted(cached.AtlasPath) && cached.AtlasPath == Path.GetFullPath(relative),
            "The producer result retains one absolute atlas path despite relative input");
        Check(cached.Cached && !new RandyTauntPreviewResult(relative).Cached,
            "The UI can distinguish an atlas cache hit from a newly rendered atlas");
        Check(typeof(RandyTauntPreviewResult).GetProperty("AtlasPath").GetSetMethod() == null
            && typeof(RandyTauntPreviewResult).GetProperty("Cached").GetSetMethod() == null,
            "Callers cannot relabel a completed atlas or mutate its cache origin");
        foreach (string value in new[] { null, "", "   " })
        {
            bool rejected = false;
            try { new RandyTauntPreviewResult(value); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "A producer cannot return a successful result without an image path");
        }
    }

    private static async Task CanceledProducerDrainsBeforeLatestSelection()
    {
        var state = new RandyTauntPreviewState();
        var queue = new AutomaticTauntPreviewQueue(100);
        RandyTauntPreviewIntent first = state.Select("t62247", "library");
        string firstIdentity = RandyTauntPreviewState.Identity(first.Key, first.Context);
        queue.Enqueue(firstIdentity, 0);
        Check(queue.TryBegin(firstIdentity, false, 99) == null,
            "A fast scroll through taunts does not start a producer before debounce completes");
        AutomaticTauntPreviewRequest running = queue.TryBegin(firstIdentity, false, 100);
        Check(running != null && queue.IsRunning, "One debounced taunt starts the shared Randy producer");
        var cancellationObserved = new TaskCompletionSource<bool>();
        var processDrained = new TaskCompletionSource<bool>();
        Task<RandyTauntPreviewResult> producer = ControlledCancelableProducer(first.Token, cancellationObserved, processDrained.Task);
        RandyTauntPreviewIntent latest = state.Select("discovery_62247a001", "library");
        string latestIdentity = RandyTauntPreviewState.Identity(latest.Key, latest.Context);
        queue.Enqueue(latestIdentity, 150);
        await cancellationObserved.Task;
        Check(first.Token.IsCancellationRequested && !latest.Token.IsCancellationRequested,
            "A new selection signals the old process without canceling the queued replacement");
        Check(queue.TryBegin(latestIdentity, false, 1000) == null && queue.IsRunning,
            "A later selected taunt waits instead of starting a second simultaneous Randy render");
        Check(!producer.IsCompleted && first.Token.WaitHandle.WaitOne(0),
            "Cancellation leaves the old source usable until its process-drain task has finished");
        processDrained.SetResult(true);
        bool canceled = false;
        try { await producer; } catch (OperationCanceledException) { canceled = true; }
        Check(canceled && !state.IsCurrent(first, latest.Key, latest.Context),
            "Canceled producer cleanup completes before the queue releases its active slot");
        first.Dispose();
        Check(first.Token.IsCancellationRequested && !latest.Token.IsCancellationRequested,
            "Releasing the drained producer never cancels the queued latest intent");
        queue.Complete(running);
        Check(queue.TryBegin(latestIdentity, true, 1000) == null,
            "Foreground MAKE or a changing library can keep the latest preview queued");
        AutomaticTauntPreviewRequest second = queue.TryBegin(latestIdentity, false, 1000);
        Check(second != null && second.Identity == latestIdentity && state.IsCurrent(latest, latest.Key, latest.Context),
            "After the previous operation drains, only the latest selected taunt starts");
        queue.Complete(running);
        Check(queue.IsRunning, "Duplicate completion of an older native task cannot unlock the current producer");
        queue.Complete(second);
        Check(!queue.IsRunning && !queue.HasWork, "A completed current producer leaves the shared queue idle");
        state.Close(); latest.Dispose();
    }

    private static async Task ClosedWindowRejectsLateSuccess()
    {
        var state = new RandyTauntPreviewState();
        var queue = new AutomaticTauntPreviewQueue(0);
        RandyTauntPreviewIntent intent = state.Select("orton3", "library");
        string identity = RandyTauntPreviewState.Identity(intent.Key, intent.Context);
        queue.Enqueue(identity, 0);
        AutomaticTauntPreviewRequest running = queue.TryBegin(identity, false, 0);
        var producer = new TaskCompletionSource<RandyTauntPreviewResult>();
        state.Close(); queue.Close();
        Check(intent.Token.IsCancellationRequested,
            "Window close asks an in-flight producer to cancel even if it later returns success");
        producer.SetResult(new RandyTauntPreviewResult("Randy-late.png", true));
        await producer.Task;
        Check(!state.IsCurrent(intent, intent.Key, intent.Context),
            "A cached success arriving after close cannot update the old window");
        queue.Complete(running);
        queue.Enqueue(identity, 1);
        Check(!queue.HasWork && queue.TryBegin(identity, false, 1) == null,
            "Closed queues drain their active task and never restart native work");
        intent.Dispose();
    }

    private static async Task<RandyTauntPreviewResult> ControlledCancelableProducer(CancellationToken token,
        TaskCompletionSource<bool> cancellationObserved, Task processDrained)
    {
        using (token.Register(() => cancellationObserved.TrySetResult(true)))
        {
            await processDrained;
            token.ThrowIfCancellationRequested();
            return new RandyTauntPreviewResult("Randy-controlled.png");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++;
        Console.WriteLine("PASS " + message);
    }
}
