using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public class DiscoveryAttireReference
    {
        public int LogicalId { get; set; }
        public int MappingId { get; set; }
        public uint CostumeKey { get; set; }
        public int CostumeIndex { get; set; }
        public ulong ModelHash { get; set; }
        public string DisplayName { get; set; }
        public string Issue { get; set; }
        public string Warning { get; set; }
        public bool IsDefault { get; set; }
    }

    public sealed class DiscoveryAttireSource : DiscoveryAttireReference
    {
        public string SourceFolder { get; set; }
        public string RelativePath { get; set; }
        public int SourceId { get; set; }
    }

    public sealed class DiscoveryAttireCatalogResult
    {
        public List<DiscoveryAttireReference> References { get; private set; }
        public List<DiscoveryAttireSource> Sources { get; private set; }
        public List<string> Issues { get; private set; }
        public DiscoveryAttireCatalogResult()
        {
            References = new List<DiscoveryAttireReference>();
            Sources = new List<DiscoveryAttireSource>();
            Issues = new List<string>();
        }
    }

    /// <summary>Resolves active profile costume references to exact extracted MCD paths.</summary>
    public static class DiscoveryAttireCatalog
    {
        private const int MaximumDepth = 64;
        private const int MaximumDirectories = 100000;
        private const int MaximumWrapperParents = 4;

        public static List<DiscoveryAttireReference> ReadReferences(string table, int logicalId)
        {
            return ReadReferences(table, logicalId, CancellationToken.None);
        }

        public static List<DiscoveryAttireReference> ReadReferences(string table, int logicalId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (logicalId < 1 || logicalId > 65535) throw new ArgumentOutOfRangeException("logicalId");
            string fullTable = FullPath(table);
            if (fullTable == null || !File.Exists(fullTable))
                throw new FileNotFoundException("Choose an existing Discovery CharProfileTable.jsfb.", table);
            var file = new FlatBufferFile(File.ReadAllBytes(fullTable), "Char");
            token.ThrowIfCancellationRequested();
            int profile = FindProfile(file, logicalId, token);
            if (profile == 0) return new List<DiscoveryAttireReference>();
            var active = new List<ActiveCostume>();
            var seenKeys = new HashSet<uint>();
            foreach (int row in file.Tables(profile, 4))
            {
                token.ThrowIfCancellationRequested();
                int keyField = file.Field(row, 5), labelField = file.Field(row, 0);
                uint key = keyField == 0 ? 0 : file.U32(keyField);
                // Repeated references are one attire, even if the table repeats UI slots.
                if (key == 0 || !seenKeys.Add(key)) continue;
                active.Add(new ActiveCostume { Key = key, LabelKey = labelField == 0 ? 0 : file.U32(labelField) });
            }
            uint defaultKey = 0;
            int selected = file.Tables(profile, 5).FirstOrDefault();
            if (selected != 0)
            {
                int keyField = file.Field(selected, 0);
                if (keyField != 0) defaultKey = file.U32(keyField);
            }
            if (defaultKey == 0 && active.Count > 0) defaultKey = active[0].Key;
            string root = DiscoveryProfiles.DataRoot(fullTable);
            Dictionary<uint, string> strings = ReadLabels(root, token);
            string characters = Child(root, "Characters", true);
            string mappingPath = characters == null ? null : Child(characters, "CharacterMapping.jsfb", false);
            string costumesPath = characters == null ? null : Child(characters, "CostumesTable.jsfb", false);
            List<CharacterMappingRecord> mappings = mappingPath == null ? new List<CharacterMappingRecord>()
                : CharacterMappingReader.ReadRecords(mappingPath);
            token.ThrowIfCancellationRequested();
            List<CostumeRecord> costumes = costumesPath == null ? new List<CostumeRecord>() : ReadCostumes(costumesPath, token);
            var result = new List<DiscoveryAttireReference>();
            foreach (ActiveCostume item in active)
            {
                token.ThrowIfCancellationRequested();
                string label;
                strings.TryGetValue(item.LabelKey, out label);
                var reference = new DiscoveryAttireReference { LogicalId = logicalId, CostumeKey = item.Key,
                    DisplayName = CleanLabel(label), IsDefault = item.Key == defaultKey };
                if (reference.DisplayName == null) reference.DisplayName = "Attire " + (result.Count + 1).ToString(CultureInfo.InvariantCulture);
                List<CostumeRecord> matchingCostumes = costumes.Where(row => row.Key == item.Key).ToList();
                // An active owner+key pair remains explicit evidence when a mod
                // omitted its optional costume row. Cross-owner associations still
                // require the costume table's source ID and variant.
                List<CharacterMappingRecord> ownerRows = mappings.Where(row => row.CostumeKey == item.Key && row.LogicalId == logicalId).ToList();
                List<CharacterMappingRecord> choices = ownerRows.Count > 0
                    ? ownerRows.Where(row => matchingCostumes.Count == 0
                        || matchingCostumes.Any(costume => costume.Index == row.CostumeIndex)).ToList()
                    : mappings.Where(row => row.CostumeKey == item.Key && matchingCostumes.Any(costume =>
                        costume.Id == row.LogicalId && costume.Index == row.CostumeIndex)).ToList();
                choices = choices.GroupBy(row => new { row.LogicalId, row.CostumeIndex, row.ModelHash }).Select(group => group.First()).ToList();
                if (choices.Count == 1)
                {
                    CharacterMappingRecord choice = choices[0];
                    reference.MappingId = choice.LogicalId;
                    reference.CostumeIndex = choice.CostumeIndex;
                    reference.ModelHash = choice.ModelHash;
                    if (matchingCostumes.Count == 0)
                        reference.Warning = Describe(reference) + (costumesPath == null
                            ? ": CostumesTable.jsfb is missing; using its unique explicit owner and costume-key mapping."
                            : ": its CostumesTable.jsfb row is missing; using its unique explicit owner and costume-key mapping.");
                }
                else reference.Issue = Describe(reference) + (choices.Count > 1
                    ? ": multiple different costume mappings; choose a model source explicitly."
                    : matchingCostumes.Count == 0
                        ? ": no matching costume in CostumesTable.jsfb and no unique owner-key mapping."
                        : ": no exact costume mapping in CharacterMapping.jsfb.");
                result.Add(reference);
            }
            return result;
        }

        public static DiscoveryAttireCatalogResult Find(RosterEntry entry, IEnumerable<ModelCandidate> inventory, string table)
        {
            return Find(entry, inventory, table, CancellationToken.None);
        }

        public static DiscoveryAttireCatalogResult Find(RosterEntry entry, IEnumerable<ModelCandidate> inventory, string table, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = new DiscoveryAttireCatalogResult();
            if (entry == null || entry.Id < 1 || entry.Id > 65535) return result;
            string fullTable = FullPath(table);
            if (fullTable == null && string.IsNullOrWhiteSpace(entry.RosterTable)) return result;
            if (fullTable == null) fullTable = FullPath(entry.RosterTable);
            if (fullTable == null || (!string.IsNullOrWhiteSpace(entry.RosterTable)
                && !string.Equals(FullPath(entry.RosterTable), fullTable, StringComparison.OrdinalIgnoreCase)))
            {
                result.Issues.Add("This wrestler belongs to a different Discovery roster.");
                return result;
            }
            try { result.References.AddRange(ReadReferences(fullTable, entry.Id, token)); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException || error is SecurityException || error is OverflowException)
            {
                result.Issues.Add("Could not read Discovery's active attires for wrestler "
                    + entry.Id.ToString(CultureInfo.InvariantCulture) + ": " + error.Message);
                return result;
            }
            foreach (DiscoveryAttireReference reference in result.References)
            {
                if (reference.Issue != null) AddIssue(result, reference.Issue);
                if (reference.Warning != null) AddIssue(result, reference.Warning);
            }
            var byHash = result.References.Where(reference => reference.Issue == null && reference.ModelHash != 0)
                .GroupBy(reference => reference.ModelHash).ToDictionary(group => group.Key, group => group.ToList());
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int directories = 0;
            if (byHash.Count > 0)
            foreach (ModelCandidate candidate in inventory ?? Enumerable.Empty<ModelCandidate>())
            {
                token.ThrowIfCancellationRequested();
                string folder = candidate == null ? null : FullPath(candidate.Folder);
                if (folder == null || !seenFolders.Add(folder) || !Directory.Exists(folder)
                    || Linked(folder) || !CharacterFolder(folder)) continue;
                foreach (string model in Models(folder, token, ref directories))
                {
                    string relative = Relative(folder, model);
                    if (relative == null) continue;
                    var hashes = new HashSet<ulong>(VirtualPaths(folder, model).Select(CharacterMappingReader.ModelPathHash));
                    foreach (ulong hash in hashes)
                    {
                        List<DiscoveryAttireReference> matching;
                        if (!byHash.TryGetValue(hash, out matching)) continue;
                        foreach (DiscoveryAttireReference reference in matching)
                        {
                            token.ThrowIfCancellationRequested();
                            string identity = folder + "\n" + relative + "\n" + reference.LogicalId.ToString(CultureInfo.InvariantCulture)
                                + ":" + reference.CostumeKey.ToString("X8", CultureInfo.InvariantCulture);
                            if (!seenSources.Add(identity)) continue;
                            result.Sources.Add(new DiscoveryAttireSource { LogicalId = reference.LogicalId, MappingId = reference.MappingId,
                                CostumeKey = reference.CostumeKey, CostumeIndex = reference.CostumeIndex, ModelHash = reference.ModelHash,
                                DisplayName = reference.DisplayName, IsDefault = reference.IsDefault, Warning = reference.Warning, SourceFolder = folder,
                                RelativePath = relative, SourceId = PhysicalId(candidate, folder, model, reference.MappingId) });
                        }
                    }
                }
            }
            foreach (DiscoveryAttireReference reference in result.References)
                if (reference.Issue == null && !result.Sources.Any(source => source.CostumeKey == reference.CostumeKey && source.LogicalId == reference.LogicalId))
                {
                    reference.Issue = Describe(reference) + " (variant " + reference.CostumeIndex.ToString(CultureInfo.InvariantCulture)
                        + ", model " + reference.ModelHash.ToString("X16", CultureInfo.InvariantCulture)
                        + "): its mapped MCD was not found in the extracted model folders.";
                    AddIssue(result, reference.Issue);
                }
            return result;
        }

        private static int FindProfile(FlatBufferFile file, int id, CancellationToken token)
        {
            foreach (int wrapper in file.Tables(file.Root, 0))
            {
                token.ThrowIfCancellationRequested();
                int field = file.Field(wrapper, 1);
                if (field == 0) continue;
                uint displacement = file.U32(field);
                if (displacement < 4 || displacement > int.MaxValue - field)
                    throw new InvalidDataException("Invalid Discovery profile table reference.");
                int profile = field + (int)displacement;
                file.FieldCount(profile);
                int idField = file.Field(profile, 2);
                if (idField != 0 && file.U16(idField) == id) return profile;
            }
            return 0;
        }

        private static List<CostumeRecord> ReadCostumes(string path, CancellationToken token)
        {
            var file = new FlatBufferFile(File.ReadAllBytes(path), "Cost");
            var result = new List<CostumeRecord>();
            foreach (int row in file.Tables(file.Root, 0))
            {
                token.ThrowIfCancellationRequested();
                int keyField = file.Field(row, 0), idField = file.Field(row, 1), indexField = file.Field(row, 2);
                if (keyField == 0 || idField == 0) continue;
                uint key = file.U32(keyField);
                int id = file.U16(idField);
                if (key != 0 && id > 0) result.Add(new CostumeRecord { Key = key, Id = id,
                    Index = indexField == 0 ? 0 : file.U16(indexField) });
            }
            return result;
        }

        private static Dictionary<uint, string> ReadLabels(string root, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = DiscoveryProfiles.FindEnglishSdb(root);
            Dictionary<uint, string> result = path == null ? new Dictionary<uint, string>() : DiscoveryProfiles.ReadStrings(path);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private static IEnumerable<string> Models(string root, CancellationToken token, ref int directories)
        {
            // Materialize before returning because an iterator cannot have a ref parameter.
            var files = new List<string>();
            var pending = new Stack<KeyValuePair<string, int>>();
            pending.Push(new KeyValuePair<string, int>(root, 0));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                KeyValuePair<string, int> node = pending.Pop();
                if (++directories > MaximumDirectories)
                    throw new InvalidDataException("This model collection is too large to match. Choose a folder closer to your wrestler models.");
                try
                {
                    foreach (string file in Directory.EnumerateFiles(node.Key))
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.Equals(Path.GetExtension(file), ".mcd", StringComparison.OrdinalIgnoreCase) && !Linked(file)) files.Add(file);
                    }
                    foreach (string child in Directory.EnumerateDirectories(node.Key))
                    {
                        token.ThrowIfCancellationRequested();
                        if (Linked(child)) continue;
                        if (node.Value >= MaximumDepth)
                            throw new InvalidDataException("This model collection has too many nested folders. Choose a folder closer to your wrestler models.");
                        pending.Push(new KeyValuePair<string, int>(child, node.Value + 1));
                    }
                }
                catch (Exception error) when (Unreadable(error)) { }
            }
            return files;
        }

        private static IEnumerable<string> VirtualPaths(string folder, string model)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ancestor = new DirectoryInfo(folder);
            int wrappers = 0;
            while (ancestor != null && wrappers <= MaximumDepth)
            {
                if (string.Equals(ancestor.Name, "Characters", StringComparison.OrdinalIgnoreCase))
                {
                    string relative = Relative(ancestor.FullName, model);
                    if (relative != null) paths.Add("characters/" + relative);
                    break;
                }
                if (wrappers <= MaximumWrapperParents && ancestor.Parent != null)
                {
                    string relative = Relative(ancestor.Parent.FullName, model);
                    if (relative != null) paths.Add("characters/" + relative);
                }
                ancestor = ancestor.Parent;
                wrappers++;
            }
            return paths;
        }

        private static int PhysicalId(ModelCandidate candidate, string root, string model, int mappingId)
        {
            if (candidate.SourceId > 0 && candidate.SourceId <= 65535) return candidate.SourceId;
            var node = new DirectoryInfo(Path.GetDirectoryName(model));
            while (node != null && (string.Equals(node.FullName, root, StringComparison.OrdinalIgnoreCase) || Relative(root, node.FullName) != null))
            {
                int? id = FolderNames.ExistingId(node.Name);
                if (id.HasValue) return id.Value;
                node = node.Parent;
            }
            return mappingId;
        }

        private static bool CharacterFolder(string root)
        {
            string name = new DirectoryInfo(root).Name;
            if (new[] { "Characters", "Roster", "Mods", "Library", "ModLibrary", "Wrestlers", "Superstars" }
                .Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase))) return false;
            try
            {
                return Directory.EnumerateDirectories(root).Where(path => !Linked(path))
                    .Select(path => FolderNames.ExistingId(Path.GetFileName(path)))
                    .Where(id => id.HasValue).Select(id => id.Value).Distinct().Take(2).Count() < 2;
            }
            catch (Exception error) when (Unreadable(error)) { return false; }
        }

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length).Replace('\\', '/') : null;
        }

        private static string Child(string folder, string name, bool directory)
        {
            return (directory ? Directory.EnumerateDirectories(folder) : Directory.EnumerateFiles(folder))
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Linked(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception error) when (Unreadable(error)) { return true; }
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                return full.Length == Path.GetPathRoot(full).Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException || error is SecurityException) { return null; }
        }

        private static string CleanLabel(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        private static string Describe(DiscoveryAttireReference reference)
        {
            return reference.DisplayName + " [wrestler " + reference.LogicalId.ToString(CultureInfo.InvariantCulture)
                + ", costume " + reference.CostumeKey.ToString("X8", CultureInfo.InvariantCulture) + "]";
        }
        private static void AddIssue(DiscoveryAttireCatalogResult result, string issue)
        {
            if (!result.Issues.Contains(issue)) result.Issues.Add(issue);
        }
        private static bool Unreadable(Exception error)
        {
            return (error is IOException && !(error is InvalidDataException)) || error is UnauthorizedAccessException || error is SecurityException;
        }
        private sealed class ActiveCostume { public uint Key; public uint LabelKey; }
        private sealed class CostumeRecord { public uint Key; public int Id; public int Index; }
    }
}
