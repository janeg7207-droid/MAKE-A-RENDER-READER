using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class AppUpdatePlan
    {
        public int PlanVersion { get; set; } = 1;
        public string AppRoot { get; set; }
        public string CanonicalAppRoot { get; set; }
        public string PackagePath { get; set; }
        public string TargetVersion { get; set; }
        public string PackageSha256 { get; set; }
        public long PackageBytes { get; set; }
        public string DownloadUrl { get; set; }
        public int MinimumUpdaterVersion { get; set; } = 1;
        public int ParentProcessId { get; set; }
        public long ParentStartTimeUtcTicks { get; set; }
        public string ParentStartToken { get; set; }
        public Dictionary<string, string> OriginalFileHashes { get; set; }
    }

    public sealed class AppUpdateApplyReport
    {
        public bool Success { get; internal set; }
        public bool RolledBack { get; internal set; }
        public bool RestartSafe { get; internal set; }
        public string BackupDirectory { get; internal set; }
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>Replaces only a validated package's allowlisted application files and keeps exact backups.</summary>
    public static class AppUpdateTransaction
    {
        private sealed class Target
        {
            public string Name, Path, Stage, Hash, OriginalHash, Backup, RecoveryCapture;
            public bool Existed, Applied, Unresolved;
        }

        public static AppUpdatePlan CreatePlan(string appRoot, string packagePath, AppUpdateManifest manifest, int parentPid, long parentStartTicks)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            var plan = new AppUpdatePlan { AppRoot = Full(appRoot), CanonicalAppRoot = CanonicalDirectory(appRoot), PackagePath = Full(packagePath),
                TargetVersion = manifest.VersionText, DownloadUrl = manifest.DownloadUri.AbsoluteUri, PackageSha256 = manifest.Sha256,
                PackageBytes = manifest.Size, MinimumUpdaterVersion = manifest.MinimumUpdaterVersion, ParentProcessId = parentPid, ParentStartTimeUtcTicks = parentStartTicks,
                ParentStartToken = ReadParentStartToken(parentPid) };
            plan.OriginalFileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in AppUpdatePackage.ApprovedFiles)
            {
                string target = Child(plan.CanonicalAppRoot, name); CheckChildren(plan.CanonicalAppRoot, target);
                plan.OriginalFileHashes[name] = File.Exists(target) ? Hash(target) : null;
            }
            return plan;
        }

        public static AppUpdatePlan CreatePlan(string appRoot, string packagePath, string version, string sha256, long size, int parentPid, long parentStartTicks, string downloadUrl)
        {
            return CreatePlan(appRoot, packagePath, AppUpdatePackage.CreateManifest(version, downloadUrl, size, sha256), parentPid, parentStartTicks);
        }

        public static void WritePlan(AppUpdatePlan plan, string path)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            WriteJson(path, plan);
        }

        public static AppUpdatePlan ReadPlan(string path)
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The update plan is too large.");
            string json = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
#if NET8_0_OR_GREATER
            return JsonSerializer.Deserialize<AppUpdatePlan>(json) ?? throw new InvalidDataException("The update plan is empty.");
#else
            return new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<AppUpdatePlan>(json) ?? throw new InvalidDataException("The update plan is empty.");
#endif
        }

        public static void Preflight(AppUpdatePlan plan, string workRoot)
        {
            AppUpdatePayload payload = Validate(plan, workRoot);
            foreach (AppUpdatePayloadFile file in payload.Files)
            {
                string target = Child(plan.CanonicalAppRoot, file.Name);
                CheckChildren(plan.CanonicalAppRoot, target);
                if (Directory.Exists(target)) throw new IOException("An application file is occupied by a directory: " + file.Name);
                if (File.Exists(target)) using (var input = File.OpenRead(target)) { if (input.Length > 512L * 1024 * 1024) throw new IOException("An application target is unexpectedly large: " + file.Name); }
                string parent = Path.GetDirectoryName(target);
                while (!Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
                ProbeWrite(parent);
            }
        }

        public static AppUpdateApplyReport Apply(AppUpdatePlan plan, string workRoot)
        {
            return Apply(plan, workRoot, null);
        }

        // The callback is an operation boundary used by portable failure/recovery tests.
        internal static AppUpdateApplyReport Apply(AppUpdatePlan plan, string workRoot, Action<int, string> beforeTarget)
        {
            return Apply(plan, workRoot, beforeTarget, null);
        }

        internal static AppUpdateApplyReport Apply(AppUpdatePlan plan, string workRoot, Action<int, string> beforeTarget, Action<string> beforeJournal)
        {
            return Apply(plan, workRoot, beforeTarget, beforeJournal, null);
        }

        internal static AppUpdateApplyReport Apply(AppUpdatePlan plan, string workRoot, Action<int, string> beforeTarget, Action<string> beforeJournal, Action<string> beforeNewRollbackCapture)
        {
            var report = new AppUpdateApplyReport();
            var targets = new List<Target>();
            string transaction = null;
            try
            {
                CheckRoot(plan);
                if (!OldTargetsUnchanged(plan)) throw new IOException("Application files changed after the update handoff. They were preserved.");
                Preflight(plan, workRoot);
                AppUpdatePayload payload = Validate(plan, workRoot);
                transaction = Path.Combine(CanonicalDirectory(workRoot), "transaction-" + Guid.NewGuid().ToString("N"));
                report.BackupDirectory = Path.Combine(transaction, "backup");
                Directory.CreateDirectory(report.BackupDirectory);
                using (var zip = ZipFile.OpenRead(plan.PackagePath))
                    foreach (AppUpdatePayloadFile file in payload.Files)
                    {
                        string staged = Child(Path.Combine(transaction, "payload"), file.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(staged));
                        ZipArchiveEntry entry = zip.GetEntry(file.Name);
                        if (entry == null) throw new InvalidDataException("The validated update entry disappeared: " + file.Name);
                        using (Stream input = entry.Open()) using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[81920]; long length = 0; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            { length += count; if (length > file.Length) throw new InvalidDataException("An update entry exceeded its validated size."); output.Write(buffer, 0, count); }
                            output.Flush(true);
                            if (length != file.Length) throw new InvalidDataException("An update entry changed size.");
                        }
                        if (!EqualHash(Hash(staged), file.Sha256)) throw new InvalidDataException("An update entry changed after validation: " + file.Name);
                        targets.Add(new Target { Name = file.Name, Path = Child(plan.CanonicalAppRoot, file.Name), Stage = staged, Hash = file.Sha256,
                            Backup = Child(report.BackupDirectory, file.Name + ".before-update") });
                    }
                Validate(plan, workRoot); // Re-hash the downloaded ZIP after extraction.
                foreach (Target target in targets)
                {
                    CheckRoot(plan);
                    CheckChildren(plan.CanonicalAppRoot, target.Path);
                    target.OriginalHash = plan.OriginalFileHashes[target.Name];
                    target.Existed = target.OriginalHash != null;
                    if (!Matches(target.Path, target.Existed, target.OriginalHash)) { target.Unresolved = true; throw new IOException("An application file changed before its backup: " + target.Name); }
                    if (!target.Existed) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target.Backup));
                    File.Copy(target.Path, target.Backup, false);
                    if (!EqualHash(Hash(target.Backup), target.OriginalHash) || !EqualHash(Hash(target.Path), target.OriginalHash))
                    { target.Unresolved = true; throw new IOException("An application file changed while its backup was saved: " + target.Name); }
                }
                beforeJournal?.Invoke("backups-ready"); Journal(transaction, "backups-ready", targets, report);
                for (int index = 0; index < targets.Count; index++)
                {
                    Target target = targets[index];
                    beforeTarget?.Invoke(index, target.Name);
                    CheckRoot(plan); CheckChildren(plan.CanonicalAppRoot, target.Path);
                    if (!Matches(target.Path, target.Existed, target.OriginalHash)) { target.Unresolved = true; throw new IOException("An application file changed after the update snapshot and was preserved: " + target.Name); }
                    beforeJournal?.Invoke("applying " + target.Name); Journal(transaction, "applying " + target.Name, targets, report);
                    try { ReplaceTarget(plan.CanonicalAppRoot, target); }
                    catch
                    {
                        try
                        {
                            if (Matches(target.Path, true, target.Hash)) target.Applied = true;
                            else if (!Matches(target.Path, target.Existed, target.OriginalHash)) target.Unresolved = true;
                        }
                        catch { target.Unresolved = true; }
                        throw;
                    }
                    target.Applied = true;
                    beforeJournal?.Invoke("applied " + target.Name); Journal(transaction, "applied " + target.Name, targets, report);
                }
                foreach (Target target in targets)
                    if (!Matches(target.Path, true, target.Hash)) { target.Unresolved = true; throw new IOException("A completed application target changed externally: " + target.Name); }
                beforeJournal?.Invoke("complete"); Journal(transaction, "complete", targets, report);
                report.Success = true;
                report.RestartSafe = true;
            }
            catch (Exception error)
            {
                report.Success = false;
                report.Errors.Add(error.Message);
                bool complete = !targets.Any(target => target.Unresolved);
                foreach (Target target in targets.Where(target => target.Applied).Reverse())
                {
                    try
                    {
                        CheckRoot(plan); CheckChildren(plan.CanonicalAppRoot, target.Path);
                        if (!Matches(target.Path, true, target.Hash)) throw new IOException("A newer external change was preserved; restore this file manually from its update backup: " + target.Name);
                        if (target.Existed)
                        {
                            if (!EqualHash(Hash(target.Backup), target.OriginalHash)) throw new IOException("The original update backup changed: " + target.Name);
                            var restore = new Target { Name = target.Name, Path = target.Path, Stage = target.Backup, Hash = target.OriginalHash, Existed = true, OriginalHash = target.Hash };
                            ReplaceTarget(plan.CanonicalAppRoot, restore);
                        }
                        else
                        {
                            beforeNewRollbackCapture?.Invoke(target.Name);
                            RollbackNewTarget(target);
                        }
                        target.Applied = false;
                    }
                    catch (Exception rollbackError) { complete = false; report.Errors.Add(rollbackError.Message); }
                }
                report.RolledBack = complete;
                try
                {
                    CheckRoot(plan);
                    report.RestartSafe = complete && OldTargetsUnchanged(plan);
                }
                catch { report.RestartSafe = false; }
                if (transaction != null) try { Journal(transaction, complete ? "rolled-back" : "rollback-incomplete", targets, report); } catch (Exception journalError) { report.Errors.Add("Recovery journal: " + journalError.Message); }
            }
            return report;
        }

        private static void RollbackNewTarget(Target target)
        {
            string captured = Path.Combine(Path.GetDirectoryName(target.Path), ".gorilla-update-" + Guid.NewGuid().ToString("N") + ".rollback");
            // Capture whichever file occupies the normal writer-visible slot in one
            // rename. The private recovery name is never a normal application target.
            File.Move(target.Path, captured);
            target.RecoveryCapture = captured;
            if (!EqualHash(Hash(captured), target.Hash))
            {
                target.Unresolved = true;
                if (MoveNew(captured, target.Path)) target.RecoveryCapture = null;
                throw new IOException("A newer file was preserved during rollback: " + target.Name
                    + (target.RecoveryCapture == null ? "." : ". Recovery copy: " + target.RecoveryCapture));
            }
            File.Delete(captured); target.RecoveryCapture = null;
            if (File.Exists(target.Path) || Directory.Exists(target.Path))
            {
                target.Unresolved = true;
                throw new IOException("A newer application target appeared during rollback and was preserved: " + target.Name);
            }
        }

        private static bool OldTargetsUnchanged(AppUpdatePlan plan)
        {
            CheckRoot(plan);
            if (plan.OriginalFileHashes == null || plan.OriginalFileHashes.Count != AppUpdatePackage.ApprovedFiles.Count
                || AppUpdatePackage.ApprovedFiles.Any(name => !plan.OriginalFileHashes.ContainsKey(name))) return false;
            foreach (string name in AppUpdatePackage.ApprovedFiles)
            {
                string path = Child(plan.CanonicalAppRoot, name); CheckChildren(plan.CanonicalAppRoot, path);
                string expected = plan.OriginalFileHashes[name];
                if (!Matches(path, expected != null, expected)) return false;
            }
            return File.Exists(Path.Combine(plan.CanonicalAppRoot, "GorillaStudio.exe"));
        }

        internal static string ReadParentStartToken(int pid)
        {
            if (Path.DirectorySeparatorChar == '\\') return null;
            string path = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
            if (!File.Exists(path)) return null;
            string stat = File.ReadAllText(path);
            int end = stat.LastIndexOf(')');
            string[] fields = stat.Substring(end + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return end > 0 && fields.Length > 19 ? "linux:" + fields[19] : null;
        }

        private static AppUpdatePayload Validate(AppUpdatePlan plan, string workRoot)
        {
            if (plan == null || plan.PlanVersion != 1 || plan.ParentProcessId < 1 || plan.ParentStartTimeUtcTicks < 1) throw new InvalidDataException("The update plan is invalid.");
            CheckRoot(plan);
            if (!OldTargetsUnchanged(plan)) throw new IOException("Application files changed after the update handoff. Open the app and request the update again.");
            string work = CanonicalDirectory(workRoot);
            if (Inside(plan.CanonicalAppRoot, work) || Inside(work, plan.CanonicalAppRoot)) throw new IOException("The update workspace must be separate from the application folder.");
            string packagePath = Full(plan.PackagePath);
            string package = Path.Combine(CanonicalDirectory(Path.GetDirectoryName(packagePath)), Path.GetFileName(packagePath));
            if (!Inside(work, package)) throw new IOException("The update ZIP must remain inside its staging workspace.");
            CheckChildren(work, package);
            if (!File.Exists(Path.Combine(plan.CanonicalAppRoot, "GorillaStudio.exe"))) throw new IOException("Choose the existing Gorilla Studio application folder.");
            var manifest = AppUpdatePackage.CreateManifest(plan.TargetVersion, plan.DownloadUrl, plan.PackageBytes, plan.PackageSha256, plan.MinimumUpdaterVersion);
            return AppUpdatePackage.Validate(package, manifest);
        }

        private static void CheckRoot(AppUpdatePlan plan)
        {
            if (plan == null || !Directory.Exists(plan.AppRoot) || string.IsNullOrWhiteSpace(plan.CanonicalAppRoot)
                || !SamePath(CanonicalDirectory(plan.AppRoot), plan.CanonicalAppRoot)) throw new IOException("The application folder moved or its selected link changed. Open the app and try the update again.");
        }

        private static void ReplaceTarget(string root, Target target)
        {
            CheckChildren(root, target.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target.Path));
            CheckChildren(root, target.Path);
            string temporary = Path.Combine(Path.GetDirectoryName(target.Path), ".gorilla-update-" + Guid.NewGuid().ToString("N") + ".new");
            string captured = temporary + ".previous";
            try
            {
                File.Copy(target.Stage, temporary, false);
                if (!EqualHash(Hash(temporary), target.Hash) || !Matches(target.Path, target.Existed, target.OriginalHash))
                    throw new IOException("An update target changed immediately before replacement: " + target.Name);
                CheckChildren(root, target.Path);
                if (target.Existed)
                {
                    File.Replace(temporary, target.Path, captured);
                    temporary = null;
                    if (!EqualHash(Hash(captured), target.OriginalHash))
                    {
                        target.Unresolved = true;
                        target.RecoveryCapture = captured;
                        if (Matches(target.Path, true, target.Hash))
                        {
                            File.Replace(captured, target.Path, null);
                            target.Applied = false;
                            target.RecoveryCapture = null;
                        }
                        throw new IOException("A concurrent change was preserved while updating: " + target.Name
                            + (target.RecoveryCapture == null ? "." : ". Recovery copy: " + target.RecoveryCapture));
                    }
                }
                else
                {
                    if (!MoveNew(temporary, target.Path)) throw new IOException("A new application target appeared during the update and was preserved: " + target.Name);
                    temporary = null;
                }
                if (!Matches(target.Path, true, target.Hash)) throw new IOException("The updated application file did not retain its expected contents: " + target.Name);
            }
            finally
            {
                if (temporary != null) TryDelete(temporary);
                // The full original file also remains in the transaction backup.
                if (File.Exists(captured) && EqualHash(Hash(captured), target.OriginalHash)) TryDelete(captured);
            }
        }

        private static bool MoveNew(string source, string target)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                if (MoveNewWindows(source, target)) return true;
                int error = Marshal.GetLastWin32Error(); if (error == 80 || error == 183) return false;
                throw new IOException("The new application file could not be saved.", new Win32Exception(error));
            }
            if (LinkNewUnix(source, target) == 0) { File.Delete(source); return true; }
            int unixError = Marshal.GetLastWin32Error(); if (unixError == 17) return false;
            throw new IOException("The new application file could not be saved.", new Win32Exception(unixError));
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveNewWindows(string source, string target);
        [DllImport("libc", EntryPoint = "link", CharSet = CharSet.Ansi, SetLastError = true)] private static extern int LinkNewUnix(string source, string target);

        private static void CheckChildren(string root, string target)
        {
            root = Full(root); target = Full(target);
            if (!Inside(root, target) || SamePath(root, target)) throw new IOException("An update target leaves its selected folder.");
            string relative = target.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = root;
            foreach (string part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An update target contains a linked child path: " + current);
            }
        }

        private static string CanonicalDirectory(string path)
        {
            string full = Full(path);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The selected update folder does not exist: " + full);
            if (Path.DirectorySeparatorChar == '\\') return DirectoryLinks.Resolve(full);
#if NET8_0_OR_GREATER
            return new DirectoryInfo(full).ResolveLinkTarget(true)?.FullName ?? full;
#else
            return full;
#endif
        }
        private static string Full(string path) { if (string.IsNullOrWhiteSpace(path)) throw new IOException("An update path is missing."); return Path.GetFullPath(path); }
        private static bool SamePath(string a, string b) { return string.Equals(Full(a).TrimEnd(Path.DirectorySeparatorChar), Full(b).TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        private static bool Inside(string root, string path) { return SamePath(root, path) || Full(path).StartsWith(Full(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        private static string Child(string root, string relative) { string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); if (!Inside(root, path)) throw new IOException("An update entry leaves its folder."); return path; }
        private static bool Matches(string path, bool exists, string hash) { return exists ? File.Exists(path) && EqualHash(Hash(path), hash) : !File.Exists(path) && !Directory.Exists(path); }
        private static bool EqualHash(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static string Hash(string path) { using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(input).Select(value => value.ToString("x2", CultureInfo.InvariantCulture))); }
        private static void ProbeWrite(string directory) { string path = Path.Combine(directory, ".gorilla-update-probe-" + Guid.NewGuid().ToString("N")); try { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Flush(true); } finally { TryDelete(path); } }
        private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static void Journal(string root, string state, List<Target> targets, AppUpdateApplyReport report) { WriteJson(Path.Combine(root, "recovery.json"), new { state, report.BackupDirectory, errors = report.Errors.ToArray(), files = targets.Select(target => new { target.Name, target.Path, target.Existed, target.OriginalHash, newHash = target.Hash, target.Backup, target.Applied, target.Unresolved, target.RecoveryCapture }).ToArray() }); }
        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
#if NET8_0_OR_GREATER
                string json = JsonSerializer.Serialize(value);
#else
                string json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Serialize(value);
#endif
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { TryDelete(temporary); }
        }
    }
}
