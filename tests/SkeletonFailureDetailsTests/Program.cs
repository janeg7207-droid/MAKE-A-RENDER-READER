using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static string root, character, output, folder, log, marker, attire, body;
    private static DateTime started;
    private static string[] request;

    private static void Main(string[] uploadedLogs)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-skeleton-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Setup();
            string sourceBefore = Hash(attire) + Hash(body);
            SkeletonFailureReport report = Find();
            Check(report != null && report.LogPath == log, "The exact current model receives its fresh skeleton log");
            Check(report.Summary == "ValueError: bad marshal data", "The final underlying script error becomes the short summary");
            Check(report.FullDetails.Contains(log) && report.FullDetails.Contains("Traceback") && report.FullDetails.Contains("does not establish"), "Details include the real path and traceback without inventing a missing skeleton");
            Check(Hash(attire) + Hash(body) == sourceBefore, "Diagnostics never modify source models");
            string originalLog = File.ReadAllText(log);
            File.WriteAllText(log, "00:01.719  reports          | ERROR Cannot read file \"C:\\Users\\TestUser\\Documents\\Gorilla Studio\\Renders\\work\\582_abcdef1234_own\\model.blend\": No such file or directory\nBlender 5.2.1 LTS\n\nBlender quit\n");
            report = Find();
            Check(report.Summary.Contains("Cannot read file") && report.Summary.Contains("No such file or directory"),
                "The missing model file from the reported Blender failure becomes the actual diagnostic summary");
            Check(!report.Summary.Contains("no skeleton"), "A missing model file is never diagnosed as missing bones");
            foreach (string uploadedLog in uploadedLogs)
            {
                byte[] uploadedBytes = File.ReadAllBytes(uploadedLog);
                File.WriteAllBytes(log, uploadedBytes);
                report = Find();
                Check(report != null && report.Summary.Contains("Cannot read file") && report.Summary.Contains("No such file or directory"),
                    "The actual uploaded rest.log reports its missing model file: " + Path.GetFileName(Path.GetDirectoryName(uploadedLog)));
                Check(File.ReadAllBytes(uploadedLog).SequenceEqual(uploadedBytes), "Reading the uploaded failure preserves its original bytes");
            }
            File.WriteAllText(log, originalLog);
            string[] automatic = new[] { character, "t62247", output };
            Check(SkeletonFailureDetails.Find("gorilla_render.py", automatic, started) != null, "Automatic attire selection follows the native default folder policy");
            Directory.CreateDirectory(Path.Combine(character, "AAA_Alternate"));
            File.WriteAllText(Path.Combine(character, "AAA_Alternate", "AAA_Alternate.mcd"), "alternate");
            File.WriteAllText(Path.Combine(character, "Default_Attire", "AAA_unused.mcd"), "unused");
            Check(SkeletonFailureDetails.Find("gorilla_render.py", automatic, started).LogPath == log, "Default folders and attire filenames outrank earlier unrelated models");
            string alias = Path.Combine(root, "695_Brody_alias");
            Directory.CreateSymbolicLink(alias, character);
            string[] aliasRequest = new[] { alias, "t62247", output, "attire=Default_Attire/Attire.mcd" };
            Func<string, string> resolve = path => path.StartsWith(alias, StringComparison.Ordinal) ? character + path.Substring(alias.Length) : path;
            Check(SkeletonFailureDetails.Find("gorilla_render.py", aliasRequest, started, resolve).LogPath == log, "An exact resolved compatibility alias retains the correct source owner");

            string other = Path.Combine(output, "work", "999_Other_own");
            Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "rest.log"), "RuntimeError: another wrestler secret");
            Check(!Find().FullDetails.Contains("another wrestler"), "An unrelated fresh wrestler log is never borrowed");
            WriteMarker("Alternative/Other.mcd");
            Check(Find() == null, "A different attire cannot supply this request's failure");
            WriteMarker();
            File.SetLastWriteTimeUtc(log, started.AddMinutes(-10));
            Check(Find() == null, "A previous run's log is not mistaken for the current failure");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(1));
            Check(Find() == null, "An impossible future log is not accepted");
            File.WriteAllText(log, "Traceback\nREST_DUMP 175 rest.npz\nBlender quit\n");
            Check(Find() == null, "A completed skeleton dump is not reported as a skeleton failure");
            File.WriteAllText(log, "REST_DUMPING started\nRuntimeError: stopped before completion\n");
            Check(Find() != null, "A similar token cannot fabricate the native completion marker");
            File.WriteAllText(log, "");
            Check(Find() == null, "An empty log cannot invent a failure cause");
            File.WriteAllText(log, "Blender started\n");
            Check(Find().Summary == "Blender did not finish reading this model's skeleton.", "A truncated log reports only the known incomplete operation");
            File.WriteAllText(marker, "{broken");
            Check(Find() == null, "Malformed sidecar JSON leaves the failure unclaimed");
            File.WriteAllText(marker, "null");
            Check(Find() == null, "A null sidecar cannot establish ownership");
            File.WriteAllText(marker, "[]");
            Check(Find() == null, "An array sidecar cannot establish ownership");
            File.WriteAllText(marker, "{\"identity\":null}");
            Check(Find() == null, "A missing identity cannot establish ownership");
            File.WriteAllText(marker, new string(' ', 1024 * 1024 + 1));
            Check(Find() == null, "Oversized identity metadata is not parsed");
            WriteMarker();
            File.AppendAllText(attire, "changed");
            Check(Find() == null, "A changed current source invalidates its older model cache identity");
            WriteMarker();
            string twin = Path.Combine(output, "work", "695_second_own");
            Directory.CreateDirectory(twin); File.Copy(marker, Path.Combine(twin, "model.geometry.json"));
            File.WriteAllText(Path.Combine(twin, "rest.log"), "RuntimeError: simultaneous run\n");
            Check(Find() == null, "Two matching concurrent failures stay ambiguous instead of choosing one");
            Directory.Delete(twin, true);

            // Explicit work directories are exact native requests and do not
            // need a geometry marker from this or an older application version.
            File.Delete(marker);
            string[] explicitRequest = request.Concat(new[] { "work=" + folder }).ToArray();
            Check(SkeletonFailureDetails.Find("gorilla_render.py", explicitRequest, started) != null, "An exact explicit work directory supports legacy caches without sidecars");
            string[] lastWins = request.Concat(new[] { "work=" + other, "work=" + folder }).ToArray();
            Check(SkeletonFailureDetails.Find("gorilla_render.py", lastWins, started).LogPath == log, "The final work option follows native option precedence");
            string[] relative = request.Concat(new[] { "work=relative" }).ToArray();
            Check(SkeletonFailureDetails.Find("gorilla_render.py", relative, started) == null, "A relative native working directory is not guessed");
            Check(SkeletonFailureDetails.Directions("gorilla_render.py", request).Contains(Path.Combine(output, "work")), "Legacy or unavailable log directions identify the actual output work folder");
            Check(SkeletonFailureDetails.Directions("gorilla_render.py", explicitRequest).Contains(folder), "Explicit work directions point to that exact directory");
            Check(SkeletonFailureDetails.Find("roster_lookup.py", explicitRequest, started) == null, "Another engine command cannot claim a skeleton diagnostic");
            Check(SkeletonFailureDetails.Find(null, explicitRequest, started) == null && SkeletonFailureDetails.Find("gorilla_render.py", null, started) == null, "Missing commands and arguments remain harmless");

            WriteMarker();
            File.WriteAllText(log, new string('x', 100000) + "\nValueError: " + new string('a', 1000) + "\n");
            report = Find();
            Check(report.Summary.Length <= 240 && report.FullDetails.Length < 40000, "Large logs and summaries stay bounded");
            Check(!report.FullDetails.Contains(new string('x', 40000)), "A large old log prefix is not loaded into the failure report");
            File.Delete(marker);
            File.WriteAllText(log, "ValueError: no sidecar\n");
            Check(Find() == null, "A legacy default cache with no source proof receives directions instead of somebody else's details");
            File.Delete(log);
            string linkedLog = Path.Combine(other, "rest.log");
            File.CreateSymbolicLink(log, linkedLog);
            Check(SkeletonFailureDetails.Find("gorilla_render.py", explicitRequest, started) == null, "A log link cannot redirect diagnostics to another model");
            Check(SkeletonFailureDetails.Directions("other", request) == null, "Directions are restricted to the character renderer");
            Console.WriteLine("PASS " + checks + " skeleton failure detail checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Setup()
    {
        character = Path.Combine(root, "695_Brody_King"); output = Path.Combine(root, "renders");
        folder = Path.Combine(output, "work", "695_abcdef1234_own");
        Directory.CreateDirectory(Path.Combine(character, "Default_Attire"));
        Directory.CreateDirectory(Path.Combine(character, "BaseModel")); Directory.CreateDirectory(folder);
        attire = Path.Combine(character, "Default_Attire", "Attire.mcd"); body = Path.Combine(character, "BaseModel", "baseModel.mcd");
        File.WriteAllText(attire, "source attire"); File.WriteAllText(body, "source body");
        log = Path.Combine(folder, "rest.log"); marker = Path.Combine(folder, "model.geometry.json");
        request = new[] { character, "t62247", output, "attire=Default_Attire/Attire.mcd", "quick=1" };
        started = DateTime.UtcNow.AddSeconds(-2);
        WriteMarker(); File.WriteAllText(log, "Blender 5.2\nTraceback (most recent call last):\nValueError: bad marshal data\nBlender quit\n");
    }
    private static SkeletonFailureReport Find() { return SkeletonFailureDetails.Find("gorilla_render.py", request, started); }
    private static void WriteMarker(string selected = "Default_Attire/Attire.mcd")
    {
        File.WriteAllText(marker, JsonSerializer.Serialize(new { identity = new { schema = 1, revision = "1", character,
            attire = selected, sources = new[] { Stamp(attire), Stamp(body) } } }));
    }
    private static object Stamp(string path)
    {
        var file = new FileInfo(path);
        return new { path, size = file.Length, mtime_ns = (file.LastWriteTimeUtc.Ticks - 621355968000000000L) * 100 };
    }
    private static string Hash(string path) { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); checks++; Console.WriteLine("PASS " + message); }
}
