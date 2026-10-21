using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.SelectionLoop
{
    public sealed class SelectionLoopInstallReport
    {
        public bool Success { get; internal set; }
        public bool RolledBack { get; internal set; }
        public int Changed { get; internal set; }
        public string BackupDirectory { get; internal set; }
        public List<string> Messages { get; } = new List<string>();
    }

    public sealed class SelectionLoopOwnedFile
    {
        public string Name { get; set; }
        public string InstalledSha256 { get; set; }
        public string OriginalSha256 { get; set; }
        public string OriginalBackup { get; set; }
    }

    public sealed class SelectionLoopOwnership
    {
        public int Schema { get; set; }
        public string GameRoot { get; set; }
        public List<SelectionLoopOwnedFile> Files { get; set; }
    }

    /// <summary>Installs only the optional companion; the original Moving Renders module is read-only.</summary>
    public static class SelectionLoopTransaction
    {
        public const string PluginName = "WWE2K26_SelectionLoop.dlp";
        public const string ConfigName = "WWE2K26_SelectionLoop.cfg";
        public const string OriginalPluginName = "WWE2K26_MovingRenders.dlp";
        public const string OriginalSha256 = "b752da3c9a9dc48dc377972a7183db0194c2b3b551074f7d0f0c9766432bf75c";
        public const string OwnershipName = ".gorilla-selection-loop-install.json";
        private const string BackupFolder = "GorillaSelectionLoopBackups";
        private const int MaximumPluginBytes = 4 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private sealed class Change
        {
            internal string Game, Name, Path, BeforeHash, AfterHash, Capture;
            internal byte[] Before, After;
            internal bool Applied;
        }

        public static SelectionLoopInstallReport Install(string packageRoot, string gameExecutable)
        { return Install(packageRoot, gameExecutable, GameClosed, null); }

        internal static SelectionLoopInstallReport Install(string packageRoot, string gameExecutable, Func<bool> gameClosed,
            Action<int, string> beforeChange)
        {
            var report = new SelectionLoopInstallReport();
            var changes = new List<Change>();
            try
            {
                string game = GameRoot(gameExecutable), plugins = Path.Combine(game, "Plugins");
                RequireClosed(gameClosed);
                string package = Path.GetFullPath(packageRoot);
                byte[] payload = ReadPackage(package), config = EnabledConfig(package);
                string payloadHash = Hash(payload), configHash = Hash(config);
                RequireOriginal(game);
                SelectionLoopOwnership old = ReadOwnership(game);
                var owned = new List<SelectionLoopOwnedFile>();
                Change dll = Snapshot(game, PluginName, payload), cfg = Snapshot(game, ConfigName, config);
                changes.Add(dll); changes.Add(cfg);
                if (old != null)
                {
                    foreach (Change item in changes)
                    {
                        SelectionLoopOwnedFile record = old.Files.FirstOrDefault(file => file.Name == item.Name);
                        if (record == null || item.BeforeHash != record.InstalledSha256)
                            throw new IOException("An installed companion file has changed or was already restored. It was preserved: " + item.Name + ". Uninstall or restore your previous files before installing again.");
                        ValidateBackup(game, record);
                        owned.Add(Clone(record));
                    }
                    if (changes.All(item => item.BeforeHash == item.AfterHash))
                    {
                        report.Success = true; report.Messages.Add("The optional companion is already installed and enabled."); return report;
                    }
                }
                string work = WorkDirectory(game); report.BackupDirectory = work;
                foreach (Change item in changes)
                {
                    BackupBefore(work, item);
                    if (old == null)
                        owned.Add(new SelectionLoopOwnedFile { Name = item.Name, InstalledSha256 = item.AfterHash,
                            OriginalSha256 = item.BeforeHash,
                            OriginalBackup = item.Before == null ? null : Relative(plugins, Path.Combine(work, item.Name + ".before.bak")) });
                    else owned.Single(file => file.Name == item.Name).InstalledSha256 = item.AfterHash;
                }
                changes.Add(Snapshot(game, OwnershipName, Encode(new SelectionLoopOwnership { Schema = 1, GameRoot = game, Files = owned })));
                BackupBefore(work, changes.Last());
                Apply(game, work, changes, gameClosed, beforeChange, () => {
                    RequireOriginal(game);
                    if (Hash(ReadPackage(package)) != payloadHash || Hash(EnabledConfig(package)) != configHash)
                        throw new IOException("The companion package changed during installation. Extract a fresh package and retry.");
                });
                report.Success = true; report.Changed = changes.Count(item => item.Applied && item.Name != OwnershipName);
                report.Messages.Add(report.Changed == 0 ? "The optional companion is already installed and enabled." : "The optional companion was installed and enabled. The original Moving Renders files were preserved.");
            }
            catch (Exception error) when (Expected(error))
            {
                report.Messages.Add(error.Message);
                Rollback(changes, report);
            }
            return report;
        }

        public static SelectionLoopInstallReport Uninstall(string gameExecutable)
        { return Uninstall(gameExecutable, GameClosed, null); }

        internal static SelectionLoopInstallReport Uninstall(string gameExecutable, Func<bool> gameClosed, Action<int, string> beforeChange)
        {
            var report = new SelectionLoopInstallReport();
            var changes = new List<Change>();
            try
            {
                string game = GameRoot(gameExecutable);
                RequireClosed(gameClosed);
                SelectionLoopOwnership state = ReadOwnership(game);
                if (state == null) { report.Success = true; report.Messages.Add("This installer has no owned companion files to remove."); return report; }
                var preserved = new List<SelectionLoopOwnedFile>();
                foreach (SelectionLoopOwnedFile record in state.Files)
                {
                    Change item = Snapshot(game, record.Name, null);
                    if (item.BeforeHash != record.InstalledSha256)
                    {
                        preserved.Add(Clone(record));
                        report.Messages.Add("Changed or missing file preserved: " + record.Name + ". Its backup and ownership record were kept.");
                        continue;
                    }
                    byte[] original = ValidateBackup(game, record);
                    item.After = original; item.AfterHash = original == null ? null : Hash(original); changes.Add(item);
                }
                string work = WorkDirectory(game); report.BackupDirectory = work;
                foreach (Change item in changes) BackupBefore(work, item);
                changes.Add(Snapshot(game, OwnershipName, preserved.Count == 0 ? null
                    : Encode(new SelectionLoopOwnership { Schema = 1, GameRoot = game, Files = preserved })));
                BackupBefore(work, changes.Last());
                Apply(game, work, changes, gameClosed, beforeChange, null);
                report.Changed = changes.Count(item => item.Applied && item.Name != OwnershipName);
                report.Success = preserved.Count == 0;
                report.Messages.Add(preserved.Count == 0 ? "The optional companion was uninstalled. Original companion files, if any, were restored."
                    : "Unchanged owned files were restored or removed. Modified files remain in place.");
            }
            catch (Exception error) when (Expected(error))
            {
                report.Messages.Add(error.Message); Rollback(changes, report);
            }
            return report;
        }

        private static void Apply(string game, string work, List<Change> changes, Func<bool> gameClosed,
            Action<int, string> beforeChange, Action revalidate)
        {
            for (int index = 0; index < changes.Count; index++)
            {
                Change item = changes[index];
                if (item.BeforeHash == item.AfterHash) continue;
                beforeChange?.Invoke(index, item.Name);
                RequireClosed(gameClosed); revalidate?.Invoke();
                Safe(game, item.Path); Safe(game, work);
                if (CurrentHash(item.Path) != item.BeforeHash)
                    throw new IOException("A destination changed during the operation and was preserved: " + item.Name);
                if (item.After == null)
                {
                    item.Capture = Path.Combine(work, item.Name + ".removed-" + Guid.NewGuid().ToString("N") + ".bak");
                    File.Move(item.Path, item.Capture); item.Applied = true;
                    if (CurrentHash(item.Capture) != item.BeforeHash)
                        throw new IOException("A destination changed while it was being removed; its captured bytes were preserved: " + item.Name);
                    continue;
                }
                string temporary = item.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    WriteNew(temporary, item.After);
                    if (item.Before == null) File.Move(temporary, item.Path);
                    else
                    {
                        item.Capture = Path.Combine(work, item.Name + ".replaced-" + Guid.NewGuid().ToString("N") + ".bak");
                        File.Replace(temporary, item.Path, item.Capture);
                    }
                    item.Applied = true;
                    if (item.Before != null && CurrentHash(item.Capture) != item.BeforeHash)
                        throw new IOException("A destination changed during replacement; its captured bytes were preserved: " + item.Name);
                    if (CurrentHash(item.Path) != item.AfterHash) throw new IOException("An installed file changed before verification: " + item.Name);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static void Rollback(List<Change> changes, SelectionLoopInstallReport report)
        {
            List<Change> applied = changes.Where(item => item.Applied).Reverse().ToList();
            if (applied.Count == 0) return;
            bool restored = true;
            foreach (Change item in applied)
            {
                try
                {
                    Safe(item.Game, item.Path); Safe(item.Game, report.BackupDirectory);
                    if (item.After != null)
                    {
                        if (!File.Exists(item.Path)) throw new IOException("The installed file is no longer present.");
                        string captured = Path.Combine(report.BackupDirectory, item.Name + ".rollback-" + Guid.NewGuid().ToString("N") + ".bak");
                        File.Move(item.Path, captured);
                        if (CurrentHash(captured) != item.AfterHash)
                        {
                            if (!File.Exists(item.Path)) File.Move(captured, item.Path);
                            throw new IOException("An externally changed file was preserved.");
                        }
                    }
                    // Move into a vacant slot only: a new external file cannot be overwritten.
                    if (item.Before != null)
                    {
                        string captured = item.Capture;
                        if (string.IsNullOrWhiteSpace(captured) || !File.Exists(captured))
                            throw new IOException("The exact original capture is unavailable.");
                        File.Move(captured, item.Path);
                    }
                    else if (File.Exists(item.Path)) throw new IOException("A new external destination was preserved.");
                    item.Applied = false;
                }
                catch (Exception error) when (Expected(error))
                { restored = false; report.Messages.Add("Could not fully roll back " + item.Name + ": " + error.Message + " Backups: " + report.BackupDirectory); }
            }
            report.RolledBack = restored; report.Changed = 0;
        }

        private static byte[] ReadPackage(string root)
        {
            string manifestPath = Path.Combine(root, "package.json"), payloadPath = Path.Combine(root, PluginName);
            Safe(root, manifestPath); Safe(root, payloadPath);
            byte[] manifestBytes = Read(manifestPath, 16384);
            string json = Utf8.GetString(manifestBytes);
            if (json.IndexOf('\\') >= 0 || json.Count(character => character == ':') != 4)
                throw new InvalidDataException("The companion package manifest must contain four plain, unique fields.");
            Dictionary<string, object> manifest = DecodeDictionary(manifestBytes);
            if (manifest == null || manifest.Count != 4 || !manifest.ContainsKey("schema") || !manifest.ContainsKey("filename") || !manifest.ContainsKey("size") || !manifest.ContainsKey("sha256")
                || Number(manifest["schema"]) != 1
                || !string.Equals(Convert.ToString(manifest["filename"], CultureInfo.InvariantCulture), PluginName, StringComparison.Ordinal))
                throw new InvalidDataException("The companion package manifest is invalid.");
            string expected = Convert.ToString(manifest["sha256"], CultureInfo.InvariantCulture);
            long size = Number(manifest["size"]);
            if (!Sha(expected) || size < 1 || size > MaximumPluginBytes) throw new InvalidDataException("The companion package checksum or size is invalid.");
            byte[] payload = Read(payloadPath, MaximumPluginBytes);
            if (payload.LongLength != size || Hash(payload) != expected) throw new InvalidDataException("The companion DLL does not match package.json. Extract the full package again.");
            return payload;
        }

        private static byte[] EnabledConfig(string root)
        {
            string path = Path.Combine(root, ConfigName); Safe(root, path);
            string text = Utf8.GetString(Read(path, 4096));
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("The companion configuration contains a NUL byte.");
            var output = new List<string>(); int found = 0;
            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) { output.Add(raw); continue; }
                string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 2 || fields[0] != "enabled" || fields[1] != "0" || ++found != 1)
                    throw new InvalidDataException("The packaged companion configuration must contain exactly one disabled 'enabled 0' setting.");
                output.Add("enabled 1");
            }
            if (found != 1) throw new InvalidDataException("The companion configuration is missing its disabled setting.");
            return Utf8.GetBytes(string.Join("\n", output));
        }

        private static SelectionLoopOwnership ReadOwnership(string game)
        {
            string path = Path.Combine(game, "Plugins", OwnershipName); Safe(game, path);
            if (!File.Exists(path)) return null;
            SelectionLoopOwnership state = DecodeOwnership(Read(path, 32768));
            if (state == null || state.Schema != 1 || !Same(state.GameRoot, game) || state.Files == null || state.Files.Count < 1 || state.Files.Count > 2
                || state.Files.Any(file => file == null || (file.Name != PluginName && file.Name != ConfigName) || !Sha(file.InstalledSha256)
                    || (file.OriginalSha256 != null && !Sha(file.OriginalSha256)) || ((file.OriginalSha256 == null) != (file.OriginalBackup == null)))
                || state.Files.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != state.Files.Count)
                throw new InvalidDataException("The companion ownership record is invalid. It was preserved; restore your previous files manually from its backup.");
            foreach (SelectionLoopOwnedFile file in state.Files)
                if (file.OriginalBackup != null) BackupPath(game, file);
            return state;
        }

        private static byte[] ValidateBackup(string game, SelectionLoopOwnedFile file)
        {
            if (file.OriginalSha256 == null) return null;
            byte[] bytes = Read(BackupPath(game, file), MaximumPluginBytes);
            if (Hash(bytes) != file.OriginalSha256) throw new InvalidDataException("An original companion backup changed and was preserved: " + file.Name);
            return bytes;
        }

        private static string BackupPath(string game, SelectionLoopOwnedFile file)
        {
            string relative = (file.OriginalBackup ?? "").Replace('\\', '/');
            string[] parts = relative.Split('/'); Guid id;
            if (parts.Length != 3 || parts[0] != BackupFolder || !Guid.TryParseExact(parts[1], "N", out id) || parts[2] != file.Name + ".before.bak")
                throw new InvalidDataException("A companion backup path is invalid.");
            string path = Path.Combine(game, "Plugins", BackupFolder, parts[1], parts[2]); Safe(game, path); return path;
        }

        private static string GameRoot(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable) || !string.Equals(Path.GetFileName(executable), "WWE2K26_x64.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new IOException("Choose your installed WWE2K26_x64.exe.");
            string game = Path.GetDirectoryName(Path.GetFullPath(executable));
            Safe(game, Path.GetFullPath(executable));
            string plugins = Path.Combine(game, "Plugins");
            if (!Directory.Exists(plugins)) throw new IOException("This game folder has no Plugins directory. Install CakeHook first.");
            Safe(game, plugins); return game;
        }

        private static void RequireOriginal(string game)
        {
            string path = Path.Combine(game, "Plugins", OriginalPluginName); Safe(game, path);
            if (!File.Exists(path) || Hash(Read(path, MaximumPluginBytes)) != OriginalSha256)
                throw new InvalidDataException("Install requires the supported original Moving Renders 4.21 DLL. The existing original plugin was preserved.");
        }
        private static bool GameClosed() { return Process.GetProcessesByName("WWE2K26_x64").Length == 0; }
        private static void RequireClosed(Func<bool> closed) { if (closed == null || !closed()) throw new IOException("Close WWE 2K26 before installing or uninstalling the companion."); }
        private static Change Snapshot(string game, string name, byte[] after)
        {
            string path = Path.Combine(game, "Plugins", name); Safe(game, path);
            byte[] before = File.Exists(path) ? Read(path, MaximumPluginBytes) : null;
            return new Change { Game = game, Name = name, Path = path, Before = before, BeforeHash = before == null ? null : Hash(before), After = after, AfterHash = after == null ? null : Hash(after) };
        }
        private static void BackupBefore(string work, Change change) { if (change.Before != null) WriteNew(Path.Combine(work, change.Name + ".before.bak"), change.Before); }
        private static string WorkDirectory(string game)
        {
            string folder = Path.Combine(game, "Plugins", BackupFolder, Guid.NewGuid().ToString("N")); Safe(game, folder); Directory.CreateDirectory(folder); Safe(game, folder); return folder;
        }
        private static void Safe(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), full = Path.GetFullPath(path);
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("A companion path escapes its selected folder.");
            for (string current = full; !Same(current, fullRoot); current = Path.GetDirectoryName(current))
            {
                if (string.IsNullOrEmpty(current)) throw new IOException("A companion path is invalid.");
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A companion target or backup is a link and was preserved: " + current);
            }
            if (Directory.Exists(full) && (Path.GetExtension(full) == ".dlp" || Path.GetExtension(full) == ".cfg" || Path.GetExtension(full) == ".json"))
                throw new IOException("A companion file is occupied by a directory: " + full);
        }
        private static byte[] Read(string path, int limit)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > limit) throw new InvalidDataException("A companion file is empty or too large: " + Path.GetFileName(path));
                byte[] bytes = new byte[checked((int)input.Length)]; int read = 0;
                while (read < bytes.Length) { int count = input.Read(bytes, read, bytes.Length - read); if (count == 0) throw new IOException("A companion file changed while it was read."); read += count; }
                if (input.ReadByte() != -1) throw new IOException("A companion file changed while it was read."); return bytes;
            }
        }
        private static string CurrentHash(string path) { return File.Exists(path) ? Hash(Read(path, MaximumPluginBytes)) : null; }
        private static string Hash(byte[] value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        private static bool Sha(string value) { return value != null && value.Length == 64 && value.All(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f'); }
        private static long Number(object value) { if (!(value is int) && !(value is long) && !(value is uint)) throw new InvalidDataException("A companion manifest number is invalid."); return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        private static bool Same(string left, string right) { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        private static string Relative(string root, string file) { return file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'); }
        private static SelectionLoopOwnedFile Clone(SelectionLoopOwnedFile file) { return new SelectionLoopOwnedFile { Name = file.Name, InstalledSha256 = file.InstalledSha256, OriginalSha256 = file.OriginalSha256, OriginalBackup = file.OriginalBackup }; }
        private static void WriteNew(string path, byte[] bytes) { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } }
        private static bool Expected(Exception error) { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException || error is System.Security.SecurityException || error is FormatException || error is OverflowException || error is System.ComponentModel.Win32Exception || error is NotSupportedException; }
        private static byte[] Encode(object value)
        {
#if NET8_0_OR_GREATER
            return Utf8.GetBytes(JsonSerializer.Serialize(value));
#else
            return Utf8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 32768 }.Serialize(value));
#endif
        }
        private static SelectionLoopOwnership DecodeOwnership(byte[] bytes)
        {
#if NET8_0_OR_GREATER
            try { return JsonSerializer.Deserialize<SelectionLoopOwnership>(Utf8.GetString(bytes)); } catch (JsonException error) { throw new InvalidDataException("The companion ownership JSON is invalid.", error); }
#else
            return new JavaScriptSerializer { MaxJsonLength = 32768 }.Deserialize<SelectionLoopOwnership>(Utf8.GetString(bytes));
#endif
        }
        private static Dictionary<string, object> DecodeDictionary(byte[] bytes)
        {
#if NET8_0_OR_GREATER
            try
            {
                using (JsonDocument document = JsonDocument.Parse(bytes))
                {
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                        result.Add(property.Name, property.Value.ValueKind == JsonValueKind.String ? (object)property.Value.GetString() : property.Value.GetInt64());
                    return result;
                }
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException || error is FormatException) { throw new InvalidDataException("The companion package JSON is invalid.", error); }
#else
            return new JavaScriptSerializer { MaxJsonLength = 16384 }.Deserialize<Dictionary<string, object>>(Utf8.GetString(bytes));
#endif
        }
    }
}
