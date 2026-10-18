using System;
using System.IO;
using GorillaStudio.NameFolders;

// CharacterFolders.Plan uses this data-only type. The tests call the actual
// physical-ID policy while avoiding unrelated roster-import dependencies.
namespace GorillaStudio.NameFolders
{
    public sealed class RosterEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }
        public string RosterTable { get; set; }
        public int SourceId { get; set; }
    }
}

internal static class Program
{
    private static string root;
    private static int passed;

    private static void Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-attire-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string normal = Model("normal/PAC/Default_Attire/Attire.mcd");
            string normalRoot = Directory.GetParent(normal).Parent.FullName;
            Equal("A standard character root is native-compatible", true, AutomaticAttire.HasNativeAttire(normalRoot));
            Equal("A standard character root does not need a wrapper", false, AutomaticAttire.Plan(normalRoot, 215).WrapDirectModel);
            Equal("Native-compatible aliases keep their existing cache identity", "", AutomaticAttire.Plan(normalRoot, 215).CacheSuffix);

            string direct = Path.GetDirectoryName(Model("direct/PAC/Attire.mcd"));
            Equal("A direct-MCD root is not native-compatible", false, AutomaticAttire.HasNativeAttire(direct));
            Equal("A direct-MCD root receives the compatibility layout", true, AutomaticAttire.Plan(direct, 215).WrapDirectModel);
            Equal("Compatibility aliases have a distinct versioned cache identity", "direct-v1", AutomaticAttire.Plan(direct, 215).CacheSuffix);
            Equal("A direct model without a body retains native stock-body extraction", null, AutomaticAttire.Plan(direct, 215).BaseModelFolder);

            string ownBody = Path.GetDirectoryName(Model("direct/PAC/BaseModel/baseModel.mcd"));
            Equal("A source keeps its own custom body", ownBody, AutomaticAttire.Plan(direct, 215).BaseModelFolder);
            Equal("A BaseModel child alone does not count as wearable attire", false, AutomaticAttire.HasNativeAttire(direct));

            string child = Path.GetDirectoryName(Model("parent/215_PAC/215_Default_Attire/215_Attire.mcd"));
            string parentBody = Path.GetDirectoryName(Model("parent/215_PAC/BaseModel/baseModel.mcd"));
            Equal("An explicit attire folder can reuse its proven parent's body", parentBody, AutomaticAttire.Plan(child, 215).BaseModelFolder);
            Equal("A parent body with a different physical ID is not reused", null, AutomaticAttire.Plan(child, 368).BaseModelFolder);

            string plainChild = Path.GetDirectoryName(Model("plain/PAC/215_Default_Attire/215_Attire.mcd"));
            string plainBody = Path.GetDirectoryName(Model("plain/PAC/BaseModel/baseModel.mcd"));
            Equal("A unique numbered child proves its plain-name parent's body ownership", plainBody, AutomaticAttire.Plan(plainChild, 215).BaseModelFolder);

            string mixedChild = Path.GetDirectoryName(Model("mixed/Mixed/215_Default_Attire/215_Attire.mcd"));
            Model("mixed/Mixed/368_Default_Attire/368_Attire.mcd");
            Model("mixed/Mixed/BaseModel/baseModel.mcd");
            Equal("Conflicting numbered children never prove a shared parent body", null, AutomaticAttire.Plan(mixedChild, 215).BaseModelFolder);

            string unproven = Path.GetDirectoryName(Model("unproven/PAC/Default_Attire/Attire.mcd"));
            Model("unproven/PAC/BaseModel/baseModel.mcd");
            Equal("An unnumbered neighbouring body is not borrowed by guessing", null, AutomaticAttire.Plan(unproven, 215).BaseModelFolder);

            string emptyBody = Path.GetDirectoryName(Model("empty-body/215_PAC/215_Default_Attire/215_Attire.mcd"));
            Model("empty-body/215_PAC/BaseModel/readme.txt");
            Equal("A body directory without its body MCD is not reused", null, AutomaticAttire.Plan(emptyBody, 215).BaseModelFolder);

            string upper = Path.GetDirectoryName(Model("upper/PAC/ATTIRE.MCD"));
            string upperBody = Path.GetDirectoryName(Model("upper/PAC/BASEMODEL/BASEMODEL.MCD"));
            Equal("Uppercase MCD extensions receive the direct compatibility layout", true, AutomaticAttire.Plan(upper, 215).WrapDirectModel);
            Equal("Body matching respects Windows case-insensitive names", upperBody, AutomaticAttire.Plan(upper, 215).BaseModelFolder);

            string bodyOnly = Path.GetDirectoryName(Model("body-only/PAC/baseModel.mcd"));
            Equal("A lone body MCD is not misrepresented as wearable attire", false, AutomaticAttire.Plan(bodyOnly, 215).WrapDirectModel);
            Equal("The BaseModel directory itself is not misrepresented as wearable attire", false, AutomaticAttire.Plan(ownBody, 215).WrapDirectModel);

            string both = Path.GetDirectoryName(Model("both/PAC/Attire.mcd"));
            Model("both/PAC/Default_Attire/Attire.mcd");
            Equal("Native child attires take precedence over a stray root MCD", false, AutomaticAttire.Plan(both, 215).WrapDirectModel);

            string nested = Path.GetDirectoryName(Model("nested/PAC/Costumes/One/Attire.mcd"));
            string nestedRoot = Directory.GetParent(nested).Parent.FullName;
            Equal("A deeper model is not mistaken for the native one-level layout", false, AutomaticAttire.HasNativeAttire(nestedRoot));
            Equal("A missing model is not represented as a usable attire", false, AutomaticAttire.Plan(nestedRoot, 215).WrapDirectModel);

            Equal("Missing folders are not native-compatible", false, AutomaticAttire.HasNativeAttire(Path.Combine(root, "missing")));
            Throws<DirectoryNotFoundException>("Missing source paths fail clearly", () => AutomaticAttire.Plan(Path.Combine(root, "missing"), 215));
            Throws<ArgumentOutOfRangeException>("Logical zero is never used as a physical body ID", () => AutomaticAttire.Plan(direct, 0));
            Throws<ArgumentOutOfRangeException>("Out-of-range physical body IDs fail clearly", () => AutomaticAttire.Plan(direct, 65536));
            Equal("Planning leaves the source model unchanged", "fixture", File.ReadAllText(Path.Combine(direct, "Attire.mcd")));

            Console.WriteLine("PASS " + passed + " automatic attire policy checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Model(string relative)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "fixture");
        return path;
    }

    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual))
            throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++;
        Console.WriteLine("PASS " + label);
    }

    private static void Throws<T>(string label, Action action) where T : Exception
    {
        try { action(); }
        catch (T) { passed++; Console.WriteLine("PASS " + label); return; }
        throw new Exception(label + ": expected " + typeof(T).Name);
    }
}
