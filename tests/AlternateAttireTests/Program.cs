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
