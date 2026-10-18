using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary;
    private static int passed;

    private static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: RosterLaunchTests <extracted Discovery data directory>"); return 2; }
        string discovery = Path.GetFullPath(args[0]);
        string table = Path.Combine(discovery, "Roster", "CharProfileTable.jsfb");
        string originalHash = Hash(table);
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-roster-launch-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Check("Direct roster launch reads all 1457 real Discovery profiles without a model-folder prompt", () => {
                RosterSessionData session = RosterLaunch.Open(discovery, null, null, null);
                Equal(1458, session.Entries.Count); Equal(table, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
                Equal(1457, session.Entries.Where(entry => entry.Id > 0).Select(entry => entry.Id).Distinct().Count());
                Equal(1, session.Entries.Count(entry => entry.Id == 0));
            });

            Check("Characters and Roster are accepted as remembered Discovery entry points", () => {
                foreach (string folder in new[] { "Characters", "Roster" })
                {
                    RosterSessionData session = RosterLaunch.Open(Path.Combine(discovery, folder), null, null, null);
                    Equal(1458, session.Entries.Count); Equal(table, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
                }
            });

            Check("An explicit current JSFB opens directly without browsing its Characters folder", () => {
                RosterSessionData session = RosterLaunch.Open(null, null, table, null);
                Equal(1458, session.Entries.Count); Equal(table, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
            });

            string model = Directory.CreateDirectory(Path.Combine(temporary, "Extracted Mods", "My Wrestler")).FullName;
            string roster = Write("my-roster.json", "[{\"id\":4000,\"name\":\"MY WRESTLER\"}]");
            var saved = new List<RosterEntry> { new RosterEntry { Id = 4000, Name = "MY WRESTLER", Folder = model,
                SourceId = 1506, Source = "Chosen model", IsMod = true, RosterTable = table } };
            Check("First-use JSON inherits the selected Discovery table without saved entries", () => {
                string imported = Write("first-use.json", "[{\"id\":1481,\"name\":\"CM PUNK\"}]");
                RosterSessionData session = RosterLaunch.Open(discovery, null, imported, null);
                Equal(1, session.Entries.Count); Equal(table, session.DiscoveryTable);
                Equal(table, session.Entries.Single().RosterTable); Equal(1481, session.Entries.Single().Id);
                Equal(0, session.Entries.Single().SourceId); Equal(true, session.ChooseCharacterFolder);
                RosterModelResolution resolved = RosterModelResolution.Resolve(session.Entries.Single(),
                    ModelLibrary.Scan(Path.Combine(discovery, "Characters")), session.DiscoveryTable);
                Equal(RosterModelState.Ready, resolved.State); Equal(312, resolved.SourceId);
                Equal(Path.Combine(discovery, "Characters", "312_CM_Punk"), resolved.Folder);
            });
            Check("An explicitly selected Discovery root wins over mixed saved roster contexts", () => {
                var mixed = new[] { saved[0], new RosterEntry { Id = 5000, Name = "ANOTHER ROSTER", RosterTable = Path.Combine(temporary, "other", "Roster", "CharProfileTable.jsfb") } };
                RosterSessionData session = RosterLaunch.Open(discovery, mixed, roster, null);
                Equal(table, session.DiscoveryTable); Equal(table, session.Entries.Single().RosterTable);
                Equal(model, session.Entries.Single().Folder); Equal(1506, session.Entries.Single().SourceId);
            });
            Check("An explicit JSON row keeps its embedded Discovery context", () => {
                string otherTable = Path.Combine(temporary, "embedded-context", "Roster", "CharProfileTable.jsfb");
                string imported = Write("embedded-context.json", JsonSerializer.Serialize(new[] { new RosterEntry { Id = 4000, Name = "MY WRESTLER", RosterTable = otherTable } }));
                RosterSessionData session = RosterLaunch.Open(discovery, saved, imported, null);
                Equal(otherTable, session.DiscoveryTable); Equal(otherTable, session.Entries.Single().RosterTable);
                Equal((string)null, session.Entries.Single().Folder);
            });
            Check("Remembered JSON overrides the larger Discovery roster and retains its known model", () => {
                RosterSessionData session = RosterLaunch.Open(discovery, saved, roster, null);
                Equal(1, session.Entries.Count); Equal("MY WRESTLER", session.Entries[0].Name);
                Equal(model, session.Entries[0].Folder); Equal(1506, session.Entries[0].SourceId);
                Equal(table, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
            });

            File.WriteAllText(roster, "[{\"id\":4000,\"name\":\"RENAMED WRESTLER\"},{\"id\":4001,\"name\":\"NEW WRESTLER\"}]");
            Check("Edited JSON reloads current names and newly added characters instead of cached membership", () => {
                RosterSessionData session = RosterLaunch.Open(discovery, saved, roster, null);
                Equal(2, session.Entries.Count); Equal("RENAMED WRESTLER", session.Entries.Single(entry => entry.Id == 4000).Name);
                Equal("NEW WRESTLER", session.Entries.Single(entry => entry.Id == 4001).Name);
                Equal((string)null, session.Entries.Single(entry => entry.Id == 4000).Folder);
                Equal(0, session.Entries.Single(entry => entry.Id == 4000).SourceId);
                Equal(table, session.DiscoveryTable);
            });

            Check("Renaming a JSON slot neither reuses its stale model nor mutates saved roster contexts", () => {
                string otherTable = Path.Combine(temporary, "Another Discovery", "Roster", "CharProfileTable.jsfb");
                var other = new RosterEntry { Id = 5000, Name = "OTHER ROSTER WRESTLER", RosterTable = otherTable, Folder = model };
                var contexts = saved.Concat(new[] { other }).ToList();
                string before = JsonSerializer.Serialize(contexts);
                RosterSessionData session = RosterLaunch.Open(discovery, contexts, roster, null);
                Equal(2, session.Entries.Count); Equal(false, session.Entries.Any(entry => entry.Id == other.Id));
                Equal((string)null, session.Entries.Single(entry => entry.Id == 4000).Folder);
                Equal(before, JsonSerializer.Serialize(contexts));
            });

            Check("Plain saved JSON model choices work without Discovery or any source-folder chooser", () => {
                var chosen = new RosterEntry { Id = 4002, Name = "PLAIN MODEL", Folder = model, SourceId = 1506,
                    Source = "Chosen model", IsMod = true };
                RosterSessionData session = RosterLaunch.Open(null, new[] { chosen }, null, null);
                Equal(true, session.ChooseCharacterFolder); Equal((string)null, session.DiscoveryTable);
                Equal(model, session.Entries.Single().Folder); Equal(1506, session.Entries.Single().SourceId);
                Equal(false, ReferenceEquals(chosen, session.Entries.Single()));
                session.Entries.Single().Name = "CANCELLED PICKER EDIT";
                Equal("PLAIN MODEL", chosen.Name);
            });

            Check("Current standalone JSON retains an exact saved name/ID model binding", () => {
                var chosen = new RosterEntry { Id = 4002, Name = "PLAIN MODEL", Folder = model, SourceId = 1506,
                    Source = "Chosen model", IsMod = true };
                string plain = Write("plain.json", "[{\"id\":4002,\"name\":\"PLAIN MODEL\"}]");
                RosterSessionData session = RosterLaunch.Open(null, new[] { chosen }, plain, null);
                Equal(model, session.Entries.Single().Folder); Equal(1506, session.Entries.Single().SourceId);
                Equal("Chosen model", session.Entries.Single().Source); Equal(true, session.Entries.Single().IsMod);
            });

            Check("A renamed standalone JSON slot cannot inherit its old wrestler model", () => {
                var chosen = new RosterEntry { Id = 4002, Name = "PLAIN MODEL", Folder = model, SourceId = 1506, Source = "Chosen model" };
                string plain = Write("renamed-plain.json", "[{\"id\":4002,\"name\":\"DIFFERENT WRESTLER\"}]");
                RosterEntry entry = RosterLaunch.Open(null, new[] { chosen }, plain, null).Entries.Single();
                Equal("DIFFERENT WRESTLER", entry.Name); Equal((string)null, entry.Folder); Equal(0, entry.SourceId);
            });

            Check("Multiple saved model choices are preserved without selecting one arbitrarily", () => {
                string alternate = Directory.CreateDirectory(Path.Combine(temporary, "Alternate Attire")).FullName;
                var choices = new[] {
                    new RosterEntry { Id = 4002, Name = "PLAIN MODEL", Folder = model, SourceId = 1506 },
                    new RosterEntry { Id = 4002, Name = "PLAIN MODEL", Folder = alternate, SourceId = 1506 }
                };
                Equal(2, RosterLaunch.Open(null, choices, null, null).Entries.Count);
                string plain = Write("ambiguous-plain.json", "[{\"id\":4002,\"name\":\"PLAIN MODEL\"}]");
                Equal((string)null, RosterLaunch.Open(null, choices, plain, null).Entries.Single().Folder);
            });

            Check("Missing remembered JSON falls back to current Discovery data", () => {
                RosterSessionData session = RosterLaunch.Open(discovery, saved, Path.Combine(temporary, "missing.json"), null);
                Equal(1459, session.Entries.Count); Equal(true, session.ChooseCharacterFolder); Equal(table, session.DiscoveryTable);
                Equal(model, session.Entries.Single(entry => entry.Id == 4000).Folder);
            });

            Check("Missing Discovery and JSON paths fall back to valid saved entries", () => {
                RosterSessionData session = RosterLaunch.Open(Path.Combine(temporary, "missing-discovery"), saved,
                    Path.Combine(temporary, "missing.json"), null);
                Equal(1, session.Entries.Count); Equal(model, session.Entries.Single().Folder); Equal(true, session.ChooseCharacterFolder);
            });

            string bundled = Write("bundled-roster.json", "[{\"id\":148,\"name\":\"ADAM COLE\"},{\"id\":65535,\"name\":\"BOUNDARY\"}]");
            Check("Bundled portable JSON opens named roster entries without inventing model IDs", () => {
                RosterSessionData session = RosterLaunch.Open(null, null, null, bundled);
                Equal(2, session.Entries.Count); Equal("ADAM COLE", session.Entries[0].Name); Equal(148, session.Entries[0].Id);
                Equal(0, session.Entries[0].SourceId); Equal((string)null, session.Entries[0].Folder);
                Equal((string)null, session.DiscoveryTable); Equal(true, session.ChooseCharacterFolder);
            });

            Check("An empty installation can open the roster import screen without a model-folder prompt", () => {
                RosterSessionData session = RosterLaunch.Open(null, null, null, Path.Combine(temporary, "missing-bundle.json"));
                Equal(0, session.Entries.Count); Equal(true, session.ChooseCharacterFolder); Equal((string)null, session.DiscoveryTable);
            });

            Check("Invalid saved entries do not suppress the valid bundled roster", () => {
                var invalid = new RosterEntry[] { null, new RosterEntry { Id = 0, Name = "ZERO" }, new RosterEntry { Id = 65536, Name = "TOO HIGH" },
                    new RosterEntry { Id = 148, Name = "  " } };
                Equal(2, RosterLaunch.Open(null, invalid, null, bundled).Entries.Count);
            });

            Check("Existing malformed preferred JSON is an actionable error instead of an obsolete fallback", () => {
                string bad = Write("broken.json", "{broken");
                Reject(() => RosterLaunch.Open(discovery, saved, bad, bundled), "Could not open the roster", bad);
            });

            Check("Existing empty preferred JSON is an error rather than a bundled roster fallback", () => {
                string empty = Write("empty.json", "[]");
                Reject(() => RosterLaunch.Open(discovery, saved, empty, bundled), empty, "No roster entries");
            });

            Check("Unsupported preferred files keep the JSON/JSFB-only restriction", () => {
                string csv = Write("roster.csv", "id,name\n148,ADAM COLE");
                Reject(() => RosterLaunch.Open(discovery, saved, csv, bundled), csv, "JSON roster");
            });

            Check("Damaged preferred JSFB cannot silently fall back to another roster", () => {
                string root = Directory.CreateDirectory(Path.Combine(temporary, "bad-data")).FullName;
                Directory.CreateDirectory(Path.Combine(root, "Characters"));
                string bad = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Roster")).FullName, "CharProfileTable.jsfb");
                File.WriteAllText(bad, "damaged table data");
                Reject(() => RosterLaunch.Open(discovery, saved, bad, bundled), bad, "not a valid CharProfileTable");
            });

            Check("A damaged Discovery table is surfaced even if a saved roster exists", () => {
                string root = Directory.CreateDirectory(Path.Combine(temporary, "bad-discovery")).FullName;
                Directory.CreateDirectory(Path.Combine(root, "Characters"));
                string bad = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Roster")).FullName, "CharProfileTable.jsfb");
                File.WriteAllText(bad, "damaged table data");
                Reject(() => RosterLaunch.Open(root, saved, null, bundled), "not a valid CharProfileTable");
            });

            Check("Common roster context accepts equivalent full paths and rejects mixed contexts", () => {
                Equal(table, RosterLaunch.CommonRosterTable(new[] { saved[0], new RosterEntry { RosterTable = Path.Combine(discovery, "Roster", ".", "CharProfileTable.jsfb") } }));
                Equal((string)null, RosterLaunch.CommonRosterTable(new[] { saved[0], new RosterEntry { RosterTable = Path.Combine(temporary, "another.jsfb") } }));
                Equal((string)null, RosterLaunch.CommonRosterTable(null));
            });

            Equal(originalHash, Hash(table));
            Console.WriteLine("Original uploaded CharProfileTable.jsfb SHA-256 unchanged.");
            Console.WriteLine(passed + " roster launch checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static string Write(string name, string contents) { string path = Path.Combine(temporary, name); File.WriteAllText(path, contents); return path; }
    private static string Hash(string path) { using (var input = File.OpenRead(path)) return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name, error); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
    private static void Reject(Action action, params string[] fragments)
    {
        try { action(); } catch (InvalidDataException error)
        {
            foreach (string fragment in fragments) if (error.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("Error did not include '" + fragment + "': " + error.Message);
            return;
        }
        throw new Exception("Expected an actionable InvalidDataException.");
    }
}
