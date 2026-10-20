using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using GorillaStudio.NameFolders;

namespace GorillaStudio.NameFolders
{
    internal static class DirectoryLinks
    {
        internal static string Resolve(string path) { var directory = new DirectoryInfo(path); var target = directory.ResolveLinkTarget(true); return target == null ? directory.FullName : target.FullName; }
        internal static void EnsureJunction(string link, string target) { if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, target); }
    }
}

internal static class Program
{
    private static int passed;
    private const string Pending = "Additional animation archive lookup is pending; missing clips may become available when it finishes.";
    private static int Main()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-taunt-stages-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string app = Path.Combine(root, "app"), data = Path.Combine(root, "discovery_data"), storage = Path.Combine(root, "storage");
            string library = Directory.CreateDirectory(Path.Combine(app, "data", "library")).FullName;
            Directory.CreateDirectory(Path.Combine(app, "data", "gamepack", "decoded"));
            File.WriteAllText(Path.Combine(library, "taunts.json"), "{\"taunts\":[]}");
            string moves = Directory.CreateDirectory(Path.Combine(data, "MoveData")).FullName;
            File.WriteAllBytes(Path.Combine(moves, "CAMMoveData.jsfb"), Cam());
            string animation = Directory.CreateDirectory(Path.Combine(data, "Animation")).FullName;
            byte[] raw = Motion(); File.WriteAllBytes(Path.Combine(animation, "current.clip"), raw);
            long revision = 41;
            var prepared = DiscoveryTauntLibrary.Prepare(app, data, CancellationToken.None, () => revision);
            var available = DiscoveryTauntLibrary.BuildPrepared(prepared, storage);
            string[] beforeFork = Snapshot(root);
            var fork = DiscoveryTauntLibrary.ForkPreparation(prepared);
            Check("Forking creates a different preparation and overlay revision", !ReferenceEquals(prepared, fork) && prepared.OverlayRevision != fork.OverlayRevision);
            Check("Forking preserves the same validated manifest and table snapshots", ReferenceEquals(prepared.ManifestStamp, fork.ManifestStamp) && ReferenceEquals(prepared.TableStamp, fork.TableStamp)
                && prepared.ManifestText == fork.ManifestText && prepared.ManifestHash == fork.ManifestHash && prepared.Table == fork.Table);
            Check("Forking reuses the exact motion asset, definition and verified-record snapshots", ReferenceEquals(prepared.Assets, fork.Assets)
                && ReferenceEquals(prepared.Definitions, fork.Definitions) && ReferenceEquals(prepared.Records, fork.Records));
            Check("Forking retains the captured external source revision", fork.CapturedRevision == 41 && ReferenceEquals(prepared.ExternalRevision, fork.ExternalRevision));
            Check("Forking an unchanged preparation remains current without writing files", fork.IsCurrent(CancellationToken.None) && Snapshot(root).SequenceEqual(beforeFork));
            Check("The real available library reports its captured source revision", available.SourceRevision == 41);
            string[] activeBefore = Snapshot(available.Root);
            var completed = DiscoveryTauntLibrary.BuildPrepared(fork, storage);
            Check("A fork builds its final stage into a different engine overlay", completed.Root != available.Root && completed.Library != available.Library);
            Check("Building the fork preserves every active-stage file and modification time", Snapshot(available.Root).SequenceEqual(activeBefore));
            Check("The fork's library retains its exact captured source revision", completed.SourceRevision == available.SourceRevision);
            Check("Both stages contain the same exact imported motion poses", new[] { available, completed }.All(stage =>
                DiscoveryMotionNpz.MatchesPoses(Path.Combine(stage.Root, "data", "gamepack", "decoded", "50000a000.npz"), DiscoveryMotionDecoder.Decode(raw))));
            revision++;
            Check("An external source revision change invalidates both stages' original preparation", !prepared.IsCurrent(CancellationToken.None) && !fork.IsCurrent(CancellationToken.None));
            Check("A fork never recaptures a later source revision silently", fork.CapturedRevision == 41);
            revision = 41;

            available.ArchiveLookupPending = true; available.ScanIncomplete = true;
            available.Definitions = 65; available.Imported = 7; available.Available = 71; available.Missing = 3;
            available.Unsupported = 4; available.ReadFailures = 5; available.CacheFailures = 6;
            available.ArchiveImported = 2; available.VerifiedImported = 8; available.VerifiedPackHash = "verified-source-hash";
            available.Warnings.Add(Pending); available.Warnings.Add("Unrelated source warning"); available.Warnings.Add("Keep this context: " + Pending);
            available.Unavailable.Add(Unavailable("70001a000", DiscoveryTauntUnavailableKind.ScanIncomplete, "Archive lookup is pending."));
            available.Unavailable.Add(Unavailable("70002a000", DiscoveryTauntUnavailableKind.MissingAnimation, "No current source was found."));
            available.Unavailable.Add(Unavailable("70003a000", DiscoveryTauntUnavailableKind.UnsupportedFormat, "Measured decoder format limitation."));
            available.Unavailable.Add(Unavailable("70004a000", DiscoveryTauntUnavailableKind.ReadFailure, "The source cannot be read."));
            available.Unavailable.Add(Unavailable("70005a000", DiscoveryTauntUnavailableKind.CacheFailure, "The cache is unavailable."));
            var fallbacks = new DiscoveryMotionFallbacks { ArchivesSearched = true, ArchiveCount = 9, Status = "Exact archive lookup completed", Incomplete = false };
            const string conflict = "70001a000 has different records in two archives; no load order was guessed.";
            fallbacks.Issues["70001a000"] = conflict; fallbacks.Warnings.Add("Precise archive warning");
            string[] beforeDiagnostics = Snapshot(root);
            string baseState = JsonSerializer.Serialize(available); string fallbackState = JsonSerializer.Serialize(fallbacks);
            string tauntsBefore = JsonSerializer.Serialize(available.Taunts);
            var diagnostic = DiscoveryTauntLibrary.WithArchiveDiagnostics(available, fallbacks, false);
            Check("Archive diagnostics produce a new report while retaining active engine paths", !ReferenceEquals(available, diagnostic) && diagnostic.Root == available.Root && diagnostic.Library == available.Library);
            Check("Archive diagnostics reuse the immutable playable list without rebuilding it", ReferenceEquals(available.Taunts, diagnostic.Taunts) && JsonSerializer.Serialize(available.Taunts) == tauntsBefore);
            Check("Archive diagnostics preserve every playable and unavailable counter", Counts(available) == Counts(diagnostic));
            Check("Archive diagnostics preserve the available stage's captured source revision", diagnostic.SourceRevision == 41);
            Check("A completed lookup clears the pending-only scan state", !diagnostic.ArchiveLookupPending && !diagnostic.ScanIncomplete);
            Check("A completed lookup updates exact archive count, searched state and status", diagnostic.ArchiveCount == 9 && diagnostic.ArchivesSearched && diagnostic.ArchiveStatus == fallbacks.Status);
            Check("Only the exact pending warning is removed", !diagnostic.Warnings.Contains(Pending) && diagnostic.Warnings.Contains("Keep this context: " + Pending));
            Check("Source and archive warnings survive diagnostics", diagnostic.Warnings.Contains("Unrelated source warning") && diagnostic.Warnings.Contains("Precise archive warning"));
            Check("An exact missing clip receives its current archive resolver reason", diagnostic.Unavailable.Single(item => item.Clip == "70001a000").Reason.Contains(conflict));
            Check("Decoder, source-read and cache failures retain their own reasons", new[] { "70003a000", "70004a000", "70005a000" }.All(clip =>
                diagnostic.Unavailable.Single(item => item.Clip == clip).Reason == available.Unavailable.Single(item => item.Clip == clip).Reason));
            Check("Diagnostic enrichment never changes the caller's pending report or fallback collection", JsonSerializer.Serialize(available) == baseState && JsonSerializer.Serialize(fallbacks) == fallbackState);
            Check("Unavailable report rows and alias lists are copied independently", diagnostic.Unavailable.Zip(available.Unavailable).All(pair =>
                !ReferenceEquals(pair.First, pair.Second) && !ReferenceEquals(pair.First.Names, pair.Second.Names)));
            diagnostic.Unavailable[0].Names.Add("Changed alias"); diagnostic.Unavailable[0].Name = "Changed label"; diagnostic.Warnings.Add("Caller mutation");
            Check("Mutating the diagnostics report cannot change the active report's names or warnings", JsonSerializer.Serialize(available) == baseState);
            Check("Archive-only diagnostics perform no filesystem writes or timestamp updates", Snapshot(root).SequenceEqual(beforeDiagnostics));
            var sourceIncomplete = DiscoveryTauntLibrary.WithArchiveDiagnostics(available, fallbacks, true);
            Check("Existing base-source scan incompleteness is retained after archive completion", sourceIncomplete.ScanIncomplete && !sourceIncomplete.ArchiveLookupPending);
            fallbacks.Incomplete = true;
            var archiveIncomplete = DiscoveryTauntLibrary.WithArchiveDiagnostics(available, fallbacks, false);
            Check("An incomplete archive search remains visible independently of pending state", archiveIncomplete.ScanIncomplete && !archiveIncomplete.ArchiveLookupPending);
            Check("Neither incomplete-diagnostics path changes playable counters or source revision", Counts(sourceIncomplete) == Counts(available)
                && Counts(archiveIncomplete) == Counts(available) && archiveIncomplete.SourceRevision == 41);
            Check("All diagnostic variants leave the real active manifest and NPZ unchanged", Snapshot(available.Root).SequenceEqual(activeBefore));
            Console.WriteLine("PASS " + passed + " Discovery taunt stage checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
    private static DiscoveryUnavailableTaunt Unavailable(string clip, DiscoveryTauntUnavailableKind kind, string reason)
    { return new DiscoveryUnavailableTaunt { Id = int.Parse(clip.Substring(0, 5)), Variant = 0, Clip = clip, Name = "Fixture " + clip, Names = new List<string> { "Fixture " + clip, "Alias " + clip }, Kind = kind, Reason = reason }; }
    private static string Counts(DiscoveryTauntImport item) { return string.Join("|", item.Definitions, item.Imported, item.Available, item.Missing, item.Unsupported,
        item.ReadFailures, item.CacheFailures, item.ArchiveImported, item.VerifiedImported, item.VerifiedPackHash); }
    private static string[] Snapshot(string root)
    {
        var paths = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0) {
            string folder = pending.Pop();
            foreach (string file in Directory.GetFiles(folder)) { using (var stream = File.OpenRead(file)) paths.Add(file + "|" + new FileInfo(file).Length + "|" + File.GetLastWriteTimeUtc(file).Ticks + "|" + Convert.ToHexString(SHA256.HashData(stream))); }
            foreach (string child in Directory.GetDirectories(folder)) if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }
    private static byte[] Cam()
    {
        var data = new byte[216]; Put(data, 0, 32U); Encoding.ASCII.GetBytes("CMov").CopyTo(data, 4);
        Short(data, 16, 6); Short(data, 18, 8); Short(data, 20, 4); Put(data, 32, 16U); Put(data, 36, 12U); Put(data, 48, 1U); Put(data, 52, 76U);
        Short(data, 64, 38); Short(data, 66, 24); Short(data, 68, 4); Short(data, 70, 8); Short(data, 72, 12); Short(data, 76, 16); Short(data, 100, 20);
        Put(data, 128, 64U); Put(data, 132, 28U); Put(data, 136, 50000U); Put(data, 144, 12345U); Put(data, 148, 60U);
        byte[] label = Encoding.UTF8.GetBytes("Stage fixture"); Put(data, 160, (uint)label.Length); label.CopyTo(data, 164); Put(data, 208, 1U); Put(data, 212, 4U); return data;
    }
    private static byte[] Motion()
    {
        var data = new byte[0xa0 + 8 + 48]; Put(data, 0, DiscoveryMotionAssets.ClipId("50000a000")); Put(data, 0x20, 1U); Put(data, 0x24, 1U);
        Put(data, 0x40, 24U); Put(data, 0x44, 3U); Put(data, 0x78, 6U); Put(data, 0x7c, 6U); BitConverter.GetBytes(30f).CopyTo(data, 0xc); Put(data, 0xa0, 12345678901234567890UL); return data;
    }
    private static void Put(byte[] bytes, int at, uint value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void Put(byte[] bytes, int at, ulong value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void Short(byte[] bytes, int at, ushort value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void Check(string label, bool condition) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
}
