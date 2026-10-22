using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public enum InstalledRenderScreenMode { RosterSelection, MatchCard, Both }

    public sealed class InstalledRenderScreenChoice
    {
        public string Path { get; set; }
        public int WrestlerId { get; set; }
    }

    public sealed class InstalledRenderScreenReport
    {
        public int Changed { get; internal set; }
        public int Unchanged { get; internal set; }
        public string BackupDirectory { get; internal set; }
        public bool RollbackComplete { get; internal set; } = true;
        public List<string> Failed { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public string Summary { get { return Changed.ToString(CultureInfo.InvariantCulture) + " installed render(s) updated; "
            + Unchanged.ToString(CultureInfo.InvariantCulture) + " already used this setting."
            + (Failed.Count == 0 ? "" : " " + Failed.Count.ToString(CultureInfo.InvariantCulture) + " problem(s) need attention."); } }
    }

    /// <summary>Changes screen metadata of explicitly selected installed movies, without rendering or changing game setup.</summary>
    public static class InstalledRenderScreenPlan
    {
        private static readonly StringComparer Paths = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private sealed class Prepared
        {
            internal string Source, OriginalHash, Staged, NewHash, Policy, PolicyHash, StagedPolicy, NewPolicyHash;
            internal int Id;
        }
        private sealed class Mutation
        {
            internal string Path, Backup, OriginalHash, NewHash;
        }

        /// <remarks>The caller must enforce Studio's native unlock check before calling this method.</remarks>
        public static InstalledRenderScreenReport Apply(string gameRoot, IEnumerable<InstalledRenderScreenChoice> chosen,
            InstalledRenderScreenMode mode, CancellationToken cancellation)
        { return Apply(gameRoot, chosen, mode, cancellation, () => Process.GetProcessesByName("WWE2K26_x64").Length == 0, null); }

        internal static InstalledRenderScreenReport Apply(string gameRoot, IEnumerable<InstalledRenderScreenChoice> chosen,
            InstalledRenderScreenMode mode, CancellationToken cancellation, Func<bool> gameClosed, Action<int, string> beforeReplace)
        {
            var report = new InstalledRenderScreenReport();
            var owned = new List<string>(); var directories = new List<string>(); var mutations = new List<Mutation>();
            var prepared = new List<Prepared>();
            try
            {
                cancellation.ThrowIfCancellationRequested(); RequireClosed(gameClosed);
                string screens = mode == InstalledRenderScreenMode.RosterSelection ? "select"
                    : mode == InstalledRenderScreenMode.MatchCard ? "matchcard" : mode == InstalledRenderScreenMode.Both ? "both" : null;
                if (screens == null) throw new InvalidDataException("Choose roster selection, match cards or both screens.");
                if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot)) throw new IOException("Choose the installed WWE game folder first.");
                string game = Path.GetFullPath(gameRoot), library = Path.Combine(game, "Plugins", "GorillaRenders");
                RequireSafe(game); RequireSafe(library);
                if (!Directory.Exists(library)) throw new IOException("The installed GorillaRenders folder does not exist.");
                var selected = (chosen ?? Enumerable.Empty<InstalledRenderScreenChoice>()).ToList();
                if (selected.Count == 0 || selected.Count > 4096 || selected.Any(item => item == null)) throw new InvalidDataException("Select between one and 4096 installed renders.");
                if (selected.Any(item => item.WrestlerId < 1 || item.WrestlerId > 65535 || string.IsNullOrWhiteSpace(item.Path))) throw new InvalidDataException("A selected installed render has no valid wrestler ID or path.");
                if (selected.GroupBy(item => item.WrestlerId).Any(group => group.Count() != 1)
                    || selected.GroupBy(item => Path.GetFullPath(item.Path), Paths).Any(group => group.Count() != 1))
                    throw new InvalidDataException("Select only one installed render for each wrestler ID.");
                foreach (var selectedItem in selected)
                {
                    string path = Path.GetFullPath(selectedItem.Path);
                    if (!Paths.Equals(Path.GetDirectoryName(path), library) || !string.Equals(Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Select files directly inside the installed Plugins/GorillaRenders folder.");
                    RequireSafe(path);
                }
                var files = Directory.EnumerateFiles(library, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase)).Take(8193).ToList();
                if (files.Count > 8192) throw new InvalidDataException("The installed render library exceeds the bounded 8192-file inspection limit.");
                var claims = new Dictionary<int, List<string>>(); var fileClaims = new Dictionary<string, int[]>(Paths);
                foreach (string path in files)
                {
                    cancellation.ThrowIfCancellationRequested(); RequireSafe(path);
                    int[] ids = NativeClaims(path).Distinct().ToArray(); fileClaims[path] = ids;
                    foreach (int id in ids)
                    {
                        List<string> owners;
                        if (!claims.TryGetValue(id, out owners)) claims[id] = owners = new List<string>();
                        owners.Add(path);
                    }
                }
                foreach (var selectedItem in selected)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string source = Path.GetFullPath(selectedItem.Path); int id = selectedItem.WrestlerId;
                    List<string> owners;
                    if (!claims.TryGetValue(id, out owners) || owners.Count != 1 || !Paths.Equals(source, owners[0]))
                        throw new InvalidDataException("Wrestler ID " + id.ToString(CultureInfo.InvariantCulture) + " has conflicting or missing installed movie claims. Resolve duplicate installed renders before changing its screens.");
                    if (fileClaims[source].Any(claim => claims[claim].Count != 1))
                        throw new InvalidDataException("A selected movie shares an extra wrestler ID with another installed movie. Resolve duplicate installed render claims before changing its screens.");
                    long videoLength; Dictionary<string, object> metadata;
                    if (!GorillaRenderMetadata.InspectCompleted(source, id, out videoLength, out metadata)) throw new InvalidDataException("The installed movie is incomplete, changed or unsupported: " + Path.GetFileName(source));
                    object current;
                    string existing = metadata.TryGetValue("screens", out current) ? current as string : "both";
                    if (existing == screens) { report.Unchanged++; continue; }
                    var item = new Prepared { Source = source, Id = id, OriginalHash = Hash(source, cancellation), Policy = SelectionLoopPolicy.SidecarPath(source) };
                    if (File.Exists(item.Policy)) { RequireSafe(item.Policy); item.PolicyHash = Hash(item.Policy, cancellation); }
                    prepared.Add(item);
                }
                if (prepared.Count == 0) return report;
                string stageRoot = Path.Combine(Path.GetTempPath(), "gorilla-installed-screens-" + Guid.NewGuid().ToString("N"));
                RequireSafe(stageRoot); if (Directory.Exists(stageRoot) || File.Exists(stageRoot)) throw new IOException("The private staging location already exists.");
                Directory.CreateDirectory(stageRoot); directories.Add(stageRoot);
                int number = 0;
                foreach (Prepared item in prepared)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string stageDirectory = Path.Combine(stageRoot, (++number).ToString("D4", CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(stageDirectory); directories.Add(stageDirectory);
                    item.Staged = Path.Combine(stageDirectory, Path.GetFileName(item.Source));
                    Copy(item.Source, item.Staged, owned, cancellation);
                    if (Hash(item.Staged, cancellation) != item.OriginalHash) throw new IOException("An installed render changed during preparation. Refresh the list and retry.");
                    long originalVideo, changedVideo; Dictionary<string, object> originalMetadata, changedMetadata;
                    if (!GorillaRenderMetadata.InspectCompleted(item.Source, item.Id, out originalVideo, out originalMetadata)
                        || !GorillaRenderMetadata.SetScreens(item.Staged, item.Id, screens)
                        || !GorillaRenderMetadata.InspectCompleted(item.Staged, item.Id, out changedVideo, out changedMetadata)
                        || originalVideo != changedVideo || !GorillaRenderMetadata.SameExceptScreens(originalMetadata, changedMetadata)
                        || !SameVideo(item.Source, item.Staged, originalVideo, cancellation))
                        throw new InvalidDataException("The prepared movie could not retain its original video and metadata: " + Path.GetFileName(item.Source));
                    item.NewHash = Hash(item.Staged, cancellation);
                    if (item.PolicyHash != null)
                    {
                        SelectionLoopPolicyRecord policy;
                        if (!SelectionLoopPolicy.Validate(item.Source, item.Policy, item.Id, out policy))
                            report.Warnings.Add("An existing invalid selection-loop sidecar was preserved unchanged: " + Path.GetFileName(item.Policy));
                        else
                        {
                            item.StagedPolicy = SelectionLoopPolicy.SidecarPath(item.Staged);
                            if (!SelectionLoopPolicy.TryWrite(item.Staged, item.Id, new Dictionary<string, object> { { "id", item.Id }, { "frames", policy.MotionFrames }, { "end", "freeze" } }))
                                throw new InvalidDataException("The verified selection-loop policy could not be rebound to the unchanged movie.");
                            owned.Add(item.StagedPolicy);
                            SelectionLoopPolicyRecord rebound;
                            if (!SelectionLoopPolicy.Validate(item.Staged, item.StagedPolicy, item.Id, out rebound)
                                || rebound.MotionFrames != policy.MotionFrames || rebound.VideoFrames != policy.VideoFrames || rebound.Fps != policy.Fps || rebound.Ending != policy.Ending)
                                throw new InvalidDataException("The prepared selection-loop policy changed its authorized frame range.");
                            item.NewPolicyHash = Hash(item.StagedPolicy, cancellation);
                        }
                    }
                }
                cancellation.ThrowIfCancellationRequested(); RequireClosed(gameClosed);
                foreach (Prepared item in prepared) VerifyUnchanged(item, cancellation);
                string backupParent = Path.Combine(game, "Plugins", "GorillaRenderBackups"); RequireSafe(backupParent);
                report.BackupDirectory = Path.Combine(backupParent, "screens-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
                RequireSafe(report.BackupDirectory); Directory.CreateDirectory(report.BackupDirectory);
                foreach (Prepared item in prepared)
                {
                    cancellation.ThrowIfCancellationRequested(); RequireClosed(gameClosed); VerifyUnchanged(item, cancellation);
                    Backup(item.Source, report.BackupDirectory, item.OriginalHash, cancellation);
                    if (item.PolicyHash != null) Backup(item.Policy, report.BackupDirectory, item.PolicyHash, cancellation);
                }
                // Publish from same-volume private temporary files so File.Replace is atomic on Windows.
                int replaceIndex = 0;
                foreach (Prepared item in prepared)
                {
                    Replace(item.Staged, item.Source, item.OriginalHash, item.NewHash, report.BackupDirectory, owned, mutations, cancellation, gameClosed, beforeReplace, () => VerifyLibrary(library, fileClaims, cancellation), ref replaceIndex);
                    if (item.StagedPolicy != null)
                        Replace(item.StagedPolicy, item.Policy, item.PolicyHash, item.NewPolicyHash, report.BackupDirectory, owned, mutations, cancellation, gameClosed, beforeReplace, () => VerifyLibrary(library, fileClaims, cancellation), ref replaceIndex);
                }
                report.Changed = prepared.Count;
            }
            catch (Exception error) when (Expected(error) || error is OperationCanceledException)
            {
                report.Failed.Add(error is OperationCanceledException ? "Changing installed screens was canceled." : error.Message);
                for (int index = mutations.Count - 1; index >= 0; index--)
                {
                    Mutation mutation = mutations[index];
                    try
                    {
                        RequireClosed(gameClosed); RequireSafe(mutation.Path); RequireSafe(mutation.Backup);
                        if (!File.Exists(mutation.Path) || Hash(mutation.Path, CancellationToken.None) != mutation.NewHash
                            || Hash(mutation.Backup, CancellationToken.None) != mutation.OriginalHash)
                            throw new IOException("A changed installed file could not safely be restored because it changed again.");
                        string restore = Path.Combine(Path.GetDirectoryName(mutation.Path), "." + Path.GetFileName(mutation.Path) + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
                        Copy(mutation.Backup, restore, owned, CancellationToken.None);
                        RequireClosed(gameClosed);
                        RequireSafe(mutation.Path);
                        if (Hash(restore, CancellationToken.None) != mutation.OriginalHash) throw new IOException("The original restore copy could not be verified.");
                        if (Hash(mutation.Path, CancellationToken.None) != mutation.NewHash) throw new IOException("An installed file changed before rollback.");
                        File.Replace(restore, mutation.Path, null);
                    }
                    catch (Exception rollbackError) when (Expected(rollbackError))
                    { report.RollbackComplete = false; report.Failed.Add("Restore needed for " + Path.GetFileName(mutation.Path) + ": " + rollbackError.Message + " Backup: " + mutation.Backup); }
                }
                report.Changed = prepared.Count(item => mutations.Any(change => Paths.Equals(change.Path, item.Source))
                    && File.Exists(item.Source) && SafeHash(item.Source) == item.NewHash);
                if (mutations.Count != 0 && report.RollbackComplete) report.Warnings.Add("The interrupted change was rolled back; the original installed movies and policies were restored.");
            }
            finally
            {
                foreach (string file in owned.AsEnumerable().Reverse())
                    try { RequireSafe(file); if (File.Exists(file)) File.Delete(file); }
                    catch (Exception error) when (Expected(error)) { report.Warnings.Add("A private temporary file could not be removed: " + file); }
                foreach (string directory in directories.AsEnumerable().Reverse())
                    try { RequireSafe(directory); if (Directory.Exists(directory)) Directory.Delete(directory, false); }
                    catch (Exception error) when (Expected(error)) { report.Warnings.Add("A private staging folder could not be removed: " + directory); }
            }
            return report;
        }

        private static void Replace(string staged, string target, string oldHash, string newHash, string backupDirectory,
            List<string> owned, List<Mutation> mutations, CancellationToken cancellation, Func<bool> gameClosed, Action<int, string> beforeReplace, Action verifyLibrary, ref int replaceIndex)
        {
            cancellation.ThrowIfCancellationRequested(); RequireClosed(gameClosed); RequireSafe(target);
            string temporary = Path.Combine(Path.GetDirectoryName(target), "." + Path.GetFileName(target) + ".screens-" + Guid.NewGuid().ToString("N") + ".tmp");
            Copy(staged, temporary, owned, cancellation);
            if (Hash(temporary, cancellation) != newHash) throw new IOException("A prepared installed movie changed before publication.");
            beforeReplace?.Invoke(++replaceIndex, target);
            cancellation.ThrowIfCancellationRequested(); RequireClosed(gameClosed); RequireSafe(target);
            verifyLibrary();
            if (!File.Exists(target) || Hash(target, cancellation) != oldHash) throw new IOException("An installed file changed before publication; it was preserved.");
            string backup = Path.Combine(backupDirectory, Path.GetFileName(target));
            RequireSafe(backup);
            if (!File.Exists(backup) || Hash(backup, cancellation) != oldHash) throw new IOException("A required original backup is missing or changed; nothing further was replaced.");
            File.Replace(temporary, target, null);
            mutations.Add(new Mutation { Path = target, Backup = backup, OriginalHash = oldHash, NewHash = newHash });
        }

        private static void VerifyUnchanged(Prepared item, CancellationToken cancellation)
        {
            RequireSafe(item.Source); RequireSafe(item.Policy);
            if (Hash(item.Source, cancellation) != item.OriginalHash || (File.Exists(item.Policy) ? Hash(item.Policy, cancellation) : null) != item.PolicyHash)
                throw new IOException("An installed movie or its policy changed during preparation. Refresh the list and retry.");
        }

        private static void VerifyLibrary(string library, Dictionary<string, int[]> expected, CancellationToken cancellation)
        {
            RequireSafe(library);
            var current = Directory.EnumerateFiles(library, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".gmr", StringComparison.OrdinalIgnoreCase)).Take(8193).ToList();
            if (current.Count != expected.Count || current.Any(path => !expected.ContainsKey(path))) throw new IOException("The installed render list changed during preparation. Refresh the list and retry.");
            foreach (string path in current)
            {
                cancellation.ThrowIfCancellationRequested(); RequireSafe(path);
                if (!NativeClaims(path).Distinct().SequenceEqual(expected[path])) throw new IOException("Installed wrestler ID claims changed during preparation. Refresh the list and retry.");
            }
        }

        private static void Backup(string source, string directory, string expected, CancellationToken cancellation)
        {
            string path = Path.Combine(directory, Path.GetFileName(source)); RequireSafe(path);
            Copy(source, path, null, cancellation);
            if (Hash(path, cancellation) != expected) throw new IOException("The original installed backup could not be verified.");
        }

        private static IEnumerable<int> NativeClaims(string path)
        {
            Dictionary<string, object> metadata;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                if (stream.Length < 64) throw new InvalidDataException("An installed GMR cannot be inspected for duplicate slots: " + Path.GetFileName(path));
                stream.Position = stream.Length - 8; uint length = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x31524d47 || length == 0 || length > 65536 || length > stream.Length - 8)
                    throw new InvalidDataException("An installed GMR has an unreadable footer: " + Path.GetFileName(path));
                stream.Position = stream.Length - 8 - length; byte[] bytes = reader.ReadBytes((int)length);
                if (bytes.Length != length) throw new IOException("An installed GMR changed while its footer was read.");
                metadata = Parse(Utf8.GetString(bytes));
            }
            object value; int id;
            if (metadata == null || !metadata.TryGetValue("format", out value) || (value as string) != "GorillaRender"
                || !metadata.TryGetValue("wrestler_id", out value) || !Integer(value, out id))
                throw new InvalidDataException("An installed GMR has no valid logical wrestler ID: " + Path.GetFileName(path));
            var claims = new List<int> { id };
            if (metadata.TryGetValue("extra_ids", out value))
            {
                var extra = value as IList;
                if (extra == null || extra.Count > 64) throw new InvalidDataException("An installed GMR has malformed extra wrestler IDs: " + Path.GetFileName(path));
                int position = 0;
                foreach (object owner in extra)
                {
                    if (!Integer(owner, out id)) throw new InvalidDataException("An installed GMR has an invalid extra wrestler ID: " + Path.GetFileName(path));
                    if (position++ < 3) claims.Add(id);
                }
            }
            return claims;
        }

        private static bool Integer(object value, out int id)
        {
            id = 0;
            if (value == null || value is string || value is bool) return false;
            try { decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture); if (number != decimal.Truncate(number) || number < 1 || number > 65535) return false; id = (int)number; return true; }
            catch (Exception error) when (error is InvalidCastException || error is FormatException || error is OverflowException) { return false; }
        }

        private static Dictionary<string, object> Parse(string json)
        {
#if NET8_0_OR_GREATER
            using (var document = JsonDocument.Parse(json)) return ConvertJson(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 128 }.Deserialize<Dictionary<string, object>>(json);
#endif
        }
#if NET8_0_OR_GREATER
        private static object ConvertJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object: return element.EnumerateObject().ToDictionary(property => property.Name, property => ConvertJson(property.Value), StringComparer.Ordinal);
                case JsonValueKind.Array: return element.EnumerateArray().Select(ConvertJson).ToArray();
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number: decimal number; return element.TryGetDecimal(out number) ? (object)number : element.GetDouble();
                case JsonValueKind.True: return true; case JsonValueKind.False: return false; default: return null;
            }
        }
#endif
        private static void RequireClosed(Func<bool> gameClosed)
        { if (gameClosed == null || !gameClosed()) throw new IOException("Close WWE 2K26 before changing installed renders."); }
        private static void RequireSafe(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked files and folders are not changed: " + current);
                string parent = Path.GetDirectoryName(current); if (parent == current) break; current = parent;
            }
        }
        private static string Hash(string path, CancellationToken cancellation)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
            {
                byte[] buffer = new byte[81920]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { cancellation.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        private static string SafeHash(string path) { try { return Hash(path, CancellationToken.None); } catch (Exception error) when (Expected(error)) { return null; } }
        private static void Copy(string source, string target, List<string> owned, CancellationToken cancellation)
        {
            RequireSafe(source); RequireSafe(target);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned?.Add(target);
                byte[] buffer = new byte[81920]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
        }
        private static bool SameVideo(string a, string b, long length, CancellationToken cancellation)
        {
            using (var left = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var right = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] aa = new byte[81920], bb = new byte[81920];
                while (length > 0)
                {
                    cancellation.ThrowIfCancellationRequested(); int wanted = (int)Math.Min(length, aa.Length), ar = Read(left, aa, wanted), br = Read(right, bb, wanted);
                    if (ar != wanted || br != wanted) return false;
                    for (int index = 0; index < wanted; index++) if (aa[index] != bb[index]) return false;
                    length -= wanted;
                }
            }
            return true;
        }
        private static int Read(Stream stream, byte[] buffer, int count) { int total = 0, read; while (total < count && (read = stream.Read(buffer, total, count - total)) > 0) total += read; return total; }
        private static bool Expected(Exception error)
        { return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException
            || error is NotSupportedException || error is FormatException || error is OverflowException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
            || error is JsonException
#endif
            ; }
    }
}
