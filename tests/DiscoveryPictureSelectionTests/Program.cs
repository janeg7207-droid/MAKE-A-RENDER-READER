using System;
using System.IO;
using System.Linq;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "gorilla-picture-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string table = Path.Combine(root, "CharProfileTable.jsfb");
            File.WriteAllBytes(table, Profiles(new Row(924, 0xAF0F2CC3FC5CECF7UL), new Row(1173, 0x02F73773FF4141F4UL),
                new Row(122, 0x1709AEF076130352UL), new Row(4000), new Row(5000, 0xAF0F2CC3FC5CECF7UL)));
            var owner = new Owner { TargetSlot = "924", FolderSlot = "1173" };
            Equal("A selected remapped character can refresh its picture status before Preview", true, DiscoveryPictureSelection.Apply(owner, table));
            Contains("PAC uses the logical 924 picture reference", owner.WRender.Text, "AF0F2CC3FC5CECF7");
            Equal("PAC cannot inherit the physical 1173 picture", false, owner.WRender.Text.Contains("02F73773FF4141F4"));
            Contains("The physical picture is cached separately for native reset", owner.OwnRenderText, "02F73773FF4141F4");
            Equal("The physical cache cannot keep the remapped PAC picture", false, owner.OwnRenderText.Contains("AF0F2CC3FC5CECF7"));
            Contains("Shared references keep their other exact logical owner", owner.WRender.Text, "Also linked to wrestler 5000");
            Contains("A reference is described as a link, without claiming decoded pixels", owner.WRender.Text, "linked in Discovery");
            Equal("A confirmed reference clears the native missing-reference color", "normal", owner.WRender.Foreground);
            Equal("The native own-slot reset caches the matching normal brush", "normal", owner.OwnRenderBrush);
            Equal("Reading status does not change the target logical ID", "924", owner.TargetSlot);
            Equal("Reading status does not change the model's physical ID", "1173", owner.FolderSlot);

            owner.TargetSlot = "1173";
            DiscoveryPictureSelection.Apply(owner, table);
            Equal("Returning to the physical slot restores that slot's exact picture status", owner.OwnRenderText, owner.WRender.Text);
            owner.TargetSlot = "4000";
            DiscoveryPictureSelection.Apply(owner, table);
            Contains("An existing profile without a link says the reference is absent", owner.WRender.Text, "No roster picture reference");
            Equal("An absent reference uses the native warning brush", "missing", owner.WRender.Foreground);
            Contains("The physical cache stays independent when target has no picture", owner.OwnRenderText, "02F73773FF4141F4");
            owner.TargetSlot = "4444";
            DiscoveryPictureSelection.Apply(owner, table);
            Contains("A missing logical profile is distinguished from a profile with no picture", owner.WRender.Text, "not in the current Discovery roster");

            File.WriteAllBytes(table, Profiles(new Row(924, 0x123456789ABCDEF0UL), new Row(1173), new Row(4444, ulong.MaxValue)));
            owner.TargetSlot = "924";
            DiscoveryPictureSelection.Apply(owner, table);
            Contains("A changed Discovery picture reference is reread on the next selection", owner.WRender.Text, "123456789ABCDEF0");
            Equal("A prior reference cannot survive a table refresh", false, owner.WRender.Text.Contains("AF0F2CC3FC5CECF7"));
            Contains("A removed physical picture clears the native reset cache", owner.OwnRenderText, "No roster picture reference");
            Equal("A removed physical picture clears the reset brush", "missing", owner.OwnRenderBrush);
            owner.TargetSlot = "4444";
            DiscoveryPictureSelection.Apply(owner, table);
            Contains("A newly added roster entry's complete unsigned picture hash is immediately readable", owner.WRender.Text, "FFFFFFFFFFFFFFFF");

            string previous = owner.WRender.Text;
            File.WriteAllBytes(table, new byte[] { 1, 2, 3 });
            Equal("A corrupt table leaves the previous native state available", false, DiscoveryPictureSelection.Apply(owner, table));
            Equal("A failed read cannot invent a missing reference", previous, owner.WRender.Text);
            Equal("A missing table does not crash character loading", false, DiscoveryPictureSelection.Apply(owner, table + ".absent"));
            Equal("An unsupported owner is ignored", false, DiscoveryPictureSelection.Apply(new object(), table));
            Equal("A missing owner is ignored", false, DiscoveryPictureSelection.Apply(null, table));
            foreach (string invalid in new[] { "", "0", "-1", "65536", "924.0", "PAC" })
            {
                owner.TargetSlot = invalid;
                Equal("Invalid target cannot substitute the physical picture: " + invalid, false, DiscoveryPictureSelection.Apply(owner, table));
            }

            if (args.Length > 0)
            {
                byte[] originalTable = File.ReadAllBytes(args[0]);
                owner.TargetSlot = "924"; owner.FolderSlot = "1173";
                Equal("The uploaded full Discovery profile table is readable", true, DiscoveryPictureSelection.Apply(owner, args[0]));
                Contains("Actual uploaded PAC selects its custom logical picture", owner.WRender.Text, "AF0F2CC3FC5CECF7");
                Contains("Actual uploaded physical 1173 retains Chad Gable's different picture", owner.OwnRenderText, "02F73773FF4141F4");
                owner.TargetSlot = "122";
                DiscoveryPictureSelection.Apply(owner, args[0]);
                Contains("Actual uploaded Avery Styles retains the custom picture reference", owner.WRender.Text, "1709AEF076130352");
                Equal("Uploaded source table bytes stay unchanged", true, File.ReadAllBytes(args[0]).SequenceEqual(originalTable));
            }
            Console.WriteLine(passed + " Discovery picture selection checks passed.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Contains(string name, string value, string expected) { Equal(name, true, value != null && value.Contains(expected)); }
    private static void Equal<T>(string name, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(name + ": expected " + expected + ", actual " + actual);
        passed++; Console.WriteLine("PASS " + name);
    }

    private sealed class Owner
    {
        public string TargetSlot;
        public string FolderSlot;
        public Label WRender = new Label { Text = "none found", Foreground = "old" };
        public string OwnRenderText = "none found";
        public object OwnRenderBrush = "old";
        public object TryFindResource(object key) { return key as string == "Text" ? "normal" : key as string == "Amber" ? "missing" : null; }
    }
    private sealed class Label { public string Text { get; set; } public object Foreground { get; set; } }
    private sealed class Row
    {
        public int Id; public ulong[] Hashes;
        public Row(int id, params ulong[] hashes) { Id = id; Hashes = hashes; }
    }
    private static byte[] Profiles(params Row[] rows)
    {
        // A small genuine JSFB layout containing profiles, logical IDs and attire refs.
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[8]);
            int root = Table(writer, new ushort[] { 4 }, 8); int vector = Vector(writer, rows.Length); At(writer, root + 4, (uint)(vector - root - 4));
            for (int i = 0; i < rows.Length; i++)
            {
                int wrapper = Table(writer, new ushort[] { 0, 4 }, 8); At(writer, vector + 4 + i * 4, (uint)(wrapper - vector - 4 - i * 4));
                int profile = Table(writer, new ushort[] { 0, 0, 4, 0, 8 }, 12); At(writer, wrapper + 4, (uint)(profile - wrapper - 4));
                At16(writer, profile + 4, (ushort)rows[i].Id); int attires = Vector(writer, rows[i].Hashes.Length); At(writer, profile + 8, (uint)(attires - profile - 8));
                for (int j = 0; j < rows[i].Hashes.Length; j++)
                {
                    int attire = Table(writer, new ushort[] { 0, 0, 0, 0, 0, 0, 8 }, 16);
                    At(writer, attires + 4 + j * 4, (uint)(attire - attires - 4 - j * 4)); At64(writer, attire + 8, rows[i].Hashes[j]);
                }
            }
            At(writer, 0, (uint)root); stream.Position = 4; writer.Write(new byte[] { (byte)'C', (byte)'h', (byte)'a', (byte)'r' });
            return stream.ToArray();
        }
    }
    private static int Table(BinaryWriter writer, ushort[] offsets, ushort size)
    {
        int vtable = (int)writer.BaseStream.Position; writer.Write((ushort)(4 + offsets.Length * 2)); writer.Write(size);
        foreach (ushort offset in offsets) writer.Write(offset);
        int table = (int)writer.BaseStream.Position; writer.Write(table - vtable); writer.Write(new byte[size - 4]); return table;
    }
    private static int Vector(BinaryWriter writer, int count) { int p = (int)writer.BaseStream.Position; writer.Write(count); writer.Write(new byte[count * 4]); return p; }
    private static void At(BinaryWriter writer, int p, uint value) { long old = writer.BaseStream.Position; writer.BaseStream.Position = p; writer.Write(value); writer.BaseStream.Position = old; }
    private static void At16(BinaryWriter writer, int p, ushort value) { long old = writer.BaseStream.Position; writer.BaseStream.Position = p; writer.Write(value); writer.BaseStream.Position = old; }
    private static void At64(BinaryWriter writer, int p, ulong value) { long old = writer.BaseStream.Position; writer.BaseStream.Position = p; writer.Write(value); writer.BaseStream.Position = old; }
}
