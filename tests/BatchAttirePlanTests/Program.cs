using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;
    private const int LogicalId = 924;
    private const int PhysicalId = 1173;
    private const string FirstPath = "Default_Attire/Attire.mcd";
    private const string SecondPath = "Red & Black/Attire.mcd";

    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-batch-attire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "models", "1173_PAC");
            Write(Path.Combine(source, FirstPath), new byte[] { 1, 3, 5, 7 });
            Write(Path.Combine(source, SecondPath), new byte[] { 2, 4, 6, 8 });
            var before = Snapshot(source);
            var row = new RosterEntry { Id = LogicalId, SourceId = PhysicalId, Name = "PAC", Folder = source, Source = "Chosen model folder" };
            BatchRenderPlan baseline = BatchRenderPlan.Create(new[] { row }, null, null);
            Check(baseline.Jobs.Count == 1 && !baseline.Jobs[0].ExtractFromGame, "A resolved filesystem character is the batch source");
            Check(AlternateAttires.Read(source).Count == 2, "The fixture contains two distinct existing wearable MCD paths");
            BatchRenderPlan plan = BatchRenderPlan.WithAttires(baseline, Choices(
                Pick(FirstPath, "Default gear"), Pick(SecondPath, "Red / Black: Classic?")));
            Plans(baseline, plan, row, source);
            Arguments(plan, source);
            Validation(baseline, source);
            OutputsAndInstallation(plan);
            Check(SameSnapshot(before, source), "Planning, argument selection, publication and installation leave both source models unchanged");
            Console.WriteLine("PASS " + passed + " batch attire planning checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Plans(BatchRenderPlan baseline, BatchRenderPlan plan, RosterEntry original, string source)
    {
        Check(plan.Jobs.Count == 2, "Choosing two existing attires creates two render jobs");
        Check(plan.Jobs.All(job => job.Entry.Id == LogicalId && job.Entry.SourceId == PhysicalId), "Both attires retain the logical roster slot and separate physical source ID");
        Check(plan.Jobs.All(job => job.HasExplicitAttire && !job.ExtractFromGame), "Both selected attires are explicit extracted-model jobs");
        Check(plan.Jobs.Select(job => job.AttireSourceRelativePath).SequenceEqual(new[] { FirstPath, SecondPath }), "The actual selected MCD paths reach the jobs in selection order");
        Check(plan.Jobs.Select(job => job.VariantIndex).SequenceEqual(new[] { 1, 2 }), "Attire variants receive stable one-based ordinals");
        Check(plan.Jobs[0].AttireName == "Default gear" && plan.Jobs[1].AttireName == "Red / Black: Classic?", "Readable attire names remain distinct from source paths");
        Check(baseline.Jobs.Count == 1 && !baseline.Jobs[0].HasExplicitAttire && baseline.Jobs[0].Entry.Folder == source, "Expanding attires does not mutate the base plan");
        Check(!ReferenceEquals(plan.Jobs[0].Entry, plan.Jobs[1].Entry) && !ReferenceEquals(plan.Jobs[0].Entry, baseline.Jobs[0].Entry), "Each attire owns an independent roster snapshot");
        original.Name = "Changed outside batch";
        Check(plan.Jobs.All(job => job.Entry.Name == "PAC"), "Later changes to the remembered roster do not rename scheduled jobs");
        string first = BatchRenderPlan.WorkspaceName(plan.Jobs[0]), second = BatchRenderPlan.WorkspaceName(plan.Jobs[1]);
        Check(first != second && first.StartsWith(LogicalId + "_", StringComparison.Ordinal) && second.StartsWith(LogicalId + "_", StringComparison.Ordinal), "Attire workspaces are distinct while preserving the logical ID prefix");
        Check(SafeFilename(first) && SafeFilename(second), "Display punctuation cannot create unsafe workspace paths");
        Check(BatchRenderPlan.WorkspaceName(plan.Jobs[0]) == first, "A planned attire has a stable workspace name");
        BatchRenderPlan implicitPlan = BatchRenderPlan.WithAttires(baseline, null);
        Check(implicitPlan.Jobs.Count == 1 && !implicitPlan.Jobs[0].HasExplicitAttire, "No attire selection preserves the ordinary single-job flow");
        Check(BatchRenderPlan.AliasAttirePath(implicitPlan.Jobs[0], false) == null
            && BatchRenderPlan.AliasAttirePath(implicitPlan.Jobs[0], true) == null, "Implicit jobs never invent an explicit attire argument");
        BatchRenderPlan repeated = BatchRenderPlan.WithAttires(baseline, Choices(Pick(FirstPath, "One"), Pick(FirstPath.Replace('/', '\\'), "Duplicate")));
        Check(repeated.Jobs.Count == 1, "Repeated references to the same normalized attire do not duplicate jobs");
        BatchRenderPlan labels = BatchRenderPlan.WithAttires(baseline, Choices(Pick(FirstPath, "Same label"), Pick(SecondPath, "Same label")));
        Check(BatchRenderPlan.WorkspaceName(labels.Jobs[0]) != BatchRenderPlan.WorkspaceName(labels.Jobs[1]), "Identical display labels cannot collide across distinct selected MCDs");
        string other = Path.Combine(root, "models", "610_AJ_STYLES");
        Write(Path.Combine(other, FirstPath), new byte[] { 21 });
        var mixedBase = BatchRenderPlan.Create(new[] { baseline.Jobs[0].Entry,
            new RosterEntry { Id = 610, Name = "AJ STYLES", Folder = other } }, null, null);
        var mixed = BatchRenderPlan.WithAttires(mixedBase, Choices(Pick(FirstPath, "Default"), Pick(SecondPath, "Red")));
        Check(mixed.Jobs.Count == 3 && mixed.Jobs.Count(job => job.Entry.Id == LogicalId) == 2,
            "Expanding one wrestler's clothes preserves the other selected wrestler's batch job");
        Check(!mixed.Jobs.Single(job => job.Entry.Id == 610).HasExplicitAttire,
            "A wrestler without selected clothes retains the ordinary automatic-attire flow");
    }

    private static void Arguments(BatchRenderPlan plan, string source)
    {
        string[] common = { source, "undisputed2", Path.Combine(root, "engine-output"), "slot=924", "hair=auto", "end=loop", "screens=matchcard", "attire=stale.mcd" };
        foreach (BatchRenderJob job in plan.Jobs)
        {
            string relative = BatchRenderPlan.AliasAttirePath(job, false);
            string[] actual = AlternateAttires.Apply("pipeline/gorilla_render.py", common, relative);
            Check(relative == job.AttireSourceRelativePath && actual.Last() == "attire=" + relative, "The native renderer receives the exact MCD selected for variant " + job.VariantIndex);
            Check(actual.Take(3).SequenceEqual(common.Take(3)) && actual.Contains("slot=924") && actual[1] == "undisputed2", "Selecting clothes preserves the taunt, output and logical slot for variant " + job.VariantIndex);
            Check(actual.Count(value => value.StartsWith("attire=", StringComparison.Ordinal)) == 1, "A previous attire option cannot leak into variant " + job.VariantIndex);
            Check(actual.Where(value => !value.StartsWith("attire=", StringComparison.Ordinal)).SequenceEqual(common.Where(value => !value.StartsWith("attire=", StringComparison.Ordinal))), "All shared render options survive attire selection for variant " + job.VariantIndex);
        }
        string direct = Path.Combine(root, "direct-model"), alias = Path.Combine(root, "wrapped-model");
        Write(Path.Combine(direct, "Attire.mcd"), new byte[] { 11 });
        Write(Path.Combine(direct, "Alternate.mcd"), new byte[] { 12 });
        Write(Path.Combine(alias, "Default_Attire", "Alternate.mcd"), new byte[] { 12 });
        var directBase = BatchRenderPlan.Create(new[] { new RosterEntry { Id = LogicalId, Name = "PAC", Folder = direct, SourceId = PhysicalId } }, null, null);
        var directPlan = BatchRenderPlan.WithAttires(directBase, Choices(Pick("Alternate.mcd", "Alternate")));
        string wrapped = BatchRenderPlan.AliasAttirePath(directPlan.Jobs.Single(), true);
        Check(wrapped == "Default_Attire/Alternate.mcd", "A direct model selection is translated through its compatibility Default_Attire wrapper");
        string[] wrappedArgs = AlternateAttires.Apply("gorilla_render.py", new[] { alias, "undisputed2", "output", "slot=924" }, wrapped);
        Check(wrappedArgs.Last() == "attire=Default_Attire/Alternate.mcd" && wrappedArgs.Contains("slot=924"), "The wrapped MCD is validated against the loaded alias while retaining the logical slot");
    }

    private static void Validation(BatchRenderPlan baseline, string source)
    {
        foreach (string invalid in new[] { "../outside.mcd", "/outside.mcd", "C:/outside.mcd", "Red & Black/../Default_Attire/Attire.mcd", "BaseModel/baseModel.mcd", "Default_Attire/Attire.json" })
            Throws<ArgumentException>("Unsafe or non-attire path is rejected: " + invalid, () => BatchRenderPlan.WithAttires(baseline, Choices(Pick(invalid, "Invalid"))));
        Throws<FileNotFoundException>("A removed MCD cannot silently become the default attire", () => BatchRenderPlan.WithAttires(baseline, Choices(Pick("Missing/Attire.mcd", "Missing"))));
        var installedOnly = BatchRenderPlan.Create(new[] { new RosterEntry { Id = 610, Name = "AJ STYLES", StockFolder = "610_AJ_Styles_2024" } }, null, null);
        Check(installedOnly.Jobs.Count == 1 && installedOnly.Jobs[0].ExtractFromGame, "The fixture includes a verified installed-game job with no extracted clothes");
        var select = new Dictionary<int, List<BatchAttireSelection>> { { installedOnly.Jobs[0].Entry.Id, new List<BatchAttireSelection> { Pick(FirstPath, "Default") } } };
        Throws<InvalidOperationException>("Installed-game-only jobs require an extracted wrestler before choosing multiple attires", () => BatchRenderPlan.WithAttires(installedOnly, select));
        Check(File.Exists(Path.Combine(source, FirstPath)) && File.Exists(Path.Combine(source, SecondPath)), "Rejected selections preserve the available MCD files");
    }

    private static void OutputsAndInstallation(BatchRenderPlan plan)
    {
        string output = Directory.CreateDirectory(Path.Combine(root, "normal-output")).FullName;
        Write(Path.Combine(output, "924_existing.gmr"), new byte[] { 90 });
        var originalOutput = Snapshot(output);
        for (int i = 0; i < plan.Jobs.Count; i++)
        {
            BatchRenderJob job = plan.Jobs[i];
            job.OutputDirectory = Path.Combine(root, "workspace", BatchRenderPlan.WorkspaceName(job));
            job.RenderPath = Path.Combine(job.OutputDirectory, "924_PAC.gmr");
            job.Status = "Complete";
            Write(job.RenderPath, Render((byte)(i + 1)));
            string published = BatchRenderPlan.PublishCompleted(job, output), reason;
            Check(BatchRenderPlan.VerifyCompleted(published, LogicalId, output, out reason), "Published attire " + job.VariantIndex + " retains valid logical-ID GMR metadata");
            Check(Path.GetFileName(published).StartsWith("924_", StringComparison.Ordinal) && SafeFilename(Path.GetFileName(published)), "Published attire " + job.VariantIndex + " has a safe logical-ID filename");
            Check(!Path.GetFileName(published).Equals("924_PAC.gmr", StringComparison.Ordinal), "Explicit attire " + job.VariantIndex + " has a distinguishing output label");
            Check(File.ReadAllBytes(published).SequenceEqual(File.ReadAllBytes(job.RenderPath)), "Publishing attire " + job.VariantIndex + " preserves its completed movie bytes");
            Check(BatchRenderPlan.PublishCompleted(job, output) == published, "Repeating publication reuses that same verified attire output");
        }
        Check(plan.Jobs.Select(job => job.PublishedRenderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Both attire outputs coexist in the ordinary output folder");
        Check(File.ReadAllBytes(Path.Combine(output, "924_existing.gmr")).SequenceEqual(originalOutput[Path.DirectorySeparatorChar + "924_existing.gmr"]), "Publishing variants never overwrites an existing ordinary render");
        string app = Path.Combine(root, "app"), game = Path.Combine(root, "game");
        Write(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.dlp"), Encoding.ASCII.GetBytes("MOVINGRENDERS-4.26 supplied fixture"));
        Write(Path.Combine(app, "plugin", "WWE2K26_MovingRenders.cfg"), Encoding.ASCII.GetBytes("enabled 1\n"));
        Write(Path.Combine(game, "Plugins", "WWE2K26_MovingRenders.dlp"), Encoding.ASCII.GetBytes("MOVINGRENDERS-4.20 old fixture"));
        Write(Path.Combine(game, "Plugins", "keep.txt"), new byte[] { 99 });
        var gameBefore = Snapshot(game);
        var sourceBefore = plan.Jobs.ToDictionary(job => job.RenderPath, job => File.ReadAllBytes(job.RenderPath));
        var duplicate = BatchRenderInstaller.Install(app, game, plan.Jobs);
        Check(duplicate.Installed == 0 && duplicate.Failed.Any(message => message.Contains("924")), "Bulk installation rejects two attire renders for the same active roster slot");
        Check(SameSnapshot(gameBefore, game) && duplicate.BackupDirectory == null && !Directory.Exists(Path.Combine(game, "Plugins", "GorillaRenders")), "Duplicate-ID rejection occurs before plugin, render-directory or backup writes");
        var chosen = BatchRenderInstaller.Install(app, game, new[] { plan.Jobs[1] });
        Check(chosen.Installed == 1 && chosen.Failed.Count == 0, "Choosing one completed attire for the active logical slot installs successfully");
        Check(File.ReadAllBytes(Path.Combine(game, "Plugins", "GorillaRenders", "924_PAC.gmr")).SequenceEqual(sourceBefore[plan.Jobs[1].RenderPath]), "The chosen attire's exact completed bytes reach the game's logical slot");
        Check(sourceBefore.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "Installing one variant preserves every completed source render");
    }

    private static BatchAttireSelection Pick(string path, string label) { return new BatchAttireSelection { RelativePath = path, DisplayName = label }; }
    private static Dictionary<int, List<BatchAttireSelection>> Choices(params BatchAttireSelection[] selections)
    { return new Dictionary<int, List<BatchAttireSelection>> { { LogicalId, selections.ToList() } }; }
    private static bool SafeFilename(string value) { return value.Length > 0 && value.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0 && !value.Any(char.IsControl); }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static Dictionary<string, byte[]> Snapshot(string path) { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(file => file.Substring(path.Length), File.ReadAllBytes); }
    private static bool SameSnapshot(Dictionary<string, byte[]> before, string path)
    { var after = Snapshot(path); return before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && pair.Value.SequenceEqual(after[pair.Key])); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
    private static void Throws<T>(string message, Action action) where T : Exception
    { try { action(); } catch (T) { Check(true, message); return; } throw new InvalidOperationException(message + ": expected " + typeof(T).Name); }

    // Validated header and metadata fixture; this does not claim to encode a playable movie.
    private static byte[] Render(byte fill)
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(Enumerable.Repeat(fill, 8).ToArray());
            byte[] metadata = Encoding.UTF8.GetBytes("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":924}");
            writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
            return stream.ToArray();
        }
    }
}
