using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        internal static string StorageDirectory { get; set; }
        public static bool FastPreviewEnabled { get; private set; } = true;
        public static bool FastRenderEnabled { get; private set; } = true;
        internal static int BusyTransitions;
        internal static void SetPerformanceModes(bool preview, bool make) { FastPreviewEnabled = preview; FastRenderEnabled = make; }
        internal static void SetPerformanceBusy(object owner, bool busy) { BusyTransitions++; }
    }
}

internal static class Program
{
    private static int passed;
    private static async Task Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "gorilla-benchmark-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FolderBridge.StorageDirectory = root;
        try
        {
            Equal("Faster times show saved seconds and measured factor", "Preview: original 20.00 s → fast 5.00 s; 15.00 s saved (75.0% less time, 4.00× speed).",
                FolderBridge.BenchmarkSpeedSummary("Preview", 20, 5));
            Equal("Slower times report longer rather than a false speed-up", "MAKE: original 10.00 s → fast 15.00 s; 5.00 s longer (50.0% more time, 0.67× speed).",
                FolderBridge.BenchmarkSpeedSummary("MAKE", 10, 15));
            True("Equal times show no change", FolderBridge.BenchmarkSpeedSummary("Preview", 5, 5).Contains("0.0% less time"));
            Throws("A zero original time is not a measurement", () => FolderBridge.BenchmarkSpeedSummary("Preview", 0, 2));
            Throws("A zero new time cannot claim infinite speed", () => FolderBridge.BenchmarkSpeedSummary("Preview", 2, 0));
            Throws("NaN time is rejected", () => FolderBridge.BenchmarkSpeedSummary("Preview", double.NaN, 2));
            Throws("Infinite time is rejected", () => FolderBridge.BenchmarkSpeedSummary("Preview", 2, double.PositiveInfinity));
            string[] original = { "character", "taunt", "my renders", "quick=1", "work=user work", "hair=auto", "slot=4000" };
            List<string> isolated = FolderBridge.BenchmarkIsolate(original, "comparison output", "comparison work");
            Equal("Ordinary output stays unchanged", "my renders", original[2]);
            Equal("Benchmark output is isolated", "comparison output", isolated[2]);
            Equal("A single shared work folder replaces any original override", "work=comparison work", isolated.Single(value => value.StartsWith("work=")));
            True("Character, taunt and roster target are kept", isolated[0] == "character" && isolated[1] == "taunt" && isolated.Contains("slot=4000"));

            var full = new FakeOwner();
            string report = await FolderBridge.CompareRenderSpeeds(full, true, value => { });
            Equal("Full comparison runs one warmup and all four measured cases", 5, full.Calls.Count);
            Equal("Each case uses the intended performance mode", "False/False,False/False,True/False,False/False,False/True", string.Join(",", full.Calls.Select(value => value.Modes)));
            True("Both measured comparisons are reported", report.Contains("Preview: original ") && report.Contains("MAKE: original "));
            True("Work reduction is explicitly separate from wall-clock time", report.Contains("77.8% fewer pixel-samples") && report.Contains("not elapsed-time predictions"));
            True("Comparison never uses ordinary output", full.Calls.All(value => value.Output != "my renders" && value.Output.StartsWith(root)));
            Equal("Every case shares the same prepared model work", 1, full.Calls.Select(value => value.Work).Distinct().Count());
            True("Native busy state is held during every engine call", full.Calls.All(value => value.Busy));
            True("Native interactive controls are disabled during benchmark", full.Calls.All(value => value.ControlsDisabled));
            True("Native controls and busy state are restored", !full.Busy && full.PreviewButton.IsEnabled && full.MakeButton.IsEnabled && full.BodyGrid.IsEnabled && !full.InstallButton.IsEnabled);
            True("Original mode choices are restored", FolderBridge.FastPreviewEnabled && FolderBridge.FastRenderEnabled);
            Equal("Background render is stopped once", 1, full.StopCalls);
            True("Persistent text report contains actual measurements", File.ReadAllText(Path.Combine(root, "performance-comparison.txt")).Contains("MAKE: original "));
            True("Structured measurements are saved", Directory.GetFiles(root, "comparison.json", SearchOption.AllDirectories).Any());
            True("Comparison declares measured order and setup caveat", report.Contains("original then fast") && report.Contains("first full-resolution MAKE"));

            var previews = new FakeOwner();
            FolderBridge.SetPerformanceModes(false, true);
            string previewReport = await FolderBridge.CompareRenderSpeeds(previews, false, value => { throw new Exception("Broken status display"); });
            Equal("Preview-only comparison does not render MAKE", 3, previews.Calls.Count);
            True("Preview-only report has no MAKE measurement", previewReport.Contains("Preview: original") && !previewReport.Contains("MAKE: original"));
            True("Progress callback exceptions do not interrupt rendering", !previewReport.Contains("Comparison stopped:"));
            True("User performance flags restore even when disabled", !FolderBridge.FastPreviewEnabled && FolderBridge.FastRenderEnabled);

            var locked = new FakeOwner { IsUnlocked = false };
            await Rejects("Licensing is required before benchmarking", () => FolderBridge.CompareRenderSpeeds(locked, true, null));
            Equal("A locked application starts no process", 0, locked.Calls.Count);
            var busy = new FakeOwner { Busy = true };
            await Rejects("An existing render cannot be overlapped", () => FolderBridge.CompareRenderSpeeds(busy, true, null));
            True("An existing busy state stays set", busy.Busy);
            var missing = new FakeOwner { TargetSlot = "" };
            await Rejects("A roster slot is required", () => FolderBridge.CompareRenderSpeeds(missing, true, null));
            var unticked = new FakeOwner { ReturnNullArguments = true };
            await Rejects("The native screen selection guard is preserved", () => FolderBridge.CompareRenderSpeeds(unticked, true, null));

            var failure = new FakeOwner { FailCall = 3 };
            string failed = await FolderBridge.CompareRenderSpeeds(failure, true, null);
            True("A failed engine does not produce a speed factor", failed.Contains("Comparison stopped:") && failed.Contains("No speed-up is calculated") && !failed.Contains("× speed"));
            True("Failure preserves the engine error", failed.Contains("simulated render error"));
            True("Failure restores modes, controls and native busy state", !failure.Busy && failure.BodyGrid.IsEnabled && !FolderBridge.FastPreviewEnabled && FolderBridge.FastRenderEnabled);
            var mismatch = new FakeOwner { DifferentFrameCounts = true };
            string changed = await FolderBridge.CompareRenderSpeeds(mismatch, false, null);
            True("Changing frame counts cannot claim a speed-up", changed.Contains("frame counts changed") && !changed.Contains("× speed"));

            string artifact = Path.Combine(root, "valid.png");
            File.WriteAllBytes(artifact, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            string valid = "RESULT " + PreviewJson.Write(new Dictionary<string, object> { { "frames", 59 }, { "preview", artifact } });
            Equal("A completed preview validates", 59, FolderBridge.BenchmarkValidateResult(0, new[] { valid }, true, root, "Preview"));
            Throws("A success exit without RESULT is rejected", () => FolderBridge.BenchmarkValidateResult(0, new string[0], true, root, "Preview"));
            Throws("Malformed RESULT is rejected", () => FolderBridge.BenchmarkValidateResult(0, new[] { "RESULT {bad}" }, true, root, "Preview"));
            Throws("A failed exit cannot accept a success RESULT", () => FolderBridge.BenchmarkValidateResult(2, new[] { valid }, true, root, "Preview"));
            Throws("An artifact outside comparison output is rejected", () => FolderBridge.BenchmarkValidateResult(0, new[] { valid }, true, Path.Combine(root, "other"), "Preview"));
            Throws("A MAKE result must contain a render file", () => FolderBridge.BenchmarkValidateResult(0, new[] { valid }, false, root, "MAKE"));
            File.Delete(artifact);
            Throws("A missing artifact cannot count as a successful timed render", () => FolderBridge.BenchmarkValidateResult(0, new[] { valid }, true, root, "Preview"));
            Console.WriteLine("Passed " + passed + " render benchmark checks.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void True(string label, bool condition) { if (!condition) throw new Exception(label); passed++; }
    private static void Equal<T>(string label, T expected, T actual) { if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual); passed++; }
    private static void Throws(string label, Action action) { try { action(); } catch { passed++; return; } throw new Exception(label); }
    private static async Task Rejects(string label, Func<Task<string>> action) { try { await action(); } catch { passed++; return; } throw new Exception(label); }

    private sealed class FakeControl { public bool IsEnabled { get; set; } = true; public object SelectedItem { get; set; } = "taunt"; }
    private sealed class Call { public string Modes; public string Output; public string Work; public bool Busy; public bool ControlsDisabled; }
    private sealed class FakeOwner
    {
        public string Folder = "model";
        public string TargetSlot = "4000";
        public string Studio = "engine";
        public bool Busy;
        public Task PrepTask = Task.CompletedTask;
        public FakeControl TauntList = new FakeControl();
        public FakeControl PreviewButton = new FakeControl();
        public FakeControl MakeButton = new FakeControl();
        public FakeControl BodyGrid = new FakeControl();
        public FakeControl InstallButton = new FakeControl { IsEnabled = false };
        public List<Call> Calls = new List<Call>();
        public bool IsUnlocked = true;
        public bool ReturnNullArguments;
        public bool DifferentFrameCounts;
        public int FailCall;
        public int StopCalls;
        private bool Unlocked() { return IsUnlocked; }
        private void StopBackgroundMake() { StopCalls++; }
        private List<string> BuildArgs(object taunt, bool quick)
        {
            if (ReturnNullArguments) return null;
            var result = new List<string> { Folder, Convert.ToString(taunt, CultureInfo.InvariantCulture), "my renders", "slot=" + TargetSlot };
            if (quick) result.Add("quick=1");
            return result;
        }
        private async Task<ValueTuple<int, List<string>>> RunPy(string script, IEnumerable<string> arguments, Action<string> line, Action<Process> started)
        {
            List<string> args = arguments.ToList();
            Calls.Add(new Call { Modes = FolderBridge.FastPreviewEnabled + "/" + FolderBridge.FastRenderEnabled,
                Output = args[2], Work = args.Single(value => value.StartsWith("work=")), Busy = Busy,
                ControlsDisabled = !BodyGrid.IsEnabled && !PreviewButton.IsEnabled && !MakeButton.IsEnabled && !InstallButton.IsEnabled });
            if (line != null) line("STAGE 7/9 Rendering");
            await Task.Delay(5);
            if (FailCall == Calls.Count) return new ValueTuple<int, List<string>>(2, new List<string> { "simulated render error" });
            bool preview = args.Contains("quick=1");
            string artifact = Path.Combine(args[2], preview ? "preview.png" : "render.gmr");
            File.WriteAllBytes(artifact, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            int frames = DifferentFrameCounts && FolderBridge.FastPreviewEnabled ? 2 : 59;
            string result = "RESULT " + PreviewJson.Write(new Dictionary<string, object> { { "frames", frames }, { preview ? "preview" : "gmr", artifact } });
            return new ValueTuple<int, List<string>>(0, new List<string> { result });
        }
    }
}
