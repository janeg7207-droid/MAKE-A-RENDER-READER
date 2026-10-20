using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryMatchupSetupReport
    {
        public DiscoveryMatchupCatalogResult Catalog { get; internal set; } = new DiscoveryMatchupCatalogResult();
        public DiscoveryArchiveMatchupResult Resolution { get; internal set; } = new DiscoveryArchiveMatchupResult();
        public MatchCardLayoutsReport Layouts { get; internal set; } = new MatchCardLayoutsReport();
        public Dictionary<ulong, string> PreparedPaths { get; } = new Dictionary<ulong, string>();
        public int AddedScreens { get; internal set; }
        public int ScreensToAdd { get; internal set; }
        public int ResolvedScreens { get { return PreparedPaths.Count; } }
        public int FoundInArchives { get; internal set; }
        public bool IsCheck { get; internal set; }
        public bool BakeRequired { get; internal set; }
        public string ReportPath { get; internal set; }
        public List<string> Missing { get; } = new List<string>();
        public List<string> Issues { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public string Summary { get { return "Discovery matchup " + (IsCheck ? "check" : "setup") + ": "
            + Catalog.Entries.Count(entry => entry.AssetHashes.Count > 0) + " linked card record(s), "
            + Resolution.Assets.Count + "/" + Catalog.RequiredHashes.Count + " referenced project(s) found, "
            + ResolvedScreens + " prepared in Discovery, " + (IsCheck ? ScreensToAdd + " need copying" : AddedScreens + " copied") + ". "
            + (Missing.Count > 0 ? Missing.Count + " unresolved reference(s); some cards are unavailable. " : "")
            + (Issues.Count > 0 ? Issues.Count + " setup issue(s). " : "")
            + (BakeRequired ? (IsCheck ? "Run Game setup, then bake once in Discovery. " : "BAKE ONCE in Discovery, then restart the game. ") : "")
            + Layouts.Summary; } }
    }

    /// <summary>
    /// Joins the user's current arena/show records to exact loose or archived Scre
    /// projects, then completes supported movie slots in that selected data folder.
    /// An arena label never becomes a guessed filename or a replacement stock card.
    /// </summary>
    public static class DiscoveryMatchupSetup
    {
        public static Task<DiscoveryMatchupSetupReport> RunAsync(string appRoot, string gameRoot, string dataRoot,
            string cacheRoot, bool checkOnly, Action<string> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() => RunAsync(appRoot, gameRoot, dataRoot, cacheRoot, checkOnly, progress,
                DiscoveryArchiveRenders.AssetRunner, cancellationToken), cancellationToken);
        }

        // Uses the exact production catalog, lookup and layout code while allowing
        // portable tests to stand in for the shipped Windows Bakery executable.
        public static async Task<DiscoveryMatchupSetupReport> RunAsync(string appRoot, string gameRoot, string dataRoot,
            string cacheRoot, bool checkOnly, Action<string> progress, DiscoveryArchiveRunner runner, CancellationToken cancellationToken)
        {
            if (runner == null) throw new ArgumentNullException(nameof(runner));
            var report = new DiscoveryMatchupSetupReport { IsCheck = checkOnly, Layouts = new MatchCardLayoutsReport { IsCheck = checkOnly } };
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot))
                    throw new IOException("Choose the actual existing Discovery data folder.");
                string root = Path.GetFullPath(dataRoot);
                ValidateExisting(root);
                string cache = DiscoveryArchiveRenders.ValidateAssetCache(cacheRoot, gameRoot, root);
                Report(progress, "Reading all current Discovery arena and matchup references…");
                report.Catalog = DiscoveryMatchupCatalog.Read(root);
                report.Warnings.AddRange(report.Catalog.Warnings);
                cancellationToken.ThrowIfCancellationRequested();
                report.Resolution = await DiscoveryArchiveMatchups.FindAsync(appRoot, gameRoot, root,
                    report.Catalog.RequiredHashes, cache, progress, runner, cancellationToken).ConfigureAwait(false);
                report.Warnings.AddRange(report.Resolution.Warnings);
                var screens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // The catalog also inventories unrelated Scre menus. Only exact
                // linked projects authorize a custom outer node; Apply separately
                // discovers ordinary matchups by their measured hierarchy.
                foreach (ulong hash in report.Catalog.RequiredHashes.OrderBy(value => value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DiscoveryArchiveMatchupAsset asset;
                    if (report.Resolution.Conflicts.Contains(hash) || !report.Resolution.Assets.TryGetValue(hash, out asset))
                    {
                        report.Missing.Add(Describe(report.Catalog, hash) + (report.Resolution.Conflicts.Contains(hash) ? " — conflicting sources" : " — project not found"));
                        continue;
                    }
                    try
                    {
                        if (asset.Hash != hash) throw new InvalidDataException("Resolved project has a different asset reference.");
                        ValidateExisting(asset.Path);
                        if (!DiscoveryArchiveMatchups.IsScre(asset.Path)) throw new InvalidDataException("Resolved project is not a valid Scre project.");
                        if (!asset.IsLoose) report.FoundInArchives++;
                        // An exact canonical override and a hash-only export can
                        // be used where Discovery stores them. A wrapped export's
                        // known original asset path must instead be copied below;
                        // patching only its pack wrapper would not install that path.
                        if (UseLooseInPlace(asset, root))
                        {
                            string actual = SafeExisting(root, asset.SourcePath);
                            if (!DiscoveryArchiveMatchups.IsScre(actual)) throw new InvalidDataException("The selected loose project changed or is invalid.");
                            screens.Add(actual); report.PreparedPaths.Add(hash, actual); continue;
                        }
                        string relative;
                        if (!DiscoveryArchiveMatchups.TryAssetPath(asset.AssetPath, out relative)
                            || DiscoveryArchiveMatchups.HashPath(relative) != hash)
                            throw new InvalidDataException("Its exact original project path is unavailable; no install filename was guessed.");
                        string destination = SafeDestination(root, relative);
                        if (File.Exists(destination))
                        {
                            // Exact path/FNV identity establishes the active Discovery
                            // override. Preserve its edits even when archived bytes differ.
                            ValidateExisting(destination);
                            if (!DiscoveryArchiveMatchups.IsScre(destination)) throw new InvalidDataException("The existing Discovery project is invalid and was left unchanged.");
                            screens.Add(destination); report.PreparedPaths.Add(hash, destination); continue;
                        }
                        // Loose export bytes may have changed while other references
                        // were resolved from archives. Never publish an older cache
                        // over the user's current source, even during a read-only check.
                        if (asset.IsLoose) ValidateLooseSource(asset, asset.Path);
                        report.ScreensToAdd++;
                        if (checkOnly) { report.BakeRequired = true; continue; }
                        cancellationToken.ThrowIfCancellationRequested();
                        AddNew(root, destination, asset.Path, cancellationToken, asset.IsLoose ? asset : null);
                        screens.Add(destination); report.PreparedPaths.Add(hash, destination); report.AddedScreens++; report.BakeRequired = true;
                    }
                    catch (Exception error) when (Expected(error))
                    {
                        string message = Describe(report.Catalog, hash) + " — " + error.Message;
                        report.Issues.Add(message); report.Missing.Add(message);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, checkOnly ? "Checking available Discovery match-card layouts…" : "Completing available Discovery match-card layouts…");
                var wrappedSources = report.Resolution.WrappedSourcePaths.Where(path => IsWithin(path, root)).ToArray();
                report.Layouts = MatchCardLayouts.Apply(appRoot, root, checkOnly, screens, report.Catalog.ScreenFiles, wrappedSources);
                report.BakeRequired |= report.Layouts.BakeRequired;
                report.Issues.AddRange(report.Layouts.Failed);
                report.Issues.AddRange(report.Layouts.Unsupported);
                cancellationToken.ThrowIfCancellationRequested();
                SaveReport(cache, report);
            }
            catch (Exception error) when (Expected(error)) { report.Issues.Add(error.Message); }
            return report;
        }

        private static string Describe(DiscoveryMatchupCatalogResult catalog, ulong hash)
        {
            var owners = catalog.Entries.Where(entry => entry.AssetHashes.Contains(hash))
                .Select(entry => (string.IsNullOrWhiteSpace(entry.Name) ? "Matchup" : entry.Name) + " [ID " + entry.Id.ToString(CultureInfo.InvariantCulture) + "]");
            return string.Join("; ", owners) + " / " + hash.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static bool UseLooseInPlace(DiscoveryArchiveMatchupAsset asset, string root)
        {
            if (!asset.IsLoose || !IsWithin(asset.SourcePath, root)) return false;
            if (asset.AssetPath == null) return true;
            string relative = Path.GetFullPath(asset.SourcePath).Substring(root.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar).Length + 1).Replace('\\', '/');
            return string.Equals(relative, asset.AssetPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeExisting(string root, string path)
        {
            string full = Path.GetFullPath(path);
            if (!IsWithin(full, root)) throw new IOException("A matchup project is outside the selected Discovery folder: " + path);
            ValidateExisting(full);
            if (!File.Exists(full)) throw new IOException("A matchup project no longer exists: " + path);
            return full;
        }

        private static string SafeDestination(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(full, root)) throw new IOException("The referenced matchup path escaped the selected Discovery folder.");
            ValidateExisting(full); return full;
        }

        private static bool IsWithin(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            string full = Path.GetFullPath(path), prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateExisting(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("A referenced matchup path is empty.");
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A matchup project cannot use a linked file or folder: " + path);
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }

        private static void AddNew(string root, string destination, string source, CancellationToken token, DiscoveryArchiveMatchupAsset looseSource = null)
        {
            ValidateExisting(source); ValidateExisting(destination);
            if (!DiscoveryArchiveMatchups.IsScre(source)) throw new InvalidDataException("The cached source is not a valid Scre project.");
            string parent = Path.GetDirectoryName(destination);
            var missing = new Stack<string>();
            for (string current = parent; !Directory.Exists(current); current = Path.GetDirectoryName(current))
            {
                if (!IsWithin(current, root)) throw new IOException("A matchup output folder escaped the selected Discovery folder.");
                missing.Push(current);
            }
            while (missing.Count > 0)
            {
                token.ThrowIfCancellationRequested(); string directory = missing.Pop(); ValidateExisting(directory); Directory.CreateDirectory(directory); ValidateExisting(directory);
            }
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                token.ThrowIfCancellationRequested();
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                ValidateExisting(destination);
                if (!DiscoveryArchiveMatchups.IsScre(temporary)) throw new InvalidDataException("The copied matchup project is incomplete.");
                token.ThrowIfCancellationRequested();
                if (looseSource != null) ValidateLooseSource(looseSource, temporary);
                token.ThrowIfCancellationRequested();
                // Move has no overwrite: an existing custom file always wins.
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void ValidateLooseSource(DiscoveryArchiveMatchupAsset source, string verifiedCopy)
        {
            if (!DiscoveryArchiveMatchups.LooseSourceUnchanged(source))
                throw new InvalidDataException("The loose matchup sources changed after they were read; refresh setup before copying the project.");
            using (var copy = new FileStream(verifiedCopy, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
            {
                string actual = BitConverter.ToString(hash.ComputeHash(copy)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actual, source.SourceSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The verified matchup copy changed before publication; refresh setup before copying it.");
            }
        }

        private static void SaveReport(string cache, DiscoveryMatchupSetupReport report)
        {
            try
            {
                ValidateExisting(cache); Directory.CreateDirectory(cache);
                string path = Path.Combine(cache, "matchup-setup-report.txt");
                ValidateExisting(path);
                var lines = new List<string> { report.Summary, "", "Missing references:" };
                lines.AddRange(report.Missing); lines.Add(""); lines.Add("Setup issues:"); lines.AddRange(report.Issues);
                lines.Add(""); lines.Add("Warnings:"); lines.AddRange(report.Warnings);
                File.WriteAllLines(path, lines, new UTF8Encoding(false)); report.ReportPath = path;
            }
            catch (Exception error) when (Expected(error)) { report.Warnings.Add("Could not save matchup setup report: " + error.Message); }
        }

        private static void Report(Action<string> progress, string message) { if (progress != null) progress(message); }
        private static bool Expected(Exception error) { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
            || error is NotSupportedException || error is System.Security.SecurityException || error is OverflowException; }
    }
}
