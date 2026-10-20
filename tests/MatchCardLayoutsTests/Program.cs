using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

            string renamedData = Path.Combine(temporary, "renamed-data");
            string renamed = Path.Combine(renamedData, "UI", "Projects", "Modded", "AEW", "arena_card_7842.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(renamed)); File.WriteAllBytes(renamed, before);
            string noise = Path.Combine(renamedData, "UI", "Projects", "Modded", "plain_data.jsfb");
            File.WriteAllText(noise, "Unrelated non-screen data");
            byte[] unknownScreen = new byte[32]; Encoding.ASCII.GetBytes("Scre").CopyTo(unknownScreen, 4);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(noise), "unrelated_unknown_schema.jsfb"), unknownScreen);
            var renamedCheck = MatchCardLayouts.Apply(app, renamedData, true);
            Check(renamedCheck.Patched == 1 && renamedCheck.AddedSlots == 52 && renamedCheck.Failed.Count == 0 && renamedCheck.Unsupported.Count == 0,
                "An arbitrarily named mod screen under a new UI Projects path is found by Scre content and match-up structure");
            Check(File.ReadAllBytes(renamed).SequenceEqual(before) && !Directory.Exists(Path.Combine(renamedData, "UI", "Assets")),
                "Read-only discovery of renamed screens creates no files or movie directories");
            var renamedRepair = MatchCardLayouts.Apply(app, renamedData, false);
            Check(renamedRepair.Patched == 1 && renamedRepair.AddedSlots == 52 && renamedRepair.Failed.Count == 0,
                "A renamed mod layout receives the same supported repairs as the shipped RAW screen");
            string later = Path.Combine(renamedData, "UI", "Projects", "New collection", "New show", "98765.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(later)); File.WriteAllBytes(later, before);
            var addedLater = MatchCardLayouts.Apply(app, renamedData, false);
            Check(addedLater.Patched == 1 && addedLater.Already == 1 && addedLater.AddedSlots == 52 && addedLater.Failed.Count == 0,
                "New mod folders and screen records are discovered on the next scan without a hardcoded arena list");

            string nestedData = Path.Combine(temporary, "nested-data");
            string nestedFile = Path.Combine(nestedData, "UI", "Projects", "Custom", "nested.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedFile)); byte[] nestedBefore = Wrap(before, 1); File.WriteAllBytes(nestedFile, nestedBefore);
            var nested = MatchCardLayouts.Apply(app, nestedData, false);
            Check(nested.Patched == 1 && nested.AddedSlots == 52 && nested.Failed.Count == 0 && nested.Unsupported.Count == 0,
                "A verified match-up ancestor remains discoverable below an extra outer container");
            string repeatedData = Path.Combine(temporary, "repeated-containers");
            string repeatedFile = Path.Combine(repeatedData, "UI", "Projects", "Custom", "two_containers.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(repeatedFile)); File.WriteAllBytes(repeatedFile, Wrap(before, 2));
            var twoContainers = MatchCardLayouts.Apply(app, repeatedData, false);
            Check(twoContainers.Patched == 1 && twoContainers.AddedSlots == 104 && twoContainers.Unsupported.Count == 0 && twoContainers.Failed.Count == 0,
                "Two nested match-up containers validate player sets independently instead of colliding by path or layout name");

            string customData = Path.Combine(temporary, "custom-root");
            string customFile = Path.Combine(customData, "UI", "Projects", "Custom", "aew_screen.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(customFile)); byte[] customBefore = RenameMatchupRoot(before, "aewroot"); File.WriteAllBytes(customFile, customBefore);
            var unrelated = MatchCardLayouts.Apply(app, customData, false);
            Check(unrelated.Patched == 0 && unrelated.Already == 0 && unrelated.AddedMovies == 0 && unrelated.Failed.Count == 0 && unrelated.Unsupported.Count == 0
                && File.ReadAllBytes(customFile).SequenceEqual(customBefore) && !Directory.Exists(Path.Combine(customData, "UI", "Assets")),
                "Similar layout names in an unrelated custom-root menu do not authorize default repairs");
            var explicitCheck = MatchCardLayouts.Apply(app, customData, true, new[] { "UI/Projects/Custom/aew_screen.jsfb" });
            Check(explicitCheck.Patched == 1 && explicitCheck.AddedSlots == 52 && explicitCheck.Failed.Count == 0 && explicitCheck.Unsupported.Count == 0
                && File.ReadAllBytes(customFile).SequenceEqual(customBefore),
                "An exact catalog reference authorizes a custom root while check mode still preserves every byte");
            var explicitRepair = MatchCardLayouts.Apply(app, customData, false, new[] { customFile });
            Check(explicitRepair.Patched == 1 && explicitRepair.AddedSlots == 52 && explicitRepair.Failed.Count == 0 && explicitRepair.Unsupported.Count == 0,
                "Catalog-authorized custom-root layouts use the supported slot recipe and original backup flow");
            Check(File.ReadAllBytes(Path.Combine(explicitRepair.BackupDirectory, "UI", "Projects", "Custom", "aew_screen.jsfb.bak")).SequenceEqual(customBefore),
                "A catalog-authorized custom screen retains its exact original backup");

            string candidateData = Path.Combine(temporary, "candidate-custom-folder");
            string candidateFile = Path.Combine(candidateData, "CustomScreens", "season_card.jsfb");
            string candidateMenu = Path.Combine(candidateData, "CustomScreens", "unrelated_menu.jsfb");
            string candidateNoise = Path.Combine(candidateData, "CustomScreens", "not_a_screen.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(candidateFile));
            File.WriteAllBytes(candidateFile, before); File.WriteAllBytes(candidateMenu, customBefore); File.WriteAllText(candidateNoise, "Unrelated data");
            string[] candidatePaths = { candidateFile, candidateMenu, candidateNoise };
            string[] candidateInitialFiles = Files(candidateData);
            var candidateCheck = MatchCardLayouts.Apply(app, candidateData, true, null, candidatePaths);
            Check(candidateCheck.Patched == 1 && candidateCheck.AddedSlots == 52 && candidateCheck.Failed.Count == 0 && candidateCheck.Unsupported.Count == 0,
                "A non-authoritative catalog candidate outside UI Projects is checked by its proven matchup hierarchy");
            Check(File.ReadAllBytes(candidateFile).SequenceEqual(before) && Files(candidateData).SequenceEqual(candidateInitialFiles),
                "Checking custom-folder candidates leaves all screen bytes and directories unchanged");
            var candidateRepair = MatchCardLayouts.Apply(app, candidateData, false, null, candidatePaths);
            Check(candidateRepair.Patched == 1 && candidateRepair.AddedSlots == 52 && candidateRepair.Failed.Count == 0 && candidateRepair.Unsupported.Count == 0 && candidateRepair.BakeRequired,
                "A custom-folder candidate receives the supported missing player slots without explicit reference authority");
            Check(File.ReadAllBytes(Path.Combine(candidateRepair.BackupDirectory, "CustomScreens", "season_card.jsfb.bak")).SequenceEqual(before),
                "A repaired candidate retains its exact original bytes in the normal layout backup");
            Check(File.ReadAllBytes(candidateMenu).SequenceEqual(customBefore) && File.ReadAllText(candidateNoise) == "Unrelated data",
                "Catalog candidacy grants no custom outer-root authority to unrelated menus or non-screen files");
            byte[] candidateAfter = File.ReadAllBytes(candidateFile);
            var candidateAgain = MatchCardLayouts.Apply(app, candidateData, false, null, candidatePaths);
            Check(candidateAgain.Patched == 0 && candidateAgain.Already == 1 && candidateAgain.AddedSlots == 0 && candidateAgain.AddedMovies == 0
                && !candidateAgain.BakeRequired && File.ReadAllBytes(candidateFile).SequenceEqual(candidateAfter),
                "Repeating candidate setup is idempotent and requires no additional bake");
            string excludedData = Path.Combine(temporary, "excluded-exports");
            string excludedFile = Path.Combine(excludedData, "UI", "Projects", "Exports", "Pack", "UI", "Projects", "original_card.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(excludedFile)); File.WriteAllBytes(excludedFile, before);
            var excludedCheck = MatchCardLayouts.Apply(app, excludedData, false, null, new[] { excludedFile }, new[] { excludedFile });
            Check(excludedCheck.Patched == 0 && excludedCheck.Already == 0 && excludedCheck.AddedMovies == 0 && excludedCheck.Failed.Count == 0
                && excludedCheck.Unsupported.Count == 0 && File.ReadAllBytes(excludedFile).SequenceEqual(before)
                && !Directory.Exists(Path.Combine(excludedData, "UI", "Assets")),
                "A verified exported source is excluded from both default UI scanning and catalog candidates without gaining repair authority");
            string outsideCandidate = Path.Combine(temporary, "outside-candidate.jsfb"); File.WriteAllBytes(outsideCandidate, before);
            string linkedCandidate = Path.Combine(candidateData, "CustomScreens", "linked_candidate.jsfb"); File.CreateSymbolicLink(linkedCandidate, outsideCandidate);
            var unsafeCandidates = MatchCardLayouts.Apply(app, candidateData, false, null,
                new[] { "", outsideCandidate, linkedCandidate, Path.Combine(candidateData, "CustomScreens", "missing.jsfb"), Path.GetDirectoryName(candidateFile) });
            Check(unsafeCandidates.Failed.Count == 5 && unsafeCandidates.Patched == 0 && unsafeCandidates.AddedMovies == 0
                && File.ReadAllBytes(outsideCandidate).SequenceEqual(before) && File.ReadAllBytes(candidateFile).SequenceEqual(candidateAfter),
                "Empty, outside, linked, missing and directory candidate paths are rejected without changing files");
            var unsafeExclusions = MatchCardLayouts.Apply(app, candidateData, true, null, null, new[] { outsideCandidate, linkedCandidate });
            Check(unsafeExclusions.Failed.Count == 2 && unsafeExclusions.Patched == 0 && unsafeExclusions.AddedMovies == 0
                && File.ReadAllBytes(outsideCandidate).SequenceEqual(before) && File.ReadAllBytes(candidateFile).SequenceEqual(candidateAfter),
                "Outside and linked exclusion paths are rejected without following or changing their targets");
            File.Delete(linkedCandidate);

            string unsupportedData = Path.Combine(temporary, "unsupported-custom");
            string unsupportedFile = Path.Combine(unsupportedData, "Custom", "exact_screen.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(unsupportedFile));
            byte[] customLeaf = (byte[])customBefore.Clone();
            int customPlayer = FindNode(customLeaf, new[] { "aewroot", "1on3", "1P" });
            int customChildren = Field(customLeaf, customPlayer, 55), leafVector = Target(customLeaf, customChildren);
            int stillLeaf = Target(customLeaf, leafVector + 8), stillName = Target(customLeaf, Field(customLeaf, stillLeaf, 1));
            Encoding.ASCII.GetBytes("SS_other").CopyTo(customLeaf, stillName + 4);
            uint preservedPointer = U32(customLeaf, customChildren); File.WriteAllBytes(unsupportedFile, customLeaf);
            var unsupportedRepair = MatchCardLayouts.Apply(app, unsupportedData, false, new[] { unsupportedFile });
            Check(unsupportedRepair.Unsupported.Any(issue => issue.Contains("aewroot/1on3/1P") && issue.Contains("custom slot children"))
                && U32(File.ReadAllBytes(unsupportedFile), customChildren) == preservedPointer,
                "Catalog authority preserves unsupported custom slot children and reports the exact unrepaired player");

            string incompleteData = Path.Combine(temporary, "incomplete-custom");
            string incompleteFile = Path.Combine(incompleteData, "UI", "Projects", "custom_incomplete.jsfb");
            Directory.CreateDirectory(Path.GetDirectoryName(incompleteFile));
            byte[] incomplete = (byte[])customBefore.Clone();
            int missingPlayer = FindNode(incomplete, new[] { "aewroot", "1on3", "1P" });
            int playerName = Target(incomplete, Field(incomplete, missingPlayer, 1)); incomplete[playerName + 4] = (byte)'9';
            int untouchedChildren = Field(incomplete, missingPlayer, 55); uint untouchedPointer = U32(incomplete, untouchedChildren);
            File.WriteAllBytes(incompleteFile, incomplete);
            var incompleteRepair = MatchCardLayouts.Apply(app, incompleteData, false, new[] { incompleteFile });
            Check(incompleteRepair.Unsupported.Any(issue => issue.Contains("aewroot/1on3") && issue.Contains("unexpected player-slot structure"))
                && U32(File.ReadAllBytes(incompleteFile), untouchedChildren) == untouchedPointer,
                "An explicit catalog path cannot bypass validation of the complete known player set");

            string outside = Path.Combine(temporary, "outside.jsfb"); File.WriteAllBytes(outside, before);
            string referenceData = Path.Combine(temporary, "reference-errors"); Directory.CreateDirectory(referenceData);
            var escaped = MatchCardLayouts.Apply(app, referenceData, false, new[] { outside });
            Check(escaped.Failed.Count == 1 && escaped.Patched == 0 && escaped.AddedMovies == 0 && File.ReadAllBytes(outside).SequenceEqual(before),
                "A catalog path outside the selected data root cannot be patched");
            string malformedReference = Path.Combine(referenceData, "not-a-screen.jsfb"); File.WriteAllBytes(malformedReference, new byte[32]);
            var invalidReference = MatchCardLayouts.Apply(app, referenceData, false, new[] { malformedReference });
            Check(invalidReference.Failed.Count == 1 && invalidReference.Patched == 0 && invalidReference.AddedMovies == 0 && File.ReadAllBytes(malformedReference).SequenceEqual(new byte[32]),
                "An explicit malformed screen reference reports its failure instead of silently counting as configured");
            string nonMatchupData = Path.Combine(temporary, "explicit-non-matchup"), nonMatchupFile = Path.Combine(nonMatchupData, "plain_screen.jsfb");
            byte[] nonMatchupBefore = Wrap(before, 1); Put32(nonMatchupBefore, 224, 80); // Empty vector at 304; original nodes are unreachable.
            Directory.CreateDirectory(nonMatchupData); File.WriteAllBytes(nonMatchupFile, nonMatchupBefore);
            var nonMatchup = MatchCardLayouts.Apply(app, nonMatchupData, false, new[] { nonMatchupFile });
            Check(nonMatchup.Patched == 0 && nonMatchup.AddedMovies == 0 && nonMatchup.Unsupported.Count == 1 && nonMatchup.Failed.Count == 0
                && File.ReadAllBytes(nonMatchupFile).SequenceEqual(nonMatchupBefore) && !Directory.Exists(Path.Combine(nonMatchupData, "UI")),
                "Explicit authority alone does not create movie files for a screen with no known player layout");
            string linkedReference = Path.Combine(referenceData, "linked.jsfb"); File.CreateSymbolicLink(linkedReference, outside);
            var linked = MatchCardLayouts.Apply(app, referenceData, false, new[] { linkedReference });
            Check(linked.Failed.Count == 1 && linked.Patched == 0 && File.ReadAllBytes(outside).SequenceEqual(before),
                "An explicit symbolic link cannot redirect a screen repair outside the selected data folder");
            File.Delete(linkedReference);

            string allData = Path.Combine(temporary, "all-discovery_data");
            foreach (string file in Directory.GetFiles(pack, "*", SearchOption.AllDirectories)) Copy(file, Path.Combine(allData, file.Substring(pack.Length + 1)));
            var all = MatchCardLayouts.Apply(app, allData, false);
            Console.WriteLine("All shipped screens: " + JsonSerializer.Serialize(all));
            Check(all.Failed.Count == 0 && all.Unsupported.Count == 0, "All 86 actual shipped match-card projects match the append-copy schema");
            Check(all.Patched == 86 && all.AddedSlots > 4000 && all.AddedMovies == 52, "All stock and creation-arena match-card files receive their omitted layouts");
            var allAgain = MatchCardLayouts.Apply(app, allData, true);
            Check(allAgain.Patched == 0 && allAgain.AddedSlots == 0 && allAgain.AddedMovies == 0 && allAgain.Already == 86 && allAgain.Failed.Count == 0, "Every completed stock screen passes a repeated read-only check");

            string wrongMovieData = Path.Combine(temporary, "wrong-existing-movies");
            foreach (string movieFile in Directory.GetFiles(Path.Combine(allData, "UI", "Assets", "movies", "gorilla_renders"), "*.bk2"))
                Copy(movieFile, Path.Combine(wrongMovieData, "UI", "Assets", "movies", "gorilla_renders", Path.GetFileName(movieFile)));
            var movieInputs = new Dictionary<string, byte[]>();
            foreach (string variant in new[] { "autoplay", "asset", "type" })
            {
                byte[] invalidMovie = (byte[])after.Clone();
                int node = FindNode(invalidMovie, new[] { "matchup", "1on1", "1P", "GM_clip" });
                if (variant == "autoplay") invalidMovie[Field(invalidMovie, node, 51)] = 0;
                else if (variant == "type") invalidMovie[Field(invalidMovie, node, 2)] = 2;
                else invalidMovie[Field(invalidMovie, node, 0)] ^= 1;
                string invalidMovieFile = Path.Combine(wrongMovieData, "UI", "Projects", "Modded", "wrong_" + variant + ".jsfb");
                Directory.CreateDirectory(Path.GetDirectoryName(invalidMovieFile)); File.WriteAllBytes(invalidMovieFile, invalidMovie); movieInputs[invalidMovieFile] = invalidMovie;
            }
            var movieValidation = MatchCardLayouts.Apply(app, wrongMovieData, false);
            Check(movieValidation.Patched == 0 && movieValidation.Already == 0 && movieValidation.Unsupported.Count == 3 && movieValidation.Failed.Count == 0,
                "Existing GM_clip nodes with wrong autoplay, movie type or layout/player asset are reported instead of counted as complete");
            Check(movieInputs.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) && movieValidation.AddedMovies == 0 && !movieValidation.BakeRequired,
                "Unsupported existing movie nodes retain their complete original files and require no guessed repair");
            string existingData = Path.Combine(temporary, "incomplete-existing-slots");
            foreach (string movieFile in Directory.GetFiles(Path.Combine(allData, "UI", "Assets", "movies", "gorilla_renders"), "*.bk2"))
                Copy(movieFile, Path.Combine(existingData, "UI", "Assets", "movies", "gorilla_renders", Path.GetFileName(movieFile)));
            var brokenSlots = new Dictionary<string, byte[]>();
            foreach (string variant in new[] { "marker", "marker_y", "wrong_player", "position", "size", "scale" })
            {
                byte[] broken = (byte[])after.Clone();
                int stillNode = FindNode(broken, new[] { "matchup", "1on1", "1P", "SS_dummy" });
                int movieNode = FindNode(broken, new[] { "matchup", "1on1", "1P", "GM_clip" });
                if (variant == "marker") SetFloats(broken, stillNode, 5, 1f, 1f);
                else if (variant == "marker_y") SetFloats(broken, stillNode, 5, 1f + 1f / 1048576f, 1f);
                else if (variant == "wrong_player") SetFloats(broken, stillNode, 5, 1f + 2f / 1048576f, 1f + 2f / 1048576f);
                else if (variant == "position") SetFloats(broken, movieNode, 3, 12f, -551f);
                else if (variant == "size") SetFloats(broken, movieNode, 4, 512f, 1024f);
                else SetFloats(broken, movieNode, 5, 1f, 1f);
                string file = Path.Combine(existingData, "UI", "Projects", "Modded", variant + ".jsfb");
                Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, broken); brokenSlots[file] = broken;
            }
            string[] beforeExistingFiles = Files(existingData);
            var existingCheck = MatchCardLayouts.Apply(app, existingData, true);
            Check(existingCheck.Patched == 6 && existingCheck.RepairedSlots == 6 && existingCheck.CheckedSlots == 606
                && existingCheck.AddedSlots == 0 && existingCheck.Failed.Count == 0 && existingCheck.Unsupported.Count == 0 && existingCheck.BakeRequired,
                "Existing movie slots with missing or wrong static-picture markers and movie transforms need repair instead of counting as complete");
            Check(brokenSlots.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) && Files(existingData).SequenceEqual(beforeExistingFiles),
                "Read-only marker and movie-transform diagnosis preserves every project and creates no backup");
            var fixedExisting = MatchCardLayouts.Apply(app, existingData, false);
            Check(fixedExisting.Patched == 6 && fixedExisting.RepairedSlots == 6 && fixedExisting.AddedSlots == 0
                && fixedExisting.CheckedSlots == 606 && fixedExisting.Unsupported.Count == 0 && fixedExisting.Failed.Count == 0 && fixedExisting.BakeRequired,
                "Known existing slots receive the exact native marker and movie transform through the normal backed-up setup flow");
            foreach (var pair in brokenSlots)
            {
                byte[] complete = File.ReadAllBytes(pair.Key);
                int group = FindNode(pair.Value, new[] { "matchup", "1on1", "1P" });
                int pointer = Field(pair.Value, group, 55);
                Check(Enumerable.Range(0, pair.Value.Length).All(index => pair.Value[index] == complete[index] || (index >= pointer && index < pointer + 4)),
                    "Repair only updates the original child pointer and appends replacement leaves: " + Path.GetFileName(pair.Key));
                Check(File.ReadAllBytes(Path.Combine(fixedExisting.BackupDirectory, pair.Key.Substring(existingData.Length + 1) + ".bak")).SequenceEqual(pair.Value),
                    "Repair retains exact incomplete original bytes for " + Path.GetFileName(pair.Key));
                int stillNode = FindNode(complete, new[] { "matchup", "1on1", "1P", "SS_dummy" });
                int movieNode = FindNode(complete, new[] { "matchup", "1on1", "1P", "GM_clip" });
                Check(ReadFloats(complete, stillNode, 5).SequenceEqual(new[] { 1f + 1f / 1048576f, 1f + 1f / 1048576f })
                    && ReadFloats(complete, movieNode, 3).SequenceEqual(new[] { 0f, -551f })
                    && ReadFloats(complete, movieNode, 4).SequenceEqual(new[] { 1024f, 1024f })
                    && ReadFloats(complete, movieNode, 5).SequenceEqual(new[] { 1102f / 1024f, 1102f / 1024f }),
                    "Both exact static-layer marker components and all movie transforms are restored for " + Path.GetFileName(pair.Key));
                if (args.Length > 1)
                {
                    string fixtures = Path.GetFullPath(args[1]);
                    Copy(pair.Key, Path.Combine(fixtures, "existing-repairs", Path.GetFileName(pair.Key)));
                    File.WriteAllBytes(Path.Combine(fixtures, "existing-repairs", Path.GetFileNameWithoutExtension(pair.Key) + ".original.jsfb"), pair.Value);
                }
            }
            var existingAgain = MatchCardLayouts.Apply(app, existingData, false);
            Check(existingAgain.Patched == 0 && existingAgain.RepairedSlots == 0 && existingAgain.AddedSlots == 0
                && existingAgain.Already == 6 && existingAgain.CheckedSlots == 606 && !existingAgain.BakeRequired,
                "A repeated setup validates all existing repaired slots without rewriting files or requesting another bake");

            string customMarkerData = Path.Combine(temporary, "custom-marker-children");
            string customMarkerFile = Path.Combine(customMarkerData, "UI", "Projects", "Custom", "custom_card.jsfb");
            byte[] customMarker = (byte[])after.Clone();
            int customStill = FindNode(customMarker, new[] { "matchup", "1on1", "1P", "SS_dummy" });
            SetFloats(customMarker, customStill, 5, 1f, 1f);
            int customCas = FindNode(customMarker, new[] { "matchup", "1on1", "1P", "CAS_dummy" });
            int customCasName = Target(customMarker, Field(customMarker, customCas, 1));
            Encoding.ASCII.GetBytes("CAS_other").CopyTo(customMarker, customCasName + 4);
            Directory.CreateDirectory(Path.GetDirectoryName(customMarkerFile)); File.WriteAllBytes(customMarkerFile, customMarker);
            var customMarkerReport = MatchCardLayouts.Apply(app, customMarkerData, false);
            Check(customMarkerReport.Patched == 0 && customMarkerReport.RepairedSlots == 0 && customMarkerReport.Unsupported.Count == 1
                && customMarkerReport.Unsupported[0].Contains("marker or transform") && File.ReadAllBytes(customMarkerFile).SequenceEqual(customMarker),
                "An incompatible marker in a custom child structure is reported without replacing unknown mod layers");
            string unsupportedExistingData = Path.Combine(temporary, "unsupported-existing-children");
            foreach (string movieFile in Directory.GetFiles(Path.Combine(allData, "UI", "Assets", "movies", "gorilla_renders"), "*.bk2"))
                Copy(movieFile, Path.Combine(unsupportedExistingData, "UI", "Assets", "movies", "gorilla_renders", Path.GetFileName(movieFile)));
            var unsupportedChildren = new Dictionary<string, byte[]>();
            foreach (string variant in new[] { "static_kind", "cas_kind", "static_children", "cas_children", "movie_children", "static_animations" })
            {
                byte[] unknown = (byte[])after.Clone();
                int stillNode = FindNode(unknown, new[] { "matchup", "1on1", "1P", "SS_dummy" });
                int casNode = FindNode(unknown, new[] { "matchup", "1on1", "1P", "CAS_dummy" });
                int movieNode = FindNode(unknown, new[] { "matchup", "1on1", "1P", "GM_clip" });
                if (variant == "static_kind") unknown[Field(unknown, stillNode, 2)] = 7;
                else if (variant == "cas_kind") unknown[Field(unknown, casNode, 2)] = 7;
                else unknown = AddChildReference(unknown, variant == "cas_children" ? casNode : variant == "movie_children" ? movieNode : stillNode,
                    variant == "static_animations" ? 56 : 55, movieNode);
                string file = Path.Combine(unsupportedExistingData, "UI", "Projects", "Modded", variant + ".jsfb");
                Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, unknown); unsupportedChildren[file] = unknown;
            }
            var unsupportedExisting = MatchCardLayouts.Apply(app, unsupportedExistingData, false);
            Check(unsupportedExisting.Patched == 0 && unsupportedExisting.Already == 0 && unsupportedExisting.RepairedSlots == 0
                && unsupportedExisting.Unsupported.Count == 6 && unsupportedExisting.Failed.Count == 0 && !unsupportedExisting.BakeRequired,
                "Correct marker and transform cannot falsely validate unknown static/CAS node kinds or non-leaf child and animation lists");
            Check(unsupportedChildren.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                && !Directory.Exists(Path.Combine(unsupportedExistingData, "Gorilla Studio Layout Backups")),
                "Unknown existing player-layer schemas remain byte-for-byte unchanged without creating backups");
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
    private static void SetFloats(byte[] bytes, int node, int property, float first, float second)
    {
        int table = Target(bytes, Field(bytes, node, property));
        BitConverter.GetBytes(first).CopyTo(bytes, Field(bytes, table, 0)); BitConverter.GetBytes(second).CopyTo(bytes, Field(bytes, table, 1));
    }
    private static float[] ReadFloats(byte[] bytes, int node, int property)
    {
        int table = Target(bytes, Field(bytes, node, property));
        return new[] { BitConverter.ToSingle(bytes, Field(bytes, table, 0)), BitConverter.ToSingle(bytes, Field(bytes, table, 1)) };
    }
    private static byte[] AddChildReference(byte[] source, int node, int property, int child)
    {
        int field = Field(source, node, property);
        if (field == 0) throw new Exception("Fixture requires a stored node-list field.");
        int vector = (source.Length + 7) & ~7, embedded = vector + 8;
        byte[] result = new byte[embedded + source.Length];
        Buffer.BlockCopy(source, 0, result, 0, source.Length);
        Buffer.BlockCopy(source, 0, result, embedded, source.Length);
        Put32(result, field, vector - field); Put32(result, vector, 1); Put32(result, vector + 4, embedded + child - vector - 4);
        return result;
    }
    private static void Copy(string source, string destination) { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(source, destination, true); }
    private static void Check(bool value, string description) { if (!value) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }

    // Preserve all real RAW 24 tables and relative offsets. A new outer node points
    // forward at each copied original root vector, introducing genuine nested paths.
    private static byte[] Wrap(byte[] original, int copies)
    {
        int stride = (original.Length + 7) & ~7;
        byte[] result = new byte[512 + stride * copies];
        Put32(result, 0, 24); Encoding.ASCII.GetBytes("Scre").CopyTo(result, 4);
        Put16(result, 8, 12); Put16(result, 10, 8); Put16(result, 18, 4);
        Put32(result, 24, 16); Put32(result, 28, 4);
        Put32(result, 32, copies);
        Put16(result, 96, 116); Put16(result, 98, 16); Put16(result, 102, 4); Put16(result, 210, 8);
        Put32(result, 272, 5); Encoding.ASCII.GetBytes("outer").CopyTo(result, 276);
        int root = (int)U32(original, 0), childField = Field(original, root, 3), childVector = Target(original, childField);
        for (int index = 0; index < copies; index++)
        {
            int node = 216 + index * 16, start = 512 + stride * index, reference = 36 + index * 4;
            Put32(result, reference, node - reference); Put32(result, node, node - 96);
            Put32(result, node + 4, 272 - node - 4); Put32(result, node + 8, start + childVector - node - 8);
            Buffer.BlockCopy(original, 0, result, start, original.Length);
        }
        return result;
    }

    private static byte[] RenameMatchupRoot(byte[] original, string name)
    {
        byte[] result = (byte[])original.Clone();
        int root = (int)U32(result, 0), vector = Target(result, Field(result, root, 3));
        for (int index = 0; index < U32(result, vector); index++)
        {
            int node = Target(result, vector + 4 + index * 4), text = Target(result, Field(result, node, 1));
            int size = (int)U32(result, text);
            if (Encoding.UTF8.GetString(result, text + 4, size) != "matchup") continue;
            if (Encoding.UTF8.GetByteCount(name) != size) throw new Exception("The root fixture name must retain its original encoded size.");
            Encoding.UTF8.GetBytes(name).CopyTo(result, text + 4); return result;
        }
        throw new Exception("The actual RAW fixture contains no matchup root.");
    }
    private static int FindNode(byte[] bytes, string[] path)
    {
        int root = (int)U32(bytes, 0), rootVector = Target(bytes, Field(bytes, root, 3));
        return FindNode(bytes, rootVector, path, 0);
    }
    private static int FindNode(byte[] bytes, int vector, string[] path, int depth)
    {
        for (int index = 0; index < U32(bytes, vector); index++)
        {
            int node = Target(bytes, vector + 4 + index * 4), name = Target(bytes, Field(bytes, node, 1));
            if (Encoding.UTF8.GetString(bytes, name + 4, (int)U32(bytes, name)) != path[depth]) continue;
            if (depth + 1 == path.Length) return node;
            int children = Field(bytes, node, 55);
            if (children != 0) return FindNode(bytes, Target(bytes, children), path, depth + 1);
        }
        throw new Exception("The actual fixture contains no expected node: " + string.Join("/", path));
    }
    private static int Field(byte[] bytes, int table, int slot)
    {
        int vt = table - unchecked((int)U32(bytes, table));
        if (4 + slot * 2 >= BitConverter.ToUInt16(bytes, vt)) return 0;
        int offset = BitConverter.ToUInt16(bytes, vt + 4 + slot * 2); return offset == 0 ? 0 : table + offset;
    }
    private static int Target(byte[] bytes, int field) { return checked(field + (int)U32(bytes, field)); }
    private static uint U32(byte[] bytes, int at) { return BitConverter.ToUInt32(bytes, at); }
    private static void Put32(byte[] bytes, int at, int value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (i * 8)); }
    private static void Put16(byte[] bytes, int at, int value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
}
