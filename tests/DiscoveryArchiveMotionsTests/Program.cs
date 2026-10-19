using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

namespace GorillaStudio.NameFolders
{
    internal static class DirectoryLinks { internal static string Resolve(string path) { return Path.GetFullPath(path); } }
}

internal static class Program
{
    private const string Clip = "62543a000", Missing = "17330a000";
    private static string root, app, game, data, cache;
    private static int checks, indexCalls, extractionCalls;
    private static bool crashOutput, mutate, cancelExtraction, growingOutput, largeOutput;
    private static string mutateOtherArchive, mutationTrigger;
    private static readonly Dictionary<string, string[]> indexes = new Dictionary<string, string[]>();
    private static readonly Dictionary<string, byte[]> payloads = new Dictionary<string, byte[]>();
    private static readonly List<string> extractedPaths = new List<string>();
    private static byte[] actual;

    private static async Task Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-motion-archives-" + Guid.NewGuid().ToString("N"));
        app = Path.Combine(root, "app"); game = Path.Combine(root, "game"); data = Path.Combine(root, "Discovery data"); cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(game); Directory.CreateDirectory(data);
        Write(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"), new byte[] { 1, 2 });
        Write(Path.Combine(game, "oo2core_9_win64.dll"), new byte[] { 3 });
        actual = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", Clip + ".clip"));
        try
        {
            Equal("Real fixture ID is the canonical requested clip", DiscoveryMotionAssets.ClipId(Clip), BitConverter.ToUInt64(actual));
            Equal("Enigma 4 canonical hash matches independently measured native ID", 0x57052adba80753fcUL, DiscoveryMotionAssets.ClipId(Missing));
            foreach (string invalid in new[] { "../bad.clips", "/bad.clip", "C:/bad.clip", "A//B.clip", "A/./B.clip", "A/../B.clip", "A.clip:stream", "A?.clip", "A\n.clip", "A.jsfb" })
                Equal("Unsafe motion path rejected: " + invalid, false, DiscoveryArchiveMotions.TryAssetPath(invalid, out _));
            var result = await Find(Clip);
            Equal("No archives is a normal unresolved result", 0, result.Assets.Count);
            Equal("No-archive issue is actionable", true, result.Issues[Clip].Contains("No game/mod CAK"));
            Equal("No archives launches no process", 0, indexCalls);
            result = await Find(); Equal("No references skips archive work", false, result.ArchivesSearched);
            await Throws<ArgumentException>("Canonical clip validation", () => Find("../17330a000"));
            await Throws<ArgumentException>("Canonical clip cannot end in a newline", () => Find("17330a000\n"));
            await Throws<ArgumentException>("A valid duplicate cannot hide a noncanonical uppercase reference", () => Find("17330a000", "17330A000"));
            await Throws<ArgumentException>("Cache cannot be inside Discovery", () => DiscoveryArchiveMotions.FindAsync(app, game, data, new[] { Clip }, Path.Combine(data, "cache"), null, Run, CancellationToken.None));

            string path = "Animation/Animation/MotionArea_62500_62600.clips";
            string cak = AddArchive("Characters and moves.cak", path, Container(actual));
            string[] before = Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            result = await Find(Clip);
            Equal("Real container record is recovered", 1, result.Assets.Count);
            Equal("Recovered bytes match the actual uploaded clip", true, actual.SequenceEqual(result.Assets[Clip].Read()));
            Equal("Only the requested raw clip is retained", 1, Directory.GetFiles(cache, "*.clip", SearchOption.AllDirectories).Length);
            Equal("No full motion container is retained", 0, Directory.GetFiles(cache, "*.clips", SearchOption.AllDirectories).Length);
            Equal("No implicit game writes", true, before.SequenceEqual(Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x)));
            Equal("No implicit Discovery writes", 0, Directory.GetFiles(data, "*", SearchOption.AllDirectories).Length);
            var decoded = DiscoveryMotionDecoder.Decode(result.Assets[Clip].Read());
            Equal("Recovered real clip decodes all native frames", 211, decoded.Frames);
            Equal("Recovered real clip decodes native body bones", 26, decoded.Ids.Length);
            Equal("Recovered real clip retains timing", 60f, decoded.Fps);
            await Find(Clip); Equal("Stable raw cache avoids repeat native extraction", 1, extractionCalls); Equal("Stable index avoids repeat indexing", 1, indexCalls);
            await Find(Missing); Equal("Cached proven catalog avoids re-extracting absent IDs", 1, extractionCalls);
            File.WriteAllBytes(result.Assets[Clip].Path, new byte[160]); await Find(Clip); Equal("Corrupt record cache is re-extracted", 2, extractionCalls);
            File.AppendAllText(cak, " changed"); await Find(Clip); Equal("CAK fingerprint invalidates record cache", 3, extractionCalls);
            File.AppendAllText(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"), " changed"); await Find(Clip); Equal("Reader fingerprint invalidates records", 4, extractionCalls);

            string second = AddArchive("override.cak", path, Container(actual)); int prior = extractionCalls;
            result = await Find(Clip);
            Equal("Same indexed path in two CAKs remains ambiguous", 0, result.Assets.Count);
            Equal("Unknown load order is explained", true, result.Warnings.Any(x => x.Contains("load order")));
            Equal("Ambiguous paths do not launch extraction", prior, extractionCalls);
            File.Delete(second);

            // Canonical filenames take priority, but never prove their payload ID.
            string canonical = "Custom/NewMoves/" + Missing + ".clip";
            byte[] wrong = (byte[])actual.Clone();
            string custom = AddArchive("Custom moves.cak", canonical, wrong);
            result = await Find(Missing); Equal("A plausible filename cannot invent its record", 0, result.Assets.Count);
            byte[] enigma = (byte[])actual.Clone(); BitConverter.GetBytes(DiscoveryMotionAssets.ClipId(Missing)).CopyTo(enigma, 0);
            payloads[custom + "|" + canonical] = enigma; File.AppendAllText(custom, " updated"); extractedPaths.Clear();
            result = await Find(Missing);
            Equal("A custom loose archive clip with proven ID loads", 1, result.Assets.Count);
            Equal("Canonical path is prioritized", canonical, extractedPaths.First());
            Equal("The record ID, rather than its filename, is returned", DiscoveryMotionAssets.ClipId(Missing), result.Assets[Missing].Id);
            File.Delete(custom);

            // Unknown names and advertised nonmatching ranges must still be searched.
            indexes[cak] = new[] { Line("Custom/AllMyTaunts.clips") };
            payloads[cak + "|Custom/AllMyTaunts.clips"] = Container(enigma); File.AppendAllText(cak, " custom");
            result = await Find(Missing); Equal("Generic mod containers are searched", 1, result.Assets.Count);
            indexes[cak] = new[] { Line("Animation/MotionArea_1_2.clips") };
            payloads[cak + "|Animation/MotionArea_1_2.clips"] = Container(enigma); File.AppendAllText(cak, " range");
            result = await Find(Missing); Equal("A misleading advertised range cannot hide an actual requested record", 1, result.Assets.Count);

            // All relevant sources must be considered even after the requested ID matches.
            indexes[cak] = new[] { Line("Custom/A.clips"), Line("Custom/B.clips") };
            payloads[cak + "|Custom/A.clips"] = Container(actual);
            byte[] different = (byte[])actual.Clone(); different[different.Length - 1] ^= 1;
            payloads[cak + "|Custom/B.clips"] = Container(different); File.AppendAllText(cak, " duplicate");
            result = await Find(Clip);
            Equal("Different bytes for a considered exact ID are not selected", false, result.Assets.ContainsKey(Clip));
            Equal("Different-byte conflict is explicit", true, result.Issues[Clip].Contains("Different motion bytes"));
            payloads[cak + "|Custom/B.clips"] = Container(actual); File.AppendAllText(cak, " identical");
            result = await Find(Clip); Equal("Byte-identical considered aliases remain usable", true, result.Assets.ContainsKey(Clip));

            indexes[cak] = Enumerable.Range(0, 5).Select(i => Line("Custom/Large_" + i + ".clips")).ToArray();
            foreach (int i in Enumerable.Range(0, 5)) payloads[cak + "|Custom/Large_" + i + ".clips"] = Container(actual);
            File.AppendAllText(cak, " large corpus"); largeOutput = true; prior = extractionCalls;
            result = await Find(Clip); largeOutput = false;
            Equal("More than 1 GiB of serial container reads is allowed", prior + 5, extractionCalls);
            Equal("Large corpus retains only verified requested records", 1, result.Assets.Count);
            Equal("Large unrelated payloads do not cause a false incomplete scan", false, result.Incomplete);
            prior = extractionCalls; await Find(Clip); Equal("Large corpus catalogs avoid another cold scan", prior, extractionCalls);

            string[] manyRequests = Enumerable.Range(500000, 455).Select(number => number + "a000").ToArray();
            byte[][] manyRecords = Enumerable.Range(500000, 4096).Select(number =>
            {
                byte[] record = number < 500455 ? (byte[])actual.Clone() : new byte[160];
                BitConverter.GetBytes(DiscoveryMotionAssets.ClipId(number + "a000")).CopyTo(record, 0);
                return record;
            }).ToArray();
            indexes[cak] = new[] { Line("Custom/CompleteMoves.clips") };
            payloads[cak + "|Custom/CompleteMoves.clips"] = Container(manyRecords);
            File.AppendAllText(cak, " many requested records"); prior = extractionCalls;
            result = await Find(manyRequests);
            Equal("A 4,096-record catalog resolves all 455 requested clips", manyRequests.Length, result.Assets.Count);
            Equal("A multi-request container is extracted once", prior + 1, extractionCalls);
            foreach (string requested in manyRequests)
                Equal("The large catalog returns the exact requested payload ID: " + requested,
                    DiscoveryMotionAssets.ClipId(requested), BitConverter.ToUInt64(result.Assets[requested].Read()));
            prior = extractionCalls;
            result = await Find(manyRequests);
            Equal("All 455 requests remain available from their exact cache entries", manyRequests.Length, result.Assets.Count);
            Equal("Repeated large-catalog lookup performs no additional extraction", prior, extractionCalls);
            Equal("Large-catalog cache has no unresolved requested records", 0, result.Issues.Count);

            indexes[cak] = new[] { Line("Custom/Duplicate.clips") };
            payloads[cak + "|Custom/Duplicate.clips"] = Container(actual, different); File.AppendAllText(cak, " duplicate table");
            result = await Find(Clip); Equal("Different payloads claiming one table ID conflict", 0, result.Assets.Count);
            result = await Find(Clip); Equal("Record caching cannot erase a duplicate-table conflict", 0, result.Assets.Count);

            indexes[cak] = new[] { Line("../escape.clips"), Line("Custom/WrongHash.clips", 1), Line("Custom/Valid.clips") };
            payloads[cak + "|Custom/Valid.clips"] = new byte[48]; File.AppendAllText(cak, " malformed");
            prior = extractionCalls; result = await Find(Clip);
            Equal("Only a safe hash-proven path reaches native unpack", prior + 1, extractionCalls);
            Equal("Malformed tables remain unresolved", 0, result.Assets.Count);
            Equal("Malformed or unsafe index makes lookup incomplete", true, result.Incomplete);
            Equal("Incomplete lookup never claims absence", true, result.Issues[Clip].Contains("incomplete"));
            indexes[cak] = new[] { Line("Custom/Valid.clips") };
            byte[] lying = Container(actual); BitConverter.GetBytes(DiscoveryMotionAssets.ClipId(Missing)).CopyTo(lying, 40);
            payloads[cak + "|Custom/Valid.clips"] = lying; File.AppendAllText(cak, " header mismatch");
            result = await Find(Clip); Equal("A table claim cannot override a different actual record-header ID", 0, result.Assets.Count);
            byte[] outside = Container(actual); BitConverter.GetBytes((uint)outside.Length).CopyTo(outside, 36);
            payloads[cak + "|Custom/Valid.clips"] = outside; File.AppendAllText(cak, " invalid offset");
            result = await Find(Clip); Equal("Out-of-bounds payload offsets are rejected before record allocation", 0, result.Assets.Count);
            Equal("Invalid record bounds have a useful warning", true, result.Warnings.Any(x => x.Contains("outside its container")));

            indexes[cak] = new[] { Line(path) }; payloads[cak + "|" + path] = Container(actual);
            File.AppendAllText(cak, " output bug"); crashOutput = true; result = await Find(Clip); crashOutput = false;
            Equal("Safe same-name Bakery index fallback is exercised", 1, result.Assets.Count);
            Equal("Fallback aliases and all extraction scratch are removed", 0, Directory.GetDirectories(cache, "extract-*", SearchOption.AllDirectories).Length);
            File.AppendAllText(cak, " mutation"); mutate = true; result = await Find(Clip); mutate = false;
            Equal("CAK mutation cannot publish motion bytes", 0, result.Assets.Count);
            Equal("Mutation is explained", true, result.Warnings.Any(x => x.Contains("changed during")));

            string negative = AddArchive("Previously negative.cak", "Custom/A_negative.clips", Container(enigma));
            indexes[cak] = new[] { Line("Custom/Z_positive.clips") }; payloads[cak + "|Custom/Z_positive.clips"] = Container(actual);
            File.AppendAllText(cak, " later positive"); mutateOtherArchive = negative; mutationTrigger = cak;
            result = await Find(Clip); mutateOtherArchive = null; mutationTrigger = null;
            Equal("A changed previously negative source can hide a new override and withholds positives", 0, result.Assets.Count);
            Equal("The final source recheck includes negative catalogs", true, result.Warnings.Any(x => x.Contains("changed after its motion scan")));
            File.Delete(negative);

            File.AppendAllText(cak, " oversized advertised"); indexes[cak] = new[] { Line(path, null, 257L * 1024 * 1024) };
            prior = extractionCalls; result = await Find(Clip);
            Equal("Advertised oversized file is skipped before unpack", prior, extractionCalls);
            Equal("Skipped budget is explicit", true, result.Incomplete && result.Warnings.Any(x => x.Contains("budget")));
            indexes[cak] = new[] { Line("Custom/A.clips"), Line("Custom/Z.clips", null, 257L * 1024 * 1024) };
            payloads[cak + "|Custom/A.clips"] = Container(actual); File.AppendAllText(cak, " matched but uncertain");
            result = await Find(Clip);
            Equal("A budget-skipped possible override prevents publishing earlier stock bytes", 0, result.Assets.Count);
            Equal("A matched-but-uncertain issue explains why it was withheld", true, result.Issues[Clip].Contains("active override cannot be ruled out"));
            indexes[cak] = new[] { Line(path) }; File.AppendAllText(cak, " growing"); growingOutput = true;
            result = await Find(Clip); growingOutput = false;
            Equal("Growing output is canceled at the per-file budget", 0, result.Assets.Count);
            Equal("Growing output explains its extraction budget", true, result.Warnings.Any(x => x.Contains("budget")));
            Equal("Budget cancellation cleans scratch", 0, Directory.GetDirectories(cache, "extract-*", SearchOption.AllDirectories).Length);

            File.AppendAllText(cak, " cancel"); cancelExtraction = true;
            using (var canceled = new CancellationTokenSource())
            {
                canceled.CancelAfter(150);
                await Throws<OperationCanceledException>("Cancellation stops native extraction", () => DiscoveryArchiveMotions.FindAsync(app, game, data, new[] { Clip }, cache, null, Run, canceled.Token));
            }
            cancelExtraction = false;
            Equal("Canceled lookup cleans extraction scratch", 0, Directory.GetDirectories(cache, "extract-*", SearchOption.AllDirectories).Length);
            Console.WriteLine("PASS " + checks + " Discovery archive motion checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<DiscoveryMotionFallbacks> Find(params string[] clips) { return DiscoveryArchiveMotions.FindAsync(app, game, data, clips, cache, null, Run, CancellationToken.None); }
    private static async Task<DiscoveryArchiveProcessResult> Run(DiscoveryArchiveCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Equal("Reader runs from cache", true, command.FileName.StartsWith(cache));
        Equal("Native operation has bounded timeout", true, command.TimeoutMilliseconds > 0 && command.TimeoutMilliseconds <= 90000);
        string[] args = command.Arguments;
        Equal("Actual native input flag is retained", "--input", args[1]);
        if (args[0] == "list-files")
        {
            indexCalls++;
            string original = indexes.ContainsKey(args[2]) ? args[2] : indexes.Keys.Single(x => File.Exists(x) && Path.GetFileName(x) == Path.GetFileName(args[2]));
            string output;
            if (args.Length == 6)
            {
                Equal("Exact extended index command", true, args[3] == "--output" && args[5] == "--extended" && args[4].StartsWith(cache));
                if (crashOutput) return new DiscoveryArchiveProcessResult { ExitCode = -1, Output = "Known native output-option failure" };
                output = args[4];
            }
            else
            {
                Equal("Fallback preserves original CAK encryption basename", Path.GetFileName(original), Path.GetFileName(args[2]));
                Equal("Fallback default-output input is inside cache", true, args.Length == 4 && args[2].StartsWith(cache) && args[3] == "--extended");
                output = Path.Combine(Path.GetDirectoryName(args[2]), Path.GetFileNameWithoutExtension(args[2]) + "_files.txt");
            }
            File.WriteAllText(output, "// Bakery extended fixture\n" + string.Join("\n", indexes[original]), Encoding.UTF8);
        }
        else
        {
            extractionCalls++; extractedPaths.Add(args[4]);
            Equal("Exact native indexed-file extraction command", true, args.Length == 7 && args[0] == "unpack-file" && args[3] == "--file" && args[5] == "--output");
            Equal("Extraction output always stays inside cache", true, args[6].StartsWith(cache));
            string output = Path.Combine(args[6], args[4].Replace('/', Path.DirectorySeparatorChar));
            if (growingOutput)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                using (var file = File.Create(output)) file.SetLength(257L * 1024 * 1024);
                await Task.Delay(10000, token);
            }
            if (cancelExtraction) await Task.Delay(10000, token);
            Write(output, payloads[args[2] + "|" + args[4]]);
            if (largeOutput) using (var file = new FileStream(output, FileMode.Open, FileAccess.Write)) file.SetLength(224L * 1024 * 1024);
            if (mutate) File.AppendAllText(args[2], " changed during extraction");
            if (mutateOtherArchive != null && args[2] == mutationTrigger) File.AppendAllText(mutateOtherArchive, " changed after its negative scan");
        }
        return new DiscoveryArchiveProcessResult { ExitCode = 0, Output = "fixture complete" };
    }
    private static string AddArchive(string name, string path, byte[] payload)
    { string cak = Path.Combine(game, "cakes", name); Write(cak, new byte[] { 10, 11 }); indexes[cak] = new[] { Line(path) }; payloads[cak + "|" + path] = payload; return cak; }
    private static string Line(string path, ulong? hash = null, long size = 16)
    { ulong h = hash ?? DiscoveryArchiveMotions.HashPath(path); return h.ToString("X16") + "|" + h + "|" + path + "|Offset:0x0|ZSize:0x" + size.ToString("X") + "|compressed:False"; }
    private static byte[] Container(params byte[][] records)
    {
        using (var file = new MemoryStream()) using (var writer = new BinaryWriter(file))
        {
            writer.Write(0UL); writer.Write(0UL); writer.Write((uint)records.Length); writer.Write((uint)records.Length);
            uint offset = (uint)(24 + 16 * records.Length);
            foreach (byte[] bytes in records) { writer.Write(BitConverter.ToUInt64(bytes)); writer.Write((uint)bytes.Length); writer.Write(offset); offset += (uint)bytes.Length; }
            foreach (byte[] bytes in records) writer.Write(bytes);
            return file.ToArray();
        }
    }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static void Equal<T>(string label, T expected, T actual) { if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual); checks++; }
    private static async Task Throws<T>(string label, Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { checks++; return; } throw new Exception(label + ": expected " + typeof(T).Name); }
}
