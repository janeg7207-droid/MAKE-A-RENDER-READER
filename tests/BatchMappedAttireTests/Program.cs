using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string temporary;
    private static int passed;
    private static int failed;

    private static int Main()
    {
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-batch-mapped-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            SplitSources();
            SharedGeometry();
            DirectSources();
            InvalidSelections();
            LoadedAttireSelection();
            Console.WriteLine("PASS " + passed + " batch mapped-attire checks; failures " + failed);
            return failed == 0 ? 0 : 1;
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void SplitSources()
    {
        Fixture fixture = DemonFixture("split-batch", false);
        List<DiscoveryAttireSource> sources = Find(fixture, 626).Sources.OrderBy(item => item.CostumeIndex).ToList();
        Equal(2, sources.Count);
        fixture.Candidates.Single(item => item.Folder == sources[0].SourceFolder).SourceId = 701;
        fixture.Candidates.Single(item => item.Folder == sources[1].SourceFolder).SourceId = 702;
        sources = Find(fixture, 626).Sources.OrderBy(item => item.CostumeIndex).ToList();
        foreach (DiscoveryAttireSource source in sources)
        {
            Write(Path.Combine(source.SourceFolder, "BaseModel", "baseModel.mcd"), Encoding.ASCII.GetBytes("body " + source.SourceId));
            Write(Path.Combine(source.SourceFolder, "Textures", "body.dds"), Encoding.ASCII.GetBytes("texture " + source.SourceId));
        }
        Dictionary<string, byte[]> before = Snapshot(fixture.Root);
        List<BatchAttireSelection> selected = sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
        RosterEntry original = Entry(fixture);
        BatchRenderPlan plan = Plan(fixture, original, selected);
        Check("Exact active choices resolve an otherwise ambiguous primary character", () => {
            Equal(2, plan.Jobs.Count); Equal(0, plan.Skipped.Count);
        });
        Check("Separate sources with identical Attire.mcd paths create separate jobs", () => {
            Equal("Default_Attire/Attire.mcd", plan.Jobs[0].AttireSourceRelativePath);
            Equal("Default_Attire/Attire.mcd", plan.Jobs[1].AttireSourceRelativePath);
            Equal(2, plan.Jobs.Select(job => job.Entry.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Check("Every mapped variant retains logical 626 and its own physical source ID", () => {
            Equal(true, plan.Jobs.All(job => job.Entry.Id == 626 && job.HasExplicitAttire && !job.ExtractFromGame));
            Equal("701,702", string.Join(",", plan.Jobs.Select(job => job.Entry.SourceId)));
        });
        Check("A mapped job keeps the complete source body and textures from the selected root", () => {
            foreach (BatchRenderJob job in plan.Jobs)
            {
                Equal("body " + job.Entry.SourceId, File.ReadAllText(Path.Combine(job.Entry.Folder, "BaseModel", "baseModel.mcd")));
                Equal("texture " + job.Entry.SourceId, File.ReadAllText(Path.Combine(job.Entry.Folder, "Textures", "body.dds")));
            }
        });
        Check("Inactive costume five and regular Finn 576 cannot enter Demon batches", () => {
            Equal(false, plan.Jobs.Any(job => job.Entry.Folder.Contains("UnlistedDemon") || job.Entry.Folder.Contains("RegularFinn")));
            Equal(true, plan.Jobs.All(job => job.Entry.Id == 626));
        });
        Check("The normal renderer receives the selected root and contained MCD together", () => {
            foreach (BatchRenderJob job in plan.Jobs)
            {
                string relative = BatchRenderPlan.AliasAttirePath(job, false);
                string[] arguments = AlternateAttires.Apply("gorilla_render.py", new[] { job.Entry.Folder, "input.npz", "output" }, relative);
                Equal(job.Entry.Folder, arguments[0]); Equal("attire=Default_Attire/Attire.mcd", arguments.Last());
            }
        });
        Check("Mapped selection snapshots do not mutate the original roster row", () => {
            Equal(null, original.Folder); Equal(576, original.SourceId);
            original.Name = "Changed after scheduling"; selected[0].DisplayName = "Changed choice"; selected[0].SourceFolder = fixture.Root;
            Equal(true, plan.Jobs.All(job => job.Entry.Name == "Demon Finn"));
            Equal("\"DEMON\" FINN BÁLOR", plan.Jobs[0].AttireName);
            Equal(sources[0].SourceFolder, plan.Jobs[0].Entry.Folder);
        });
        Check("Mapped choices own independent logical roster snapshots", () => {
            Equal(false, ReferenceEquals(plan.Jobs[0].Entry, plan.Jobs[1].Entry));
            plan.Jobs[0].Entry.Name = "First variant only"; Equal("Demon Finn", plan.Jobs[1].Entry.Name);
        });
        Check("Source and attire ordinals keep mapped workspaces distinct", () => {
            Equal("1,2", string.Join(",", plan.Jobs.Select(job => job.VariantIndex)));
            Equal(2, plan.Jobs.Select(BatchRenderPlan.WorkspaceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Equal(true, plan.Jobs.All(job => SafeFilename(BatchRenderPlan.WorkspaceName(job))));
        });
        Check("Mapped outputs coexist in the ordinary output directory", () => {
            string output = Path.Combine(temporary, "normal-output");
            foreach (BatchRenderJob job in plan.Jobs)
            {
                job.OutputDirectory = Path.Combine(temporary, "workspace", BatchRenderPlan.WorkspaceName(job));
                job.RenderPath = Path.Combine(job.OutputDirectory, "626_Demon.gmr");
                Write(job.RenderPath, Render((byte)job.VariantIndex));
                string published = BatchRenderPlan.PublishCompleted(job, output), reason;
                Equal(true, BatchRenderPlan.VerifyCompleted(published, 626, output, out reason));
                Equal(true, File.ReadAllBytes(published).SequenceEqual(File.ReadAllBytes(job.RenderPath)));
                Equal(true, Path.GetFileName(published).StartsWith("626_", StringComparison.Ordinal));
            }
            Equal(2, plan.Jobs.Select(job => job.PublishedRenderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
        Check("Planning and publication never modify Discovery or either complete model source", () => Equal(true, SameSnapshot(before, fixture.Root)));
        Check("A repeated exact mapped selection is deduplicated without losing the other source", () => {
            var picks = sources.Select(BatchRenderPlan.DiscoveryChoice).ToList(); picks.Add(BatchRenderPlan.DiscoveryChoice(sources[0]));
            Equal(2, Plan(fixture, Entry(fixture), picks).Jobs.Count);
        });
        Check("One explicit mapped choice schedules only the selected outfit", () => {
            BatchRenderPlan one = Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { BatchRenderPlan.DiscoveryChoice(sources[1]) });
            Equal(1, one.Jobs.Count); Equal(sources[1].SourceFolder, one.Jobs[0].Entry.Folder); Equal(702, one.Jobs[0].Entry.SourceId);
        });
        Check("An explicit active selection replaces an unrelated previously saved model root", () => {
            RosterEntry entry = Entry(fixture); entry.Folder = fixture.Candidates.Single(item => item.Folder.Contains("RegularFinn")).Folder;
            BatchRenderPlan chosen = Plan(fixture, entry, new List<BatchAttireSelection> { BatchRenderPlan.DiscoveryChoice(sources[1]) });
            Equal(1, chosen.Jobs.Count); Equal(sources[1].SourceFolder, chosen.Jobs[0].Entry.Folder); Equal(702, chosen.Jobs[0].Entry.SourceId);
            Equal(true, entry.Folder.Contains("RegularFinn"));
        });
        Check("A mapped job snapshots the supplied Discovery roster context when its row has none", () => {
            RosterEntry entry = Entry(fixture); entry.RosterTable = null;
            BatchRenderPlan chosen = Plan(fixture, entry, new List<BatchAttireSelection> { BatchRenderPlan.DiscoveryChoice(sources[1]) });
            Equal(Path.GetFullPath(fixture.Table), chosen.Jobs.Single().Entry.RosterTable);
            Equal(null, entry.RosterTable);
        });
        Check("A valid job refresh returns all currently active mapped alternatives", () => {
            BatchRenderPlan chosen = Plan(fixture, Entry(fixture), sources.Select(BatchRenderPlan.DiscoveryChoice).ToList());
            DiscoveryAttireCatalogResult refreshed = BatchRenderPlan.RefreshDiscoveryAttires(chosen.Jobs[0], fixture.Candidates, fixture.Table);
            Equal(2, refreshed.Sources.Count); Equal("0,1", string.Join(",", refreshed.Sources.Select(source => source.CostumeIndex).OrderBy(index => index)));
        });
        Check("Mixed-roster planning expands only the selected logical wrestler", () => {
            var regular = new RosterEntry { Id = 576, SourceId = 576, Name = "Regular Finn", RosterTable = fixture.Table };
            var choices = new Dictionary<int, List<BatchAttireSelection>> { { 626, sources.Select(BatchRenderPlan.DiscoveryChoice).ToList() } };
            BatchRenderPlan mixed = BatchRenderPlan.CreateWithAttires(new[] { Entry(fixture), regular }, fixture.Candidates, fixture.Table, choices);
            Equal(3, mixed.Jobs.Count); Equal(2, mixed.Jobs.Count(job => job.Entry.Id == 626)); Equal(1, mixed.Jobs.Count(job => job.Entry.Id == 576));
            Equal(false, mixed.Jobs.Single(job => job.Entry.Id == 576).HasExplicitAttire);
            Equal(true, mixed.Jobs.Single(job => job.Entry.Id == 576).Entry.Folder.Contains("RegularFinn"));
        });
        Check("The legacy local expansion method cannot bypass keyed Discovery validation", () => {
            RosterEntry entry = Entry(fixture); entry.Folder = sources[0].SourceFolder; entry.SourceId = sources[0].SourceId;
            BatchRenderPlan baseline = BatchRenderPlan.Create(new[] { entry }, fixture.Candidates, fixture.Table);
            Reject(() => BatchRenderPlan.WithAttires(baseline, new Dictionary<int, List<BatchAttireSelection>> {
                { 626, new List<BatchAttireSelection> { BatchRenderPlan.DiscoveryChoice(sources[0]) } } }));
        });
    }

    private static void SharedGeometry()
    {
        Fixture fixture = Minimal("shared-geometry-batch");
        fixture.Profiles.Add(Profile(626, 576, Attire(100, "Original", 100U, 0), Attire(101, "Alternate", 101U, 0)));
        fixture.Costumes.Add(Costume(626, 100U, 0)); fixture.Costumes.Add(Costume(626, 101U, 6));
        string path = fixture.Model("Demon/Default_Attire/Attire.mcd");
        fixture.Mappings.Add(Mapping(626, 100U, 0, path)); fixture.Mappings.Add(Mapping(626, 101U, 6, path)); fixture.Save();
        List<DiscoveryAttireSource> sources = Find(fixture, 626).Sources;
        Check("Two active costume keys sharing one MCD produce two batch jobs", () => {
            Equal(2, sources.Count);
            List<BatchAttireSelection> choices = sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            Equal(2, choices.Select(choice => BatchRenderPlan.SelectionIdentity(choice)).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            BatchRenderPlan plan = Plan(fixture, Entry(fixture), choices);
            Equal(2, plan.Jobs.Count); Equal(1, plan.Jobs.Select(job => job.Entry.Folder + "|" + job.AttireSourceRelativePath).Distinct().Count());
            Equal("Original,Alternate", string.Join(",", plan.Jobs.Select(job => job.AttireName)));
            Equal(2, plan.Jobs.Select(BatchRenderPlan.WorkspaceName).Distinct().Count());
        });
    }

    private static void DirectSources()
    {
        Fixture fixture = Minimal("direct-models-batch");
        fixture.Profiles.Add(Profile(626, 576, Attire(100, "Direct original", 100U, 0), Attire(101, "Direct alternate", 101U, 1)));
        fixture.Costumes.Add(Costume(626, 100U, 0)); fixture.Costumes.Add(Costume(626, 101U, 1));
        string first = fixture.Model("DirectOriginal/Attire.mcd"), second = fixture.Model("DirectAlternate/Attire.mcd");
        fixture.Mappings.Add(Mapping(626, 100U, 0, first)); fixture.Mappings.Add(Mapping(626, 101U, 1, second)); fixture.Save();
        Check("Direct-root models receive exactly one compatibility wrapper prefix", () => {
            List<DiscoveryAttireSource> sources = Find(fixture, 626).Sources;
            Equal(2, sources.Count);
            BatchRenderPlan plan = Plan(fixture, Entry(fixture), sources.Select(BatchRenderPlan.DiscoveryChoice).ToList());
            foreach (BatchRenderJob job in plan.Jobs) {
                Equal("Attire.mcd", BatchRenderPlan.AliasAttirePath(job, false));
                Equal("Default_Attire/Attire.mcd", BatchRenderPlan.AliasAttirePath(job, true));
            }
        });
    }

    private static void InvalidSelections()
    {
        Check("Removing an active profile costume rejects the previously selected key", () => {
            Fixture fixture = DemonFixture("removed-active-key", false);
            var selected = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            fixture.Profiles[0].Attires.RemoveAt(1); fixture.Save();
            Reject(() => Plan(fixture, Entry(fixture), selected));
        });
        Check("Removing the mapping row rejects the previously selected source", () => {
            Fixture fixture = DemonFixture("removed-mapping", false);
            var selected = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            fixture.Mappings.RemoveAt(1); fixture.Save(); Reject(() => Plan(fixture, Entry(fixture), selected));
        });
        Check("A missing current Discovery table rejects keyed choices", () => {
            Fixture fixture = DemonFixture("removed-table", false);
            var selected = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            File.Delete(fixture.Table); Reject(() => Plan(fixture, Entry(fixture), selected));
        });
        Check("Removing a mapped MCD rejects the stale choice", () => {
            Fixture fixture = DemonFixture("removed-mcd", false); DiscoveryAttireSource source = Find(fixture, 626).Sources.First();
            var selected = new List<BatchAttireSelection> { BatchRenderPlan.DiscoveryChoice(source) };
            File.Delete(Path.Combine(source.SourceFolder, source.RelativePath)); Reject(() => Plan(fixture, Entry(fixture), selected));
        });
        Check("Removing a mapped source root rejects the stale choice", () => {
            Fixture fixture = DemonFixture("removed-source", false); DiscoveryAttireSource source = Find(fixture, 626).Sources.First();
            var selected = new List<BatchAttireSelection> { BatchRenderPlan.DiscoveryChoice(source) };
            Directory.Delete(source.SourceFolder, true); Reject(() => Plan(fixture, Entry(fixture), selected));
        });
        Check("Regular Finn's existing same-named MCD cannot replace a Demon source", () => {
            Fixture fixture = DemonFixture("wrong-folder", false); BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(Find(fixture, 626).Sources.First());
            choice.SourceFolder = fixture.Candidates.Single(item => item.Folder.Contains("RegularFinn")).Folder;
            Reject(() => Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choice }));
        });
        Check("An absolute attire path cannot escape its mapped source", () => {
            Fixture fixture = DemonFixture("absolute-mcd", false); BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(Find(fixture, 626).Sources.First());
            choice.RelativePath = Path.Combine(choice.SourceFolder, choice.RelativePath);
            Reject(() => Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choice }));
        });
        Check("A parent traversal cannot escape its mapped source", () => {
            Fixture fixture = DemonFixture("escape-mcd", false); BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(Find(fixture, 626).Sources.First());
            choice.RelativePath = "../RegularFinn/Default_Attire/Attire.mcd";
            Reject(() => Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choice }));
        });
        Check("A different roster table cannot validate a selected Discovery source", () => {
            Fixture fixture = DemonFixture("roster-context", false); Fixture other = DemonFixture("other-context", false);
            var selected = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            var entry = Entry(fixture); entry.RosterTable = other.Table; Reject(() => Plan(fixture, entry, selected));
        });
        Check("Changing a selected key cannot borrow another active source", () => {
            Fixture fixture = DemonFixture("wrong-key", false); var choices = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            choices[0].DiscoveryKey = choices[1].DiscoveryKey;
            Reject(() => Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choices[0] }));
        });
        Check("A caller cannot replace the exact mapped physical source ID", () => {
            Fixture fixture = DemonFixture("wrong-source-id", false); BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(Find(fixture, 626).Sources.First());
            choice.SourceId = 1234;
            Reject(() => Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choice }));
        });
        Check("A caller cannot replace the selected model-path hash", () => {
            Fixture fixture = DemonFixture("wrong-source-hash", false); BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(Find(fixture, 626).Sources.First());
            choice.DiscoveryModelHash = 0x1234567890ABCDEFUL;
            Reject(() => Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choice }));
        });
        Check("An unkeyed choice cannot jump from its saved source to a separate folder", () => {
            Fixture fixture = DemonFixture("unkeyed-cross-root", false); var sources = Find(fixture, 626).Sources.ToList();
            RosterEntry entry = Entry(fixture); entry.Folder = sources[0].SourceFolder;
            BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(sources[1]); choice.DiscoveryKey = null;
            Reject(() => Plan(fixture, entry, new List<BatchAttireSelection> { choice }));
        });
        Check("An unspecified physical source ID is restored from current verified evidence", () => {
            Fixture fixture = DemonFixture("unspecified-source-id", false); BatchAttireSelection choice = BatchRenderPlan.DiscoveryChoice(Find(fixture, 626).Sources.First());
            choice.SourceId = 0;
            Equal(999, Plan(fixture, Entry(fixture), new List<BatchAttireSelection> { choice }).Jobs.Single().Entry.SourceId);
        });
        Check("Reloading a scheduled mapped job validates the active profile again", () => {
            Fixture fixture = DemonFixture("refresh-removed-key", false); var selected = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            BatchRenderJob job = Plan(fixture, Entry(fixture), selected).Jobs[1];
            fixture.Profiles[0].Attires.RemoveAt(1); fixture.Save();
            Reject(() => BatchRenderPlan.RefreshDiscoveryAttires(job, fixture.Candidates, fixture.Table));
        });
        Check("Reloading a scheduled mapped job rejects a changed mapping hash", () => {
            Fixture fixture = DemonFixture("refresh-changed-hash", false); var selected = Find(fixture, 626).Sources.Select(BatchRenderPlan.DiscoveryChoice).ToList();
            BatchRenderJob job = Plan(fixture, Entry(fixture), selected).Jobs[0];
            fixture.Mappings[0].Hash = Hash("characters/Unrelated/Default_Attire/Attire.mcd"); fixture.Save();
            Reject(() => BatchRenderPlan.RefreshDiscoveryAttires(job, fixture.Candidates, fixture.Table));
        });
    }

    private static RosterEntry Entry(Fixture fixture) { return new RosterEntry { Id = 626, SourceId = 576, Name = "Demon Finn", RosterTable = fixture.Table }; }

    private static void LoadedAttireSelection()
    {
        Fixture fixture = DemonFixture("loaded-attire-selection", false);
        DiscoveryAttireSource source = Find(fixture, 626).Sources.OrderBy(item => item.CostumeIndex).First();
        RosterEntry row = Entry(fixture); row.Folder = source.SourceFolder; row.SourceId = source.SourceId;
        BatchRenderJob ordinary = BatchRenderPlan.Create(new[] { row }, fixture.Candidates, fixture.Table).Jobs.Single();
        BatchRenderJob explicitJob = Plan(fixture, Entry(fixture), new List<BatchAttireSelection> {
            BatchRenderPlan.DiscoveryChoice(source) }).Jobs.Single();
        string seeded = AlternateAttires.Validate(source.SourceFolder, source.RelativePath);
        string explicitAlias = BatchRenderPlan.AliasAttirePath(explicitJob, true);
        Check("Loading an ordinary mapped job preserves its freshly seeded Discovery attire", () => {
            Equal(false, ordinary.HasExplicitAttire);
            Equal(seeded, BatchRenderPlan.LoadedAttire(ordinary, false, null, true, seeded, null, false));
        });
        Check("A mapped default replaces stale attire from the previously loaded model", () => {
            Equal(seeded, BatchRenderPlan.LoadedAttire(ordinary, false, null, true, seeded, "OldModel/Previous.mcd", false));
            Equal(seeded, BatchRenderPlan.LoadedAttire(ordinary, false, null, true, seeded, "OldChoice.mcd", true));
        });
        Check("An explicit batch choice takes precedence over the native mapped default", () => {
            Equal(explicitAlias, BatchRenderPlan.LoadedAttire(explicitJob, false, explicitAlias, true,
                "NativeDefault/Attire.mcd", "Previous/Attire.mcd", true));
        });
        Check("Restoring an explicit result uses the attire recorded for that completed result", () => {
            explicitJob.AttireRelativePath = "CompletedResult/Attire.mcd";
            Equal(explicitJob.AttireRelativePath, BatchRenderPlan.LoadedAttire(explicitJob, true, explicitAlias,
                true, seeded, "Previous/Attire.mcd", false));
        });
        Check("Restoring an ordinary result also preserves its recorded attire", () => {
            ordinary.AttireRelativePath = "CompletedDefault/Attire.mcd";
            Equal(ordinary.AttireRelativePath, BatchRenderPlan.LoadedAttire(ordinary, true, null,
                true, seeded, null, false));
        });
        Check("A plain local job retains the previous attire when its model stays the same", () => {
            Equal("LocalChoice/Attire.mcd", BatchRenderPlan.LoadedAttire(ordinary, false, null,
                false, null, "LocalChoice/Attire.mcd", true));
        });
        Check("A plain local job clears the previous attire when its model changes", () => {
            Equal(null, BatchRenderPlan.LoadedAttire(ordinary, false, null,
                false, null, "OldModel/Attire.mcd", false));
        });
        Check("A mapped load without a freshly seeded attire cannot silently fall back", () => {
            foreach (string missing in new[] { null, "", " " })
                Reject(() => BatchRenderPlan.LoadedAttire(ordinary, false, null,
                    true, missing, "Previous/Attire.mcd", true));
        });
        Check("An explicit batch load cannot silently fall back when its selected alias is missing", () => {
            foreach (string missing in new[] { null, "", " " })
                Reject(() => BatchRenderPlan.LoadedAttire(explicitJob, false, missing,
                    true, seeded, "Previous/Attire.mcd", true));
        });
    }

    private static BatchRenderPlan Plan(Fixture fixture, RosterEntry entry, List<BatchAttireSelection> selections)
    { return BatchRenderPlan.CreateWithAttires(new[] { entry }, fixture.Candidates, fixture.Table, new Dictionary<int, List<BatchAttireSelection>> { { 626, selections } }); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is IOException) { return; }
        throw new Exception("Expected the stale or unproven mapped selection to be rejected.");
    }
    private static bool SafeFilename(string value) { return value.Length > 0 && value.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0 && !value.Any(char.IsControl); }
    private static void Write(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); }
    private static Dictionary<string, byte[]> Snapshot(string path) { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(file => file.Substring(path.Length), File.ReadAllBytes); }
    private static bool SameSnapshot(Dictionary<string, byte[]> before, string path) { var after = Snapshot(path); return before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && pair.Value.SequenceEqual(after[pair.Key])); }

    // Header-only fixture exercises production publication verification, not movie decoding.
    private static byte[] Render(byte fill)
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(Enumerable.Repeat(fill, 8).ToArray());
            byte[] metadata = Encoding.UTF8.GetBytes("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":626}");
            writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1")); return stream.ToArray();
        }
    }

    private static Fixture DemonFixture(string name, bool sameFolder)
    {
        Fixture result = Minimal(name);
        var demon = Profile(626, 576, Attire(100, "\"DEMON\" FINN BÁLOR", 0xFE1B1EB5U, 0), Attire(101, "CARNAGE", 0x4292F641U, 1)); demon.DefaultKey = 0x4292F641U;
        result.Profiles.Add(demon); result.Profiles.Add(Profile(576, 576, Attire(102, "FINN BÁLOR", 0xA6805F9CU, 0)));
        result.Costumes.Add(Costume(626, 0xFE1B1EB5U, 0)); result.Costumes.Add(Costume(626, 0x4292F641U, 1)); result.Costumes.Add(Costume(626, 0xA26E7EA8U, 5)); result.Costumes.Add(Costume(576, 0xA6805F9CU, 0));
        string normal = result.Model(sameFolder ? "Demon/Default_Attire/Original.mcd" : "OriginalDemon/Default_Attire/Attire.mcd");
        string carnage = result.Model(sameFolder ? "Demon/Default_Attire/Carnage.mcd" : "CarnageDemon/Default_Attire/Attire.mcd");
        result.Mappings.Add(Mapping(626, 0xFE1B1EB5U, 0, normal)); result.Mappings.Add(Mapping(626, 0x4292F641U, 1, carnage));
        result.Mappings.Add(Mapping(626, 0xA26E7EA8U, 5, result.Model("UnlistedDemon/Default_Attire/Attire.mcd")));
        result.Mappings.Add(Mapping(576, 0xA6805F9CU, 0, result.Model("RegularFinn/Default_Attire/Attire.mcd")));
        result.Save(); return result;
    }

    private static Fixture Minimal(string name) { return new Fixture(Path.Combine(temporary, name)); }
    private static ProfileRow Profile(int id, int baseId, params AttireRow[] attires) { return new ProfileRow { Id = id, BaseId = baseId, Attires = attires.ToList() }; }
    private static AttireRow Attire(uint label, string name, uint key, int uiIndex) { return new AttireRow { Label = label, Name = name, Key = key, UiIndex = uiIndex }; }
    private static CostumeRow Costume(int id, uint key, int index) { return new CostumeRow { Id = id, Key = key, Index = index }; }
    private static MapRow Mapping(int id, uint key, int index, string path) { return new MapRow { Id = id, Key = key, Index = index, Hash = Hash("characters/" + path) }; }
    private static ulong Hash(string path) { return CharacterMappingReader.ModelPathHash(path); }
    private static DiscoveryAttireCatalogResult Find(Fixture fixture, int id) { return DiscoveryAttireCatalog.Find(new RosterEntry { Id = id, RosterTable = fixture.Table, SourceId = 576 }, fixture.Candidates, fixture.Table, CancellationToken.None); }
    private static string SourceIdentity(DiscoveryAttireSource source) { return Path.GetFullPath(source.SourceFolder).TrimEnd(Path.DirectorySeparatorChar) + "|" + source.RelativePath; }
    private static void AssertContainedSource(DiscoveryAttireSource source)
    {
        Equal(false, string.IsNullOrWhiteSpace(source.RelativePath)); Equal(false, Path.IsPathRooted(source.RelativePath));
        string folder = Path.GetFullPath(source.SourceFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string model = Path.GetFullPath(Path.Combine(folder, source.RelativePath));
        Equal(true, model.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Equal(true, File.Exists(model)); Equal(".mcd", Path.GetExtension(model).ToLowerInvariant());
    }

    private sealed class Fixture
    {
        internal Fixture(string root) { Root = root; Directory.CreateDirectory(Path.Combine(root, "Roster")); Directory.CreateDirectory(Path.Combine(root, "Characters")); }
        internal readonly string Root;
        internal string Table { get { return Path.Combine(Root, "Roster", "CharProfileTable.jsfb"); } }
        internal string MappingPath { get { return Path.Combine(Root, "Characters", "CharacterMapping.jsfb"); } }
        internal readonly List<ProfileRow> Profiles = new List<ProfileRow>();
        internal readonly List<CostumeRow> Costumes = new List<CostumeRow>();
        internal readonly List<MapRow> Mappings = new List<MapRow>();
        internal readonly List<ModelCandidate> Candidates = new List<ModelCandidate>();
        internal string Model(string relative)
        {
            string full = Path.Combine(Root, "Characters", relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllText(full, "model path fixture");
            string first = relative.Split('/')[0]; string folder = Path.Combine(Root, "Characters", first);
            if (!Candidates.Any(item => item.Folder == folder)) Candidates.Add(new ModelCandidate { Folder = folder, Name = "Unrelated display name", SourceId = 999 });
            return relative;
        }
        internal void Save()
        {
            var profiles = new List<Node>();
            foreach (ProfileRow profile in Profiles)
            {
                Node[] attires = profile.Attires.Select(attire => (Node)new TableNode(U32(attire.Label), null, U8(0), U8(0), attire.UiIndex == 0 ? null : U8(attire.UiIndex), U32(attire.Key))).ToArray();
                var nested = new TableNode(null, null, U16(profile.Id), U16(profile.BaseId), new VectorNode(attires),
                    new VectorNode(new TableNode(profile.DefaultKey.HasValue ? U32(profile.DefaultKey.Value) : null)), null, U32((uint)profile.Id));
                profiles.Add(new TableNode(U16(profile.Id), nested));
            }
            WriteFlatBuffer(Table, "Char", new TableNode(new VectorNode(profiles.ToArray())));
            WriteFlatBuffer(MappingPath, "Char", new TableNode(new VectorNode(Mappings.Select(row => (Node)new TableNode(U16(row.Id), U32(row.Key), row.Index == 0 ? null : U8(row.Index), null, null, null, null, row.Hash == 0 ? null : U64(row.Hash))).ToArray())));
            WriteFlatBuffer(Path.Combine(Root, "Characters", "CostumesTable.jsfb"), "Cost", new TableNode(new VectorNode(Costumes.Select(row => (Node)new TableNode(U32(row.Key), U16(row.Id), row.Index == 0 ? null : U16(row.Index), U8(1))).ToArray())));
            var strings = new Dictionary<uint, string>();
            foreach (ProfileRow profile in Profiles) { strings[(uint)profile.Id] = "Profile " + profile.Id; foreach (AttireRow attire in profile.Attires) strings[attire.Label] = attire.Name; }
            Directory.CreateDirectory(Path.Combine(Root, "Sdb")); WriteSdb(Path.Combine(Root, "Sdb", "ENG.sdb"), strings);
        }
    }

    private sealed class ProfileRow { internal int Id, BaseId; internal uint? DefaultKey; internal List<AttireRow> Attires; }
    private sealed class AttireRow { internal uint Label, Key; internal string Name; internal int UiIndex; }
    private sealed class CostumeRow { internal int Id, Index; internal uint Key; }
    private sealed class MapRow { internal int Id, Index; internal uint Key; internal ulong Hash; }

    // A small forward FlatBuffer fixture writer. Every reference targets a later
    // object; scalar layout is independent of the production reader's offsets.
    private abstract class Node { }
    private sealed class ScalarNode : Node { internal ScalarNode(byte[] data) { Data = data; } internal readonly byte[] Data; }
    private sealed class TableNode : Node { internal TableNode(params Node[] fields) { Fields = fields; } internal readonly Node[] Fields; }
    private sealed class VectorNode : Node { internal VectorNode(params Node[] values) { Values = values; } internal readonly Node[] Values; }
    private static Node U8(int value) { return new ScalarNode(new[] { checked((byte)value) }); }
    private static Node U16(int value) { return new ScalarNode(BitConverter.GetBytes(checked((ushort)value))); }
    private static Node U32(uint value) { return new ScalarNode(BitConverter.GetBytes(value)); }
    private static Node U64(ulong value) { return new ScalarNode(BitConverter.GetBytes(value)); }
    private static void WriteFlatBuffer(string path, string identifier, TableNode root)
    {
        using (var bytes = new MemoryStream())
        using (var output = new BinaryWriter(bytes))
        {
            output.Write((uint)0); output.Write(Encoding.ASCII.GetBytes(identifier)); long rootOffset = WriteNode(output, root);
            output.BaseStream.Position = 0; output.Write(checked((uint)rootOffset)); File.WriteAllBytes(path, bytes.ToArray());
        }
    }
    private static long WriteNode(BinaryWriter output, Node node)
    {
        var vector = node as VectorNode;
        if (vector != null)
        {
            long start = output.BaseStream.Position; output.Write((uint)vector.Values.Length);
            foreach (Node value in vector.Values) output.Write((uint)0);
            for (int index = 0; index < vector.Values.Length; index++) { long target = WriteNode(output, vector.Values[index]); PatchOffset(output, start + 4 + 4 * index, target); }
            return start;
        }
        var table = (TableNode)node; long vtable = output.BaseStream.Position;
        output.Write(checked((ushort)(4 + table.Fields.Length * 2))); output.Write(checked((ushort)(4 + table.Fields.Length * 8)));
        for (int index = 0; index < table.Fields.Length; index++) output.Write(table.Fields[index] == null ? (ushort)0 : checked((ushort)(4 + index * 8)));
        long position = output.BaseStream.Position; output.Write(checked((int)(position - vtable)));
        for (int index = 0; index < table.Fields.Length; index++)
        {
            var scalar = table.Fields[index] as ScalarNode;
            byte[] bytes = scalar == null ? new byte[0] : scalar.Data; output.Write(bytes); output.Write(new byte[8 - bytes.Length]);
        }
        for (int index = 0; index < table.Fields.Length; index++)
            if (table.Fields[index] != null && !(table.Fields[index] is ScalarNode)) { long target = WriteNode(output, table.Fields[index]); PatchOffset(output, position + 4 + index * 8, target); }
        return position;
    }
    private static void PatchOffset(BinaryWriter output, long field, long target)
    {
        long end = output.BaseStream.Position; output.BaseStream.Position = field; output.Write(checked((uint)(target - field))); output.BaseStream.Position = end;
    }
    private static void WriteSdb(string path, Dictionary<uint, string> strings)
    {
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write((uint)0); output.Write((uint)strings.Count); uint offset = checked((uint)(8 + strings.Count * 12));
            foreach (var pair in strings) { byte[] bytes = Encoding.UTF8.GetBytes(pair.Value); output.Write(offset); output.Write((uint)bytes.Length); output.Write(pair.Key); offset += checked((uint)bytes.Length + 1); }
            foreach (var pair in strings) { output.Write(Encoding.UTF8.GetBytes(pair.Value)); output.Write((byte)0); }
        }
    }
    private static void Check(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + Environment.NewLine + error); } }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected '" + expected + "', got '" + actual + "'."); }
    private static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
}
