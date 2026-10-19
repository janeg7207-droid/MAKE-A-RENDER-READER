using System;
using System.IO;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static readonly string root = Path.Combine(Path.GetTempPath(), "discovery-refresh-policy");
    private static string PathFor(string name) { return Path.Combine(root, name); }

    private static void Main()
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
        Console.WriteLine("PASS " + passed + " Discovery refresh queue checks");
    }

    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
}
