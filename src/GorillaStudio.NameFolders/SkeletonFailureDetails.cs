using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class SkeletonFailureReport
    {
        public string LogPath { get; internal set; }
        public string Summary { get; internal set; }
        public string FullDetails { get; internal set; }
    }

    /// <summary>Reads only a fresh rest.log belonging to the exact current model request.</summary>
    public static class SkeletonFailureDetails
    {
        private const int MaximumFolders = 256;
        private const int MaximumMarkerBytes = 1024 * 1024;
        private const int MaximumLogTailBytes = 32 * 1024;
        private const long UnixEpochTicks = 621355968000000000L;

        public static SkeletonFailureReport Find(string script, IEnumerable<string> arguments,
            DateTime startedUtc, Func<string, string> resolveSource = null)
        {
            string[] args = arguments == null ? null : arguments.ToArray();
            if (!Renderer(script) || args == null || args.Length < 3) return null;
            try
            {
                var options = Options(args);
                string work;
                var candidates = new List<string>();
                if (options.TryGetValue("work", out work) && !string.IsNullOrWhiteSpace(work))
                {
                    // A supplied work directory is already exact. Relative work
                    // paths depend on native process CWD and are not guessed.
                    if (!Path.IsPathRooted(work)) return null;
                    string log = Path.Combine(Path.GetFullPath(work), "rest.log");
                    if (FreshLog(log, startedUtc)) candidates.Add(log);
                }
                else
                {
                    string character = Canonical(args[0], resolveSource);
                    if (!Directory.Exists(args[0])) return null;
                    string attire = SelectedAttire(args[0], options);
                    if (attire == null) return null;
                    string root = Path.Combine(Path.GetFullPath(args[2]), "work");
                    if (!Directory.Exists(root)) return null;
                    Match id = Regex.Match(Path.GetFileName(Path.GetFullPath(args[0]).TrimEnd('\\', '/')),
                        @"^(?:placeholder_)?([0-9]+)_", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                    string pattern = id.Success ? id.Groups[1].Value + "_*" : "*";
                    int visited = 0;
                    foreach (string folder in Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly))
                    {
                        if (++visited > MaximumFolders) return null;
                        if (Linked(folder)) continue;
                        string log = Path.Combine(folder, "rest.log");
                        if (!FreshLog(log, startedUtc)) continue;
                        if (MatchesMarker(Path.Combine(folder, "model.geometry.json"), character, attire, resolveSource))
                            candidates.Add(log);
                    }
                }
                // Simultaneous failed requests must not borrow each other's
                // output. No log contents are read until one owner is proven.
                if (candidates.Count != 1) return null;
                string tail = Tail(candidates[0]);
                if (Regex.IsMatch(tail, @"(?m)^REST_DUMP(?:\s|$)", RegexOptions.CultureInvariant)) return null;
                string[] lines = tail.Replace("\r", "").Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
                if (lines.Length == 0) return null;
                string error = lines.LastOrDefault(line => Regex.IsMatch(line,
                    @"(?:\b\w*(?:Error|Exception)\s*:|\berror\s*:|\bfailed\b|\bcould not\b|\bcannot\b|\bnot found\b|permission denied)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                string summary = error == null ? "Blender did not finish reading this model's skeleton."
                    : error.Trim();
                if (summary.Length > 240) summary = summary.Substring(0, 237) + "…";
                string full = "Skeleton preparation stopped.\n" + summary + "\n\nrest.log:\n" + candidates[0]
                    + "\n\nLast log lines:\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 20)))
                    + "\n\nThe original model files were kept. This error needs the log's Blender or script details; it does not establish that the wrestler has no skeleton.";
                return new SkeletonFailureReport { LogPath = candidates[0], Summary = summary, FullDetails = full };
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException || error is InvalidOperationException
                || error is System.ComponentModel.Win32Exception || error is OverflowException)
            { return null; }
        }

        public static string Directions(string script, IEnumerable<string> arguments)
        {
            string[] args = arguments == null ? null : arguments.ToArray();
            if (!Renderer(script) || args == null || args.Length < 3) return null;
            try
            {
                string work;
                var options = Options(args);
                string root = options.TryGetValue("work", out work) && Path.IsPathRooted(work)
                    ? Path.GetFullPath(work) : Path.Combine(Path.GetFullPath(args[2]), "work");
                return "Open the Output folder, then work, and the affected wrestler's model folder. Send its rest.log.\nWorking folder: " + root;
            }
            catch (Exception error) when (error is ArgumentException || error is NotSupportedException || error is IOException)
            { return null; }
        }

        private static bool Renderer(string script)
        {
            try { return script != null && string.Equals(Path.GetFileNameWithoutExtension(script.Replace('\\', '/')),
                "gorilla_render", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        private static Dictionary<string, string> Options(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string arg in args.Skip(3))
            {
                int equal = arg == null ? -1 : arg.IndexOf('=');
                if (equal >= 0) result[arg.Substring(0, equal)] = arg.Substring(equal + 1);
            }
            return result;
        }

        private static string SelectedAttire(string character, Dictionary<string, string> options)
        {
            string explicitPath;
            if (options.TryGetValue("attire", out explicitPath) && !string.IsNullOrWhiteSpace(explicitPath))
            {
                string path = explicitPath.Replace('\\', '/');
                if (Path.IsPathRooted(path) || path.IndexOf(':') >= 0 || path.Split('/').Any(part => part == ".." || part == ".")) return null;
                return File.Exists(Path.Combine(character, path.Replace('/', Path.DirectorySeparatorChar))) ? path : null;
            }
            // The native find_attire looks exactly one child directory deep,
            // preferring default folders, attire filenames and ordinal paths.
            var paths = new List<string>();
            foreach (string child in Directory.GetDirectories(character))
            {
                if (string.Equals(Path.GetFileName(child), "BaseModel", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string file in Directory.GetFiles(child))
                    if (string.Equals(Path.GetExtension(file), ".mcd", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.GetFileName(child) + "/" + Path.GetFileName(file));
            }
            return paths.OrderBy(path => path.Split('/')[0].IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path).IndexOf("attire", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        }

        private static bool MatchesMarker(string path, string character, string attire, Func<string, string> resolve)
        {
            if (!File.Exists(path) || Linked(path) || new FileInfo(path).Length > MaximumMarkerBytes) return false;
            try
            {
                var marker = JsonObject(File.ReadAllText(path));
                if (marker == null) return false;
                var identity = Object(marker, "identity");
                if (identity == null || Number(identity, "schema") != 1
                    || !SamePath(Text(identity, "character"), character)
                    || !string.Equals(Text(identity, "attire"), attire, PathComparison)) return false;
                object raw;
                var sources = identity.TryGetValue("sources", out raw) ? raw as IList : null;
                if (sources == null || sources.Count != 2) return false;
                string expectedAttire = Canonical(Path.Combine(character, attire.Replace('/', Path.DirectorySeparatorChar)), resolve);
                for (int index = 0; index < sources.Count; index++)
                {
                    var stamp = sources[index] as Dictionary<string, object>;
                    if (stamp == null) return false;
                    string source = Text(stamp, "path");
                    if (source == null || !File.Exists(source)) return false;
                    if (index == 0 && !SamePath(source, expectedAttire)) return false;
                    if (index == 1 && !string.Equals(Path.GetFileName(source), "baseModel.mcd", StringComparison.OrdinalIgnoreCase)) return false;
                    var file = new FileInfo(source);
                    long nanos = Number(stamp, "mtime_ns");
                    // DateTime's 100 ns resolution is the native Windows file
                    // timestamp resolution. Linux finer precision is truncated.
                    if (file.Length != Number(stamp, "size")
                        || file.LastWriteTimeUtc.Ticks - UnixEpochTicks != nanos / 100) return false;
                }
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is FormatException || error is OverflowException)
            { return false; }
        }

        private static string Canonical(string path, Func<string, string> resolve)
        {
            return Path.GetFullPath(resolve == null ? path : resolve(path)).TrimEnd('\\', '/');
        }

        private static StringComparison PathComparison { get { return Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; } }
        private static bool SamePath(string left, string right)
        { return left != null && right != null && string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), PathComparison); }
        private static bool Linked(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        private static bool FreshLog(string path, DateTime startedUtc)
        {
            if (!File.Exists(path) || Linked(path)) return false;
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            return stamp >= startedUtc.ToUniversalTime().AddSeconds(-1) && stamp <= DateTime.UtcNow.AddSeconds(2);
        }
        private static string Tail(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bool truncated = stream.Length > MaximumLogTailBytes;
                if (truncated) stream.Seek(-MaximumLogTailBytes, SeekOrigin.End);
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    string text = reader.ReadToEnd();
                    int first = truncated ? text.IndexOf('\n') : -1;
                    return first >= 0 ? text.Substring(first + 1) : text;
                }
            }
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        { object raw; return value.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null; }
        private static string Text(Dictionary<string, object> value, string key)
        { object raw; return value.TryGetValue(key, out raw) ? raw as string : null; }
        private static long Number(Dictionary<string, object> value, string key)
        { object raw; return value.TryGetValue(key, out raw) ? Convert.ToInt64(raw, CultureInfo.InvariantCulture) : long.MinValue; }
        private static Dictionary<string, object> JsonObject(string text)
        {
#if NET8_0_OR_GREATER
            try { using (JsonDocument document = JsonDocument.Parse(text)) return ConvertJson(document.RootElement) as Dictionary<string, object>; }
            catch (JsonException) { return null; }
#else
            return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
#endif
        }
#if NET8_0_OR_GREATER
        private static object ConvertJson(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: return value.EnumerateObject().ToDictionary(item => item.Name, item => ConvertJson(item.Value), StringComparer.Ordinal);
                case JsonValueKind.Array: return value.EnumerateArray().Select(ConvertJson).ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number: return value.GetInt64();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
