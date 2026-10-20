using System;
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
    private static readonly string Game = Path.Combine(Path.GetTempPath(), "two-stage-game");
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "two-stage-data");

    private static async Task<int> Main(string[] args)
    {
        try
        {
            Task<Dictionary<string, object>> handoff = AvailableBeforeBlockedArchive();
            await CanceledArchiveRetainsAvailable();
            await FinalPublicationWaitsForRender();
            await SupersededFinalPublication();
            await EmptyRecoveryDoesNotRebuild();
            await NoMissingSkipsArchive();
            await FailedRemainingStagesRetainAvailable();
            await LateCanceledRecoveryCannotPublish();
            Dictionary<string, object> evidence = await handoff;
            evidence["checks"] = passed;
            if (args.Length > 0) {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
                File.WriteAllText(args[0], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine(JsonSerializer.Serialize(evidence));
            Console.WriteLine("PASS " + passed + " Discovery library refresh stage checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<Dictionary<string, object>> AvailableBeforeBlockedArchive()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        using (var lease = coordinator.Begin(Game, Data))
        {
            var clock = Stopwatch.StartNew();
            var archiveEntered = Signal();
            var order = new List<string>();
            Library visible = null;
            int publications = 0, builds = 0;
            long availableMs = -1;
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => { order.Add("prepare"); return Task.FromResult(Available()); },
                async (library, token) => {
                    await coordinator.WaitForCommitAsync(lease, () => true, token);
                    visible = library; publications++; order.Add("publish-" + library.Revision);
                    if (library.Revision == "available") availableMs = clock.ElapsedMilliseconds;
                }, library => true,
                async (library, token) => {
                    Check(ReferenceEquals(visible, library), "The matching available library is published before opening the archive reader");
                    order.Add("archive"); archiveEntered.SetResult(true);
                    await Task.Delay(8000); // Models an archive reader that cannot be interrupted.
                    return new[] { 17330 };
                }, recovered => recovered.Length > 0,
                (library, recovered, token) => {
                    builds++; order.Add("enrich"); return Task.FromResult(Enriched(library, recovered));
                }, lease.Token);
            await archiveEntered.Task;
            Check(!run.IsCompleted && publications == 1 && visible.Revision == "available",
                "Preview can use the first matching library throughout the blocked archive lookup");
            Check(visible.Clips.SequenceEqual(new[] { 100, 200, 300 }),
                "Bundled, extracted and verified clips remain available while missing clips are searched");
            Check(availableMs >= 0 && availableMs < 5000,
                "The validated available library is published under five seconds without waiting for the eight-second archive");
            Library result = await run;
            clock.Stop();
            Check(ReferenceEquals(result, visible) && visible.Clips.Contains(17330) && publications == 2 && builds == 1,
                "Recovered clips publish one enriched library after the initial usable library");
            Check(order.SequenceEqual(new[] { "prepare", "publish-available", "archive", "enrich", "publish-enriched" }),
                "Refresh stages preserve prepare, initial publication, archive, enrichment and final publication order");
            return new Dictionary<string, object> {
                { "fakeArchiveBlockMs", 8000 }, { "availablePublicationMs", availableMs },
                { "finalPublicationMs", clock.ElapsedMilliseconds }, { "previewAvailableDuringArchive", true },
                { "publications", publications }, { "enrichmentBuilds", builds }
            };
        }
    }

    private static async Task CanceledArchiveRetainsAvailable()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        using (var lease = coordinator.Begin(Game, Data))
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.Token))
        {
            Library visible = null;
            var archiveEntered = Signal();
            int publications = 0, enrichments = 0;
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => Task.FromResult(Available()),
                (library, token) => { visible = library; publications++; return Task.CompletedTask; }, library => true,
                async (library, token) => {
                    archiveEntered.SetResult(true); await Task.Delay(Timeout.Infinite, token); return new[] { 17330 };
                }, recovered => recovered.Length > 0,
                (library, recovered, token) => { enrichments++; return Task.FromResult(Enriched(library, recovered)); }, cancellation.Token);
            await archiveEntered.Task; cancellation.Cancel();
            await Canceled(run, "Canceling archive enrichment cancels only the remaining refresh stages");
            Check(visible != null && visible.Revision == "available" && publications == 1 && enrichments == 0,
                "Archive cancellation retains the first valid matching library without a partial enriched publication");
            Check(coordinator.IsCurrent(lease, Game, Data), "A canceled archive scan leaves the published matching context usable");
        }
    }

    private static async Task FinalPublicationWaitsForRender()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        using (var lease = coordinator.Begin(Game, Data))
        {
            Library visible = null, privateEnriched = null;
            int renderBusy = 0, publications = 0;
            var finalWaiting = Signal();
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => Task.FromResult(Available()),
                async (library, token) => {
                    if (library.Revision == "enriched") finalWaiting.SetResult(true);
                    await coordinator.WaitForCommitAsync(lease, () => Volatile.Read(ref renderBusy) == 0, token);
                    visible = library; publications++;
                }, library => true,
                (library, token) => { Volatile.Write(ref renderBusy, 1); return Task.FromResult(new[] { 17330 }); }, recovered => recovered.Length > 0,
                (library, recovered, token) => { privateEnriched = Enriched(library, recovered); return Task.FromResult(privateEnriched); }, lease.Token);
            await finalWaiting.Task; await Task.Delay(70);
            Check(!run.IsCompleted && publications == 1 && visible.Revision == "available",
                "A render started during archive lookup keeps the first library active until final publication is idle");
            Check(Volatile.Read(ref renderBusy) == 1, "Enrichment does not clear a running render's busy state");
            Check(privateEnriched != null && !ReferenceEquals(privateEnriched, visible)
                && !ReferenceEquals(privateEnriched.Clips, visible.Clips) && visible.Clips.SequenceEqual(new[] { 100, 200, 300 }),
                "Enrichment builds a separate library and leaves active clip data unchanged until final publication");
            Volatile.Write(ref renderBusy, 0);
            Library result = await Before(run);
            Check(publications == 2 && ReferenceEquals(result, visible) && visible.Revision == "enriched",
                "The enriched library publishes after the active render finishes");
        }
    }

    private static async Task SupersededFinalPublication()
    {
        using (var coordinator = new DiscoveryDataLoadCoordinator())
        using (var lease = coordinator.Begin(Game, Data))
        {
            Library visible = null;
            int publications = 0;
            var finalWaiting = Signal();
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => Task.FromResult(Available()),
                async (library, token) => {
                    if (library.Revision == "enriched") finalWaiting.SetResult(true);
                    await coordinator.WaitForCommitAsync(lease, () => library.Revision == "available", token);
                    visible = library; publications++;
                }, library => true, (library, token) => Task.FromResult(new[] { 17330 }), recovered => recovered.Length > 0,
                (library, recovered, token) => Task.FromResult(Enriched(library, recovered)), lease.Token);
            await finalWaiting.Task;
            using (var replacement = coordinator.Begin(Game, Data + "-new")) {
                await Canceled(run, "A new roster supersedes a final publication that is waiting for the renderer");
                Check(publications == 1 && visible.Revision == "available", "Supersession never publishes the old roster's recovered library");
                Check(coordinator.IsCurrent(replacement, Game, Data + "-new"), "The newer roster retains ownership after the old final stage cancels");
            }
        }
    }

    private static async Task EmptyRecoveryDoesNotRebuild()
    {
        int publications = 0, builds = 0, archiveReads = 0;
        Library first = Available();
        Library result = await DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
            token => Task.FromResult(first), (library, token) => { publications++; return Task.CompletedTask; }, library => true,
            (library, token) => { archiveReads++; return Task.FromResult(new int[0]); }, recovered => recovered.Length > 0,
            (library, recovered, token) => { builds++; return Task.FromResult(Enriched(library, recovered)); }, CancellationToken.None);
        Check(archiveReads == 1 && builds == 0 && publications == 1 && ReferenceEquals(result, first),
            "An archive lookup without recovered assets neither rebuilds nor republishes the already available library");
    }

    private static async Task NoMissingSkipsArchive()
    {
        int archiveReads = 0, builds = 0, publications = 0;
        Library result = await DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
            token => Task.FromResult(Available()), (library, token) => { publications++; return Task.CompletedTask; }, library => false,
            (library, token) => { archiveReads++; return Task.FromResult(new[] { 17330 }); }, recovered => true,
            (library, recovered, token) => { builds++; return Task.FromResult(Enriched(library, recovered)); }, CancellationToken.None);
        Check(result.Revision == "available" && publications == 1 && archiveReads == 0 && builds == 0,
            "A complete available library skips archive lookup and enrichment entirely");
    }

    private static async Task FailedRemainingStagesRetainAvailable()
    {
        foreach (string failingStage in new[] { "archive", "enrichment" })
        {
            Library visible = null;
            int publications = 0;
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => Task.FromResult(Available()), (library, token) => { visible = library; publications++; return Task.CompletedTask; }, library => true,
                (library, token) => {
                    if (failingStage == "archive") throw new InvalidDataException("Unreadable archive fixture");
                    return Task.FromResult(new[] { 17330 });
                }, recovered => true,
                (library, recovered, token) => { throw new InvalidDataException("Unreadable recovered clip fixture"); }, CancellationToken.None);
            try { await run; throw new Exception("The failed remaining stage was silently accepted."); }
            catch (InvalidDataException) { }
            Check(publications == 1 && visible != null && visible.Revision == "available" && visible.Clips.SequenceEqual(new[] { 100, 200, 300 }),
                "A failed " + failingStage + " stage retains the first published matching library");
        }
    }

    private static async Task LateCanceledRecoveryCannotPublish()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            Library visible = null;
            int publications = 0, builds = 0;
            var archiveEntered = Signal();
            var archiveResult = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => Task.FromResult(Available()), (library, token) => { visible = library; publications++; return Task.CompletedTask; }, library => true,
                (library, token) => { archiveEntered.SetResult(true); return archiveResult.Task; }, recovered => recovered.Length > 0,
                (library, recovered, token) => { builds++; return Task.FromResult(Enriched(library, recovered)); }, cancellation.Token);
            await archiveEntered.Task; cancellation.Cancel(); archiveResult.SetResult(new[] { 17330 });
            await Canceled(run, "Late recovery from an archive that ignored cancellation cannot continue enrichment");
            Check(publications == 1 && builds == 0 && visible.Revision == "available",
                "Ignoring cancellation in the archive reader cannot replace the matching available library");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            Library visible = null;
            int publications = 0;
            Task<Library> run = DiscoveryLibraryRefreshStages.RunAsync<Library, int[]>(
                token => Task.FromResult(Available()), (library, token) => { visible = library; publications++; return Task.CompletedTask; }, library => true,
                (library, token) => Task.FromResult(new[] { 17330 }), recovered => true,
                (library, recovered, token) => { cancellation.Cancel(); return Task.FromResult(Enriched(library, recovered)); }, cancellation.Token);
            await Canceled(run, "Cancellation while building the private enriched library prevents its publication");
            Check(publications == 1 && visible.Revision == "available", "A canceled enriched build leaves the first publication intact");
        }
    }

    private sealed class Library { internal string Revision; internal int[] Clips; }
    private static Library Available() { return new Library { Revision = "available", Clips = new[] { 100, 200, 300 } }; }
    private static Library Enriched(Library first, int[] recovered) { return new Library { Revision = "enriched", Clips = first.Clips.Concat(recovered).ToArray() }; }
    private static TaskCompletionSource<bool> Signal() { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
    private static async Task<T> Before<T>(Task<T> task) { if (await Task.WhenAny(task, Task.Delay(4000)) != task) throw new TimeoutException(); return await task; }
    private static async Task Canceled(Task task, string message) { try { await task; } catch (OperationCanceledException) { Check(true, message); return; } throw new Exception("Expected cancellation: " + message); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Interlocked.Increment(ref passed); Console.WriteLine("PASS " + message); }
}
