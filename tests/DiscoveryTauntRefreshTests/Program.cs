using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static readonly string root = Path.Combine(Path.GetTempPath(), "discovery-refresh-policy");
    private static string PathFor(string name) { return Path.Combine(root, name); }

    private static async Task Main()
    {
        var gate = new DiscoveryTauntRefreshGate();
        Equal("An unused gate does not request background work", false, gate.HasWork);
        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC"));
        Equal("A selected model queues its Discovery context", true, gate.HasWork);
        Equal("A busy renderer delays the refresh", null, gate.TryBegin(PathFor("PAC"), true));
        Equal("Busy work does not discard the pending refresh", true, gate.HasWork);
        DiscoveryTauntRefreshRequest first = gate.TryBegin(PathFor("PAC"), false);
        Equal("The queued refresh starts once model preparation is complete", PathFor("Discovery A"), first.Root);
        Equal("An active refresh cannot be started twice", null, gate.TryBegin(PathFor("PAC"), false));
        gate.Complete(first);
        Equal("A completed model load requests no work on later renders", false, gate.HasWork);

        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC"));
        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC") + Path.DirectorySeparatorChar);
        DiscoveryTauntRefreshRequest coalesced = gate.TryBegin(PathFor("pac"), false);
        Equal("Repeated pending requests coalesce into one refresh", first.Generation + 1, coalesced.Generation);
        Equal("Windows folder casing and trailing separators match safely", PathFor("PAC"), coalesced.Folder);
        gate.Complete(coalesced);

        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC"));
        gate.Enqueue(PathFor("Discovery B"), PathFor("Cole"));
        Equal("An unfinished native load keeps the latest root queued", null, gate.TryBegin(PathFor("PAC"), true));
        DiscoveryTauntRefreshRequest newest = gate.TryBegin(PathFor("Cole"), false);
        Equal("A newer model selection uses its own Discovery context", PathFor("Discovery B"), newest.Root);
        Equal("A newer model selection replaces an older pending model", PathFor("Cole"), newest.Folder);

        gate.Enqueue(PathFor("Discovery C"), PathFor("Roman"));
        gate.Complete(first);
        Equal("A stale completion cannot release another active refresh", null, gate.TryBegin(PathFor("Roman"), false));
        gate.Complete(newest);
        DiscoveryTauntRefreshRequest queued = gate.TryBegin(PathFor("Roman"), false);
        Equal("A selection queued during an import survives its completion", PathFor("Discovery C"), queued.Root);
        gate.Complete(queued);

        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC"));
        Equal("A stale model context cannot refresh a different selected wrestler", null, gate.TryBegin(PathFor("Cole"), false));
        Equal("An abandoned model request releases its background polling", false, gate.HasWork);

        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC"));
        gate.CancelPending();
        Equal("Closing a window cancels its pending import", false, gate.HasWork);
        Equal("A canceled request cannot start after window shutdown", null, gate.TryBegin(PathFor("PAC"), false));

        gate.Enqueue(PathFor("Discovery A"), PathFor("PAC"));
        Equal("A model load with no completed active folder is abandoned safely", null, gate.TryBegin(null, false));
        Equal("No phantom refresh remains after an abandoned folder", false, gate.HasWork);
        await ContextTests();
        Console.WriteLine("PASS " + passed + " Discovery refresh queue checks");
    }

    private static async Task ContextTests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "taunt-context-" + Guid.NewGuid().ToString("N"));
        string data = Directory.CreateDirectory(Path.Combine(directory, "Discovery")).FullName;
        string game = Directory.CreateDirectory(Path.Combine(directory, "Game")).FullName;
        try
        {
            using (var cache = new DiscoveryTauntContextCache(game, data))
            {
                Equal("An unimported context cannot skip the first lookup", false, cache.CanReuse);
                Equal("An unchanged context has the same identity", true, cache.Matches(game, data));
                Equal("A different Discovery root requires a new context", false, cache.Matches(game, directory));
                Equal("A different game root requires a new context", false, cache.Matches(directory, data));
                cache.CompleteRefresh(cache.BeginRefresh());
                Equal("A completed unchanged context avoids another full lookup", true, cache.CanReuse);
                long started = cache.BeginRefresh(); cache.Invalidate(); cache.CompleteRefresh(started);
                Equal("A source change during import cannot be cached as current", false, cache.CanReuse);
                cache.CompleteRefresh(cache.BeginRefresh());
                Equal("A completed retry can be reused", true, cache.CanReuse);
                string clip = Path.Combine(data, "added.clip"); File.WriteAllText(clip, "first motion");
                await Invalidated(cache, "Adding an extracted animation invalidates the library");
                cache.CompleteRefresh(cache.BeginRefresh());
                File.AppendAllText(clip, " changed");
                await Invalidated(cache, "Editing an extracted animation invalidates the library");
                cache.CompleteRefresh(cache.BeginRefresh()); File.Delete(clip);
                await Invalidated(cache, "Removing an animation invalidates the library");
                cache.CompleteRefresh(cache.BeginRefresh()); File.WriteAllText(Path.Combine(game, "patch.cak"), "archive");
                await Invalidated(cache, "A newly installed game archive invalidates the library");
                cache.CompleteRefresh(cache.BeginRefresh()); File.WriteAllText(Path.Combine(data, "CAMMoveData.jsfb"), "moves");
                await Invalidated(cache, "An updated moves table invalidates the library");
                cache.CompleteRefresh(cache.BeginRefresh()); cache.Invalidate();
                Equal("Explicit Refresh data retries even unchanged sources", false, cache.CanReuse);
                cache.CompleteRefresh(cache.BeginRefresh(), false);
                Equal("Linked sources outside the watched trees retain full refreshes", false, cache.CanReuse);
                cache.CompleteRefresh(cache.BeginRefresh()); cache.Dispose();
                Equal("A closed context cannot be reused", false, cache.CanReuse);
            }
            using (var unavailable = new DiscoveryTauntContextCache(null, Path.Combine(directory, "missing")))
            {
                unavailable.CompleteRefresh(unavailable.BeginRefresh());
                Equal("Unavailable source monitoring conservatively retains full refreshes", false, unavailable.CanReuse);
            }
            using (var replaced = new DiscoveryTauntContextCache(game, data))
            {
                replaced.CompleteRefresh(replaced.BeginRefresh());
                Directory.Move(data, data + "-old"); Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "replacement.clip"), "replacement");
                await Invalidated(replaced, "Replacing the data folder at the same path invalidates its old watcher");
                replaced.CompleteRefresh(replaced.BeginRefresh());
                Equal("A watcher attached to a replaced source cannot bless later stale results", false, replaced.CanReuse);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Invalidated(DiscoveryTauntContextCache cache, string message)
    {
        var timer = Stopwatch.StartNew();
        while (cache.CanReuse && timer.ElapsedMilliseconds < 3000) await Task.Delay(10);
        Equal(message, false, cache.CanReuse);
        // Let additional events from this one filesystem mutation drain before
        // establishing the next test's completed revision.
        await Task.Delay(50);
    }

    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
}
