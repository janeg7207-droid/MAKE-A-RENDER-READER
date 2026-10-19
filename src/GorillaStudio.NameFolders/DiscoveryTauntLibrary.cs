using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public enum DiscoveryTauntUnavailableKind { MissingAnimation, UnsupportedFormat, ScanIncomplete, ReadFailure, CacheFailure }

    public sealed class DiscoveryUnavailableTaunt
    {
        public int Id { get; internal set; }
        public int Variant { get; internal set; }
        public string Clip { get; internal set; }
        public string Name { get; internal set; }
        public List<string> Names { get; internal set; }
        public DiscoveryTauntUnavailableKind Kind { get; internal set; }
        public string Reason { get; internal set; }
        // UI-only identity: never put this key in the engine taunts.json manifest.
        public string Key { get { return "discovery_unavailable_" + Clip; } }
    }

    public sealed class DiscoveryTauntImport
    {
        public string Root { get; internal set; }
        public string Library { get; internal set; }
        public int Definitions { get; internal set; }
        public int Imported { get; internal set; }
        public int Available { get; internal set; }
        public int Missing { get; internal set; }
        public int Unsupported { get; internal set; }
        public int ReadFailures { get; internal set; }
        public int CacheFailures { get; internal set; }
        public bool ScanIncomplete { get; internal set; }
        public bool AnimationRootFound { get; internal set; }
        public IReadOnlyList<string> SearchRoots { get; internal set; } = new string[0];
        public List<string> Warnings { get; } = new List<string>();
        public List<DiscoveryUnavailableTaunt> Unavailable { get; } = new List<DiscoveryUnavailableTaunt>();
        internal List<Dictionary<string, object>> Taunts { get; set; }
    }

    /// <summary>Builds a writable engine data overlay without changing Discovery or the installed application.</summary>
    public static class DiscoveryTauntLibrary
    {
        public static DiscoveryTauntImport Build(string applicationRoot, string storageRoot, string discoveryRoot)
        {
            string baseData = Path.Combine(applicationRoot, "data");
            string baseLibrary = Path.Combine(baseData, "library");
            string baseManifest = Path.Combine(baseLibrary, "taunts.json");
            var manifest = JsonObject(File.ReadAllText(baseManifest));
            object raw;
            if (!manifest.TryGetValue("taunts", out raw) || !(raw is IEnumerable))
                throw new InvalidDataException("The bundled taunt library has no animation list.");
            var entries = ((IEnumerable)raw).Cast<object>().Select(value => value as Dictionary<string, object>).ToList();
            if (entries.Any(value => value == null)) throw new InvalidDataException("A bundled taunt entry is invalid.");
            var result = new DiscoveryTauntImport { Library = baseLibrary, Taunts = entries, Available = entries.Count };
            string table = DiscoveryTaunts.FindTable(discoveryRoot);
            if (table == null) return result;

            string sourceRoot = Path.GetFullPath(discoveryRoot);
            // The CAM table's parent is MoveData; its parent is the full data root.
            DirectoryInfo tableParent = Directory.GetParent(table);
            if (tableParent != null && string.Equals(tableParent.Name, "MoveData", StringComparison.OrdinalIgnoreCase))
                sourceRoot = tableParent.Parent.FullName;
            List<DiscoveryTaunt> definitions = DiscoveryTaunts.Read(table);
            result.Definitions = definitions.Count;
            DiscoveryMotionAssets assets = DiscoveryMotionAssets.Read(sourceRoot);
            result.ScanIncomplete = assets.ScanIncomplete;
            result.AnimationRootFound = assets.AnimationRootFound;
            result.SearchRoots = assets.SearchRoots;
            result.Warnings.AddRange(assets.Warnings.Take(8));
            string identity = Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(applicationRoot) + "\n" + sourceRoot + "\n" + Hash(File.ReadAllBytes(baseManifest))));
            string overlay = Path.Combine(storageRoot, "taunt-data", identity.Substring(0, 20));
            string data = Path.Combine(overlay, "data"), library = Path.Combine(data, "library"), pack = Path.Combine(data, "gamepack");
            Directory.CreateDirectory(data);
            foreach (string directory in Directory.GetDirectories(applicationRoot))
                if (!string.Equals(Path.GetFileName(directory), "data", StringComparison.OrdinalIgnoreCase))
                    DirectoryLinks.EnsureJunction(Path.Combine(overlay, Path.GetFileName(directory)), directory);
            LinkChildren(baseData, data, new[] { "library", "gamepack", "caps", "start" });
            MaterializeDirectory(Path.Combine(baseData, "caps"), Path.Combine(data, "caps"), false);
            MaterializeDirectory(Path.Combine(baseData, "start"), Path.Combine(data, "start"), false);
            Directory.CreateDirectory(library);
            LinkChildren(baseLibrary, library, new[] { "previews_game" });
            MaterializeDirectory(Path.Combine(baseLibrary, "previews_game"), Path.Combine(library, "previews_game"), true);
            Directory.CreateDirectory(pack);
            LinkChildren(Path.Combine(baseData, "gamepack"), pack, new[] { "decoded" });
            string decoded = Path.Combine(pack, "decoded");
            Directory.CreateDirectory(decoded);
            foreach (string original in Directory.GetFiles(Path.Combine(baseData, "gamepack", "decoded")))
            {
                string target = Path.Combine(decoded, Path.GetFileName(original));
                if (!File.Exists(target)) CopyAtomic(original, target);
            }
            string cache = Path.Combine(storageRoot, "motion-cache-v1");
            Directory.CreateDirectory(cache);

            var byClip = entries.Where(item => item.ContainsKey("clip") && item["clip"] is string)
                .GroupBy(item => (string)item["clip"], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
            var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveryTaunt definition in definitions)
            {
                List<Dictionary<string, object>> matches;
                byClip.TryGetValue(definition.Clip, out matches);
                DiscoveryMotionAsset asset = assets.Find(definition.Clip);
                if (asset == null)
                {
                    if (matches == null)
                    {
                        result.Missing++;
                        AddUnavailable(result, definition, assets.ScanIncomplete ? DiscoveryTauntUnavailableKind.ScanIncomplete : DiscoveryTauntUnavailableKind.MissingAnimation,
                            LookupMissReason(definition.Clip, assets, sourceRoot));
                    }
                    else
                    {
                        AddNames(matches, definition);
                        foreach (Dictionary<string, object> item in matches)
                            item["tested_on"] = new[] { "Bundled motion; no matching body clip in Discovery" };
                    }
                    continue;
                }
                MotionImportStage stage = MotionImportStage.ReadSource;
                try
                {
                    byte[] bytes = asset.Read();
                    string hash = Hash(bytes);
                    string cached = Path.Combine(cache, hash + ".npz");
                    DecodedDiscoveryMotion motion = null;
                    if (!File.Exists(cached))
                    {
                        stage = MotionImportStage.Decode;
                        motion = DiscoveryMotionDecoder.Decode(bytes);
                        stage = MotionImportStage.PrepareCache;
                        DiscoveryMotionNpz.Write(cached, motion);
                    }
                    stage = MotionImportStage.PrepareCache;
                    int frames = BitConverter.ToInt32(bytes, 0x44);
                    float fps = BitConverter.ToSingle(bytes, 0x0c);
                    string originalClip = Path.Combine(baseData, "gamepack", "decoded", definition.Clip + ".npz");
                    string stockMarker = cached + ".stock-v2-" + identity.Substring(0, 12);
                    bool originalMotion = false;
                    if (matches != null)
                    {
                        if (File.Exists(stockMarker)) originalMotion = File.ReadAllText(stockMarker) == "yes";
                        else
                        {
                            if (motion == null)
                            {
                                stage = MotionImportStage.Decode;
                                motion = DiscoveryMotionDecoder.Decode(bytes);
                                stage = MotionImportStage.PrepareCache;
                            }
                            originalMotion = DiscoveryMotionNpz.MatchesPoses(originalClip, motion);
                            WriteAtomic(stockMarker, originalMotion ? "yes" : "no");
                        }
                    }
                    string destination = Path.Combine(decoded, definition.Clip + ".npz");
                    string stamp = destination + ".sha256";
                    if (!File.Exists(destination) || !File.Exists(stamp) || File.ReadAllText(stamp) != hash ||
                        new FileInfo(destination).Length != new FileInfo(cached).Length)
                    {
                        CopyAtomic(cached, destination);
                        WriteAtomic(stamp, hash);
                    }
                    applied.Add(definition.Clip);
                    if (matches == null)
                    {
                        var item = new Dictionary<string, object> {
                            { "key", "discovery" + definition.Clip + "_" + hash.Substring(0, 12) },
                            { "name", definition.Name }, { "clip", definition.Clip }, { "cut", null },
                            { "blend", 0.6 }, { "face", "facial_6368.npz" }, { "face_offset", 0 },
                            { "seconds", Math.Round(frames / (double)fps, 2) },
                            { "also", definition.Names.Skip(1).ToArray() }, { "tags", new[] { "Discovery motion" } },
                            { "drift_fix", false }, { "tested_on", new[] { "Preview on your selected wrestler" } }
                        };
                        entries.Add(item);
                        matches = new List<Dictionary<string, object>> { item };
                        byClip.Add(definition.Clip, matches);
                        result.Imported++;
                    }
                    else
                    {
                        AddNames(matches, definition);
                        if (!originalMotion)
                            foreach (Dictionary<string, object> item in matches)
                            {
                                // The engine retains loop.npz per taunt key. A
                                // changed motion needs a new key to avoid an old
                                // pose cache surviving a Discovery refresh.
                                item["key"] = Convert.ToString(item["key"], CultureInfo.InvariantCulture) + "_discovery_" + hash.Substring(0, 12);
                                item["tested_on"] = new[] { "Discovery motion; preview on your selected wrestler" };
                                item["cut"] = null;
                                item["seconds"] = Math.Round(frames / (double)fps, 2);
                            }
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is ArgumentException || error is OverflowException)
                {
                    DiscoveryTauntUnavailableKind kind;
                    string reason;
                    if (stage == MotionImportStage.ReadSource)
                    {
                        result.ReadFailures++;
                        kind = DiscoveryTauntUnavailableKind.ReadFailure;
                        reason = "Animation clip " + definition.Clip + " was indexed in " + asset.Path + ", but could not be read: " + error.Message;
                    }
                    else if (stage == MotionImportStage.Decode)
                    {
                        result.Unsupported++;
                        kind = DiscoveryTauntUnavailableKind.UnsupportedFormat;
                        reason = "Animation clip " + definition.Clip + " was found in " + asset.Path + ", but this app could not decode its animation format: " + error.Message;
                    }
                    else
                    {
                        result.CacheFailures++;
                        kind = DiscoveryTauntUnavailableKind.CacheFailure;
                        reason = "Animation clip " + definition.Clip + " was found in " + asset.Path + ", but its preview cache could not be prepared in " + storageRoot + ": " + error.Message;
                    }
                    if (result.Warnings.Count < 8) result.Warnings.Add(definition.Name + ": " + error.Message);
                    if (matches != null)
                    {
                        AddNames(matches, definition);
                        foreach (Dictionary<string, object> item in matches)
                            item["tested_on"] = new[] { kind == DiscoveryTauntUnavailableKind.ReadFailure ? "Bundled motion; Discovery clip could not be read" :
                                kind == DiscoveryTauntUnavailableKind.CacheFailure ? "Bundled motion; Discovery preview cache could not be prepared" :
                                "Bundled motion; Discovery animation format could not be decoded" };
                    }
                    else AddUnavailable(result, definition, kind, reason);
                }
            }
            foreach (string stamp in Directory.GetFiles(decoded, "*.npz.sha256"))
            {
                string clip = Path.GetFileName(stamp).Substring(0, Path.GetFileName(stamp).Length - ".npz.sha256".Length);
                if (applied.Contains(clip)) continue;
                string original = Path.Combine(baseData, "gamepack", "decoded", clip + ".npz");
                string target = Path.Combine(decoded, clip + ".npz");
                if (File.Exists(original)) CopyAtomic(original, target);
                else if (File.Exists(target)) File.Delete(target);
                File.Delete(stamp);
            }
            RemoveOtherVariantAliases(entries, definitions);
            entries = entries.OrderBy(item => Convert.ToString(item["name"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase).ToList();
            manifest["taunts"] = entries;
            WriteAtomic(Path.Combine(library, "taunts.json"), Json(manifest));
            result.Root = overlay;
            result.Library = library;
            result.Taunts = entries;
            result.Available = entries.Count;
            return result;
        }

        private enum MotionImportStage { ReadSource, Decode, PrepareCache }

        private static string LookupMissReason(string clip, DiscoveryMotionAssets assets, string sourceRoot)
        {
            string roots = assets.SearchRoots.Count == 0 ? sourceRoot : string.Join("; ", assets.SearchRoots);
            string reason = "Animation clip " + clip + " was not located in the scanned extracted .clip/.clips files under " + roots + ".";
            if (assets.ScanIncomplete)
                reason += " The scan was incomplete" + (assets.Warnings.Count == 0 ? " because some files or folders could not be read."
                    : ": " + assets.Warnings[0].TrimEnd('.') + ".") + " Load Discovery data again to review the scan warnings.";
            else if (!assets.AnimationRootFound)
                reason += " No Animation folder was found in that scan.";
            return reason + " Installed-game CAK archives were not searched.";
        }

        private static void AddUnavailable(DiscoveryTauntImport result, DiscoveryTaunt definition,
            DiscoveryTauntUnavailableKind kind, string reason)
        {
            result.Unavailable.Add(new DiscoveryUnavailableTaunt {
                Id = definition.Id, Variant = definition.Variant, Clip = definition.Clip,
                Name = definition.Name, Names = new List<string>(definition.Names), Kind = kind, Reason = reason
            });
        }

        private static void AddNames(IEnumerable<Dictionary<string, object>> items, DiscoveryTaunt definition)
        {
            foreach (Dictionary<string, object> item in items)
            {
                var names = new List<string>(definition.Names);
                object existing;
                if (item.TryGetValue("name", out existing)) names.Add(Convert.ToString(existing, CultureInfo.InvariantCulture));
                if (item.TryGetValue("also", out existing) && existing is IEnumerable)
                    foreach (object name in (IEnumerable)existing) names.Add(Convert.ToString(name, CultureInfo.InvariantCulture));
                item["name"] = definition.Name;
                item["also"] = names.Where(name => !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, definition.Name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        private static void RemoveOtherVariantAliases(IEnumerable<Dictionary<string, object>> entries, IEnumerable<DiscoveryTaunt> definitions)
        {
            var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveryTaunt definition in definitions)
                foreach (string name in definition.Names)
                {
                    string normalized = name.Trim();
                    HashSet<string> clips;
                    if (!owners.TryGetValue(normalized, out clips))
                        owners.Add(normalized, clips = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    clips.Add(definition.Clip);
                }
            foreach (Dictionary<string, object> entry in entries)
            {
                object value;
                if (!entry.TryGetValue("also", out value) || !(value is IEnumerable) || value is string ||
                    !entry.ContainsKey("clip")) continue;
                string clip = Convert.ToString(entry["clip"], CultureInfo.InvariantCulture);
                var aliases = new List<string>();
                foreach (object alias in (IEnumerable)value)
                {
                    string name = Convert.ToString(alias, CultureInfo.InvariantCulture).Trim();
                    HashSet<string> clips;
                    // Older Gorilla libraries grouped separate animation
                    // variants as search aliases. Keep an alias only when its
                    // Discovery definition belongs to this exact motion clip.
                    if (owners.TryGetValue(name, out clips) && !clips.Contains(clip)) continue;
                    if (name.Length > 0 && !aliases.Contains(name, StringComparer.OrdinalIgnoreCase)) aliases.Add(name);
                }
                entry["also"] = aliases.ToArray();
            }
        }

        private static void LinkChildren(string source, string destination, string[] excludedDirectories)
        {
            foreach (string directory in Directory.GetDirectories(source))
                if (!excludedDirectories.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                    DirectoryLinks.EnsureJunction(Path.Combine(destination, Path.GetFileName(directory)), directory);
            foreach (string file in Directory.GetFiles(source))
            {
                if (string.Equals(Path.GetFileName(file), "taunts.json", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target)) CopyAtomic(file, target);
            }
        }

        private static void CopyAtomic(string source, string destination)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void WriteAtomic(string path, string value)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), value, StringComparison.Ordinal)) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, value, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static Dictionary<string, object> JsonObject(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json)) return (Dictionary<string, object>)Object(document.RootElement);
#else
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
#endif
        }

        private static void MaterializeDirectory(string source, string target, bool readOnlyFiles)
        {
            if (!Directory.Exists(source)) { Directory.CreateDirectory(target); return; }
            if (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            {
                if (!string.Equals(DirectoryLinks.Resolve(target), DirectoryLinks.Resolve(source), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("An unexpected link occupies the Discovery animation cache.");
                Directory.Delete(target);
            }
            Directory.CreateDirectory(target);
            foreach (string original in Directory.GetFiles(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(original));
                if (File.Exists(destination)) continue;
                bool linked = false;
                if (readOnlyFiles && Environment.OSVersion.Platform == PlatformID.Win32NT)
                    linked = CreateHardLink(destination, original, IntPtr.Zero);
                if (!linked) CopyAtomic(original, destination);
            }
            foreach (string child in Directory.GetDirectories(source))
                MaterializeDirectory(child, Path.Combine(target, Path.GetFileName(child)), readOnlyFiles);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newPath, string existingPath, IntPtr security);

        private static string Json(object value)
        {
#if NET8_0_OR_GREATER
            return JsonSerializer.Serialize(value);
#else
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value);
#endif
        }

#if NET8_0_OR_GREATER
        private static object Object(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: return value.EnumerateObject().ToDictionary(property => property.Name, property => Object(property.Value));
                case JsonValueKind.Array: return value.EnumerateArray().Select(Object).ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number: return value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
