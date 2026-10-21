using System;
using System.Collections.Generic;
using System.Linq;

namespace GorillaStudio.NameFolders
{
    public static class ModelSourceAttires
    {
        public static List<AlternateAttire> Read(IEnumerable<ModelSourceAssociation> records, RosterEntry entry, string context,
            string currentSource = null, string alias = null)
        {
            var result = new List<AlternateAttire>();
            foreach (ModelCandidate source in ModelSourceAssociations.Sources(records, entry, context))
                foreach (AlternateAttire item in AlternateAttires.Read(source.Folder))
                {
                    if (!DiscoveryAttireOptions.IsLocalChoice(source.Folder, source.Folder, item.RelativePath)) continue;
                    string key = DiscoveryAttireOptions.Key(source.Folder, item.RelativePath);
                    result.Add(new AlternateAttire { SourceFolder = source.Folder, SourceRelativePath = item.RelativePath,
                        ExplicitSourceKey = key, IsDefault = item.IsDefault,
                        RelativePath = currentSource != null && string.Equals(DiscoveryAttireOptions.Key(currentSource, ""),
                            DiscoveryAttireOptions.Key(source.Folder, ""), StringComparison.OrdinalIgnoreCase)
                            ? AlternateAttires.AliasRelativePath(alias, item.RelativePath) : null,
                        DisplayName = source.Name + " · " + item.DisplayName + " (selected pack)" });
                }
            return result;
        }
    }
}
