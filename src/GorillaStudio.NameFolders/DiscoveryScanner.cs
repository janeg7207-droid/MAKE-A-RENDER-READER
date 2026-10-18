using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    public sealed class DiscoveryScanResult
    {
        public List<RosterEntry> Entries = new List<RosterEntry>();
        public List<string> Warnings = new List<string>();
    }

    public static class DiscoveryScanner
    {
        private static readonly Regex Numbered = new Regex(@"^(\d+)_(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Placeholder = new Regex(@"^placeholder_(\d+)_A\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AttireSuffix = new Regex(@"_A\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static DiscoveryScanResult Scan(string root, IEnumerable<DiscoveryProfile> profiles, string rosterTable = null)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException("Choose an existing Discovery or Characters folder.");
            var result = new DiscoveryScanResult();
            var byId = new Dictionary<int, DiscoveryProfile>();
            foreach (DiscoveryProfile profile in profiles ?? Enumerable.Empty<DiscoveryProfile>())
                if (profile != null && profile.Id > 0 && profile.Id <= 65535 && !byId.ContainsKey(profile.Id)) byId.Add(profile.Id, profile);
            var byName = byId.Values.Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => NameKey(p.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Id).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

            DirectoryInfo selected = new DirectoryInfo(root);
            string primaryCharacters, dataRoot, singleFolder = null;
            if (string.Equals(selected.Name, "Characters", StringComparison.OrdinalIgnoreCase))
            {
                primaryCharacters = selected.FullName;
                dataRoot = selected.Parent == null ? selected.FullName : selected.Parent.FullName;
            }
            else if (selected.Parent != null && string.Equals(selected.Parent.Name, "Characters", StringComparison.OrdinalIgnoreCase))
            {
                singleFolder = selected.FullName;
                primaryCharacters = selected.Parent.FullName;
                dataRoot = selected.Parent.Parent == null ? primaryCharacters : selected.Parent.Parent.FullName;
            }
            else
            {
                dataRoot = string.Equals(selected.Name, "Roster", StringComparison.OrdinalIgnoreCase) && selected.Parent != null
                    ? selected.Parent.FullName : selected.FullName;
                primaryCharacters = ChildDirectory(dataRoot, "Characters");
            }
            string table = rosterTable ?? CharacterFolders.FindDiscoveryTable(dataRoot) ?? TableAt(dataRoot);
            if (!string.IsNullOrWhiteSpace(table)) table = Path.GetFullPath(table);
            if (table == null)
            {
                DirectoryInfo parent = Directory.GetParent(dataRoot);
                string bakeMe = parent == null ? null : ChildDirectory(parent.FullName, "BakeMe");
                if (bakeMe != null) table = TableAt(bakeMe);
            }

            var characterRoots = new List<string>();
            if (primaryCharacters != null) characterRoots.Add(primaryCharacters);
            DirectoryInfo dataParent = Directory.GetParent(dataRoot);
            string alternate = dataParent == null ? null : ChildDirectory(dataParent.FullName, "BakeMe_Characters");
            string alternateCharacters = alternate == null ? null : ChildDirectory(alternate, "Characters");
            if (singleFolder == null && alternateCharacters != null && (primaryCharacters == null || !SamePath(alternateCharacters, primaryCharacters))) characterRoots.Add(alternateCharacters);
            if (characterRoots.Count == 0) throw new InvalidDataException("The selected data folder has no Characters directory or sibling BakeMe_Characters\\Characters directory.");
            string primaryMapping = primaryCharacters == null ? null : ChildFile(primaryCharacters, "CharacterMapping.jsfb");
            var mappings = new Dictionary<string, Dictionary<ulong, List<int>>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string characters in characterRoots)
            {
                string physicalRoot = Directory.GetParent(characters) == null ? characters : Directory.GetParent(characters).FullName;
                var indexes = new List<Dictionary<ulong, List<int>>>();
                foreach (string path in new[] { ChildFile(characters, "CharacterMapping.jsfb"), primaryMapping }.Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Dictionary<ulong, List<int>> index;
                    if (!mappings.TryGetValue(path, out index))
                    {
                        try { index = CharacterMappingReader.Read(path); }
                        catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException)
                        {
                            result.Warnings.Add("Could not read character mapping " + path + ": " + error.Message);
                            index = new Dictionary<ulong, List<int>>();
                        }
                        mappings.Add(path, index);
                    }
                    indexes.Add(index);
                }
                IEnumerable<string> folders = singleFolder == null ? Directories(characters, result) : new[] { singleFolder };
                foreach (string folder in folders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string basename = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (basename.StartsWith("_", StringComparison.Ordinal) || basename.StartsWith(".", StringComparison.Ordinal)) continue;
                    int prefix = ExplicitId(Numbered.Match(basename));
                    int placeholder = ExplicitId(Placeholder.Match(basename));
                    var childIds = Directories(folder, result).Select(p => ExplicitId(Numbered.Match(Path.GetFileName(p)))).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
                    bool ambiguousAttires = prefix == 0 && placeholder == 0 && childIds.Count > 1;
                    int physicalId = prefix > 0 ? prefix : placeholder > 0 ? placeholder : childIds.Count == 1 ? childIds[0] : 0;
                    string fallback = prefix > 0 ? Numbered.Match(basename).Groups[2].Value.Replace('_', ' ').Trim()
                        : placeholder > 0 ? "Wrestler " + placeholder.ToString(CultureInfo.InvariantCulture)
                        : AttireSuffix.Replace(basename, "").Replace('_', ' ').Trim();
                    var mappedIds = new HashSet<int>();
                    if (!ambiguousAttires && indexes.Count > 0)
                    {
                        foreach (string model in Models(folder, result))
                        {
                            string relative = Relative(physicalRoot, model);
                            if (relative == null) continue;
                            ulong hash = CharacterMappingReader.ModelPathHash(relative);
                            foreach (Dictionary<ulong, List<int>> index in indexes)
                            {
                                List<int> ids;
                                if (index.TryGetValue(hash, out ids)) foreach (int id in ids) mappedIds.Add(id);
                            }
                        }
                    }
                    List<int> resolved;
                    string origin;
                    if (ambiguousAttires) { resolved = new List<int> { 0 }; origin = "Choose an attire folder"; }
                    else if (mappedIds.Count > 0) { resolved = mappedIds.OrderBy(id => id).ToList(); origin = "Character mapping"; }
                    else if (physicalId > 0) { resolved = new List<int> { physicalId }; origin = "Folder ID"; }
                    else if (byName.TryGetValue(NameKey(fallback), out resolved)) origin = "Name match";
                    else { resolved = new List<int> { 0 }; origin = "Choose roster slot"; }
                    foreach (int id in resolved)
                    {
                        if (!seen.Add(Path.GetFullPath(folder) + "\0" + id.ToString(CultureInfo.InvariantCulture))) continue;
                        DiscoveryProfile profile;
                        byId.TryGetValue(id, out profile);
                        int sourceId = physicalId > 0 ? physicalId
                            : origin == "Folder ID" ? id
                            : profile != null && profile.BaseId > 0 && profile.BaseId <= 65535 ? profile.BaseId : id;
                        result.Entries.Add(new RosterEntry {
                            Id = id, SourceId = sourceId, Name = profile != null && !string.IsNullOrWhiteSpace(profile.Name) ? profile.Name : fallback,
                            Folder = Path.GetFullPath(folder), RosterTable = table, IsMod = true, Source = origin
                        });
                    }
                }
            }
            return result;
        }

        private static int ExplicitId(Match match)
        {
            int id;
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 && id <= 65535 ? id : 0;
        }

        private static string NameKey(string name)
        {
            return Regex.Replace((name ?? "").Replace('_', ' ').Trim(), @"\s+", " ");
        }

        private static string Relative(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : null;
        }

        private static bool SamePath(string first, string second)
        {
            return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private static string ChildDirectory(string root, string name)
        {
            return Directory.GetDirectories(root).FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }

        private static string ChildFile(string root, string name)
        {
            return Directory.GetFiles(root).FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }

        private static string TableAt(string root)
        {
            string roster = ChildDirectory(root, "Roster");
            return roster == null ? null : ChildFile(roster, "CharProfileTable.jsfb");
        }

        private static IEnumerable<string> Directories(string path, DiscoveryScanResult result)
        {
            try { return Directory.GetDirectories(path); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                result.Warnings.Add("Could not read character folder " + path + ": " + error.Message);
                return new string[0];
            }
        }

        private static IEnumerable<string> Models(string folder, DiscoveryScanResult result)
        {
            var models = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    models.AddRange(Directory.GetFiles(current).Where(p => string.Equals(Path.GetExtension(p), ".mcd", StringComparison.OrdinalIgnoreCase)));
                    foreach (string child in Directory.GetDirectories(current))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    result.Warnings.Add("Could not scan model folder " + current + ": " + error.Message);
                }
            }
            return models;
        }
    }
}
