using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        // Runs the original signed-in application's process launcher directly. No preview cache,
        // installer, game files, or ordinary render output are used by this comparison.
        public static async Task<string> CompareRenderSpeeds(object owner, bool includeMake, Action<string> status)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            Type type = owner.GetType();
            MethodInfo unlocked = BenchmarkMethod(type, "Unlocked", 0);
            FieldInfo busy = type.GetField("Busy", BenchmarkMembers);
            if (unlocked == null || busy == null || busy.FieldType != typeof(bool))
                throw new InvalidOperationException("This version cannot start the speed comparison safely.");
            if (!(bool)unlocked.Invoke(owner, null))
                throw new InvalidOperationException("Sign in and unlock rendering before comparing speeds.");
            if ((bool)busy.GetValue(owner))
                throw new InvalidOperationException("Wait for the current preview or render to finish first.");
            if (string.IsNullOrWhiteSpace(BenchmarkField(owner, "Folder") as string))
                throw new InvalidOperationException("Load a wrestler before comparing speeds.");
            if (string.IsNullOrWhiteSpace(BenchmarkField(owner, "TargetSlot") as string))
                throw new InvalidOperationException("Choose the wrestler's roster slot first.");

            object list = BenchmarkField(owner, "TauntList");
            PropertyInfo selected = list == null ? null : list.GetType().GetProperty("SelectedItem");
            object taunt = selected == null ? null : selected.GetValue(list, null);
            if (taunt == null) throw new InvalidOperationException("Choose a taunt before comparing speeds.");
            MethodInfo build = BenchmarkMethod(type, "BuildArgs", 2);
            MethodInfo run = BenchmarkMethod(type, "RunPy", 4);
            MethodInfo stop = BenchmarkMethod(type, "StopBackgroundMake", 0);
            if (build == null || run == null || stop == null)
                throw new InvalidOperationException("The original render launcher is unavailable.");
            List<string> previewArgs = BenchmarkArguments(owner, build, taunt, true);
            List<string> makeArgs = includeMake ? BenchmarkArguments(owner, build, taunt, false) : null;
            string studio = BenchmarkField(owner, "Studio") as string;
            if (string.IsNullOrWhiteSpace(studio))
                throw new InvalidOperationException("The render engine folder is unavailable.");

            bool previousPreview = FastPreviewEnabled;
            bool previousMake = FastRenderEnabled;
            bool previousBusy = (bool)busy.GetValue(owner);
            List<BenchmarkControlState> controls = BenchmarkControls(owner);
            var measurements = new List<BenchmarkMeasurement>();
            double preparationSeconds = 0;
            string failure = null;
            string directory = null;
            bool uiBusy = false;
            try
            {
                busy.SetValue(owner, true);
                uiBusy = true;
                SetPerformanceBusy(owner, true);
                foreach (BenchmarkControlState control in controls) control.Property.SetValue(control.Control, false, null);
                stop.Invoke(owner, null);
                Task prep = BenchmarkField(owner, "PrepTask") as Task;
                if (prep != null && !prep.IsCompleted)
                {
                    BenchmarkStatus(status, "Waiting for the current model preparation…");
                    Stopwatch preparing = Stopwatch.StartNew();
                    try { await prep; }
                    catch { /* The measured original process reports a preparation failure itself. */ }
                    preparationSeconds = preparing.Elapsed.TotalSeconds;
                }

                directory = Path.Combine(StorageDirectory, "benchmarks",
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string output = Path.Combine(directory, "output");
                string work = Path.Combine(directory, "work");
                Directory.CreateDirectory(output);
                previewArgs = BenchmarkIsolate(previewArgs, output, work);
                if (makeArgs != null) makeArgs = BenchmarkIsolate(makeArgs, output, work);
                string script = Path.Combine(studio, "pipeline", "gorilla_render.py");

                measurements.Add(await BenchmarkRun(owner, run, script, previewArgs, "Warmup preview", true, false, true, output, status));
                measurements.Add(await BenchmarkRun(owner, run, script, previewArgs, "Original preview", true, false, false, output, status));
                measurements.Add(await BenchmarkRun(owner, run, script, previewArgs, "Fast preview", true, true, false, output, status));
                if (measurements[1].Frames != measurements[2].Frames)
                    throw new InvalidOperationException("Preview frame counts changed between runs; no preview speed-up will be calculated.");
                if (includeMake)
                {
                    measurements.Add(await BenchmarkRun(owner, run, script, makeArgs, "Original MAKE", false, false, false, output, status));
                    measurements.Add(await BenchmarkRun(owner, run, script, makeArgs, "Fast MAKE", false, true, false, output, status));
                    if (measurements[3].Frames != measurements[4].Frames)
                        throw new InvalidOperationException("MAKE frame counts changed between runs; no MAKE speed-up will be calculated.");
                }
            }
            catch (Exception error)
            {
                var invocation = error as TargetInvocationException;
                failure = (invocation != null && invocation.InnerException != null ? invocation.InnerException : error).Message;
            }
            finally
            {
                SetPerformanceModes(previousPreview, previousMake);
                busy.SetValue(owner, previousBusy);
                if (uiBusy) SetPerformanceBusy(owner, false);
                foreach (BenchmarkControlState control in controls)
                    control.Property.SetValue(control.Control, control.Enabled, null);
            }

            string report = BenchmarkReport(measurements, preparationSeconds, failure);
            if (directory != null)
            {
                try
                {
                    var document = new Dictionary<string, object>
                    {
                        { "version", 1 }, { "createdUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                        { "preparationSeconds", preparationSeconds }, { "measurements", measurements },
                        { "error", failure }, { "text", report }
                    };
                    File.WriteAllText(Path.Combine(directory, "comparison.json"), PreviewJson.Write(document), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(directory, "comparison.txt"), report, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(StorageDirectory, "performance-comparison.txt"), report + "\n\nReports: " + directory, new UTF8Encoding(false));
                    report += "\n\nReports: " + directory;
                }
                catch (Exception error) { report += "\n\nCould not save the comparison report: " + error.Message; }
            }
            BenchmarkStatus(status, failure == null ? "Speed comparison complete." : "Speed comparison stopped.");
            return report;
        }

        private const BindingFlags BenchmarkMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo BenchmarkMethod(Type type, string name, int parameters)
        {
            return type.GetMethods(BenchmarkMembers).FirstOrDefault(method => method.Name == name && method.GetParameters().Length == parameters);
        }

        private static object BenchmarkField(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, BenchmarkMembers);
            return field == null ? null : field.GetValue(owner);
        }

        private static List<string> BenchmarkArguments(object owner, MethodInfo build, object taunt, bool preview)
        {
            var arguments = build.Invoke(owner, new[] { taunt, (object)preview }) as IEnumerable<string>;
            if (arguments == null) throw new InvalidOperationException("Tick Roster selection, Match card, or both before comparing speeds.");
            var result = arguments.ToList();
            if (result.Count < 3) throw new InvalidOperationException("The original renderer did not provide its input arguments.");
            return result;
        }

        internal static List<string> BenchmarkIsolate(IEnumerable<string> original, string output, string work)
        {
            List<string> arguments = original.ToList();
            if (arguments.Count < 3) throw new ArgumentException("The render needs a character, taunt and output folder.");
            arguments[2] = output;
            arguments.RemoveAll(argument => argument != null && argument.StartsWith("work=", StringComparison.Ordinal));
            arguments.Add("work=" + work);
            return arguments;
        }

        private static async Task<BenchmarkMeasurement> BenchmarkRun(object owner, MethodInfo run, string script,
            List<string> arguments, string name, bool preview, bool fast, bool warmup, string output, Action<string> status)
        {
            SetPerformanceModes(preview && fast, !preview && fast);
            BenchmarkStatus(status, name + "…");
            Action<string> line = value =>
            {
                if (value != null && (value.StartsWith("STAGE ", StringComparison.Ordinal) || value.StartsWith("FRAMES ", StringComparison.Ordinal)))
                    BenchmarkStatus(status, name + ": " + value);
            };
            Stopwatch timer = Stopwatch.StartNew();
            object launched = run.Invoke(owner, new object[] { script, arguments, line, (Action<Process>)null });
            var task = launched as Task<ValueTuple<int, List<string>>>;
            if (task == null) throw new InvalidOperationException("The original renderer returned an unsupported task.");
            ValueTuple<int, List<string>> result = await task;
            timer.Stop();
            int frames = BenchmarkValidateResult(result.Item1, result.Item2, preview, output, name);
            var measurement = new BenchmarkMeasurement
            {
                Name = name, Preview = preview, Fast = fast, Warmup = warmup,
                Seconds = timer.Elapsed.TotalSeconds, Frames = frames
            };
            BenchmarkStatus(status, name + ": " + measurement.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " seconds.");
            return measurement;
        }

        internal static int BenchmarkValidateResult(int exitCode, IEnumerable<string> lines, bool preview, string output, string name)
        {
            List<string> recorded = lines == null ? new List<string>() : lines.ToList();
            if (exitCode != 0)
                throw new InvalidOperationException(name + " failed (exit " + exitCode + "). " +
                    (recorded.LastOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("RESULT ", StringComparison.Ordinal)) ?? "No engine error was reported."));
            string last = recorded.LastOrDefault(value => value != null && value.StartsWith("RESULT ", StringComparison.Ordinal));
            Dictionary<string, object> result;
            try { result = last == null ? null : PreviewJson.Read(last.Substring(7)); }
            catch { result = null; }
            object frameValue;
            int frames;
            if (result == null || !result.TryGetValue("frames", out frameValue) ||
                !int.TryParse(Convert.ToString(frameValue, CultureInfo.InvariantCulture), out frames) || frames <= 0)
                throw new InvalidOperationException(name + " did not return a valid completed RESULT with a frame count.");
            object fileValue;
            string artifact = null;
            if (preview)
            {
                if (result.ContainsKey("gmr")) throw new InvalidOperationException(name + " unexpectedly returned a full render.");
                if (result.TryGetValue("play", out fileValue)) artifact = fileValue as string;
                if (string.IsNullOrWhiteSpace(artifact) && result.TryGetValue("preview", out fileValue)) artifact = fileValue as string;
            }
            else if (result.TryGetValue("gmr", out fileValue)) artifact = fileValue as string;
            string allowed = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (string.IsNullOrWhiteSpace(artifact) || !Path.GetFullPath(artifact).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(artifact) || new FileInfo(artifact).Length == 0)
                throw new InvalidOperationException(name + " did not produce its expected comparison file.");
            return frames;
        }

        internal static string BenchmarkSpeedSummary(string label, double originalSeconds, double fastSeconds)
        {
            if (double.IsNaN(originalSeconds) || double.IsInfinity(originalSeconds) || originalSeconds <= 0 ||
                double.IsNaN(fastSeconds) || double.IsInfinity(fastSeconds) || fastSeconds <= 0)
                throw new ArgumentOutOfRangeException("originalSeconds", "Both measured times must be positive and finite.");
            double saved = originalSeconds - fastSeconds;
            double percent = Math.Abs(saved) / originalSeconds * 100;
            return label + ": original " + originalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s → fast " +
                fastSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s; " + Math.Abs(saved).ToString("0.00", CultureInfo.InvariantCulture) +
                " s " + (saved >= 0 ? "saved" : "longer") + " (" + percent.ToString("0.0", CultureInfo.InvariantCulture) + "% " +
                (saved >= 0 ? "less time" : "more time") + ", " + (originalSeconds / fastSeconds).ToString("0.00", CultureInfo.InvariantCulture) + "× speed).";
        }

        private static string BenchmarkReport(List<BenchmarkMeasurement> measurements, double preparationSeconds, string failure)
        {
            var report = new StringBuilder("Measured on this computer.\n");
            if (failure != null)
                report.AppendLine("Comparison stopped: " + failure).AppendLine("No speed-up is calculated for this incomplete comparison.");
            else
            {
                BenchmarkMeasurement oldPreview = measurements.First(value => value.Preview && !value.Fast && !value.Warmup);
                BenchmarkMeasurement newPreview = measurements.First(value => value.Preview && value.Fast && !value.Warmup);
                report.AppendLine(BenchmarkSpeedSummary("Preview", oldPreview.Seconds, newPreview.Seconds));
                BenchmarkMeasurement oldMake = measurements.FirstOrDefault(value => !value.Preview && !value.Fast && !value.Warmup);
                BenchmarkMeasurement newMake = measurements.FirstOrDefault(value => !value.Preview && value.Fast && !value.Warmup);
                if (oldMake != null && newMake != null) report.AppendLine(BenchmarkSpeedSummary("MAKE", oldMake.Seconds, newMake.Seconds));
            }
            foreach (BenchmarkMeasurement measurement in measurements)
                report.AppendLine(measurement.Name + ": " + measurement.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s (" + measurement.Frames + " frames).");
            if (preparationSeconds > 0)
                report.AppendLine("Wait for existing model preparation: " + preparationSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s (excluded).");
            report.AppendLine("One original-preview warmup is excluded. Measured runs use original then fast settings and a shared prepared model; preview cache hits are bypassed.");
            report.AppendLine("Process startup, rendering and encoding are included. The first full-resolution MAKE may do additional setup. These are single runs; GPU load and render order can affect the comparison.");
            report.AppendLine("Default renderer work: preview 384² × 16 → 256² × 8 = 77.8% fewer pixel-samples; MAKE 1024² × 64 → 1024² × 32 = 50% fewer samples. These work reductions are not elapsed-time predictions; inherited renderer overrides can change the original settings.");
            report.Append("Fast preview uses a smaller picture; fast MAKE keeps the original size with fewer shadow samples. Comparison outputs are separate from your renders; nothing is installed into the game.");
            return report.ToString();
        }

        private static void BenchmarkStatus(Action<string> status, string value)
        {
            try { if (status != null) status(value); }
            catch { /* Progress text must never interrupt the render process. */ }
        }

        private static List<BenchmarkControlState> BenchmarkControls(object owner)
        {
            var states = new List<BenchmarkControlState>();
            foreach (string name in new[] { "BodyGrid", "PreviewButton", "MakeButton", "InstallButton" })
            {
                object control = BenchmarkField(owner, name);
                PropertyInfo enabled = control == null ? null : control.GetType().GetProperty("IsEnabled");
                if (enabled != null && enabled.PropertyType == typeof(bool) && enabled.CanRead && enabled.CanWrite)
                    states.Add(new BenchmarkControlState { Control = control, Property = enabled, Enabled = (bool)enabled.GetValue(control, null) });
            }
            return states;
        }

        private sealed class BenchmarkControlState
        {
            internal object Control;
            internal PropertyInfo Property;
            internal bool Enabled;
        }

        private sealed class BenchmarkMeasurement
        {
            public string Name { get; set; }
            public bool Preview { get; set; }
            public bool Fast { get; set; }
            public bool Warmup { get; set; }
            public double Seconds { get; set; }
            public int Frames { get; set; }
        }
    }
}
