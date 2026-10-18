using System;
using System.Diagnostics;
using GorillaStudio.NameFolders;

namespace GorillaStudio.NameFolders
{
    public static class DiscoveryTauntBridge
    {
        public static void ConfigureProcess(object owner, Process process, string script,
            System.Collections.Generic.IEnumerable<string> args) { }
    }
}

// The environment bridge is exercised without loading the Windows-only UI.
namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        public static bool FastPreviewEnabled { get; set; } = true;
        public static bool FastRenderEnabled { get; set; }
    }
}

internal static class Program
{
    private static int passed;

    private static void Main()
    {
        Equal("Existing renderer flags remain before the fast-preview overrides", "hide=coat res=512 res=256 samples=8",
            Configure(@"C:\studio\pipeline\gorilla_render.py", new[] { "folder", "taunt", "out", "quick=1" }, "hide=coat res=512"));
        Equal("A fresh preview gets lower detail", "res=256 samples=8",
            Configure("/studio/pipeline/gorilla_render.py", new[] { "folder", "taunt", "out", "quick=1" }, null));
        FolderBridge.FastPreviewEnabled = false;
        Equal("Disabling fast preview keeps the original environment", "res=640",
            Configure("gorilla_render.py", new[] { "quick=1" }, "res=640"));
        FolderBridge.FastPreviewEnabled = true;
        Equal("Full MAKE keeps its original renderer flags", "sentinel",
            Configure("gorilla_render.py", new[] { "hair=auto" }, "sentinel"));
        Equal("Model preparation keeps its original renderer flags", "sentinel",
            Configure("gorilla_render.py", new[] { "prepare=model" }, "sentinel"));
        Equal("Roster loading keeps its original environment", "sentinel",
            Configure("roster_lookup.py", new[] { "quick=1" }, "sentinel"));
        Equal("Explicit non-preview mode keeps its original environment", "sentinel",
            Configure("gorilla_render.py", new[] { "quick=0" }, "sentinel"));
        Equal("Missing arguments keep the original environment", "sentinel",
            Configure("gorilla_render.py", null, "sentinel"));
        Equal("Missing script keeps the original environment", "sentinel",
            Configure(null, new[] { "quick=1" }, "sentinel"));

        string[] arguments = { "folder", "taunt", "out", "quick=1", "frames=1,40", "end=freeze", "slot=1481" };
        string before = string.Join("|", arguments);
        string extra = Configure("gorilla_render.py", arguments, null);
        Equal("Frame selection and all original arguments are unchanged", before, string.Join("|", arguments));
        Equal("Animation frame rate is never overridden", false, extra.Contains("fps="));
        Equal("The final quick flag wins when preview is disabled", "sentinel",
            Configure("gorilla_render.py", new[] { "quick=1", "quick=0" }, "sentinel"));
        Equal("The final quick flag wins when preview is enabled", "sentinel res=256 samples=8",
            Configure("gorilla_render.py", new[] { "quick=0", "quick=1" }, "sentinel"));
        Equal("Taunt preparation is never reduced to preview detail", "sentinel",
            Configure("gorilla_render.py", new[] { "quick=1", "prepare=taunt" }, "sentinel"));

        FolderBridge.FastRenderEnabled = true;
        Equal("Fast MAKE reduces samples while keeping inherited resolution", "res=1024 samples=64 samples=32",
            Configure(@"C:\studio\pipeline\gorilla_render.py", new[] { "folder", "taunt", "out", "hair=auto" }, "res=1024 samples=64"));
        Equal("Fast MAKE never adds a resolution override", "samples=32",
            Configure("gorilla_render.py", new[] { "folder", "taunt", "out" }, null));
        Equal("Fast MAKE follows the final quick flag", "sentinel samples=32",
            Configure("gorilla_render.py", new[] { "quick=1", "quick=0" }, "sentinel"));
        Equal("Fast preview detail remains separate from fast MAKE", "sentinel res=256 samples=8",
            Configure("gorilla_render.py", new[] { "quick=0", "quick=1" }, "sentinel"));
        FolderBridge.FastPreviewEnabled = false;
        Equal("Disabling fast preview never applies the fast MAKE setting to preview", "sentinel",
            Configure("gorilla_render.py", new[] { "quick=1" }, "sentinel"));
        Equal("Fast MAKE never changes taunt preparation", "sentinel",
            Configure("gorilla_render.py", new[] { "prepare=taunt" }, "sentinel"));
        Equal("Fast MAKE never changes model preparation even with a quick flag", "sentinel",
            Configure("gorilla_render.py", new[] { "prepare=model", "quick=1" }, "sentinel"));
        Equal("Fast MAKE never changes stock-model extraction", "sentinel",
            Configure("extract_stock.py", new[] { "312_CM_Punk" }, "sentinel"));
        Equal("Fast MAKE never changes roster loading", "sentinel",
            Configure("roster_lookup.py", new[] { "folder" }, "sentinel"));
        Equal("Fast MAKE never changes preview generation for the taunt browser", "sentinel",
            Configure("preview_game.py", new[] { "one", "taunt" }, "sentinel"));
        FolderBridge.FastRenderEnabled = false;
        Equal("Switching fast MAKE off restores the original renderer environment", "res=1024 samples=64",
            Configure("gorilla_render.py", new[] { "folder", "taunt", "out" }, "res=1024 samples=64"));
        Console.WriteLine(passed + " preview-process checks passed; no engine process launched.");
    }

    private static string Configure(string script, string[] arguments, string extra)
    {
        using (var process = new Process())
        {
            process.StartInfo.EnvironmentVariables["GORILLA_EXTRA"] = extra;
            FolderBridge.ConfigurePreviewProcess(null, process, script, arguments);
            return process.StartInfo.EnvironmentVariables["GORILLA_EXTRA"];
        }
    }

    private static void Equal<T>(string description, T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new InvalidOperationException(description + ": expected " + expected + ", got " + actual);
        passed++;
    }
}
