using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    public sealed class ModelCandidate
    {
        public string Folder { get; set; }
        public string Name { get; set; }
        public int SourceId { get; set; }
    }

    /// <summary>Indexes a mod collection once, without changing its files or choosing a roster slot.</summary>
    public static class ModelLibrary
    {
        private static readonly Regex Numbered = new Regex(@"^(\d+)_(.+)$", RegexOptions.CultureInvariant);
        private static readonly Regex Placeholder = new Regex(@"^placeholder_(\d+)_A\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AttireSuffix = new Regex(@"_A\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AttireName = new Regex(@"^(?:\d+_)?(?:default[ _-]*)?(?:attire(?:[ _-]*\d+)?|basemodel|base[ _-]*model|model|models|gear|costume|costumes|a\d+)(?:[ _-].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private const int MaximumDirectories = 100000;
        private const int MaximumDepth = 64;

        public static List<ModelCandidate> Find(string libraryRoot, RosterEntry selected)
        {
            return Match(Scan(libraryRoot), selected);
        }

        public static List<ModelCandidate> Scan(string libraryRoot)
        {
            return Scan(libraryRoot, CancellationToken.None);
        }

        public static List<ModelCandidate> Scan(string libraryRoot, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
                throw new DirectoryNotFoundException("Choose an existing folder containing your wrestler mods.");
            var root = new Node(Path.GetFullPath(libraryRoot), null, 0);
            var nodes = new List<Node>();
            var pending = new Stack<Node>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Node node = pending.Pop();
                if (nodes.Count >= MaximumDirectories)
                    throw new InvalidDataException("This collection is too large to index. Choose a folder closer to your wrestler mods.");
                nodes.Add(node);
                try
                {
                    foreach (string file in Directory.EnumerateFiles(node.Folder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.Equals(Path.GetExtension(file), ".mcd", StringComparison.OrdinalIgnoreCase)) node.DirectModel = true;
                    }
                    foreach (string folder in Directory.EnumerateDirectories(node.Folder))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (SkipDirectory(folder)) continue;
                        if (node.Depth >= MaximumDepth)
                            throw new InvalidDataException("This collection has too many nested folders. Choose a folder closer to your wrestler mods.");
                        var child = new Node(folder, node, node.Depth + 1);
                        node.Children.Add(child);
                        pending.Push(child);
                    }
                }
                catch (Exception error) when (Unreadable(error)) { }
            }
            // A postorder pass reuses the file traversal instead of scanning each character's
            // descendants again. A character owns its attire/base-model folders as one item.
            for (int index = nodes.Count - 1; index >= 0; index--)
            {
                Node node = nodes[index];
                node.HasModel = node.DirectModel || node.Children.Any(child => child.HasModel);
                node.SourceId = ExplicitId(Numbered.Match(node.Name));
                if (node.SourceId == 0) node.SourceId = ExplicitId(Placeholder.Match(node.Name));
                if (node.SourceId == 0)
                {
                    int[] childIds = node.Children.Where(child => child.HasModel)
                        .Select(child => ExplicitId(Numbered.Match(child.Name))).Where(id => id > 0).Distinct().Take(2).ToArray();
                    if (childIds.Length == 1) node.SourceId = childIds[0];
                }
            }
            var candidates = new List<ModelCandidate>();
            Collect(root, candidates, cancellationToken);
            return candidates.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<ModelCandidate> Match(IEnumerable<ModelCandidate> candidates, RosterEntry selected)
        {
            if (selected == null) throw new ArgumentNullException("selected", "Choose a wrestler from your roster first.");
            string selectedName = NameKey(selected.Name);
            List<ModelCandidate> available = (candidates ?? Enumerable.Empty<ModelCandidate>())
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Folder)).ToList();
            List<ModelCandidate> named = selectedName.Length == 0 ? new List<ModelCandidate>()
                : available.Where(candidate => NameKey(candidate.Name) == selectedName).ToList();
            // A custom display name is stronger evidence than a stock body's ID. For
            // example, PAC may use Neville's physical body while a PAC folder is present.
            IEnumerable<ModelCandidate> matching = named.Count > 0 ? named
                : available.Where(candidate => selected.SourceId > 0 && selected.SourceId <= 65535 && candidate.SourceId == selected.SourceId);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return matching
                .Where(candidate => seen.Add(candidate.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Collect(Node node, List<ModelCandidate> result, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.HasModel) return;
            bool assets = AttireName.IsMatch(node.Name);
            bool collection = IsCollection(node.Name);
            bool directCharacter = !collection && node.DirectModel && (!assets || node.Parent == null);
            bool ownsAttires = !collection && !assets && node.Children.Any(child => child.HasModel && AttireName.IsMatch(child.Name));
            // A numeric prefix can also be a dated mod pack (2026_Updates). It only
            // supplies a model ID after the folder's own files or attire layout prove
            // that it is a character; never consume unrelated character descendants.
            if (directCharacter || ownsAttires)
            {
                // Different physical model IDs under a plain parent are separate choices.
                // Loading the whole parent would let the engine choose the wrong attire.
                int[] childIds = node.Children.Where(child => child.HasModel)
                    .Select(child => ExplicitId(Numbered.Match(child.Name))).Where(id => id > 0).Distinct().Take(2).ToArray();
                if (node.SourceId == 0 && childIds.Length > 1)
                {
                    foreach (Node child in node.Children) CollectAttire(child, result, cancellationToken);
                    return;
                }
                result.Add(Candidate(node));
                return;
            }
            foreach (Node child in node.Children) CollectAttire(child, result, cancellationToken);
        }

        private static void CollectAttire(Node node, List<ModelCandidate> result, CancellationToken cancellationToken)
        {
            // An attire with no resolved character parent can still be selected explicitly.
            if (node.HasModel && AttireName.IsMatch(node.Name) && node.DirectModel)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(Candidate(node));
            }
            else Collect(node, result, cancellationToken);
        }

        private static ModelCandidate Candidate(Node node)
        {
            string name = node.Name;
            Match numbered = Numbered.Match(name);
            if (numbered.Success && ExplicitId(numbered) > 0) name = numbered.Groups[2].Value;
            name = AttireSuffix.Replace(name, "").Replace('_', ' ').Trim();
            return new ModelCandidate { Folder = node.Folder, Name = name, SourceId = node.SourceId };
        }

        private static int ExplicitId(Match match)
        {
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value > 0 && value <= 65535 ? value : 0;
        }

        private static string NameKey(string name)
        {
            var output = new StringBuilder();
            foreach (char character in (name ?? "").Normalize(NormalizationForm.FormC))
                if (char.IsLetterOrDigit(character)) output.Append(char.ToUpperInvariant(character));
            return output.ToString();
        }

        private static bool IsCollection(string name)
        {
            string key = NameKey(name);
            return key == "CHARACTERS" || key == "MODS" || key == "LIBRARY" || key == "MODLIBRARY"
                || key == "WRESTLERS" || key == "SUPERSTARS";
        }

        private static bool SkipDirectory(string folder)
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal)
                || string.Equals(name, "cache", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Props", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Arenas", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Roster", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Sdb", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                FileAttributes attributes = File.GetAttributes(folder);
                return (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0;
            }
            catch (Exception error) when (Unreadable(error)) { return true; }
        }

        private static bool Unreadable(Exception error)
        {
            return (error is IOException && !(error is InvalidDataException)) || error is UnauthorizedAccessException || error is SecurityException;
        }

        private sealed class Node
        {
            public Node(string folder, Node parent, int depth)
            {
                Folder = folder;
                Name = new DirectoryInfo(folder).Name;
                Parent = parent;
                Depth = depth;
            }
            public readonly string Folder;
            public readonly string Name;
            public readonly Node Parent;
            public readonly int Depth;
            public readonly List<Node> Children = new List<Node>();
            public bool DirectModel;
            public bool HasModel;
            public int SourceId;
        }
    }
}
