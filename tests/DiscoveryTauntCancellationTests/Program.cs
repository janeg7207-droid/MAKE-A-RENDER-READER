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
        internal static Action BeforeEnsureJunction;
        internal static string Resolve(string path)
        {
            var directory = new DirectoryInfo(path); FileSystemInfo target = directory.ResolveLinkTarget(true);
            return target == null ? directory.FullName : target.FullName;
        }
        internal static void EnsureJunction(string link, string target)
        {
            Action before = BeforeEnsureJunction; BeforeEnsureJunction = null; if (before != null) before();
            if (Directory.Exists(link)) {
                if (Resolve(link) != Path.GetFullPath(target)) throw new IOException("Unexpected test overlay link.");
                return;
            }
            Directory.CreateSymbolicLink(link, target);
        }
    }
}

internal static class Program
{
    private static int passed;
    private static int Main()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-taunt-cancellation-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var fixture = new Fixture(root);
            var originals = Files(fixture.App).Concat(Files(fixture.Data)).ToDictionary(path => path, Hash);
            DiscoveryTauntLibrary.Preparation prepared = DiscoveryTauntLibrary.Prepare(fixture.App, fixture.Data, CancellationToken.None);
            Check("The focused fixture contains exactly sixty-five current CAM definitions", DiscoveryTaunts.Read(fixture.Table).Count == 65);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                ThrowsCanceled(() => DiscoveryTauntLibrary.BuildPrepared(prepared, fixture.Storage, null, null, canceled.Token));
                Check("Pre-canceled BuildPrepared creates no engine overlay", !Directory.Exists(Path.Combine(fixture.Storage, "taunt-data")));
                ThrowsCanceled(() => DiscoveryTauntLibrary.BuildCurrentPreparation(prepared, fixture.Storage, null, null, canceled.Token));
                Check("Pre-canceled current-preparation build creates no engine overlay", !Directory.Exists(Path.Combine(fixture.Storage, "taunt-data")));
                ThrowsCanceled(() => DiscoveryTauntLibrary.Build(fixture.App, fixture.Storage, fixture.Data, null, canceled.Token));
                Check("Pre-canceled direct Build stops before creating storage", !Directory.Exists(fixture.Storage));
            }

            using (var middle = new CancellationTokenSource())
            {
                bool barrierReached = false;
                ThrowsCanceled(() => DiscoveryTauntLibrary.BuildPrepared(prepared, fixture.Storage, null, message => {
                    if (message.StartsWith("Preparing taunt 33 of 65", StringComparison.Ordinal)) { barrierReached = true; middle.Cancel(); }
                }, middle.Token));
                Check("Cancellation interrupts the real per-definition build at its thirty-third definition", barrierReached);
                Check("Mid-build cancellation publishes no playable manifest", Manifests(fixture.Storage).Length == 0);
                Check("Mid-build cancellation leaves original application and Discovery bytes unchanged", Unchanged(originals));
                Check("Mid-build cancellation cleans temporary atomic-write files", TemporaryFiles(fixture.Storage).Length == 0);
            }
            string incomplete = Overlays(fixture.Storage).Single();
            string incompleteNpz = Path.Combine(incomplete, "data", "gamepack", "decoded", "50000a000.npz");
            Check("Mid-build fixture reaches a real imported motion before cancellation", File.Exists(incompleteNpz));
            File.WriteAllText(incompleteNpz, "interrupted private overlay bytes; never a published library");
            DiscoveryTauntLibrary.Preparation fresh = DiscoveryTauntLibrary.Prepare(fixture.App, fixture.Data, CancellationToken.None);
            DiscoveryTauntImport active;
            using (var valid = new CancellationTokenSource()) active = DiscoveryTauntLibrary.BuildPrepared(fresh, fixture.Storage, null, null, valid.Token);
            Check("A fresh valid preparation publishes all sixty-five exact playable definitions", active.Available == 65 && ManifestCount(active.Library) == 65);
            Check("A fresh build never trusts the canceled attempt's incomplete overlay", active.Root != incomplete &&
                DiscoveryMotionNpz.MatchesPoses(Path.Combine(active.Root, "data", "gamepack", "decoded", "50000a000.npz"), DiscoveryMotionDecoder.Decode(fixture.Raw)));
            Check("The canceled private overlay remains unpublished after a later successful build", !File.Exists(Path.Combine(incomplete, "data", "library", "taunts.json")));
            var activeFiles = Files(active.Root).ToDictionary(path => path, Hash);
            var existingManifests = Manifests(fixture.Storage);

            using (var canceledReuse = new CancellationTokenSource())
            {
                bool reached = false;
                ThrowsCanceled(() => DiscoveryTauntLibrary.BuildPrepared(fresh, fixture.Storage, null, message => {
                    if (message.StartsWith("Preparing taunt 33 of 65", StringComparison.Ordinal)) { reached = true; canceledReuse.Cancel(); }
                }, canceledReuse.Token));
                Check("Reusing an active preparation still honors mid-build cancellation", reached);
                Check("A canceled reused preparation cannot mutate the active manifest or decoded NPZ", Unchanged(activeFiles));
                Check("A canceled reused preparation publishes no second manifest", Manifests(fixture.Storage).SequenceEqual(existingManifests));
            }
            using (var publishing = new CancellationTokenSource())
            {
                bool reached = false;
                ThrowsCanceled(() => DiscoveryTauntLibrary.BuildPrepared(fresh, fixture.Storage, null, message => {
                    if (message == "Publishing taunt library…") { reached = true; publishing.Cancel(); }
                }, publishing.Token));
                Check("Cancellation is observed at the final manifest publication barrier", reached);
                Check("Publication-barrier cancellation exposes no new taunts manifest", Manifests(fixture.Storage).SequenceEqual(existingManifests));
                Check("Publication-barrier cancellation preserves every active overlay file", Unchanged(activeFiles));
                Check("Publication-barrier cancellation leaves no temporary publication files", TemporaryFiles(fixture.Storage).Length == 0);
            }
            using (var current = new CancellationTokenSource())
            {
                bool reached = false;
                ThrowsCanceled(() => DiscoveryTauntLibrary.BuildCurrentPreparation(fresh, fixture.Storage, null, message => {
                    if (message == "Publishing taunt library…") { reached = true; current.Cancel(); }
                }, current.Token));
                Check("The current-preparation API also checks cancellation before publication", reached && Manifests(fixture.Storage).SequenceEqual(existingManifests));
            }
            using (var materializing = new CancellationTokenSource())
            {
                bool reached = false;
                DirectoryLinks.BeforeEnsureJunction = () => { reached = true; materializing.Cancel(); };
                try { ThrowsCanceled(() => DiscoveryTauntLibrary.BuildPrepared(fresh, fixture.Storage, null, null, materializing.Token)); }
                finally { DirectoryLinks.BeforeEnsureJunction = null; }
                Check("Cancellation during actual overlay materialization stops before publication", reached && Manifests(fixture.Storage).SequenceEqual(existingManifests));
                Check("Materialization cancellation leaves source assets and the active overlay unchanged", Unchanged(originals) && Unchanged(activeFiles));
            }
            DiscoveryTauntLibrary.Preparation after = DiscoveryTauntLibrary.Prepare(fixture.App, fixture.Data, CancellationToken.None);
            DiscoveryTauntImport recovered;
            using (var valid = new CancellationTokenSource()) recovered = DiscoveryTauntLibrary.BuildPrepared(after, fixture.Storage, null, null, valid.Token);
            Check("A valid fresh preparation succeeds after every interrupted stage", recovered.Available == 65 && ManifestCount(recovered.Library) == 65);
            Check("The recovered publication has the exact imported motion's poses", DiscoveryMotionNpz.MatchesPoses(
                Path.Combine(recovered.Root, "data", "gamepack", "decoded", "50000a000.npz"), DiscoveryMotionDecoder.Decode(fixture.Raw)));
            Check("Publishing a later library preserves the previously active manifest and NPZ", Unchanged(activeFiles));
            Check("Every original fixture source remains byte-for-byte unchanged", Unchanged(originals));
            Check("All canceled and completed attempts leave no atomic-write temporary files", TemporaryFiles(fixture.Storage).Length == 0);
            Console.WriteLine("PASS " + passed + " Discovery taunt cancellation checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { DirectoryLinks.BeforeEnsureJunction = null; try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private sealed class Fixture
    {
        internal readonly string App, Data, Storage, Table;
        internal readonly byte[] Raw;
        internal Fixture(string root)
        {
            App = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            Data = Directory.CreateDirectory(Path.Combine(root, "discovery_data")).FullName;
            Storage = Path.Combine(root, "private-storage");
            string library = Directory.CreateDirectory(Path.Combine(App, "data", "library")).FullName;
            string decoded = Directory.CreateDirectory(Path.Combine(App, "data", "gamepack", "decoded")).FullName;
            Directory.CreateDirectory(Path.Combine(App, "engine"));
            File.WriteAllText(Path.Combine(App, "engine", "original.py"), "original engine fixture");
            var entries = new List<object>();
            byte[] original = Motion("50001a000");
            for (int index = 1; index < 65; index++) {
                string clip = (50000 + index) + "a000";
                entries.Add(new { key = "t" + (50000 + index), name = "Bundled " + index, clip, cut = new[] { 1, 2 }, seconds = 0.1 });
                DiscoveryMotionNpz.Write(Path.Combine(decoded, clip + ".npz"), DiscoveryMotionDecoder.Decode(original));
            }
            File.WriteAllText(Path.Combine(library, "taunts.json"), JsonSerializer.Serialize(new { taunts = entries }));
            string moves = Directory.CreateDirectory(Path.Combine(Data, "MoveData")).FullName; Table = Path.Combine(moves, "CAMMoveData.jsfb");
            File.WriteAllBytes(Table, CamTable(65));
            Raw = Motion("50000a000"); string animations = Directory.CreateDirectory(Path.Combine(Data, "Animation")).FullName;
            File.WriteAllBytes(Path.Combine(animations, "new.clip"), Raw);
        }
    }
    private static byte[] CamTable(int count)
    {
        int prefix = (28 + count * 4 + 7) & ~7; byte[] data = new byte[prefix + count * 216];
        Put(data, 0, 16U); Encoding.ASCII.GetBytes("CMov").CopyTo(data, 4);
        Short(data, 8, 6); Short(data, 10, 8); Short(data, 12, 4); Put(data, 16, 8U); Put(data, 20, 4U); Put(data, 24, (uint)count);
        for (int index = 0; index < count; index++)
        {
            int at = prefix + index * 216, element = 28 + index * 4; Put(data, element, (uint)(at + 128 - element));
            Short(data, at + 64, 38); Short(data, at + 66, 24);
            Short(data, at + 68, 4); Short(data, at + 70, 8); Short(data, at + 72, 12); Short(data, at + 76, 16); Short(data, at + 100, 20);
            Put(data, at + 128, 64U); Put(data, at + 132, 28U); Put(data, at + 136, (uint)(50000 + index));
            Put(data, at + 140, 0U); Put(data, at + 144, (uint)(90000 + index)); Put(data, at + 148, 60U);
            byte[] name = Encoding.UTF8.GetBytes("Taunt " + index.ToString("000")); Put(data, at + 160, (uint)name.Length); name.CopyTo(data, at + 164);
            Put(data, at + 208, 1U); Put(data, at + 212, 4U);
        }
        return data;
    }
    private static byte[] Motion(string clip)
    {
        byte[] data = new byte[0xA0 + 8 + 48]; Put(data, 0, DiscoveryMotionAssets.ClipId(clip));
        Put(data, 0x20, 1U); Put(data, 0x24, 1U); Put(data, 0x40, 24U); Put(data, 0x44, 3U); Put(data, 0x78, 6U); Put(data, 0x7c, 6U);
        BitConverter.GetBytes(30f).CopyTo(data, 0x0c); Put(data, 0xa0, 12345678901234567890UL); return data;
    }
    private static string[] Files(string root)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0) {
            string folder = pending.Pop(); files.AddRange(Directory.GetFiles(folder));
            foreach (string child in Directory.GetDirectories(folder)) if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
        return files.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }
    private static string[] Manifests(string storage) { return Directory.Exists(storage) ? Files(storage).Where(path => Path.GetFileName(path) == "taunts.json").ToArray() : new string[0]; }
    private static string[] TemporaryFiles(string storage) { return Directory.Exists(storage) ? Files(storage).Where(path => path.EndsWith(".tmp", StringComparison.Ordinal)).ToArray() : new string[0]; }
    private static string[] Overlays(string storage) { return Directory.GetDirectories(Path.Combine(storage, "taunt-data")); }
    private static bool Unchanged(Dictionary<string, string> expected) { return expected.All(pair => File.Exists(pair.Key) && Hash(pair.Key) == pair.Value); }
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(stream)); }
    private static int ManifestCount(string library) { using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(library, "taunts.json")))) return json.RootElement.GetProperty("taunts").GetArrayLength(); }
    private static void Short(byte[] bytes, int at, ushort value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void Put(byte[] bytes, int at, uint value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void Put(byte[] bytes, int at, ulong value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void ThrowsCanceled(Action action) { try { action(); } catch (OperationCanceledException) { return; } throw new Exception("Expected cancellation to propagate."); }
    private static void Check(string label, bool condition) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
}
