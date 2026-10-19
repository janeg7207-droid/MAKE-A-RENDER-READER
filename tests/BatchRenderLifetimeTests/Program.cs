using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static readonly List<OwnedProcess> cleanup = new List<OwnedProcess>();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--worker-", StringComparison.Ordinal))
            return await RunWorker(args);

        string root = Path.Combine(Path.GetTempPath(), "gorilla-batch-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string unrelatedReady = Path.Combine(root, "unrelated");
            Process unrelated = await StartWorker("control", unrelatedReady);
            using (var empty = new BatchRenderLifetime())
            {
                Check("A new lifetime has no running render processes", !empty.HasRunningProcesses);
                Check("A new lifetime does not start canceled", !empty.IsCancellationRequested && !empty.Token.IsCancellationRequested);
                await WithTimeout(empty.CancelAsync(), "empty cancellation");
                Check("Closing an inactive lifetime cancels its token", empty.IsCancellationRequested && empty.Token.IsCancellationRequested);
                await WithTimeout(empty.CancelAsync(), "repeated empty cancellation");
                Check("Repeated inactive cancellation remains complete", !empty.HasRunningProcesses);
                Check("Closing an inactive lifetime leaves an unrelated process running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Inactive close leaves unrelated work responsive");
            }

            using (var lifetime = new BatchRenderLifetime())
            {
                string ready = Path.Combine(root, "owned");
                Process parent = await StartWorker("tree", ready);
                Process grandchild = ReadGrandchild(ready);
                lifetime.TrackProcess(parent);
                Check("Tracking a live render reports an active process", lifetime.HasRunningProcesses);
                lifetime.TrackProcess(parent);
                Task firstCancel = lifetime.CancelAsync();
                Check("Cancellation signals the session token before process cleanup finishes", lifetime.IsCancellationRequested && lifetime.Token.IsCancellationRequested);
                await WithTimeout(Task.WhenAll(firstCancel, lifetime.CancelAsync()), "concurrent owned tree cancellation");
                await WaitUntil(() => !IsRunning(parent) && !IsRunning(grandchild), "owned parent and grandchild exit");
                Check("Cancellation terminates the owned parent", !IsRunning(parent));
                Check("Cancellation terminates the owned grandchild", !IsRunning(grandchild));
                Check("Cancellation leaves an unrelated process running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Tree cancellation leaves unrelated work responsive");
                Check("A canceled lifetime no longer reports running processes", !lifetime.HasRunningProcesses);
                Check("Cancellation does not dispose the caller's parent Process", parent.Id > 0 && parent.HasExited);
                await WithTimeout(lifetime.CancelAsync(), "repeated owned cancellation");
                Check("Repeated cancellation completes without disturbing unrelated work", IsRunning(unrelated));

                string lateReady = Path.Combine(root, "late");
                Process lateParent = await StartWorker("tree", lateReady);
                Process lateGrandchild = ReadGrandchild(lateReady);
                lifetime.TrackProcess(lateParent);
                await WaitUntil(() => !IsRunning(lateParent) && !IsRunning(lateGrandchild), "automatic late process cleanup");
                Check("Tracking after cancellation starts termination without another cancellation call", !IsRunning(lateParent) && !IsRunning(lateGrandchild));
                await WithTimeout(lifetime.CancelAsync(), "late process cancellation");
                await WaitUntil(() => !IsRunning(lateParent) && !IsRunning(lateGrandchild), "late parent and grandchild exit");
                Check("A process tracked after cancellation is terminated", !IsRunning(lateParent));
                Check("A second cancellation drains the late process's descendants", !IsRunning(lateGrandchild));
                Check("Late process cleanup leaves unrelated work running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Late process cleanup leaves unrelated work responsive");
                Check("Late cleanup leaves no owned process running", !lifetime.HasRunningProcesses);
            }

            using (var concurrent = new BatchRenderLifetime())
            {
                string ready = Path.Combine(root, "concurrent-parent");
                Process parent = await StartWorker("tree", ready);
                Process grandchild = ReadGrandchild(ready);
                concurrent.TrackProcess(parent);
                var pending = new Process[6];
                for (int index = 0; index < pending.Length; index++)
                    pending[index] = await StartWorker("leaf", Path.Combine(root, "concurrent-late-" + index));

                Task cancellation = concurrent.CancelAsync();
                int overlap = cancellation.IsCompleted ? 0 : 1;
                concurrent.TrackProcess(pending[0]);
                var trackers = new Task[pending.Length - 1];
                for (int index = 1; index < pending.Length; index++)
                {
                    Process process = pending[index];
                    trackers[index - 1] = Task.Run(() =>
                    {
                        if (!cancellation.IsCompleted) Interlocked.Increment(ref overlap);
                        concurrent.TrackProcess(process);
                    });
                }
                await WithTimeout(Task.WhenAll(trackers), "concurrent process tracking");
                Check("A late TrackProcess call overlaps an unfinished cancellation", overlap > 0);
                await WithTimeout(cancellation, "cancellation with concurrent process tracking");
                await WithTimeout(concurrent.CancelAsync(), "concurrent process tracking drain");
                await WaitUntil(() => !IsRunning(parent) && !IsRunning(grandchild), "concurrent parent tree exit");
                Check("Concurrent tracking cancellation stops the original parent and grandchild", !IsRunning(parent) && !IsRunning(grandchild));
                for (int index = 0; index < pending.Length; index++)
                {
                    Process process = pending[index];
                    await WaitUntil(() => !IsRunning(process), "concurrently tracked worker exit");
                    Check("Concurrent tracking cancellation stops late owned worker " + (index + 1), !IsRunning(process));
                }
                Check("Concurrent tracking drains every owned process", !concurrent.HasRunningProcesses);
                Check("Concurrent tracking cancellation leaves unrelated work running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Concurrent tracking cancellation leaves unrelated work responsive");
            }

            using (var completed = new BatchRenderLifetime())
            {
                string ready = Path.Combine(root, "normal");
                Process worker = await StartWorker("once", ready);
                completed.TrackProcess(worker);
                File.WriteAllText(ready + ".release", "complete");
                await WaitUntil(() => worker.HasExited, "normal worker completion");
                Check("Normal render completion clears the running state", !completed.HasRunningProcesses);
                Check("Normal render completion does not cancel the session token", !completed.Token.IsCancellationRequested);
                await WithTimeout(completed.CancelAsync(), "completed process cancellation");
                Check("Closing a completed session leaves unrelated work running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Completed-session cleanup leaves unrelated work responsive");
                Check("Completed-session cleanup does not dispose the caller's Process", worker.Id > 0 && worker.HasExited);
            }

            using (var orphaned = new BatchRenderLifetime())
            {
                string ready = Path.Combine(root, "completed-parent");
                Process parent = await StartWorker("tree-once", ready);
                Process grandchild = ReadGrandchild(ready);
                orphaned.TrackProcess(parent);
                File.WriteAllText(ready + ".release", "complete");
                await WaitUntil(() => parent.HasExited, "parent normal completion");
                Check("The captured grandchild remains alive after its parent exits", IsRunning(grandchild));
                Check("An exited parent still reports its running captured descendant", orphaned.HasRunningProcesses);
                await WithTimeout(orphaned.CancelAsync(), "captured descendant cancellation");
                await WaitUntil(() => !IsRunning(grandchild), "captured descendant exit");
                Check("Cancellation terminates a captured descendant after its parent exits", !IsRunning(grandchild));
                Check("Captured-descendant cleanup leaves unrelated work running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Captured-descendant cleanup leaves unrelated work responsive");
            }

            using (var nextSession = new BatchRenderLifetime())
            {
                Check("An independent later session starts uncanceled", !nextSession.Token.IsCancellationRequested);
                string ready = Path.Combine(root, "next-session");
                Process parent = await StartWorker("tree", ready);
                Process grandchild = ReadGrandchild(ready);
                nextSession.TrackProcess(parent);
                Check("An independent later session can track a new render", nextSession.HasRunningProcesses);
                await WithTimeout(nextSession.CancelAsync(), "independent session cancellation");
                await WaitUntil(() => !IsRunning(parent) && !IsRunning(grandchild), "independent session tree exit");
                Check("An independent later session cancels its own complete render tree", !IsRunning(parent) && !IsRunning(grandchild));
                Check("Independent-session cleanup still leaves unrelated work running", IsRunning(unrelated));
                await CheckControlHeartbeat(unrelatedReady, "Independent-session cleanup leaves unrelated work responsive");
            }

            Console.WriteLine("PASS " + passed + " batch render lifetime checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (OwnedProcess owned in cleanup)
            {
                try
                {
                    using (Process current = Process.GetProcessById(owned.Id))
                    {
                        if (current.StartTime.ToUniversalTime().Ticks == owned.StartTicks && IsRunning(current))
                        {
                            current.Kill(true);
                            current.WaitForExit(5000);
                        }
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static async Task<int> RunWorker(string[] args)
    {
        string kind = args[0].Substring("--worker-".Length);
        string ready = args[1];
        if (kind == "tree" || kind == "tree-once")
        {
            using (Process child = Process.Start(SelfStartInfo("leaf", ready + ".leaf")))
            {
                await WaitUntil(() => File.Exists(ready + ".leaf"), "child handshake");
                WriteReady(ready, child.Id.ToString(CultureInfo.InvariantCulture));
                if (kind == "tree-once")
                {
                    await WaitUntil(() => File.Exists(ready + ".release"), "parent release");
                    return 0;
                }
                await Task.Delay(System.Threading.Timeout.Infinite);
            }
        }
        else
        {
            if (kind == "control") File.WriteAllText(ready + ".beat", "0");
            WriteReady(ready, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            if (kind == "once")
                await WaitUntil(() => File.Exists(ready + ".release"), "worker release");
            else if (kind == "control")
            {
                int heartbeat = 0;
                while (true)
                {
                    await Task.Delay(50);
                    File.WriteAllText(ready + ".beat", (++heartbeat).ToString(CultureInfo.InvariantCulture));
                }
            }
            else
                await Task.Delay(System.Threading.Timeout.Infinite);
        }
        return 0;
    }

    private static async Task<Process> StartWorker(string kind, string ready)
    {
        Process process = Process.Start(SelfStartInfo(kind, ready));
        Remember(process);
        await WaitUntil(() => File.Exists(ready), kind + " ready handshake");
        if (kind == "tree" || kind == "tree-once")
            Remember(Process.GetProcessById(int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture)));
        return process;
    }

    private static ProcessStartInfo SelfStartInfo(string kind, string ready)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--worker-" + kind);
        info.ArgumentList.Add(ready);
        return info;
    }

    private static Process ReadGrandchild(string ready)
    {
        return Process.GetProcessById(int.Parse(File.ReadAllText(ready), CultureInfo.InvariantCulture));
    }

    private static void WriteReady(string path, string value)
    {
        File.WriteAllText(path + ".writing", value);
        File.Move(path + ".writing", path);
    }

    private static void Remember(Process process)
    {
        cleanup.Add(new OwnedProcess { Id = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks });
    }

    private static bool IsRunning(Process process)
    {
        try
        {
            process.Refresh();
            if (process.HasExited) return false;
            if (OperatingSystem.IsLinux())
            {
                string stat = File.ReadAllText("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/stat");
                int close = stat.LastIndexOf(')');
                if (close >= 0 && close + 2 < stat.Length && (stat[close + 2] == 'Z' || stat[close + 2] == 'X')) return false;
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
    }

    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Timeout) throw new TimeoutException("Timed out waiting for " + description);
            await Task.Delay(25);
        }
    }

    private static async Task WithTimeout(Task task, string description)
    {
        if (await Task.WhenAny(task, Task.Delay(Timeout)) != task)
            throw new TimeoutException("Timed out waiting for " + description);
        await task;
    }

    private static async Task CheckControlHeartbeat(string ready, string description)
    {
        DateTime before = File.GetLastWriteTimeUtc(ready + ".beat");
        await WaitUntil(() => File.GetLastWriteTimeUtc(ready + ".beat") > before, description);
        Check(description, true);
    }

    private static void Check(string description, bool condition)
    {
        if (!condition) throw new InvalidOperationException(description);
        passed++;
        Console.WriteLine("PASS " + description);
    }

    private sealed class OwnedProcess
    {
        public int Id;
        public long StartTicks;
    }
}
