using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root, app, game, data, cache;
    private static int checks, indexed, extracted;
    private static bool outputCrash, invalidOutput, mutateArchive;
    private static readonly Dictionary<string, string[]> indexes = new Dictionary<string, string[]>();
    private static readonly List<DiscoveryArchiveCommand> commands = new List<DiscoveryArchiveCommand>();
    private const string Asset = "Mods/AEW/Dynamite_custom_screen.jsfb";
    private static readonly ulong hash = DiscoveryArchiveMatchups.HashPath(Asset);

    private static async Task Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-matchup-resolver-" + Guid.NewGuid().ToString("N"));
        app = Path.Combine(root, "app"); game = Path.Combine(root, "game"); data = Path.Combine(root, "discovery_data"); cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(game); Directory.CreateDirectory(data);
        Write(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"), new byte[] { 1, 2 });
        Write(Path.Combine(game, "oo2core_9_win64.dll"), new byte[] { 3 });
        try
        {
            foreach (string invalid in new[] { "../outside.jsfb", "/absolute.jsfb", "C:/outside.jsfb", "A//B.jsfb", "A/./B.jsfb", "A/../B.jsfb", "A.jsfb:stream", "A.jsfb ", "A?.jsfb", "A\n.jsfb", "screen.dds" })
                Equal("Reject unsafe indexed path " + invalid.Replace('\n', '?'), false, DiscoveryArchiveMatchups.TryAssetPath(invalid, out _));
            Equal("Nonstandard mod folders are valid when exactly referenced", true, DiscoveryArchiveMatchups.TryAssetPath(Asset, out _));
            Equal("Hashing follows case-independent normalized game paths", hash, DiscoveryArchiveMatchups.HashPath(Asset.ToUpperInvariant().Replace('/', '\\')));
            // Values independently executed from the original Python 3.14
            // gs_paths.fnv1a64 code object, without importing/replacing that code.
            Equal("ASCII hashing matches the original engine's measured screen reference", 0x94e985399efe3bfdUL,
                DiscoveryArchiveMatchups.HashPath("UI/Projects/MatchUp/MatchUp_orgNeon_2K19/2K19_match_card.jsfb"));
            const string unicodePath = "Mods/AEW/México_東京_💥.jsfb";
            const ulong unicodeHash = 0x302f987c1dde909aUL;
            Equal("Non-ASCII paths hash UTF8 bytes like the original renderer", unicodeHash, DiscoveryArchiveMatchups.HashPath(unicodePath));
            Equal("Uppercase and Windows separators retain the measured Unicode hash", unicodeHash,
                DiscoveryArchiveMatchups.HashPath("MODS\\AEW\\MÉXICO_東京_💥.JSFB"));

            var result = await Find();
            Equal("Unavailable projects are never called resolved", 0, result.Assets.Count);
            Equal("Unavailable projects have a useful status", true, result.Status.Contains("Unresolved references"));
            Equal("No archives never starts native tools", 0, commands.Count);
            result = await DiscoveryArchiveMatchups.FindAsync(app, game, data, new[] { 0UL }, cache, null, Run, CancellationToken.None);
            Equal("Zero references produce no lookup", true, result.Status.Contains("No nonzero"));

            string loose = Path.Combine(data, Asset.Replace('/', Path.DirectorySeparatorChar));
            Write(loose, Scre('A'));
            result = await Find();
            Equal("An exact Discovery-relative mod project loads without filename restrictions", 1, result.Assets.Count);
            Equal("Loose provenance keeps its original asset path", Asset, result.Assets[hash].AssetPath);
            Equal("Loose provenance points at its actual source file", loose, result.Assets[hash].SourcePath);
            Equal("Loose provenance is identified", true, result.Assets[hash].IsLoose);
            Equal("Every resolved project is a cache copy", true, result.Assets[hash].Path.StartsWith(cache));
            Equal("Loose loading does not require Bakery", 0, commands.Count);
            Equal("Source project remains unchanged", true, File.ReadAllBytes(loose).SequenceEqual(Scre('A')));
            string previous = result.Assets[hash].Path;
            Write(loose, Scre('B')); result = await Find();
            Equal("Changing a loose screen invalidates its cached pixels", false, result.Assets[hash].Path == previous);
            Equal("Changed screen cache has current bytes", true, File.ReadAllBytes(result.Assets[hash].Path).SequenceEqual(Scre('B')));

            string literal = Path.Combine(data, "exports", hash.ToString("X16") + ".jsfb");
            Write(literal, Scre('C')); result = await Find();
            Equal("Conflicting loose exports do not overwrite active screens", 0, result.Assets.Count);
            Equal("Conflicting exact reference is returned", hash, result.Conflicts.Single());
            Write(literal, Scre('B')); result = await Find();
            Equal("Byte-identical aliases are accepted", 1, result.Assets.Count);
            Equal("Known relative paths outrank hash-only aliases of the same bytes", Asset, result.Assets[hash].AssetPath);
            File.Delete(loose); result = await Find();
            Equal("Explicit full uint64 hash export is readable", 1, result.Assets.Count);
            Equal("A hash-only export never invents an installation path", null, result.Assets[hash].AssetPath);
            Equal("A hash-only export explains the installation limitation", true, result.Status.Contains("no proven original"));
            File.Delete(literal);
            Write(loose, new byte[32]); result = await Find();
            Equal("Opaque JSFB is never treated as a screen", 0, result.Assets.Count);
            File.Delete(loose);

            string cak = AddArchive(Path.Combine(game, "cakes", "AEW mod.cak"), new[] { Line(hash, Asset), Line(hash + 1, "Other/Unused.jsfb") });
            string[] beforeFiles = Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
            result = await Find();
            Equal("An exact referenced archive project is extracted", 1, result.Assets.Count);
            Equal("Its original indexed path is preserved", Asset, result.Assets[hash].AssetPath);
            Equal("Its original archive is recorded", cak, result.Assets[hash].ArchivePath);
            Equal("Archive extraction provenance is distinguished", false, result.Assets[hash].IsLoose);
            Equal("Only requested assets are extracted", 1, extracted);
            Equal("The recursive archive count is exact", 1, result.ArchiveCount);
            Equal("No game file or index is written by resolution", true, beforeFiles.SequenceEqual(Directory.GetFiles(game, "*", SearchOption.AllDirectories).OrderBy(x => x)));
            Equal("Discovery receives no implicit installation", false, File.Exists(loose));
            await Find(); Equal("Unchanged extended indexes are reused", 1, indexed); Equal("Unchanged Scre extraction is reused", 1, extracted);
            File.AppendAllText(cak, " changed"); await Find();
            Equal("Archive changes invalidate indexes", 2, indexed); Equal("Archive changes invalidate extracted projects", 2, extracted);
            File.AppendAllText(Path.Combine(app, "engine", "bakery", "Bakery.CLI.exe"), " changed"); await Find();
            Equal("Native reader changes invalidate indexes", 3, indexed); Equal("Native reader changes invalidate projects", 3, extracted);
            result = await Find(); File.WriteAllBytes(result.Assets[hash].Path, new byte[32]); await Find();
            Equal("Corrupt cached Scre is extracted again", 4, extracted);

            string second = AddArchive(Path.Combine(data, "Mods", "override.cak"), new[] { Line(hash, Asset) });
            int beforeExtract = extracted; result = await Find();
            Equal("Ambiguous CAK load order never picks the newest archive", 0, result.Assets.Count);
            Equal("Ambiguous archive hash is returned", hash, result.Conflicts.Single());
            Equal("Ambiguous archives are not extracted speculatively", beforeExtract, extracted);
            File.Delete(second);
            Write(loose, Scre('D')); result = await Find();
            Equal("Active extracted Discovery edits take precedence over archived stock", true, result.Assets[hash].IsLoose && File.ReadAllBytes(result.Assets[hash].Path).SequenceEqual(Scre('D')));
            File.Delete(loose);

            indexes[cak] = new[] { Line(hash, "../escape.jsfb"), Line(hash, "Wrong/project.jsfb"), Line(hash, "C:/bad.jsfb"), Line(hash, "UI/Assets/texture.dds") };
            File.AppendAllText(cak, " unsafe"); beforeExtract = extracted; result = await Find();
            Equal("Unsafe and hash-mismatched rows do not resolve references", 0, result.Assets.Count);
            Equal("Unsafe rows never reach native extraction", beforeExtract, extracted);
            Equal("Invalid referenced index rows are explained", 4, result.Warnings.Count);
            indexes[cak] = new[] { Line(hash, Asset.Replace('/', '\\')) };
            File.AppendAllText(cak, " normalized"); result = await Find();
            Equal("Indexed Windows separators are normalized", Asset, result.Assets[hash].AssetPath);

            File.AppendAllText(cak, " nativebug"); outputCrash = true; result = await Find(); outputCrash = false;
            Equal("The real Bakery output-option bug uses the safe default-output fallback", 1, result.Assets.Count);
            Equal("Default-output fallback was exercised", true, commands.Any(x => x.Arguments[0] == "list-files" && x.Arguments.Length == 4));
            Equal("Temporary fallback archives and sidecars are removed", 0, Directory.GetDirectories(cache, "index-*", SearchOption.AllDirectories).Length);

            File.AppendAllText(cak, " wrongformat"); invalidOutput = true; result = await Find(); invalidOutput = false;
            Equal("Archive output must be an actual nonempty Scre hierarchy", 0, result.Assets.Count);
            Equal("Non-Scre extraction cannot masquerade as a successful match", true, result.Warnings.Any(x => x.Contains("complete Scre")));
            Equal("Failed extraction scratch is removed", 0, Directory.GetDirectories(cache, "extract-*", SearchOption.AllDirectories).Length);
            File.AppendAllText(cak, " changewhilereading"); mutateArchive = true; result = await Find(); mutateArchive = false;
            Equal("An archive changed during extraction cannot publish a result", 0, result.Assets.Count);
            Equal("Mutation is reported", true, result.Warnings.Any(x => x.Contains("changed during")));

            await Throws<ArgumentException>("The resolver cannot use a source-directory cache", () => DiscoveryArchiveMatchups.FindAsync(app, game, data, new[] { hash }, Path.Combine(data, "cache"), null, Run, CancellationToken.None));
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); await Throws<OperationCanceledException>("Cancellation prevents lookup", () => DiscoveryArchiveMatchups.FindAsync(app, game, data, new[] { hash }, cache, null, Run, canceled.Token));
            }
            string linked = Path.Combine(data, "linked"); Directory.CreateSymbolicLink(linked, game);
            result = await Find(); Directory.Delete(linked);
            Equal("A source reparse folder is not traversed as another mod archive", 1, result.ArchiveCount);

            string unicodeSource = Path.Combine(data, unicodePath.Replace('/', Path.DirectorySeparatorChar));
            Write(unicodeSource, Scre('U'));
            result = await DiscoveryArchiveMatchups.FindAsync(app, game, data, new[] { unicodeHash }, cache, null, Run, CancellationToken.None);
            Equal("An extracted Unicode project resolves its actual UTF8 profile reference", unicodeSource, result.Assets[unicodeHash].SourcePath);
            Equal("Unicode provenance preserves the actual original filename", unicodePath, result.Assets[unicodeHash].AssetPath);
            File.Delete(unicodeSource);
            ulong bakeryHash = 14695981039346656037UL;
            foreach (char character in unicodePath) bakeryHash = unchecked((bakeryHash ^ char.ToLowerInvariant(character)) * 1099511628211UL);
            Equal("The shipped Bakery char algorithm is distinguishable from UTF8", true, bakeryHash != unicodeHash);
            string unicodeCak = AddArchive(Path.Combine(game, "Unicode mod.cak"), new[] { Line(bakeryHash, unicodePath) });
            int beforeUnicodeExtract = extracted;
            result = await DiscoveryArchiveMatchups.FindAsync(app, game, data, new[] { unicodeHash }, cache, null, Run, CancellationToken.None);
            Equal("A Unicode Bakery hash disagreement is not a proven archive match", 0, result.Assets.Count);
            Equal("Unverified Unicode archive paths are not sent to native extraction", beforeUnicodeExtract, extracted);
            Equal("The Unicode reader limitation has an actionable explanation", true, result.Warnings.Any(x => x.Contains("non-ASCII") && x.Contains("extract")));
            File.Delete(unicodeCak);

            if (args.Length > 0)
            {
                string real = Path.Combine(args[0], "data", "gamepack", "screens", "template_matchup.jsfb");
                Equal("The actual native movie screen template is accepted", true, DiscoveryArchiveMatchups.IsScre(real));
                string pack = Path.Combine(args[0], "data", "gamepack", "screens", "BakeMe");
                int actual = 0;
                foreach (string path in Directory.GetFiles(pack, "*.jsfb", SearchOption.AllDirectories)
                    .Where(path => !Path.GetFileName(path).StartsWith("CharacterSelect", StringComparison.OrdinalIgnoreCase)))
                { Equal("Actual bundled Scre project validates: " + Path.GetFileName(path), true, DiscoveryArchiveMatchups.IsScre(path)); actual++; }
                Equal("Actual match-card screen fixtures were exercised", true, actual >= 86);
            }
            Console.WriteLine("PASS " + checks + " Discovery matchup archive checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<DiscoveryArchiveMatchupResult> Find() { return DiscoveryArchiveMatchups.FindAsync(app, game, data, new[] { hash }, cache, null, Run, CancellationToken.None); }
    private static Task<DiscoveryArchiveProcessResult> Run(DiscoveryArchiveCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); commands.Add(command);
        Equal("Bakery is run from its cache copy", true, command.FileName.StartsWith(cache));
        Equal("Archive timeout matches the native setup for this operation",
            command.Arguments[0] == "list-files" ? 300000 : 180000, command.TimeoutMilliseconds);
        string[] a = command.Arguments;
        Equal("The native source flag is exact", "--input", a[1]);
        if (a[0] == "list-files")
        {
            indexed++;
            string original = indexes.ContainsKey(a[2]) ? a[2] : indexes.Keys.Single(x => Path.GetFileName(x) == Path.GetFileName(a[2]));
            string output;
            if (a.Length == 6)
            {
                Equal("Explicit index outputs stay in cache", true, a[3] == "--output" && a[4].StartsWith(cache) && a[5] == "--extended");
                if (outputCrash) return Task.FromResult(new DiscoveryArchiveProcessResult { ExitCode = -1, Output = "Known fixture output-option crash" });
                output = a[4];
            }
            else
            {
                Equal("Native bug fallback retains the archive encryption basename", Path.GetFileName(original), Path.GetFileName(a[2]));
                Equal("Native bug fallback writes sidecars beside a cache input", true, a.Length == 4 && a[2].StartsWith(cache) && a[3] == "--extended");
                output = Path.Combine(Path.GetDirectoryName(a[2]), Path.GetFileNameWithoutExtension(a[2]) + "_files.txt");
            }
            File.WriteAllText(output, "// Bakery extended fixture\n" + string.Join("\n", indexes[original]), Encoding.UTF8);
        }
        else
        {
            extracted++;
            Equal("Only the exact JSFB is unpacked", true, a[0] == "unpack-file" && a[3] == "--file" && a[4] == Asset);
            Equal("Project extraction output stays in cache", true, a.Length == 7 && a[5] == "--output" && a[6].StartsWith(cache));
            Write(Path.Combine(a[6], Asset.Replace('/', Path.DirectorySeparatorChar)), invalidOutput ? new byte[32] : Scre('X'));
            if (mutateArchive) File.AppendAllText(a[2], " changed during extraction");
        }
        return Task.FromResult(new DiscoveryArchiveProcessResult { ExitCode = 0, Output = "fixture complete" });
    }
    private static string AddArchive(string path, string[] rows) { Write(path, new byte[] { 10, 11 }); indexes[path] = rows; return path; }
    private static string Line(ulong value, string asset) { return value.ToString("X16") + "|" + value + "|" + asset + "|Offset:0x0|ZSize:0x10|compressed:False"; }
    private static byte[] Scre(char marker)
    {
        var bytes = new byte[88]; Put32(bytes, 0, 24); Encoding.ASCII.GetBytes("Scre").CopyTo(bytes, 4);
        bytes[8] = 12; bytes[10] = 8; bytes[18] = 4; Put32(bytes, 24, 16); Put32(bytes, 28, 4);
        Put32(bytes, 32, 1); Put32(bytes, 36, 28); bytes[40] = 8; bytes[42] = 8; bytes[46] = 4;
        Put32(bytes, 64, 24); Put32(bytes, 68, 4); Put32(bytes, 72, 7); Encoding.ASCII.GetBytes("matchu" + marker).CopyTo(bytes, 76); return bytes;
    }
    private static void Put32(byte[] bytes, int at, uint value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static void Equal<T>(string label, T expected, T actual) { if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual); checks++; }
    private static async Task Throws<T>(string label, Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { checks++; return; } throw new Exception(label + ": expected exception"); }
}
