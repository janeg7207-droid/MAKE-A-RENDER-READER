using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class MatchCardReadinessReport
    {
        public List<string> Issues { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public List<string> MissingScreenPaths { get; } = new List<string>();
        public int MissingScreenFiles { get { return MissingScreenPaths.Count; } }
        public int ExpectedScreenFiles { get; internal set; }
        public int? WrestlerId { get; internal set; }
        public string Screens { get; internal set; }
        public string Ending { get; internal set; }
        public string InstalledPath { get; internal set; }
        public GmrLibraryAuditReport Library { get; internal set; }
        // This reports files and settings only. It cannot observe the running game,
        // CakeHook loading, the selected arena, or whether Discovery has baked them.
        public bool IsConfigured { get { return Issues.Count == 0; } }
        public string Summary { get { return IsConfigured ? "Match card files and settings are configured. In-game playback still needs checking."
            : "Match card setup needs attention: " + string.Join(" ", Issues); } }
    }

    public sealed class MatchCardSetupReport
    {
        public bool HasResult { get; internal set; }
        public bool Success { get; internal set; }
        public bool BakeRequired { get; internal set; }
        public bool IsCheck { get; internal set; }
        public int Added { get; internal set; }
        public int Patched { get; internal set; }
        public List<string> Failed { get; } = new List<string>();
        public string Summary { get; internal set; }
    }

    /// <summary>Read-only diagnostics for the original moving-render installation.</summary>
    public static class MatchCardReadiness
    {
        private const int MaximumJsonBytes = 16 * 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        // The original RunPy task has no cancellation argument. A closed check
        // dialog must release its Busy lease even if that task stops responding.
        // Observe any later fault without publishing its result to the closed UI.
        internal static async Task<T> WaitForCheckAsync<T>(Task<T> task, CancellationToken cancellation)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (cancellation.CanBeCanceled)
            {
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellation.Register(() => stopped.TrySetResult(true)))
                {
                    if (await Task.WhenAny(task, stopped.Task) != task || cancellation.IsCancellationRequested)
                    {
                        ObserveCheckFault(task);
                        cancellation.ThrowIfCancellationRequested();
                    }
                }
            }
            T result = await task;
            cancellation.ThrowIfCancellationRequested();
            return result;
        }

        private static void ObserveCheckFault(Task task)
        {
            task.ContinueWith(completed => { var ignored = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public static MatchCardReadinessReport Evaluate(string appRoot, string gameRoot, string discoveryRoot,
            string gmrPath, int? expectedLogicalId)
        {
            return Evaluate(appRoot, gameRoot, discoveryRoot, gmrPath, expectedLogicalId, CancellationToken.None);
        }

        public static MatchCardReadinessReport Evaluate(string appRoot, string gameRoot, string discoveryRoot,
            string gmrPath, int? expectedLogicalId, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var report = new MatchCardReadinessReport();
            if (expectedLogicalId.HasValue && (expectedLogicalId < 1 || expectedLogicalId > 65535))
                report.Issues.Add("The selected logical wrestler ID is invalid.");
            try { InspectRender(report, gmrPath, expectedLogicalId, cancellation); }
            catch (Exception error) when (ReadFailure(error)) { report.Issues.Add("The render could not be checked: " + error.Message); }
            try { InspectInstallation(report, gameRoot, gmrPath, cancellation); }
            catch (Exception error) when (ReadFailure(error)) { report.Issues.Add("The installed render or plugin could not be checked: " + error.Message); }
            try { InspectScreens(report, appRoot, discoveryRoot, cancellation); }
            catch (Exception error) when (ReadFailure(error)) { report.Issues.Add("The Discovery screen files could not be checked: " + error.Message); }
            report.Notes.Add("Screen files must be baked in Discovery after Game setup changes them. Their presence alone does not confirm a bake.");
            report.Notes.Add("Close WWE 2K26 before installing, then restart it. CakeHook and the Moving Renders plugin must load in the game.");
            report.Notes.Add("Roster selection uses picture references; match cards use the logical wrestler ID. Selection moving does not confirm that the match-card slot or game hook is correct.");
            return report;
        }

        private static void InspectRender(MatchCardReadinessReport report, string path, int? expectedId, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            { report.Issues.Add("Make a full Gorilla Render before installing; a Preview is not a game render."); return; }
            Dictionary<string, object> metadata;
            uint frames;
            if (!ReadRender(path, out metadata, out frames, cancellation))
            { report.Issues.Add("The selected file is not a complete Gorilla Render with a valid video header and metadata."); return; }
            object value;
            int id;
            if (!metadata.TryGetValue("wrestler_id", out value) || !Integer(value, out id) || id < 1 || id > 65535)
                report.Issues.Add("The render has no valid logical wrestler ID.");
            else
            {
                report.WrestlerId = id;
                if (expectedId.HasValue && id != expectedId.Value)
                    report.Issues.Add("The render targets wrestler " + id.ToString(CultureInfo.InvariantCulture)
                        + ", but the selected roster slot is " + expectedId.Value.ToString(CultureInfo.InvariantCulture) + ". Make it again for the selected slot.");
                else report.Notes.Add("Render targets logical roster wrestler " + id.ToString(CultureInfo.InvariantCulture) + ".");
            }
            report.Screens = metadata.TryGetValue("screens", out value) ? value as string : "both";
            if (report.Screens != "both" && report.Screens != "matchcard")
                report.Issues.Add(report.Screens == "select" ? "Match card is turned off in this render. Tick Match card and make it again."
                    : "The render has an unknown screen setting; make it again with Match card selected.");
            report.Ending = metadata.TryGetValue("end", out value) ? value as string : "loop";
            if (report.Ending == "still" || frames == 1)
                report.Issues.Add("This is a still render. Choose Loop or Play once and make it again for movement.");
            else if (report.Ending == "freeze") report.Notes.Add("Ending is Play once + flash freeze: it moves once, then holds. Choose Loop and make it again for continuous movement.");
            else if (report.Ending != "loop") report.Issues.Add("The render has an unknown ending setting.");
        }

        private static void InspectInstallation(MatchCardReadinessReport report, string gameRoot, string source, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            { report.Issues.Add("Choose the installed WWE 2K26 game folder."); return; }
            string plugins = Path.Combine(gameRoot, "Plugins");
            report.Notes.Add("Plugin log: " + Path.Combine(plugins, "WWE2K26_MovingRenders.log"));
            report.Notes.Add("If every arena stays static after setup, baking and restarting, inspect the plugin log and GorillaRenders/_scan_ids.txt for the match-card ID lookup and game-build compatibility.");
            if (!Directory.Exists(plugins))
            { report.Issues.Add("The game has no Plugins folder. Install CakeHook before moving renders can load."); return; }
            string plugin = Path.Combine(plugins, "WWE2K26_MovingRenders.dlp");
            if (!File.Exists(plugin) || new FileInfo(plugin).Length == 0)
                report.Issues.Add("The Moving Renders plugin is missing from the game. Install the render again with the game closed.");
            string config = Path.Combine(plugins, "WWE2K26_MovingRenders.cfg");
            if (!File.Exists(config)) report.Issues.Add("The Moving Renders configuration is missing; its enabled state cannot be checked.");
            else
            {
                if (new FileInfo(config).Length > 1024 * 1024) throw new InvalidDataException("The Moving Renders configuration is too large.");
                string enabled = null;
                foreach (string input in File.ReadAllLines(config))
                {
                    string line = input.Trim();
                    int comment = line.IndexOf('#');
                    int semicolon = line.IndexOf(';');
                    int slash = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment < 0 || (semicolon >= 0 && semicolon < comment)) comment = semicolon;
                    if (comment < 0 || (slash >= 0 && slash < comment)) comment = slash;
                    if (comment >= 0) line = line.Substring(0, comment).Trim();
                    string[] words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length >= 2 && string.Equals(words[0], "enabled", StringComparison.OrdinalIgnoreCase)) enabled = words[1];
                }
                if (enabled == "0") report.Issues.Add("Moving Renders is disabled in WWE2K26_MovingRenders.cfg (enabled 0). Set enabled 1 with the game closed.");
                else if (enabled != "1") report.Issues.Add("The configuration does not explicitly set enabled 1; the plugin enabled state is unknown.");
            }
            report.Library = GmrLibraryAudit.Inspect(Path.Combine(plugins, "GorillaRenders"), plugin, cancellation);
            AppendLibrary(report, report.Library);
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
            report.InstalledPath = Path.Combine(plugins, "GorillaRenders", Path.GetFileName(source));
            if (!File.Exists(report.InstalledPath))
                report.Issues.Add("This render is not installed in the selected game. Press Install into the game.");
            else if (!SameContents(source, report.InstalledPath, cancellation))
                report.Issues.Add("The installed copy differs from this render. Install the current render again.");
            else report.Notes.Add("The installed copy matches this render exactly.");
        }

        private static void AppendLibrary(MatchCardReadinessReport report, GmrLibraryAuditReport library)
        {
            report.Notes.Add("Installed GMR library: " + library.RootFiles.ToString(CultureInfo.InvariantCulture) + " root file(s), "
                + library.HeaderCompatibleFiles.ToString(CultureInfo.InvariantCulture) + " header-compatible candidate(s), "
                + library.NestedFiles.ToString(CultureInfo.InvariantCulture) + " nested file(s). "
                + (library.Complete ? "The bounded inspection completed." : "Inspection was incomplete; uninspected files are not classified."));
            report.Notes.Add(library.Original421Verified
                ? "Installed MovingRenders 4.21 matches the verified original; its measured scan limits apply."
                : "The installed plugin does not match the verified original 4.21 binary. The library findings below describe original 4.21 rules, not confirmed limits of this installed version.");
            foreach (string problem in library.Problems.Take(20))
            {
                if (library.Original421Verified) report.Issues.Add(problem);
                else report.Notes.Add("Original 4.21 compatibility: " + problem);
            }
            int filesWithProblems = library.Records.Count(item => item.Problems.Count != 0);
            if (filesWithProblems != 0 && library.Original421Verified)
                report.Issues.Add(filesWithProblems.ToString(CultureInfo.InvariantCulture) + " installed GMR file(s) could not be verified against the original plugin's header/metadata rules. See file details below.");
            var detailedFiles = library.Records.Where(row => row.Problems.Count != 0 || row.Notes.Count != 0).ToList();
            foreach (GmrLibraryRecord item in detailedFiles.Take(30))
                report.Notes.Add(Path.GetFileName(item.Path) + ": " + string.Join(" ", item.Problems.Concat(item.Notes)));
            if (library.Problems.Count > 20 || detailedFiles.Count > 30)
                report.Notes.Add("Additional library findings were omitted from this summary. The native _scan_ids.txt and plugin log can identify the actual accepted files.");
            report.Notes.AddRange(library.Notes);
        }

        private static void InspectScreens(MatchCardReadinessReport report, string appRoot, string discoveryRoot, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(appRoot))
            { report.Issues.Add("The app's supplied screen pack could not be located."); return; }
            string pack = Path.Combine(appRoot, "data", "gamepack", "screens", "BakeMe");
            if (!Directory.Exists(pack))
            { report.Issues.Add("The app's supplied screen pack is missing. Extract the full ZIP before Game setup."); return; }
            if (string.IsNullOrWhiteSpace(discoveryRoot) || !Directory.Exists(discoveryRoot))
            { report.Issues.Add("Choose the Discovery data folder used by the game, then run Game setup and bake in Discovery."); return; }
            string fullPack = Path.GetFullPath(pack).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullData = Path.GetFullPath(discoveryRoot);
            var pending = new Stack<string>(); pending.Push(fullPack);
            while (pending.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The supplied screen pack contains a linked directory and could not be checked safely.");
                foreach (string child in Directory.GetDirectories(directory)) pending.Push(child);
                foreach (string file in Directory.GetFiles(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    string relative = file.Substring(fullPack.Length + 1);
                    report.ExpectedScreenFiles++;
                    // Existing mod layouts can differ intentionally; do not require
                    // byte-for-byte equality with the supplied stock screen pack.
                    string destination = Path.Combine(fullData, relative);
                    if (!File.Exists(destination) || new FileInfo(destination).Length == 0) report.MissingScreenPaths.Add(relative);
                }
            }
            if (report.ExpectedScreenFiles == 0) report.Issues.Add("The supplied screen pack is empty. Extract the full ZIP.");
            else if (report.MissingScreenFiles > 0)
                report.Issues.Add(report.MissingScreenFiles.ToString(CultureInfo.InvariantCulture)
                    + " required screen or placeholder movie files are missing or empty in Discovery. Run Game setup, then bake once in Discovery.");
            else report.Notes.Add("All " + report.ExpectedScreenFiles.ToString(CultureInfo.InvariantCulture)
                + " supplied screen and placeholder paths exist. Custom arena clip layers and the game's baked files still need checking.");
        }

        public static MatchCardSetupReport FromSetupResult(IEnumerable<string> lines)
        {
            var report = new MatchCardSetupReport { Summary = "Game setup returned no readable result. Run Game setup again before relying on moving match cards." };
            if (lines == null) return report;
            string json = null;
            foreach (string line in lines)
                if (line != null && line.StartsWith("RESULT ", StringComparison.Ordinal)) json = line.Substring(7);
            if (json == null || json.Length > MaximumJsonBytes) return report;
            try
            {
                var data = Deserialize(json);
                object added, patched, failed, bake, check;
                int addCount, patchCount;
                if (data == null || !data.TryGetValue("added", out added) || !Integer(added, out addCount) || addCount < 0
                    || !data.TryGetValue("patched", out patched) || !Integer(patched, out patchCount) || patchCount < 0
                    || !data.TryGetValue("failed", out failed) || !(failed is IList)
                    || !data.TryGetValue("bake", out bake) || !(bake is bool)) return report;
                foreach (object error in (IList)failed)
                {
                    if (!(error is string)) return report;
                    report.Failed.Add((string)error);
                }
                if (data.TryGetValue("check", out check) && !(check is bool)) return report;
                report.HasResult = true; report.Success = report.Failed.Count == 0;
                report.Added = addCount; report.Patched = patchCount;
                report.BakeRequired = (bool)bake; report.IsCheck = check is bool && (bool)check;
                string action = report.IsCheck ? "Game setup check" : "Game setup";
                report.Summary = !report.Success ? action + " could not prepare " + report.Failed.Count.ToString(CultureInfo.InvariantCulture)
                    + " file(s). Fix these setup errors before relying on moving match cards: " + string.Join("; ", report.Failed)
                    : report.IsCheck && (addCount > 0 || patchCount > 0) ? "Game setup check found missing or unpatched screen files. Run Game setup, then bake once in Discovery."
                    : report.BakeRequired ? "Game setup changed screen files. BAKE ONCE in Discovery, then restart the game."
                    : "Game setup reports no screen changes. This does not verify the previous bake or in-game playback.";
                if (!report.Success && report.BakeRequired) report.Summary += " Setup also reports that baking is required after the errors are fixed.";
                return report;
            }
            catch (Exception error) when (ReadFailure(error)) { return report; }
        }

        /// <summary>
        /// Explicit user action only: enable an existing plugin configuration without
        /// replacing its other settings. A missing configuration or enabled key is a
        /// safe failure; the caller should install the original plugin first.
        /// </summary>
        public static bool EnablePlugin(string gameRoot)
        {
            string temporary = null;
            try
            {
                if (string.IsNullOrWhiteSpace(gameRoot)) return false;
                string config = Path.GetFullPath(Path.Combine(gameRoot, "Plugins", "WWE2K26_MovingRenders.cfg"));
                if (!File.Exists(config) || new FileInfo(config).Length > 1024 * 1024
                    || (File.GetAttributes(config) & FileAttributes.ReparsePoint) != 0) return false;
                byte[] before = File.ReadAllBytes(config);
                bool bom = before.Length >= 3 && before[0] == 0xef && before[1] == 0xbb && before[2] == 0xbf;
                string original = Utf8.GetString(before, bom ? 3 : 0, before.Length - (bom ? 3 : 0));
                bool found = false;
                string enabled = Regex.Replace(original, @"^([ \t]*enabled[ \t]+)([^\s#;]+)", match =>
                {
                    found = true;
                    return match.Groups[1].Value + "1";
                }, RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!found) return false;
                if (original == enabled) return true;
                byte[] body = Utf8.GetBytes(enabled);
                byte[] after = new byte[body.Length + (bom ? 3 : 0)];
                if (bom) { after[0] = 0xef; after[1] = 0xbb; after[2] = 0xbf; }
                Array.Copy(body, 0, after, bom ? 3 : 0, body.Length);
                temporary = Path.Combine(Path.GetDirectoryName(config), ".moving-renders-enable-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(after, 0, after.Length); output.Flush(true); }
                // Retain the first original configuration and never replace a newer
                // user edit made while the confirmation dialog was open.
                if (!File.ReadAllBytes(config).SequenceEqual(before)) return false;
                string backup = config + ".before-enable.bak";
                if (!File.Exists(backup))
                {
                    try
                    {
                        using (var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { output.Write(before, 0, before.Length); output.Flush(true); }
                    }
                    catch (IOException) { if (!File.Exists(backup)) throw; }
                }
                if (!File.ReadAllBytes(config).SequenceEqual(before)) return false;
                File.Replace(temporary, config, null); temporary = null;
                return true;
            }
            catch (Exception error) when (ReadFailure(error)) { return false; }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool ReadRender(string path, out Dictionary<string, object> metadata, out uint frames, CancellationToken cancellation)
        {
            metadata = null; frames = 0;
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(source, Utf8, true))
            {
                if (source.Length < 60) return false;
                source.Position = source.Length - 8;
                uint length = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x31524D47 || length < 2 || length > MaximumJsonBytes || length > source.Length - 60) return false;
                long videoLength = source.Length - length - 8;
                source.Position = 0;
                if (reader.ReadUInt32() != 0x694B4942 || (long)reader.ReadUInt32() + 8 != videoLength) return false;
                frames = reader.ReadUInt32(); reader.ReadUInt32();
                if (frames == 0 || reader.ReadUInt32() != frames || 44L + (frames + 1L) * 4 > videoLength) return false;
                if (reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 1024) return false;
                uint fpsNumerator = reader.ReadUInt32(), fpsDenominator = reader.ReadUInt32();
                if (fpsDenominator == 0 || fpsNumerator != fpsDenominator * 30L || (reader.ReadUInt32() & 1048576) == 0 || reader.ReadUInt32() != 0) return false;
                for (long frame = 0; frame < frames; frame++)
                {
                    if ((frame & 255) == 0) cancellation.ThrowIfCancellationRequested();
                    uint offset = reader.ReadUInt32();
                    if ((offset & 1) == 0 || (offset & ~1U) < 44 + (frames + 1L) * 4 || (offset & ~1U) >= videoLength) return false;
                }
                if ((reader.ReadUInt32() & ~1U) != videoLength) return false;
                source.Position = videoLength;
                byte[] bytes = reader.ReadBytes((int)length);
                if (bytes.Length != length) return false;
                metadata = Deserialize(Utf8.GetString(bytes));
                object format, version;
                int versionNumber;
                return metadata != null && metadata.TryGetValue("format", out format) && (format as string) == "GorillaRender"
                    && metadata.TryGetValue("version", out version) && Integer(version, out versionNumber) && versionNumber == 1;
            }
        }

        private static bool SameContents(string first, string second, CancellationToken cancellation)
        {
            if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
            using (var a = File.OpenRead(first)) using (var b = File.OpenRead(second))
                return Hash(a, cancellation).SequenceEqual(Hash(b, cancellation));
        }

        private static byte[] Hash(Stream source, CancellationToken cancellation)
        {
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[81920]; int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                { cancellation.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, buffer, 0); }
                cancellation.ThrowIfCancellationRequested(); sha.TransformFinalBlock(buffer, 0, 0); return sha.Hash;
            }
        }

        private static bool Integer(object value, out int result)
        {
            result = 0;
            if (value == null || value is string || value is bool || value.GetType().IsEnum) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal:
                    decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    if (number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue) return false;
                    result = (int)number; return true;
                default: return false;
            }
        }

        private static bool ReadFailure(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is FormatException || error is OverflowException
                || error is NotSupportedException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                ;
        }

        internal static Dictionary<string, object> Deserialize(string json)
        {
#if NET8_0_OR_GREATER
            using (var document = JsonDocument.Parse(json)) return ConvertJson(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumJsonBytes, RecursionLimit = 100 }.DeserializeObject(json) as Dictionary<string, object>;
#endif
        }

#if NET8_0_OR_GREATER
        private static object ConvertJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject()) dictionary.Add(property.Name, ConvertJson(property.Value));
                    return dictionary;
                case JsonValueKind.Array:
                    var array = new List<object>(); foreach (JsonElement item in element.EnumerateArray()) array.Add(ConvertJson(item)); return array;
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    long integer; decimal number;
                    if (element.TryGetInt64(out integer)) return integer;
                    if (element.TryGetDecimal(out number)) return number;
                    return element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
