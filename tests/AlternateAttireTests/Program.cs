using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root;
    private static async Task Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-alternate-attires-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string model = Path.Combine(root, "312_CM_Punk");
            FileAt(model, "BaseModel/baseModel.mcd");
            FileAt(model, "BaseModel/unused.mcd");
            FileAt(model, "Textures/not_a_costume.mcd");
            FileAt(model, "312_Alternate_Attire/312_Attire.mcd");
            FileAt(model, "312_Default_Attire/312_Attire.mcd");
            FileAt(model, "312_Default_Attire/312_Accessory.mcd");
            FileAt(model, "Costumes/Red & Black/Attire.MCD");
            FileAt(model, "Other/Attire.txt");
            var catalog = AlternateAttires.Read(model);
            Equal("Four wearable models are listed", 4, catalog.Count);
            Equal("Native default folder has first priority", "312_Default_Attire/312_Attire.mcd", catalog[0].RelativePath);
            Equal("Exactly one model is the native automatic default", 1, catalog.Count(item => item.IsDefault));
            Equal("Default label is readable without a numeric prefix", "Default Attire (default)", catalog[0].DisplayName);
            Equal("Nested models are listed by explicit relative path", true, catalog.Any(item => item.RelativePath == "Costumes/Red & Black/Attire.MCD"));
            Equal("Base model is never shown as clothes", false, catalog.Any(item => item.RelativePath.IndexOf("BaseModel", StringComparison.OrdinalIgnoreCase) >= 0));
            Equal("Texture folders do not create false attire entries", false, catalog.Any(item => item.RelativePath.StartsWith("Textures/")));
            Equal("Multiple models in one folder have distinct labels", 2, catalog.Where(item => item.RelativePath.StartsWith("312_Default_Attire/")).Select(item => item.DisplayName).Distinct().Count());
            Equal("Character files remain untouched", "fixture", File.ReadAllText(Path.Combine(model, "312_Default_Attire", "312_Attire.mcd")));
            Equal("Missing character has an empty catalog", 0, AlternateAttires.Read(Path.Combine(root, "missing")).Count);
            Equal("No loaded wrestler has an empty catalog", 0, AlternateAttires.Read(null).Count);
            Equal("Automatic attire sends no explicit override", null, AlternateAttires.Validate(model, null));
            Equal("Windows relative separators are normalized", "312_Alternate_Attire/312_Attire.mcd", AlternateAttires.Validate(model, @"312_Alternate_Attire\312_Attire.mcd"));
            Equal("Spaces, ampersands and case are preserved", "Costumes/Red & Black/Attire.MCD", AlternateAttires.Validate(model, "Costumes/Red & Black/Attire.MCD"));
            foreach (string invalid in new[] { "../outside.mcd", "Costumes/../outside.mcd", "/tmp/outside.mcd", @"C:\outside.mcd", "C:outside.mcd", "Default//Attire.mcd", "./Attire.mcd", "BaseModel/baseModel.mcd", "Costumes/BaseModel/other.mcd", "312_Default_Attire/baseModel.mcd", "Attire.png", "bad\"path.mcd", "bad\nname.mcd" })
                Throws<ArgumentException>("Unsafe or non-attire selection rejected: " + invalid.Replace('\n', '?'), () => AlternateAttires.Validate(model, invalid));
            Throws<FileNotFoundException>("Removed costume fails instead of using the default", () => AlternateAttires.Validate(model, "Removed/Attire.mcd"));
            Throws<DirectoryNotFoundException>("No model root fails before applying clothes", () => AlternateAttires.Validate(Path.Combine(root, "missing"), "Default/Attire.mcd"));
            Equal("Windows engine script is recognized", true, AlternateAttires.IsRender(@"C:\studio\pipeline\gorilla_render.py"));
            Equal("Another command is never treated as the character renderer", false, AlternateAttires.IsRender("preview_game.py"));
            string selected = "312_Alternate_Attire/312_Attire.mcd";
            foreach (string option in new[] { "prepare=model", "prepare=taunt", "quick=1", "end=loop" })
            {
                string[] original = { model, "roman3", Path.Combine(root, "output"), option, "slot=1481", "screens=matchcard", "hide=lambert1" };
                string originalText = string.Join("|", original);
                string[] actual = AlternateAttires.Apply("gorilla_render.py", original, selected);
                Equal(option + " selects the requested relative MCD", "attire=" + selected, actual.Last());
                Equal(option + " preserves model root, logical slot, gear and motion", originalText, string.Join("|", actual.Take(original.Length)));
                Equal(option + " does not mutate the native argument array", originalText, string.Join("|", original));
                Equal(option + " automatic default adds no option", originalText, string.Join("|", AlternateAttires.Apply("gorilla_render.py", original, null)));
                string[] duplicated = original.Concat(new[] { "attire=old.mcd", "attire=also-old.mcd" }).ToArray();
                string[] replaced = AlternateAttires.Apply("gorilla_render.py", duplicated, selected);
                Equal(option + " cannot contain conflicting clothes options", 1, replaced.Count(value => value.StartsWith("attire=")));
            }
            string[] old = { model, "roman3", "output", "attire=old.mcd" };
            Equal("Choosing automatic clears a stale explicit attire option", 3, AlternateAttires.Apply("gorilla_render.py", old, null).Length);
            Equal("Other engine commands are left unchanged", string.Join("|", old), string.Join("|", AlternateAttires.Apply("setup_screens.py", old, selected)));
            Equal("Null arguments are accepted", null, AlternateAttires.Apply("gorilla_render.py", null, selected));
            Equal("Incomplete engine invocation is left unchanged", 1, AlternateAttires.Apply("gorilla_render.py", new[] { model }, selected).Length);
            var firstArgs = AlternateAttires.Apply("gorilla_render.py", new[] { model, "roman3", "output", "quick=1" }, selected);
            var secondArgs = AlternateAttires.Apply("gorilla_render.py", new[] { model, "roman3", "output", "quick=1" }, catalog[0].RelativePath);
            string firstKey = PreviewCache.Key(firstArgs, true, new[] { Path.Combine(model, "312_Alternate_Attire", "312_Attire.mcd") }, "same");
            string secondKey = PreviewCache.Key(secondArgs, true, new[] { Path.Combine(model, "312_Alternate_Attire", "312_Attire.mcd") }, "same");
            Equal("A cached preview cannot cross costume choices", false, firstKey == secondKey);
            Equal("Process quoting protects spaces and shell-like characters", "\"attire=Costumes/Red & Black/Attire.MCD\"", AlternateAttires.QuoteArgument("attire=Costumes/Red & Black/Attire.MCD"));
            Equal("Process quoting doubles trailing backslashes", "\"folder\\\\\"", AlternateAttires.QuoteArgument("folder\\"));
            Equal("Process quoting escapes literal double quotes", "\"a\\\"b\"", AlternateAttires.QuoteArgument("a\"b"));
            FileAt(model, "New Purple Attire/Attire.mcd");
            Equal("Refreshing reads a newly added alternate", 5, AlternateAttires.Read(model).Count);
            Equal("New attire can be selected immediately", "New Purple Attire/Attire.mcd", AlternateAttires.Validate(model, "New Purple Attire/Attire.mcd"));
            File.Delete(Path.Combine(model, "312_Alternate_Attire", "312_Attire.mcd"));
            Equal("Refreshing removes a deleted alternate", 4, AlternateAttires.Read(model).Count);
            Throws<FileNotFoundException>("Old selection never silently falls back after deletion", () => AlternateAttires.Apply("gorilla_render.py", firstArgs, selected));
            string direct = Path.Combine(root, "direct"); FileAt(direct, "Attire.mcd");
            Equal("An explicitly selected direct MCD is discoverable", "Attire.mcd", AlternateAttires.Read(direct)[0].RelativePath);
            Equal("A direct MCD does not falsely claim the native one-level automatic layout", false, AlternateAttires.Read(direct)[0].IsDefault);
            await PreparationChecks();
            await CatalogChecks();
            WrappedCatalogChecks();
            DiscoveryOptionsChecks();
            SourceFailureChecks();
            if (args.Length > 0)
            {
                string actual = args[0];
                int characters = 0;
                foreach (string folder in Directory.GetDirectories(Path.Combine(actual, "Characters")))
                {
                    var actualAttires = AlternateAttires.Read(folder);
                    if (actualAttires.Count == 0) continue;
                    characters++;
                    Equal("Uploaded character's default remains its actual model: " + Path.GetFileName(folder), true, actualAttires[0].IsDefault);
                    Equal("Uploaded model's explicit choice resolves: " + Path.GetFileName(folder), actualAttires[0].RelativePath, AlternateAttires.Validate(folder, actualAttires[0].RelativePath));
                }
                Equal("All ten uploaded model folders are read", 10, characters);
                if (args.Length > 1)
                {
                    string export = Path.GetFullPath(args[1]);
                    string exportedModel = Path.Combine(export, "Characters", "312_CM_Punk");
                    string sourceMcd = Path.Combine(actual, "Characters", "312_CM_Punk", "312_Default_Attire", "312_Attire.mcd");
                    foreach (string relative in new[] { "312_Default_Attire/312_Attire.mcd", "Added Alternate/Attire.mcd" })
                    {
                        string target = Path.Combine(exportedModel, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(sourceMcd, target, true);
                    }
                    // The native trace stops before model import; this body is a
                    // structural placeholder, never passed to Blender.
                    FileAt(exportedModel, "BaseModel/baseModel.mcd");
                    Directory.CreateDirectory(Path.Combine(export, "Roster"));
                    File.Copy(Path.Combine(actual, "Roster", "CharProfileTable.jsfb"), Path.Combine(export, "Roster", "CharProfileTable.jsfb"), true);
                    var requests = new List<object>();
                    foreach (string option in new[] { "prepare=model", "prepare=taunt", "quick=1", "end=loop" })
                        foreach (string relative in new[] { null, "312_Default_Attire/312_Attire.mcd", "Added Alternate/Attire.mcd" })
                            requests.Add(new { option = option, selected = relative, arguments = AlternateAttires.Apply("gorilla_render.py",
                                new[] { exportedModel, "roman3", Path.Combine(export, "output"), option, "slot=1481", "screens=both" }, relative) });
                    File.WriteAllText(Path.Combine(export, "requests.json"), JsonSerializer.Serialize(new {
                        source_model = sourceMcd, character = exportedModel, requests = requests
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            Console.WriteLine("PASS " + passed + " alternate attire checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void SourceFailureChecks()
    {
        var state = new AttireSourceState();
        var independent = new AttireSourceState();
        Equal("A new source context can render", true, state.CanRender);
        state.RestoreFailed("Native restoration failed.");
        Equal("A double load/restore failure latches the source context closed", false, state.CanRender);
        foreach (string option in new[] { "prepare=model", "prepare=taunt", "quick=1", "end=loop", "attire=Default/Attire.mcd" })
            Throws<InvalidOperationException>("A failed source cannot enter rendering even with " + option, () => {
                state.RequireRender("gorilla_render.py");
                AlternateAttires.Apply("gorilla_render.py", new[] { root, "taunt", "output", option }, null);
            });
        state.RequireRender("roster_lookup.py");
        Equal("The source failure does not block roster lookup needed for recovery", false, state.CanRender);
        Equal("Another owner remains usable", true, independent.CanRender);
        string alias = Path.Combine(root, "626_Demon_Finn");
        Equal("A different loaded folder cannot clear a source failure", false, state.ConfirmLoaded(alias, Path.Combine(root, "576_Finn"), 626, "626"));
        Equal("A different logical slot cannot clear a source failure", false, state.ConfirmLoaded(alias, alias, 626, "576"));
        Equal("A malformed logical slot cannot clear a source failure", false, state.ConfirmLoaded(alias, alias, 0, "0"));
        Equal("Unverified recovery leaves the failure latched", false, state.CanRender);
        Equal("A completed native load of the matching alias and logical slot clears the latch", true, state.ConfirmLoaded(alias, alias, 626, "626"));
        state.RequireRender("gorilla_render.py");
        Equal("The verified model can render after recovery", true, state.CanRender);
    }

    private static void DiscoveryOptionsChecks()
    {
        string first = Path.Combine(root, "626_Demon_Finn"), second = Path.Combine(root, "Carnage", "626_Demon_Finn");
        FileAt(first, "Default/Attire.mcd"); FileAt(first, "Extra Purple/Attire.mcd"); FileAt(second, "Default/Attire.mcd");
        var mapped = new[] {
            new AlternateAttire { SourceFolder = first, SourceRelativePath = "Default/Attire.mcd", RelativePath = "Default/Attire.mcd", DisplayName = "Demon default", DiscoveryKey = "626:101:626:0", IsDefault = true },
            new AlternateAttire { SourceFolder = second, SourceRelativePath = "Default/Attire.mcd", DisplayName = "CARNAGE", DiscoveryKey = "626:102:626:1" }
        };
        var original = AlternateAttires.Read(first);
        var choices = DiscoveryAttireOptions.Combine(first, first, original, mapped, true);
        Equal("Two mapped sources and an extra local attire remain selectable", 3, choices.Count);
        Equal("Discovery's costume label is retained", "CARNAGE", choices[1].DisplayName);
        Equal("An unmapped local attire is an explicit manual choice", true, choices.Any(item => item.RelativePath == "Extra Purple/Attire.mcd" && item.DiscoveryKey == null));
        Equal("A local choice is identified in the dropdown", "Extra Purple (local)", choices[2].DisplayName);
        Equal("A local choice keeps the mapped default authoritative", false, choices[2].IsDefault);
        Equal("Combining choices does not mutate the local catalog", "Extra Purple", original.Single(item => item.RelativePath == "Extra Purple/Attire.mcd").DisplayName);
        Equal("The exact mapped automatic default is preserved", mapped[0], choices[0]);
        var competingDefault = new AlternateAttire { RelativePath = "Extra Purple/Attire.mcd", DisplayName = "Extra Purple (default)", IsDefault = true };
        var competingChoices = DiscoveryAttireOptions.Combine(first, first, new[] { competingDefault }, mapped, true);
        Equal("A native local default cannot also claim Discovery's explicit default", false, competingChoices.Last().IsDefault);
        Equal("A displaced native default loses its default label", "Extra Purple (local)", competingChoices.Last().DisplayName);
        Equal("Removing a copied default label does not mutate the source row", "Extra Purple (default)", competingDefault.DisplayName);
        Equal("Mapped source paths remain relative to their own complete character", "Default/Attire.mcd", choices[1].SourceRelativePath);
        Equal("An external source is not passed as an attire override under the old root", null, choices[1].RelativePath);
        var afterSwitch = mapped.Select(item => new AlternateAttire { SourceFolder = item.SourceFolder, SourceRelativePath = item.SourceRelativePath,
            RelativePath = item.SourceFolder == second ? item.SourceRelativePath : null, DisplayName = item.DisplayName, DiscoveryKey = item.DiscoveryKey }).ToArray();
        Equal("Switching the loaded source preserves the complete two-source catalog", 2,
            DiscoveryAttireOptions.Combine(second, second, AlternateAttires.Read(second), afterSwitch, true).Count);
        Equal("Manual local attire behavior is retained without an authoritative catalog", 2,
            DiscoveryAttireOptions.Combine(first, first, original, null).Count);
        Equal("Mapped and local references to one exact MCD do not create duplicate manual entries", 3,
            DiscoveryAttireOptions.Combine(first, first, original, mapped).Count);
        var sharedModel = new[] {
            new AlternateAttire { SourceFolder = first, SourceRelativePath = "Default/Attire.mcd", DiscoveryKey = "927:1:927:0", DisplayName = "First active costume" },
            new AlternateAttire { SourceFolder = first, SourceRelativePath = "Default/Attire.mcd", DiscoveryKey = "927:2:927:6", DisplayName = "Second active costume" }
        };
        Equal("Distinct active costume keys sharing an MCD keep both labels and the local extra", 3,
            DiscoveryAttireOptions.Combine(first, first, original, sharedModel, true).Count);
        Equal("An exact repeated active reference is deduplicated without losing local extras", 3,
            DiscoveryAttireOptions.Combine(first, first, original, sharedModel.Concat(new[] { sharedModel[0] }), true).Count);
        Equal("Physical source roots distinguish otherwise identical costume paths", false,
            DiscoveryAttireOptions.Key(first, "Default/Attire.mcd") == DiscoveryAttireOptions.Key(second, "Default/Attire.mcd"));
        string alias = Path.Combine(root, "mapped-wrapper"); Directory.CreateDirectory(alias);
        AlternateAttires.RememberWrappedSource(alias, first);
        Equal("A mapped source path acquires one compatibility wrapper prefix", "Default_Attire/Default/Attire.mcd",
            AlternateAttires.AliasRelativePath(alias, "Default/Attire.mcd"));
        Equal("Wrapper conversion preserves the original source-relative MCD identity", "Default/Attire.mcd",
            AlternateAttires.SourceRelativePath(alias, "Default_Attire/Default/Attire.mcd"));
        Directory.CreateSymbolicLink(Path.Combine(alias, "Default_Attire"), first);
        Equal("Factory-wrapped local attires retain their alias-relative argument", "Default_Attire/Extra Purple/Attire.mcd",
            DiscoveryAttireOptions.ValidateLocalSelection(alias, first, "Default_Attire/Extra Purple/Attire.mcd"));
        Equal("A regular same-source local choice validates", true, DiscoveryAttireOptions.IsLocalChoice(first, first, "Extra Purple/Attire.mcd"));
        Equal("An existing alias file absent from the matched source is rejected", false,
            DiscoveryAttireOptions.IsLocalChoice(first, second, "Extra Purple/Attire.mcd"));
        foreach (string unsafePath in new[] { "../Default/Attire.mcd", "/tmp/Attire.mcd", "BaseModel/baseModel.mcd", "Default/Attire.txt", "Removed/Attire.mcd", null })
            Equal("An unsafe or absent local choice is rejected: " + unsafePath, false, DiscoveryAttireOptions.IsLocalChoice(first, first, unsafePath));
        var external = Path.Combine(root, "576_Other_Persona"); FileAt(external, "Attire.mcd");
        Directory.CreateSymbolicLink(Path.Combine(first, "Linked Persona"), external);
        Equal("An unmapped linked source is not a same-character local attire", false,
            DiscoveryAttireOptions.IsLocalChoice(first, first, "Linked Persona/Attire.mcd"));
        File.CreateSymbolicLink(Path.Combine(first, "Linked.mcd"), Path.Combine(external, "Attire.mcd"));
        Equal("An MCD file link cannot silently add a different character source", false,
            DiscoveryAttireOptions.IsLocalChoice(first, first, "Linked.mcd"));
        var rejected = new[] {
            new AlternateAttire { RelativePath = "../576_Other_Persona/Attire.mcd" },
            new AlternateAttire { RelativePath = "Linked Persona/Attire.mcd" },
            new AlternateAttire { RelativePath = "Linked.mcd" },
            new AlternateAttire { RelativePath = "Missing.mcd" }
        };
        Equal("Invalid local entries cannot displace the exact mapped catalog", 2,
            DiscoveryAttireOptions.Combine(first, first, rejected, mapped, true).Count);
        var duplicate = original.Single(item => item.RelativePath == "Extra Purple/Attire.mcd");
        Equal("Repeated local MCD paths add one explicit choice", 3,
            DiscoveryAttireOptions.Combine(first, first, original.Concat(new[] { duplicate }), mapped, true).Count);
        FileAt(first, "New Blue/Attire.mcd");
        Equal("A refreshed local scan adds new clothes alongside mapped sources", 4,
            DiscoveryAttireOptions.Combine(first, first, AlternateAttires.Read(first), mapped, true).Count);
        File.Delete(Path.Combine(first, "Extra Purple", "Attire.mcd"));
        Equal("A removed local MCD is rejected even if the prior catalog was cached", 2,
            DiscoveryAttireOptions.Combine(first, first, original, mapped, true).Count);
        string[] selected = AlternateAttires.Apply("gorilla_render.py", new[] { second, "taunt", "output", "quick=1", "slot=626" }, "Default/Attire.mcd");
        Equal("A switched preview uses the new whole-source root", second, selected[0]);
        Equal("A switched preview retains logical Demon Finn ID", true, selected.Contains("slot=626"));
        Equal("A switched preview uses a safe in-source costume path", "attire=Default/Attire.mcd", selected.Last());
    }

    private static void WrappedCatalogChecks()
    {
        string source = Path.Combine(root, "direct-nested", "215_PAC");
        FileAt(source, "Attire.mcd");
        FileAt(source, "Costumes/Red/Attire.mcd");
        FileAt(Path.Combine(root, "direct-nested", "368_Other_Wrestler"), "Attire.mcd");
        string alias = Path.Combine(root, "wrapper", "Characters", "215_PAC");
        Directory.CreateDirectory(alias);
        Directory.CreateSymbolicLink(Path.Combine(alias, "Default_Attire"), source);
        Equal("Ordinary linked attire folders retain the existing bounded-link policy", 1, AlternateAttires.Read(alias).Count);
        AlternateAttires.RememberWrappedSource(alias, source);
        List<AlternateAttire> wrapped = AlternateAttires.Read(alias);
        Equal("A factory-proven wrapper exposes nested attire models from its actual source", 2, wrapped.Count);
        Equal("The wrapper retains the same native direct automatic default", "Default_Attire/Attire.mcd", wrapped[0].RelativePath);
        Equal("Exactly the native automatic model is marked default in the wrapper", 1, wrapped.Count(item => item.IsDefault));
        string relative = "Default_Attire/Costumes/Red/Attire.mcd";
        Equal("Nested choices point through the existing wrapper link exactly once", true, wrapped.Any(item => item.RelativePath == relative));
        Equal("A wrapped nested attire resolves to a real model before preparation", relative, AlternateAttires.Validate(alias, relative));
        string[] render = AlternateAttires.Apply("gorilla_render.py", new[] { alias, "roman3", "output", "quick=1", "slot=1481" }, relative);
        Equal("Native preview receives the precise nested wrapped model", "attire=" + relative, render.Last());
        Equal("Choosing a wrapped attire preserves the selected wrestler installation slot", "slot=1481", render[4]);
        Equal("A wrapper never discovers a neighbouring wrestler's model", false, wrapped.Any(item => item.RelativePath.Contains("Other_Wrestler")));
        Equal("Reading the original source keeps its source-relative paths for batch jobs", true,
            AlternateAttires.Read(source).Any(item => item.RelativePath == "Costumes/Red/Attire.mcd"));
        FileAt(source, "Costumes/Blue/Attire.mcd");
        Equal("Refreshing a wrapper includes newly added nested attires", 3, AlternateAttires.Read(alias).Count);
        File.Delete(Path.Combine(source, "Costumes", "Red", "Attire.mcd"));
        Throws<FileNotFoundException>("A removed wrapped model cannot silently fall back to automatic clothes", () => AlternateAttires.Validate(alias, relative));
    }

    private static async Task CatalogChecks()
    {
        string first = Path.Combine(root, "catalog-first"), second = Path.Combine(root, "catalog-second");
        FileAt(first, "Default/Attire.mcd"); FileAt(second, "Other/Attire.mcd");
        int reads = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var release = new ManualResetEventSlim())
        {
            var catalog = new AlternateAttireCatalog(folder => {
                Interlocked.Increment(ref reads);
                entered.TrySetResult(true); release.Wait(TimeSpan.FromSeconds(5));
                return AlternateAttires.Read(folder);
            });
            Task<AlternateAttireCatalogResult> opening = catalog.ReadAsync(first, false);
            await Timeout(entered.Task);
            Equal("A slow attire scan returns without blocking the selection caller", false, opening.IsCompleted);
            Equal("The UI can observe loading without enumerating the model again", true, catalog.IsLoading);
            Equal("Repeated requests during one scan share its work", true, ReferenceEquals(opening, catalog.ReadAsync(first, false)));
            release.Set();
            AlternateAttireCatalogResult original = await opening;
            Equal("The current wrestler's scan is accepted", true, catalog.IsCurrent(original));
            Equal("The async scan lists the real relative MCD", "Default/Attire.mcd", original.Models.Single().RelativePath);
            for (int selection = 0; selection < 10; selection++) await catalog.ReadAsync(first, false);
            Equal("Changing choices in the same wrestler does not rescan its folders", 1, reads);
            FileAt(first, "Added Purple/Attire.mcd");
            AlternateAttireCatalogResult refreshed = await catalog.ReadAsync(first, true);
            Equal("Explicit Refresh discovers a newly added attire", 2, refreshed.Models.Count);
            Equal("Explicit Refresh reads the current source rather than a stale cache", 2, reads);
            Equal("A refresh makes an earlier result stale before it can update the UI", false, catalog.IsCurrent(original));
            File.Delete(Path.Combine(first, "Default", "Attire.mcd"));
            AlternateAttireCatalogResult removed = await catalog.ReadAsync(first, true);
            Equal("Explicit Refresh removes deleted models from the choices", "Added Purple/Attire.mcd", removed.Models.Single().RelativePath);
            Throws<FileNotFoundException>("A removed selection still fails validation instead of falling back", () => AlternateAttires.Validate(first, "Default/Attire.mcd"));
        }

        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var releaseFirst = new ManualResetEventSlim())
        {
            var catalog = new AlternateAttireCatalog(folder => {
                if (folder == first) { firstEntered.TrySetResult(true); releaseFirst.Wait(TimeSpan.FromSeconds(5)); }
                return AlternateAttires.Read(folder);
            });
            Task<AlternateAttireCatalogResult> old = catalog.ReadAsync(first, false);
            await Timeout(firstEntered.Task);
            AlternateAttireCatalogResult latest = await catalog.ReadAsync(second, false);
            Equal("Switching wrestlers can finish while the old scan is still running", true, catalog.IsCurrent(latest));
            Equal("The new wrestler cannot inherit the old wrestler's attire choices", "Other/Attire.mcd", latest.Models.Single().RelativePath);
            releaseFirst.Set();
            Equal("A late result for the previous wrestler is discarded", null, await old);
            Equal("The late result cannot replace the latest cached model", "Other/Attire.mcd", (await catalog.ReadAsync(second, false)).Models.Single().RelativePath);
        }

        int attempts = 0;
        var retrying = new AlternateAttireCatalog(folder => {
            if (++attempts == 1) throw new IOException("Temporarily unavailable model folder");
            return AlternateAttires.Read(folder);
        });
        AlternateAttireCatalogResult failed = await retrying.ReadAsync(second, false);
        Equal("A scan failure remains observable without faulting the UI task", true, failed.Error is IOException);
        Equal("The failed scan does not leave the Refresh button loading forever", false, retrying.IsLoading);
        AlternateAttireCatalogResult retried = await retrying.ReadAsync(second, true);
        Equal("Refresh can recover after the folder becomes readable", "Other/Attire.mcd", retried.Models.Single().RelativePath);

        var closingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var releaseClosing = new ManualResetEventSlim())
        {
            var closing = new AlternateAttireCatalog(folder => {
                closingEntered.TrySetResult(true); releaseClosing.Wait(TimeSpan.FromSeconds(5));
                return AlternateAttires.Read(folder);
            });
            Task<AlternateAttireCatalogResult> pending = closing.ReadAsync(second, false);
            await Timeout(closingEntered.Task); closing.Close(); releaseClosing.Set();
            Equal("Closing the window discards an in-flight attire scan", null, await pending);
            Equal("A closed catalog cannot launch work again", null, await closing.ReadAsync(first, true));
        }
    }

    private static async Task PreparationChecks()
    {
        var native = new FakePreparation();
        int generation = 1;
        Task selected = AlternateAttires.QueuePreparation(() => native.Current, task => native.Current = task,
            () => generation == 1, () => native.PrepareAgain());
        Equal("Go immediately sees the replacement preparation task", true, ReferenceEquals(selected, native.Current));
        Equal("New attire waits for the original preparation", false, selected.IsCompleted);
        Equal("Old preparation is not invalidated while it runs", "same-folder|1481|roman3", native.Wanted);
        native.OldGate.SetResult(true);
        await Timeout(native.Started.Task);
        Equal("Attire still prepares after old native chain sets the same PrepDone key", 1, native.PrepareCount);
        Equal("Go waits until alternate model preparation finishes", false, selected.IsCompleted);
        native.NewGate.SetResult(true); await Timeout(selected);
        Equal("Go can continue after the correct attire finishes", true, native.Current.IsCompletedSuccessfully);
        Equal("The correct native preparation key finishes", "same-folder|1481|roman3", native.Done);

        native = new FakePreparation(); generation = 1;
        Task first = AlternateAttires.QueuePreparation(() => native.Current, task => native.Current = task,
            () => generation == 1, () => native.PrepareAgain());
        generation = 2;
        Task second = AlternateAttires.QueuePreparation(() => native.Current, task => native.Current = task,
            () => generation == 2, () => native.PrepareAgain());
        Equal("Rapid attire changes expose the newest wait task", true, ReferenceEquals(second, native.Current));
        native.OldGate.SetResult(true); await Timeout(native.Started.Task);
        Equal("Superseded attire never starts an unnecessary import", 1, native.PrepareCount);
        Equal("The superseded wait is completed so the latest cannot deadlock", true, first.IsCompleted);
        native.NewGate.SetResult(true); await Timeout(second);
        Equal("The latest selected attire completes without a task cycle", true, second.IsCompletedSuccessfully);

        native = new FakePreparation(); bool sameFolder = true;
        Task changed = AlternateAttires.QueuePreparation(() => native.Current, task => native.Current = task,
            () => sameFolder, () => native.PrepareAgain());
        sameFolder = false; native.OldGate.SetResult(true); await Timeout(changed);
        Equal("Changing wrestlers discards the previous wrestler's pending attire", 0, native.PrepareCount);

        Task ownerTask = Task.FromException(new IOException("old import failed"));
        int retries = 0;
        Task retried = AlternateAttires.QueuePreparation(() => ownerTask, task => ownerTask = task,
            () => true, () => { retries++; ownerTask = Task.CompletedTask; });
        await Timeout(retried);
        Equal("A failed previous import does not prevent the new attire", 1, retries);
        Task failed = AlternateAttires.QueuePreparation(() => ownerTask, task => ownerTask = task,
            () => true, () => { throw new IOException("new import failed"); });
        try { await Timeout(failed); throw new Exception("Expected import error"); } catch (IOException) { passed++; }
        Equal("A failed new import remains observable to Go", true, ReferenceEquals(failed, ownerTask));
    }

    private static async Task Timeout(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task) throw new Exception("Preparation task did not finish");
        await task;
    }

    // Matches the native Prepare/PrepChain behavior, including its attire-blind
    // key and the old chain writing PrepDone and clearing PrepWanted on exit.
    private sealed class FakePreparation
    {
        private const string Key = "same-folder|1481|roman3";
        public readonly TaskCompletionSource<bool> OldGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> NewGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Done;
        public string Wanted = Key;
        public int PrepareCount;
        public Task Current;
        public FakePreparation() { Current = OldChain(); }
        private async Task OldChain() { await OldGate.Task; Done = Key; Wanted = null; }
        public void PrepareAgain()
        {
            Done = null; Wanted = null;
            if (Done == Key || Wanted == Key) return;
            Wanted = Key;
            Current = NewChain(Current);
        }
        private async Task NewChain(Task before)
        {
            await before;
            if (Wanted != Key || Done == Key) return;
            PrepareCount++; Started.SetResult(true);
            await NewGate.Task; Done = Key; Wanted = null;
        }
    }

    private static void FileAt(string folder, string relative)
    {
        string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "fixture");
    }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++;
    }
    private static void Throws<T>(string label, Action action) where T : Exception
    {
        try { action(); } catch (T) { passed++; return; }
        throw new Exception(label + ": expected " + typeof(T).Name);
    }
}
