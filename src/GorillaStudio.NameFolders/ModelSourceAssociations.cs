using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class ModelSourceAssociation
    {
        public string RosterContext { get; set; }
        public int LogicalId { get; set; }
        public string WrestlerName { get; set; }
        public List<ModelCandidate> Sources { get; set; }
    }

    /// <summary>Explicit model choices belong to one named slot in one roster.</summary>
    public static class ModelSourceAssociations
    {
        public static string Context(RosterEntry entry, string fallbackContext)
        {
            string value = entry != null && !string.IsNullOrWhiteSpace(entry.RosterTable) ? entry.RosterTable : fallbackContext;
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("The selected roster context is required.");
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static List<ModelSourceAssociation> Replace(IEnumerable<ModelSourceAssociation> existing,
            RosterEntry entry, string context, IEnumerable<ModelCandidate> selected)
        {
            RequireEntry(entry);
            string owner = Context(entry, context);
            var roots = new List<ModelCandidate>();
            foreach (ModelCandidate candidate in selected ?? Enumerable.Empty<ModelCandidate>())
            {
                ModelCandidate current = Validate(candidate);
                if (current == null) throw new InvalidDataException("A selected model folder is missing or no longer contains a character model.");
                if (!roots.Any(item => SamePath(item.Folder, current.Folder))) roots.Add(current);
            }
            var records = (existing ?? Enumerable.Empty<ModelSourceAssociation>()).Where(item => item != null && !Owns(item, entry, owner))
                .Select(Clone).ToList();
            if (roots.Count > 0) records.Add(new ModelSourceAssociation { RosterContext = owner, LogicalId = entry.Id,
                WrestlerName = Name(entry.Name), Sources = roots });
            return records;
        }

        public static List<ModelCandidate> Sources(IEnumerable<ModelSourceAssociation> records, RosterEntry entry, string context)
        {
            RequireEntry(entry);
            var saved = (records ?? Enumerable.Empty<ModelSourceAssociation>()).Where(item => item != null).ToList();
            if (saved.Count == 0 || (string.IsNullOrWhiteSpace(entry.RosterTable) && string.IsNullOrWhiteSpace(context)))
                return new List<ModelCandidate>();
            string owner = Context(entry, context);
            var result = new List<ModelCandidate>();
            foreach (ModelCandidate item in saved.Where(record => Owns(record, entry, owner))
                .SelectMany(record => record.Sources ?? new List<ModelCandidate>()))
            {
                ModelCandidate current = Validate(item);
                if (current == null) throw new InvalidDataException("A model selected for " + entry.Name + " is missing or has changed: "
                    + (item == null ? "(missing source)" : item.Folder) + ". Choose its model folders again.");
                if (!result.Any(candidate => SamePath(candidate.Folder, current.Folder))) result.Add(current);
            }
            return result;
        }

        public static bool IsAssociated(IEnumerable<ModelSourceAssociation> records, RosterEntry entry, string context, string folder)
        { return !string.IsNullOrWhiteSpace(folder) && Sources(records, entry, context).Any(item => SamePath(item.Folder, folder)); }

        public static List<ModelSourceAssociation> Load(string path)
        {
            if (!File.Exists(path)) return new List<ModelSourceAssociation>();
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("The saved model choices file is too large.");
#if NET8_0_OR_GREATER
            return JsonSerializer.Deserialize<List<ModelSourceAssociation>>(File.ReadAllText(path)) ?? new List<ModelSourceAssociation>();
#else
            return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<List<ModelSourceAssociation>>(File.ReadAllText(path)) ?? new List<ModelSourceAssociation>();
#endif
        }

        public static void Save(string path, IEnumerable<ModelSourceAssociation> records)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var values = (records ?? Enumerable.Empty<ModelSourceAssociation>()).Where(item => item != null).Select(Clone).ToList();
            try
            {
#if NET8_0_OR_GREATER
                File.WriteAllText(temporary, JsonSerializer.Serialize(values), new UTF8Encoding(false));
#else
                File.WriteAllText(temporary, new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(values), new UTF8Encoding(false));
#endif
                if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static ModelCandidate Validate(ModelCandidate item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Folder)) return null;
            try
            {
                string root = Path.GetFullPath(item.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return null;
                ModelCandidate found = ModelLibrary.Scan(root).FirstOrDefault(candidate => SamePath(candidate.Folder, root));
                if (found == null) return null;
                // A numbered source that changed owners must be chosen again.
                if (item.SourceId > 0 && item.SourceId != found.SourceId) return null;
                return new ModelCandidate { Folder = root, Name = string.IsNullOrWhiteSpace(item.Name) ? found.Name : item.Name,
                    SourceId = found.SourceId };
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
                || error is System.Security.SecurityException || error is NotSupportedException) { return null; }
        }

        private static bool Owns(ModelSourceAssociation item, RosterEntry entry, string context)
        { return item.LogicalId == entry.Id && string.Equals(Name(item.WrestlerName), Name(entry.Name), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.RosterContext) && SamePath(item.RosterContext, context); }

        private static string Name(string value) { return (value ?? "").Trim().Normalize(NormalizationForm.FormC); }
        private static void RequireEntry(RosterEntry entry)
        { if (entry == null || entry.Id < 1 || entry.Id > 65535 || string.IsNullOrWhiteSpace(entry.Name)) throw new ArgumentException("Choose a named wrestler from the roster first."); }
        private static bool SamePath(string left, string right)
        {
            try { return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException) { return false; }
        }
        private static ModelSourceAssociation Clone(ModelSourceAssociation item)
        { return new ModelSourceAssociation { RosterContext = item.RosterContext, LogicalId = item.LogicalId, WrestlerName = item.WrestlerName,
            Sources = (item.Sources ?? new List<ModelCandidate>()).Where(source => source != null).Select(source => new ModelCandidate {
                Folder = source.Folder, Name = source.Name, SourceId = source.SourceId }).ToList() }; }
    }
}
