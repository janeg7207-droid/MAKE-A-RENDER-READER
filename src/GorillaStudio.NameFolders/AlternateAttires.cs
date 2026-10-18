using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public sealed class AlternateAttire
    {
        public string RelativePath { get; internal set; }
        public string DisplayName { get; internal set; }
        public bool IsDefault { get; internal set; }
        public override string ToString() { return DisplayName; }
    }

    /// <summary>
    /// Lists wearable models inside one character. The engine accepts an explicit
    /// relative MCD path, so choosing clothes never changes the character's body,
    /// physical ID, Discovery context or logical installation slot.
    /// </summary>
    public static class AlternateAttires
    {
        public static List<AlternateAttire> Read(string characterFolder)
        {
            if (string.IsNullOrWhiteSpace(characterFolder) || !Directory.Exists(characterFolder))
                return new List<AlternateAttire>();
            string root = Path.GetFullPath(characterFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var models = new List<string>();
            int entries = 0;
            Scan(root, "", 0, models, ref entries);
            string nativeDefault = models.Where(path => path.Split('/').Length == 2)
                .OrderBy(path => DefaultRank(path)).ThenBy(path => AttireRank(path)).ThenBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            return models.OrderBy(path => path == nativeDefault ? 0 : 1)
                .ThenBy(path => DefaultRank(path)).ThenBy(path => AttireRank(path)).ThenBy(path => path, StringComparer.Ordinal)
                .Select(path => new AlternateAttire { RelativePath = path, IsDefault = path == nativeDefault,
                    DisplayName = Display(path) + (path == nativeDefault ? " (default)" : "") }).ToList();
        }

        private static void Scan(string root, string relative, int depth, List<string> models, ref int entries)
        {
            string folder = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            foreach (string file in Directory.GetFiles(folder).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (++entries > 8192) throw new InvalidDataException("This character contains too many files to list its attires. Choose its character folder.");
                string name = Path.GetFileName(file);
                if (!string.Equals(Path.GetExtension(name), ".mcd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "baseModel.mcd", StringComparison.OrdinalIgnoreCase)) continue;
                models.Add(relative.Length == 0 ? name : relative + "/" + name);
            }
            if (depth >= 4) return;
            foreach (string child in Directory.GetDirectories(folder).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (++entries > 8192) throw new InvalidDataException("This character contains too many folders to list its attires. Choose its character folder.");
                string name = Path.GetFileName(child);
                if (string.Equals(name, "BaseModel", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Textures", StringComparison.OrdinalIgnoreCase)) continue;
                // A compatibility Default_Attire can itself be a junction. Read
                // its direct models, but never follow linked trees recursively.
                bool link = (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0;
                Scan(root, relative.Length == 0 ? name : relative + "/" + name, link ? 4 : depth + 1, models, ref entries);
            }
        }

        private static int DefaultRank(string path)
        {
            int slash = path.IndexOf('/');
            string folder = slash < 0 ? "" : path.Substring(0, slash);
            return folder.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
        }

        private static int AttireRank(string path)
        {
            return Path.GetFileName(path).IndexOf("attire", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
        }

        private static string Display(string relative)
        {
            string[] parts = relative.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = i == parts.Length - 1 ? Path.GetFileNameWithoutExtension(parts[i]) : parts[i];
                int underscore = part.IndexOf('_');
                if (underscore > 0 && part.Substring(0, underscore).All(char.IsDigit)) part = part.Substring(underscore + 1);
                parts[i] = part.Replace('_', ' ');
            }
            if (parts.Length > 1 && string.Equals(parts[parts.Length - 1], "Attire", StringComparison.OrdinalIgnoreCase))
                return string.Join(" / ", parts.Take(parts.Length - 1));
            return string.Join(" / ", parts);
        }

        public static string Validate(string characterFolder, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            string normalized = relative.Replace('\\', '/');
            string[] parts = normalized.Split('/');
            if (Path.IsPathRooted(normalized) || normalized.IndexOf(':') >= 0
                || normalized.Any(c => c == '"' || char.IsControl(c))
                || parts.Any(part => part.Length == 0 || part == "." || part == "..")
                || !string.Equals(Path.GetExtension(normalized), ".mcd", StringComparison.OrdinalIgnoreCase)
                || parts.Any(part => string.Equals(part, "BaseModel", StringComparison.OrdinalIgnoreCase))
                || string.Equals(parts[parts.Length - 1], "baseModel.mcd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an attire model inside this character folder.", "relative");
            if (string.IsNullOrWhiteSpace(characterFolder) || !Directory.Exists(characterFolder))
                throw new DirectoryNotFoundException("Load the wrestler before choosing an attire.");
            string full = Path.Combine(characterFolder, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) throw new FileNotFoundException("The selected attire is no longer available. Refresh the attire list and choose another.", full);
            return normalized;
        }

        public static bool IsRender(string script)
        {
            return !string.IsNullOrWhiteSpace(script) && string.Equals(
                Path.GetFileNameWithoutExtension(script.Replace('\\', '/')), "gorilla_render", StringComparison.OrdinalIgnoreCase);
        }

        public static string[] Apply(string script, IEnumerable<string> arguments, string relative)
        {
            string[] original = arguments == null ? null : arguments.ToArray();
            if (!IsRender(script) || original == null || original.Length < 3) return original;
            string selected = Validate(original[0], relative);
            var result = original.Where((value, index) => index < 3 || value == null || !value.StartsWith("attire=", StringComparison.Ordinal)).ToList();
            if (selected != null) result.Add("attire=" + selected);
            return result.ToArray();
        }

        // ProcessStartInfo.Arguments follows Windows CommandLineToArgvW quoting.
        // This is one argument, never shell text. In particular trailing slashes
        // and quotes cannot split a path or inject another renderer option.
        public static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }

        /// <summary>
        /// Native preparation keys omit attire. Wait for the old chain before
        /// clearing those keys, while publishing a task that Go can await now.
        /// Every queued change completes even when superseded by a newer choice.
        /// </summary>
        public static Task QueuePreparation(Func<Task> readTask, Action<Task> writeTask, Func<bool> stillCurrent, Action prepare)
        {
            if (readTask == null || writeTask == null || stillCurrent == null || prepare == null)
                throw new ArgumentNullException("A preparation task and callbacks are required.");
            Task before = readTask() ?? Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            writeTask(completion.Task);
            _ = CompletePreparation(before, completion, readTask, writeTask, stillCurrent, prepare);
            return completion.Task;
        }

        private static async Task CompletePreparation(Task before, TaskCompletionSource<bool> completion,
            Func<Task> readTask, Action<Task> writeTask, Func<bool> stillCurrent, Action prepare)
        {
            try
            {
                try { await before; } catch { }
                if (stillCurrent())
                {
                    // Prepare builds its next chain from the field itself. It
                    // must never await the placeholder we are about to complete.
                    writeTask(Task.CompletedTask);
                    Task actual;
                    try { prepare(); actual = readTask() ?? Task.CompletedTask; }
                    finally { writeTask(completion.Task); }
                    await actual;
                }
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
