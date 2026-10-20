using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static string root;

    private static void Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-randy-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string model = Path.Combine(root, RandyPreviewAssets.StockFolderKey);
            Fill(model);
            Check(RandyPreviewAssets.IsReady(model), "A complete exact stock source is ready without any selected wrestler");
            Equal(40, RandyPreviewAssets.RequiredCachePaths.Count, "All native stock extraction assets are checked");
            Equal(40, RandyPreviewAssets.RequiredCachePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Expected paths are unique");
            Check(RandyPreviewAssets.RequiredCachePaths.All(p => !p.EndsWith(".tex", StringComparison.Ordinal)), "Stock TEX entries require their converted DDS cache files");
            string before = HashTree(model);
            string[] preview = RandyPreviewAssets.CreateArguments(model, "discovery_62247a001", Path.Combine(root, "previews"), Path.Combine(root, "private-work"));
            Equal(Path.GetFullPath(model), preview[0], "The preview uses the complete exact Randy model");
            Equal("discovery_62247a001", preview[1], "Each taunt retains its exact playable key");
            Check(preview.Contains("quick=1"), "Every generated request is preview only");
            Check(preview.Contains("hair=black") && preview.Contains("end=loop"), "Randy's appearance and ending are independent of the selected wrestler");
            Check(preview.Contains("attire=" + RandyPreviewAssets.DefaultAttire) && preview.Contains("slot=161"), "The exact Randy attire and slot are fixed");
            Check(!preview.Any(p => p.StartsWith("gmr=", StringComparison.Ordinal)), "Preview arguments never request an installed render");
            string[] firstTaunt = RandyPreviewAssets.CreateArguments(model, "t62247", Path.Combine(root, "shared-output"));
            string[] secondTaunt = RandyPreviewAssets.CreateArguments(model, "t62003", Path.Combine(root, "shared-output"));
            Check(!firstTaunt.Any(value => value.StartsWith("work=", StringComparison.Ordinal))
                && !secondTaunt.Any(value => value.StartsWith("work=", StringComparison.Ordinal)), "Shared Randy previews leave model/taunt cache separation to the native engine");
            Check(firstTaunt[0] == secondTaunt[0] && firstTaunt[2] == secondTaunt[2]
                && firstTaunt[1] != secondTaunt[1], "Different taunts share one fixed Randy source/output while keeping their exact keys");
            Check(!Directory.Exists(Path.Combine(root, "previews")) && !Directory.Exists(Path.Combine(root, "private-work")), "Argument inspection starts no work and creates no output folders");
            Equal(before, HashTree(model), "Validation and argument construction leave all source files unchanged");
            Equal(Path.GetFullPath(Path.Combine(root, "custom", "stock", "Characters", RandyPreviewAssets.StockFolderKey)),
                RandyPreviewAssets.CacheDirectory(Path.Combine(root, "custom"), Path.Combine(root, "local"), Path.Combine(root, "profile")), "GS_USER takes precedence exactly as the native engine");
            Equal(Path.GetFullPath(Path.Combine(root, "local", "GorillaStudio", "stock", "Characters", RandyPreviewAssets.StockFolderKey)),
                RandyPreviewAssets.CacheDirectory(null, Path.Combine(root, "local"), Path.Combine(root, "profile")), "The normal cache uses LOCALAPPDATA/GorillaStudio");
            Equal(Path.GetFullPath(Path.Combine(root, "profile", "GorillaStudio", "stock", "Characters", RandyPreviewAssets.StockFolderKey)),
                RandyPreviewAssets.CacheDirectory("", "", Path.Combine(root, "profile")), "Missing local data falls back to the native user profile");
            foreach (string relative in RandyPreviewAssets.RequiredCachePaths)
            {
                string path = Path.Combine(model, relative.Replace('/', Path.DirectorySeparatorChar));
                byte[] bytes = File.ReadAllBytes(path);
                File.Delete(path);
                RandyPreviewAssetStatus status = RandyPreviewAssets.Inspect(model);
                Check(!status.IsReady && status.MissingPaths.SequenceEqual(new[] { relative }), "A partial extraction reports its exact missing asset: " + relative);
                File.WriteAllBytes(path, bytes);
            }
            foreach (string relative in new[] { "BaseModel/baseModel.mcd", RandyPreviewAssets.DefaultAttire, "BaseModel/Textures/body_color.dds", "BaseModel/baseModel.mtls" })
            {
                string path = Path.Combine(model, relative.Replace('/', Path.DirectorySeparatorChar));
                byte[] original = File.ReadAllBytes(path);
                File.WriteAllBytes(path, new byte[0]);
                Check(!RandyPreviewAssets.IsReady(model), "An empty mandatory model asset is never treated as extracted: " + relative);
                File.WriteAllBytes(path, original);
            }
            foreach (string relative in new[] { "BaseModel/baseModel.mcd", RandyPreviewAssets.DefaultAttire, "BaseModel/Textures/body_color.dds" })
            {
                string path = Path.Combine(model, relative.Replace('/', Path.DirectorySeparatorChar));
                byte[] original = File.ReadAllBytes(path), damaged = (byte[])original.Clone();
                damaged[0] = 0;
                File.WriteAllBytes(path, damaged);
                Check(!RandyPreviewAssets.IsReady(model), "Corrupt model or decoded texture magic is rejected: " + relative);
                File.WriteAllBytes(path, original);
            }
            string converted = Path.Combine(model, "BaseModel/Textures/body_color.dds");
            File.Move(converted, Path.ChangeExtension(converted, ".tex"));
            Check(!RandyPreviewAssets.IsReady(model), "An unconverted TEX cannot stand in for the extractor's DDS output");
            File.Move(Path.ChangeExtension(converted, ".tex"), converted);
            Check(!RandyPreviewAssets.IsReady(Path.Combine(root, "Brody King")), "A selected wrestler folder cannot stand in for Randy");
            Check(!RandyPreviewAssets.IsReady(null), "A missing extracted path is not ready");
            Check(!RandyPreviewAssets.IsReady(Path.Combine(root, "absent", RandyPreviewAssets.StockFolderKey)), "An absent exact-name folder is not ready");
            foreach (string key in new[] { "", "../taunt", "taunt/other", "taunt\\other", " taunt", "discovery_unavailable_62247a007" })
                Reject(() => RandyPreviewAssets.CreateArguments(model, key, root, root), "Invalid/unavailable taunts do not become preview requests: " + key);
            foreach (string path in Directory.GetFiles(model, "*", SearchOption.AllDirectories)) File.Delete(path);
            try { RandyPreviewAssets.Validate(model); throw new Exception("Expected incomplete source rejection"); }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains("and 36 more") && error.Message.Length < 700, "Incomplete extraction errors remain bounded and actionable");
            }
            if (args.Length > 0) AuditNativeIndex(args[0]);
            Console.WriteLine("PASS " + checks + " Randy preview asset checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void AuditNativeIndex(string path)
    {
        byte[] before = File.ReadAllBytes(path);
        using (JsonDocument doc = JsonDocument.Parse(before))
        {
            string[] paths = doc.RootElement.GetProperty(RandyPreviewAssets.StockFolderKey).EnumerateArray()
                .Select(value => value.GetString().Substring(RandyPreviewAssets.StockFolderKey.Length + 1)).ToArray();
            Check(paths.SequenceEqual(RandyPreviewAssets.StockIndexPaths), "The helper's complete asset list exactly matches the actual original stock index");
        }
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Native stock index audit leaves the supplied source unchanged");
    }

    private static void Fill(string model)
    {
        foreach (string relative in RandyPreviewAssets.RequiredCachePaths)
        {
            string path = Path.Combine(model, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = relative.EndsWith(".dds", StringComparison.Ordinal) ? new byte[128] : new byte[16];
            byte[] magic = relative.EndsWith(".dds", StringComparison.Ordinal) ? new byte[] { 68, 68, 83, 32 }
                : relative.EndsWith(".mcd", StringComparison.Ordinal) ? new byte[] { 77, 67, 68, 33 } : new byte[] { 1 };
            Array.Copy(magic, bytes, magic.Length); File.WriteAllBytes(path, bytes);
        }
    }

    private static string HashTree(string folder)
    {
        using (SHA256 sha = SHA256.Create())
            return string.Join("\n", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p)
                .Select(path => path + ":" + Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)))));
    }

    private static void Reject(Action action, string message)
    {
        try { action(); throw new Exception("Expected rejection: " + message); }
        catch (ArgumentException) { Check(true, message); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Equal<T>(T expected, T actual, string message) { Check(EqualityComparer<T>.Default.Equals(expected, actual), message); }
}
