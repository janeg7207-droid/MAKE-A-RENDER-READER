using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    public sealed class MatchCardLayoutsReport
    {
        public int Patched { get; internal set; }
        public int Already { get; internal set; }
        public int AddedMovies { get; internal set; }
        public int AddedSlots { get; internal set; }
        public int RepairedSlots { get; internal set; }
        public int CheckedSlots { get; internal set; }
        public bool IsCheck { get; internal set; }
        public bool BakeRequired { get; internal set; }
        public string BackupDirectory { get; internal set; }
        public List<string> Unsupported { get; } = new List<string>();
        public List<string> Failed { get; } = new List<string>();
        public string Summary { get { return (IsCheck ? "Match card check: " : "Match card setup: ")
            + Patched + (IsCheck ? " layout file(s) need repair, " : " layout file(s) repaired, ")
            + AddedSlots + " missing movie slot(s), " + AddedMovies + (IsCheck ? " missing placeholder movie(s)." : " placeholder movie(s) added.")
            + (CheckedSlots > 0 ? " " + CheckedSlots + " supported player slot(s) checked." : "")
            + (RepairedSlots > 0 ? " " + RepairedSlots + (IsCheck ? " existing movie slot(s) need repair." : " existing movie slot(s) repaired.") : "")
            + (BakeRequired ? (IsCheck ? " Run Game setup, then bake once in Discovery." : " BAKE ONCE in Discovery, then restart the game.") : "")
            + (Failed.Count + Unsupported.Count > 0 ? " " + (Failed.Count + Unsupported.Count) + " setup issue(s) need attention." : ""); } }
    }

    /// <summary>
    /// Completes each actual match-up slot independently. The original setup stops at
    /// the first GM_clip anywhere in a file and only covers ten of twenty layouts.
    /// This uses the same measured Scre schema and append-only movie-layer recipe.
    /// </summary>
    public static class MatchCardLayouts
    {
        private const int Limit = 64 * 1024 * 1024;
        private static readonly string[] Layouts = { "1on1", "1on2", "1on3", "1on4", "1on5", "1on6", "1on7", "2on2", "2on3", "3on3", "4on4", "5man", "6man", "8man", "fatal_4way", "promo_1", "promo_2", "triple_threat", "triple_threat_tag", "fatal_4way_tag" };
        private static readonly int[] Slots = { 2, 3, 4, 5, 6, 7, 8, 4, 5, 6, 8, 5, 6, 8, 4, 1, 2, 3, 6, 8 };
        private static readonly string[] ScreenDirectories = { "UI/Projects/ShowAssets/matchups", "UI/Projects/MatchUp", "UI/Projects/2K22_show_assets/matchup_screens" };
        private static readonly HashSet<int> ScalarSlots = new HashSet<int> { 0, 2, 30, 39, 51, 52, 53 };
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static MatchCardLayoutsReport Apply(string appRoot, string discoveryRoot, bool checkOnly)
        {
            return Apply(appRoot, discoveryRoot, checkOnly, null);
        }

        /// <summary>
        /// Exact catalog-resolved screen paths can authorize a custom outer node name.
        /// Every path must remain inside the selected data folder; the measured layout,
        /// complete player groups and supported leaf recipe are still required.
        /// </summary>
        public static MatchCardLayoutsReport Apply(string appRoot, string discoveryRoot, bool checkOnly, IEnumerable<string> explicitScreenPaths)
        {
            return Apply(appRoot, discoveryRoot, checkOnly, explicitScreenPaths, null);
        }

        /// <summary>
        /// Catalog candidates extend discovery to other folders, but must retain
        /// the proven matchup hierarchy. Only explicit referenced screens may
        /// authorize a custom outer node name.
        /// </summary>
        public static MatchCardLayoutsReport Apply(string appRoot, string discoveryRoot, bool checkOnly,
            IEnumerable<string> explicitScreenPaths, IEnumerable<string> candidateScreenPaths)
        {
            return Apply(appRoot, discoveryRoot, checkOnly, explicitScreenPaths, candidateScreenPaths, null);
        }

        // Verified pack-export sources must remain unchanged after their canonical
        // project is copied. Exclusions only remove paths; they grant no authority.
        public static MatchCardLayoutsReport Apply(string appRoot, string discoveryRoot, bool checkOnly,
            IEnumerable<string> explicitScreenPaths, IEnumerable<string> candidateScreenPaths, IEnumerable<string> excludedScreenPaths)
        {
            var report = new MatchCardLayoutsReport { IsCheck = checkOnly };
            try
            {
                if (string.IsNullOrWhiteSpace(appRoot) || string.IsNullOrWhiteSpace(discoveryRoot) || !Directory.Exists(discoveryRoot))
                    throw new IOException("The app and actual Discovery data folders are required.");
                string root = Path.GetFullPath(discoveryRoot);
                string templatePath = Path.Combine(appRoot, "data", "gamepack", "screens", "template_matchup.jsfb");
                var template = new Screen(ReadSmall(templatePath));
                int movie = template.Nodes.Single(node => node.Path.SequenceEqual(new[] { "bg" })).Position;
                if (template.ByteField(movie, 2) != 7 || template.ByteField(movie, 51) != 1)
                    throw new InvalidDataException("The supplied match-card movie template is not a playing movie layer.");
                byte[] placeholder = ValidatePlaceholders(appRoot);
                var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var explicitFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string sub in ScreenDirectories)
                {
                    string directory = Path.Combine(root, sub.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory)) continue;
                    foreach (string file in WalkFiles(directory))
                    {
                        string name = Path.GetFileName(file);
                        if (name.EndsWith(".jsfb", StringComparison.OrdinalIgnoreCase) && !name.Equals("matchup_bg.jsfb", StringComparison.OrdinalIgnoreCase)
                            && (name.StartsWith("matchup", StringComparison.OrdinalIgnoreCase) || name.EndsWith("match_card.jsfb", StringComparison.OrdinalIgnoreCase)))
                        { files.Add(file); knownNames.Add(file); }
                    }
                }
                string projects = Path.Combine(root, "UI", "Projects");
                if (Directory.Exists(projects))
                    foreach (string file in WalkFiles(projects))
                        if (file.EndsWith(".jsfb", StringComparison.OrdinalIgnoreCase) && HasScreenHeader(file)) files.Add(file);
                AddCatalogPaths(root, explicitScreenPaths, files, explicitFiles, report);
                AddCatalogPaths(root, candidateScreenPaths, files, null, report);
                var excludedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                AddCatalogPaths(root, excludedScreenPaths, excludedFiles, null, report);
                files.ExceptWith(excludedFiles);
                int recognized = 0;
                foreach (string file in files)
                {
                    bool relevant = explicitFiles.Contains(file) || knownNames.Contains(file);
                    try
                    {
                        ValidateInputPath(root, file);
                        byte[] original = ReadSmall(file);
                        var source = new Screen(original);
                        bool explicitScreen = explicitFiles.Contains(file);
                        if (!relevant && !HasMatchupLayout(source)) continue;
                        relevant = true;
                        if (HasPlayerGroups(source, explicitScreen)) recognized++;
                        int added, repaired, checkedSlots; List<string> unsupported;
                        byte[] patched = Patch(source, template, movie, explicitScreen, out added, out repaired, out checkedSlots, out unsupported);
                        report.CheckedSlots += checkedSlots;
                        foreach (string message in unsupported) report.Unsupported.Add(Relative(root, file) + ": " + message);
                        if (added == 0 && repaired == 0) { if (unsupported.Count == 0) report.Already++; continue; }
                        if (!checkOnly) ReplaceBackedUp(root, file, original, patched, report);
                        report.Patched++; report.AddedSlots += added; report.RepairedSlots += repaired; report.BakeRequired = true;
                    }
                    catch (Exception error) when (Expected(error))
                    { if (relevant) report.Failed.Add(Relative(root, file) + ": " + error.Message); }
                }
                if (recognized > 0)
                    for (int layout = 0; layout < Layouts.Length; layout++)
                        for (int player = 1; player <= Slots[layout]; player++)
                        {
                            string file = Path.Combine(root, MoviePath(layout, player).Replace('/', Path.DirectorySeparatorChar));
                            try
                            {
                                if (File.Exists(file))
                                {
                                    // Keep custom video content, but its header still has
                                    // to identify this exact layout and player to the plugin.
                                    if (!PlaceholderIdentity(file, layout, player))
                                        report.Unsupported.Add(Relative(root, file) + ": existing movie has an unknown layout/player identity; custom bytes were left unchanged.");
                                    continue;
                                }
                                byte[] bytes = Placeholder(placeholder, layout, player);
                                if (!checkOnly) AddNew(root, file, bytes);
                                report.AddedMovies++; report.BakeRequired = true;
                            }
                            catch (Exception error) when (Expected(error)) { report.Failed.Add(Relative(root, file) + ": " + error.Message); }
                        }
            }
            catch (Exception error) when (Expected(error)) { report.Failed.Add(error.Message); }
            return report;
        }

        private static bool HasMatchupLayout(Screen screen)
        {
            return screen.Nodes.Any(node => node.Path.Count >= 2 && Layouts.Contains(node.Path[node.Path.Count - 1])
                && node.Path.Take(node.Path.Count - 1).Contains("matchup"));
        }

        private static void AddCatalogPaths(string root, IEnumerable<string> paths, SortedSet<string> files,
            HashSet<string> explicitFiles, MatchCardLayoutsReport report)
        {
            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) throw new IOException("A catalog screen path is empty.");
                    string portable = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    string full = Path.GetFullPath(Path.IsPathRooted(portable) ? portable : Path.Combine(root, portable));
                    string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(prefix, PathComparison))
                        throw new IOException("A catalog screen path is outside the selected Discovery folder: " + path);
                    ValidateInputPath(root, full);
                    if (!File.Exists(full)) throw new IOException("A catalog screen path is not an existing file: " + path);
                    files.Add(full);
                    if (explicitFiles != null) explicitFiles.Add(full);
                }
                catch (Exception error) when (Expected(error)) { report.Failed.Add(error.Message); }
            }
        }

        private static bool PlayerGroup(Node node, string layout, bool explicitScreen)
        {
            int count = node.Path.Count;
            if (count < 2 || node.Path[count - 2] != layout) return false;
            string player = node.Path[count - 1];
            return player.Length == 2 && player[0] >= '1' && player[0] <= '8' && player[1] == 'P'
                && (explicitScreen || node.Path.Take(count - 2).Contains("matchup"));
        }

        private static bool HasPlayerGroups(Screen source, bool explicitScreen)
        {
            return source.Nodes.Any(node => Layouts.Any(layout => PlayerGroup(node, layout, explicitScreen)));
        }

        private static byte[] Patch(Screen source, Screen template, int movie, bool explicitScreen,
            out int added, out int repaired, out int checkedSlots, out List<string> unsupported)
        {
            added = 0; repaired = 0; checkedSlots = 0; unsupported = new List<string>();
            if (!HasPlayerGroups(source, explicitScreen))
                unsupported.Add("No supported match-up player-slot groups were found; left unchanged.");
            var clone = new Copier(source.Bytes);
            var changes = new HashSet<int>();
            for (int layout = 0; layout < Layouts.Length; layout++)
            {
                var containers = source.Nodes.Where(node => PlayerGroup(node, Layouts[layout], explicitScreen)).GroupBy(node => node.Parent);
                foreach (var container in containers)
                {
                    var groups = container.ToList();
                    string label = string.Join("/", groups[0].Path.Take(groups[0].Path.Count - 1));
                    if (groups.Count != Slots[layout] || Enumerable.Range(1, Slots[layout]).Any(player => groups.Count(node => node.Path.Last() == player + "P") != 1))
                    { unsupported.Add(label + " has an unexpected player-slot structure; left unchanged."); continue; }
                    checkedSlots += groups.Count;
                    foreach (Node group in groups)
                    {
                        int[] children = source.Children(group.Position);
                        string[] names = children.Select(source.Name).ToArray();
                        int player = int.Parse(group.Path.Last().Substring(0, 1), CultureInfo.InvariantCulture);
                        int[] existingMovies = children.Where(child => source.Name(child) == "GM_clip").ToArray();
                        bool repairExisting = existingMovies.Length > 0;
                        if (existingMovies.Length > 0)
                        {
                            if (existingMovies.Length != 1 || !ExpectedMovie(source, existingMovies[0], layout, player))
                            {
                                unsupported.Add(string.Join("/", group.Path) + ": existing GM_clip is not an autoplay movie for this layout/player asset; existing bytes were left unchanged.");
                                continue;
                            }
                            if (children.Length != 3 || names[0] != "CAS_dummy" || names[1] != "SS_dummy" || names[2] != "GM_clip"
                                || source.ByteField(children[0], 2) != 2 || source.ByteField(children[1], 2) != 2
                                || children.Any(child => !EmptyNodeLists(source, child)))
                            {
                                unsupported.Add(string.Join("/", group.Path) + ": the existing movie has custom or unsupported slot children; its marker or transform was not counted as complete, and existing bytes were left unchanged.");
                                continue;
                            }
                            if (ExpectedMarker(source, children[1], layout, player)
                                && ExpectedTransform(source, existingMovies[0])) continue;
                        }
                        else if (children.Length != 2 || names[0] != "CAS_dummy" || names[1] != "SS_dummy"
                            || children.Any(child => source.ByteField(child, 2) != 2))
                        { unsupported.Add(string.Join("/", group.Path) + " has custom slot children; left unchanged."); continue; }
                        // Validate every clone before changing the original child pointer.
                        // Unsupported non-leaf/mod schemas leave the complete input unchanged.
                        try
                        {
                            int vector = clone.Allocate(16); clone.Put32(vector, 3);
                            int cas = clone.Table(source, children[0], true);
                            int still = clone.Table(source, children[1], true);
                            int moving = repairExisting ? clone.Table(source, existingMovies[0], true) : clone.Table(template, movie, true);
                            clone.Name(moving, "GM_clip"); clone.Asset(moving, Hash(MoviePath(layout, player)));
                            clone.Floats(moving, 3, 0f, -551f); clone.Floats(moving, 4, 1024f, 1024f);
                            clone.Floats(moving, 5, 1102f / 1024f, 1102f / 1024f);
                            float marker = 1f + (8 * layout + player) / 1048576f;
                            clone.Floats(still, 5, marker, marker);
                            clone.Point(vector + 4, cas); clone.Point(vector + 8, still); clone.Point(vector + 12, moving);
                            int childField = source.Field(group.Position, 55); clone.Point(childField, vector);
                            changes.Add(childField);
                            if (repairExisting) repaired++; else added++;
                        }
                        catch (InvalidDataException error) { unsupported.Add(string.Join("/", group.Path) + ": " + error.Message); }
                    }
                }
            }
            if (added == 0 && repaired == 0) return source.Bytes;
            byte[] result = clone.Result();
            // Existing bytes can change only in the original group's child-vector
            // pointer; all replacement nodes, strings and scalar tables are appended.
            var changedBytes = new HashSet<int>(changes.SelectMany(field => Enumerable.Range(field, 4)));
            for (int index = 0; index < source.Bytes.Length; index++)
                if (source.Bytes[index] != result[index] && !changedBytes.Contains(index))
                    throw new InvalidDataException("A non-slot screen byte changed; refusing the layout update.");
            var verified = new Screen(result);
            foreach (Node group in verified.Nodes.Where(node => node.Position < source.Bytes.Length && changes.Contains(source.Field(node.Position, 55))))
            {
                int[] children = verified.Children(group.Position);
                if (children.Length != 3 || verified.Name(children[2]) != "GM_clip" || verified.ByteField(children[2], 2) != 7 || verified.ByteField(children[2], 51) != 1)
                    throw new InvalidDataException("The completed slot did not retain its playing movie layer.");
                int layout = Array.IndexOf(Layouts, group.Path[group.Path.Count - 2]);
                int player = int.Parse(group.Path.Last().Substring(0, 1), CultureInfo.InvariantCulture);
                if (!ExpectedMarker(verified, children[1], layout, player) || !ExpectedTransform(verified, children[2]))
                    throw new InvalidDataException("The completed slot did not retain its exact plugin marker and movie transform.");
            }
            return result;
        }

        private static bool ExpectedMovie(Screen source, int movie, int layout, int player)
        {
            if (source.ByteField(movie, 2) != 7 || source.ByteField(movie, 51) != 1) return false;
            int asset = source.Field(movie, 0);
            if (asset == 0 || (long)asset + 8 > (long)movie + source.ShapeOf(movie).Size) return false;
            ulong actual = U32(source.Bytes, asset) | (ulong)U32(source.Bytes, asset + 4) << 32;
            return actual == Hash(MoviePath(layout, player));
        }

        private static bool ExpectedMarker(Screen source, int still, int layout, int player)
        {
            float marker = 1f + (8 * layout + player) / 1048576f;
            return ExpectedFloats(source, still, 5, marker, marker);
        }

        private static bool EmptyNodeLists(Screen source, int table)
        {
            foreach (int property in new[] { 55, 56 })
            {
                int field = source.Field(table, property);
                if (field != 0 && U32(source.Bytes, source.Target(field)) != 0) return false;
            }
            return true;
        }

        private static bool ExpectedTransform(Screen source, int movie)
        {
            return ExpectedFloats(source, movie, 3, 0f, -551f)
                && ExpectedFloats(source, movie, 4, 1024f, 1024f)
                && ExpectedFloats(source, movie, 5, 1102f / 1024f, 1102f / 1024f);
        }

        private static bool ExpectedFloats(Screen source, int table, int property, float first, float second)
        {
            int field = source.Field(table, property);
            if (field == 0) return false;
            int value = source.Target(field), a = source.Field(value, 0), b = source.Field(value, 1);
            int end = checked(value + source.ShapeOf(value).Size);
            if (a == 0 || b == 0 || a == b || (long)a + 4 > end || (long)b + 4 > end) return false;
            return U32(source.Bytes, a) == BitConverter.ToUInt32(BitConverter.GetBytes(first), 0)
                && U32(source.Bytes, b) == BitConverter.ToUInt32(BitConverter.GetBytes(second), 0);
        }

        private static byte[] ValidatePlaceholders(string appRoot)
        {
            string directory = Path.Combine(appRoot, "data", "gamepack", "screens", "BakeMe", "UI", "Assets", "movies", "gorilla_renders");
            byte[] basis = ReadSmall(Path.Combine(directory, "ph_L00_1P.bk2"));
            if (basis.Length != 1312 || Encoding.ASCII.GetString(basis, 0, 4) != "BIKi" || U32(basis, 20) != 32 || U32(basis, 24) != 32
                || U32(basis, 28) != 960000 || U32(basis, 32) != 32032)
                throw new InvalidDataException("The supplied placeholder movie has an unknown format; no layout files were changed.");
            foreach (string file in Directory.GetFiles(directory, "ph_*.bk2"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                int layout, player;
                if (stem.Length < 9 || !int.TryParse(stem.Substring(4, 2), out layout) || !int.TryParse(stem.Substring(7, stem.Length - 8), out player)
                    || layout < 0 || layout >= Layouts.Length || player < 1 || player > Slots[layout])
                    throw new InvalidDataException("The supplied placeholder movie name is unknown: " + stem);
                if (!ReadSmall(file).SequenceEqual(Placeholder(basis, layout, player)))
                    throw new InvalidDataException("The supplied placeholder movies do not share the validated layout/player encoding.");
            }
            return basis;
        }

        private static byte[] Placeholder(byte[] basis, int layout, int player)
        {
            byte[] result = (byte[])basis.Clone();
            uint multiplier = (uint)(31 + layout * 8 + player);
            Put32(result, 28, 30000 * multiplier); Put32(result, 32, 1001 * multiplier);
            return result;
        }

        private static bool PlaceholderIdentity(string path, int layout, int player)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var header = new byte[36];
                int position = 0;
                while (position < header.Length) { int count = stream.Read(header, position, header.Length - position); if (count == 0) return false; position += count; }
                uint multiplier = (uint)(31 + layout * 8 + player);
                return Encoding.ASCII.GetString(header, 0, 4) == "BIKi" && U32(header, 28) == 30000 * multiplier && U32(header, 32) == 1001 * multiplier;
            }
        }

        private static string MoviePath(int layout, int player) { return "UI/Assets/movies/gorilla_renders/ph_L" + layout.ToString("00", CultureInfo.InvariantCulture) + "_" + player.ToString(CultureInfo.InvariantCulture) + "P.bk2"; }
        private static ulong Hash(string value) { ulong hash = 14695981039346656037; foreach (byte b in Encoding.UTF8.GetBytes(value.ToLowerInvariant())) { hash ^= b; hash *= 1099511628211; } return hash; }
        private static byte[] ReadSmall(string path) { var file = new FileInfo(path); if (!file.Exists || file.Length < 8 || file.Length > Limit) throw new InvalidDataException("Missing or oversized screen resource: " + path); return File.ReadAllBytes(path); }
        private static string Relative(string root, string path) { return path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1) : path; }
        private static bool Expected(Exception error) { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException; }

        private static bool HasScreenHeader(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < 12 || stream.Length > Limit) return false;
                    byte[] header = new byte[8];
                    return stream.Read(header, 0, header.Length) == header.Length && Encoding.ASCII.GetString(header, 4, 4) == "Scre";
                }
            }
            catch (Exception error) when (Expected(error)) { return false; }
        }

        private static void ValidateInputPath(string root, string file)
        {
            string current = Path.GetFullPath(file), boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (current != null && (string.Equals(current, boundary, PathComparison)
                || current.StartsWith(boundary + Path.DirectorySeparatorChar, PathComparison)))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A linked catalog screen path was not followed: " + current);
                if (string.Equals(current, boundary, PathComparison)) return;
                current = Path.GetDirectoryName(current);
            }
            throw new IOException("A screen path is outside the selected Discovery folder.");
        }

        private static IEnumerable<string> WalkFiles(string root)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string folder = pending.Pop();
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string child in Directory.GetDirectories(folder)) pending.Push(child);
                foreach (string file in Directory.GetFiles(folder))
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            }
        }

        private static void ReplaceBackedUp(string root, string file, byte[] before, byte[] after, MatchCardLayoutsReport report)
        {
            EnsureDirectories(root, Path.GetDirectoryName(file));
            if (report.BackupDirectory == null)
                report.BackupDirectory = Path.Combine(root, "Gorilla Studio Layout Backups", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string backup = Path.Combine(report.BackupDirectory, Relative(root, file) + ".bak");
            EnsureDirectories(root, Path.GetDirectoryName(backup));
            using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(before, 0, before.Length); stream.Flush(true); }
            Atomic(file, after, () => { if (!File.ReadAllBytes(file).SequenceEqual(before)) throw new IOException("The screen changed during setup; the newer file was left alone."); });
        }

        private static void AddNew(string root, string file, byte[] bytes)
        {
            EnsureDirectories(root, Path.GetDirectoryName(file));
            Atomic(file, bytes, null);
        }

        private static void EnsureDirectories(string root, string directory)
        {
            string full = Path.GetFullPath(directory), baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (full != baseRoot && !full.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Screen output escaped the chosen Discovery folder.");
            var missing = new Stack<string>(); string current = full;
            while (current != null && (current == baseRoot || current.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                if (Directory.Exists(current)) { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("A linked screen output directory was not followed: " + current); }
                else missing.Push(current);
                if (current == baseRoot) break;
                current = Path.GetDirectoryName(current);
            }
            while (missing.Count > 0) Directory.CreateDirectory(missing.Pop());
        }

        private static void Atomic(string file, byte[] bytes, Action validate)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(file), ".gorilla-layout-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (validate != null) { validate(); File.Replace(temporary, file, null); }
                else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private sealed class Node { internal int Position, Parent; internal List<string> Path; }
        private sealed class Shape { internal int VTable, Size; internal int[] Offsets; }
        private sealed class Screen
        {
            internal readonly byte[] Bytes; internal readonly List<Node> Nodes = new List<Node>();
            internal Screen(byte[] bytes)
            {
                Bytes = bytes;
                if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 4, 4) != "Scre") throw new InvalidDataException("Not a Scre project.");
                int root = Target(0); ShapeOf(root);
                foreach (int child in Vector(root, 3)) Walk(child, root, new List<string>(), new HashSet<int>(), 0);
            }
            private void Walk(int position, int parent, List<string> path, HashSet<int> parents, int depth)
            {
                if (depth > 64 || Nodes.Count > 30000 || !parents.Add(position)) throw new InvalidDataException("Cyclic or oversized screen hierarchy.");
                var names = new List<string>(path) { Name(position) };
                Nodes.Add(new Node { Position = position, Parent = parent, Path = names });
                foreach (int child in Children(position)) Walk(child, position, names, parents, depth + 1);
                parents.Remove(position);
            }
            internal int[] Children(int table) { return Vector(table, 55); }
            internal string Name(int table) { int field = Field(table, 1); return field == 0 ? "?" : String(Target(field)); }
            internal int ByteField(int table, int index) { int field = Field(table, index); return field == 0 ? -1 : Bytes[field]; }
            internal Shape ShapeOf(int table)
            {
                Need(Bytes, table, 4); int displacement = unchecked((int)U32(Bytes, table)); int vt = checked(table - displacement); Need(Bytes, vt, 4);
                int length = U16(Bytes, vt), size = U16(Bytes, vt + 2);
                if (vt < 8 || length < 4 || length > 1024 || length % 2 != 0 || size < 4 || size > 4096) throw new InvalidDataException("Unknown screen table shape.");
                Need(Bytes, vt, length); Need(Bytes, table, size);
                var fields = new int[(length - 4) / 2];
                for (int i = 0; i < fields.Length; i++) { fields[i] = U16(Bytes, vt + 4 + 2 * i); if (fields[i] != 0 && (fields[i] < 4 || fields[i] >= size)) throw new InvalidDataException("Invalid screen table field."); }
                return new Shape { VTable = vt, Size = size, Offsets = fields };
            }
            internal int Field(int table, int index) { Shape shape = ShapeOf(table); return index >= shape.Offsets.Length || shape.Offsets[index] == 0 ? 0 : table + shape.Offsets[index]; }
            internal int Target(int field) { uint offset = U32(Bytes, field); if (offset < 4 || offset > int.MaxValue - field) throw new InvalidDataException("Invalid forward screen offset."); int result = field + (int)offset; Need(Bytes, result, 4); return result; }
            internal string String(int position) { uint length = U32(Bytes, position); if (length > 4096) throw new InvalidDataException("Oversized screen name."); Need(Bytes, position + 4, (int)length + 1); if (Bytes[position + 4 + (int)length] != 0) throw new InvalidDataException("Unterminated screen name."); return new UTF8Encoding(false, true).GetString(Bytes, position + 4, (int)length); }
            internal int[] Vector(int table, int index)
            {
                int field = Field(table, index); if (field == 0) return new int[0]; int vector = Target(field); uint count = U32(Bytes, vector);
                if (count > 30000) throw new InvalidDataException("Oversized screen child vector."); Need(Bytes, vector + 4, checked((int)count * 4));
                var result = new int[(int)count]; for (int i = 0; i < result.Length; i++) { result[i] = Target(vector + 4 + i * 4); ShapeOf(result[i]); } return result;
            }
        }

        private sealed class Copier
        {
            private readonly List<byte> output;
            internal Copier(byte[] source) { output = new List<byte>(source); }
            internal int Allocate(int size, int residue = 0) { if (size < 0 || output.Count > Limit - size - 8) throw new InvalidDataException("Repaired screen would be too large."); while (output.Count % 8 != residue) output.Add(0); int position = output.Count; output.AddRange(new byte[size]); return position; }
            internal void Put32(int position, uint value) { for (int i = 0; i < 4; i++) output[position + i] = (byte)(value >> (i * 8)); }
            internal void Point(int field, int target) { if (target <= field || field % 4 != 0 || target % 4 != 0) throw new InvalidDataException("Invalid appended screen offset."); Put32(field, checked((uint)(target - field))); }
            private int String(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); int position = Allocate(5 + bytes.Length); Put32(position, (uint)bytes.Length); for (int i = 0; i < bytes.Length; i++) output[position + 4 + i] = bytes[i]; return position; }
            internal int Table(Screen source, int position, bool node)
            {
                Shape shape = source.ShapeOf(position);
                int vlength = 4 + shape.Offsets.Length * 2, vt = Allocate(vlength);
                for (int i = 0; i < vlength; i++) output[vt + i] = source.Bytes[shape.VTable + i];
                int copy = Allocate(shape.Size, position % 8);
                for (int i = 0; i < shape.Size; i++) output[copy + i] = source.Bytes[position + i];
                Put32(copy, checked((uint)(copy - vt)));
                for (int index = 0; index < shape.Offsets.Length; index++)
                {
                    int offset = shape.Offsets[index]; if (offset == 0) continue;
                    int field = position + offset;
                    if (!node)
                    {
                        // These measured property tables contain scalars only. A valid
                        // nested reference is an unknown schema and must not be guessed.
                        int width = shape.Offsets.Where(item => item > offset).DefaultIfEmpty(shape.Size).Min() - offset;
                        if (width > 8) throw new InvalidDataException("Unknown non-scalar movie property.");
                        if (width == 4)
                        {
                            uint displacement = U32(source.Bytes, field);
                            if (displacement >= 4 && displacement <= source.Bytes.Length - field - 4)
                            {
                                int target = field + (int)displacement;
                                bool table = false; try { source.ShapeOf(target); table = true; } catch (InvalidDataException) { } catch (OverflowException) { }
                                if (table) throw new InvalidDataException("Nested movie properties are not supported.");
                            }
                        }
                        continue;
                    }
                    if (ScalarSlots.Contains(index)) continue;
                    if (index == 1) Point(copy + offset, String(source.String(source.Target(field))));
                    else if (index == 55 || index == 56)
                    {
                        if (U32(source.Bytes, source.Target(field)) != 0) throw new InvalidDataException("Custom non-leaf slot children are not supported.");
                        Point(copy + offset, Allocate(4));
                    }
                    else Point(copy + offset, Table(source, source.Target(field), false));
                }
                return copy;
            }
            private int Field(int table, int index) { int vt = checked(table - (int)Read32(table)); int length = Read16(vt); if (4 + 2 * index >= length) throw new InvalidDataException("Required movie property is absent."); int offset = Read16(vt + 4 + 2 * index); if (offset == 0) throw new InvalidDataException("Required movie property is not stored."); return table + offset; }
            private uint Read32(int position) { return (uint)(output[position] | output[position + 1] << 8 | output[position + 2] << 16 | output[position + 3] << 24); }
            private int Read16(int position) { return output[position] | output[position + 1] << 8; }
            internal void Name(int table, string value) { Point(Field(table, 1), String(value)); }
            internal void Asset(int table, ulong hash) { int field = Field(table, 0); for (int i = 0; i < 8; i++) output[field + i] = (byte)(hash >> (i * 8)); }
            internal void Floats(int table, int property, float first, float second) { int field = Field(table, property); int sub = checked(field + (int)Read32(field)); Put32(Field(sub, 0), BitConverter.ToUInt32(BitConverter.GetBytes(first), 0)); Put32(Field(sub, 1), BitConverter.ToUInt32(BitConverter.GetBytes(second), 0)); }
            internal byte[] Result() { while (output.Count % 8 != 0) output.Add(0); return output.ToArray(); }
        }

        private static void Need(byte[] bytes, int position, int count) { if (position < 0 || count < 0 || position > bytes.Length - (long)count) throw new InvalidDataException("Invalid or truncated screen offset."); }
        private static uint U32(byte[] bytes, int position) { Need(bytes, position, 4); return (uint)(bytes[position] | bytes[position + 1] << 8 | bytes[position + 2] << 16 | bytes[position + 3] << 24); }
        private static int U16(byte[] bytes, int position) { Need(bytes, position, 2); return bytes[position] | bytes[position + 1] << 8; }
        private static void Put32(byte[] bytes, int position, uint value) { for (int i = 0; i < 4; i++) bytes[position + i] = (byte)(value >> (i * 8)); }
    }
}
