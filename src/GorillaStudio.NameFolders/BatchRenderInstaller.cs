using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    public sealed class BatchRenderInstallReport
    {
        public int Installed { get; internal set; }
        public List<string> Failed { get; } = new List<string>();
        public string BackupDirectory { get; internal set; }
        public string Summary { get { return Installed.ToString(CultureInfo.InvariantCulture) + " render(s) installed"
            + (Failed.Count == 0 ? "." : "; " + Failed.Count.ToString(CultureInfo.InvariantCulture) + " problem(s) need attention."); } }
    }

    /// <summary>
    /// Installs an explicitly selected, completed batch. The UI must first enforce its
    /// normal native unlock check. No setup, authentication or rendering runs here.
    /// </summary>
    public static class BatchRenderInstaller
    {
        private const int SmallFileLimit = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex CompatiblePlugin = new Regex(@"MOVINGRENDERS-(4\.(2[1-9]|[3-9][0-9])|[5-9]\.)", RegexOptions.CultureInvariant);

        private sealed class PreparedJob
        {
            internal BatchRenderJob Job;
            internal string Source, Name, Hash;
        }

        public static BatchRenderInstallReport Install(string appRoot, string gameRoot, IEnumerable<BatchRenderJob> jobs)
        {
            var report = new BatchRenderInstallReport();
            try
            {
                if (string.IsNullOrWhiteSpace(appRoot) || string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
                    throw new IOException("Choose the application and installed WWE game folders.");
                string app = Path.GetFullPath(appRoot), game = Path.GetFullPath(gameRoot);
                string plugins = Path.Combine(game, "Plugins"), destination = Path.Combine(plugins, "GorillaRenders");
                if (!Directory.Exists(plugins)) throw new IOException("The game has no Plugins folder. Install CakeHook first.");
                SafePath(game, destination);
                if (Process.GetProcessesByName("WWE2K26_x64").Length != 0)
                    throw new IOException("Close WWE 2K26 before installing the batch.");
                var rows = (jobs ?? Enumerable.Empty<BatchRenderJob>()).ToList();
                if (rows.Count == 0) throw new IOException("There are no completed batch renders to install.");
                if (rows.Any(job => job == null || job.Entry == null)) throw new IOException("A batch entry has no wrestler information.");
                int[] duplicates = rows.GroupBy(job => job.Entry.Id).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
                if (duplicates.Length != 0) throw new IOException("More than one batch render uses wrestler ID " + string.Join(", ", duplicates) + ". Choose one render for each slot.");

                var prepared = new List<PreparedJob>();
                byte[] agreedMap = null;
                string mapSource = null, mapHash = null;
                bool? hasMap = null;
                foreach (BatchRenderJob job in rows)
                {
                    string source = string.IsNullOrWhiteSpace(job.RenderPath) ? null : Path.GetFullPath(job.RenderPath);
                    string sourceHash = source != null && File.Exists(source) ? Hash(source) : null;
                    string reason;
                    if (!BatchRenderPlan.VerifyCompleted(job.RenderPath, job.Entry.Id, job.OutputDirectory, out reason))
                        throw new IOException(Label(job) + ": " + reason);
                    if (sourceHash == null || Hash(source) != sourceHash) throw new IOException(Label(job) + ": the completed render changed during validation.");
                    string name = Path.GetFileName(source);
                    Match prefix = Regex.Match(name, @"^(\d+)_.*\.gmr$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    int filenameId;
                    if (!prefix.Success || !int.TryParse(prefix.Groups[1].Value, out filenameId) || filenameId != job.Entry.Id)
                        throw new IOException(Label(job) + ": the render filename does not match its verified wrestler ID.");
                    SafePath(Path.GetFullPath(job.OutputDirectory), source);
                    if (Inside(game, source)) throw new IOException("Batch sources must remain outside the installed game folder.");
                    prepared.Add(new PreparedJob { Job = job, Source = source, Name = name, Hash = sourceHash });
                    string map = Path.Combine(Path.GetDirectoryName(source), "_roster_pictures.txt");
                    bool present = File.Exists(map);
                    if (hasMap.HasValue && hasMap.Value != present)
                        throw new IOException("The completed jobs do not share the same roster picture map. Make the batch again from one Discovery roster.");
                    hasMap = present;
                    if (!present) continue;
                    SafePath(Path.GetFullPath(job.OutputDirectory), map);
                    string currentMapHash = Hash(map);
                    byte[] current = NormalizeMap(ReadSmall(map));
                    if (Hash(map) != currentMapHash) throw new IOException("A roster picture map changed during validation. Make the batch again.");
                    if (agreedMap != null && !agreedMap.SequenceEqual(current))
                        throw new IOException("The completed jobs contain conflicting roster picture maps. Nothing was installed.");
                    agreedMap = current; mapSource = map; mapHash = currentMapHash;
                }

                // Validate every source and shared context before creating any game files.
                string plugin = Path.Combine(plugins, "WWE2K26_MovingRenders.dlp");
                string config = Path.Combine(plugins, "WWE2K26_MovingRenders.cfg");
                bool updatePlugin = !File.Exists(plugin) || !CompatiblePlugin.IsMatch(Encoding.ASCII.GetString(ReadSmall(plugin)));
                string suppliedPlugin = Path.Combine(app, "plugin", "WWE2K26_MovingRenders.dlp");
                string suppliedConfig = Path.Combine(app, "plugin", "WWE2K26_MovingRenders.cfg");
                if (updatePlugin && (!File.Exists(suppliedPlugin) || !CompatiblePlugin.IsMatch(Encoding.ASCII.GetString(ReadSmall(suppliedPlugin)))
                    || !File.Exists(suppliedConfig))) throw new IOException("The supplied Moving Renders plugin or configuration is missing or outdated. Extract the full app ZIP.");
                if ((updatePlugin || !File.Exists(config)) && !File.Exists(suppliedConfig))
                    throw new IOException("The supplied Moving Renders configuration is missing. Extract the full app ZIP.");
                if ((updatePlugin || !File.Exists(config)) && ReadSmall(suppliedConfig).Length == 0)
                    throw new IOException("The supplied Moving Renders configuration is empty. Extract the full app ZIP.");
                SafePath(game, plugin); SafePath(game, config);
                if (updatePlugin)
                {
                    InstallFile(game, suppliedPlugin, plugin, Hash(suppliedPlugin), report);
                    InstallFile(game, suppliedConfig, config, Hash(suppliedConfig), report);
                }
                else if (!File.Exists(config)) InstallFile(game, suppliedConfig, config, Hash(suppliedConfig), report);
                Directory.CreateDirectory(destination);

                foreach (PreparedJob item in prepared)
                {
                    try
                    {
                        string target = Path.Combine(destination, item.Name);
                        var older = Directory.GetFiles(destination, "*", SearchOption.TopDirectoryOnly)
                            .Where(path => IsSlotRender(path, item.Job.Entry.Id) && !SamePath(path, target)).ToArray();
                        var oldHashes = older.ToDictionary(path => path, Hash, StringComparer.OrdinalIgnoreCase);
                        foreach (string old in older) Backup(game, old, oldHashes[old], report);
                        InstallFile(game, item.Source, target, item.Hash, report);
                        foreach (string old in older)
                        {
                            SafePath(game, old);
                            if (Hash(old) != oldHashes[old]) throw new IOException("An older installed render changed during installation and was preserved: " + Path.GetFileName(old));
                            File.Delete(old);
                        }
                        report.Installed++;
                    }
                    catch (Exception error) when (Expected(error)) { report.Failed.Add(Label(item.Job) + ": " + error.Message); }
                }
                if (report.Installed > 0 && mapSource != null)
                    try { InstallFile(game, mapSource, Path.Combine(destination, "_roster_pictures.txt"), mapHash, report); }
                    catch (Exception error) when (Expected(error)) { report.Failed.Add("The renders were copied, but their roster picture map could not be installed: " + error.Message); }
            }
            catch (Exception error) when (Expected(error)) { report.Failed.Add(error.Message); }
            return report;
        }

        private static string Label(BatchRenderJob job) { return (job.Entry.Name ?? "Wrestler") + " (" + job.Entry.Id.ToString(CultureInfo.InvariantCulture) + ")"; }
        private static bool IsSlotRender(string path, int id)
        {
            Match match = Regex.Match(Path.GetFileName(path), @"^(\d+)_.*\.gmr$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int found; return match.Success && int.TryParse(match.Groups[1].Value, out found) && found == id;
        }

        private static void InstallFile(string root, string source, string destination, string expectedSource, BatchRenderInstallReport report)
        {
            SafePath(root, destination);
            string before = File.Exists(destination) ? Hash(destination) : null;
            if (Hash(source) != expectedSource) throw new IOException("A batch source changed after validation: " + Path.GetFileName(source));
            if (before == expectedSource) return;
            if (before != null) Backup(root, destination, before, report);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".gorilla-batch-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
                if (Hash(temporary) != expectedSource || Hash(source) != expectedSource)
                    throw new IOException("A batch source changed while it was being copied: " + Path.GetFileName(source));
                SafePath(root, destination);
                if (before == null)
                {
                    if (File.Exists(destination)) throw new IOException("A newer installed file appeared and was preserved: " + Path.GetFileName(destination));
                    File.Move(temporary, destination);
                }
                else
                {
                    if (!File.Exists(destination) || Hash(destination) != before)
                        throw new IOException("An installed file changed during installation and was preserved: " + Path.GetFileName(destination));
                    File.Replace(temporary, destination, null);
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void Backup(string root, string file, string expected, BatchRenderInstallReport report)
        {
            SafePath(root, file);
            if (report.BackupDirectory == null) report.BackupDirectory = Path.Combine(root, "Plugins", "GorillaRenderBatchBackups",
                DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string relative = Path.GetFullPath(file).Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1);
            string target = Path.Combine(report.BackupDirectory, relative + ".bak");
            SafePath(root, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (!File.Exists(target)) File.Copy(file, target, false);
            if (Hash(target) != expected || Hash(file) != expected) throw new IOException("An installed file changed while its backup was being made: " + Path.GetFileName(file));
        }

        private static byte[] NormalizeMap(byte[] bytes)
        {
            string text = Utf8.GetString(bytes).TrimStart('\ufeff');
            var rows = new SortedDictionary<ulong, SortedSet<int>>();
            foreach (string input in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] words = input.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                ulong hash;
                if (words.Length < 2 || words[0].Length != 16 || !ulong.TryParse(words[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hash) || hash == 0)
                    throw new InvalidDataException("A completed render contains an invalid roster picture map.");
                SortedSet<int> ids;
                if (!rows.TryGetValue(hash, out ids)) rows[hash] = ids = new SortedSet<int>();
                foreach (string word in words.Skip(1))
                {
                    int id;
                    if (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1 || id > 65535)
                        throw new InvalidDataException("A completed render contains an invalid roster picture owner ID.");
                    ids.Add(id);
                }
            }
            return Utf8.GetBytes(string.Join("\n", rows.Select(pair => pair.Key.ToString("X16", CultureInfo.InvariantCulture)
                + " " + string.Join(" ", pair.Value))) + (rows.Count == 0 ? "" : "\n"));
        }

        private static byte[] ReadSmall(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > SmallFileLimit) throw new IOException("A plugin or roster picture map is missing or too large: " + path);
            return File.ReadAllBytes(path);
        }
        private static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream));
        }
        private static bool SamePath(string left, string right) { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        private static bool Inside(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(path);
            return string.Equals(fullRoot, full, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        private static void SafePath(string root, string path)
        {
            if (!Inside(root, path)) throw new IOException("The installation path escaped its selected folder.");
            string current = Path.GetFullPath(path), boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (current != null && Inside(boundary, current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("A linked installation path was left alone: " + current); }
                catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
                if (SamePath(current, boundary)) break;
                current = Path.GetDirectoryName(current);
            }
        }
        private static bool Expected(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException
                || error is NotSupportedException || error is FormatException || error is OverflowException || error is System.Security.SecurityException;
        }
    }
}
