using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string temporary;
    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("Usage: MatchCardLayoutsTests original-app-root [export-fixtures-directory]"); return 2; }
        temporary = Path.Combine(Path.GetTempPath(), "gorilla-matchcard-layouts-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Path.GetFullPath(args[0]);
            string pack = Path.Combine(app, "data", "gamepack", "screens", "BakeMe");
            string data = Path.Combine(temporary, "discovery_data");
            string rawRelative = Path.Combine("UI", "Projects", "ShowAssets", "matchups", "matchup_0966_RAW_24.jsfb");
            string input = Path.Combine(pack, rawRelative), output = Path.Combine(data, rawRelative);
            Copy(input, output);
            foreach (string file in Directory.GetFiles(Path.Combine(pack, "UI", "Assets", "movies", "gorilla_renders"), "ph_*.bk2"))
                Copy(file, Path.Combine(data, "UI", "Assets", "movies", "gorilla_renders", Path.GetFileName(file)));
            byte[] before = File.ReadAllBytes(output);
            string[] initialFiles = Files(data);
            var check = MatchCardLayouts.Apply(app, data, true);
            Console.WriteLine(JsonSerializer.Serialize(check));
            Check(check.Failed.Count == 0 && check.Unsupported.Count == 0, "The real RAW 24 schema and all 49 original placeholder identities are recognized");
            Check(check.Patched == 1 && check.AddedSlots == 52 && check.AddedMovies == 52 && check.BakeRequired && check.IsCheck, "Read-only inspection finds 52 missing slots across the ten omitted match types");
            Check(File.ReadAllBytes(output).SequenceEqual(before) && Files(data).SequenceEqual(initialFiles), "Inspection leaves every input and directory unchanged");
            var repaired = MatchCardLayouts.Apply(app, data, false);
            Check(repaired.Patched == 1 && repaired.AddedSlots == 52 && repaired.AddedMovies == 52 && repaired.Failed.Count == 0 && repaired.Unsupported.Count == 0, "Setup completes a partially patched real file instead of stopping at its first existing GM_clip");
            Check(repaired.BakeRequired && Directory.Exists(repaired.BackupDirectory), "Actual changes require a bake and retain an original backup");
            Check(File.ReadAllBytes(Path.Combine(repaired.BackupDirectory, rawRelative + ".bak")).SequenceEqual(before), "The backup contains the exact original screen and cannot be mistaken for a new JSFB asset");
            Check(File.ReadAllBytes(input).SequenceEqual(before), "The application screen pack remains untouched");
            Check(File.ReadAllBytes(output).Length > before.Length, "Replacement nodes are appended to the original project");
            byte[] after = File.ReadAllBytes(output);
            var repeated = MatchCardLayouts.Apply(app, data, false);
            Check(repeated.Patched == 0 && repeated.AddedSlots == 0 && repeated.AddedMovies == 0 && repeated.Already == 1 && !repeated.BakeRequired, "A second setup is idempotent and does not request another bake");
            Check(File.ReadAllBytes(output).SequenceEqual(after), "Already complete layouts are byte-for-byte unchanged");
            Check(Directory.GetFiles(Path.Combine(data, "UI", "Assets", "movies", "gorilla_renders"), "ph_*.bk2").Length == 101, "All twenty layouts receive their 101 distinct player placeholders");
            Check(Directory.GetFiles(data, "*.tmp", SearchOption.AllDirectories).Length == 0, "No temporary file remains after setup");
            if (args.Length > 1)
            {
                string fixtures = Path.GetFullPath(args[1]); Directory.CreateDirectory(fixtures);
                File.WriteAllBytes(Path.Combine(fixtures, "RAW24.original.jsfb"), before);
                File.WriteAllBytes(Path.Combine(fixtures, "RAW24.completed.jsfb"), after);
                foreach (string file in Directory.GetFiles(Path.Combine(data, "UI", "Assets", "movies", "gorilla_renders"), "ph_*.bk2"))
                    Copy(file, Path.Combine(fixtures, "movies", Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(fixtures, "layout-result.json"), JsonSerializer.Serialize(repaired, new JsonSerializerOptions { WriteIndented = true }));
            }
            string malformed = Path.Combine(data, "UI", "Projects", "ShowAssets", "matchups", "matchup_broken.jsfb");
            File.WriteAllBytes(malformed, new byte[32]);
            Check(MatchCardLayouts.Apply(app, data, false).Failed.Any(error => error.Contains("matchup_broken.jsfb")), "An unsupported project is reported instead of replacing its bytes");
            Check(File.ReadAllBytes(malformed).SequenceEqual(new byte[32]), "Malformed mod screen input is left untouched");
            File.Delete(malformed);
            string customMovie = Path.Combine(data, "UI", "Assets", "movies", "gorilla_renders", "ph_L02_1P.bk2");
            File.WriteAllText(customMovie, "my existing custom placeholder");
            var custom = MatchCardLayouts.Apply(app, data, false);
            Check(custom.AddedMovies == 0 && File.ReadAllText(customMovie) == "my existing custom placeholder", "User-supplied existing movie files are preserved");
            Check(custom.Unsupported.Any(issue => issue.Contains("ph_L02_1P") && issue.Contains("unknown layout/player identity")), "A preserved custom movie with the wrong identity is clearly reported");
            Check(MatchCardLayouts.Apply(null, data, false).Failed.Count == 1, "A missing app folder returns a readable failure");
            Check(MatchCardLayouts.Apply(app, null, false).Failed.Count == 1, "A missing Discovery folder returns a readable failure");

            string allData = Path.Combine(temporary, "all-discovery_data");
            foreach (string file in Directory.GetFiles(pack, "*", SearchOption.AllDirectories)) Copy(file, Path.Combine(allData, file.Substring(pack.Length + 1)));
            var all = MatchCardLayouts.Apply(app, allData, false);
            Console.WriteLine("All shipped screens: " + JsonSerializer.Serialize(all));
            Check(all.Failed.Count == 0 && all.Unsupported.Count == 0, "All 86 actual shipped match-card projects match the append-copy schema");
            Check(all.Patched == 86 && all.AddedSlots > 4000 && all.AddedMovies == 52, "All stock and creation-arena match-card files receive their omitted layouts");
            var allAgain = MatchCardLayouts.Apply(app, allData, true);
            Check(allAgain.Patched == 0 && allAgain.AddedSlots == 0 && allAgain.AddedMovies == 0 && allAgain.Already == 86 && allAgain.Failed.Count == 0, "Every completed stock screen passes a repeated read-only check");
            if (args.Length > 1)
            {
                string fixtures = Path.GetFullPath(args[1]);
                foreach (string file in Directory.GetFiles(allData, "*.jsfb", SearchOption.AllDirectories).Where(file => !file.Contains("Gorilla Studio Layout Backups")))
                    Copy(file, Path.Combine(fixtures, "all", file.Substring(allData.Length + 1)));
                File.WriteAllText(Path.Combine(fixtures, "all-layout-results.json"), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine(passed + " match card layout checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private static string[] Files(string root) { return Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray(); }
    private static void Copy(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true); }
    private static void Check(bool value, string description) { if (!value) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }
}
