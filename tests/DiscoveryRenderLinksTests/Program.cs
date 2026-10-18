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
        root = Path.Combine(Path.GetTempPath(), "gorilla-render-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const ulong custom = 0x1709AEF076130352UL;
            byte[] original = Build(new Row(122, custom, custom, 0), new Row(9000, custom, ulong.MaxValue),
                new Row(1481, 0xDA9AD6983EE0D088UL), new Row(4000), new Row(0, 123), new Row(65535, 42));
            string path = Write("profiles.jsfb", original);
            var links = DiscoveryRenderLinks.Read(path);
            Equal("Unknown modded picture hashes remain linked to their actual logical ID", custom, links[122].Single());
            Equal("Duplicate attires and zero picture hashes do not add duplicate links", 1, links[122].Count);
            Equal("The entire unsigned 64-bit picture hash survives", ulong.MaxValue, links[9000][1]);
            Equal("Shared picture hashes remain visible for every owner", true, links[9000].Contains(links[122][0]));
            Equal("Known stock picture hashes follow the same exact field", 0xDA9AD6983EE0D088UL, links[1481].Single());
            Equal("A profile with no picture links remains distinguishable from a missing ID", 0, links[4000].Count);
            Equal("Zero roster IDs cannot create an install target", false, links.ContainsKey(0));
            Equal("Roster IDs retain their complete unsigned short range", 42UL, links[65535].Single());
            Equal("No physical folder or base ID is substituted for the logical ID", false, links.ContainsKey(312));
            Equal("Reading does not change any source bytes", true, File.ReadAllBytes(path).SequenceEqual(original));
            Equal("An empty profile vector gives an empty index", 0, DiscoveryRenderLinks.Read(Write("empty.jsfb", Build())).Count);
            var merged = DiscoveryRenderLinks.Read(Write("duplicate-profile.jsfb", Build(new Row(122, 10), new Row(122, 20, 10))));
            Equal("Duplicate logical profile records retain all exact unique picture links", "10,20", string.Join(",", merged[122]));

            byte[] missing = Build(new Row(122, custom));
            ClearField(missing, Wrapper(missing), 1);
            Equal("A missing wrapper profile reference is ignored", 0, DiscoveryRenderLinks.Read(Write("no-profile.jsfb", missing)).Count);
            missing = Build(new Row(122, custom));
            ClearField(missing, Profile(missing), 2);
            Equal("An absent logical ID never becomes a guessed target", 0, DiscoveryRenderLinks.Read(Write("no-id.jsfb", missing)).Count);
            missing = Build(new Row(122, custom));
            ClearField(missing, Profile(missing), 4);
            Equal("An absent attire vector creates no picture link", 0, DiscoveryRenderLinks.Read(Write("no-attires.jsfb", missing))[122].Count);
            missing = Build(new Row(122, custom));
            ClearField(missing, Attire(missing), 6);
            Equal("An absent picture field creates no picture link", 0, DiscoveryRenderLinks.Read(Write("no-picture.jsfb", missing))[122].Count);
            byte[] decoy = Build(new Row(122, custom));
            int decoyProfile = Profile(decoy);
            Put16(decoy, VTable(decoy, decoyProfile) + 4 + 2 * 3, 6);
            Put16(decoy, decoyProfile + 6, 312);
            var withBaseId = DiscoveryRenderLinks.Read(Write("physical-base-id.jsfb", decoy));
            Equal("A different explicit physical base ID cannot take the logical picture owner", "122", string.Join(",", withBaseId.Keys));
            decoy = Build(new Row(122, custom));
            int decoyAttire = Attire(decoy);
            ClearField(decoy, decoyAttire, 6);
            Put16(decoy, VTable(decoy, decoyAttire) + 4 + 2 * 1, 8);
            Equal("A hash in a different attire field never becomes a roster picture", 0,
                DiscoveryRenderLinks.Read(Write("other-hash.jsfb", decoy))[122].Count);

            // A real FlatBuffer may reuse a vtable located after the object.
            byte[] signed = Build(new Row(122, custom));
            int attire = Attire(signed), oldVtable = VTable(signed, attire), newVtable = signed.Length;
            int vlength = U16(signed, oldVtable); Array.Resize(ref signed, signed.Length + vlength);
            Array.Copy(signed, oldVtable, signed, newVtable, vlength);
            Put(signed, attire, unchecked((uint)(attire - newVtable)));
            Equal("Shared vtables with a negative signed displacement remain supported", custom,
                DiscoveryRenderLinks.Read(Write("signed-vtable.jsfb", signed))[122].Single());

            File.WriteAllBytes(path, Build(new Row(122, 99), new Row(9001, custom)));
            var refreshed = DiscoveryRenderLinks.Read(path);
            Equal("Refreshing reads a changed picture hash instead of cached stock metadata", 99UL, refreshed[122].Single());
            Equal("Refreshing includes a newly added roster profile immediately", custom, refreshed[9001].Single());
            Equal("Refreshing removes deleted profiles", false, refreshed.ContainsKey(1481));
            Equal("A prior result is a stable snapshot", custom, links[122].Single());

            Throws<FileNotFoundException>("Missing tables fail clearly", () => DiscoveryRenderLinks.Read(Path.Combine(root, "absent.jsfb")));
            Bad("wrong-identifier", bytes => bytes[4] = (byte)'X');
            Throws<InvalidDataException>("Truncated headers fail clearly", () => DiscoveryRenderLinks.Read(Write("short.jsfb", new byte[] { 1, 2, 3 })));
            Bad("invalid-root", bytes => Put(bytes, 0, uint.MaxValue));
            Bad("invalid-root-vector", bytes => Put(bytes, 20, uint.MaxValue));
            Bad("invalid-wrapper-reference", bytes => Put(bytes, 28, uint.MaxValue));
            Bad("invalid-profile-reference", bytes => Put(bytes, Field(bytes, Wrapper(bytes), 1), uint.MaxValue));
            Bad("self-profile-reference", bytes => Put(bytes, Field(bytes, Wrapper(bytes), 1), 0));
            Bad("invalid-attire-vector", bytes => Put(bytes, Field(bytes, Profile(bytes), 4), uint.MaxValue));
            Bad("truncated-u16-id", bytes => {
                int profile = Profile(bytes), vt = VTable(bytes, profile);
                Put16(bytes, vt + 2, 5); // ID starts at offset 4 and needs two bytes.
            });
            Bad("truncated-u64-picture", bytes => {
                int t = Attire(bytes), vt = VTable(bytes, t);
                Put16(bytes, vt + 2, 12); // Picture starts at offset 8 and needs eight bytes.
            });
            Bad("invalid-picture-field-offset", bytes => {
                int vt = VTable(bytes, Attire(bytes)); Put16(bytes, vt + 4 + 2 * 6, 2);
            });
            byte[] truncated = Build(new Row(122, custom)); Array.Resize(ref truncated, truncated.Length - 1);
            Throws<InvalidDataException>("A truncated picture cannot produce a partial hash", () => DiscoveryRenderLinks.Read(Write("truncated-picture.jsfb", truncated)));

            if (args.Length > 0) ActualData(args[0]);
            Console.WriteLine("PASS " + passed + " Discovery roster picture link checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ActualData(string path)
    {
        byte[] before = File.ReadAllBytes(path);
        var links = DiscoveryRenderLinks.Read(path);
        Equal("The uploaded roster retains all 1457 logical profiles", 1457, links.Count);
        Equal("The uploaded AVERY STYLES picture is read without a stock filename", "1709AEF076130352", links[122].Single().ToString("X16"));
        Equal("AVERY STYLES has one actual picture owner in the uploaded data", "122", string.Join(",", links.Where(pair => pair.Value.Contains(links[122][0])).Select(pair => pair.Key)));
        Equal("The uploaded CM Punk GM picture stays linked to logical ID 1481", "DA9AD6983EE0D088", links[1481].Single().ToString("X16"));
        Equal("The complete uploaded roster includes all 1139 unique picture hashes", 1139, links.SelectMany(pair => pair.Value).Distinct().Count());
        Equal("The uploaded data is left byte-identical", true, before.SequenceEqual(File.ReadAllBytes(path)));
    }

    private static void Bad(string name, Action<byte[]> change)
    {
        byte[] bytes = Build(new Row(122, 0x1709AEF076130352UL)); change(bytes);
        Throws<InvalidDataException>(name + " is rejected", () => DiscoveryRenderLinks.Read(Write(name + ".jsfb", bytes)));
    }

    private static byte[] Build(params Row[] rows)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(16); writer.Write(Encoding.ASCII.GetBytes("Char"));
            writer.Write((ushort)6); writer.Write((ushort)8); writer.Write((ushort)4); writer.Write((ushort)0);
            writer.Write(8); writer.Write(4); writer.Write(rows.Length);
            int[] entries = new int[rows.Length];
            for (int index = 0; index < rows.Length; index++) { entries[index] = (int)stream.Position; writer.Write(0); }
            for (int index = 0; index < rows.Length; index++)
            {
                int wrapper = Table(writer, 8, 0, 4); Patch(writer, entries[index], wrapper - entries[index]);
                int profile = Table(writer, 12, 0, 0, 4, 0, 8); Patch(writer, wrapper + 4, profile - wrapper - 4);
                long end = stream.Position; stream.Position = profile + 4; writer.Write((ushort)rows[index].Id); stream.Position = end;
                int vector = (int)stream.Position; writer.Write(rows[index].Hashes.Length);
                int[] attires = new int[rows[index].Hashes.Length];
                for (int attire = 0; attire < attires.Length; attire++) { attires[attire] = (int)stream.Position; writer.Write(0); }
                Patch(writer, profile + 8, vector - profile - 8);
                for (int attire = 0; attire < attires.Length; attire++)
                {
                    int target = Table(writer, 16, 0, 0, 0, 0, 0, 0, 8);
                    Patch(writer, attires[attire], target - attires[attire]);
                    end = stream.Position; stream.Position = target + 8; writer.Write(rows[index].Hashes[attire]); stream.Position = end;
                }
            }
            writer.Flush(); return stream.ToArray();
        }
    }

    private static int Table(BinaryWriter writer, int size, params int[] fields)
    {
        int vtable = (int)writer.BaseStream.Position;
        writer.Write((ushort)(4 + fields.Length * 2)); writer.Write((ushort)size);
        foreach (int field in fields) writer.Write((ushort)field);
        int table = (int)writer.BaseStream.Position;
        writer.Write(table - vtable); writer.Write(new byte[size - 4]); return table;
    }
    private static void Patch(BinaryWriter writer, int position, int value)
    {
        long end = writer.BaseStream.Position; writer.BaseStream.Position = position; writer.Write(value); writer.BaseStream.Position = end;
    }
    private static int U16(byte[] bytes, int p) { return BitConverter.ToUInt16(bytes, p); }
    private static int U32(byte[] bytes, int p) { return BitConverter.ToInt32(bytes, p); }
    private static int VTable(byte[] bytes, int table) { return table - U32(bytes, table); }
    private static int Field(byte[] bytes, int table, int field) { return table + U16(bytes, VTable(bytes, table) + 4 + 2 * field); }
    private static int Wrapper(byte[] bytes) { return 28 + U32(bytes, 28); }
    private static int Profile(byte[] bytes) { int p = Field(bytes, Wrapper(bytes), 1); return p + U32(bytes, p); }
    private static int Attire(byte[] bytes) { int p = Field(bytes, Profile(bytes), 4); int v = p + U32(bytes, p); return v + 4 + U32(bytes, v + 4); }
    private static void ClearField(byte[] bytes, int table, int field) { Put16(bytes, VTable(bytes, table) + 4 + 2 * field, 0); }
    private static void Put(byte[] bytes, int p, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, p, 4); }
    private static void Put16(byte[] bytes, int p, ushort value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, p, 2); }
    private static string Write(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
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
        public readonly int Id; public readonly ulong[] Hashes;
        public Row(int id, params ulong[] hashes) { Id = id; Hashes = hashes; }
    }
}
