using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root, app, game, data, render, config;
    private static int passed;

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-matchcard-readiness-" + Guid.NewGuid().ToString("N"));
        try
        {
            app = Path.Combine(root, "app"); game = Path.Combine(root, "game"); data = Path.Combine(root, "discovery_data");
            config = Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.cfg");
            Write(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp"), "original-plugin-fixture");
            Write(config, "# Keep this option\nenabled 1\nverbose 0\n");
            Write(Path.Combine(app, "data", "gamepack", "screens", "BakeMe", "UI", "Projects", "matchup.jsfb"), "original-layout");
            Write(Path.Combine(app, "data", "gamepack", "screens", "BakeMe", "UI", "Assets", "placeholder.bk2"), "original-movie");
            Write(Path.Combine(data, "UI", "Projects", "matchup.jsfb"), "custom-arena-layout-with-different-bytes");
            Write(Path.Combine(data, "UI", "Assets", "placeholder.bk2"), "custom-placeholder");
            render = Render("924_PAC.gmr", Metadata()); Install();
            var ready = Evaluate();
            Check(ready.IsConfigured && ready.Issues.Count == 0, "Logical PAC slot, current installed render, enabled plugin and existing screens configure successfully");
            Check(ready.WrestlerId == 924 && ready.Screens == "both" && ready.Ending == "loop", "The report reads the real metadata, not the physical model folder number");
            Check(ready.ExpectedScreenFiles == 2 && ready.MissingScreenFiles == 0, "Existing modded screen files need not equal shipped stock bytes");
            Check(ready.Notes.Any(note => note.Contains("does not confirm a bake")), "Present files never claim Discovery has baked them");
            Check(ready.Summary.Contains("In-game playback still needs checking"), "Configured does not claim observed in-game playback");
            Check(ready.Notes.Any(note => note.EndsWith("WWE2K26_MovingRenders.log")), "The plugin log path is available for a failed runtime test");

            var physical = MatchCardReadiness.Evaluate(app, game, data, render, 1173);
            Check(!physical.IsConfigured && physical.Issues.Any(issue => issue.Contains("924") && issue.Contains("1173")), "A physical 1173 selection cannot silently validate logical PAC 924");
            ChangeRender(Metadata().Replace("\"both\"", "\"select\""));
            Check(Issue("Match card is turned off"), "Roster-only renders explain that Match card must be selected and remade");
            ChangeRender(Metadata().Replace("\"both\"", "\"matchcard\""));
            Check(Evaluate().IsConfigured, "A match-card-only render is valid for match cards");
            ChangeRender(Metadata().Replace("\"end\":\"loop\"", "\"end\":\"freeze\""));
            Check(Evaluate().IsConfigured && Evaluate().Notes.Any(note => note.Contains("moves once, then holds")), "Play once and freeze is valid but explicitly distinguished from looping");
            ChangeRender(Metadata().Replace("\"loop\"", "\"still\""));
            Check(Issue("still render"), "Still output does not pass a moving-render readiness check");
            ChangeRender(Metadata().Replace(",\"screens\":\"both\"", "").Replace(",\"end\":\"loop\"", ""));
            Check(Evaluate().IsConfigured && Evaluate().Screens == "both" && Evaluate().Ending == "loop", "Older native metadata defaults to both screens and looping");
            ChangeRender(Metadata().Replace("\"both\"", "\"unknown\""));
            Check(Issue("unknown screen"), "Unknown screen settings are not presented as ready");
            ChangeRender(Metadata().Replace("\"loop\"", "\"unknown\""));
            Check(Issue("unknown ending"), "Unknown ending settings are not presented as ready");
            foreach (string id in new[] { "0", "65536", "924.5", "\"924\"", "true", "null", "1e300" })
            {
                ChangeRender(Metadata().Replace("\"wrestler_id\":924", "\"wrestler_id\":" + id));
                Check(!Evaluate().IsConfigured, "Nonnumeric, out-of-range or fractional target ID is rejected: " + id);
            }
            foreach (string json in new[] { "[]", "null", "{broken", "{\"format\":\"Other\",\"version\":1}", "{\"format\":\"GorillaRender\",\"version\":2}" })
            {
                ChangeRender(json); Check(Issue("not a complete Gorilla Render") || Issue("could not be checked"), "Malformed or unsupported metadata is a readable failure: " + json);
            }
            ChangeRender(Metadata());
            byte[] valid = File.ReadAllBytes(render);
            Mutate(valid, bytes => bytes[bytes.Length - 1] = (byte)'2', "Invalid footer magic is rejected");
            Mutate(valid, bytes => Put(bytes, bytes.Length - 8, uint.MaxValue), "Oversized metadata footer is rejected without allocation");
            Mutate(valid, bytes => Put(bytes, 4, 1), "Video size must agree with its metadata boundary");
            Mutate(valid, bytes => Put(bytes, 8, 0), "A zero-frame video is not configured");
            Mutate(valid, bytes => Put(bytes, 16, 999), "Repeated frame counts must agree");
            Mutate(valid, bytes => Put(bytes, 20, 256), "A low-resolution Preview cannot masquerade as a full game video");
            Mutate(valid, bytes => Put(bytes, 32, 0), "A zero FPS denominator is rejected");
            Mutate(valid, bytes => Put(bytes, 36, 0), "The video requires an alpha channel for the original plugin");
            Mutate(valid, bytes => Put(bytes, 40, 1), "Unsupported audio streams cannot pass readiness");
            Mutate(valid, bytes => Put(bytes, 44, 56), "Every video frame must be a keyframe");
            Mutate(valid, bytes => Put(bytes, 44, 1), "A video frame may not overlap the header");
            Mutate(valid, bytes => Put(bytes, 52, 1000), "The final index must point at the actual end of video");
            File.WriteAllBytes(render, new byte[] { 1, 2, 3 }); Install();
            Check(Issue("not a complete Gorilla Render"), "Truncated input is handled without crashing");
            ChangeRender(Metadata());

            string installed = Evaluate().InstalledPath;
            File.Delete(installed); Check(Issue("not installed"), "A made but uninstalled render is detected");
            Install(); File.AppendAllText(installed, "changed");
            Check(Issue("installed copy differs"), "A stale installed copy is detected");
            Install(); byte[] altered = File.ReadAllBytes(installed); altered[60] ^= 1; File.WriteAllBytes(installed, altered);
            Check(Issue("installed copy differs"), "Equal lengths alone cannot validate the installed render");
            Install();
            Write(config, "enabled 0\nverbose 1\n");
            Check(Issue("disabled"), "Compatible installed plugin with enabled 0 is reported instead of silently ignored");
            Write(config, " \tEnAbLeD\t1   # local comment\r\n; enabled 0\r\n//enabled 0\r\nverbose 1\r\n");
            Check(Evaluate().IsConfigured, "Config parsing handles whitespace, case and commented-out settings");
            Write(config, "enabled 1\nenabled 0\n"); Check(Issue("disabled"), "The final active enabled setting is honored");
            Write(config, "enabled 0\nenabled 1\n"); Check(Evaluate().IsConfigured, "A later active enable overrides an earlier disabled line");
            Write(config, "# enabled 1\nverbose 1\n"); Check(Issue("enabled state is unknown"), "Commented settings do not imply the plugin is enabled");
            Write(config, "enabled yes\n"); Check(Issue("enabled state is unknown"), "An unknown enabled value requires attention");
            Write(config, new string('x', 1024 * 1024 + 1));
            Check(Issue("configuration is too large"), "An oversized plugin config produces a readable diagnostic instead of escaping the check");
            File.Delete(config); Check(Issue("configuration is missing"), "A missing config leaves the enabled state unknown");
            Write(config, "enabled 1\n");
            string plugin = Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp");
            File.Delete(plugin); Check(Issue("plugin is missing"), "A copied video without the plugin cannot be configured");
            Write(plugin, "plugin-fixture");
            File.Delete(Path.Combine(data, "UI", "Assets", "placeholder.bk2"));
            Check(Evaluate().MissingScreenFiles == 1 && Issue("Run Game setup, then bake once"), "Missing placeholder movies produce setup and bake instructions");
            Write(Path.Combine(data, "UI", "Projects", "matchup.jsfb"), "");
            Check(Evaluate().MissingScreenFiles == 2, "Empty layouts do not count as installed screen files");
            Write(Path.Combine(data, "UI", "Projects", "matchup.jsfb"), "custom-layout");
            Write(Path.Combine(data, "UI", "Assets", "placeholder.bk2"), "movie");
            Check(!MatchCardReadiness.Evaluate(null, game, data, render, 924).IsConfigured, "An unknown supplied screen pack cannot prove readiness");
            Check(!MatchCardReadiness.Evaluate(app, null, data, render, 924).IsConfigured, "An unknown game folder cannot prove installation");
            Check(!MatchCardReadiness.Evaluate(app, game, null, render, 924).IsConfigured, "An unknown Discovery root cannot prove screen setup");
            Check(!MatchCardReadiness.Evaluate(app, game, data, null, 924).IsConfigured, "A Preview-only session cannot prove a full render installation");

            SetupResults();
            EnablePlugin();

            if (args.Length >= 2)
            {
                var actual = MatchCardReadiness.Evaluate(args[0], game, args[1], render, 924);
                Check(actual.ExpectedScreenFiles == 188, "The actual uploaded app contains 188 required screens and placeholder movies");
                Check(actual.MissingScreenFiles == 188, "The actual uploaded Discovery copy needs the entire screen pack");
                Console.WriteLine("Actual uploaded data: " + actual.Summary);
            }
            Console.WriteLine(passed + " match card readiness checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void SetupResults()
    {
        var result = MatchCardReadiness.FromSetupResult(new[] { "SETUP 188 screen files added", "RESULT {\"added\":188,\"patched\":0,\"failed\":[],\"bake\":true}" });
        Check(result.HasResult && result.Success && result.Added == 188 && result.BakeRequired, "Native setup reports changes and an explicit bake requirement");
        Check(result.Summary.Contains("BAKE ONCE in Discovery"), "Successful setup with changes clearly requests the required bake");
        result = MatchCardReadiness.FromSetupResult(new[] { "RESULT {\"added\":0,\"patched\":2,\"failed\":[\"mod match card (parse error)\"],\"bake\":false}" });
        Check(result.HasResult && !result.Success && !result.BakeRequired && result.Patched == 2 && result.Failed.Count == 1, "Failed file entries remain errors even when the native process exited successfully");
        Check(result.Summary.Contains("mod match card (parse error)"), "Setup errors identify the unprepared arena screen");
        result = MatchCardReadiness.FromSetupResult(new[] { "RESULT {\"added\":1,\"patched\":0,\"failed\":[\"locked file\"],\"bake\":true}" });
        Check(!result.Success && result.BakeRequired && result.Summary.Contains("after the errors are fixed"), "Mixed successful and failed setup changes preserve both failure and bake requirements");
        result = MatchCardReadiness.FromSetupResult(new[] { "RESULT {\"added\":188,\"patched\":0,\"failed\":[],\"bake\":true,\"check\":true}" });
        Check(result.Success && result.IsCheck && result.Summary.Contains("Run Game setup"), "Read-only checks do not claim their projected additions were written");
        result = MatchCardReadiness.FromSetupResult(new[] { "RESULT {\"added\":0,\"patched\":0,\"failed\":[],\"bake\":false}" });
        Check(result.Success && !result.BakeRequired && result.Summary.Contains("does not verify the previous bake"), "No current changes do not prove an earlier bake happened");
        foreach (string json in new[] { "{broken", "[]", "null", "{}", "{\"added\":0,\"patched\":0,\"failed\":[],\"bake\":\"true\"}",
            "{\"added\":-1,\"patched\":0,\"failed\":[],\"bake\":false}", "{\"added\":0,\"patched\":0,\"failed\":[1],\"bake\":false}",
            "{\"added\":0,\"patched\":0,\"failed\":[],\"bake\":false,\"check\":\"true\"}", "{\"added\":1e300,\"patched\":0,\"failed\":[],\"bake\":false}" })
        {
            var invalid = MatchCardReadiness.FromSetupResult(new[] { "RESULT " + json });
            Check(!invalid.HasResult && !invalid.Success && !invalid.BakeRequired, "Malformed setup result cannot claim setup or a bake requirement: " + json);
        }
        Check(!MatchCardReadiness.FromSetupResult(null).HasResult, "A failed setup with no output is readable");
        Check(!MatchCardReadiness.FromSetupResult(new[] { "RESULT {\"added\":0,\"patched\":0,\"failed\":[],\"bake\":false}", "RESULT {broken" }).Success, "A later malformed result cannot silently reuse an earlier success");
    }

    private static void EnablePlugin()
    {
        string original = "# Moving Renders – 雪\r\n  enabled\t0 # keep this comment\r\nverbose 1\r\n; enabled 0\r\n\tenabled 0\t// second active line\r\nmu_scale_test 42\r\n";
        byte[] before = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(original)).ToArray();
        File.WriteAllBytes(config, before);
        Check(MatchCardReadiness.EnablePlugin(game), "Explicit enable action updates an existing configuration");
        byte[] after = File.ReadAllBytes(config);
        string expected = original.Replace("enabled\t0", "enabled\t1").Replace("\tenabled 0", "\tenabled 1");
        Check(after.SequenceEqual(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(expected))), "UTF8 BOM, Unicode, comments, CRLF, whitespace and unrelated values survive enabling");
        Check(File.ReadAllBytes(config + ".before-enable.bak").SequenceEqual(before), "The first original configuration is backed up before replacement");
        Check(Evaluate().IsConfigured, "Every active disabled line is enabled, including duplicates");
        Check(MatchCardReadiness.EnablePlugin(game) && File.ReadAllBytes(config).SequenceEqual(after), "Enabling an already enabled config is unchanged");
        Write(config, "enabled 0\nverbose 0\n");
        Check(MatchCardReadiness.EnablePlugin(game) && File.ReadAllText(config) == "enabled 1\nverbose 0\n", "Configs without a BOM preserve their UTF8 format and LF endings");
        Check(File.ReadAllBytes(config + ".before-enable.bak").SequenceEqual(before), "Later enable actions retain the original one-time backup");
        Write(config, "# enabled 0\nverbose 1\n");
        Check(!MatchCardReadiness.EnablePlugin(game) && File.ReadAllText(config) == "# enabled 0\nverbose 1\n", "An absent active enabled key does not invent a configuration setting");
        File.WriteAllBytes(config, new byte[] { 0xff, 0xfe, 0xff });
        Check(!MatchCardReadiness.EnablePlugin(game) && File.ReadAllBytes(config).SequenceEqual(new byte[] { 0xff, 0xfe, 0xff }), "An unreadable encoding is left untouched");
        File.Delete(config);
        Check(!MatchCardReadiness.EnablePlugin(game) && !File.Exists(config), "A missing config is not created with guessed defaults");
        Check(!MatchCardReadiness.EnablePlugin(null), "A missing game folder is a safe enable failure");
        Check(Directory.GetFiles(Path.GetDirectoryName(config), "*.tmp").Length == 0, "Enable actions leave no temporary files");
        Write(config, "enabled 1\n");
    }

    private static string Metadata() { return "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":924,\"name\":\"PAC\",\"screens\":\"both\",\"end\":\"loop\"}"; }
    private static MatchCardReadinessReport Evaluate() { return MatchCardReadiness.Evaluate(app, game, data, render, 924); }
    private static bool Issue(string value) { return Evaluate().Issues.Any(issue => issue.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0); }
    private static void Install() { string path = Path.Combine(game, "Plugins", "GorillaRenders", Path.GetFileName(render)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(render, path, true); }
    private static void ChangeRender(string json) { Render(Path.GetFileName(render), json); Install(); }
    private static void Mutate(byte[] original, Action<byte[]> mutation, string reason) { byte[] bytes = (byte[])original.Clone(); mutation(bytes); File.WriteAllBytes(render, bytes); Install(); Check(!Evaluate().IsConfigured, reason); }
    private static void Put(byte[] bytes, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, new UTF8Encoding(false)); }
    private static string Render(string name, string json)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(root);
        byte[] metadata = Encoding.UTF8.GetBytes(json);
        using (var output = File.Create(path)) using (var writer = new BinaryWriter(output))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); // Header fixture only; not a real decoded movie.
            writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }
}
