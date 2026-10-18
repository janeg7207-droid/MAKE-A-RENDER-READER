using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    /// <summary>Resolves logical Discovery slots to uniquely indexed game model folders.</summary>
    public static class StockModels
    {
        public static Dictionary<int, string> Read(string root, string stockIndexPath)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException("The Discovery data folder does not exist.");
            var directory = new DirectoryInfo(root);
            string dataRoot = (string.Equals(directory.Name, "Characters", StringComparison.OrdinalIgnoreCase)
                || string.Equals(directory.Name, "Roster", StringComparison.OrdinalIgnoreCase)) && directory.Parent != null
                ? directory.Parent.FullName : directory.FullName;
            string characters = Directory.GetDirectories(dataRoot).FirstOrDefault(p => string.Equals(Path.GetFileName(p), "Characters", StringComparison.OrdinalIgnoreCase));
            string mapping = characters == null ? null : Directory.GetFiles(characters).FirstOrDefault(p => string.Equals(Path.GetFileName(p), "CharacterMapping.jsfb", StringComparison.OrdinalIgnoreCase));
            if (mapping == null) return new Dictionary<int, string>();
            Dictionary<ulong, List<int>> slots = CharacterMappingReader.Read(mapping);
            Dictionary<string, string[]> index;
            try
            {
                string json = File.ReadAllText(stockIndexPath);
#if NET8_0_OR_GREATER
                index = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
#else
                index = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 }.Deserialize<Dictionary<string, string[]>>(json);
#endif
                if (index == null) throw new InvalidDataException("The stock model index must be an object mapping model folders to path arrays.");
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                )
            {
                throw new InvalidDataException("Could not read the stock model index: " + error.Message, error);
            }
            var candidates = new Dictionary<int, HashSet<string>>();
            foreach (KeyValuePair<string, string[]> item in index)
            {
                string key = item.Key;
                if (string.IsNullOrWhiteSpace(key) || key == "." || key == ".." || key.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || item.Value == null) continue;
                foreach (string path in item.Value)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string relative = path.Replace('\\', '/');
                    if (!relative.EndsWith(".mcd", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("/", StringComparison.Ordinal)
                        || relative.IndexOf(':') >= 0 || relative.Split('/').Any(part => part == "." || part == "..")
                        || !relative.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    List<int> logicalIds;
                    if (!slots.TryGetValue(CharacterMappingReader.ModelPathHash("characters/" + relative), out logicalIds)) continue;
                    foreach (int id in logicalIds)
                    {
                        HashSet<string> folders;
                        if (!candidates.TryGetValue(id, out folders)) candidates[id] = folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        folders.Add(key);
                    }
                }
            }
            var result = new Dictionary<int, string>();
            foreach (KeyValuePair<int, HashSet<string>> item in candidates)
                if (item.Value.Count == 1) result[item.Key] = item.Value.Single();
            return result;
        }
    }
}
