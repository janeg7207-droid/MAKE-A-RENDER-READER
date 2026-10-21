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
    private const int LogicalId = 1727;
    private const string Attire = "Default_Attire/Attire.mcd";

    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-selected-model-routing-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try {
            MultipleSelectedPacks();
            AssociationValidation();
            UnnumberedSelectedPacks();
            Console.WriteLine("PASS " + passed + " batch selected-model routing checks (filesystem fixtures; no model-render quality claim)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { System.IO.Directory.Delete(root, true); }
    }

    private static void UnnumberedSelectedPacks()
    {
        Fixture fixture = MakeFixture("unnumbered-selected-packs");
        string first = MakePack(fixture.Directory, "Will_Ospreay_First", 4);
        string second = MakePack(fixture.Directory, "Will_Ospreay_Second", 5);
        var sources = new[] { new ModelCandidate { Folder = first, Name = "First unnumbered pack" },
            new ModelCandidate { Folder = second, Name = "Second unnumbered pack" } };
        RosterEntry entry = fixture.Entry; entry.SourceId = 999;
        List<ModelSourceAssociation> records = ModelSourceAssociations.Replace(null, entry, fixture.Table, sources);
        Check(ModelSourceAssociations.Sources(records, entry, fixture.Table).All(source => source.SourceId == 0),
            "Unnumbered saved model packs retain unknown physical IDs instead of invented numbered metadata");
        BatchRenderPlan plan = BatchRenderPlan.CreateWithAttires(new[] { entry }, sources, fixture.Table, null, records, fixture.Table);
        Check(plan.Jobs.Count == 2 && plan.Jobs.All(job => job.Entry.Id == LogicalId && job.Entry.SourceId == LogicalId),
            "Unnumbered selected packs use the logical-slot runtime fallback rather than a previous model's physical ID");
        foreach (BatchRenderJob job in plan.Jobs)
        {
            BatchRenderPlan.RefreshDiscoveryAttires(job, sources, fixture.Table, records, fixture.Table);
            string[] arguments = AlternateAttires.Apply("gorilla_render.py", new[] { job.Entry.Folder, "same-taunt.npz", "output", "slot=" + LogicalId },
                BatchRenderPlan.AliasAttirePath(job, false));
            Check(arguments[0] == job.Entry.Folder && arguments[1] == "same-taunt.npz" && arguments.Contains("slot=" + LogicalId)
                && arguments.Last() == "attire=" + Attire,
                "An unnumbered selected pack survives fresh proof and retains its exact attire and logical render slot");
        }
        var mixedSources = new[] { sources[0], fixture.Inventory[1] };
        records = ModelSourceAssociations.Replace(records, entry, fixture.Table, mixedSources);
        BatchRenderPlan mixed = BatchRenderPlan.CreateWithAttires(new[] { entry }, mixedSources, fixture.Table, null, records, fixture.Table);
        Check(mixed.Jobs.Select(job => job.Entry.SourceId).OrderBy(id => id).SequenceEqual(new[] { 1202, LogicalId }.OrderBy(id => id)),
            "A mixed numbered and unnumbered selection preserves the known physical source while falling back only for the unknown pack");
        foreach (BatchRenderJob job in mixed.Jobs) BatchRenderPlan.RefreshDiscoveryAttires(job, mixedSources, fixture.Table, records, fixture.Table);
        Check(records.SelectMany(record => record.Sources).Single(source => source.Folder == first).SourceId == 0,
            "Rendering fallback never rewrites the explicit source association's unknown physical metadata");
    }

    private static void MultipleSelectedPacks()
    {
        Fixture fixture = MakeFixture("two-explicit-routing-packs");
        List<ModelSourceAssociation> records = Associate(fixture);
        string saved = Path.Combine(fixture.Directory, "selected-models.json");
        ModelSourceAssociations.Save(saved, records);
        records = ModelSourceAssociations.Load(saved);
        Check(ModelSourceAssociations.Sources(records, fixture.Entry, fixture.Table).Count == 2,
            "Two explicitly selected complete filesystem packs survive association save and reload");
        DiscoveryAttireCatalogResult catalog = BatchRenderPlan.FindAttires(fixture.Entry, fixture.Inventory, fixture.Table);
        Check(catalog.References.Count == 0 && catalog.Sources.Count == 0,
            "The routing fixture has no Discovery costume keys to authorize either model implicitly");
        List<BatchAttireSelection> available = BatchRenderPlan.AvailableAttires(fixture.Entry, catalog, records, fixture.Table);
        Check(available.Count == 2 && available.All(choice => choice.IsAvailable && string.IsNullOrWhiteSpace(choice.DiscoveryKey)),
            "Both associated complete packs appear as available unkeyed attire sources");
        Check(available.Select(choice => choice.SourceFolder).OrderBy(path => path).SequenceEqual(new[] { fixture.First, fixture.Second }.OrderBy(path => path)),
            "Only the two explicitly associated roots appear despite a same-name decoy in the inventory");
        List<BatchAttireSelection> defaults = BatchRenderPlan.DefaultAssociatedAttires(fixture.Entry, catalog, records, fixture.Table);
        Check(defaults.Count == 2 && defaults.All(choice => choice.RelativePath == Attire),
            "Automatic two-pack batching chooses one existing default MCD per selected pack");
        BatchRenderPlan automatic = BatchRenderPlan.CreateWithAttires(new[] { fixture.Entry }, fixture.Inventory, fixture.Table, null, records, fixture.Table);
        Check(automatic.Jobs.Count == 2 && automatic.Jobs.All(job => job.HasExplicitAttire && job.Entry.Id == LogicalId)
            && automatic.Jobs.Select(job => job.Entry.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Starting an associated wrestler without explicit attire choices automatically schedules both complete packs");
        RosterEntry loaded = fixture.Entry; loaded.Folder = fixture.First; loaded.SourceId = 1201;
        BatchRenderPlan loadedAutomatic = BatchRenderPlan.CreateWithAttires(new[] { loaded }, fixture.Inventory, fixture.Table, null, records, fixture.Table);
        Check(loadedAutomatic.Jobs.Count == 2 && loadedAutomatic.Jobs.Count(job => job.Entry.Folder == fixture.First) == 1
            && loadedAutomatic.Jobs.Count(job => job.Entry.Folder == fixture.Second) == 1,
            "An already loaded primary model does not duplicate or hide the explicitly associated second pack");
        List<ModelCandidate> limitedInventory = fixture.Inventory.Where(model => model.Folder != fixture.Second).ToList();
        DiscoveryAttireCatalogResult limitedCatalog = BatchRenderPlan.FindAttires(fixture.Entry, limitedInventory, fixture.Table);
        List<BatchAttireSelection> limitedAvailable = BatchRenderPlan.AvailableAttires(fixture.Entry, limitedCatalog, records, fixture.Table);
        BatchRenderPlan withoutInventoryMatch = BatchRenderPlan.CreateWithAttires(new[] { fixture.Entry }, limitedInventory, fixture.Table, null, records, fixture.Table);
        Check(limitedAvailable.Count == 2 && withoutInventoryMatch.Jobs.Count == 2
            && withoutInventoryMatch.Jobs.Any(job => job.Entry.Folder == fixture.Second && job.Entry.SourceId == 1202
                && File.ReadAllBytes(Path.Combine(job.Entry.Folder, "BaseModel", "baseModel.mcd"))[0] == 2),
            "A fresh explicit second-pack association remains available with its own body when ordinary inventory omits it");
        BatchRenderPlan checkedOne = Plan(fixture, new List<BatchAttireSelection> { defaults.Single(choice => choice.SourceFolder == fixture.Second) }, records);
        Check(checkedOne.Jobs.Count == 1 && checkedOne.Jobs.Single().Entry.Folder == fixture.Second,
            "Checking one explicit pack overrides automatic expansion to both associated packs");
        var before = Snapshot(fixture.Directory);
        defaults[0].SourceId = 65530; defaults[1].SourceId = 65531;
        RosterEntry original = fixture.Entry;
        BatchRenderPlan plan = Plan(fixture, defaults, records, original);
        Check(plan.Jobs.Count == 2 && plan.Skipped.Count == 0,
            "Two associated packs create two jobs for the same logical wrestler without a primary-model prompt");
        Check(plan.Jobs.All(job => job.Entry.Id == LogicalId && job.HasExplicitAttire && !job.ExtractFromGame),
            "Both selected model jobs retain the same logical installation slot");
        Check(plan.Jobs.Select(job => job.Entry.SourceId).OrderBy(id => id).SequenceEqual(new[] { 1201, 1202 }),
            "Each job derives its physical source ID from its explicit pack instead of forged choice IDs");
        Check(plan.Jobs.Select(job => job.Entry.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2
            && plan.Jobs.All(job => job.AttireSourceRelativePath == Attire),
            "Identical relative attire filenames stay separate because their complete source roots differ");
        foreach (BatchRenderJob job in plan.Jobs) {
            byte marker = job.Entry.SourceId == 1201 ? (byte)1 : (byte)2;
            Check(File.ReadAllBytes(Path.Combine(job.Entry.Folder, "BaseModel", "baseModel.mcd"))[0] == marker
                && File.ReadAllBytes(Path.Combine(job.Entry.Folder, "Textures", "body.dds"))[0] == marker
                && File.ReadAllBytes(Path.Combine(job.Entry.Folder, Attire))[0] == marker,
                "The complete body, textures and attire remain together for physical routing source " + job.Entry.SourceId);
            string[] arguments = AlternateAttires.Apply("gorilla_render.py", new[] {
                job.Entry.Folder, "17330a000.npz", "output", "slot=" + LogicalId, "end=loop", "screens=matchcard"
            }, BatchRenderPlan.AliasAttirePath(job, false));
            Check(arguments[0] == job.Entry.Folder && arguments[1] == "17330a000.npz" && arguments.Contains("slot=" + LogicalId)
                && arguments.Last() == "attire=" + Attire,
                "The shared taunt and logical slot use the exact selected pack for physical routing source " + job.Entry.SourceId);
            BatchRenderPlan.RefreshDiscoveryAttires(job, fixture.Inventory, fixture.Table, records, fixture.Table);
        }
        Check(plan.Jobs.Select(BatchRenderPlan.WorkspaceName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Separate selected packs receive distinct batch workspaces");
        string output = Path.Combine(root, "ordinary-output");
        foreach (BatchRenderJob job in plan.Jobs) {
            job.OutputDirectory = Path.Combine(root, "workspaces", BatchRenderPlan.WorkspaceName(job));
            job.RenderPath = Path.Combine(job.OutputDirectory, LogicalId + "_Will_Ospreay.gmr");
            Write(job.RenderPath, Render((byte)job.Entry.SourceId));
            string published = BatchRenderPlan.PublishCompleted(job, output), reason;
            Check(BatchRenderPlan.VerifyCompleted(published, LogicalId, output, out reason)
                && File.ReadAllBytes(published).SequenceEqual(File.ReadAllBytes(job.RenderPath)),
                "Publication preserves the completed routing-fixture bytes and logical metadata for source " + job.Entry.SourceId);
        }
        Check(plan.Jobs.Select(job => job.PublishedRenderPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Both selected pack outputs coexist in the ordinary output directory");
        Check(Unchanged(before, fixture.Directory), "Association lookup, planning and publication preserve both complete source packs and roster bytes");
        Check(original.Folder == null && original.SourceId == 0, "Planning does not rewrite the original roster association");
        Check(BatchRenderPlan.AvailableAttires(fixture.Entry, catalog).Count == 0,
            "Removing explicit associations removes availability when no Discovery mapping or current model authorizes the roots");
    }

    private static void AssociationValidation()
    {
        foreach (string mutation in new[] { "revoked", "other-context", "other-persona", "missing-mcd", "missing-source", "logical-id", "physical-id", "record-physical-id", "source-folder", "relative-path" }) {
            Fixture fixture = MakeFixture("association-refresh-" + mutation);
            List<ModelSourceAssociation> records = Associate(fixture);
            DiscoveryAttireCatalogResult catalog = BatchRenderPlan.FindAttires(fixture.Entry, fixture.Inventory, fixture.Table);
            List<BatchAttireSelection> selected = BatchRenderPlan.DefaultAssociatedAttires(fixture.Entry, catalog, records, fixture.Table);
            BatchRenderJob job = Plan(fixture, selected, records).Jobs.Single(item => item.Entry.SourceId == 1202);
            string table = fixture.Table, context = fixture.Table;
            switch (mutation) {
                case "revoked": records = ModelSourceAssociations.Replace(records, fixture.Entry, fixture.Table, new ModelCandidate[0]); break;
                case "other-context":
                    table = Path.Combine(fixture.Directory, "OtherDiscovery", "Roster", "CharProfileTable.jsfb"); WriteEmptyTable(table);
                    context = table; job.Entry.RosterTable = table; break;
                case "other-persona": job.Entry.Name = "Another wrestler persona"; break;
                case "missing-mcd": File.Delete(Path.Combine(fixture.Second, Attire)); break;
                case "missing-source": System.IO.Directory.Delete(fixture.Second, true); break;
                case "logical-id": job.Entry.Id = LogicalId + 1; break;
                case "physical-id": job.Entry.SourceId = 1203; break;
                case "record-physical-id": records.SelectMany(record => record.Sources).Single(source => source.Folder == fixture.Second).SourceId = 1203; break;
                case "source-folder": job.Entry.Folder = fixture.Unselected; break;
                case "relative-path": job.AttireSourceRelativePath = "../1203_Unselected_Other_Model/Default_Attire/Attire.mcd"; break;
            }
            Reject(() => BatchRenderPlan.RefreshDiscoveryAttires(job, fixture.Inventory, table, records, context),
                "A selected-model job rejects stale or changed association evidence: " + mutation);
        }
        Fixture unselected = MakeFixture("unselected-root-rejection");
        List<ModelSourceAssociation> allowed = Associate(unselected);
        Reject(() => Plan(unselected, new List<BatchAttireSelection> {
            new BatchAttireSelection { SourceFolder = unselected.Unselected, SourceId = 1201, RelativePath = Attire, DisplayName = "Same-name unselected model" }
        }, allowed), "A same-name unselected source cannot borrow another explicit association");
        Reject(() => Plan(unselected, new List<BatchAttireSelection> {
            new BatchAttireSelection { SourceFolder = unselected.Second, SourceId = 1202, RelativePath = Attire, DisplayName = "Unapproved complete pack" }
        }, null), "A complete filesystem pack needs an explicit association before cross-source batching");
        var different = unselected.Entry; different.RosterTable = Path.Combine(unselected.Directory, "Other", "Roster", "CharProfileTable.jsfb"); WriteEmptyTable(different.RosterTable);
        Check(ModelSourceAssociations.Sources(allowed, different, different.RosterTable).Count == 0,
            "The same logical ID in another roster cannot inherit these selected model roots");
        different = unselected.Entry; different.Id++;
        Check(ModelSourceAssociations.Sources(allowed, different, unselected.Table).Count == 0,
            "A different logical ID cannot inherit the selected model roots");
        different = unselected.Entry; different.Name = "Other wrestler";
        Check(ModelSourceAssociations.Sources(allowed, different, unselected.Table).Count == 0,
            "A different roster persona cannot inherit the selected model roots");
        Fixture missing = MakeFixture("missing-selected-pack-automatic");
        List<ModelSourceAssociation> missingRecords = Associate(missing);
        System.IO.Directory.Delete(missing.Second, true);
        Reject(() => BatchRenderPlan.CreateWithAttires(new[] { missing.Entry }, missing.Inventory, missing.Table, null, missingRecords, missing.Table),
            "Automatic batching rejects a missing selected second pack instead of silently reducing two packs to one");
    }

    private static List<ModelSourceAssociation> Associate(Fixture fixture)
    { return ModelSourceAssociations.Replace(new List<ModelSourceAssociation>(), fixture.Entry, fixture.Table, fixture.Inventory.Take(2)); }
    private static BatchRenderPlan Plan(Fixture fixture, List<BatchAttireSelection> choices, IEnumerable<ModelSourceAssociation> associations, RosterEntry entry = null)
    { return BatchRenderPlan.CreateWithAttires(new[] { entry ?? fixture.Entry }, fixture.Inventory, fixture.Table,
        new Dictionary<int, List<BatchAttireSelection>> { { LogicalId, choices } }, associations, fixture.Table); }

    private sealed class Fixture
    {
        internal string Directory, Table, First, Second, Unselected;
        internal List<ModelCandidate> Inventory;
        internal RosterEntry Entry { get { return new RosterEntry { Id = LogicalId, Name = "Will Ospreay", RosterTable = Table }; } }
    }

    private static Fixture MakeFixture(string name)
    {
        string folder = Path.Combine(root, name), table = Path.Combine(folder, "Discovery", "Roster", "CharProfileTable.jsfb");
        WriteEmptyTable(table);
        string first = MakePack(folder, "1201_Will_Ospreay_First", 1), second = MakePack(folder, "1202_Will_Ospreay_Second", 2);
        string unselected = MakePack(folder, "1203_Unselected_Other_Model", 3);
        return new Fixture { Directory = folder, Table = table, First = first, Second = second, Unselected = unselected,
            Inventory = new List<ModelCandidate> { new ModelCandidate { Folder = first, SourceId = 1201, Name = "First Will Ospreay model" },
                new ModelCandidate { Folder = second, SourceId = 1202, Name = "Second Will Ospreay model" },
                new ModelCandidate { Folder = unselected, SourceId = 1203, Name = "Will Ospreay same-name decoy" } } };
    }

    private static string MakePack(string parent, string name, byte marker)
    {
        string folder = Path.Combine(parent, "Models", name);
        Write(Path.Combine(folder, Attire), new[] { marker, (byte)2, (byte)3, (byte)4 });
        Write(Path.Combine(folder, "BaseModel", "baseModel.mcd"), new[] { marker, (byte)10, (byte)20 });
        Write(Path.Combine(folder, "Textures", "body.dds"), new[] { marker, (byte)30, (byte)40 });
        return folder;
    }

    private static void WriteEmptyTable(string path)
    {
        using (var data = new MemoryStream()) using (var output = new BinaryWriter(data)) {
            output.Write((uint)14); output.Write(Encoding.ASCII.GetBytes("Char"));
            output.Write((ushort)6); output.Write((ushort)8); output.Write((ushort)4);
            output.Write(6); output.Write((uint)4); output.Write((uint)0); Write(path, data.ToArray());
        }
    }
    private static byte[] Render(byte marker)
    {
        using (var data = new MemoryStream()) using (var output = new BinaryWriter(data)) {
            output.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint value in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) output.Write(value);
            output.Write(Enumerable.Repeat(marker, 8).ToArray());
            byte[] metadata = Encoding.UTF8.GetBytes("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":" + LogicalId + "}");
            output.Write(metadata); output.Write((uint)metadata.Length); output.Write(Encoding.ASCII.GetBytes("GMR1")); return data.ToArray();
        }
    }
    private static void Write(string path, byte[] data) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, data); }
    private static Dictionary<string, byte[]> Snapshot(string path) { return System.IO.Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(file => file.Substring(path.Length), File.ReadAllBytes); }
    private static bool Unchanged(Dictionary<string, byte[]> before, string path) { var after = Snapshot(path); return before.Count == after.Count && before.All(pair => after.ContainsKey(pair.Key) && pair.Value.SequenceEqual(after[pair.Key])); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); passed++; Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message) {
        try { action(); } catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is IOException || error is InvalidDataException) { Check(true, message); return; }
        throw new InvalidOperationException("Expected rejection: " + message);
    }
}
