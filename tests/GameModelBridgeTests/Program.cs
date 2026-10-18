using GorillaStudio.NameFolders;

string root = Path.Combine(Path.GetTempPath(), "gorilla-model-bridge-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine("PASS " + name);
}
void Reject(Action action, string expected)
{
    try { action(); }
    catch (Exception error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { return; }
    throw new Exception("Expected rejection containing: " + expected);
}
void Equal(string expected, string actual)
{
    if (expected != actual) throw new Exception($"Expected '{expected}', got '{actual}'.");
}
try
{
    string[] known = { "312_CM_Punk", "64_Rhea Ripley's Model", "65_André_&_Guest" };
    Check("Physical key must exist in supplied index", () => GameModelFiles.ValidateKey(known[0], known));
    Check("Spaces and apostrophes remain valid indexed keys", () => GameModelFiles.ValidateKey(known[1], known));
    Check("Unicode and ampersands remain valid indexed keys", () => GameModelFiles.ValidateKey(known[2], known));
    Check("Logical profile ID cannot invent a stock key", () => Reject(() => GameModelFiles.ValidateKey("1481_CM_PUNK_GM", known), "stock-model index"));
    Check("Unknown index cannot permit a key", () => Reject(() => GameModelFiles.ValidateKey(known[0], null), "stock-model index"));
    Check("Traversal, rooted paths and argument quotes are rejected", () =>
    {
        foreach (string key in new[] { "../312_CM_Punk", "..\\312_CM_Punk", "/312_CM_Punk", "C:\\312_CM_Punk", ".", "..", "312_\"CM_Punk", "312_CM_Punk\n", "312_CM_Punk\0", "312_CM\tPunk" })
            Reject(() => GameModelFiles.ValidateKey(key, new[] { key }), "valid game-model key");
    });
    Check("Extractor requires successful exit even with existing STOCKDIR", () => Reject(() => GameModelFiles.StockDirectory(1, new[] { "STOCKDIR " + root }), "code 1"));
    Check("Extractor requires STOCKDIR rather than any existing path", () => Reject(() => GameModelFiles.StockDirectory(0, new[] { root }), "could not be loaded"));
    Check("Extractor requires the returned directory to exist", () => Reject(() => GameModelFiles.StockDirectory(0, new[] { "STOCKDIR " + Path.Combine(root, "missing") }), "existing model folder"));
    Check("Successful STOCKDIR preserves spaces and apostrophes", () =>
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "CM Punk's Model")).FullName;
        Equal(folder, GameModelFiles.StockDirectory(0, new[] { "EXTRACT 2/2", "STOCKDIR " + folder }));
    });
    Check("Last STOCKDIR output wins", () =>
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "newer model")).FullName;
        Equal(folder, GameModelFiles.StockDirectory(0, new[] { "STOCKDIR " + root, "STOCKDIR " + folder }));
    });
    Check("Native missing-game errors remain readable", () => Reject(() => GameModelFiles.StockDirectory(1,
        new[] { "Traceback (most recent call last):", "  File \"extract_stock.py\", line 1", "OSError: oo2core_9_win64.dll is missing", "EXTRACT 1/2" }), "oo2core_9_win64.dll is missing"));
    Console.WriteLine($"{passed} game-model bridge file/argument checks passed. Windows RunPy execution is not exercised by this portable suite.");
    return 0;
}
finally { Directory.Delete(root, true); }
