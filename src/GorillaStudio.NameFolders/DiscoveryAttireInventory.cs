using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace GorillaStudio.NameFolders
{
    /// <summary>Refreshes remembered model locations, including newly added separate character sources.</summary>
    public static class DiscoveryAttireInventory
    {
        public static List<ModelCandidate> Refresh(IEnumerable<ModelCandidate> previous, IEnumerable<string> roots,
            CancellationToken token = default(CancellationToken))
        {
            var candidates = new Dictionary<string, ModelCandidate>(StringComparer.OrdinalIgnoreCase);
            var scanned = new List<string>();
            IEnumerable<string> locations = (roots ?? new string[0]).Concat((previous ?? new ModelCandidate[0])
                .Where(item => item != null).Select(item => item.Folder));
            foreach (string raw in locations.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
            {
                token.ThrowIfCancellationRequested();
                if (scanned.Any(root => SameOrChild(raw, root))) continue;
                scanned.Add(raw);
                foreach (ModelCandidate model in ModelLibrary.Scan(raw, token))
                {
                    token.ThrowIfCancellationRequested();
                    int id = CharacterFolders.PhysicalId(model.Folder) ?? model.SourceId;
                    string folder = id > 0 && id <= 65535 ? AutomaticAttire.CharacterRoot(model.Folder, id) : model.Folder;
                    candidates[folder] = new ModelCandidate { Folder = folder, Name = model.Name, SourceId = id };
                }
            }
            return candidates.Values.OrderBy(item => item.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool SameOrChild(string candidate, string parent)
        {
            return string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
