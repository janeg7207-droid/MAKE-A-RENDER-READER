using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary;
    private static int passed;

    private static int Main(string[] args)
    {
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-model-library-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Check("A nested PAC model is found without browsing its character folder", () => {
                string root = Root("pac"); string pac = Model(root, "AEW", "PAC", "Default_Attire");
                var match = ModelLibrary.Find(root, Wrestler(4000, "PAC", 215)).Single();
                Equal(Directory.GetParent(pac).FullName, match.Folder); Equal("PAC", match.Name); Equal(0, match.SourceId);
            });
            Check("A dated numeric mod pack never hides its independent wrestlers", () => {
                string root = Root("dated-pack"); Model(root, "2026_Updates", "PAC", "Default_Attire"); Model(root, "2026_Updates", "AJ_Styles", "Default_Attire");
                var candidates = ModelLibrary.Scan(root); Equal(2, candidates.Count); Equal(false, candidates.Any(candidate => candidate.SourceId == 2026));
                Equal("PAC", ModelLibrary.Match(candidates, Wrestler(4000, "PAC", 0)).Single().Name);
                Equal("AJ Styles", ModelLibrary.Match(candidates, Wrestler(4001, "AJ STYLES", 0)).Single().Name);
            });
            Check("A numeric package with one wrestler never lends its ID to that wrestler", () => {
                string root = Root("single-dated-pack"); Model(root, "2026_Updates", "PAC", "Default_Attire");
                var candidate = ModelLibrary.Scan(root).Single(); Equal("PAC", candidate.Name); Equal(0, candidate.SourceId);
            });
            Check("All of a character's attires and base model remain one choice", () => {
                string root = Root("attires"); Model(root, "PAC", "Default_Attire"); Model(root, "PAC", "Attire_2"); Model(root, "PAC", "BaseModel");
                var candidates = ModelLibrary.Scan(root); Equal(1, candidates.Count); Equal("PAC", candidates[0].Name);
            });
            Check("Choosing a character itself as the library also works", () => {
                string root = Root("single"); string model = Model(root, "PAC");
                var candidate = ModelLibrary.Scan(model).Single(); Equal(model, candidate.Folder); Equal("PAC", candidate.Name);
            });
            Check("Physical ID differs from the logical roster slot without losing the model", () => {
                string root = Root("source-id"); string model = Model(root, "123_Rey_Mysterio_2019", "Default_Attire");
                var candidate = ModelLibrary.Find(root, Wrestler(910, "REY MYSTERIO '19", 123)).Single();
                Equal(123, candidate.SourceId); Equal(Directory.GetParent(model).FullName, candidate.Folder);
            });
            Check("Logical IDs never override a different proven physical source ID", () => {
                string root = Root("wrong-logical"); Model(root, "910_Wrong_Model", "Default_Attire"); Model(root, "123_Right_Model", "Default_Attire");
                var candidate = ModelLibrary.Find(root, Wrestler(910, "Different localized name", 123)).Single(); Equal(123, candidate.SourceId);
            });
            Check("An unproven logical ID is not used to pick a model", () => {
                string root = Root("unproven"); Model(root, "4000_Unrelated", "Default_Attire");
                Equal(0, ModelLibrary.Find(root, Wrestler(4000, "PAC", 0)).Count);
            });
            Check("Exact custom names take priority over a stock body ID", () => {
                string root = Root("name-priority"); Model(root, "215_Neville", "Default_Attire"); Model(root, "PAC", "Default_Attire");
                var candidate = ModelLibrary.Find(root, Wrestler(4000, "PAC", 215)).Single(); Equal("PAC", candidate.Name); Equal(0, candidate.SourceId);
            });
            Check("Duplicate PAC folders stay ambiguous instead of taking the first", () => {
                string root = Root("duplicates"); Model(root, "Pack One", "PAC", "Default_Attire"); Model(root, "Pack Two", "PAC", "Default_Attire");
                var candidates = ModelLibrary.Find(root, Wrestler(4000, "PAC", 215)); Equal(2, candidates.Count); Equal(2, candidates.Select(candidate => candidate.Folder).Distinct().Count());
            });
            Check("Case, underscores, punctuation, spaces and apostrophes normalize exactly", () => {
                string root = Root("punctuation"); Model(root, "CM_Punk", "Default_Attire"); Model(root, "Tony_D’Angelo", "Default_Attire");
                var candidates = ModelLibrary.Scan(root);
                Equal("CM Punk", ModelLibrary.Match(candidates, Wrestler(1, "cm-punk", 0)).Single().Name);
                Equal("Tony D’Angelo", ModelLibrary.Match(candidates, Wrestler(2, "TONY D'ANGELO", 0)).Single().Name);
                Equal(0, ModelLibrary.Match(candidates, Wrestler(3, "Punk", 0)).Count);
            });
            Check("Numeric stage names retain their number without creating an ID", () => {
                string root = Root("stage-number"); Model(root, "2 Cold Scorpio", "Default_Attire");
                var candidate = ModelLibrary.Find(root, Wrestler(4001, "2 COLD SCORPIO", 0)).Single(); Equal("2 Cold Scorpio", candidate.Name); Equal(0, candidate.SourceId);
            });
            Check("A unique numbered attire child provides the physical source ID", () => {
                string root = Root("child-id"); Model(root, "PAC", "215_Default_Attire"); Model(root, "PAC", "215_Attire_2");
                var candidate = ModelLibrary.Scan(root).Single(); Equal("PAC", candidate.Name); Equal(215, candidate.SourceId);
            });
            Check("Nested package wrappers retain the loadable model folder and physical ID", () => {
                string root = Root("nested-package"); string attire = Model(root, "Library", "Mods", "PAC", "Character", "368_Default_Attire");
                var candidate = ModelLibrary.Scan(root).Single(); Equal(Directory.GetParent(attire).FullName, candidate.Folder); Equal(368, candidate.SourceId);
                Equal(candidate.Folder, ModelLibrary.Match(new[] { candidate }, Wrestler(9000, "Different localized name", 368)).Single().Folder);
                Equal(0, ModelLibrary.Match(new[] { candidate }, Wrestler(9000, "PAC", 0)).Count);
            });
            Check("Conflicting numbered attires are separate models and never an arbitrary parent", () => {
                string root = Root("conflicting-id"); Model(root, "Mixed Mod", "215_Default_Attire"); Model(root, "Mixed Mod", "368_Default_Attire");
                var candidates = ModelLibrary.Scan(root); Equal(2, candidates.Count); Equal(false, candidates.Any(candidate => Path.GetFileName(candidate.Folder) == "Mixed Mod"));
                Equal(215, ModelLibrary.Match(candidates, Wrestler(4000, "PAC", 215)).Single().SourceId);
            });
            Check("Placeholder character folders expose their explicit physical ID", () => {
                string root = Root("placeholder"); Model(root, "placeholder_368_A0", "Default_Attire");
                Equal(368, ModelLibrary.Find(root, Wrestler(9000, "Roman Reigns", 368)).Single().SourceId);
            });
            Check("A character attire suffix is removed from display-name matching", () => {
                string root = Root("attire-suffix"); Model(root, "PAC_A1", "Default_Attire");
                Equal("PAC", ModelLibrary.Find(root, Wrestler(4000, "PAC", 0)).Single().Name);
            });
            Check("Folders without an actual MCD are never considered loadable models", () => {
                string root = Root("missing-models"); Directory.CreateDirectory(Path.Combine(root, "PAC", "Default_Attire")); File.WriteAllText(Path.Combine(root, "PAC", "profile.json"), "{}");
                Equal(0, ModelLibrary.Scan(root).Count);
            });
            Check("Uppercase model extensions are recognized", () => {
                string root = Root("uppercase"); string pac = Directory.CreateDirectory(Path.Combine(root, "PAC")).FullName; File.WriteAllText(Path.Combine(pac, "ATTIRE.MCD"), "fixture");
                Equal(pac, ModelLibrary.Scan(root).Single().Folder);
            });
            Check("Hidden and cache folders cannot add duplicate models", () => {
                string root = Root("skip-helper"); Model(root, "PAC", "Default_Attire"); Model(root, ".git", "PAC", "Default_Attire"); Model(root, "_Global", "PAC", "Default_Attire"); Model(root, "Cache", "PAC", "Default_Attire");
                Equal(1, ModelLibrary.Scan(root).Count);
            });
            Check("A Discovery data parent never turns a prop or arena MCD into a wrestler", () => {
                string root = Root("resources"); Model(root, "Characters", "PAC", "Default_Attire"); Model(root, "Props", "9348_Tripwire"); Model(root, "Arenas", "Arena");
                var candidate = ModelLibrary.Scan(root).Single(); Equal("PAC", candidate.Name);
            });
            Check("A stray MCD in Characters never hides its real character folders", () => {
                string root = Root("container-model"); string characters = Directory.CreateDirectory(Path.Combine(root, "Characters")).FullName;
                File.WriteAllText(Path.Combine(characters, "stray.mcd"), "not a character folder"); Model(characters, "215_PAC", "Default_Attire");
                var candidate = ModelLibrary.Scan(characters).Single(); Equal("PAC", candidate.Name); Equal(215, candidate.SourceId);
            });
            Check("Directory links are skipped so a library loop cannot recurse", () => {
                string root = Root("links"); Model(root, "PAC", "Default_Attire"); Directory.CreateSymbolicLink(Path.Combine(root, "Loop"), root);
                Equal(1, ModelLibrary.Scan(root).Count);
            });
            Check("A cancelled scan stops before indexing", () => {
                string root = Root("cancelled"); Model(root, "PAC", "Default_Attire");
                using (var cancellation = new CancellationTokenSource()) { cancellation.Cancel(); Throws<OperationCanceledException>(() => ModelLibrary.Scan(root, cancellation.Token)); }
            });
            Check("Repeated candidate paths do not create artificial ambiguity", () => {
                var candidate = new ModelCandidate { Folder = Path.Combine(temporary, "PAC"), Name = "PAC" };
                Equal(1, ModelLibrary.Match(new[] { candidate, candidate }, Wrestler(4000, "PAC", 0)).Count);
            });
            Check("Missing library folders report a clear error", () => Throws<DirectoryNotFoundException>(() => ModelLibrary.Scan(Path.Combine(temporary, "not-present"))));
            Check("Excessive nesting is bounded instead of silently missing a model", () => {
                string root = Root("deep"), folder = root;
                for (int index = 0; index < 66; index++) folder = Directory.CreateDirectory(Path.Combine(folder, "d")).FullName;
                File.WriteAllText(Path.Combine(folder, "Attire.mcd"), "fixture");
                Throws<InvalidDataException>(() => ModelLibrary.Scan(root));
            });
            if (args.Length > 0) Check("The user's extracted Discovery data indexes all ten physical character models", () => {
                var candidates = ModelLibrary.Scan(args[0]); Equal(10, candidates.Count);
                var rey = ModelLibrary.Match(candidates, Wrestler(910, "REY MYSTERIO '19", 123)).Single(); Equal(123, rey.SourceId);
                var punk = ModelLibrary.Match(candidates, Wrestler(1481, "CM PUNK", 312)).Single(); Equal(312, punk.SourceId);
            });
            Console.WriteLine(passed + " model library checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static string Root(string name) { return Directory.CreateDirectory(Path.Combine(temporary, name)).FullName; }
    private static string Model(string root, params string[] folders)
    {
        string path = root; foreach (string folder in folders) path = Path.Combine(path, folder);
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "Attire.mcd"), "model fixture"); return path;
    }
    private static RosterEntry Wrestler(int id, string name, int sourceId) { return new RosterEntry { Id = id, Name = name, SourceId = sourceId }; }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { throw new Exception("FAIL " + name, error); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name + "."); }
}
