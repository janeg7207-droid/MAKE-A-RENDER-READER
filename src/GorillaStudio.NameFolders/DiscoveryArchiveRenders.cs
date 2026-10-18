using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryArchiveRenderResult
    {
        public Dictionary<ulong, string> Images { get; } = new Dictionary<ulong, string>();
        public List<ulong> Conflicts { get; } = new List<ulong>();
        public List<string> Warnings { get; } = new List<string>();
        public int ArchiveCount { get; internal set; }
        public string Status { get; internal set; }
    }

    public sealed class DiscoveryArchiveCommand
    {
        public string FileName { get; internal set; }
        public string[] Arguments { get; internal set; }
        public string WorkingDirectory { get; internal set; }
        public int TimeoutMilliseconds { get; internal set; }
    }

    public sealed class DiscoveryArchiveProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
    }

    public delegate Task<DiscoveryArchiveProcessResult> DiscoveryArchiveRunner(
        DiscoveryArchiveCommand command, CancellationToken cancellationToken);

    /// <summary>Resolves linked TypeA picture hashes using the shipped Bakery archive index.</summary>
    public static class DiscoveryArchiveRenders
    {
        private const long MaximumIndexBytes = 256L * 1024 * 1024;
        private const long MaximumPictureBytes = 128L * 1024 * 1024;
        private const long MaximumFallbackCopyBytes = 512L * 1024 * 1024;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private sealed class Archive
        {
            public string Path;
            public string Root;
            public string Fingerprint;
        }

        private sealed class Match
        {
            public Archive Archive;
            public string Path;
        }

        public static Task<DiscoveryArchiveRenderResult> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<ulong> requiredHashes, string cacheRoot, Action<string> progress)
        {
            return FindAsync(appRoot, gameRoot, discoveryRoot, requiredHashes, cacheRoot, progress, RunProcessAsync, CancellationToken.None);
        }

        public static Task<DiscoveryArchiveRenderResult> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<ulong> requiredHashes, string cacheRoot, Action<string> progress, CancellationToken cancellationToken)
        {
            return FindAsync(appRoot, gameRoot, discoveryRoot, requiredHashes, cacheRoot, progress, RunProcessAsync, cancellationToken);
        }

        // This overload also lets portable tests exercise the exact Windows commands
        // without substituting an imaginary archive format for Bakery's real index.
        public static Task<DiscoveryArchiveRenderResult> FindAsync(string appRoot, string gameRoot, string discoveryRoot,
            IEnumerable<ulong> requiredHashes, string cacheRoot, Action<string> progress,
            DiscoveryArchiveRunner runner, CancellationToken cancellationToken)
        {
            if (requiredHashes == null) throw new ArgumentNullException(nameof(requiredHashes));
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            if (string.IsNullOrWhiteSpace(cacheRoot)) throw new ArgumentException("Choose a render cache outside the game and Discovery folders.", nameof(cacheRoot));
            var requested = new HashSet<ulong>(requiredHashes.Where(value => value != 0));
            string cache = Path.GetFullPath(cacheRoot);
            if (HasExistingReparseAncestor(cache)) throw new ArgumentException("The render cache cannot use a linked directory.", nameof(cacheRoot));
            foreach (string source in new[] { gameRoot, discoveryRoot })
                if (!string.IsNullOrWhiteSpace(source) && IsWithin(cache, Path.GetFullPath(source)))
                    throw new ArgumentException("The render cache must be outside the game and Discovery folders.", nameof(cacheRoot));
            return Task.Run(async () =>
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await FindCoreAsync(appRoot, gameRoot, discoveryRoot, requested, cache, progress, runner, cancellationToken).ConfigureAwait(false); }
                finally { Gate.Release(); }
            }, cancellationToken);
        }

        private static async Task<DiscoveryArchiveRenderResult> FindCoreAsync(string appRoot, string gameRoot, string discoveryRoot,
            HashSet<ulong> required, string cache, Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken token)
        {
            var result = new DiscoveryArchiveRenderResult();
            if (required.Count == 0) { result.Status = "No linked roster pictures need archive lookup."; return result; }
            var archives = FindArchives(gameRoot, discoveryRoot, cache, result.Warnings, token);
            result.ArchiveCount = archives.Count;
            if (archives.Count == 0)
            {
                result.Status = "Render references are linked; no game or Discovery CAK archives are available to read their pictures.";
                return result;
            }
            string executable;
            string toolKey;
            try { executable = PrepareTool(appRoot, gameRoot, cache, out toolKey); }
            catch (Exception error) when (IsFileError(error))
            {
                result.Warnings.Add(error.Message);
                result.Status = "Render references are linked; the bundled archive reader is unavailable.";
                return result;
            }
            var matches = new Dictionary<ulong, List<Match>>();
            for (int i = 0; i < archives.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                Archive archive = archives[i];
                Report(progress, "Reading linked roster pictures: archive " + (i + 1) + " of " + archives.Count + "…");
                try
                {
                    archive.Fingerprint = FileStamp(archive.Path);
                    string key = Hash("index-v1\n" + archive.Fingerprint + "\n" + toolKey);
                    string directory = Path.Combine(cache, "indexes", key);
                    Directory.CreateDirectory(directory);
                    string index = Path.Combine(directory, "files.txt");
                    if (!CacheValid(index))
                    {
                        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
                        try
                        {
                            var execution = await runner(Command(executable, 45000, "list-files", "--input", archive.Path,
                                "--output", temporary, "--extended"), token).ConfigureAwait(false);
                            if (execution == null || execution.ExitCode != 0 || !File.Exists(temporary))
                                await DefaultIndexFallbackAsync(executable, archive, temporary, directory, progress, runner, token).ConfigureAwait(false);
                            if (new FileInfo(temporary).Length > MaximumIndexBytes) throw new IOException("The archive index exceeds the supported size.");
                            if (FileStamp(archive.Path) != archive.Fingerprint) throw new IOException("The archive changed while its picture index was being read.");
                            Replace(temporary, index);
                            SaveValidation(index);
                        }
                        finally { Delete(temporary); }
                    }
                    ReadMatches(index, archive, required, matches, result.Warnings);
                }
                catch (Exception error) when (IsFileError(error) || error is TimeoutException || error is System.ComponentModel.Win32Exception)
                { result.Warnings.Add("Could not read " + Path.GetFileName(archive.Path) + ": " + error.Message); }
            }

            foreach (var pair in matches.OrderBy(value => value.Key))
            {
                token.ThrowIfCancellationRequested();
                // The same hash in two archives may be an override. File dates and
                // alphabetical order do not prove which archive the game loads.
                var candidates = pair.Value.GroupBy(value => value.Archive.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList();
                if (candidates.Count != 1)
                {
                    result.Conflicts.Add(pair.Key);
                    result.Warnings.Add("Linked picture " + pair.Key.ToString("x16", CultureInfo.InvariantCulture)
                        + " is present in multiple archives; their game load order is not established.");
                    continue;
                }
                Match match = candidates[0];
                try
                {
                    string loose = LoosePicture(match, gameRoot, discoveryRoot);
                    string sourceKey = loose == null ? match.Archive.Fingerprint : FileStamp(loose) + "\n" + FileHash(loose);
                    string directory = Path.Combine(cache, "pictures", Hash("picture-v1\n" + sourceKey + "\n" + toolKey + "\n" + match.Path));
                    Directory.CreateDirectory(directory);
                    string destination = Path.Combine(directory, pair.Key.ToString("x16", CultureInfo.InvariantCulture) + ".dds");
                    if (!CacheValid(destination) || !ValidDds(destination))
                    {
                        if (loose != null)
                        {
                            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                File.Copy(loose, temporary);
                                if (FileStamp(loose) + "\n" + FileHash(loose) != sourceKey) throw new IOException("The linked loose picture changed while it was being read.");
                                Replace(temporary, destination);
                            }
                            finally { Delete(temporary); }
                        }
                        else
                        {
                            string staging = Path.Combine(directory, "extract-" + Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(staging);
                            try
                            {
                                Report(progress, "Loading linked roster picture " + Path.GetFileNameWithoutExtension(match.Path) + "…");
                                var execution = await runner(Command(executable, 90000, "unpack-file", "--input", match.Archive.Path,
                                    "--file", match.Path, "--output", staging), token).ConfigureAwait(false);
                                string extracted = Path.Combine(staging, Path.ChangeExtension(match.Path.Replace('/', Path.DirectorySeparatorChar), ".dds"));
                                if (execution == null || execution.ExitCode != 0 || !ValidDds(extracted) || HasReparsePath(extracted, staging))
                                    throw new IOException("The linked texture could not be decoded from " + Path.GetFileName(match.Archive.Path) + ". " + ShortOutput(execution));
                                if (FileStamp(match.Archive.Path) != match.Archive.Fingerprint) throw new IOException("The archive changed while its linked picture was being extracted.");
                                Replace(extracted, destination);
                            }
                            finally { DeleteDirectory(staging); }
                        }
                        if (!ValidDds(destination)) throw new IOException("The archive reader did not produce a complete DDS roster picture.");
                        SaveValidation(destination);
                    }
                    result.Images.Add(pair.Key, destination);
                }
                catch (Exception error) when (IsFileError(error) || error is TimeoutException || error is System.ComponentModel.Win32Exception)
                { result.Warnings.Add("Linked picture " + pair.Key.ToString("x16", CultureInfo.InvariantCulture) + ": " + error.Message); }
            }
            result.Status = result.Images.Count + " linked roster picture(s) loaded from archive indexes."
                + (result.Conflicts.Count == 0 ? "" : " " + result.Conflicts.Count + " need a known archive load order.")
                + (required.Count > result.Images.Count + result.Conflicts.Count ? " Other picture references remain linked; their image data could not be read." : "");
            return result;
        }

        private static List<Archive> FindArchives(string game, string discovery, string cache, List<string> warnings, CancellationToken token)
        {
            var found = new Dictionary<string, Archive>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in new[] { discovery, game })
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                string fullRoot = Path.GetFullPath(root);
                var pending = new Stack<string>(); pending.Push(fullRoot);
                while (pending.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    if (!visited.Add(directory) || IsWithin(directory, cache)) continue;
                    try
                    {
                        if (HasReparsePath(directory, fullRoot)) continue;
                        foreach (string path in Directory.GetFiles(directory))
                            if (string.Equals(Path.GetExtension(path), ".cak", StringComparison.OrdinalIgnoreCase)
                                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                                found[path] = new Archive { Path = path, Root = fullRoot };
                        foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
                    }
                    catch (Exception error) when (IsFileError(error)) { warnings.Add("Could not inspect archive folder " + directory + ": " + error.Message); }
                }
            }
            return found.Values.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ReadMatches(string index, Archive archive, HashSet<ulong> required,
            Dictionary<ulong, List<Match>> matches, List<string> warnings)
        {
            if (new FileInfo(index).Length > MaximumIndexBytes) throw new IOException("The archive index exceeds the supported size.");
            foreach (string line in File.ReadLines(index))
            {
                if (line.Length > 8192 || line.StartsWith("//", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line)) continue;
                string[] fields = line.Split('|');
                ulong hash;
                if (fields.Length < 3 || fields[0].Length != 16 || !ulong.TryParse(fields[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash)
                    || !required.Contains(hash)) continue;
                string path;
                if (!TryPicturePath(fields[2], out path) || PathHash(path) != hash)
                { warnings.Add("Ignored an invalid linked-picture path in " + Path.GetFileName(archive.Path) + "."); continue; }
                List<Match> list;
                if (!matches.TryGetValue(hash, out list)) matches.Add(hash, list = new List<Match>());
                if (!list.Any(value => string.Equals(value.Archive.Path, archive.Path, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase))) list.Add(new Match { Archive = archive, Path = path });
            }
        }

        private static bool TryPicturePath(string input, out string path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(input) || input.Length > 1024 || input.IndexOfAny(new[] { ':', '\0', '"', '\r', '\n' }) >= 0) return false;
            string normalized = input.Replace('\\', '/');
            string[] parts = normalized.Split('/');
            if (parts.Length != 5 || !string.Equals(parts[0], "UI", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[1], "Assets", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[2], "Roster", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(parts[3], "TypeA", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(parts[4]), ".tex", StringComparison.OrdinalIgnoreCase)
                || parts[4].Length <= 4 || parts.Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal)
                    || part.EndsWith(".", StringComparison.Ordinal) || part.IndexOfAny(new[] { '<', '>', '|', '*', '?' }) >= 0)) return false;
            path = normalized; return true;
        }

        private static ulong PathHash(string path)
        {
            ulong value = 14695981039346656037UL;
            foreach (char c in path) value = unchecked((value ^ char.ToLowerInvariant(c)) * 1099511628211UL);
            return value;
        }

        private static string LoosePicture(Match match, string game, string discovery)
        {
            foreach (string root in new[] { match.Archive.Root, discovery, game }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string extension in new[] { ".dds", ".tex" })
                {
                    string path = Path.Combine(root, Path.ChangeExtension(match.Path.Replace('/', Path.DirectorySeparatorChar), extension));
                    if (File.Exists(path) && !HasReparsePath(path, root) && ValidDds(path)) return path;
                }
            }
            return null;
        }

        private static string PrepareTool(string appRoot, string gameRoot, string cache, out string key)
        {
            if (string.IsNullOrWhiteSpace(appRoot)) throw new IOException("Keep the original engine/bakery folder beside GorillaStudio.exe.");
            string source = Path.Combine(Path.GetFullPath(appRoot), "engine", "bakery");
            string executable = Path.Combine(source, "Bakery.CLI.exe");
            if (!File.Exists(executable) || HasReparsePath(executable, source)) throw new IOException("Keep the original engine/bakery/Bakery.CLI.exe beside GorillaStudio.exe.");
            var files = new List<string>();
            var pending = new Stack<string>(); pending.Push(source);
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                if (HasReparsePath(directory, source)) continue;
                foreach (string path in Directory.GetFiles(directory))
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) files.Add(path);
                foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
            }
            string oodle = string.IsNullOrWhiteSpace(gameRoot) ? null : Path.Combine(gameRoot, "oo2core_9_win64.dll");
            if (oodle != null && (!File.Exists(oodle) || HasReparsePath(oodle, gameRoot))) oodle = null;
            key = Hash(string.Join("\n", files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value =>
                value.Substring(source.Length) + "\n" + FileStamp(value) + "\n" + FileHash(value)))
                + "\n" + (oodle == null ? "no-game-oodle" : FileStamp(oodle) + "\n" + FileHash(oodle)));
            string destination = Path.Combine(cache, "bakery", key);
            foreach (string file in files)
            {
                string target = Path.Combine(destination, file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (!File.Exists(target) || FileHash(target) != FileHash(file)) File.Copy(file, target, true);
            }
            if (oodle != null)
            {
                string target = Path.Combine(destination, "oo2core_9_win64.dll");
                if (!File.Exists(target) || FileHash(target) != FileHash(oodle)) File.Copy(oodle, target, true);
            }
            return Path.Combine(destination, "Bakery.CLI.exe");
        }

        private static DiscoveryArchiveCommand Command(string executable, int timeout, params string[] arguments)
        {
            return new DiscoveryArchiveCommand { FileName = executable, Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable), TimeoutMilliseconds = timeout };
        }

        // The shipped setup_screens module documents Bakery 2.0.1's list-files
        // output-option crash. Its default output works, but writes beside its input.
        // Give it a short-lived, original-name cache hard link instead of permitting
        // any index or other sidecar in the user's installed game directory.
        private static async Task DefaultIndexFallbackAsync(string executable, Archive archive, string output, string directory,
            Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken token)
        {
            string staging = Path.Combine(directory, "index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string shadow = Path.Combine(staging, Path.GetFileName(archive.Path));
                if (!CreateCacheHardLink(shadow, archive.Path) && !CreateCacheSymbolicLink(shadow, archive.Path))
                    await CopyFallbackArchiveAsync(archive.Path, shadow, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (FileStamp(archive.Path) != archive.Fingerprint) throw new IOException("The archive changed while preparing its temporary index input.");
                var execution = await runner(Command(executable, 45000, "list-files", "--input", shadow, "--extended"), token).ConfigureAwait(false);
                string sidecar = Path.Combine(staging, Path.GetFileNameWithoutExtension(shadow) + "_files.txt");
                if (execution == null || execution.ExitCode != 0 || !File.Exists(sidecar))
                    throw new IOException("The archive reader could not index " + Path.GetFileName(archive.Path) + ". " + ShortOutput(execution));
                if (new FileInfo(sidecar).Length > MaximumIndexBytes || HasReparsePath(sidecar, staging))
                    throw new IOException("The archive reader produced an invalid temporary index.");
                Replace(sidecar, output);
            }
            finally { DeleteDirectory(staging); }
        }

        private static async Task CopyFallbackArchiveAsync(string sourcePath, string shadow, Action<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            long size = new FileInfo(sourcePath).Length;
            if (size > MaximumFallbackCopyBytes)
                throw new IOException("Bakery requires its default index output for this archive. Its cache must be on the same drive as the game; "
                    + "a cross-drive copy larger than 512 MiB was not started. You can instead extract its linked TypeA DDS pictures into Discovery data.");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(shadow)));
            if (drive.AvailableFreeSpace < size + 16L * 1024 * 1024)
                throw new IOException("There is not enough render-cache space for the archive reader's temporary cross-drive copy.");
            Report(progress, "Archive reader compatibility: copying " + Path.GetFileName(sourcePath) + " ("
                + ((size + 1048575) / 1048576).ToString(CultureInfo.InvariantCulture) + " MiB) into temporary cache…");
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, true))
            using (var destination = new FileStream(shadow, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1048576, true))
                await source.CopyToAsync(destination, 1048576, token).ConfigureAwait(false);
        }

        private static bool CreateCacheHardLink(string link, string source)
        {
            try
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? CreateHardLinkWindows(link, source, IntPtr.Zero) : CreateHardLinkUnix(source, link) == 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static bool CreateCacheSymbolicLink(string link, string source)
        {
            // Windows developer mode can permit cross-volume file links without
            // elevation. Failure is ordinary; never request administrator access.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            try { return CreateSymbolicLinkWindows(link, source, 2); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool CreateSymbolicLinkWindows(string newFileName, string existingFileName, int flags);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int CreateHardLinkUnix(string existingFileName, string newFileName);

        private static Task<DiscoveryArchiveProcessResult> RunProcessAsync(DiscoveryArchiveCommand command, CancellationToken token)
        {
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo { FileName = command.FileName,
                        Arguments = string.Join(" ", command.Arguments.Select(Quote)), WorkingDirectory = command.WorkingDirectory,
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    var output = new StringBuilder();
                    DataReceivedEventHandler capture = (sender, args) =>
                    {
                        if (args.Data == null) return;
                        lock (output) { if (output.Length < 65536) output.AppendLine(args.Data.Substring(0, Math.Min(args.Data.Length, 4096))); }
                    };
                    process.OutputDataReceived += capture; process.ErrorDataReceived += capture;
                    bool started = false;
                    try
                    {
                        process.Start(); started = true;
                        process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        var clock = Stopwatch.StartNew();
                        while (!process.WaitForExit(100))
                        {
                            token.ThrowIfCancellationRequested();
                            if (clock.ElapsedMilliseconds >= command.TimeoutMilliseconds) throw new TimeoutException("The archive reader timed out; selecting the character can be retried.");
                        }
                        token.ThrowIfCancellationRequested();
                        lock (output) return new DiscoveryArchiveProcessResult { ExitCode = process.ExitCode, Output = output.ToString() };
                    }
                    finally
                    {
                        if (started)
                        {
                            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
                            catch (InvalidOperationException) { }
                            catch (System.ComponentModel.Win32Exception) { }
                        }
                    }
                }
            }, token);
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            var text = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { text.Append('\\', slashes * 2 + 1).Append(c); slashes = 0; continue; }
                text.Append('\\', slashes).Append(c); slashes = 0;
            }
            return text.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static bool ValidDds(string path)
        {
            if (!File.Exists(path)) return false;
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length < 129 || stream.Length > MaximumPictureBytes) return false;
                using (var reader = new BinaryReader(stream))
                {
                    byte[] header = reader.ReadBytes(128);
                    if (BitConverter.ToUInt32(header, 0) != 0x20534444 || BitConverter.ToUInt32(header, 4) != 124
                        || BitConverter.ToUInt32(header, 76) != 32) return false;
                    uint height = BitConverter.ToUInt32(header, 12), width = BitConverter.ToUInt32(header, 16);
                    if (height == 0 || width == 0 || height > 16384 || width > 16384 || (long)height * width > 16777216) return false;
                    uint code = BitConverter.ToUInt32(header, 84); long minimum; int extra = 0;
                    if (code == 0)
                    {
                        uint bits = BitConverter.ToUInt32(header, 88); if (bits == 0 || bits > 128) return false;
                        minimum = ((long)width * bits + 7) / 8 * height;
                    }
                    else
                    {
                        int block;
                        if (code == 0x31545844 || code == 0x31495441 || code == 0x55344342 || code == 0x53344342) block = 8;
                        else if (code == 0x33545844 || code == 0x35545844 || code == 0x32495441 || code == 0x55354342 || code == 0x53354342) block = 16;
                        else if (code == 0x30315844)
                        {
                            if (stream.Length < 148) return false;
                            uint format = reader.ReadUInt32(); extra = 20;
                            if (format == 71 || format == 72 || format == 80 || format == 81) block = 8;
                            else if (new uint[] { 74, 75, 77, 78, 83, 84, 95, 96, 98, 99 }.Contains(format)) block = 16;
                            else if (format == 28 || format == 29 || format == 87 || format == 91) { minimum = (long)width * height * 4; return stream.Length >= 148 + minimum; }
                            else return false;
                        }
                        else return false;
                        minimum = ((long)width + 3) / 4 * (((long)height + 3) / 4) * block;
                    }
                    return stream.Length >= 128 + extra + minimum;
                }
            }
        }

        private static string FileStamp(string path)
        {
            var info = new FileInfo(path); return Path.GetFullPath(path) + "\n" + info.Length.ToString(CultureInfo.InvariantCulture)
                + "\n" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        private static string FileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
        private static string Hash(string value) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static bool CacheValid(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length <= MaximumIndexBytes && File.Exists(path + ".sha256")
                && new FileInfo(path + ".sha256").Length <= 128 && File.ReadAllText(path + ".sha256").Trim() == FileHash(path); }
            catch (Exception error) when (IsFileError(error)) { return false; }
        }
        private static void SaveValidation(string path) { File.WriteAllText(path + ".sha256", FileHash(path)); }
        private static void Replace(string source, string destination) { if (File.Exists(destination)) File.Delete(destination); File.Move(source, destination); }
        private static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static void DeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static bool IsWithin(string path, string root) { return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        private static bool HasReparsePath(string path, string root)
        {
            string full = Path.GetFullPath(path), boundary = Path.GetFullPath(root);
            if (!IsWithin(full, boundary)) return true;
            return HasExistingReparseAncestor(full);
        }
        private static bool HasExistingReparseAncestor(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        private static bool IsFileError(Exception error) { return error is IOException || error is UnauthorizedAccessException || error is ArgumentException
            || error is NotSupportedException || error is System.Security.SecurityException; }
        private static string ShortOutput(DiscoveryArchiveProcessResult value) { string text = value == null ? "No result was returned." : value.Output ?? "";
            return text.Length > 600 ? text.Substring(text.Length - 600) : text; }
        private static void Report(Action<string> progress, string value) { if (progress != null) progress(value); }
    }
}
