using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    /// <summary>Finds extracted models by Discovery's explicit model-path hashes, independent of display names.</summary>
    public static class MappedModelLibrary
    {
        private const int MaximumDepth = 64;
        private const int MaximumDirectories = 100000;
        private const int MaximumWrapperParents = 4;

        public static Dictionary<RosterEntry, List<ModelCandidate>> FindMatches(IEnumerable<RosterEntry> entries,
            IEnumerable<ModelCandidate> candidates, string rosterTable)
        {
            return FindMatches(entries, candidates, rosterTable, CancellationToken.None);
        }

        // This method only supplies evidence. Callers keep valid saved associations and
        // retain multiple verified packs/attires as explicit choices rather than picking one.
        public static Dictionary<RosterEntry, List<ModelCandidate>> FindMatches(IEnumerable<RosterEntry> entries,
            IEnumerable<ModelCandidate> candidates, string rosterTable, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new Dictionary<RosterEntry, List<ModelCandidate>>();
            string table = FullPath(rosterTable);
            if (table == null) return result;
            List<RosterEntry> rows = (entries ?? Enumerable.Empty<RosterEntry>())
                .Where(entry => entry != null && entry.Id > 0 && entry.Id <= 65535
                    && (string.IsNullOrWhiteSpace(entry.RosterTable) || SamePath(entry.RosterTable, table)))
                .Distinct().ToList();
            if (rows.Count == 0) return result;

            string mapping = MappingPath(table);
            if (mapping == null) return result;
            Dictionary<ulong, List<int>> index;
            try { index = CharacterMappingReader.Read(mapping); }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException || error is SecurityException || error is OverflowException)
            {
                throw new InvalidDataException("Could not read Discovery's CharacterMapping.jsfb. Re-select the Discovery data folder or restore its character mapping. " + error.Message, error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = new HashSet<int>(rows.Select(entry => entry.Id));
            var matches = new Dictionary<int, List<ModelCandidate>>();
            var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int directories = 0;
            foreach (ModelCandidate source in candidates ?? Enumerable.Empty<ModelCandidate>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string folder = source == null ? null : FullPath(source.Folder);
                if (folder == null || !seenFolders.Add(folder) || !Directory.Exists(folder) || ReparsePoint(folder) || !CharacterFolder(folder)) continue;
                var candidate = new ModelCandidate { Folder = folder, Name = source.Name, SourceId = source.SourceId };
                var ids = new HashSet<int>();
                foreach (string model in Models(folder, cancellationToken, ref directories))
                {
                    foreach (string path in VirtualPaths(folder, model))
                    {
                        List<int> mapped;
                        if (index.TryGetValue(CharacterMappingReader.ModelPathHash(path), out mapped))
                            foreach (int id in mapped) if (wanted.Contains(id)) ids.Add(id);
                    }
                }
                foreach (int id in ids)
                {
                    List<ModelCandidate> available;
                    if (!matches.TryGetValue(id, out available)) matches.Add(id, available = new List<ModelCandidate>());
                    available.Add(candidate);
                }
            }
            foreach (RosterEntry entry in rows)
            {
                List<ModelCandidate> available;
                if (matches.TryGetValue(entry.Id, out available))
                    result.Add(entry, available.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase).ToList());
            }
            return result;
        }

        private static string MappingPath(string table)
        {
            string root = DiscoveryProfiles.DataRoot(table);
            if (!Directory.Exists(root)) return null;
            try
            {
                string characters = Directory.EnumerateDirectories(root)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "Characters", StringComparison.OrdinalIgnoreCase));
                return characters == null ? null : Directory.EnumerateFiles(characters)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "CharacterMapping.jsfb", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception error) when (Unreadable(error))
            {
                throw new InvalidDataException("Could not find Discovery's CharacterMapping.jsfb. Check that its Characters folder is readable. " + error.Message, error);
            }
        }

        private static List<string> Models(string root, CancellationToken cancellationToken, ref int directories)
        {
            var models = new List<string>();
            var pending = new Stack<KeyValuePair<string, int>>();
            pending.Push(new KeyValuePair<string, int>(root, 0));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                KeyValuePair<string, int> node = pending.Pop();
                if (++directories > MaximumDirectories)
                    throw new InvalidDataException("This model collection is too large to match. Choose a folder closer to your wrestler models.");
                try
                {
                    foreach (string file in Directory.EnumerateFiles(node.Key))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.Equals(Path.GetExtension(file), ".mcd", StringComparison.OrdinalIgnoreCase) && !ReparsePoint(file)) models.Add(file);
                    }
                    foreach (string child in Directory.EnumerateDirectories(node.Key))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (ReparsePoint(child)) continue;
                        if (node.Value >= MaximumDepth)
                            throw new InvalidDataException("This model collection has too many nested folders. Choose a folder closer to your wrestler models.");
                        pending.Push(new KeyValuePair<string, int>(child, node.Value + 1));
                    }
                }
                catch (Exception error) when (Unreadable(error)) { }
            }
            return models;
        }

        private static IEnumerable<string> VirtualPaths(string folder, string model)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ancestor = new DirectoryInfo(folder);
            int wrappers = 0;
            // A moved collection retains the character/attire suffix of its game path.
            // Every proposed suffix still needs an exact hash in CharacterMapping; no
            // folder number, name, or shared profile base ID can establish a match.
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

        private static string Relative(string root, string path)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length).Replace('\\', '/') : null;
        }

        private static bool ReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception error) when (Unreadable(error)) { return true; }
        }

        private static bool CharacterFolder(string folder)
        {
            string name = new DirectoryInfo(folder).Name;
            if (string.Equals(name, "Characters", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Roster", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Mods", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Library", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                // A hash can identify one child's model while its parent contains a
                // second wrestler. Loading that whole parent would let the renderer
                // select an unrelated body, so leave the individual attire choices.
                return Directory.EnumerateDirectories(folder).Where(path => !ReparsePoint(path))
                    .Select(path => FolderNames.ExistingId(new DirectoryInfo(path).Name))
                    .Where(value => value.HasValue).Select(value => value.Value).Distinct().Take(2).Count() < 2;
            }
            catch (Exception error) when (Unreadable(error)) { return false; }
        }

        private static bool SamePath(string first, string second)
        {
            string normalized = FullPath(first);
            return normalized != null && string.Equals(normalized, second, StringComparison.OrdinalIgnoreCase);
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                return full.Length == Path.GetPathRoot(full).Length ? full
                    : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException || error is SecurityException) { return null; }
        }

        private static bool Unreadable(Exception error)
        {
            return (error is IOException && !(error is InvalidDataException)) || error is UnauthorizedAccessException || error is SecurityException;
        }
    }
}
