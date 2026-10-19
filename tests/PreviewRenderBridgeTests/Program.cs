using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int checks;
    private static string root;
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1cAAAAASUVORK5CYII=");

    private static async Task<int> Main()
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-preview-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FolderBridge.StoragePath = Path.Combine(root, "storage");
        try
        {
            await CacheAndCallbacks();
            await ChangedInputsAndSettings();
            await FailedAndChangingInputs();
            await MakeAndPrepareBypass();
            await ExceptionBehavior();
            await JunctionAndOutputIsolation();
            await DiscoveryOverlayInvalidation();
            await CapturedTauntThumbnails();
            await UnavailableTauntDoesNotStart();
            Console.WriteLine(checks + " preview process bridge checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static async Task UnavailableTauntDoesNotStart()
    {
        FakeOwner owner = Create("unavailable");
        foreach (string mode in new[] { "quick=1", "end=loop", "prepare=taunt" })
        {
            var callbacks = new List<string>();
            var result = await FolderBridge.RunPreviewProcess(owner, owner.Script,
                new[] { owner.Folder, "discovery_unavailable_62247a001", owner.Output, mode }, callbacks.Add,
                process => { throw new Exception("Unavailable motion must not start a native process"); });
            Equal(1, result.Item1);
            Equal(0, owner.Calls);
            Equal(true, result.Item2.Single().Contains("no playable animation"));
            Equal(result.Item2.Single(), callbacks.Single());
            Equal(false, owner.Busy);
        }
    }

    private static async Task CacheAndCallbacks()
    {
        FakeOwner owner = Create("reuse");
        int started = 0;
        var callbacks = new List<string>();
        ValueTuple<int, List<string>> first = await Run(owner, callbacks.Add, process => { Equal(true, process != null); started++; });
        Equal(0, first.Item1); Equal(1, owner.Calls); Equal(1, started);
        Equal(true, callbacks.Any(line => line.StartsWith("RESULT ")));
        Equal(false, owner.Busy);
        Equal(false, owner.Timings.Last().Cached);
        Equal(true, owner.Timings.Last().Preview);
        Equal(true, owner.Timings.Last().Fast);
        Equal(true, owner.Timings.Last().Seconds >= 0);
        callbacks.Clear();
        ValueTuple<int, List<string>> second = await Run(owner, callbacks.Add, process => started++);
        Equal(0, second.Item1); Equal(1, owner.Calls); Equal(1, started);
        Equal("STAGE 9/9 Cached preview", callbacks[0]);
        Equal(true, callbacks[1].StartsWith("RESULT "));
        Equal(true, owner.Timings.Last().Cached);
        Equal(false, owner.Busy);
        string cached = PreviewPath(second.Item2);
        Equal(true, cached.StartsWith(FolderBridge.StoragePath, StringComparison.Ordinal));
        File.Delete(owner.Preview);
        Equal(true, File.Exists(cached));
        await Run(owner);
        Equal(1, owner.Calls);
        File.Delete(cached);
        await Run(owner);
        Equal(2, owner.Calls);
    }

    private static async Task ChangedInputsAndSettings()
    {
        FakeOwner owner = Create("changes");
        await Run(owner); await Run(owner); Equal(1, owner.Calls);
        Change(owner.Texture); await Run(owner); Equal(2, owner.Calls);
        Change(owner.Model); await Run(owner); Equal(3, owner.Calls);
        await Run(owner, null, null, "hair=black"); Equal(4, owner.Calls);
        await Run(owner, null, null, "hair=black"); Equal(4, owner.Calls);
        await Run(owner, null, null, "hair=black", "hide=lambert1"); Equal(5, owner.Calls);
        await Run(owner, null, null, "hair=black", "end=still"); Equal(6, owner.Calls);
        await Run(owner, null, null, "hair=black", "slot=1234"); Equal(7, owner.Calls);
        Change(owner.Roster); await Run(owner); Equal(8, owner.Calls);
        Change(owner.Cap); await Run(owner); Equal(9, owner.Calls);
        Change(owner.Start); await Run(owner); Equal(10, owner.Calls);
        Change(owner.LibraryFile); await Run(owner); Equal(11, owner.Calls);
        Change(owner.BlenderScript); await Run(owner); Equal(12, owner.Calls);
        Change(owner.Engine); await Run(owner); Equal(13, owner.Calls);
        Change(owner.BlenderExe); await Run(owner); Equal(14, owner.Calls);
        string previous = Environment.GetEnvironmentVariable("GORILLA_EXTRA");
        try { Environment.SetEnvironmentVariable("GORILLA_EXTRA", "samples=17"); await Run(owner); Equal(15, owner.Calls); }
        finally { Environment.SetEnvironmentVariable("GORILLA_EXTRA", previous); }
        FolderBridge.FastPreviewEnabled = false;
        try { await Run(owner); Equal(16, owner.Calls); await Run(owner); Equal(16, owner.Calls); Equal(false, owner.Timings.Last().Fast); }
        finally { FolderBridge.FastPreviewEnabled = true; }
        await Run(owner); Equal(16, owner.Calls); Equal(true, owner.Timings.Last().Cached);
    }

    private static async Task FailedAndChangingInputs()
    {
        FakeOwner owner = Create("failed"); owner.Code = 1;
        await Run(owner); await Run(owner); Equal(2, owner.Calls);
        owner.Code = 0; owner.Malformed = true;
        await Run(owner); await Run(owner); Equal(4, owner.Calls);
        owner.Malformed = false;
        owner.AfterRun = () => Change(owner.Texture);
        await Run(owner); Equal(5, owner.Calls);
        owner.AfterRun = null;
        await Run(owner); Equal(6, owner.Calls);
        await Run(owner); Equal(6, owner.Calls);
        File.Delete(owner.Model); File.Delete(owner.Texture);
        await Run(owner); await Run(owner); Equal(8, owner.Calls);
        Equal(false, owner.Timings.Last().Cached);
        FakeOwner broken = Create("broken-resolve");
        DirectoryLinks.FailPath = broken.Folder;
        try { await Run(broken); await Run(broken); Equal(2, broken.Calls); }
        finally { DirectoryLinks.FailPath = null; }
        FakeOwner notFrozen = Create("not-frozen"); notFrozen.FailFreeze = true;
        await Run(notFrozen); await Run(notFrozen); Equal(2, notFrozen.Calls);
    }

    private static async Task MakeAndPrepareBypass()
    {
        FakeOwner owner = Create("make");
        await Run(owner);
        int afterPreview = owner.Calls;
        foreach (string[] suffix in new[] { new[] { "quick=0" }, new[] { "quick=1", "quick=0" }, new[] { "quick=1", "prepare=taunt" }, new string[0] })
        {
            string[] args = new[] { owner.Folder, "Taunt1", owner.Output }.Concat(suffix).ToArray();
            Action<string> callback = line => owner.ExternalLines++;
            Action<Process> started = process => owner.ExternalStarts++;
            ValueTuple<int, List<string>> result = await FolderBridge.RunPreviewProcess(owner, owner.Script, args, callback, started);
            Equal(0, result.Item1);
            Equal(true, owner.LastArgs.SequenceEqual(args));
            Equal(callback, owner.LastCallback);
            Equal(started, owner.LastStarted);
            Equal(false, owner.Timings.Last().Preview);
            Equal(false, owner.Timings.Last().Cached);
        }
        Equal(afterPreview + 4, owner.Calls); Equal(4, owner.ExternalStarts);
        Equal(true, owner.ExternalLines >= 8);
        ValueTuple<int, List<string>> nonRenderer = await FolderBridge.RunPreviewProcess(owner, Path.Combine(Path.GetDirectoryName(owner.Script), "setup.py"), new[] { "quick=1" }, null, null);
        Equal(0, nonRenderer.Item1); Equal(afterPreview + 5, owner.Calls);
    }

    private static async Task ExceptionBehavior()
    {
        FakeOwner owner = Create("exception");
        var original = new IOException("native failure"); owner.Throw = original;
        try { await Run(owner); throw new Exception("Expected native failure"); }
        catch (IOException error) { Equal(original, error); }
        Equal(false, owner.Busy); Equal(1, owner.Timings.Count);
        var synchronous = new SyncThrowOwner(original);
        try { await FolderBridge.RunPreviewProcess(synchronous, "other.py", new string[0], null, null); throw new Exception("Expected sync failure"); }
        catch (IOException error) { Equal(original, error); }
        owner.Throw = null;
        await Run(owner);
        var callbackFailure = new InvalidOperationException("callback failed");
        try { await Run(owner, line => { throw callbackFailure; }); throw new Exception("Expected callback failure"); }
        catch (InvalidOperationException error) { Equal(callbackFailure, error); }
        Equal(false, owner.Busy);
    }

    private static async Task JunctionAndOutputIsolation()
    {
        FakeOwner owner = Create("junction");
        string source = owner.Folder;
        string alias = Path.Combine(Path.GetDirectoryName(source), "Alias");
        Directory.CreateSymbolicLink(alias, source);
        owner.Folder = alias;
        await Run(owner); await Run(owner); Equal(1, owner.Calls);
        Change(owner.Texture);
        await Run(owner); Equal(2, owner.Calls);
        string first = PreviewPath((await Run(owner)).Item2);
        await Run(owner, null, null, "hair=green");
        File.WriteAllBytes(owner.Preview, Png.Concat(new byte[] { 99 }).ToArray());
        ValueTuple<int, List<string>> cached = await Run(owner);
        Equal(first, PreviewPath(cached.Item2)); Equal(true, File.ReadAllBytes(first).SequenceEqual(Png));
    }

    private static async Task DiscoveryOverlayInvalidation()
    {
        FakeOwner owner = Create("overlay-app");
        string overlay = Path.Combine(root, "discovery-overlay");
        string manifest = Write(overlay, "data/library/manifest.json");
        string decoded = Write(overlay, "data/gamepack/decoded/62541_Undisputed2.npz");
        string original = Write(root, "original-gamepack/taunt001.npz");
        string linked = Path.Combine(overlay, "data", "gamepack", "decoded", "taunt001.npz");
        File.CreateSymbolicLink(linked, original);
        string previous = Environment.GetEnvironmentVariable("GS_ROOT");
        FolderBridge.DiscoveryEngineRoot = overlay;
        try
        {
            await Run(owner); await Run(owner); Equal(1, owner.Calls);
            Change(decoded); await Run(owner); Equal(2, owner.Calls);
            Change(manifest); await Run(owner); Equal(3, owner.Calls);
            Change(original); await Run(owner); Equal(4, owner.Calls);
            string secondOverlay = Path.Combine(root, "discovery-overlay-2");
            Write(secondOverlay, "data/library/manifest.json"); Write(secondOverlay, "data/gamepack/decoded/62541_Undisputed2.npz");
            FolderBridge.DiscoveryEngineRoot = secondOverlay;
            await Run(owner); Equal(5, owner.Calls);
            await Run(owner); Equal(5, owner.Calls);
            owner.AfterRun = () => FolderBridge.DiscoveryEngineRoot = overlay;
            await Run(owner, null, null, "hair=overlay-change"); Equal(6, owner.Calls);
            owner.AfterRun = null;
            FolderBridge.DiscoveryEngineRoot = secondOverlay;
            await Run(owner, null, null, "hair=overlay-change"); Equal(7, owner.Calls);
            await Run(owner, null, null, "hair=overlay-change"); Equal(7, owner.Calls);
            FolderBridge.DiscoveryEngineRoot = null;
            Environment.SetEnvironmentVariable("GS_ROOT", overlay);
            await Run(owner); Equal(8, owner.Calls);
            await Run(owner); Equal(8, owner.Calls);
            Change(decoded); await Run(owner); Equal(9, owner.Calls);
            Environment.SetEnvironmentVariable("GS_ROOT", secondOverlay);
            await Run(owner); Equal(10, owner.Calls);
            Environment.SetEnvironmentVariable("GS_ROOT", previous);
            FolderBridge.DiscoveryEngineRoot = overlay;
            await Run(owner); Equal(11, owner.Calls);
            DirectoryLinks.BeforeResolve = path => {
                if (path != owner.Folder) return;
                FolderBridge.DiscoveryEngineRoot = secondOverlay;
                DirectoryLinks.BeforeResolve = null;
            };
            await Run(owner); Equal(12, owner.Calls);
            Equal(false, owner.Timings.Last().Cached);
            FolderBridge.DiscoveryEngineRoot = Path.Combine(root, "missing-overlay");
            await Run(owner); await Run(owner); Equal(14, owner.Calls);
            Equal(false, owner.Timings.Last().Cached);
        }
        finally { DirectoryLinks.BeforeResolve = null; FolderBridge.DiscoveryEngineRoot = null; Environment.SetEnvironmentVariable("GS_ROOT", previous); }
    }

    private static async Task CapturedTauntThumbnails()
    {
        FakeOwner owner = Create("capture-app");
        string overlay = Path.Combine(root, "capture-overlay");
        string manifest = Write(overlay, "data/library/manifest.json");
        string motion = Write(overlay, "data/gamepack/decoded/discovery_taunt.npz");
        FolderBridge.DiscoveryEngineRoot = overlay;
        int captured = 0;
        bool captureUsedCache = false;
        DiscoveryTauntBridge.OnCapture = (actualOwner, actualScript, actualArgs, actualLines) => {
            Equal(owner, actualOwner); Equal(owner.Script, actualScript);
            Equal(true, PreviewCache.IsPreviewRequest(actualScript, actualArgs));
            List<string> lines = actualLines.ToList();
            captured++;
            captureUsedCache = PreviewPath(lines).StartsWith(FolderBridge.StoragePath, StringComparison.Ordinal);
            string directory = Directory.CreateDirectory(Path.Combine(overlay, "data", "library", "previews_game")).FullName;
            File.Copy(PreviewPath(lines), Path.Combine(directory, "discovery_taunt.png"), true);
        };
        try
        {
            await Run(owner); Equal(1, owner.Calls); Equal(1, captured); Equal(false, captureUsedCache);
            await Run(owner); Equal(1, owner.Calls); Equal(2, captured); Equal(true, captureUsedCache);
            string thumb = Path.Combine(overlay, "data", "library", "previews_game", "discovery_taunt.png");
            Change(thumb); await Run(owner); Equal(1, owner.Calls);
            Directory.Delete(Path.GetDirectoryName(thumb), true);
            await Run(owner); Equal(1, owner.Calls);
            Change(manifest); await Run(owner); Equal(2, owner.Calls);
            Change(motion); await Run(owner); Equal(3, owner.Calls);
            // An actual model folder with the same name is still a render input.
            string modelTexture = Write(owner.Folder, "previews_game/body.dds");
            await Run(owner); Equal(4, owner.Calls);
            Change(modelTexture); await Run(owner); Equal(5, owner.Calls);
            DiscoveryTauntBridge.OnCapture = (actualOwner, actualScript, actualArgs, actualLines) => { captured++; throw new IOException("thumbnail failure"); };
            int before = captured;
            ValueTuple<int, List<string>> hit = await Run(owner);
            Equal(0, hit.Item1); Equal(5, owner.Calls); Equal(before + 1, captured);
            ValueTuple<int, List<string>> rendered = await Run(owner, null, null, "hair=new");
            Equal(0, rendered.Item1); Equal(6, owner.Calls); Equal(before + 2, captured);
            before = captured;
            owner.Code = 1;
            await Run(owner, null, null, "hair=failed"); Equal(before, captured);
            owner.Code = 0;
            await FolderBridge.RunPreviewProcess(owner, owner.Script, new[] { owner.Folder, "Taunt1", owner.Output }, null, null);
            Equal(before, captured);
            await FolderBridge.RunPreviewProcess(owner, owner.Script, new[] { owner.Folder, "Taunt1", owner.Output, "prepare=model", "quick=1" }, null, null);
            Equal(before, captured);
        }
        finally { DiscoveryTauntBridge.OnCapture = null; FolderBridge.DiscoveryEngineRoot = null; }
    }

    private static Task<ValueTuple<int, List<string>>> Run(FakeOwner owner, Action<string> callback = null, Action<Process> started = null, params string[] options)
    {
        return FolderBridge.RunPreviewProcess(owner, owner.Script, new[] { owner.Folder, "Taunt1", owner.Output, "quick=1" }.Concat(options), callback, started);
    }

    private static FakeOwner Create(string name)
    {
        string app = Path.Combine(root, name);
        string model = Write(app, "Characters/PAC/body.mcd");
        string engine = Write(app, "engine/gs_engine.exe");
        var owner = new FakeOwner(app, Path.GetDirectoryName(model), engine) {
            Model = model, Texture = Write(app, "Characters/PAC/face.dds"), Roster = Write(app, "Roster/CharProfileTable.jsfb"),
            Cap = Write(app, "data/caps/Taunt1.json"), Start = Write(app, "data/start/Taunt1.json"),
            LibraryFile = Write(app, "data/library/Taunt1.json"), BlenderScript = Write(app, "engine/blender/render_kit.pyc"),
            BlenderExe = Write(app, "blender.exe"), Script = Path.Combine(app, "pipeline", "gorilla_render.py"),
            Output = Directory.CreateDirectory(Path.Combine(app, "output")).FullName
        };
        owner.Preview = Path.Combine(owner.Output, "shared-preview.png");
        owner.SetBlender();
        return owner;
    }

    private static string Write(string folder, string name) { string path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "fixture"); return path; }
    private static void Change(string path) { File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5)); }
    private static string PreviewPath(List<string> lines) { using (JsonDocument document = JsonDocument.Parse(lines.Last(line => line.StartsWith("RESULT ")).Substring(7))) return document.RootElement.GetProperty("preview").GetString(); }
    private static void Equal<T>(T expected, T actual) { checks++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + "; got " + actual); }

    internal sealed class FakeOwner
    {
        private static string BaseDir, EngineExe;
        private string Library, BlenderPath;
        public string Folder, Model, Texture, Roster, Cap, Start, LibraryFile, BlenderScript, BlenderExe, Engine, Script, Output, Preview;
        public string[] LastArgs;
        public Action<string> LastCallback;
        public Action<Process> LastStarted;
        public bool Busy, Malformed, FailFreeze;
        public int Calls, Code, ExternalLines, ExternalStarts;
        public Exception Throw;
        public Action AfterRun;
        public List<Timing> Timings = new List<Timing>();
        public FakeOwner(string app, string folder, string engine) { BaseDir = app; EngineExe = engine; Engine = engine; Folder = folder; Library = Path.Combine(app, "data", "library"); }
        public void SetBlender() { BlenderPath = BlenderExe; }
        private async Task<ValueTuple<int, List<string>>> RunPy(string script, IEnumerable<string> args, Action<string> callback, Action<Process> started)
        {
            Calls++; LastArgs = args.ToArray(); LastCallback = callback; LastStarted = started;
            if (Throw != null) throw Throw;
            started?.Invoke(new Process());
            await Task.Yield();
            File.WriteAllBytes(Preview, Png);
            var lines = new List<string> { "STAGE 8/9 Preview", Malformed ? "RESULT {broken" : "RESULT " + JsonSerializer.Serialize(new { name = "PAC", id = 924, frames = 59, preview = Preview }) };
            foreach (string line in lines) callback?.Invoke(line);
            AfterRun?.Invoke();
            return new ValueTuple<int, List<string>>(Code, lines);
        }
    }

    internal sealed class SyncThrowOwner
    {
        private readonly Exception error;
        internal SyncThrowOwner(Exception error) { this.error = error; }
        private Task<ValueTuple<int, List<string>>> RunPy(string script, IEnumerable<string> args, Action<string> callback, Action<Process> started) { throw error; }
    }

    internal sealed class Timing
    {
        internal bool Preview, Fast, Cached;
        internal double Seconds;
    }
}

namespace GorillaStudio.NameFolders
{
    internal static class DiscoveryTauntBridge
    {
        internal static Action<object, string, IEnumerable<string>, IEnumerable<string>> OnCapture;
        internal static void CapturePreview(object owner, string script, IEnumerable<string> args, IEnumerable<string> engineLines)
        {
            OnCapture?.Invoke(owner, script, args, engineLines);
        }
    }

    public static partial class FolderBridge
    {
        public static bool FastPreviewEnabled = true, FastRenderEnabled;
        internal static string DiscoveryEngineRoot;
        internal static string StoragePath;
        internal static string StorageDirectory { get { return StoragePath; } }
        internal static void SetPerformanceBusy(object owner, bool busy)
        {
            var fake = owner as Program.FakeOwner;
            if (fake == null) return;
            if (fake.FailFreeze) throw new InvalidOperationException("Cannot freeze controls");
            fake.Busy = busy;
        }
        internal static void ReportRenderTiming(object owner, bool preview, bool fast, bool cached, double seconds)
        {
            var fake = owner as Program.FakeOwner;
            if (fake != null) fake.Timings.Add(new Program.Timing { Preview = preview, Fast = fast, Cached = cached, Seconds = seconds });
        }
    }

    internal static class DirectoryLinks
    {
        internal static string FailPath;
        internal static Action<string> BeforeResolve;
        internal static string Resolve(string path)
        {
            BeforeResolve?.Invoke(path);
            if (path == FailPath) throw new IOException("Cannot inspect model folder");
            FileSystemInfo info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            return (info.LinkTarget == null ? info : info.ResolveLinkTarget(true)).FullName;
        }
    }
}
