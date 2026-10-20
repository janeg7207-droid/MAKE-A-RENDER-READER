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

            string wrappedData = Path.Combine(temporary, "wrapped source Discovery");
            Table(wrappedData, new[] { Tuple.Create(1806, hash, Asset) });
            string wrappedSource = Path.Combine(wrappedData, "Pack", "BakeMe", Asset.Replace('/', Path.DirectorySeparatorChar));
            string canonicalDestination = Path.Combine(wrappedData, Asset.Replace('/', Path.DirectorySeparatorChar));
            Copy(original, wrappedSource); var wrappedBefore = Snapshot(wrappedData);
            var wrappedCheck = await RunFor(wrappedData, true);
            Check(wrappedCheck.Resolution.Assets.TryGetValue(hash, out var wrappedAsset) && wrappedAsset.IsLoose
                && wrappedAsset.SourcePath == wrappedSource && string.Equals(wrappedAsset.AssetPath, Asset, StringComparison.OrdinalIgnoreCase)
                && wrappedCheck.FoundInArchives == 0 && wrappedCheck.ScreensToAdd == 1 && wrappedCheck.ResolvedScreens == 0,
                "A wrapped loose matchup resolves its exact canonical project path and reports that the canonical copy is still needed");
            Check(Snapshot(wrappedData).SequenceEqual(wrappedBefore) && !File.Exists(canonicalDestination),
                "Checking a wrapped matchup source preserves its bytes and creates no canonical destination");
            var wrappedSetup = await RunFor(wrappedData, false);
            Check(wrappedSetup.AddedScreens == 1 && wrappedSetup.PreparedPaths[hash] == canonicalDestination
                && wrappedSetup.Layouts.Patched == 1 && wrappedSetup.Layouts.AddedSlots == 52 && wrappedSetup.Layouts.Failed.Count == 0,
                "A wrapped matchup is copied to its proven canonical path and only that canonical project receives fifty-two missing slots");
            string canonicalBackup = Path.Combine(wrappedSetup.Layouts.BackupDirectory, Asset.Replace('/', Path.DirectorySeparatorChar) + ".bak");
            Check(Hash(wrappedSource) == sourceHash && File.Exists(canonicalBackup) && Hash(canonicalBackup) == sourceHash
                && !File.Exists(Path.Combine(wrappedSetup.Layouts.BackupDirectory, "Pack", "BakeMe", Asset.Replace('/', Path.DirectorySeparatorChar) + ".bak")),
                "Canonical repair backs up the newly copied original while leaving the wrapped package source untouched");
            string canonicalAfter = Hash(canonicalDestination);
            var wrappedAgain = await RunFor(wrappedData, false);
            Check(wrappedAgain.Resolution.Assets.TryGetValue(hash, out var canonicalAsset) && canonicalAsset.IsLoose
                && canonicalAsset.SourcePath == canonicalDestination && !wrappedAgain.Resolution.Conflicts.Contains(hash)
                && wrappedAgain.PreparedPaths[hash] == canonicalDestination && wrappedAgain.AddedScreens == 0,
                "A repaired canonical override wins over the older wrapped source on repeated setup");
            Check(wrappedAgain.Layouts.Patched == 0 && wrappedAgain.Layouts.AddedSlots == 0 && wrappedAgain.Layouts.AddedMovies == 0
                && wrappedAgain.Layouts.Already == 1 && !wrappedAgain.BakeRequired
                && Hash(canonicalDestination) == canonicalAfter && Hash(wrappedSource) == sourceHash,
                "Repeating wrapped-source setup is idempotent and never patches the original package copy");

            foreach (bool deleteSource in new[] { false, true })
            {
                string changeName = deleteSource ? "deleted" : "modified";
                string staleRoot = Path.Combine(temporary, changeName + " wrapped source Discovery");
                Table(staleRoot, new[] { Tuple.Create(1806, hash, Asset), Tuple.Create(1900, nextHash, next) });
                string staleWrappedSource = Path.Combine(staleRoot, "Pack", "BakeMe", Asset.Replace('/', Path.DirectorySeparatorChar));
                string staleDestination = Path.Combine(staleRoot, Asset.Replace('/', Path.DirectorySeparatorChar));
                Copy(original, staleWrappedSource);
                byte[] modifiedSource = File.ReadAllBytes(original).Concat(new byte[] { 0xAB, 0xCD }).ToArray();
                bool sourceChangedAfterCaching = false;
                var staleSetup = await DiscoveryMatchupSetup.RunAsync(app, game, staleRoot, Path.Combine(temporary, changeName + "-source-cache"), false, null,
                    async (command, token) => {
                        var execution = await Runner(command, token);
                        if (!sourceChangedAfterCaching && command.Arguments[0] == "list-files")
                        {
                            if (deleteSource) File.Delete(staleWrappedSource); else File.WriteAllBytes(staleWrappedSource, modifiedSource);
                            sourceChangedAfterCaching = true;
                        }
                        return execution;
                    }, CancellationToken.None);
                string staleReference = hash.ToString("X16");
                bool staleRejected = sourceChangedAfterCaching && !staleSetup.PreparedPaths.ContainsKey(hash) && !File.Exists(staleDestination)
                    && staleSetup.Missing.Any(message => message.Contains(staleReference))
                    && staleSetup.Issues.Concat(staleSetup.Warnings).Any(message => message.Contains(staleReference));
                if (!staleRejected) Console.Error.WriteLine("Stale wrapped-source diagnostics: " + System.Text.Json.JsonSerializer.Serialize(new {
                    SourceChangedAfterCaching = sourceChangedAfterCaching, Prepared = staleSetup.PreparedPaths.ContainsKey(hash),
                    CanonicalExists = File.Exists(staleDestination), staleSetup.Missing, staleSetup.Issues, staleSetup.Warnings
                }));
                Check(staleRejected,
                    "A " + changeName + " wrapped source is rejected after caching instead of copying stale project bytes");
                Check((deleteSource ? !File.Exists(staleWrappedSource) : File.ReadAllBytes(staleWrappedSource).SequenceEqual(modifiedSource))
                    && Directory.GetFiles(staleRoot, "*.tmp", SearchOption.AllDirectories).Length == 0,
                    "Rejecting a " + changeName + " wrapped source preserves its current state and leaves no temporary files");
            }

            string conflictingWrapped = Path.Combine(temporary, "conflicting wrapped Discovery");
            Table(conflictingWrapped, new[] { Tuple.Create(1806, hash, Asset) });
            string firstWrapped = Path.Combine(conflictingWrapped, "FirstPack", "BakeMe", Asset.Replace('/', Path.DirectorySeparatorChar));
            string secondWrapped = Path.Combine(conflictingWrapped, "UI", "Projects", "Exports", "SecondPack", Asset.Replace('/', Path.DirectorySeparatorChar));
            Copy(original, firstWrapped); Copy(original, secondWrapped);
            File.WriteAllBytes(secondWrapped, File.ReadAllBytes(secondWrapped).Concat(new byte[] { 0xAB }).ToArray());
            var conflictingWrappedBefore = Snapshot(conflictingWrapped);
            var conflictingWrappedSetup = await RunFor(conflictingWrapped, false);
            Check(conflictingWrappedSetup.Resolution.Conflicts.Contains(hash) && conflictingWrappedSetup.Missing.Any(message => message.Contains("conflicting"))
                && conflictingWrappedSetup.PreparedPaths.Count == 0 && conflictingWrappedSetup.AddedScreens == 0
                && !File.Exists(Path.Combine(conflictingWrapped, Asset.Replace('/', Path.DirectorySeparatorChar))),
                "Different wrapped exports for one matchup remain unresolved without creating a guessed canonical copy");
            Check(conflictingWrappedSetup.Layouts.Patched == 0 && conflictingWrappedSetup.Layouts.AddedSlots == 0
                && conflictingWrappedSetup.Layouts.AddedMovies == 0 && Snapshot(conflictingWrapped).SequenceEqual(conflictingWrappedBefore)
                && !Directory.Exists(Path.Combine(conflictingWrapped, "UI", "Assets")),
                "Conflicting package sources remain unchanged even when one sits inside the ordinary UI Projects scan");

            string unlinked = Path.Combine(temporary, "unlinked custom Discovery");
            Table(unlinked, Array.Empty<Tuple<int, ulong, string>>());
            string unlinkedCard = Path.Combine(unlinked, "CustomScreens", "My arena", "live_card.jsfb");
            string unrelatedMenu = Path.Combine(unlinked, "CustomScreens", "Other menus", "unrelated_menu.jsfb");
            Copy(original, unlinkedCard);
            byte[] unrelatedBefore = RenameMatchupRoot(File.ReadAllBytes(original), "menuone"); Write(unrelatedMenu, unrelatedBefore);
            var unlinkedBefore = Snapshot(unlinked);
            var unlinkedCheck = await RunFor(unlinked, true);
            Check(unlinkedCheck.Catalog.RequiredHashes.Count == 0 && unlinkedCheck.Catalog.ScreenFiles.Count == 2,
                "Unlinked Scre candidates outside UI Projects are discovered without inventing arena references");
            Check(unlinkedCheck.Layouts.Patched == 1 && unlinkedCheck.Layouts.AddedSlots == 52
                && unlinkedCheck.Layouts.Failed.Count == 0 && unlinkedCheck.Layouts.Unsupported.Count == 0,
                "Integrated check finds the proven custom-location matchup's fifty-two missing movie slots");
            Check(Snapshot(unlinked).SequenceEqual(unlinkedBefore) && !Directory.Exists(Path.Combine(unlinked, "UI"))
                && !Directory.Exists(Path.Combine(unlinked, "Gorilla Studio Layout Backups")),
                "Checking unlinked custom-location candidates preserves every source byte and creates no movie or backup folders");
            var unlinkedSetup = await RunFor(unlinked, false);
            Check(unlinkedSetup.Layouts.Patched == 1 && unlinkedSetup.Layouts.AddedSlots == 52
                && unlinkedSetup.Layouts.AddedMovies == 101 && unlinkedSetup.Layouts.Failed.Count == 0 && unlinkedSetup.BakeRequired,
                "Integrated setup repairs a proven unlinked custom-location matchup and supplies its player movies");
            string unlinkedBackup = Path.Combine(unlinkedSetup.Layouts.BackupDirectory, "CustomScreens", "My arena", "live_card.jsfb.bak");
            Check(File.Exists(unlinkedBackup) && Hash(unlinkedBackup) == sourceHash && Hash(original) == sourceHash,
                "The custom-location repair keeps an exact backup and preserves the bundled RAW source");
            Check(File.ReadAllBytes(unrelatedMenu).SequenceEqual(unrelatedBefore)
                && !File.Exists(Path.Combine(unlinkedSetup.Layouts.BackupDirectory, "CustomScreens", "Other menus", "unrelated_menu.jsfb.bak")),
                "An unlinked custom outer-root menu receives no matchup authority and remains unchanged");
            string repairedUnlinkedHash = Hash(unlinkedCard);
            var unlinkedAgain = await RunFor(unlinked, false);
            Check(unlinkedAgain.Layouts.Patched == 0 && unlinkedAgain.Layouts.AddedSlots == 0
                && unlinkedAgain.Layouts.AddedMovies == 0 && unlinkedAgain.Layouts.Already == 1 && !unlinkedAgain.BakeRequired
                && Hash(unlinkedCard) == repairedUnlinkedHash && File.ReadAllBytes(unrelatedMenu).SequenceEqual(unrelatedBefore),
                "Repeating integrated custom-location setup is idempotent and still preserves unrelated menus");

            string conflicting = Path.Combine(temporary, "conflicting Discovery"); Directory.CreateDirectory(conflicting);
            Table(conflicting, new[] { Tuple.Create(1806, hash, Asset) });
            Copy(original, Path.Combine(conflicting, "first", hash.ToString("x16") + ".jsfb"));
            Copy(original, Path.Combine(conflicting, "second", hash.ToString("x16") + ".jsfb"));
            File.AppendAllText(Path.Combine(conflicting, "second", hash.ToString("x16") + ".jsfb"), "different");
            var conflict = await RunFor(conflicting, true);
            Check(conflict.Resolution.Conflicts.Contains(hash) && conflict.Missing.Any(message => message.Contains("conflicting")) && conflict.PreparedPaths.Count == 0, "Conflicting loose exports remain unresolved instead of choosing an arbitrary model/card source");

            string invalidExisting = Path.Combine(temporary, "invalid existing Discovery");
            Table(invalidExisting, new[] { Tuple.Create(1806, hash, Asset) });
            string invalidDestination = Path.Combine(invalidExisting, Asset.Replace('/', Path.DirectorySeparatorChar));
            byte[] invalidBytes = Encoding.UTF8.GetBytes("my damaged custom screen"); Write(invalidDestination, invalidBytes);
            var invalidBefore = Snapshot(invalidExisting);
            var invalidSetup = await RunFor(invalidExisting, false);
            Check(invalidSetup.Warnings.Any(message => message.Contains("active canonical Discovery matchup is invalid") && message.Contains("fallback copies were not promoted"))
                && invalidSetup.Missing.Count == 1 && invalidSetup.PreparedPaths.Count == 0 && invalidSetup.AddedScreens == 0,
                "An already invalid canonical matchup is reported and blocks fallback promotion without escaping as an exception");
            Check(Snapshot(invalidExisting).SequenceEqual(invalidBefore) && File.ReadAllBytes(invalidDestination).SequenceEqual(invalidBytes),
                "An invalid custom matchup destination remains byte-for-byte unchanged despite an available archive source");

            string changedDestinationRoot = Path.Combine(temporary, "destination changed after lookup Discovery");
            Table(changedDestinationRoot, new[] { Tuple.Create(1806, hash, Asset) });
            string changedDestination = Path.Combine(changedDestinationRoot, Asset.Replace('/', Path.DirectorySeparatorChar));
            byte[] changedBytes = Encoding.UTF8.GetBytes("new damaged custom screen created during lookup");
            bool changedDuringLookup = false;
            var changedSetup = await DiscoveryMatchupSetup.RunAsync(app, game, changedDestinationRoot, Path.Combine(temporary, "destination-change-cache"), false, null,
                async (command, token) => {
                    var execution = await Runner(command, token);
                    if (command.Arguments[0] == "unpack-file" && Argument(command, "--file") == Asset)
                    { Write(changedDestination, changedBytes); changedDuringLookup = true; }
                    return execution;
                }, CancellationToken.None);
            Check(changedDuringLookup && changedSetup.Issues.Any(message => message.Contains("existing Discovery project is invalid"))
                && changedSetup.Missing.Count == 1 && changedSetup.PreparedPaths.Count == 0 && changedSetup.AddedScreens == 0,
                "An invalid destination created after archive lookup becomes a setup issue instead of escaping the report");
            Check(File.ReadAllBytes(changedDestination).SequenceEqual(changedBytes)
                && !Directory.Exists(Path.Combine(changedDestinationRoot, "UI", "Assets"))
                && !Directory.Exists(Path.Combine(changedDestinationRoot, "Gorilla Studio Layout Backups")),
                "A destination changed during lookup is preserved without guessed overwrite, layout backup or placeholder movies");

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

    private static byte[] RenameMatchupRoot(byte[] originalBytes, string name)
    {
        byte[] result = (byte[])originalBytes.Clone();
        var screen = new FlatBufferFile(result, "Scre");
        foreach (int node in screen.Tables(screen.Root, 3))
        {
            int field = screen.Field(node, 1);
            if (field == 0) continue;
            int text = checked(field + (int)screen.U32(field)); int length = checked((int)screen.U32(text));
            if (Encoding.UTF8.GetString(result, text + 4, length) != "matchup") continue;
            byte[] replacement = Encoding.UTF8.GetBytes(name);
            if (replacement.Length != length) throw new InvalidOperationException("The fixture name must preserve the native root string's encoded length.");
            Buffer.BlockCopy(replacement, 0, result, text + 4, replacement.Length); return result;
        }
        throw new InvalidOperationException("The actual RAW fixture has no matchup root to rename.");
    }

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
