using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;

namespace GorillaStudio.NameFolders
{
    public static partial class FolderBridge
    {
        static partial void SelectAttireArguments(object owner, string script, ref string[] arguments);
        // The native RunPy remains responsible for process startup, cancellation,
        // output dispatch and exit handling. Only complete quick previews can skip it.
        public static async Task<ValueTuple<int, List<string>>> RunPreviewProcess(object owner, string script,
            IEnumerable<string> args, Action<string> onLine, Action<Process> started)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            string[] arguments = args == null ? null : args.ToArray();
            if (arguments != null && arguments.Any(DiscoveryTauntSearch.IsUnavailableKey))
            {
                string message = "This taunt has no playable animation. Check its search result for the missing file or unsupported format.";
                if (onLine != null) onLine(message);
                return new ValueTuple<int, List<string>>(1, new List<string> { message });
            }
            SelectAttireArguments(owner, script, ref arguments);
            bool preview = PreviewCache.IsPreviewRequest(script, arguments);
            bool fast = preview ? FastPreviewEnabled : FastRenderEnabled;
            bool cached = false, frozen = false;
            var timer = Stopwatch.StartNew();
            try
            {
                try { SetPerformanceBusy(owner, true); frozen = true; } catch { }
                PreviewRequest request = preview && frozen ? PreviewRequest.Capture(owner, script, arguments, fast) : null;
                string key = request == null ? null : await Task.Run(() => request.Fingerprint());
                PreviewCacheStore store = null;
                if (key != null && request.EngineRootUnchanged())
                {
                    try { store = new PreviewCacheStore(Path.Combine(StorageDirectory, "preview-cache")); } catch { }
                    if (store != null)
                    {
                        List<string> lines;
                        if (store.TryRead(key, out lines))
                        {
                            cached = true;
                            if (onLine != null) foreach (string line in lines) onLine(line);
                            try { DiscoveryTauntBridge.CapturePreview(owner, script, arguments, lines); } catch { }
                            return new ValueTuple<int, List<string>>(0, lines);
                        }
                    }
                }

                ValueTuple<int, List<string>> result = await InvokeNativeRender(owner, script, arguments, onLine, started);
                if (store != null && result.Item1 == 0 && fast == FastPreviewEnabled)
                {
                    // Rendering can prepare files or the user can edit a texture while
                    // Blender runs. Such results must not be saved under an older key.
                    string after = await Task.Run(() => request.Fingerprint());
                    if (key == after) await Task.Run(() => store.Save(key, result.Item2));
                }
                if (preview && result.Item1 == 0)
                    try { DiscoveryTauntBridge.CapturePreview(owner, script, arguments, result.Item2); } catch { }
                return result;
            }
            finally
            {
                timer.Stop();
                if (frozen) try { SetPerformanceBusy(owner, false); } catch { }
                try { ReportRenderTiming(owner, preview, fast, cached, timer.Elapsed.TotalSeconds); } catch { }
            }
        }

        private static Task<ValueTuple<int, List<string>>> InvokeNativeRender(object owner, string script,
            IEnumerable<string> args, Action<string> onLine, Action<Process> started)
        {
            MethodInfo run = owner.GetType().GetMethod("RunPy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(IEnumerable<string>), typeof(Action<string>), typeof(Action<Process>) }, null);
            if (run == null) throw new MissingMethodException(owner.GetType().FullName, "RunPy");
            try
            {
                var task = run.Invoke(owner, new object[] { script, args, onLine, started }) as Task<ValueTuple<int, List<string>>>;
                if (task == null) throw new InvalidOperationException("The native renderer returned an unsupported task.");
                return task;
            }
            catch (TargetInvocationException error)
            {
                if (error.InnerException != null) ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private sealed class PreviewRequest
        {
            private string[] arguments;
            private string script, baseDirectory, engine, studio, library, blender, game, setup;
            private string discoveryEngineRoot, inheritedEngineRoot;
            private bool fast;

            internal static PreviewRequest Capture(object owner, string script, string[] args, bool fast)
            {
                try
                {
                    return new PreviewRequest {
                        arguments = args, script = script, fast = fast,
                        baseDirectory = ReadPath(owner, "BaseDir") ?? AppDomain.CurrentDomain.BaseDirectory,
                        engine = ReadPath(owner, "EngineExe"), studio = ReadPath(owner, "Studio"),
                        library = ReadPath(owner, "Library"), blender = ReadPath(owner, "BlenderPath"),
                        game = ReadPath(owner, "Game"), setup = ReadPath(owner, "SetupFolder"),
                        discoveryEngineRoot = DiscoveryEngineRoot,
                        inheritedEngineRoot = Environment.GetEnvironmentVariable("GS_ROOT")
                    };
                }
                catch { return null; }
            }

            internal string Fingerprint()
            {
                try
                {
                    if (arguments == null || arguments.Length < 1 || !Directory.Exists(arguments[0]) || !Directory.Exists(baseDirectory)) return null;
                    // The taunt bridge sets GS_ROOT on each child process rather than
                    // globally. Track that effective overlay as well as the app files.
                    if (!EngineRootUnchanged()) return null;
                    string effectiveRoot = !string.IsNullOrWhiteSpace(discoveryEngineRoot) ? discoveryEngineRoot
                        : (!string.IsNullOrWhiteSpace(inheritedEngineRoot) ? inheritedEngineRoot : baseDirectory);
                    effectiveRoot = Path.GetFullPath(effectiveRoot);
                    if (!Directory.Exists(effectiveRoot)) return null;
                    var files = new HashSet<string>(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    var visited = new HashSet<string>(files.Comparer);
                    var signature = new StringBuilder();
                    AddTree(arguments[0], files, visited, signature);
                    if (files.Count == 0) return null;
                    string model = DirectoryLinks.Resolve(arguments[0]);
                    AddDiscoveryAncestors(arguments[0], files, visited, signature);
                    AddDiscoveryAncestors(model, files, visited, signature);
                    AddDiscoveryContext(setup, files, visited, signature);
                    foreach (string name in new[] { "library", "caps", "start", "faces", "gamepack" })
                        AddOptionalTree(Path.Combine(baseDirectory, "data", name), files, visited, signature, name == "library");
                    foreach (string name in new[] { "library", "caps", "start", "faces", "gamepack" })
                        AddOptionalTree(Path.Combine(effectiveRoot, "data", name), files, visited, signature, name == "library");
                    AddOptionalTree(library, files, visited, signature, true);
                    string engineFolder = Path.Combine(baseDirectory, "engine");
                    string executable = engine ?? Path.Combine(engineFolder, "gs_engine.exe");
                    if (File.Exists(executable)) AddFile(executable, files);
                    else if (!File.Exists(script)) return null;
                    if (Directory.Exists(engineFolder))
                    {
                        foreach (string file in Directory.GetFiles(engineFolder)) AddFile(file, files);
                        AddOptionalTree(Path.Combine(engineFolder, "blender"), files, visited, signature);
                        AddOptionalTree(Path.Combine(engineFolder, "bakery"), files, visited, signature);
                    }
                    if (File.Exists(script)) AddFile(script, files);
                    if (!string.IsNullOrWhiteSpace(studio)) AddOptionalTree(Path.Combine(studio, "pipeline"), files, visited, signature);
                    if (!string.IsNullOrWhiteSpace(blender))
                    {
                        if (!File.Exists(blender)) return null;
                        AddFile(blender, files);
                    }
                    if (!string.IsNullOrWhiteSpace(game))
                    {
                        AddOptionalFile(Path.Combine(game, "WWE2K26_x64.exe"), files);
                        foreach (string dll in Directory.Exists(game) ? Directory.GetFiles(game, "oo2core*.dll") : new string[0]) AddFile(dll, files);
                    }
                    AddOptionalFile(typeof(FolderBridge).Assembly.Location, files);
                    foreach (string name in new[] { "GORILLA_EXTRA", "GORILLA_BLENDER", "GORILLA_GAME" })
                        signature.Append(name).Append('=').Append(Environment.GetEnvironmentVariable(name) ?? "").Append('\n');
                    signature.Append("game=").Append(game).Append("\nblender=").Append(blender).Append("\nscript=").Append(script)
                        .Append("\neffective-root=").Append(effectiveRoot).Append("\ninherited-GS_ROOT=").Append(inheritedEngineRoot);
                    string key = PreviewCache.Key(arguments, fast, files, signature.ToString());
                    return EngineRootUnchanged() ? key : null;
                }
                catch { return null; }
            }

            internal bool EngineRootUnchanged()
            {
                return string.Equals(discoveryEngineRoot, DiscoveryEngineRoot, StringComparison.Ordinal)
                    && string.Equals(inheritedEngineRoot, Environment.GetEnvironmentVariable("GS_ROOT"), StringComparison.Ordinal);
            }

            private static void AddDiscoveryAncestors(string model, HashSet<string> files, HashSet<string> visited, StringBuilder signature)
            {
                DirectoryInfo directory = new DirectoryInfo(model);
                for (int level = 0; directory != null && level < 4; level++, directory = directory.Parent)
                    AddDiscoveryContext(directory.FullName, files, visited, signature);
            }

            private static void AddDiscoveryContext(string folder, HashSet<string> files, HashSet<string> visited, StringBuilder signature)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
                foreach (string name in new[] { "Roster", "CAS", "UI" }) AddOptionalTree(Path.Combine(folder, name), files, visited, signature);
                AddOptionalFile(Path.Combine(folder, "Characters", "CharacterMapping.jsfb"), files);
                AddOptionalFile(Path.Combine(folder, "Characters", "CostumesTable.jsfb"), files);
                AddOptionalFile(Path.Combine(folder, "Sdb", "ENG.sdb"), files);
            }

            private static void AddOptionalTree(string folder, HashSet<string> files, HashSet<string> visited, StringBuilder signature, bool libraryRoot = false)
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) AddTree(folder, files, visited, signature, libraryRoot);
            }

            private static void AddTree(string folder, HashSet<string> files, HashSet<string> visited, StringBuilder signature, bool libraryRoot = false)
            {
                string resolved = DirectoryLinks.Resolve(folder);
                if (!visited.Add(resolved)) return;
                var directory = new DirectoryInfo(resolved);
                long stamp = directory.LastWriteTimeUtc.Ticks;
                foreach (string file in Directory.GetFiles(resolved)) AddFile(file, files);
                foreach (string child in Directory.GetDirectories(resolved).OrderBy(path => path, StringComparer.Ordinal))
                {
                    // These files display completed previews; they never feed model
                    // rendering. Capturing a thumbnail must not invalidate its preview.
                    if (libraryRoot && string.Equals(Path.GetFileName(child), "previews_game", StringComparison.OrdinalIgnoreCase)) continue;
                    AddTree(child, files, visited, signature);
                }
                directory.Refresh();
                if (!directory.Exists || directory.LastWriteTimeUtc.Ticks != stamp) throw new IOException("Preview inputs changed during scanning.");
                // Directory times also distinguish empty/missing/new attire folders.
                signature.Append(resolved).Append(':').Append(libraryRoot ? "library-inputs" : stamp.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }

            private static void AddFile(string path, HashSet<string> files)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) path = DirectoryLinks.Resolve(path);
                files.Add(Path.GetFullPath(path));
            }

            private static void AddOptionalFile(string path, HashSet<string> files)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) AddFile(path, files);
            }

            private static string ReadPath(object owner, string name)
            {
                for (Type type = owner.GetType(); type != null; type = type.BaseType)
                {
                    FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (field != null) return field.GetValue(field.IsStatic ? null : owner) as string;
                }
                return null;
            }
        }
    }
}
