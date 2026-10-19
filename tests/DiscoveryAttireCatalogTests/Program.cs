using System;
using System.Collections.Generic;
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

    private static int Main(string[] args)
    {
        temporary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "gorilla-discovery-attires-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Fixture split = DemonFixture("split", false);
            Check("The record reader retains the explicit costume key and optional zero index", () => {
                List<CharacterMappingRecord> rows = CharacterMappingReader.ReadRecords(split.MappingPath);
                Equal(4, rows.Count);
                CharacterMappingRecord normal = rows.Single(row => row.LogicalId == 626 && row.CostumeIndex == 0);
                Equal(0xFE1B1EB5U, normal.CostumeKey);
                Equal(Hash("characters/OriginalDemon/Default_Attire/Attire.mcd"), normal.ModelHash);
            });
            Check("The original hash-to-logical-ID reader retains its established behavior", () => {
                Dictionary<ulong, List<int>> index = CharacterMappingReader.Read(split.MappingPath);
                Equal(true, index[Hash("characters/OriginalDemon/Default_Attire/Attire.mcd")].Contains(626));
                Equal(true, index[Hash("characters/RegularFinn/Default_Attire/Attire.mcd")].Contains(576));
            });
            Check("Only the two explicit Demon profile attires are referenced", () => {
                List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(split.Table, 626);
                Equal(2, refs.Count);
                Equal("0,1", string.Join(",", refs.Select(item => item.CostumeIndex).OrderBy(index => index)));
                Equal(true, refs.All(item => item.LogicalId == 626 && item.MappingId == 626));
            });
            Check("Localized attire names come from the explicit entry's SDB key", () => {
                List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(split.Table, 626);
                Equal("\"DEMON\" FINN BÁLOR", refs.Single(item => item.CostumeIndex == 0).DisplayName);
                Equal("CARNAGE", refs.Single(item => item.CostumeIndex == 1).DisplayName);
            });
            Check("The profile's explicit default costume key is retained", () => {
                List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(split.Table, 626);
                Equal(1, refs.Count(item => item.IsDefault));
                Equal(1, refs.Single(item => item.IsDefault).CostumeIndex);
            });
            Check("Distinct extracted attire folders resolve through exact model-path hashes", () => {
                DiscoveryAttireCatalogResult result = Find(split, 626);
                Equal(2, result.Sources.Count);
                Equal("0,1", string.Join(",", result.Sources.Select(item => item.CostumeIndex).OrderBy(index => index)));
                Equal(true, result.Sources.All(item => item.LogicalId == 626 && item.MappingId == 626));
            });
            Check("A shared BaseId never mixes regular Finn or an unlisted index-five model into Demon", () => {
                DiscoveryAttireCatalogResult result = Find(split, 626);
                Equal(false, result.Sources.Any(item => item.CostumeIndex == 5 || item.MappingId == 576));
                Equal(false, result.Sources.Any(item => item.RelativePath.IndexOf("RegularFinn", StringComparison.OrdinalIgnoreCase) >= 0));
                Equal(1, Find(split, 576).Sources.Count);
            });
            Check("Every resolved source uses an existing contained relative MCD path", () => {
                foreach (DiscoveryAttireSource source in Find(split, 626).Sources) AssertContainedSource(source);
            });
            Check("Two explicit models inside the same source folder remain separate choices", () => {
                Fixture same = DemonFixture("same-folder", true);
                DiscoveryAttireCatalogResult result = Find(same, 626);
                Equal(2, result.Sources.Count);
                Equal(2, result.Sources.Select(SourceIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                foreach (DiscoveryAttireSource source in result.Sources) AssertContainedSource(source);
            });
            Check("Repeated mapping records and candidate paths do not duplicate an attire", () => {
                Fixture duplicate = DemonFixture("duplicates", false);
                duplicate.Mappings.Add(duplicate.Mappings[0]); duplicate.Mappings.Add(duplicate.Mappings[1]);
                duplicate.Save();
                duplicate.Candidates.Add(duplicate.Candidates[0]);
                duplicate.Candidates.Add(new ModelCandidate { Folder = duplicate.Candidates[0].Folder + Path.DirectorySeparatorChar, Name = "Duplicate path" });
                Equal(2, Find(duplicate, 626).Sources.Count);
                Equal(2, DiscoveryAttireCatalog.ReadReferences(duplicate.Table, 626).Count);
            });
            Check("An unavailable explicit attire stays visible as an unresolved reference", () => {
                Fixture unavailable = DemonFixture("unavailable", false);
                foreach (string model in Directory.GetFiles(unavailable.Root, "*.mcd", SearchOption.AllDirectories)) File.Delete(model);
                DiscoveryAttireCatalogResult result = Find(unavailable, 626);
                Equal(2, result.References.Count);
                Equal(0, result.Sources.Count);
                Equal(true, result.Issues.Count > 0 || result.References.Any(item => !string.IsNullOrWhiteSpace(item.Issue)));
            });
            Check("A missing model hash is not silently replaced with another attire", () => {
                Fixture missing = DemonFixture("missing-hash", false);
                missing.Mappings[1].Hash = 0; missing.Save();
                DiscoveryAttireCatalogResult result = Find(missing, 626);
                Equal(2, result.References.Count);
                Equal(1, result.Sources.Count);
                Equal(0, result.Sources.Single().CostumeIndex);
            });
            Check("A matching wrestler name and numeric prefix cannot replace a different hash", () => {
                Fixture wrong = DemonFixture("name-is-not-proof", false);
                wrong.Mappings[0].Hash = Hash("characters/SomeOtherPack/Default_Attire/Attire.mcd");
                wrong.Mappings[1].Hash = Hash("characters/AnotherPack/Default_Attire/Attire.mcd"); wrong.Save();
                foreach (ModelCandidate candidate in wrong.Candidates) { candidate.Name = "\"DEMON\" FINN BÁLOR"; candidate.SourceId = 626; }
                Equal(0, Find(wrong, 626).Sources.Count);
            });
            Check("The explicit owner ID wins when a costume key is shared by other mapping IDs", () => {
                Fixture owner = Minimal("owner-key");
                owner.Profiles.Add(Profile(626, 576, Attire(10, "Owner", 100U, 0)));
                owner.Costumes.Add(Costume(576, 100U, 0));
                string right = owner.Model("OwnerModel/Default_Attire/Attire.mcd");
                string other = owner.Model("OtherModel/Default_Attire/Attire.mcd");
                owner.Mappings.Add(Mapping(626, 100U, 0, right)); owner.Mappings.Add(Mapping(576, 100U, 0, other)); owner.Save();
                DiscoveryAttireReference reference = DiscoveryAttireCatalog.ReadReferences(owner.Table, 626).Single();
                Equal(626, reference.MappingId); Equal(Hash("characters/" + right), reference.ModelHash);
                Equal(1, Find(owner, 626).Sources.Count);
            });
            Check("A unique explicit costume remap preserves the logical owner separately", () => {
                Fixture remap = Minimal("unique-remap");
                remap.Profiles.Add(Profile(626, 576, Attire(10, "Remapped attire", 100U, 0)));
                remap.Costumes.Add(Costume(777, 100U, 3));
                string path = remap.Model("RemappedModel/Default_Attire/Attire.mcd");
                remap.Mappings.Add(Mapping(777, 100U, 3, path)); remap.Save();
                DiscoveryAttireReference reference = DiscoveryAttireCatalog.ReadReferences(remap.Table, 626).Single();
                Equal(626, reference.LogicalId); Equal(777, reference.MappingId); Equal(3, reference.CostumeIndex);
                Equal(1, Find(remap, 626).Sources.Count);
            });
            Check("An ambiguous shared costume remap remains unresolved", () => {
                Fixture ambiguous = Minimal("ambiguous-remap");
                ambiguous.Profiles.Add(Profile(626, 576, Attire(10, "Ambiguous attire", 100U, 0)));
                ambiguous.Costumes.Add(Costume(777, 100U, 0)); ambiguous.Costumes.Add(Costume(888, 100U, 0));
                ambiguous.Mappings.Add(Mapping(777, 100U, 0, ambiguous.Model("First/Default_Attire/Attire.mcd")));
                ambiguous.Mappings.Add(Mapping(888, 100U, 0, ambiguous.Model("Second/Default_Attire/Attire.mcd"))); ambiguous.Save();
                DiscoveryAttireCatalogResult result = Find(ambiguous, 626);
                Equal(0, result.Sources.Count);
                Equal(true, result.Issues.Count > 0 || result.References.Any(item => !string.IsNullOrWhiteSpace(item.Issue)));
            });
            Check("An owner-key mapping remains usable when its optional costume row is absent", () => {
                Fixture absent = Minimal("absent-costume-row");
                absent.Profiles.Add(Profile(626, 576, Attire(10, "Owner evidence", 100U, 0)));
                absent.Mappings.Add(Mapping(626, 100U, 2, absent.Model("OwnerEvidence/Default_Attire/Attire.mcd"))); absent.Save();
                DiscoveryAttireReference reference = DiscoveryAttireCatalog.ReadReferences(absent.Table, 626).Single();
                Equal(626, reference.MappingId); Equal(2, reference.CostumeIndex); Equal(true, !string.IsNullOrWhiteSpace(reference.Warning)); Equal(true, string.IsNullOrWhiteSpace(reference.Issue));
                DiscoveryAttireCatalogResult result = Find(absent, 626);
                Equal(1, result.Sources.Count); Equal(true, !string.IsNullOrWhiteSpace(result.Sources.Single().Warning)); Equal(true, result.Issues.Count > 0);
            });
            Check("Missing costume evidence cannot choose between different owner-key mappings", () => {
                Fixture ambiguous = Minimal("ambiguous-owner-without-costume"); ambiguous.Profiles.Add(Profile(626, 576, Attire(10, "Ambiguous owner", 100U, 0)));
                ambiguous.Mappings.Add(Mapping(626, 100U, 0, ambiguous.Model("FirstOwner/Default_Attire/Attire.mcd")));
                ambiguous.Mappings.Add(Mapping(626, 100U, 1, ambiguous.Model("SecondOwner/Default_Attire/Attire.mcd"))); ambiguous.Save();
                DiscoveryAttireCatalogResult result = Find(ambiguous, 626);
                Equal(0, result.Sources.Count); Equal(true, result.References.Any(item => !string.IsNullOrWhiteSpace(item.Issue))); Equal(true, result.Issues.Count > 0);
            });
            Check("A cross-owner mapping needs an explicit costume relationship", () => {
                Fixture foreign = Minimal("foreign-without-costume"); foreign.Profiles.Add(Profile(626, 576, Attire(10, "Unproven remap", 100U, 0)));
                foreign.Mappings.Add(Mapping(777, 100U, 0, foreign.Model("ForeignOwner/Default_Attire/Attire.mcd"))); foreign.Save();
                DiscoveryAttireCatalogResult result = Find(foreign, 626);
                Equal(0, result.Sources.Count); Equal(true, result.References.Any(item => !string.IsNullOrWhiteSpace(item.Issue)));
            });
            Check("Mapping variant comes from the costume reference rather than the optional UI byte", () => {
                Fixture index = Minimal("variant-six");
                index.Profiles.Add(Profile(927, 927, Attire(10, "Alternate", 100U, 0)));
                index.Costumes.Add(Costume(927, 100U, 6));
                index.Mappings.Add(Mapping(927, 100U, 6, index.Model("Raquel/Default_Attire/Attire.mcd"))); index.Save();
                Equal(6, DiscoveryAttireCatalog.ReadReferences(index.Table, 927).Single().CostumeIndex);
            });
            Check("Two explicit costume keys sharing one MCD remain distinct attire choices", () => {
                Fixture shared = Minimal("shared-geometry");
                shared.Profiles.Add(Profile(927, 927, Attire(10, "Normal", 100U, 0), Attire(11, "Alternate", 101U, 0)));
                shared.Costumes.Add(Costume(927, 100U, 0)); shared.Costumes.Add(Costume(927, 101U, 6));
                string path = shared.Model("Raquel/Default_Attire/Attire.mcd");
                shared.Mappings.Add(Mapping(927, 100U, 0, path)); shared.Mappings.Add(Mapping(927, 101U, 6, path)); shared.Save();
                DiscoveryAttireCatalogResult result = Find(shared, 927);
                Equal(2, result.References.Count); Equal(2, result.Sources.Count);
                Equal(2, result.Sources.Select(item => item.CostumeKey).Distinct().Count()); Equal(1, result.Sources.Select(item => item.ModelHash).Distinct().Count());
            });
            Check("An absent default reference uses the first explicit profile attire", () => {
                Fixture defaults = DemonFixture("default-first", false); defaults.Profiles[0].DefaultKey = null; defaults.Save();
                Equal(0, DiscoveryAttireCatalog.ReadReferences(defaults.Table, 626).Single(item => item.IsDefault).CostumeIndex);
            });
            Check("A different roster context cannot contribute model sources", () => {
                var entry = new RosterEntry { Id = 626, RosterTable = Path.Combine(temporary, "other", "CharProfileTable.jsfb") };
                Equal(0, DiscoveryAttireCatalog.Find(entry, split.Candidates, split.Table, CancellationToken.None).Sources.Count);
            });
            Check("A canceled model search stops before scanning or resolving candidates", () => {
                using (var cancel = new CancellationTokenSource()) {
                    cancel.Cancel(); Throws<OperationCanceledException>(() => DiscoveryAttireCatalog.Find(new RosterEntry { Id = 626 }, split.Candidates, split.Table, cancel.Token));
                }
            });
            Check("A nested directory link cannot donate an outside model", () => {
                Fixture linked = Minimal("linked-directory"); linked.Profiles.Add(Profile(626, 576, Attire(10, "Linked", 100U, 0))); linked.Costumes.Add(Costume(626, 100U, 0));
                string candidate = Directory.CreateDirectory(Path.Combine(linked.Root, "Characters", "Demon")).FullName;
                string outside = Directory.CreateDirectory(Path.Combine(temporary, "outside-directory")).FullName; File.WriteAllText(Path.Combine(outside, "Attire.mcd"), "fixture");
                Directory.CreateSymbolicLink(Path.Combine(candidate, "Default_Attire"), outside);
                linked.Candidates.Add(new ModelCandidate { Folder = candidate }); linked.Mappings.Add(Mapping(626, 100U, 0, "Demon/Default_Attire/Attire.mcd")); linked.Save();
                Equal(0, Find(linked, 626).Sources.Count);
            });
            Check("A linked MCD file cannot become an attire source", () => {
                Fixture linked = Minimal("linked-file"); linked.Profiles.Add(Profile(626, 576, Attire(10, "Linked file", 100U, 0))); linked.Costumes.Add(Costume(626, 100U, 0));
                string candidate = Directory.CreateDirectory(Path.Combine(linked.Root, "Characters", "Demon", "Default_Attire")).FullName;
                string outside = Path.Combine(temporary, "outside-model.bin"); File.WriteAllText(outside, "fixture");
                File.CreateSymbolicLink(Path.Combine(candidate, "Attire.mcd"), outside);
                linked.Candidates.Add(new ModelCandidate { Folder = Directory.GetParent(candidate).FullName }); linked.Mappings.Add(Mapping(626, 100U, 0, "Demon/Default_Attire/Attire.mcd")); linked.Save();
                Equal(0, Find(linked, 626).Sources.Count);
            });
            Check("A linked candidate root cannot escape the selected model collection", () => {
                Fixture linked = DemonFixture("linked-root", false);
                ModelCandidate original = linked.Candidates[0]; string link = Path.Combine(temporary, "linked-root-candidate"); Directory.CreateSymbolicLink(link, original.Folder);
                linked.Candidates.Clear(); linked.Candidates.Add(new ModelCandidate { Folder = link });
                Equal(0, Find(linked, 626).Sources.Count);
            });
            Check("A parent-traversal path hash does not make an outside sibling eligible", () => {
                Fixture escape = Minimal("escape-hash"); escape.Profiles.Add(Profile(626, 576, Attire(10, "Escape", 100U, 0))); escape.Costumes.Add(Costume(626, 100U, 0));
                escape.Model("Demon/Default_Attire/Attire.mcd"); escape.Mappings.Add(new MapRow { Id = 626, Key = 100U, Index = 0, Hash = Hash("characters/../Demon/Default_Attire/Attire.mcd") }); escape.Save();
                Equal(0, Find(escape, 626).Sources.Count);
            });
            Check("A newly added explicit attire is discovered by the next read", () => {
                Fixture added = DemonFixture("growth", false);
                Equal(2, Find(added, 626).Sources.Count);
                added.Profiles[0].Attires.Add(Attire(500, "New attire", 500U, 2)); added.Costumes.Add(Costume(626, 500U, 2));
                added.Mappings.Add(Mapping(626, 500U, 2, added.Model("NewDemon/Default_Attire/Attire.mcd"))); added.Save();
                Equal(3, Find(added, 626).Sources.Count);
                Equal("New attire", DiscoveryAttireCatalog.ReadReferences(added.Table, 626).Single(item => item.CostumeIndex == 2).DisplayName);
            });
            Check("A plain JSON or manually selected wrestler needs no Discovery companion table", () => {
                DiscoveryAttireCatalogResult result = DiscoveryAttireCatalog.Find(new RosterEntry { Id = 626 }, split.Candidates, null, CancellationToken.None);
                Equal(0, result.References.Count); Equal(0, result.Sources.Count); Equal(0, result.Issues.Count);
            });
            Check("A saved roster context supplies an omitted explicit table argument", () => {
                DiscoveryAttireCatalogResult result = DiscoveryAttireCatalog.Find(new RosterEntry { Id = 626, RosterTable = split.Table }, split.Candidates, null, CancellationToken.None);
                Equal(2, result.References.Count); Equal(2, result.Sources.Count);
            });
            Check("A missing costume companion uses exact owner-key mappings with visible warnings", () => {
                Fixture missing = DemonFixture("missing-costumes", false); File.Delete(Path.Combine(missing.Root, "Characters", "CostumesTable.jsfb"));
                DiscoveryAttireCatalogResult result = Find(missing, 626);
                Equal(2, result.References.Count); Equal(2, result.Sources.Count); Equal(true, result.Issues.Count > 0);
                Equal(true, result.References.All(item => !string.IsNullOrWhiteSpace(item.Warning) && string.IsNullOrWhiteSpace(item.Issue)));
                Equal(true, result.Sources.All(item => !string.IsNullOrWhiteSpace(item.Warning)));
            });
            Check("A missing mapping companion leaves explicit attires unresolved", () => {
                Fixture missing = DemonFixture("missing-mapping", false); File.Delete(missing.MappingPath);
                DiscoveryAttireCatalogResult result = Find(missing, 626);
                Equal(2, result.References.Count); Equal(0, result.Sources.Count); Equal(true, result.Issues.Count > 0);
            });
            Check("A damaged profile reports an actionable Find issue without interrupting model loading", () => {
                Fixture invalid = DemonFixture("invalid-profile", false); File.WriteAllText(invalid.Table, "damaged");
                DiscoveryAttireCatalogResult result = Find(invalid, 626);
                Equal(0, result.Sources.Count); Equal(true, result.Issues.Count > 0);
                Throws<InvalidDataException>(() => DiscoveryAttireCatalog.ReadReferences(invalid.Table, 626));
            });
            Check("A damaged mapping reports an issue while the strict metadata API rejects it", () => {
                Fixture invalid = DemonFixture("invalid-mapping", false); File.WriteAllText(invalid.MappingPath, "damaged");
                DiscoveryAttireCatalogResult result = Find(invalid, 626);
                Equal(0, result.Sources.Count); Equal(true, result.Issues.Count > 0);
                Throws<InvalidDataException>(() => DiscoveryAttireCatalog.ReadReferences(invalid.Table, 626));
            });
            Check("A damaged costume companion reports an issue while the strict API rejects it", () => {
                Fixture invalid = DemonFixture("invalid-costumes", false); File.WriteAllText(Path.Combine(invalid.Root, "Characters", "CostumesTable.jsfb"), "damaged");
                DiscoveryAttireCatalogResult result = Find(invalid, 626);
                Equal(0, result.Sources.Count); Equal(true, result.Issues.Count > 0);
                Throws<InvalidDataException>(() => DiscoveryAttireCatalog.ReadReferences(invalid.Table, 626));
            });
            Check("A missing optional SDB uses readable fallback labels while retaining exact models", () => {
                Fixture missing = DemonFixture("missing-sdb", false); File.Delete(Path.Combine(missing.Root, "Sdb", "ENG.sdb"));
                DiscoveryAttireCatalogResult result = Find(missing, 626);
                Equal(2, result.Sources.Count); Equal(true, result.References.All(item => !string.IsNullOrWhiteSpace(item.DisplayName)));
            });
            ActualData(args.Length == 0 ? "/workspace/discovery-work/discovery_data" : args[0]);
            Console.WriteLine((failed == 0 ? "PASS " : "FAIL ") + passed + " Discovery attire catalog checks" + (failed == 0 ? "" : "; " + failed + " failed"));
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(temporary, true); } catch (IOException) { } }
    }

    private static void ActualData(string root)
    {
        string table = Path.Combine(root, "Roster", "CharProfileTable.jsfb");
        if (!File.Exists(table)) throw new FileNotFoundException("Pass the actual uploaded Discovery data root to run the required real-data checks.", table);
        Check("Actual Demon data lists exactly the original and CARNAGE attires", () => {
            List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(table, 626);
            Equal(2, refs.Count); Equal("0,1", string.Join(",", refs.Select(item => item.CostumeIndex).OrderBy(index => index)));
            Equal("CARNAGE", refs.Single(item => item.CostumeIndex == 1).DisplayName);
            Equal(0x505521EC1E7A4A26UL, refs.Single(item => item.CostumeIndex == 0).ModelHash);
            Equal(0xA91342496A944BE3UL, refs.Single(item => item.CostumeIndex == 1).ModelHash);
        });
        Check("Actual regular Finn remains a separate logical profile", () => {
            List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(table, 576);
            Equal(1, refs.Count); Equal(576, refs.Single().MappingId); Equal(0xFC55B17D82B7225AUL, refs.Single().ModelHash);
        });
        Check("Actual CM Punk names all eleven attires and keeps PINK APRIL as the explicit default", () => {
            List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(table, 312);
            Equal(11, refs.Count); Equal("PINK APRIL", refs.Single(item => item.IsDefault).DisplayName); Equal(9, refs.Single(item => item.IsDefault).CostumeIndex);
        });
        Check("Actual Raquel preserves the distinct zero and six costume references", () => {
            List<DiscoveryAttireReference> refs = DiscoveryAttireCatalog.ReadReferences(table, 927);
            Equal("0,6", string.Join(",", refs.Select(item => item.CostumeIndex).OrderBy(index => index)));
            Equal(1, refs.Select(item => item.ModelHash).Distinct().Count());
        });
        Check("Actual profile 284 retains its unique owner mapping despite the missing costume row", () => {
            DiscoveryAttireReference reference = DiscoveryAttireCatalog.ReadReferences(table, 284).Single();
            Equal(284, reference.MappingId); Equal(0xBD479D76U, reference.CostumeKey); Equal(0xA3480BC38164FEA7UL, reference.ModelHash);
            Equal(true, string.IsNullOrWhiteSpace(reference.Issue)); Equal(true, !string.IsNullOrWhiteSpace(reference.Warning));
        });
        Check("Actual uploaded model files do not fabricate an unavailable Demon model", () => {
            List<ModelCandidate> candidates = ModelLibrary.Scan(Path.Combine(root, "Characters"));
            var result = DiscoveryAttireCatalog.Find(new RosterEntry { Id = 626, RosterTable = table, SourceId = 576 }, candidates, table, CancellationToken.None);
            Equal(2, result.References.Count); Equal(0, result.Sources.Count);
            Equal(true, result.Issues.Count > 0 || result.References.Any(item => !string.IsNullOrWhiteSpace(item.Issue)));
        });
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
