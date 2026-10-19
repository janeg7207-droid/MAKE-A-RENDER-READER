using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GorillaStudio.NameFolders;

namespace GorillaStudio.NameFolders
{
    internal static class DirectoryLinks
    {
        internal static Action<string> BeforeResolve;
        internal static string Denied;
        internal static string Resolve(string path)
        {
            BeforeResolve?.Invoke(path);
            if (Denied != null && Path.GetFullPath(path).StartsWith(Denied, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Fixture source is inaccessible.");
            string full = Path.GetFullPath(path), current = Path.GetPathRoot(full);
            foreach (string component in full.Substring(current.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                FileSystemInfo info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
                FileSystemInfo target = info.ResolveLinkTarget(true);
                if (target != null) current = target.FullName;
                if (Denied != null && current.StartsWith(Denied, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Fixture target is inaccessible.");
            }
            return current;
        }
    }
}

internal static class Program
{
    private static int passed;
    private static readonly CancellationToken None = CancellationToken.None;

    private static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "motion-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Basic(Path.Combine(root, "basic"));
            Links(Path.Combine(root, "links"));
            CancellationAndChanges(Path.Combine(root, "changes"));
            if (args.Length > 0)
            {
                var clock = Stopwatch.StartNew();
                DiscoveryMotionAssets actual = DiscoveryMotionAssets.Read(args[0], None);
                long read = clock.ElapsedMilliseconds; clock.Restart();
                Check("Uploaded Discovery snapshot remains current", actual.IsCurrent(None));
                Console.WriteLine("Actual records=" + actual.Count + "; read_ms=" + read + "; metadata_audit_ms=" + clock.ElapsedMilliseconds
                    + "; warnings=" + actual.Warnings.Count + "; links=" + actual.HasLinkedSources);
            }
            Console.WriteLine("PASS " + passed + " Discovery motion snapshot checks");
        }
        finally { DirectoryLinks.BeforeResolve = null; DirectoryLinks.Denied = null; Directory.Delete(root, true); }
    }

    private static void Basic(string root)
    {
        Directory.CreateDirectory(root);
        string packed = Path.Combine(root, "motion.clips");
        Container(packed, "62541a000");
        DiscoveryMotionAssets original = DiscoveryMotionAssets.Read(root, None);
        Check("Unchanged container snapshot is current", original.IsCurrent(None));
        Check("Snapshot still indexes the actual container bytes", original.Find("62541a000").Read().SequenceEqual(Motion("62541a000")));
        using (var exclusive = new FileStream(packed, FileMode.Open, FileAccess.Read, FileShare.None))
            Check("Metadata audit does not reopen the animation table or payload", original.IsCurrent(None));
        string irrelevant = Path.Combine(root, "notes.adefs");
        File.WriteAllText(irrelevant, "nonmotion content");
        Check("Nonmotion file additions do not invalidate an animation snapshot", original.IsCurrent(None));
        File.WriteAllText(irrelevant, "changed nonmotion data");
        Check("Nonmotion file writes need no animation table rescan", original.IsCurrent(None));
        string loose = Path.Combine(root, "edited.clip"); File.WriteAllBytes(loose, Motion("62541a000"));
        Check("A new loose override invalidates the old lookup", !original.IsCurrent(None));
        DiscoveryMotionAssets changed = DiscoveryMotionAssets.Read(root, None);
        Check("New loose override keeps its existing precedence", changed.Find("62541a000").Loose);
        File.Delete(loose);
        Check("Removing a loose override invalidates the snapshot", !changed.IsCurrent(None));
        changed = DiscoveryMotionAssets.Read(root, None);
        string child = Path.Combine(root, "NewMods"); Directory.CreateDirectory(child);
        Check("New directories invalidate even before their animation contents are read", !changed.IsCurrent(None));
        changed = DiscoveryMotionAssets.Read(root, None); Directory.Delete(child);
        Check("Removing an indexed directory invalidates the snapshot", !changed.IsCurrent(None));
        changed = DiscoveryMotionAssets.Read(root, None);
        File.SetLastWriteTimeUtc(packed, DateTime.UtcNow.AddMinutes(1));
        Check("Changed animation metadata invalidates the snapshot", !changed.IsCurrent(None));
        changed = DiscoveryMotionAssets.Read(root, None);
        DateTime written = File.GetLastWriteTimeUtc(packed);
        string replacement = Path.Combine(root, "replacement.bin"); Container(replacement, "62541a000");
        File.SetLastWriteTimeUtc(replacement, written); File.Move(replacement, packed, true);
        Check("Replacing a motion file at the same path is detected even with unchanged size and write time", !changed.IsCurrent(None));
        File.WriteAllBytes(Path.Combine(root, "invalid.clips"), new byte[24]);
        changed = DiscoveryMotionAssets.Read(root, None);
        Check("A stable malformed container retains its warning", changed.Warnings.Count == 1 && changed.Count == 1);
        Check("Stable format warnings do not force endless source-change retries", changed.IsCurrent(None));
    }

    private static void Links(string root)
    {
        string data = Path.Combine(root, "data"), external = Path.Combine(root, "external"), other = Path.Combine(root, "other");
        Directory.CreateDirectory(data); Directory.CreateDirectory(external); Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(data, "local.clip"), Motion("62541a000"));
        File.WriteAllBytes(Path.Combine(external, "outside.clip"), Motion("62542a000"));
        File.WriteAllBytes(Path.Combine(other, "outside.clip"), Motion("62543a000"));
        string alias = Path.Combine(data, "Animation"); Directory.CreateSymbolicLink(alias, external);
        Directory.CreateSymbolicLink(Path.Combine(external, "BackToRoot"), data);
        DiscoveryMotionAssets linked = DiscoveryMotionAssets.Read(data, None);
        Check("Cross-linked motion directories remain supported", linked.Count == 2 && linked.Find("62542a000") != null);
        Check("Linked sources are identified for context invalidation", linked.HasLinkedSources);
        Check("A directory cycle is bounded and its unchanged targets stay current", linked.IsCurrent(None));
        File.WriteAllBytes(Path.Combine(external, "new.clip"), Motion("62544a000"));
        Check("New overrides under an external linked target invalidate the snapshot", !linked.IsCurrent(None));
        File.Delete(Path.Combine(external, "new.clip")); linked = DiscoveryMotionAssets.Read(data, None);
        Directory.Delete(alias); Directory.CreateSymbolicLink(alias, other);
        Check("Retargeting a directory link invalidates its original animation lookup", !linked.IsCurrent(None));
        Directory.Delete(alias); Directory.CreateSymbolicLink(alias, external);
        linked = DiscoveryMotionAssets.Read(data, None);
        DirectoryLinks.Denied = external;
        Check("An inaccessible linked directory cannot be claimed current", !linked.IsCurrent(None));
        DirectoryLinks.Denied = null;
        string fileLink = Path.Combine(data, "file-link.clip"), targetA = Path.Combine(root, "A.clip"), targetB = Path.Combine(root, "B.clip");
        File.WriteAllBytes(targetA, Motion("62545a000")); File.WriteAllBytes(targetB, Motion("62545a000"));
        File.SetLastWriteTimeUtc(targetB, File.GetLastWriteTimeUtc(targetA));
        File.CreateSymbolicLink(fileLink, targetA);
        DiscoveryMotionAssets fileLinked = DiscoveryMotionAssets.Read(data, None);
        Check("A loose linked file is read and audited", fileLinked.Find("62545a000").Loose && fileLinked.IsCurrent(None));
        File.Delete(fileLink); File.CreateSymbolicLink(fileLink, targetB);
        Check("Retargeting a file link is detected despite identical target length/write metadata", !fileLinked.IsCurrent(None));
        File.Delete(fileLink); File.CreateSymbolicLink(fileLink, targetA);
        DirectoryLinks.Denied = targetA;
        DiscoveryMotionAssets inaccessible = DiscoveryMotionAssets.Read(data, None);
        Check("Inaccessible sources retain warnings and a non-reusable snapshot", inaccessible.ScanIncomplete && !inaccessible.IsCurrent(None));
        DirectoryLinks.Denied = null;
    }

    private static void CancellationAndChanges(string root)
    {
        Directory.CreateDirectory(root); string clip = Path.Combine(root, "original.clip"); File.WriteAllBytes(clip, Motion("62541a000"));
        Directory.CreateDirectory(Path.Combine(root, "Later"));
        bool changed = false;
        DirectoryLinks.BeforeResolve = path => {
            if (!changed && Path.GetFileName(path) == "Later")
            { changed = true; File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(2)); }
        };
        DiscoveryMotionAssets during = DiscoveryMotionAssets.Read(root, None); DirectoryLinks.BeforeResolve = null;
        Check("A source changed after metadata capture is not accepted as a stable snapshot", changed && !during.IsCurrent(None));
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            ThrowsCanceled("An already canceled scan does no indexing work", () => DiscoveryMotionAssets.Read(root, canceled.Token));
            ThrowsCanceled("An already canceled metadata audit stops immediately", () => during.IsCurrent(canceled.Token));
        }
        using (var cancel = new CancellationTokenSource())
        {
            DirectoryLinks.BeforeResolve = path => { if (Path.GetFileName(path) == "Later") cancel.Cancel(); };
            ThrowsCanceled("Cancellation during directory traversal is observed", () => DiscoveryMotionAssets.Read(root, cancel.Token));
            DirectoryLinks.BeforeResolve = null;
        }
    }

    private static byte[] Motion(string name)
    { var data = new byte[160]; BitConverter.GetBytes(DiscoveryMotionAssets.ClipId(name)).CopyTo(data, 0); return data; }

    private static void Container(string path, string name)
    {
        byte[] motion = Motion(name);
        using (var file = File.Create(path)) using (var writer = new BinaryWriter(file))
        { writer.Write(0UL); writer.Write(0UL); writer.Write(1U); writer.Write(1U); writer.Write(DiscoveryMotionAssets.ClipId(name)); writer.Write(160U); writer.Write(40U); writer.Write(motion); }
    }

    private static void Check(string name, bool value)
    { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }

    private static void ThrowsCanceled(string name, Action action)
    { try { action(); } catch (OperationCanceledException) { Check(name, true); return; } throw new Exception(name); }
}
