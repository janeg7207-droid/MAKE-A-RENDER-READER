using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace GorillaStudio.NameFolders
{
    /// <summary>Combines proven mapped sources with the loaded source's manual choices.</summary>
    public static class DiscoveryAttireOptions
    {
        public static IReadOnlyList<AlternateAttire> Combine(string alias, string currentSource,
            IEnumerable<AlternateAttire> local, IEnumerable<AlternateAttire> mapped, bool authoritative = false)
        {
            var choices = new List<AlternateAttire>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool mappedDefault = false;
            foreach (AlternateAttire item in mapped ?? new AlternateAttire[0])
            {
                if (item == null || string.IsNullOrWhiteSpace(item.SourceFolder) || string.IsNullOrWhiteSpace(item.SourceRelativePath)) continue;
                string path = Key(item.SourceFolder, item.SourceRelativePath);
                seen.Add(path);
                if (references.Add(path + "\n" + (item.DiscoveryKey ?? ""))) choices.Add(item);
                mappedDefault |= item.IsDefault;
            }
            foreach (AlternateAttire item in local ?? new AlternateAttire[0])
            {
                if (item == null || string.IsNullOrWhiteSpace(item.RelativePath)) continue;
                string relative;
                try { relative = ValidateLocalSelection(alias, currentSource, item.RelativePath); }
                catch (Exception error) when (LocalSelectionError(error)) { continue; }
                string sourceRelative = AlternateAttires.SourceRelativePath(alias, relative);
                if (!seen.Add(Key(currentSource, sourceRelative))) continue;
                // Active mappings choose the automatic default and prove any
                // cross-source association. Additional clothes within that
                // same source remain explicit local choices, not new mappings.
                string label = item.DisplayName ?? sourceRelative;
                const string defaultSuffix = " (default)";
                if (mappedDefault && label.EndsWith(defaultSuffix, StringComparison.OrdinalIgnoreCase))
                    label = label.Substring(0, label.Length - defaultSuffix.Length);
                choices.Add(new AlternateAttire {
                    RelativePath = relative,
                    DisplayName = label + (authoritative ? " (local)" : ""),
                    IsDefault = item.IsDefault && !mappedDefault
                });
            }
            return choices.AsReadOnly();
        }

        /// <summary>
        /// Validates an explicit local choice against a caller-proven character
        /// source. Factory aliases may add one Default_Attire wrapper; the
        /// original source's descendants must still contain the actual MCD.
        /// </summary>
        public static string ValidateLocalSelection(string alias, string currentSource, string relative)
        {
            string selected = AlternateAttires.Validate(alias, relative);
            if (selected == null) throw new ArgumentException("Choose an attire model inside this character folder.", "relative");
            string sourceRelative = AlternateAttires.SourceRelativePath(alias, selected);
            AlternateAttires.Validate(currentSource, sourceRelative);
            string path = Path.GetFullPath(currentSource);
            foreach (string part in sourceRelative.Split('/'))
            {
                path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("A local attire must belong to this character folder; linked model sources require an exact Discovery mapping.", "relative");
            }
            return selected;
        }

        public static bool IsLocalChoice(string alias, string currentSource, string relative)
        {
            try { ValidateLocalSelection(alias, currentSource, relative); return true; }
            catch (Exception error) when (LocalSelectionError(error)) { return false; }
        }

        private static bool LocalSelectionError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is SecurityException || error is NotSupportedException;
        }

        public static string Key(string source, string relative)
        {
            return (string.IsNullOrWhiteSpace(source) ? "" : Path.GetFullPath(source).TrimEnd('\\', '/'))
                + "\n" + (relative ?? "").Replace('\\', '/');
        }
    }
}
