using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static string root;

    private static async Task<int> Main(string[] args)
    {
        root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-randy-builder-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            PlanValidation();
            await ReusesSavedAndResumesMissing();
            await SerialGenerationWaitsForThePreviousWorker();
            await ErrorsContinueAndStayBounded();
            await CancellationWaitsForCleanup();
            await CanceledProbeDoesNotGenerate();
            await LateCancellationKeepsCommittedCache();
            await ChangedInputContextCancelsOldWork();
            await EarlyCancellationAndInvalidCallbacks();
            await ProgressFailureDoesNotAbandonWork();
            if (args.Length > 0) ActualManifestPlan(args[0]);
            Console.WriteLine("PASS " + checks + " Randy preview builder checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static RandyPreviewBuildItem[] Items(params string[] keys)
    { return keys.Select(key => new RandyPreviewBuildItem(key, "Taunt " + key)).ToArray(); }

    private static void PlanValidation()
    {
        var rows = new List<RandyPreviewBuildItem> {
            null, new RandyPreviewBuildItem("first", "Original name"), new RandyPreviewBuildItem("first", "Repeated name"),
            new RandyPreviewBuildItem("discovery_unavailable_62247a006", "Unsupported Hardy variant"),
            new RandyPreviewBuildItem(null, "Missing key"), new RandyPreviewBuildItem("", "Empty key"),
            new RandyPreviewBuildItem("../escape", "Traversal"), new RandyPreviewBuildItem("bad/key", "Slash"),
            new RandyPreviewBuildItem("bad key", "Space"), new RandyPreviewBuildItem("bad\nkey", "Control"),
            new RandyPreviewBuildItem("second_2", "Next usable motion"), new RandyPreviewBuildItem("FIRST", "Different exact key")
        };
        var plan = RandyPreviewBuilder.Plan(rows);
        Check("Planning filters unavailable, missing, malformed and traversal keys", plan.Select(item => item.Key).SequenceEqual(new[] { "first", "second_2", "FIRST" }));
        Check("Repeated exact keys retain their first readable name", plan[0].Name == "Original name");
        Check("Planning preserves the input collection", rows.Count == 12 && rows[2].Name == "Repeated name");
        rows.Clear();
        Check("The planned list remains stable when the source collection changes", plan.Count == 3 && plan[1].Name == "Next usable motion");
        Check("Whitespace names fall back to the playable key", new RandyPreviewBuildItem("named", " ").Name == "named");
        Throws<ArgumentNullException>("A missing source list is rejected", () => RandyPreviewBuilder.Plan(null));
        Throws<InvalidDataException>("The input-row limit includes skipped invalid rows", () => RandyPreviewBuilder.Plan(Enumerable.Repeat<RandyPreviewBuildItem>(null, 20001)));
        Throws<InvalidDataException>("More than ten thousand unique motions are rejected before generation", () => RandyPreviewBuilder.Plan(Enumerable.Range(0, 10001).Select(index => new RandyPreviewBuildItem("t" + index, null))));
        Check("Ten thousand unique playable motions fit the documented bound", RandyPreviewBuilder.Plan(Enumerable.Range(0, 10000).Select(index => new RandyPreviewBuildItem("t" + index, null))).Count == 10000);
        Check("Duplicate rows at the input boundary do not invent extra jobs", RandyPreviewBuilder.Plan(Enumerable.Repeat(new RandyPreviewBuildItem("one", "One"), 20000)).Count == 1);
    }

    private static async Task ReusesSavedAndResumesMissing()
    {
        string cache = Directory.CreateDirectory(Path.Combine(root, "resume")).FullName;
        File.WriteAllText(Ready(cache, "stock"), "verified Randy atlas marker");
        var firstGenerated = new List<string>();
        var progress = new List<RandyPreviewBuildProgress>();
        var first = await RandyPreviewBuilder.RunAsync(Items("stock", "new1", "new2", "new1"),
            (item, token) => Task.FromResult(File.Exists(Ready(cache, item.Key))),
            (item, token) => { token.ThrowIfCancellationRequested(); firstGenerated.Add(item.Key); File.WriteAllText(Ready(cache, item.Key), "verified " + item.Key); return Task.CompletedTask; },
            progress.Add, CancellationToken.None);
        Check("Cached stock previews skip the producer while missing motions are saved", firstGenerated.SequenceEqual(new[] { "new1", "new2" }));
        Check("The first build counts cached and newly generated previews separately", first.Total == 3 && first.Completed == 3 && first.Cached == 1 && first.Generated == 2 && first.Failed == 0 && !first.Canceled);
        Check("The final progress snapshot describes the completed queue", progress.Last().Finished && progress.Last().Completed == 3 && progress.Last().Generated == 2 && !progress.Last().Canceled);
        RandyPreviewBuildProgress early = progress.First(item => item.Key == "stock");
        Check("Previously delivered progress remains immutable", early.Completed == 0 && early.Cached == 0 && early.Generated == 0);
        string stockContents = File.ReadAllText(Ready(cache, "stock"));
        int generated = 0;
        var cached = await RandyPreviewBuilder.RunAsync(Items("stock", "new1", "new2"),
            (item, token) => Task.FromResult(File.Exists(Ready(cache, item.Key))),
            (item, token) => { generated++; throw new Exception("A complete saved preview must not render again."); }, null, CancellationToken.None);
        Check("An unchanged second build generates nothing", generated == 0 && cached.Cached == 3 && cached.Generated == 0 && cached.Failed == 0);
        Check("Cache-only builds preserve already saved preview bytes", File.ReadAllText(Ready(cache, "stock")) == stockContents);
        File.Delete(Ready(cache, "new2"));
        var resumed = new List<string>();
        var last = await RandyPreviewBuilder.RunAsync(Items("stock", "new1", "new2"),
            (item, token) => Task.FromResult(File.Exists(Ready(cache, item.Key))),
            (item, token) => { resumed.Add(item.Key); File.WriteAllText(Ready(cache, item.Key), "verified " + item.Key); return Task.CompletedTask; }, null, CancellationToken.None);
        Check("Resuming after one missing cache entry rebuilds only that motion", resumed.SequenceEqual(new[] { "new2" }) && last.Cached == 2 && last.Generated == 1);
    }

    private static async Task SerialGenerationWaitsForThePreviousWorker()
    {
        var started = Signal(); var release = Signal();
        var order = new List<string>(); var progress = new List<RandyPreviewBuildProgress>();
        int active = 0, maximum = 0;
        Task<RandyPreviewBuildSummary> run = RandyPreviewBuilder.RunAsync(Items("first", "second", "third"),
            (item, token) => { order.Add("probe:" + item.Key); return Task.FromResult(false); },
            async (item, token) => {
                int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count); order.Add("start:" + item.Key);
                try {
                    if (item.Key == "first") { started.TrySetResult(true); await release.Task; }
                    await Task.Delay(5, token); order.Add("saved:" + item.Key);
                }
                finally { Interlocked.Decrement(ref active); }
            }, progress.Add, CancellationToken.None);
        await Before(started.Task);
        Check("An unfinished worker prevents the next cache probe from starting", order.SequenceEqual(new[] { "probe:first", "start:first" }) && !run.IsCompleted);
        Check("A started but unfinished worker is not counted as generated", progress.All(item => item.Generated == 0 && item.Completed == 0));
        release.TrySetResult(true);
        RandyPreviewBuildSummary result = await Before(run);
        Check("Each generation finishes before the next motion is probed", order.SequenceEqual(new[] {
            "probe:first", "start:first", "saved:first", "probe:second", "start:second", "saved:second", "probe:third", "start:third", "saved:third" }));
        Check("One worker runs at a time throughout the queue", maximum == 1 && active == 0 && result.Generated == 3 && result.Completed == 3);
    }

    private static async Task ErrorsContinueAndStayBounded()
    {
        var generated = new List<string>(); var progress = new List<RandyPreviewBuildProgress>();
        var result = await RandyPreviewBuilder.RunAsync(Items("probe_error", "good1", "render_error", "unrelated_cancel", "stock", "good2"),
            (item, token) => {
                if (item.Key == "probe_error") throw new IOException("Cache index unreadable");
                return Task.FromResult(item.Key == "stock");
            },
            (item, token) => {
                if (item.Key == "render_error") throw new InvalidDataException("Broken renderer\r\n" + new string('x', 6000) + "\nlast line\tend");
                if (item.Key == "unrelated_cancel") throw new OperationCanceledException("One item failed independently of this build's token.");
                generated.Add(item.Key); return Task.CompletedTask;
            }, progress.Add, CancellationToken.None);
        Check("A cache error never falls through into rendering that failed item", !generated.Contains("probe_error"));
        Check("Later motions still generate after cache, renderer and unrelated-cancellation failures", generated.SequenceEqual(new[] { "good1", "good2" }));
        Check("Per-item failures count as processed without canceling the whole build", result.Total == 6 && result.Completed == 6 && result.Cached == 1 && result.Generated == 2 && result.Failed == 3 && !result.Canceled);
        var error = progress.First(item => item.Key == "render_error" && item.Message.StartsWith("Preview failed:", StringComparison.Ordinal));
        Check("Renderer error details are bounded and remain on one line", error.Message.Length <= 500 && !error.Message.Any(char.IsControl));
        Check("Error progress still identifies the failed taunt", error.Name == "Taunt render_error" && error.Failed == 2);
        Check("A queue with failed motions still publishes a finished progress snapshot", progress.Last().Finished && progress.Last().Failed == 3 && progress.Last().Completed == 6);
    }

    private static async Task CancellationWaitsForCleanup()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            var entered = Signal(); var cleaning = Signal(); var releaseCleanup = Signal();
            var probed = new List<string>(); var progress = new List<RandyPreviewBuildProgress>();
            bool cleaned = false;
            Task<RandyPreviewBuildSummary> run = RandyPreviewBuilder.RunAsync(Items("active", "must_not_start"),
                (item, token) => { probed.Add(item.Key); return Task.FromResult(false); },
                async (item, token) => {
                    entered.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cleaning.TrySetResult(true); await releaseCleanup.Task; throw; }
                    finally { cleaned = true; }
                }, progress.Add, cancellation.Token);
            await Before(entered.Task); cancellation.Cancel(); await Before(cleaning.Task);
            Check("Canceling a running preview waits for its producer's cleanup", !run.IsCompleted && !cleaned);
            Check("Cancellation never schedules a later taunt during cleanup", probed.SequenceEqual(new[] { "active" }));
            releaseCleanup.TrySetResult(true);
            var result = await Before(run);
            Check("Cancellation returns only after the active callback has cleaned up", cleaned && result.Canceled);
            Check("An interrupted preview is neither generated nor counted as a failed item", result.Total == 2 && result.Completed == 0 && result.Generated == 0 && result.Failed == 0);
            Check("Final cancellation progress records a stopped build", progress.Last().Finished && progress.Last().Canceled && progress.Last().Completed == 0);
        }
    }

    private static async Task CanceledProbeDoesNotGenerate()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            var entered = Signal(); var release = Signal(); int generated = 0, probes = 0;
            Task<RandyPreviewBuildSummary> run = RandyPreviewBuilder.RunAsync(Items("cached_late", "later"),
                async (item, token) => { probes++; entered.TrySetResult(true); await release.Task; return true; },
                (item, token) => { generated++; return Task.CompletedTask; }, null, cancellation.Token);
            await Before(entered.Task); cancellation.Cancel();
            Check("A canceled cache probe is awaited until its read finishes", !run.IsCompleted);
            release.TrySetResult(true); var result = await Before(run);
            Check("A late cache hit after cancellation is not counted or followed by another job", result.Canceled && result.Cached == 0 && result.Completed == 0 && probes == 1 && generated == 0);
        }
    }

    private static async Task LateCancellationKeepsCommittedCache()
    {
        string cache = Directory.CreateDirectory(Path.Combine(root, "late-cancel")).FullName;
        using (var cancellation = new CancellationTokenSource())
        {
            int generated = 0;
            var result = await RandyPreviewBuilder.RunAsync(Items("committed", "later"),
                (item, token) => Task.FromResult(File.Exists(Ready(cache, item.Key))),
                (item, token) => { generated++; File.WriteAllText(Ready(cache, item.Key), "validated producer result"); cancellation.Cancel(); return Task.CompletedTask; }, null, cancellation.Token);
            Check("Cancellation after a successful cache publication preserves the saved preview", File.Exists(Ready(cache, "committed")) && !File.Exists(Ready(cache, "later")) && generated == 1);
            Check("Late-canceled generation is not reported as a completed active job", result.Canceled && result.Completed == 0 && result.Generated == 0);
        }
        var resumed = new List<string>();
        var finished = await RandyPreviewBuilder.RunAsync(Items("committed", "later"),
            (item, token) => Task.FromResult(File.Exists(Ready(cache, item.Key))),
            (item, token) => { resumed.Add(item.Key); File.WriteAllText(Ready(cache, item.Key), "validated producer result"); return Task.CompletedTask; }, null, CancellationToken.None);
        Check("A restart reuses a cache committed just before cancellation", resumed.SequenceEqual(new[] { "later" }) && finished.Cached == 1 && finished.Generated == 1 && finished.Completed == 2);
    }

    private static async Task ChangedInputContextCancelsOldWork()
    {
        var state = new RandyTauntPreviewState();
        using (RandyTauntPreviewIntent original = state.Select("old_motion", "library-revision-one"))
        {
            var entered = Signal(); var cleaning = Signal(); var release = Signal(); var generated = new List<string>();
            bool cleaned = false;
            Task<RandyPreviewBuildSummary> old = RandyPreviewBuilder.RunAsync(Items("old_motion", "old_later"),
                (item, token) => Task.FromResult(false),
                async (item, token) => {
                    generated.Add(item.Key); entered.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cleaning.TrySetResult(true); await release.Task; throw; }
                    finally { cleaned = true; }
                }, null, original.Token);
            await Before(entered.Task);
            using (RandyTauntPreviewIntent current = state.Select("new_motion", "library-revision-two"))
            {
                await Before(cleaning.Task);
                Check("Changing the library inputs cancels the old builder and preserves the new intent", original.Token.IsCancellationRequested && state.IsCurrent(current, "new_motion", "library-revision-two"));
                Check("Stale-input cancellation waits for cleanup rather than returning early", !old.IsCompleted && !cleaned);
                release.TrySetResult(true); var stopped = await Before(old);
                Check("Old-input cleanup cannot start remaining jobs or cancel the replacement", stopped.Canceled && cleaned && generated.SequenceEqual(new[] { "old_motion" }) && !current.Token.IsCancellationRequested);
                int fresh = 0;
                var result = await RandyPreviewBuilder.RunAsync(Items("new_motion"), (item, token) => Task.FromResult(false),
                    (item, token) => { fresh++; return Task.CompletedTask; }, null, current.Token);
                Check("A new input context can build normally after old cleanup finishes", fresh == 1 && result.Generated == 1 && !result.Canceled);
            }
        }
        state.Close();
    }

    private static async Task EarlyCancellationAndInvalidCallbacks()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); int probes = 0, generated = 0, progress = 0;
            var result = await RandyPreviewBuilder.RunAsync(Items("first", "second"),
                (item, token) => { probes++; return Task.FromResult(false); },
                (item, token) => { generated++; return Task.CompletedTask; }, update => progress++, cancellation.Token);
            Check("A pre-canceled request returns its planned total without starting work", result.Canceled && result.Total == 2 && result.Completed == 0 && probes == 0 && generated == 0);
            Check("A pre-canceled request does not invoke callbacks", progress == 0);
        }
        await ThrowsAsync<ArgumentNullException>("A missing cache probe is rejected", () => RandyPreviewBuilder.RunAsync(Items("one"), null, (item, token) => Task.CompletedTask, null, CancellationToken.None));
        await ThrowsAsync<ArgumentNullException>("A missing producer is rejected", () => RandyPreviewBuilder.RunAsync(Items("one"), (item, token) => Task.FromResult(false), null, null, CancellationToken.None));
        int callbacks = 0;
        await ThrowsAsync<InvalidDataException>("An oversized plan fails before any probe, producer or progress callback", () => RandyPreviewBuilder.RunAsync(
            Enumerable.Repeat(new RandyPreviewBuildItem("same", "same"), 20001),
            (item, token) => { callbacks++; return Task.FromResult(false); },
            (item, token) => { callbacks++; return Task.CompletedTask; }, update => callbacks++, CancellationToken.None));
        Check("Rejected oversized requests have no work or progress side effects", callbacks == 0);
        var empty = await RandyPreviewBuilder.RunAsync(new RandyPreviewBuildItem[] { null, new RandyPreviewBuildItem("discovery_unavailable_missing", "Unavailable") },
            (item, token) => { callbacks++; return Task.FromResult(false); },
            (item, token) => { callbacks++; return Task.CompletedTask; }, null, CancellationToken.None);
        Check("An entirely unavailable list finishes without opening the producer", empty.Total == 0 && empty.Completed == 0 && !empty.Canceled && callbacks == 0);
    }

    private static async Task ProgressFailureDoesNotAbandonWork()
    {
        int generated = 0, reports = 0;
        var result = await RandyPreviewBuilder.RunAsync(Items("first", "second"), (item, token) => Task.FromResult(false),
            (item, token) => { generated++; return Task.CompletedTask; },
            update => { reports++; throw new InvalidOperationException("The preview window was closed."); }, CancellationToken.None);
        Check("A progress-display error never abandons remaining preview work", generated == 2 && result.Generated == 2 && result.Completed == 2 && result.Failed == 0 && reports > 0);
    }

    private static void ActualManifestPlan(string path)
    {
        byte[] before = File.ReadAllBytes(path);
        string checksum = Convert.ToHexString(SHA256.HashData(before));
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        string[] files = Directory.GetFiles(directory).OrderBy(file => file, StringComparer.Ordinal).ToArray();
        List<RandyPreviewBuildItem> rows;
        using (JsonDocument document = JsonDocument.Parse(before))
            rows = document.RootElement.GetProperty("taunts").EnumerateArray().Select(item =>
                new RandyPreviewBuildItem(item.GetProperty("key").GetString(), item.GetProperty("name").GetString())).ToList();
        var plan = RandyPreviewBuilder.Plan(rows);
        Check("The actual supplied manifest contains all 894 original taunts", rows.Count == 894);
        Check("Every original playable key can enter the builder plan once", plan.Count == 894 && plan.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() == 894);
        Check("The real-manifest plan preserves its first and last motion identities", plan[0].Key == rows[0].Key && plan[plan.Count - 1].Key == rows[rows.Count - 1].Key);
        Check("Planning the actual corpus preserves source manifest bytes and timestamp", checksum == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) && stamp == File.GetLastWriteTimeUtc(path));
        Check("The read-only corpus plan creates no neighboring files", files.SequenceEqual(Directory.GetFiles(directory).OrderBy(file => file, StringComparer.Ordinal)));
    }

    private static string Ready(string directory, string key) { return Path.Combine(directory, key + ".ready"); }
    private static TaskCompletionSource<bool> Signal() { return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); }
    private static async Task Before(Task task)
    { if (await Task.WhenAny(task, Task.Delay(5000)) != task) throw new TimeoutException("A builder callback or cleanup did not finish."); await task; }
    private static async Task<T> Before<T>(Task<T> task) { await Before((Task)task); return await task; }
    private static void Check(string name, bool condition)
    { if (!condition) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    private static void Throws<T>(string name, Action action) where T : Exception
    { try { action(); } catch (T) { Check(name, true); return; } throw new Exception(name + ": expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(string name, Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { Check(name, true); return; } throw new Exception(name + ": expected " + typeof(T).Name); }
}
