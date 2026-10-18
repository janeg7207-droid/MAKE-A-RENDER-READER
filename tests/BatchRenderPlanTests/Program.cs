using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;
    private static string root;

    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-batch-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Plans();
            Results();
            if (args.Length > 0) UploadedDiscovery(args[0]);
            Console.WriteLine(passed + " batch plan and completed-render checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Plans()
    {
        string moxley = Model("library", "Jon Moxley"), pac = Model("library", "1173_My PAC Model");
        var rows = new[] {
            new RosterEntry { Id = 4000, Name = "JON MOXLEY", SourceId = 194, Source = "Profile" },
            new RosterEntry { Id = 924, Name = "PAC", Folder = pac, SourceId = 194, Source = "Chosen model folder" },
            new RosterEntry { Id = 610, Name = "AJ STYLES", StockFolder = "610_AJ_Styles_2024" },
            new RosterEntry { Id = 5000, Name = "Missing Wrestler" }
        };
        var plan = BatchRenderPlan.Create(rows, ModelLibrary.Scan(Path.Combine(root, "library")), null);
        Check(plan.Jobs.Count == 3 && plan.Skipped.Count == 1, "Only resolved or verified installed-game jobs enter the batch");
        Check(plan.Jobs[0].Entry.Folder == moxley && plan.Jobs[0].Entry.Id == 4000, "Plain-name models retain the logical roster ID");
        Check(plan.Jobs[1].Entry.Folder == pac && plan.Jobs[1].Entry.SourceId == 1173 && plan.Jobs[1].Entry.Id == 924, "A saved model's physical source remains separate from the PAC logical slot");
        Check(plan.Jobs[2].ExtractFromGame && plan.Jobs[2].Entry.StockFolder == "610_AJ_Styles_2024", "Installed-game jobs retain only the verified stock model key");
        Check(rows[0].Folder == null && rows[1].SourceId == 194, "Planning never rewrites remembered roster rows");
        rows[0].Name = "Changed after start"; rows[1].Folder = "Changed folder";
        Check(plan.Jobs[0].Entry.Name == "JON MOXLEY" && plan.Jobs[1].Entry.Folder == pac, "Jobs snapshot the selected names, IDs and model associations");
        Model("ambiguous", "First/Adam Cole"); Model("ambiguous", "Second/Adam Cole");
        var ambiguous = BatchRenderPlan.Create(new[] { new RosterEntry { Id = 4001, Name = "ADAM COLE" } }, ModelLibrary.Scan(Path.Combine(root, "ambiguous")), null);
        Check(ambiguous.Jobs.Count == 0 && ambiguous.Skipped.Single().Contains("several models"), "A batch never chooses an arbitrary matching attire pack");
        var duplicates = BatchRenderPlan.Create(new[] { new RosterEntry { Id = 4000, Name = "First", Folder = moxley }, new RosterEntry { Id = 4000, Name = "Second", Folder = pac } }, null, null);
        Check(duplicates.Jobs.Count == 0 && duplicates.Skipped.Count == 1, "Two selected models for one logical slot cannot overwrite one another");
        var invalid = BatchRenderPlan.Create(new[] { new RosterEntry { Id = 0, Name = "Unassigned", Folder = moxley }, new RosterEntry { Id = 65536, Name = "Out of range", Folder = pac } }, null, null);
        Check(invalid.Jobs.Count == 0 && invalid.Skipped.Count == 2, "Unassigned and out-of-range roster rows cannot become jobs");
        var wrongBody = BatchRenderPlan.Create(new[] { new RosterEntry { Id = 7777, Name = "Unknown mod", SourceId = 1173, Source = "Profile" } }, ModelLibrary.Scan(Path.Combine(root, "library")), null);
        Check(wrongBody.Jobs.Count == 0, "An inherited profile body ID never selects another wrestler's model");
    }

    private static void Results()
    {
        string output = Directory.CreateDirectory(Path.Combine(root, "batch-output", "924")).FullName;
        string path = Path.Combine(output, "924_PAC.gmr"), reason;
        byte[] valid = Render("{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":924}");
        File.WriteAllBytes(path, valid);
        Check(BatchRenderPlan.VerifyCompleted(path, 924, output, out reason) && reason == null, "A completed 1024-square 30-fps alpha GMR for this isolated job verifies");
        Check(!BatchRenderPlan.VerifyCompleted(path, 4000, output, out reason), "A valid movie for a different logical wrestler cannot count as success");
        Check(!BatchRenderPlan.VerifyCompleted(path, 924, Path.Combine(root, "other-job"), out reason), "A stale file from another job's output directory cannot count as success");
        Check(!BatchRenderPlan.VerifyCompleted(path, 924, output + "-lookalike", out reason), "An output prefix without the directory boundary cannot count as this job");
        Check(!BatchRenderPlan.VerifyCompleted(null, 924, output, out reason), "A native render that returned no LastGmr is a failure");
        Check(!BatchRenderPlan.VerifyCompleted(Path.Combine(output, "missing.gmr"), 924, output, out reason), "A missing completed file cannot count as success");
        foreach (var item in new[] { new { Offset = 0, Value = 0u }, new { Offset = 4, Value = 57u }, new { Offset = 8, Value = 0u },
            new { Offset = 16, Value = 1u }, new { Offset = 20, Value = 512u }, new { Offset = 24, Value = 512u },
            new { Offset = 28, Value = 60u }, new { Offset = 32, Value = 0u }, new { Offset = 36, Value = 0u },
            new { Offset = 40, Value = 1u }, new { Offset = 44, Value = 56u }, new { Offset = 44, Value = 55u },
            new { Offset = 48, Value = 57u }, new { Offset = 48, Value = 65u }, new { Offset = 52, Value = 62u } })
        {
            byte[] corrupt = (byte[])valid.Clone(); Array.Copy(BitConverter.GetBytes(item.Value), 0, corrupt, item.Offset, 4); File.WriteAllBytes(path, corrupt);
            Check(!BatchRenderPlan.VerifyCompleted(path, 924, output, out reason), "Malformed Bink field at " + item.Offset + " with value " + item.Value + " cannot pass");
        }
        foreach (string json in new[] { "null", "[]", "{broken", "{\"format\":\"Other\",\"version\":1,\"wrestler_id\":924}",
            "{\"format\":\"GorillaRender\",\"version\":2,\"wrestler_id\":924}", "{\"format\":\"GorillaRender\",\"version\":\"1\",\"wrestler_id\":924}",
            "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":\"924\"}", "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":true}",
            "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":[]}" })
        {
            File.WriteAllBytes(path, Render(json)); Check(!BatchRenderPlan.VerifyCompleted(path, 924, output, out reason), "Malformed or unsupported GMR metadata is rejected: " + json);
        }
        byte[] badFooter = (byte[])valid.Clone(); badFooter[badFooter.Length - 1] = (byte)'2'; File.WriteAllBytes(path, badFooter);
        Check(!BatchRenderPlan.VerifyCompleted(path, 924, output, out reason), "An unknown footer is rejected");
        File.WriteAllBytes(path, valid.Take(40).ToArray());
        Check(!BatchRenderPlan.VerifyCompleted(path, 924, output, out reason), "A truncated movie is rejected");
        File.WriteAllBytes(path, valid);
        Check(BatchRenderPlan.VerifyCompleted(path, 924, output, out reason), "Rejected inputs do not poison later valid completed output");
    }

    private static void UploadedDiscovery(string data)
    {
        string table = Path.Combine(data, "Roster", "CharProfileTable.jsfb");
        List<RosterEntry> roster = RosterImport.LoadFile(table);
        RosterEntry pac = roster.Single(entry => entry.Id == 924), avery = roster.Single(entry => entry.Id == 122);
        var freshPac = BatchRenderPlan.Copy(pac); freshPac.Folder = Model("actual-pac", "PAC");
        var freshAvery = BatchRenderPlan.Copy(avery); freshAvery.Folder = Model("actual-avery", "Avery Styles");
        var plan = BatchRenderPlan.Create(new[] { freshPac, freshAvery }, null, table);
        Check(plan.Jobs.Count == 2 && plan.Jobs[0].Entry.Name == pac.Name && plan.Jobs[1].Entry.Name == avery.Name, "The uploaded Discovery table supplies the real PAC and Avery names and IDs for batch planning");
        Check(plan.Jobs.All(job => job.Entry.RosterTable == table), "Both real-data jobs retain the actual table for final picture-key correlation");
    }

    private static string Model(string group, string name)
    {
        string folder = Path.Combine(root, group, name); Directory.CreateDirectory(Path.Combine(folder, "Default_Attire"));
        File.WriteAllBytes(Path.Combine(folder, "Default_Attire", "attire.mcd"), new byte[] { 1 }); return folder;
    }

    // Header/metadata fixture only; no claim that these bytes decode as a real movie.
    private static byte[] Render(string json)
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint word in new uint[] { 56, 2, 4, 2, 1024, 1024, 30, 1, 1048576, 0, 57, 61, 64 }) writer.Write(word);
            writer.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            byte[] metadata = Encoding.UTF8.GetBytes(json); writer.Write(metadata); writer.Write((uint)metadata.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
            return stream.ToArray();
        }
    }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); passed++; }
}
