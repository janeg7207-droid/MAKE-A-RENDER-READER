using System;
using System.Collections.Generic;
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
            await CacheHitCompletesBeforeGenerationDebounce();
            await AutomaticCacheMissDebouncesOneGeneration();
            await RapidUncachedScrollGeneratesOnlyLatest();
            await CachedSelectionCancelsEarlierGeneration();
            await ForegroundWorkDropsAutomaticGeneration();
            await TransientPreparationBusyPausesAutomaticGeneration();
            await ManualPreviewCanGenerateImmediately();
            LateScheduleCompletionCannotUnlockNewSelection();
            await CanceledProducerDrainsBeforeLatestSelection();
            await ExplicitMakePreemptionWaitsForOwnedPreviewDrain();
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

    private static async Task CacheHitCompletesBeforeGenerationDebounce()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent intent = state.Select("t62247", "library");
        var schedule = new RandyTauntPreviewSchedule(false, 1500);
        var producer = new FakeRandyProducer("t62247");
        var queue = new AutomaticTauntPreviewQueue();
        Check(!schedule.AllowRender && !schedule.Completed,
            "An automatic selection first allows only an immediate cached Randy preview");
        Task<RandyTauntPreviewResult> probe = producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token);
        Check(probe.IsCompleted,
            "An already cached atlas completes its producer call without waiting for the generation timer");
        RandyTauntPreviewResult result = await probe;
        if (result != null && state.IsCurrent(intent, intent.Key, intent.Context)) schedule.Complete();
        Check(result != null && result.Cached && schedule.Completed && producer.RenderedKeys.Count == 0,
            "A cached automatic preview is displayed without starting any model renderer");
        Check(!schedule.QueueGeneration(false) && !queue.HasWork,
            "A cache hit never queues an unnecessary delayed render afterward");
        Check(schedule.SelectedAtMilliseconds == 1500,
            "A completed cache probe retains its original selection time");
        state.Close(); intent.Dispose();
    }

    private static async Task AutomaticCacheMissDebouncesOneGeneration()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent intent = state.Select("discovery_62247a001", "library");
        var schedule = new RandyTauntPreviewSchedule(false, 1000);
        var producer = new FakeRandyProducer();
        var queue = new AutomaticTauntPreviewQueue();
        RandyTauntPreviewResult cacheMiss = await producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token);
        Check(cacheMiss == null && producer.RenderedKeys.Count == 0,
            "An uncached automatic probe returns a miss without launching Randy generation immediately");
        Check(schedule.QueueGeneration(false) && schedule.AllowRender && !schedule.Completed,
            "A playable uncached taunt enables exactly one automatic generation phase");
        Check(schedule.SelectedAtMilliseconds == 1000,
            "The generation phase keeps the initial selection time rather than restarting debounce after cache lookup");
        string identity = RandyTauntPreviewState.Identity(intent.Key, intent.Context);
        queue.Enqueue(identity, schedule.SelectedAtMilliseconds);
        Check(queue.TryBegin(identity, false, 1649) == null,
            "An uncached automatic preview waits for the full 650 millisecond selection debounce");
        AutomaticTauntPreviewRequest request = queue.TryBegin(identity, false, 1650);
        Check(request != null && queue.IsRunning,
            "The latest uncached taunt starts its producer when generation debounce expires");
        Check(!schedule.QueueGeneration(false) && !schedule.Completed,
            "A second scheduling attempt cannot duplicate or prematurely complete an active generation");
        RandyTauntPreviewResult generated = await producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token);
        if (generated != null && state.IsCurrent(intent, intent.Key, intent.Context)) schedule.Complete();
        queue.Complete(request);
        Check(generated != null && !generated.Cached && producer.RenderedKeys.Count == 1
            && producer.RenderedKeys[0] == intent.Key,
            "One cache miss produces one actual generation of its selected taunt");
        Check(schedule.Completed && !schedule.QueueGeneration(false) && !queue.HasWork,
            "A completed automatic render cannot queue itself again");
        var slowLookup = new RandyTauntPreviewSchedule(false, 2000);
        var laterQueue = new AutomaticTauntPreviewQueue();
        Check(slowLookup.QueueGeneration(false), "A slow cache lookup can still request its one generation phase");
        laterQueue.Enqueue(identity, slowLookup.SelectedAtMilliseconds);
        Check(laterQueue.TryBegin(identity, false, 2800) != null,
            "A lookup that finishes after debounce does not impose another 650 millisecond wait");
        laterQueue.Close(); slowLookup.Complete(); state.Close(); intent.Dispose();
    }

    private static async Task RapidUncachedScrollGeneratesOnlyLatest()
    {
        var state = new RandyTauntPreviewState();
        var producer = new FakeRandyProducer();
        var queue = new AutomaticTauntPreviewQueue();
        RandyTauntPreviewIntent earlier = state.Select("discovery_62247a001", "library");
        var earlierSchedule = new RandyTauntPreviewSchedule(false, 0);
        Check(await producer.ProduceAsync(earlier.Key, earlierSchedule.AllowRender, earlier.Token) == null,
            "The first rapidly selected uncached taunt initially performs only a cache probe");
        if (earlierSchedule.QueueGeneration(false)) queue.Enqueue(RandyTauntPreviewState.Identity(earlier.Key, earlier.Context), earlierSchedule.SelectedAtMilliseconds);
        RandyTauntPreviewIntent latest = state.Select("discovery_62247a002", "library");
        var latestSchedule = new RandyTauntPreviewSchedule(false, 100);
        Check(earlier.Token.IsCancellationRequested && !latest.Token.IsCancellationRequested,
            "Scrolling to another uncached taunt cancels the earlier request before its generation phase begins");
        Check(await producer.ProduceAsync(latest.Key, latestSchedule.AllowRender, latest.Token) == null,
            "The latest taunt also checks its own cache without prematurely rendering");
        string latestIdentity = RandyTauntPreviewState.Identity(latest.Key, latest.Context);
        if (latestSchedule.QueueGeneration(false)) queue.Enqueue(latestIdentity, latestSchedule.SelectedAtMilliseconds);
        Check(queue.TryBegin(latestIdentity, false, 650) == null && producer.RenderedKeys.Count == 0,
            "The earlier taunt's debounce deadline does not start the newer selection too early");
        AutomaticTauntPreviewRequest request = queue.TryBegin(latestIdentity, false, 750);
        Check(request != null && request.Identity == latestIdentity,
            "Rapid scrolling leaves only the latest uncached taunt queued for generation");
        RandyTauntPreviewResult result = await producer.ProduceAsync(latest.Key, latestSchedule.AllowRender, latest.Token);
        Check(result != null && producer.RenderedKeys.Count == 1 && producer.RenderedKeys[0] == latest.Key,
            "A rapid uncached scroll generates the final selected taunt and never generates the skipped taunt");
        earlierSchedule.Complete();
        Check(!latestSchedule.Completed && latestSchedule.AllowRender && state.IsCurrent(latest, latest.Key, latest.Context),
            "Cleaning up the skipped selection cannot complete or invalidate the latest generation");
        latestSchedule.Complete(); queue.Complete(request); state.Close(); earlier.Dispose(); latest.Dispose();
    }

    private static async Task CachedSelectionCancelsEarlierGeneration()
    {
        var state = new RandyTauntPreviewState();
        var producer = new FakeRandyProducer("orton3");
        var queue = new AutomaticTauntPreviewQueue();
        RandyTauntPreviewIntent older = state.Select("discovery_62247a001", "library");
        var olderSchedule = new RandyTauntPreviewSchedule(false, 0);
        await producer.ProduceAsync(older.Key, olderSchedule.AllowRender, older.Token);
        if (olderSchedule.QueueGeneration(false)) queue.Enqueue(RandyTauntPreviewState.Identity(older.Key, older.Context), olderSchedule.SelectedAtMilliseconds);
        RandyTauntPreviewIntent latest = state.Select("orton3", "library");
        var latestSchedule = new RandyTauntPreviewSchedule(false, 100);
        queue.CancelPending();
        RandyTauntPreviewResult cached = await producer.ProduceAsync(latest.Key, latestSchedule.AllowRender, latest.Token);
        if (cached != null && state.IsCurrent(latest, latest.Key, latest.Context)) latestSchedule.Complete();
        Check(cached != null && cached.Cached && latestSchedule.Completed,
            "Selecting a cached taunt immediately replaces an earlier uncached generation intent");
        Check(older.Token.IsCancellationRequested && producer.RenderedKeys.Count == 0 && !queue.HasWork,
            "The canceled uncached predecessor does not render later behind the cached preview");
        state.Close(); older.Dispose(); latest.Dispose();
    }

    private static async Task ForegroundWorkDropsAutomaticGeneration()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent intent = state.Select("discovery_62247a001", "library");
        var schedule = new RandyTauntPreviewSchedule(false, 0);
        var producer = new FakeRandyProducer();
        var queue = new AutomaticTauntPreviewQueue();
        Check(await producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token) == null,
            "An automatic cache lookup can report a miss while foreground work owns the renderer");
        Check(!schedule.QueueGeneration(true) && schedule.Completed && !schedule.AllowRender,
            "A foreground-busy cache miss is completed without queuing native generation");
        Check(!schedule.QueueGeneration(false) && producer.RenderedKeys.Count == 0 && !queue.HasWork,
            "Finishing foreground MAKE does not resurrect a previously rejected automatic generation");
        var queued = new RandyTauntPreviewSchedule(false, 1000);
        Check(queued.QueueGeneration(false), "A taunt selected before MAKE can reach its debounced generation phase");
        string identity = RandyTauntPreviewState.Identity(intent.Key, intent.Context);
        queue.Enqueue(identity, queued.SelectedAtMilliseconds);
        Check(queue.TryBegin(identity, true, 1650) == null,
            "Foreground work starting during debounce prevents the queued producer from beginning");
        Check(!queued.QueueGeneration(true) && queued.Completed,
            "The blocked generation is explicitly completed instead of waiting to start unexpectedly after MAKE");
        queue.CancelPending();
        Check(queue.TryBegin(identity, false, 10000) == null && producer.RenderedKeys.Count == 0,
            "No dropped automatic request starts after foreground work has drained");
        state.Close(); intent.Dispose();
    }

    private static async Task ManualPreviewCanGenerateImmediately()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent intent = state.Select("discovery_62247a001", "library");
        var schedule = new RandyTauntPreviewSchedule(true, 900);
        var producer = new FakeRandyProducer();
        Check(schedule.AllowRender && !schedule.Completed && schedule.SelectedAtMilliseconds == 900,
            "A manual Preview request permits its producer to generate immediately after checking cache");
        Check(!schedule.QueueGeneration(false) && !schedule.Completed,
            "Manual Preview never needs a second automatic generation phase");
        RandyTauntPreviewResult result = await producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token);
        schedule.Complete();
        Check(result != null && !result.Cached && producer.RenderedKeys.Count == 1 && schedule.Completed,
            "One explicit Preview click can generate an uncached taunt without the automatic debounce path");
        var cacheSchedule = new RandyTauntPreviewSchedule(true, 1000);
        RandyTauntPreviewResult cached = await producer.ProduceAsync(intent.Key, cacheSchedule.AllowRender, intent.Token);
        cacheSchedule.Complete();
        Check(cached.Cached && producer.RenderedKeys.Count == 1,
            "Manual Preview still reuses an existing Randy atlas instead of rendering again");
        state.Close(); intent.Dispose();
    }

    private static async Task TransientPreparationBusyPausesAutomaticGeneration()
    {
        var state = new RandyTauntPreviewState();
        RandyTauntPreviewIntent intent = state.Select("discovery_62247a001", "library");
        long selectedGeneration = intent.Generation;
        var schedule = new RandyTauntPreviewSchedule(false, 1000);
        var producer = new FakeRandyProducer();
        var queue = new AutomaticTauntPreviewQueue();
        Check(await producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token) == null,
            "A taunt selected during ordinary model preparation can still perform its cache-first lookup");
        Check(schedule.QueueGeneration(false) && schedule.AllowRender && !schedule.Completed,
            "Ordinary preparation does not count as an explicit foreground cancellation of the latest cache miss");
        string identity = RandyTauntPreviewState.Identity(intent.Key, intent.Context);
        queue.Enqueue(identity, schedule.SelectedAtMilliseconds);
        Check(queue.TryBegin(identity, true, 1650) == null && queue.HasWork && !queue.IsRunning,
            "An ordinary busy preparation pauses a ready generation while preserving its pending queue entry");
        Check(queue.TryBegin(identity, true, 2000) == null && !schedule.Completed
            && !intent.Token.IsCancellationRequested && state.IsCurrent(intent, intent.Key, intent.Context)
            && producer.RenderedKeys.Count == 0,
            "Repeated busy polling retains the same live request without starting or discarding its render");
        AutomaticTauntPreviewRequest request = queue.TryBegin(identity, false, 2100);
        Check(request != null && request.Identity == identity && intent.Generation == selectedGeneration,
            "Once preparation becomes idle, the original latest intent starts without another selection");
        RandyTauntPreviewResult generated = await producer.ProduceAsync(intent.Key, schedule.AllowRender, intent.Token);
        schedule.Complete(); queue.Complete(request);
        Check(generated != null && producer.RenderedKeys.Count == 1 && producer.RenderedKeys[0] == intent.Key,
            "A paused automatic cache miss produces exactly one preview when ordinary preparation drains");
        Check(!queue.HasWork && queue.TryBegin(identity, false, 4000) == null && !schedule.QueueGeneration(false),
            "Finishing the paused request leaves no delayed duplicate generation behind");
        state.Close(); intent.Dispose();
    }

    private static void LateScheduleCompletionCannotUnlockNewSelection()
    {
        var state = new RandyTauntPreviewState();
        var queue = new AutomaticTauntPreviewQueue(0);
        RandyTauntPreviewIntent old = state.Select("discovery_62247a001", "library");
        var oldSchedule = new RandyTauntPreviewSchedule(false, 0);
        oldSchedule.QueueGeneration(false);
        string oldIdentity = RandyTauntPreviewState.Identity(old.Key, old.Context);
        queue.Enqueue(oldIdentity, oldSchedule.SelectedAtMilliseconds);
        AutomaticTauntPreviewRequest oldRequest = queue.TryBegin(oldIdentity, false, 0);
        RandyTauntPreviewIntent latest = state.Select("discovery_62247a002", "library");
        var latestSchedule = new RandyTauntPreviewSchedule(false, 1);
        string latestIdentity = RandyTauntPreviewState.Identity(latest.Key, latest.Context);
        queue.Enqueue(latestIdentity, latestSchedule.SelectedAtMilliseconds);
        Check(queue.TryBegin(latestIdentity, false, 1) == null,
            "A newer cache-first selection waits for an older active producer's drainage");
        oldSchedule.Complete(); old.Dispose(); queue.Complete(oldRequest);
        AutomaticTauntPreviewRequest latestProbe = queue.TryBegin(latestIdentity, false, 1);
        Check(latestProbe != null && !latestSchedule.AllowRender && !latestSchedule.Completed,
            "Releasing the old generation starts the latest cache probe without granting generation prematurely");
        oldSchedule.Complete(); queue.Complete(oldRequest);
        Check(queue.IsRunning && !latestSchedule.Completed && state.IsCurrent(latest, latest.Key, latest.Context),
            "Late duplicate completion of older work cannot unlock or complete the newest cache-first operation");
        queue.Complete(latestProbe); latestSchedule.Complete(); state.Close(); latest.Dispose();
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

    private static async Task ExplicitMakePreemptionWaitsForOwnedPreviewDrain()
    {
        var state = new RandyTauntPreviewState();
        var queue = new AutomaticTauntPreviewQueue(0);
        RandyTauntPreviewIntent intent = state.Select("discovery_62247a001", "library");
        var schedule = new RandyTauntPreviewSchedule(false, 0);
        schedule.QueueGeneration(false);
        string identity = RandyTauntPreviewState.Identity(intent.Key, intent.Context);
        queue.Enqueue(identity, 0);
        AutomaticTauntPreviewRequest running = queue.TryBegin(identity, false, 0);
        // Model an additional pending event from this same preempted preview.
        // Explicit MAKE must clear it as well as canceling the active producer.
        queue.Enqueue(identity, 1);
        var cancellationObserved = new TaskCompletionSource<bool>();
        var processDrained = new TaskCompletionSource<bool>();
        var runningDone = new TaskCompletionSource<bool>();
        var selected = new SelectedMakeFixture {
            Wrestler = "Chosen mod wrestler", LogicalId = 4000, Attire = "Default_Attire/Attire.mcd"
        };
        string originalSelection = selected.Snapshot;
        bool ownedBusy = true;
        int makeCalls = 0;
        string observedMakeSelection = null;
        Task producerLifetime = runOwnedPreview();
        Task nativeMake = cancelDrainThenMake();
        await cancellationObserved.Task;
        Check(intent.Token.IsCancellationRequested && !nativeMake.IsCompleted,
            "Explicit MAKE requests cancellation of the active Randy preview before invoking the selected wrestler renderer");
        Check(makeCalls == 0 && ownedBusy && queue.IsRunning,
            "Selected-wrestler MAKE cannot start while the preempted preview process is still draining");
        Check(selected.Snapshot == originalSelection,
            "Preempting a Randy preview retains the selected wrestler, logical slot and attire fixture");
        processDrained.SetResult(true);
        await nativeMake;
        await producerLifetime;
        Check(makeCalls == 1 && !ownedBusy && runningDone.Task.IsCompleted,
            "Native MAKE starts once after the owned process drains and the preview busy lease is restored");
        Check(observedMakeSelection == originalSelection && selected.Snapshot == originalSelection,
            "The released MAKE operation receives the selected wrestler and attire rather than Randy's preview identity");
        Check(!queue.HasWork && queue.TryBegin(identity, false, 10000) == null && !schedule.QueueGeneration(false),
            "The preempted preview's pending generation cannot restart behind the explicit MAKE operation");
        Check(!state.IsCurrent(intent, intent.Key, intent.Context),
            "A late result from the preempted preview remains ineligible after MAKE has started");
        state.Close(); intent.Dispose();

        async Task runOwnedPreview()
        {
            try { await ControlledCancelableProducer(intent.Token, cancellationObserved, processDrained.Task); }
            catch (OperationCanceledException) { }
            finally
            {
                queue.Complete(running);
                ownedBusy = false;
                runningDone.TrySetResult(true);
            }
        }

        async Task cancelDrainThenMake()
        {
            state.Cancel(); queue.CancelPending(); schedule.Complete();
            await runningDone.Task;
            if (!processDrained.Task.IsCompleted || ownedBusy || queue.IsRunning)
                throw new InvalidOperationException("MAKE entered before the owned Randy producer drained.");
            makeCalls++;
            observedMakeSelection = selected.Snapshot;
        }
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

    private sealed class FakeRandyProducer
    {
        private readonly HashSet<string> cached;
        public readonly List<string> RenderedKeys = new List<string>();
        internal FakeRandyProducer(params string[] cachedKeys)
        { cached = new HashSet<string>(cachedKeys, StringComparer.Ordinal); }

        internal Task<RandyTauntPreviewResult> ProduceAsync(string key, bool allowRender, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (cached.Contains(key)) return Task.FromResult(new RandyTauntPreviewResult("Randy-" + key + ".png", true));
            // Null stands for the producer's cache-miss response in this
            // controlled fixture. No model/native process is started here.
            if (!allowRender) return Task.FromResult<RandyTauntPreviewResult>(null);
            RenderedKeys.Add(key); cached.Add(key);
            return Task.FromResult(new RandyTauntPreviewResult("Randy-" + key + ".png"));
        }
    }

    private sealed class SelectedMakeFixture
    {
        public string Wrestler, Attire;
        public int LogicalId;
        public string Snapshot { get { return Wrestler + "|" + LogicalId + "|" + Attire; } }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++;
        Console.WriteLine("PASS " + message);
    }
}
