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
    private static int passed, extracts;
    private static string temporary, app, game, data, cache, original;
    private static readonly Dictionary<string, List<string>> archiveRows = new Dictionary<string, List<string>>();
    private const string Asset = "UI/Projects/CustomShows/AEW_card.jsfb";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: DiscoveryMatchupSetupTests original-app-root actual-discovery-data"); return 2; }
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-matchup-setup-" + Guid.NewGuid().ToString("N"));
        app = Path.GetFullPath(args[0]); game = Path.Combine(temporary, "game"); data = Path.Combine(temporary, "Discovery data"); cache = Path.Combine(temporary, "private-cache");
        original = Path.Combine(app, "data", "gamepack", "screens", "BakeMe", "UI", "Projects", "ShowAssets", "matchups", "matchup_0966_RAW_24.jsfb");
        Directory.CreateDirectory(game); Directory.CreateDirectory(data);
        // The lookup copies this fake executable into its private cache; the runner
        // below tests production commands against real Scre output, never executes it.
        string fakeApp = Path.Combine(temporary, "app");
        CopyDirectory(Path.Combine(app, "data", "gamepack", "screens"), Path.Combine(fakeApp, "data", "gamepack", "screens"));
        Write(Path.Combine(fakeApp, "engine", "bakery", "Bakery.CLI.exe"), new byte[] { 1, 2, 3 });
        Write(Path.Combine(fakeApp, "engine", "bakery", "Libraries", "crn2.dll"), new byte[] { 4, 5 });
        Write(Path.Combine(game, "oo2core_9_win64.dll"), new byte[] { 6, 7 });
        app = fakeApp;
        try
        {
            ulong hash = DiscoveryArchiveMatchups.HashPath(Asset);
            Table(data, new[] { Tuple.Create(1806, hash, Asset) });
            string cak = Archive("AEW_mod.cak", Asset);
            string sourceHash = Hash(original), archiveHash = Hash(cak);
            var before = Snapshot(data);
            var check = await Run(true);
            Check(check.Catalog.RequiredHashes.SetEquals(new[] { hash }) && check.Catalog.Entries.Any(entry => entry.Id == 1806), "Setup rereads the exact unsigned project reference from the current table");
            Check(check.FoundInArchives == 1 && check.Resolution.Assets.ContainsKey(hash) && check.Missing.Count == 0, "Exact indexed custom AEW project is found without guessing its name");
            Check(check.IsCheck && check.AddedScreens == 0 && check.ScreensToAdd == 1 && check.ResolvedScreens == 0, "A cached archive project is reported as needing setup, not already prepared");
            Check(check.BakeRequired && check.Summary.Contains("need copying"), "Read-only inspection explains the required setup and bake");
            Check(Snapshot(data).SequenceEqual(before), "Inspection never writes into Discovery");
            Check(Hash(original) == sourceHash && Hash(cak) == archiveHash, "Inspection preserves original app assets and game archive bytes");
            Check(check.ReportPath != null && check.ReportPath.StartsWith(cache) && File.Exists(check.ReportPath), "The diagnostic report stays in the private cache");
            var setup = await Run(false);
            string output = Path.Combine(data, Asset.Replace('/', Path.DirectorySeparatorChar));
            Check(setup.AddedScreens == 1 && setup.ResolvedScreens == 1 && setup.PreparedPaths[hash] == output, "Setup copies the real project to its exact indexed Discovery path");
            Check(setup.Layouts.Patched == 1 && setup.Layouts.AddedSlots == 52 && setup.Layouts.Failed.Count == 0, "A real partially prepared screen receives its missing twenty-layout coverage");
            Check(setup.Layouts.AddedMovies == 101 && setup.BakeRequired, "A fresh selected dataset receives every layout/player placeholder and needs baking");
            Check(Hash(original) == sourceHash && Hash(cak) == archiveHash, "Setup leaves original screen and archive sources unchanged");
            Check(Directory.GetFiles(data, "*.tmp", SearchOption.AllDirectories).Length == 0, "Copy and layout setup leave no temporary files");
            byte[] custom = File.ReadAllBytes(output).Concat(new byte[] { 0xAB, 0xCD }).ToArray();
            File.WriteAllBytes(output, custom);
            int extractedBefore = extracts;
            var repeated = await Run(false);
            Check(repeated.AddedScreens == 0 && repeated.Layouts.Patched == 0 && repeated.Layouts.Already == 1 && !repeated.BakeRequired, "A repeated setup is idempotent and does not demand another bake");
            Check(File.ReadAllBytes(output).SequenceEqual(custom) && extracts == extractedBefore, "An edited Discovery override remains authoritative over an older archive");

            string next = "UI/Projects/CustomShows/AEW_future.jsfb";
            ulong nextHash = DiscoveryArchiveMatchups.HashPath(next);
            Table(data, new[] { Tuple.Create(1806, hash, Asset), Tuple.Create(1900, nextHash, next) });
            var updated = await Run(true);
            Check(updated.Catalog.RequiredHashes.Count == 2 && updated.Missing.Any(message => message.Contains("1900") && message.Contains(nextHash.ToString("X16"))), "A newly added table record appears on the next check with its exact missing ID and hash");
            Archive("new_arena.cak", next);
            var future = await Run(false);
            Check(future.AddedScreens == 1 && future.PreparedPaths.ContainsKey(nextHash) && future.Missing.Count == 0, "Future dataset updates resolve their added assets on the next setup");

            string hashFolder = Path.Combine(temporary, "hash Discovery"); Directory.CreateDirectory(hashFolder);
            Table(hashFolder, new[] { Tuple.Create(1806, hash, Asset) });
            string hashFile = Path.Combine(hashFolder, "exports", hash.ToString("x16") + ".jsfb"); Copy(original, hashFile);
            var hashResult = await RunFor(hashFolder, false);
            Check(hashResult.PreparedPaths[hash] == hashFile && hashResult.Layouts.Patched == 1 && hashResult.AddedScreens == 0, "An exact hash-named Discovery export is patched where it exists without guessing an install name");
            Check(!File.Exists(Path.Combine(hashFolder, Asset.Replace('/', Path.DirectorySeparatorChar))), "A hash-only export does not create a fictitious named project");

            string conflicting = Path.Combine(temporary, "conflicting Discovery"); Directory.CreateDirectory(conflicting);
            Table(conflicting, new[] { Tuple.Create(1806, hash, Asset) });
            Copy(original, Path.Combine(conflicting, "first", hash.ToString("x16") + ".jsfb"));
            Copy(original, Path.Combine(conflicting, "second", hash.ToString("x16") + ".jsfb"));
            File.AppendAllText(Path.Combine(conflicting, "second", hash.ToString("x16") + ".jsfb"), "different");
            var conflict = await RunFor(conflicting, true);
            Check(conflict.Resolution.Conflicts.Contains(hash) && conflict.Missing.Any(message => message.Contains("conflicting")) && conflict.PreparedPaths.Count == 0, "Conflicting loose exports remain unresolved instead of choosing an arbitrary model/card source");

            string malicious = Path.Combine(temporary, "unsafe Discovery"); Directory.CreateDirectory(malicious);
            Table(malicious, new[] { Tuple.Create(2001, DiscoveryArchiveMatchups.HashPath("../escape.jsfb"), "../escape.jsfb") });
            var unsafeResult = await RunFor(malicious, false);
            Check(unsafeResult.Missing.Count == 1 && !File.Exists(Path.Combine(temporary, "escape.jsfb")), "A malicious path reference is not guessed or written outside Discovery");
            string outside = Path.Combine(temporary, "outside"); Directory.CreateDirectory(outside);
            string linked = Path.Combine(temporary, "linked Discovery"); Directory.CreateSymbolicLink(linked, outside);
            var linkedResult = await RunFor(linked, false);
            Check(linkedResult.Issues.Any(message => message.Contains("linked")) && Directory.GetFiles(outside).Length == 0, "A linked Discovery destination is rejected before mutation");

            var tokenSource = new CancellationTokenSource(); tokenSource.Cancel();
            bool cancelled = false;
            try { await DiscoveryMatchupSetup.RunAsync(app, game, data, cache, false, null, Runner, tokenSource.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "An already cancelled setup aborts before any mutation");
            string cancelData = Path.Combine(temporary, "cancel Discovery"); Directory.CreateDirectory(cancelData);
            Table(cancelData, new[] { Tuple.Create(1806, hash, Asset) });
            var cancelBefore = Snapshot(cancelData); var duringLookup = new CancellationTokenSource(); cancelled = false;
            try
            {
                await DiscoveryMatchupSetup.RunAsync(app, game, cancelData, Path.Combine(temporary, "cancel-cache"), false, null,
                    async (command, token) => { var result = await Runner(command, token); if (command.Arguments[0] == "unpack-file") duringLookup.Cancel(); return result; }, duringLookup.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && Snapshot(cancelData).SequenceEqual(cancelBefore), "Cancellation after archive extraction aborts before the first Discovery copy");
            before = Snapshot(data);
            var badCache = await DiscoveryMatchupSetup.RunAsync(app, game, data, Path.Combine(data, "cache"), false, null, Runner, CancellationToken.None);
            Check(badCache.Issues.Count > 0 && Snapshot(data).SequenceEqual(before), "An in-dataset cache is rejected without writing Discovery files");

            var actualBefore = Snapshot(Path.GetFullPath(args[1]));
            var actual = await DiscoveryMatchupSetup.RunAsync(app, "", Path.GetFullPath(args[1]), Path.Combine(temporary, "actual-cache"), true, null, Runner, CancellationToken.None);
            Check(actual.Catalog.RequiredHashes.Count == 181 && actual.Catalog.Entries.Count(entry => entry.AssetHashes.Count > 0) == 282 && actual.Catalog.Entries.Any(entry => entry.Id == 806), "The supplied real dataset's 282 linked records, 181 distinct project references and AEW record are read");
            Check(actual.AddedScreens == 0 && actual.Missing.Count > 0 && actual.Missing.Any(message => message.Contains("806")), "Actual unavailable screen bytes are reported by record instead of claiming all arenas work");
            Check(Snapshot(Path.GetFullPath(args[1])).SequenceEqual(actualBefore), "Reading the full supplied dataset preserves every original file byte");
            Console.WriteLine(passed + " Discovery matchup setup checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static Task<DiscoveryMatchupSetupReport> Run(bool check) { return RunFor(data, check); }
    private static Task<DiscoveryMatchupSetupReport> RunFor(string folder, bool check)
    { return DiscoveryMatchupSetup.RunAsync(app, game, folder, cache, check, null, Runner, CancellationToken.None); }
    private static Task<DiscoveryArchiveProcessResult> Runner(DiscoveryArchiveCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string operation = command.Arguments[0], input = Argument(command, "--input"), output = Argument(command, "--output");
        if (operation == "list-files")
        {
            var paths = archiveRows[input];
            Write(output, Encoding.UTF8.GetBytes(string.Join("\n", paths.Select(path => DiscoveryArchiveMatchups.HashPath(path).ToString("X16") + "|" + DiscoveryArchiveMatchups.HashPath(path) + "|" + path + "|Offset:0|ZSize:1|compressed:0"))));
        }
        else if (operation == "unpack-file") { Copy(original, Path.Combine(output, Argument(command, "--file").Replace('/', Path.DirectorySeparatorChar))); extracts++; }
        else throw new Exception("Unexpected Bakery command: " + operation);
        return Task.FromResult(new DiscoveryArchiveProcessResult { ExitCode = 0, Output = "ok" });
    }
    private static string Argument(DiscoveryArchiveCommand command, string key) { return command.Arguments[Array.IndexOf(command.Arguments, key) + 1]; }
    private static string Archive(string name, params string[] paths)
    { string path = Path.Combine(game, "cakes", name); Write(path, Encoding.UTF8.GetBytes(name)); archiveRows[path] = paths.ToList(); return path; }

    // A tiny schema fixture, using the measured StCr root field 1 and row's exact
    // uint32 IDs/string references/uint64 field 4. Scre content remains real data.
    private static void Table(string folder, IEnumerable<Tuple<int, ulong, string>> rows)
    {
        var bytes = new List<byte>(new byte[64]);
        Put32(bytes, 0, 32); Set(bytes, 4, Encoding.ASCII.GetBytes("StCr"));
        Put16(bytes, 16, 8); Put16(bytes, 18, 8); Put16(bytes, 22, 4); Put32(bytes, 32, 16); Put32(bytes, 36, 12);
        var list = rows.ToList(); Put32(bytes, 48, (uint)list.Count);
        while (bytes.Count < 52 + 4 * list.Count) bytes.Add(0);
        for (int i = 0; i < list.Count; i++)
        {
            int vtable = bytes.Count; bytes.AddRange(new byte[14]);
            while (bytes.Count % 8 != 0) bytes.Add(0);
            int row = bytes.Count; bytes.AddRange(new byte[32]);
            Put16(bytes, vtable, 14); Put16(bytes, vtable + 2, 32);
            foreach (var pair in new[] { Tuple.Create(0, 4), Tuple.Create(1, 8), Tuple.Create(2, 12), Tuple.Create(4, 24) }) Put16(bytes, vtable + 4 + pair.Item1 * 2, (ushort)pair.Item2);
            Put32(bytes, row, (uint)(row - vtable)); Put32(bytes, row + 4, (uint)list[i].Item1); Put32(bytes, row + 8, (uint)list[i].Item1);
            Put32(bytes, row + 24, (uint)list[i].Item2); Put32(bytes, row + 28, (uint)(list[i].Item2 >> 32));
            byte[] text = Encoding.UTF8.GetBytes(list[i].Item3); int str = bytes.Count; bytes.AddRange(new byte[4]); bytes.AddRange(text); bytes.Add(0);
            Put32(bytes, str, (uint)text.Length); Put32(bytes, row + 12, (uint)(str - row - 12)); Put32(bytes, 52 + 4 * i, (uint)(row - 52 - 4 * i));
        }
        Write(Path.Combine(folder, "CreateShow", "MatchUpScreenAsset.jsfb"), bytes.ToArray());
    }
    private static void Put16(List<byte> bytes, int offset, ushort value) { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); }
    private static void Put32(List<byte> bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
    private static void Set(List<byte> bytes, int offset, byte[] value) { for (int i = 0; i < value.Length; i++) bytes[offset + i] = value[i]; }
    private static string[] Snapshot(string folder) { return Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(path => path).Select(path => path + ":" + Hash(path)).ToArray(); }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return Convert.ToHexString(sha.ComputeHash(stream)); }
    private static void CopyDirectory(string source, string destination) { foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) Copy(file, Path.Combine(destination, file.Substring(source.Length + 1))); }
    private static void Copy(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true); }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); passed++; Console.WriteLine("PASS: " + message); }
}
