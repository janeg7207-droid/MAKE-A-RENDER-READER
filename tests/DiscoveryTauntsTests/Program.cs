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

    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-discovery-taunts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string table = Write("discovery/MoveData/CAMMoveData.jsfb", Build(
                new Row(62541, "Undisputed 2", 4),
                new Row(62757, "LA Parka 3", 4) { Variant = 2, MoveId = 26550515 },
                new Row(62757, "LA Parka 1", 4) { Variant = 0, MoveId = 26550513 },
                new Row(62757, "LA Parka 2", 4) { Variant = 1, MoveId = 26550514 },
                new Row(62757, "LA Parka 2", 4) { Variant = 1, MoveId = 26550514 },
                new Row(37470, "Joe Gacy 2", 4) { MoveId = 68031 },
                new Row(1200, "A strike", 2),
                new Row(1201, "A hold", 3),
                new Row(1202, "A grapple", 1),
                new Row(1203, "Uncategorized"),
                new Row(65535, "Unused", 4),
                new Row(0, "Invalid", 4),
                new Row(1204, " ", 4),
                new Row(1205, "TBD", 4),
                new Row(70000, "New\r\nTaunt", 2, 4)));
            List<DiscoveryTaunt> taunts = DiscoveryTaunts.Read(table);
            Equal("Only named real taunts are imported", 6, taunts.Count);
            DiscoveryTaunt undisputed = taunts.Single(taunt => taunt.Id == 62541);
            Equal("Undisputed 2 retains its exact Discovery name", "Undisputed 2", undisputed.Name);
            Equal("The logical move ID identifies the legacy clip", "62541a000", undisputed.Clip);
            Equal("Animation ID remains the logical CAM ID", 62541, undisputed.AnimationId);
            Equal("A missing variant field defaults to zero", "0", string.Join(",", undisputed.Variants));
            DiscoveryTaunt parka = taunts.Single(taunt => taunt.Id == 62757 && taunt.Variant == 0);
            Equal("Variant zero names the primary taunt", "LA Parka 1", parka.Name);
            Equal("Distinct named variants remain separate taunts", "LA Parka 1|LA Parka 2|LA Parka 3", string.Join("|", taunts.Where(taunt => taunt.Id == 62757).Select(taunt => taunt.Name)));
            Equal("Duplicate variant rows do not create duplicate taunts", 3, taunts.Count(taunt => taunt.Id == 62757));
            Equal("Variant game move IDs remain attached to their own motion", "26550513", string.Join(",", parka.DataMoveIds));
            Equal("LA Parka 2 selects its actual separate animation", "62757a001", taunts.Single(taunt => taunt.Id == 62757 && taunt.Variant == 1).Clip);
            Equal("LA Parka 3 selects its actual separate animation", "62757a002", taunts.Single(taunt => taunt.Id == 62757 && taunt.Variant == 2).Clip);
            Equal("Game move IDs do not overwrite the animation clip ID", "37470a000", taunts.Single(taunt => taunt.Id == 37470).Clip);
            Equal("Category four may coexist with another category", "New  Taunt", taunts.Single(taunt => taunt.Id == 70000).Name);
            Equal("Results sort by taunt name", "Joe Gacy 2", taunts[0].Name);
            Equal("A direct CAM table can be selected", table, DiscoveryTaunts.FindTable(table));
            Equal("The complete data root resolves its moves table", table, DiscoveryTaunts.FindTable(Path.Combine(root, "discovery")));
            Equal("The MoveData folder resolves its moves table", table, DiscoveryTaunts.FindTable(Path.GetDirectoryName(table)));
            Directory.CreateDirectory(Path.Combine(root, "discovery", "Roster"));
            Equal("An existing roster folder locates the adjacent moves table", table, DiscoveryTaunts.FindTable(Path.Combine(root, "discovery", "Roster")));
            string profile = Write("discovery/Roster/CharProfileTable.jsfb", new byte[] { 0 });
            Equal("The selected profile table locates the adjacent moves table", table, DiscoveryTaunts.FindTable(profile));
            Equal("A missing moves table stays missing", null, DiscoveryTaunts.FindTable(Path.Combine(root, "missing")));
            Equal("Blank selections stay missing", null, DiscoveryTaunts.FindTable(" "));
            string upper = Write("uppercase/MOVEDATA/CAMMOVEDATA.JSFB", Build(new Row(62541, "Undisputed 2", 4)));
            Equal("Windows case-insensitive path conventions work on portable tests", upper, DiscoveryTaunts.FindTable(Path.Combine(root, "uppercase")));
            Equal("Names use strict UTF-8", "El Hijo del Vikingo – Héctor", DiscoveryTaunts.Read(Write("unicode.jsfb", Build(new Row(70001, "El Hijo del Vikingo – Héctor", 4))))[0].Name);

            byte[] bad = Build(new Row(62541, "Undisputed 2", 4));
            bad[4] = (byte)'X';
            Throws<InvalidDataException>("Wrong table types fail clearly", () => DiscoveryTaunts.Read(Write("wrong.jsfb", bad)));
            Throws<InvalidDataException>("Truncated files fail clearly", () => DiscoveryTaunts.Read(Write("truncated.jsfb", new byte[] { 1, 2, 3 })));
            Throws<FileNotFoundException>("Missing input files fail clearly", () => DiscoveryTaunts.Read(Path.Combine(root, "absent.jsfb")));
            bad = Build(new Row(62541, "Undisputed 2", 4));
            Array.Resize(ref bad, bad.Length - 1);
            Throws<InvalidDataException>("Truncated category vectors cannot create a taunt", () => DiscoveryTaunts.Read(Write("category.jsfb", bad)));
            bad = Build(new Row(62541, "Unique name", 4));
            int text = Find(bad, Encoding.UTF8.GetBytes("Unique name"));
            bad[text] = 0xff;
            Throws<InvalidDataException>("Invalid UTF-8 names cannot silently change identity", () => DiscoveryTaunts.Read(Write("utf8.jsfb", bad)));
            bad = Build(new Row(62541, "Unique name", 4));
            text = Find(bad, Encoding.UTF8.GetBytes("Unique name"));
            bad[text + "Unique name".Length] = 1;
            Throws<InvalidDataException>("Unterminated names fail clearly", () => DiscoveryTaunts.Read(Write("terminator.jsfb", bad)));
            bad = Build(new Row(62541, new string('x', 16385), 4));
            Throws<InvalidDataException>("Unreasonable name lengths fail clearly", () => DiscoveryTaunts.Read(Write("oversize.jsfb", bad)));
            bad = Build(new Row(62541, "Undisputed 2", 4));
            Put(bad, 20, uint.MaxValue);
            Throws<InvalidDataException>("Invalid table vector pointers fail clearly", () => DiscoveryTaunts.Read(Write("badvector.jsfb", bad)));
            Equal("Reading leaves the original table byte-identical", true, File.ReadAllBytes(table).SequenceEqual(Build(
                new Row(62541, "Undisputed 2", 4), new Row(62757, "LA Parka 3", 4) { Variant = 2, MoveId = 26550515 },
                new Row(62757, "LA Parka 1", 4) { Variant = 0, MoveId = 26550513 }, new Row(62757, "LA Parka 2", 4) { Variant = 1, MoveId = 26550514 },
                new Row(62757, "LA Parka 2", 4) { Variant = 1, MoveId = 26550514 }, new Row(37470, "Joe Gacy 2", 4) { MoveId = 68031 },
                new Row(1200, "A strike", 2), new Row(1201, "A hold", 3), new Row(1202, "A grapple", 1), new Row(1203, "Uncategorized"),
                new Row(65535, "Unused", 4), new Row(0, "Invalid", 4), new Row(1204, " ", 4), new Row(1205, "TBD", 4), new Row(70000, "New\r\nTaunt", 2, 4))));
            if (args.Length > 0) ActualData(args[0]);
            Console.WriteLine("PASS " + passed + " Discovery taunt checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ActualData(string table)
    {
        var taunts = DiscoveryTaunts.Read(table);
        DiscoveryTaunt undisputed = taunts.Single(taunt => taunt.Id == 62541);
        Equal("Uploaded Discovery data contains Undisputed 2", "Undisputed 2", undisputed.Name);
        Equal("Uploaded Undisputed 2 joins animation 62541a000", "62541a000", undisputed.Clip);
        Equal("The uploaded catalog has unique animation variants", taunts.Count, taunts.Select(taunt => taunt.Clip).Distinct().Count());
        Equal("The uploaded catalog includes over 1400 taunts", true, taunts.Count > 1400);
        Equal("The uploaded LA Parka 2 has its own actual variant", "62757a001", taunts.Single(taunt => taunt.Name == "LA Parka 2").Clip);
        Equal("The uploaded LA Parka 3 has its own actual variant", "62757a002", taunts.Single(taunt => taunt.Name == "LA Parka 3").Clip);
        Equal("The uploaded Joe Gacy game move ID is retained separately", true, taunts.Single(taunt => taunt.Id == 37470).DataMoveIds.Contains(68031));
        Console.WriteLine("Actual uploaded data: " + taunts.Count + " named taunts");
    }

    private static string Write(string relative, byte[] contents)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, contents); return path;
    }

    private static byte[] Build(params Row[] rows)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(16); writer.Write(Encoding.ASCII.GetBytes("CMov"));
            writer.Write((ushort)6); writer.Write((ushort)8); writer.Write((ushort)4); writer.Write((ushort)0);
            writer.Write(8); writer.Write(4); writer.Write(rows.Length);
            for (int index = 0; index < rows.Length; index++) writer.Write(0);
            for (int index = 0; index < rows.Length; index++)
            {
                Row row = rows[index]; int vtable = (int)stream.Position;
                writer.Write((ushort)38); writer.Write((ushort)24);
                for (int field = 0; field < 17; field++) writer.Write((ushort)(field == 0 ? 4 : field == 1 ? 8 : field == 2 ? 12 : field == 4 ? 16 : field == 16 ? 20 : 0));
                int table = (int)stream.Position;
                writer.Write(table - vtable); writer.Write(0); writer.Write(row.Id); writer.Write(row.Variant); writer.Write(row.MoveId); writer.Write(0);
                int text = (int)stream.Position; byte[] name = Encoding.UTF8.GetBytes(row.Name); writer.Write(name.Length); writer.Write(name); writer.Write((byte)0);
                int categories = (int)stream.Position; writer.Write(row.Categories.Length); foreach (int category in row.Categories) writer.Write(category);
                long end = stream.Position;
                stream.Position = 28 + index * 4; writer.Write(table - (28 + index * 4));
                stream.Position = table + 4; writer.Write(text - (table + 4));
                stream.Position = table + 20; writer.Write(categories - (table + 20));
                stream.Position = end;
            }
            writer.Flush(); return stream.ToArray();
        }
    }

    private static int Find(byte[] haystack, byte[] needle)
    {
        for (int index = 0; index <= haystack.Length - needle.Length; index++)
            if (needle.SequenceEqual(haystack.Skip(index).Take(needle.Length))) return index;
        throw new Exception("Missing fixture text");
    }
    private static void Put(byte[] bytes, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
    private static void Throws<T>(string label, Action action) where T : Exception
    {
        try { action(); } catch (T) { passed++; Console.WriteLine("PASS " + label); return; }
        throw new Exception(label + ": expected " + typeof(T).Name);
    }
    private sealed class Row
    {
        public int Id, Variant, MoveId; public string Name; public int[] Categories;
        public Row(int id, string name, params int[] categories) { Id = id; MoveId = id; Name = name; Categories = categories; }
    }
}
