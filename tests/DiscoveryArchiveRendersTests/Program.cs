using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root, app, game, data, cache;
    private static int passed, indexes, extracts;
    private static readonly Dictionary<string, string[]> rows = new Dictionary<string, string[]>();
    private static readonly List<DiscoveryArchiveCommand> commands = new List<DiscoveryArchiveCommand>();
    private const string Picture = "UI/Assets/Roster/TypeA/Custom_PAC.tex";
    private static ulong pictureHash = Hash(Picture);
    private static bool invalidDds, missingOutput, extractionFails, explicitIndexFails;

    private static async Task Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-archive-pictures-" + Guid.NewGuid().ToString("N"));
        app = Path.Combine(root, "app"); game = Path.Combine(root, "game"); data = Path.Combine(root, "Discovery data"); cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(app); Directory.CreateDirectory(game); Directory.CreateDirectory(data);
        Write(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"), new byte[] { 1, 2, 3 });
        Write(Path.Combine(app, "engine", "bakery", "Libraries", "crn2.dll"), new byte[] { 4, 5 });
        Write(Path.Combine(game, "oo2core_9_win64.dll"), new byte[] { 6, 7 });
        try
        {
            var none = await Find();
            Equal("No archives preserves linked-reference status", true, none.Status.Contains("references are linked"));
            Equal("No archives is reported explicitly", 0, none.ArchiveCount);
            Equal("No archives never starts a process", 0, commands.Count);
            var empty = await DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { 0UL }, cache, null, Run, CancellationToken.None);
            Equal("Zero hashes require no index", true, empty.Status.Contains("No linked"));

            string cak = Archive(Path.Combine(game, "cakes", "custom PAC.cak"), Picture);
            string[] originalFiles = Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            string originalCak = Convert.ToBase64String(File.ReadAllBytes(cak));
            string originalCli = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe")));
            var progress = new List<string>();
            var result = await DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { pictureHash, pictureHash }, cache, progress.Add, Run, CancellationToken.None);
            Equal("An exact custom picture hash loads from its CAK index", 1, result.Images.Count);
            Equal("Nested game archives are discovered", 1, result.ArchiveCount);
            Equal("The resolved image is a cache DDS", true, result.Images[pictureHash].StartsWith(cache) && result.Images[pictureHash].EndsWith(".dds"));
            Equal("Hash-named DDS alias preserves the complete unsigned profile key", pictureHash.ToString("x16") + ".dds", Path.GetFileName(result.Images[pictureHash]));
            Equal("One index operation reads the original archive", 1, indexes);
            Equal("Only the selected linked image is extracted", 1, extracts);
            Equal("Progress includes archive lookup and selected picture extraction", true, progress.Any(x => x.Contains("archive")) && progress.Any(x => x.Contains("Custom_PAC")));
            Equal("The cached tool includes Crunch", true, File.Exists(Path.Combine(Path.GetDirectoryName(commands[0].FileName), "Libraries", "crn2.dll")));
            Equal("The cached tool includes the game's own Oodle", true, File.Exists(Path.Combine(Path.GetDirectoryName(commands[0].FileName), "oo2core_9_win64.dll")));
            Equal("No files are written into the game directory", true, originalFiles.SequenceEqual(Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x)));
            Equal("The original CAK remains unchanged", originalCak, Convert.ToBase64String(File.ReadAllBytes(cak)));
            Equal("The original Bakery remains unchanged", originalCli, Convert.ToBase64String(File.ReadAllBytes(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"))));
            await Find();
            Equal("Unchanged archive index is cached", 1, indexes);
            Equal("Unchanged decoded picture is cached", 1, extracts);

            File.AppendAllText(cak, " changed");
            await Find();
            Equal("Changed archive invalidates the index", 2, indexes);
            Equal("Changed archive invalidates its decoded image", 2, extracts);
            File.AppendAllText(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"), " updated");
            await Find();
            Equal("Updated Bakery invalidates archive indexes", 3, indexes);
            Equal("Updated Bakery invalidates decoded pictures", 3, extracts);
            File.AppendAllText(Path.Combine(game, "oo2core_9_win64.dll"), " updated");
            await Find();
            Equal("Updated Oodle invalidates native decoder cache", 4, indexes);
            Equal("Updated Oodle invalidates decoded pictures", 4, extracts);

            string index = commands.Last(x => x.Arguments[0] == "list-files").Arguments[4];
            index = Path.Combine(Path.GetDirectoryName(index), "files.txt");
            File.AppendAllText(index, " damaged");
            await Find();
            Equal("Corrupted cached index is rebuilt", 5, indexes);
            result = await Find(); File.WriteAllBytes(result.Images[pictureHash], new byte[] { 9 });
            await Find();
            Equal("Corrupted DDS cache is extracted again", 5, extracts);

            string second = Archive(Path.Combine(data, "mods", "second.cak"), Picture);
            int beforeExtract = extracts;
            result = await Find();
            Equal("Discovery archives are indexed alongside game archives", 2, result.ArchiveCount);
            Equal("Same hash in two CAKs is never resolved by file date", 0, result.Images.Count);
            Equal("The exact ambiguous hash is returned", pictureHash, result.Conflicts.Single());
            Equal("Ambiguous images are not extracted speculatively", beforeExtract, extracts);
            Equal("Conflict explains missing load-order evidence", true, result.Warnings.Any(x => x.Contains("load order")));
            File.Delete(second);

            string loose = Path.Combine(game, Picture.Replace('/', Path.DirectorySeparatorChar));
            Write(Path.ChangeExtension(loose, ".dds"), Dds(65));
            beforeExtract = extracts;
            result = await Find();
            Equal("An indexed external loose DDS is copied to cache", 65, File.ReadAllBytes(result.Images[pictureHash])[128]);
            Equal("An indexed loose image avoids native extraction", beforeExtract, extracts);
            Write(Path.ChangeExtension(loose, ".dds"), Dds(66));
            result = await Find();
            Equal("A changed loose picture invalidates its cache", 66, File.ReadAllBytes(result.Images[pictureHash])[128]);
            File.Delete(Path.ChangeExtension(loose, ".dds"));
            Write(loose, Dds(67));
            result = await Find();
            Equal("A DDS payload exported with TEX extension is valid", 67, File.ReadAllBytes(result.Images[pictureHash])[128]);
            File.Delete(loose);

            // Each bad row claims the selected hash, proving the indexed path is
            // checked rather than trusting a convenient hash or wrestler ID.
            rows[cak] = new[] {
                Line(pictureHash, "UI/Assets/Roster/TypeA/Other.tex"),
                Line(pictureHash, "UI/Assets/Roster/TypeA/../Custom_PAC.tex"),
                Line(pictureHash, "/UI/Assets/Roster/TypeA/Custom_PAC.tex"),
                Line(pictureHash, "C:/UI/Assets/Roster/TypeA/Custom_PAC.tex"),
                Line(pictureHash, "UI/Assets/Roster/TypeA/Custom_PAC.dds"),
                Line(pictureHash, "UI/Assets/Roster/TypeA/Custom_PAC.tex:stream"),
                Line(pictureHash, "UI/Assets/Roster/TypeA/Custom_PAC.tex "),
                Line(pictureHash, "Characters/Custom_PAC.tex"),
                pictureHash.ToString("x16").Substring(1) + "|0|" + Picture
            };
            File.AppendAllText(cak, " invalidindex");
            beforeExtract = extracts; result = await Find();
            Equal("Unsafe and mismatched index paths cannot produce an image", 0, result.Images.Count);
            Equal("Unsafe index paths never reach extraction", beforeExtract, extracts);
            Equal("Invalid exact-hash rows are reported", 8, result.Warnings.Count);
            Equal("Unreadable reference retains its linked status", true, result.Status.Contains("references remain linked"));

            rows[cak] = new[] { Line(pictureHash, Picture.Replace('/', '\\')), Line(pictureHash, Picture) };
            File.AppendAllText(cak, " normalize"); result = await Find();
            Equal("Slash-normalized exact paths are supported", 1, result.Images.Count);
            Equal("Duplicate rows within one archive do not conflict", 0, result.Conflicts.Count);
            Equal("Native extraction receives normalized forward slashes", Picture, commands.Last(x => x.Arguments[0] == "unpack-file").Arguments[4]);

            File.AppendAllText(cak, " invaliddds"); invalidDds = true;
            result = await Find(); invalidDds = false;
            Equal("A truncated native DDS never becomes a resolved render", 0, result.Images.Count);
            Equal("Invalid DDS produces a useful texture warning", true, result.Warnings.Any(x => x.Contains("decoded")));
            Equal("Temporary extraction directories are removed after failure", 0, Directory.GetDirectories(cache, "extract-*", SearchOption.AllDirectories).Length);
            result = await Find(); Equal("A failed extraction is retryable", 1, result.Images.Count);

            File.AppendAllText(cak, " failedprocess"); extractionFails = true;
            result = await Find(); extractionFails = false;
            Equal("A failing native extraction cannot publish output", 0, result.Images.Count);
            Equal("Native errors remain visible", true, result.Warnings.Any(x => x.Contains("fixture decoder failed")));
            File.AppendAllText(cak, " noindex"); missingOutput = true;
            result = await Find(); missingOutput = false;
            Equal("Successful exit without an index is not accepted", 0, result.Images.Count);
            Equal("Missing index output is reported", true, result.Warnings.Any(x => x.Contains("could not index")));

            File.AppendAllText(cak, " outputoptioncrash"); explicitIndexFails = true;
            int beforeCommands = commands.Count;
            originalFiles = Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            originalCak = Convert.ToBase64String(File.ReadAllBytes(cak));
            result = await Find(); explicitIndexFails = false;
            Equal("The shipped output-option crash falls back to default index output", 1, result.Images.Count);
            var fallback = commands.Skip(beforeCommands).Single(x => x.Arguments[0] == "list-files" && x.Arguments.Length == 4);
            Equal("Compatibility indexing retains the archive's encryption basename", Path.GetFileName(cak), Path.GetFileName(fallback.Arguments[2]));
            Equal("Compatibility indexing runs against a cache-only archive path", true, fallback.Arguments[2].StartsWith(cache));
            Equal("The default index sidecar is kept away from game data", true, originalFiles.SequenceEqual(Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x)));
            Equal("Compatibility indexing leaves original archive bytes unchanged", originalCak, Convert.ToBase64String(File.ReadAllBytes(cak)));
            Equal("The temporary cache archive link is removed", false, File.Exists(fallback.Arguments[2]));
            Equal("The temporary default index sidecar is removed", false, File.Exists(Path.Combine(Path.GetDirectoryName(fallback.Arguments[2]), Path.GetFileNameWithoutExtension(cak) + "_files.txt")));
            int afterFallback = indexes; await Find();
            Equal("An index recovered from the native output bug remains cached", afterFallback, indexes);

            string unavailable = Path.Combine(root, "no-app");
            result = await DiscoveryArchiveRenders.FindAsync(unavailable, game, data, new[] { pictureHash }, cache, null, Run, CancellationToken.None);
            Equal("Missing bundled reader is reported without breaking the roster", true, result.Status.Contains("reader is unavailable"));
            await Throws<ArgumentException>("A game-folder cache is rejected", () => DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { pictureHash }, Path.Combine(game, "cache"), null, Run, CancellationToken.None));
            await Throws<ArgumentException>("A Discovery-folder cache is rejected", () => DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { pictureHash }, data, null, Run, CancellationToken.None));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Throws<OperationCanceledException>("Cancellation is honored before reading archives", () => DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { pictureHash }, cache, null, Run, cancellation.Token));
            }
            await Symlinks(cak);
            await CopyFallbackChecks(cak);
            await NativeProcessChecks();
            Console.WriteLine("PASS " + passed + " Discovery archive render checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<DiscoveryArchiveRenderResult> Find() { return DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { pictureHash }, cache, null, Run, CancellationToken.None); }

    private static Task<DiscoveryArchiveProcessResult> Run(DiscoveryArchiveCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); commands.Add(command);
        Equal("Native executable runs from its own cache directory", Path.GetDirectoryName(command.FileName), command.WorkingDirectory);
        Equal("Native executable is copied outside source folders", true, command.FileName.StartsWith(cache));
        Equal("Archive timeout matches the native setup for this operation",
            command.Arguments[0] == "list-files" ? 300000 : 180000, command.TimeoutMilliseconds);
        string[] args = command.Arguments;
        Equal("Archive flag is exact shipped Bakery input option", "--input", args[1]);
        Equal("Archive retains its original filename", true, File.Exists(args[2]) && Path.GetExtension(args[2]) == ".cak");
        if (args[0] == "list-files")
        {
            indexes++;
            string output;
            string original = rows.ContainsKey(args[2]) ? args[2] : rows.Keys.Single(x => Path.GetFileName(x) == Path.GetFileName(args[2]));
            if (args.Length == 6)
            {
                Equal("Index output flag is explicit", "--output", args[3]);
                Equal("Extended hash index uses the shipped flag", "--extended", args[5]);
                Equal("Index output is a cache file", true, args[4].StartsWith(cache));
                if (explicitIndexFails) return Task.FromResult(new DiscoveryArchiveProcessResult { ExitCode = -1, Output = "fixture native output-option crash" });
                output = args[4];
            }
            else
            {
                Equal("Fallback uses no output option", 4, args.Length);
                Equal("Fallback still requests the shipped extended hash index", "--extended", args[3]);
                Equal("Fallback input is a cache archive with identical bytes", true, args[2].StartsWith(cache) && File.ReadAllBytes(args[2]).SequenceEqual(File.ReadAllBytes(original)));
                output = Path.Combine(Path.GetDirectoryName(args[2]), Path.GetFileNameWithoutExtension(args[2]) + "_files.txt");
                Equal("Fallback's predicted default sidecar is cache-only", true, output.StartsWith(cache));
            }
            if (!missingOutput) File.WriteAllText(output, "// Fixture cake\n" + string.Join("\n", rows[original]) + "\n", Encoding.UTF8);
        }
        else
        {
            extracts++;
            Equal("Extraction uses the shipped unpack-file command", "unpack-file", args[0]);
            Equal("Extraction specifies one exact file", "--file", args[3]);
            Equal("Extraction explicitly writes into cache", true, args[5] == "--output" && args[6].StartsWith(cache) && args.Length == 7);
            Equal("Native DDS conversion remains enabled", false, args.Contains("--no-convert-dds"));
            if (extractionFails) return Task.FromResult(new DiscoveryArchiveProcessResult { ExitCode = 1, Output = "fixture decoder failed" });
            string output = Path.Combine(args[6], Path.ChangeExtension(args[4].Replace('/', Path.DirectorySeparatorChar), ".dds"));
            Write(output, invalidDds ? new byte[128] : Dds(42));
        }
        return Task.FromResult(new DiscoveryArchiveProcessResult { ExitCode = 0, Output = "success" });
    }

    private static async Task Symlinks(string archive)
    {
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        string linkedArchive = Path.Combine(data, "linked.cak"), linkedDirectory = Path.Combine(data, "linked-folder");
        try
        {
            string target = Archive(Path.Combine(outside, "outside.cak"), Picture);
            File.CreateSymbolicLink(linkedArchive, target); Directory.CreateSymbolicLink(linkedDirectory, outside);
            var result = await Find();
            Equal("Reparse files and folders cannot introduce archive candidates", 1, result.ArchiveCount);
            await Throws<ArgumentException>("A symlinked cache cannot write outside its actual destination", () =>
                DiscoveryArchiveRenders.FindAsync(app, game, data, new[] { pictureHash }, Path.Combine(linkedDirectory, "cache"), null, Run, CancellationToken.None));
            string nestedRoot = Path.Combine(linkedDirectory, "nested"); Directory.CreateDirectory(nestedRoot);
            result = await DiscoveryArchiveRenders.FindAsync(app, null, nestedRoot, new[] { pictureHash }, cache, null, Run, CancellationToken.None);
            Equal("A selected root underneath a linked ancestor is not traversed", 0, result.ArchiveCount);
            Directory.CreateSymbolicLink(Path.Combine(data, "cycle"), data);
            result = await Find(); Equal("A folder cycle cannot prevent archive completion", 1, result.ArchiveCount);
            Directory.Delete(Path.Combine(data, "cycle"));
        }
        finally { File.Delete(linkedArchive); Directory.Delete(linkedDirectory); Directory.Delete(outside, true); }
    }

    private static async Task NativeProcessChecks()
    {
        if (!File.Exists("/bin/sh")) return;
        var method = typeof(DiscoveryArchiveRenders).GetMethod("RunProcessAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Func<DiscoveryArchiveCommand, CancellationToken, Task<DiscoveryArchiveProcessResult>> runner = (command, token) =>
            (Task<DiscoveryArchiveProcessResult>)method.Invoke(null, new object[] { command, token });
        // Windows quoting differs from POSIX shell quoting; on Linux test process
        // lifetime with simple arguments and test the quoting routine separately.
        var quote = typeof(DiscoveryArchiveRenders).GetMethod("Quote", BindingFlags.Static | BindingFlags.NonPublic);
        Equal("Windows quoting doubles a trailing backslash", "\"folder with space\\\\\"", quote.Invoke(null, new object[] { "folder with space\\" }));
        Equal("Windows quoting escapes embedded quotes", "\"a\\\"b\"", quote.Invoke(null, new object[] { "a\"b" }));
        var trueCommand = new DiscoveryArchiveCommand { FileName = "/bin/true", Arguments = new string[0], WorkingDirectory = root, TimeoutMilliseconds = 5000 };
        Equal("Default process runner captures a real exit code", 0, (await runner(trueCommand, CancellationToken.None)).ExitCode);
        var wait = new DiscoveryArchiveCommand { FileName = "/bin/sleep", Arguments = new[] { "10" }, WorkingDirectory = root, TimeoutMilliseconds = 100 };
        await Throws<TimeoutException>("Native reader timeout terminates its process", async () => { await runner(wait, CancellationToken.None); });
        using (var cts = new CancellationTokenSource())
        {
            cts.CancelAfter(100); wait.TimeoutMilliseconds = 10000;
            await Throws<OperationCanceledException>("Native reader cancellation terminates its process", async () => { await runner(wait, cts.Token); });
        }
    }

    private static async Task CopyFallbackChecks(string source)
    {
        var method = typeof(DiscoveryArchiveRenders).GetMethod("CopyFallbackArchiveAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Func<string, string, Action<string>, CancellationToken, Task> copy = (input, output, progress, token) =>
            (Task)method.Invoke(null, new object[] { input, output, progress, token });
        string shadow = Path.Combine(cache, "copy-test.cak");
        var notes = new List<string>();
        byte[] before = File.ReadAllBytes(source);
        await copy(source, shadow, notes.Add, CancellationToken.None);
        Equal("A small cross-volume fallback copy preserves exact archive bytes", true, File.ReadAllBytes(shadow).SequenceEqual(before));
        Equal("Fallback copying never modifies its original source", true, File.ReadAllBytes(source).SequenceEqual(before));
        Equal("Copy overhead is reported with the archive name and MiB", true, notes.Single().Contains(Path.GetFileName(source)) && notes.Single().Contains("MiB"));
        File.Delete(shadow);
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Throws<OperationCanceledException>("A canceled compatibility copy does not start", () => copy(source, shadow, null, cts.Token));
            Equal("A canceled copy creates no partial archive", false, File.Exists(shadow));
        }
        string huge = Path.Combine(root, "huge.cak");
        using (var file = File.Create(huge)) file.SetLength(512L * 1024 * 1024 + 1);
        await Throws<IOException>("Large archives are never automatically copied across drives", () => copy(huge, shadow, null, CancellationToken.None));
        Equal("Rejected large archive copies create no destination", false, File.Exists(shadow));
        File.Delete(huge);
    }

    private static string Archive(string path, string picture) { Write(path, new byte[] { 10, 11 }); rows[path] = new[] { Line(Hash(picture), picture) }; return path; }
    private static string Line(ulong hash, string path) { return hash.ToString("X16") + "|" + hash + "|" + path + "|Offset:0x1024|ZSize:0x100|compressed:True"; }
    private static ulong Hash(string path) { ulong value = 14695981039346656037UL; foreach (char c in path) value = unchecked((value ^ char.ToLowerInvariant(c)) * 1099511628211UL); return value; }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static byte[] Dds(byte marker)
    {
        var bytes = new byte[132]; bytes[0] = (byte)'D'; bytes[1] = (byte)'D'; bytes[2] = (byte)'S'; bytes[3] = 32;
        bytes[4] = 124; bytes[12] = 1; bytes[16] = 1; bytes[76] = 32; bytes[80] = 0x41; bytes[88] = 32; bytes[128] = marker; return bytes;
    }
    private static void Equal<T>(string message, T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual); passed++; }
    private static async Task Throws<T>(string message, Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { passed++; return; } throw new Exception(message + ": exception not raised"); }
}
