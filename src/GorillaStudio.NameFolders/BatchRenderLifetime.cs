using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace GorillaStudio.NameFolders
{
    /// <summary>Owns only the exact native processes registered by one batch session.</summary>
    public sealed class BatchRenderLifetime : IDisposable
    {
        private readonly object gate = new object();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly CancellationToken token;
        private readonly Dictionary<string, OwnedProcess> owned = new Dictionary<string, OwnedProcess>(StringComparer.Ordinal);
        private readonly int ownPid;
        private bool requested, disposed;
        private long generation, completedGeneration = -1;
        private Task cancelTask;

        private sealed class OwnedProcess
        {
            internal int Pid;
            internal long Started;
            internal ulong LinuxStarted;
            internal string Key { get { return Pid + ":" + Started + ":" + LinuxStarted; } }
        }

        private sealed class ProcessRow
        {
            internal int Pid, Parent;
            internal ulong Started;
            internal char State;
        }

        public BatchRenderLifetime()
        {
            token = cancellation.Token;
            using (Process current = Process.GetCurrentProcess()) ownPid = current.Id;
        }

        public CancellationToken Token { get { return token; } }
        public bool IsCancellationRequested { get { lock (gate) return requested; } }
        public bool HasRunningProcesses { get { return Snapshot().Any(IsAlive); } }

        public void TrackProcess(Process process)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            int pid;
            long started;
            try
            {
                pid = process.Id;
                if (pid == ownPid) throw new ArgumentException("The batch cannot own the application itself.", nameof(process));
                if (process.HasExited) return;
                started = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (InvalidOperationException) { return; } // Already completed; do not take ownership of a reused PID.
            OwnedProcess captured = Capture(pid);
            if (captured == null || captured.Started != started) return;
            Add(captured);
            // Keep descendants that already exist even if their parent exits
            // before cancellation. The caller retains ownership of its Process.
            DiscoverChildren();
            bool stop;
            lock (gate) stop = requested || disposed;
            if (stop) Observe(StopLateProcessAsync());
        }

        private async Task StopLateProcessAsync()
        {
            await CancelAsync().ConfigureAwait(false);
            // A started callback may land after a drain's final locked check
            // but just before its Task is marked complete. Join another drain
            // in that narrow handoff rather than leaving the late child alive.
            if (HasRunningProcesses) await CancelAsync().ConfigureAwait(false);
        }

        public Task CancelAsync()
        {
            lock (gate)
            {
                if (!requested)
                {
                    requested = true;
                    if (!disposed) cancellation.Cancel();
                }
                if (cancelTask != null && !cancelTask.IsCompleted) return cancelTask;
                if (cancelTask != null && !cancelTask.IsFaulted && !cancelTask.IsCanceled && completedGeneration == generation)
                    return cancelTask;
                // A fault is retryable. A late started callback creates a new
                // drain after a prior successful cancellation has completed.
                cancelTask = Task.Run((Action)CancelCore);
                return cancelTask;
            }
        }

        private void CancelCore()
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                long stoppedGeneration;
                if (IsLinux)
                {
                    // Freeze known parents before discovering/killing their
                    // children, so a normal renderer cannot fork a new orphan
                    // between our tree snapshot and parent termination.
                    foreach (OwnedProcess process in Snapshot()) Signal(process, 19);
                    for (int pass = 0; pass < 32; pass++)
                    {
                        int added = DiscoverChildren();
                        foreach (OwnedProcess process in Snapshot()) Signal(process, 19);
                        if (added == 0) break;
                        if (pass == 31) throw new InvalidOperationException("The batch process tree is still changing. Retry cancellation before closing.");
                    }
                    OwnedProcess[] stop = Snapshot(out stoppedGeneration);
                    foreach (OwnedProcess process in stop.Reverse()) Signal(process, 9);
                }
                else if (IsWindows)
                {
                    DiscoverChildren();
                    // Descendants are retained independently so taskkill can
                    // still stop them if the original parent just completed.
                    OwnedProcess[] stop = Snapshot(out stoppedGeneration);
                    foreach (OwnedProcess process in stop) KillWindowsTree(process);
                }
                else if (HasRunningProcesses)
                    throw new PlatformNotSupportedException("Batch process cancellation is supported on Windows and Linux.");
                else { lock (gate) stoppedGeneration = generation; }

                while (HasRunningProcesses)
                {
                    // A late native started callback must be terminated by a
                    // new tree pass, not merely waited on until the timeout.
                    lock (gate) if (generation != stoppedGeneration) break;
                    if (deadline.Elapsed > TimeSpan.FromSeconds(25))
                        throw new InvalidOperationException("A batch process has not exited. Retry cancellation before closing or selecting another wrestler.");
                    Thread.Sleep(40);
                }
                lock (gate)
                {
                    // A started callback can add another exact owned process
                    // while cancellation is awaiting the previous tree.
                    if (owned.Values.Any(IsAlive)) continue;
                    completedGeneration = generation;
                    return;
                }
            }
        }

        private void Add(OwnedProcess process)
        {
            lock (gate)
                if (!owned.ContainsKey(process.Key)) { owned.Add(process.Key, process); generation++; }
        }

        private OwnedProcess[] Snapshot()
        { lock (gate) return owned.Values.ToArray(); }

        private OwnedProcess[] Snapshot(out long currentGeneration)
        { lock (gate) { currentGeneration = generation; return owned.Values.ToArray(); } }

        private int DiscoverChildren()
        {
            var roots = Snapshot().Where(IsAlive).ToDictionary(value => value.Pid);
            if (roots.Count == 0) return 0;
            List<ProcessRow> rows = ProcessRows();
            int added = 0;
            bool progress;
            do
            {
                progress = false;
                foreach (ProcessRow row in rows)
                {
                    OwnedProcess parent;
                    if (row.Pid == ownPid || roots.ContainsKey(row.Pid) || !roots.TryGetValue(row.Parent, out parent)) continue;
                    if (!SameLiveProcess(parent)) continue;
                    OwnedProcess child = Capture(row.Pid);
                    if (child == null || child.Started < parent.Started || (IsLinux && child.LinuxStarted != row.Started)
                        || !SameLiveProcess(parent)) continue;
                    roots.Add(child.Pid, child); Add(child); added++; progress = true;
                }
            } while (progress);
            return added;
        }

        private static OwnedProcess Capture(int pid)
        {
            try
            {
                ProcessRow row = IsLinux ? LinuxRow(pid) : null;
                if (IsLinux && (row == null || row.State == 'Z' || row.State == 'X')) return null;
                using (Process process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return null;
                    return new OwnedProcess { Pid = pid, Started = process.StartTime.ToUniversalTime().Ticks,
                        LinuxStarted = row == null ? 0 : row.Started };
                }
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static bool IsAlive(OwnedProcess identity)
        {
            try
            {
                OwnedProcess current = Capture(identity.Pid);
                return current != null && current.Started == identity.Started && current.LinuxStarted == identity.LinuxStarted;
            }
            catch (Exception error) when (error is Win32Exception || error is UnauthorizedAccessException || error is IOException)
            { return true; } // Failure to prove exit must not unlock the app.
        }

        private static bool SameLiveProcess(OwnedProcess identity)
        {
            OwnedProcess current = Capture(identity.Pid);
            return current != null && current.Started == identity.Started && current.LinuxStarted == identity.LinuxStarted;
        }

        private static void Signal(OwnedProcess identity, int signal)
        {
            if (!SameLiveProcess(identity)) return;
            if (kill(identity.Pid, signal) != 0 && SameLiveProcess(identity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The owned batch process could not be stopped.");
        }

        private static void KillWindowsTree(OwnedProcess identity)
        {
            if (!SameLiveProcess(identity)) return;
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");
            using (var tool = new Process { StartInfo = new ProcessStartInfo {
                FileName = executable, Arguments = "/PID " + identity.Pid + " /T /F",
                UseShellExecute = false, CreateNoWindow = true
            } })
            {
                // The exact start time is checked immediately before launching
                // the OS tree operation; an unrelated reused PID is never owned.
                if (!SameLiveProcess(identity)) return;
                tool.Start();
                if (!tool.WaitForExit(10000))
                {
                    try { tool.Kill(); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("Windows did not finish stopping the batch process. Retry cancellation.");
                }
            }
        }

        private static List<ProcessRow> ProcessRows()
        {
            var rows = new List<ProcessRow>();
            if (IsLinux)
            {
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                {
                    int pid;
                    if (!int.TryParse(Path.GetFileName(directory), out pid)) continue;
                    try { ProcessRow row = LinuxRow(pid); if (row != null) rows.Add(row); }
                    catch (UnauthorizedAccessException) { } // Unrelated protected processes cannot be our normal renderer children.
                }
                return rows;
            }
            if (!IsWindows) return rows;
            using (SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the batch process tree.");
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                bool valid = Process32First(snapshot, ref entry);
                if (!valid && Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
                while (valid)
                {
                    if (entry.Id <= int.MaxValue && entry.Parent <= int.MaxValue)
                        rows.Add(new ProcessRow { Pid = (int)entry.Id, Parent = (int)entry.Parent });
                    entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                    valid = Process32Next(snapshot, ref entry);
                }
                if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error(), "The process tree inspection did not finish.");
            }
            return rows;
        }

        private static ProcessRow LinuxRow(int pid)
        {
            try
            {
                string line = File.ReadAllText("/proc/" + pid + "/stat");
                int close = line.LastIndexOf(')');
                string[] fields = line.Substring(close + 2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return new ProcessRow { Pid = pid, State = fields[0][0], Parent = int.Parse(fields[1]), Started = ulong.Parse(fields[19]) };
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static bool IsWindows { get { return Path.DirectorySeparatorChar == '\\'; } }
        private static bool IsLinux { get { return Directory.Exists("/proc/self"); } }

        private static void Observe(Task task)
        { task.ContinueWith(done => { var ignored = done.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                if (owned.Values.Any(IsAlive)) throw new InvalidOperationException("Stop and await the owned batch processes before disposing their session.");
                disposed = true;
                requested = true;
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }

        [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            internal uint Size, Usage, Id;
            internal UIntPtr Heap;
            internal uint Module, Threads, Parent;
            internal int Priority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        }
    }
}
