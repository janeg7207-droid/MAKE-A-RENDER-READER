using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root, library, plugin;

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-gmr-library-" + Guid.NewGuid().ToString("N"));
        library = Path.Combine(root, "game", "Plugins", "GorillaRenders");
        plugin = Path.Combine(root, "game", "Plugins", "WWE2K26_MovingRenders.dlp");
        try
        {
            var absent = Audit();
            Check(absent.Complete && absent.RootFiles == 0 && absent.Notes.Any(note => note.Contains("does not exist")), "Absent library is explained without creating files");
            Directory.CreateDirectory(library); File.WriteAllText(plugin, "other-plugin-version");
            var one = Save("924_PAC.gmr", Json(924));
            var before = Hash(one);
            var report = Audit();
            Check(report.RootFiles == 1 && report.HeaderCompatibleFiles == 1 && report.Complete, "A canonical completed header/metadata file is counted once");
            Check(report.Records[0].WrestlerId == 924 && report.Records[0].NativeIds.SequenceEqual(new[] { 924 }), "Logical ID comes from metadata, not model folder or filename");
            Check(!report.Original421Verified, "A different plugin cannot inherit pinned original-version claims");
            Check(Hash(one) == before, "Inspection preserves all GMR bytes");

            Save("renamed_without_id.GMR", Json(1001));
            Save("not-a-movie.png", Json(999));
            Save(Path.Combine("Alternate attires", "924_second.gmr"), Json(924));
            report = Audit();
            Check(report.RootFiles == 2 && report.NestedFiles == 1 && report.Records.Count == 2, "Native flat-folder scan and case-insensitive GMR extension are distinguished from nested files");
            Check(report.Problems.Any(note => note.Contains("subfolders")), "A saved nested GMR is explicitly reported as outside the native scan");
            Check(report.Records.Any(item => item.WrestlerId == 1001), "A filename without a numeric prefix is not falsely declared unreadable by the native parser audit");
            Reset();

            Save("A_select.gmr", Json(900, "select"));
            Save("B_matchcard.gmr", Json(900, "matchcard"));
            report = Audit();
            Check(report.Records.Count(item => item.ShadowedIds.Contains(900)) == 1, "Duplicate target claims are found before complementary screen filtering");
            Check(report.Problems.Any(note => note.Contains("before checking selection/match-card")), "Duplicate-screen conflict explains the actual native ordering rule");
            Reset();
            Save("A_alias.gmr", Json(1, "both", "[2,2,3,4,5]"));
            Save("B_primary.gmr", Json(2));
            report = Audit(); var alias = report.Records.Single(item => item.WrestlerId == 1);
            Check(alias.NativeIds.SequenceEqual(new[] { 1, 2, 2, 3 }), "Native four-ID capacity includes primary and duplicate alias tokens without invented deduplication");
            Check(alias.Notes.Any(note => note.Contains("2 extra ID(s)")), "Aliases beyond the first three consumed entries are explained");
            Check(report.Records.Single(item => item.WrestlerId == 2).ShadowedIds.Contains(2), "Earlier extra ID can shadow a later primary ID");
            Reset();
            Save("aliases.gmr", Json(100, "both", "[0,65536,101,102,103]"));
            Check(Audit().Records[0].NativeIds.SequenceEqual(new[] { 100, 101, 102, 103 }), "Out-of-range nonnegative aliases do not consume the native valid-ID slots");
            Save("aliases.gmr", Json(100, "both", "[4294967296,101]"));
            Check(Audit().Records[0].HeaderCompatible && Audit().Records[0].NativeIds.SequenceEqual(new[] { 100, 101 }), "A large positive integer alias is ignored instead of misclassified as fractional metadata");
            foreach (string aliases in new[] { "[true]", "[\"101\"]", "[101.5]", "[1e2]", "[-1]", "{}", "null" })
            {
                Save("aliases.gmr", Json(100, "both", aliases));
                Check(!Audit().Records[0].HeaderCompatible, "Noncanonical alias metadata cannot claim native compatibility: " + aliases);
            }
            Save("aliases.gmr", Json(100, "both", "[" + string.Join(",", Enumerable.Range(1000, 65)) + "]"));
            Check(Audit().Records[0].Problems.Any(note => note.Contains("64-entry")), "An oversized native metadata array is reported");
            Reset();

            Save("many.gmr", Json(100));
            for (int n = 0; n < 255; n++) Save(n + ".gmr", Json(n + 1000));
            report = Audit();
            Check(report.RootFiles == 256 && !report.Problems.Any(note => note.Contains("stops after 256")), "Exactly 256 valid root records do not exceed original capacity");
            Save("extra.gmr", Json(9999)); report = Audit();
            Check(report.RootFiles == 257 && report.Problems.Any(note => note.Contains("257") && note.Contains("stops after 256")), "The 257th present root GMR exposes the original record limit");
            Check(report.Notes.Any(note => note.Contains("do not predict which files survive")), "Portable enumeration order does not pretend to identify the file dropped by native capacity");
            Reset();

            string valid = Save("valid.gmr", Json(924)); byte[] bytes = File.ReadAllBytes(valid);
            foreach (var test in new[] {
                Tuple.Create(0, 0x3242494bU, "BIKi"), Tuple.Create(4, 8U, "complete BIKi"),
                Tuple.Create(8, 100001U, "frame count"), Tuple.Create(20, 768U, "1024"),
                Tuple.Create(28, 60U, "30/1"), Tuple.Create(32, 2U, "30/1"),
                Tuple.Create(36, 0U, "alpha"), Tuple.Create(40, 1U, "audio"),
                Tuple.Create(44, 1U, "first video packet"), Tuple.Create(48, 59U, "packet"),
                Tuple.Create(52, 62U, "boundary") })
            {
                byte[] changed = (byte[])bytes.Clone(); Put(changed, test.Item1, test.Item2); File.WriteAllBytes(valid, changed);
                Check(Audit().Records[0].Problems.Any(note => note.IndexOf(test.Item3, StringComparison.OrdinalIgnoreCase) >= 0), "Native incompatibility is explained for header offset " + test.Item1);
            }
            File.WriteAllBytes(valid, bytes); byte[] dependent = (byte[])bytes.Clone(); Put(dependent, 44, 56); File.WriteAllBytes(valid, dependent);
            Check(Audit().Records[0].HeaderCompatible && Audit().Records[0].Notes.Any(note => note.Contains("dependent frames")), "Non-key movie does not falsely become a fully selectable all-key movie");
            File.WriteAllBytes(valid, new byte[] { 1, 2, 3 });
            Check(!Audit().Records[0].HeaderCompatible, "Truncated existing GMR is not mistaken for a playable file");
            Save("valid.gmr", Json(924).Replace("\"wrestler_id\":924", "\"wrestler_id\":9.24e2"));
            Check(!Audit().Records[0].HeaderCompatible, "JSON exponent spelling cannot claim a normalized native wrestler ID");
            Save("valid.gmr", Json(924).Replace("\"name\":\"Fixture\"", "\"name\":\"" + new string('x', 65536) + "\""));
            Check(Audit().Records[0].Problems.Any(note => note.Contains("65,536")), "Managed-readable metadata over64KiB identifies native size incompatibility");
            Reset();

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool stopped = false;
                try { GmrLibraryAudit.Inspect(library, plugin, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Cancellation stops the library audit before enumeration");
                stopped = false;
                try { MatchCardReadiness.Evaluate(null, null, null, null, null, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Closed-dialog cancellation propagates through readiness");
            }
            CancellationFlowChecks().GetAwaiter().GetResult();

            if (args.Length >= 2)
            {
                File.Copy(args[0], Path.Combine(library, "actual-native.gmr")); File.Copy(args[1], plugin, true);
                report = Audit();
                Check(report.Original421Verified, "Exact actual original4.21 plugin SHA activates its measured limits");
                Check(report.Records.Single().HeaderCompatible, "Actual original-native encoded GMR passes header/metadata inspection");
                var actualBefore = Hash(Path.Combine(library, "actual-native.gmr"));
                for (int n = 0; n < 257; n++) Save(n + ".gmr", Json(n + 1000));
                var readiness = MatchCardReadiness.Evaluate(null, Path.Combine(root, "game"), null, null, null);
                Check(readiness.Issues.Any(note => note.Contains("stops after 256")), "Known native library capacity is surfaced in existing readiness Issues");
                Check(Hash(Path.Combine(library, "actual-native.gmr")) == actualBefore, "Readiness keeps actual native movie bytes unchanged");
                string damaged = Path.Combine(root, "damaged-selected.gmr"); File.WriteAllBytes(damaged, new byte[] { 1, 2, 3 });
                readiness = MatchCardReadiness.Evaluate(null, Path.Combine(root, "game"), null, damaged, null);
                Check(readiness.Issues.Any(note => note.Contains("not a complete Gorilla Render")) && readiness.Issues.Any(note => note.Contains("stops after 256")), "A damaged selected movie does not suppress the independent installed-library findings");
                File.WriteAllText(plugin, "newer-unverified-plugin");
                readiness = MatchCardReadiness.Evaluate(null, Path.Combine(root, "game"), null, null, null);
                Check(!readiness.Issues.Any(note => note.Contains("stops after 256")) && readiness.Notes.Any(note => note.Contains("not confirmed limits")), "Unknown plugin versions receive scoped reference findings without false capacity rejection");
            }
            Console.WriteLine(passed + " GMR library audit checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static GmrLibraryAuditReport Audit() { return GmrLibraryAudit.Inspect(library, plugin, CancellationToken.None); }

    private static async Task CancellationFlowChecks()
    {
        Check(await MatchCardReadiness.WaitForCheckAsync(Task.FromResult(42), CancellationToken.None) == 42,
            "A completed original check retains its result");
        using (var closing = new CancellationTokenSource())
        {
            var native = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool busy = true, followOn = false, published = false;
            var waiting = WaitForNative();
            closing.Cancel();
            Check(await Task.WhenAny(waiting, Task.Delay(1000)) == waiting,
                "Closing releases an unresponsive original-check wait promptly");
            try { await waiting; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Check(!busy && !followOn && !published && !native.Task.IsCompleted,
                "Close releases Busy without starting archive follow-on or publishing output");
            native.SetException(new InvalidOperationException("late native error after close"));
            Check(native.Task.IsFaulted && !followOn && !published,
                "A late native fault cannot resume a closed check");

            async Task WaitForNative()
            {
                try
                {
                    await MatchCardReadiness.WaitForCheckAsync(native.Task, closing.Token);
                    closing.Token.ThrowIfCancellationRequested();
                    followOn = true; published = true;
                }
                finally { busy = false; }
            }
        }
        using (var closing = new CancellationTokenSource())
        {
            closing.Cancel(); bool cancelled = false;
            try { await MatchCardReadiness.WaitForCheckAsync(Task.FromResult(42), closing.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancellation wins over a simultaneously completed native result");
        }
    }

    private static string Json(int id, string screens = "both", string aliases = "[]")
    { return "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":" + id + ",\"name\":\"Fixture\",\"screens\":\"" + screens + "\",\"end\":\"loop\",\"extra_ids\":" + aliases + "}"; }
    private static string Save(string name, string metadata)
    {
        string path = Path.Combine(library, name); Directory.CreateDirectory(Path.GetDirectoryName(path)); byte[] json = Encoding.UTF8.GetBytes(metadata);
        using (var output = File.Create(path)) using (var writer = new BinaryWriter(output))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(new byte[] { 1,2,3,4,5,6,7,8 }); writer.Write(json); writer.Write((uint)json.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static void Reset() { Directory.Delete(library, true); Directory.CreateDirectory(library); }
    private static void Put(byte[] bytes, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static string Hash(string path) { using (var s = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(s)); }
    private static void Check(bool ok, string description) { if (!ok) throw new Exception("FAIL " + description); passed++; Console.WriteLine("PASS " + description); }
}
