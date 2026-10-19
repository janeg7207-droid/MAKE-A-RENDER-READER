using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;
    private const string Url = "https://raw.githubusercontent.com/janeg7207-droid/MAKE-A-RENDER-READER/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/downloads/GorillaStudio_v1.0.2_Discovery_Experimental_Update.zip";
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--validate")
        {
            try
            {
                var manifest = AppUpdatePackage.ParseManifest(File.ReadAllBytes(args[2]));
                var payload = AppUpdatePackage.Validate(args[1], manifest);
                Console.WriteLine(JsonSerializer.Serialize(new { version = manifest.VersionText, size = manifest.Size, sha256 = manifest.Sha256, files = payload.Files.Count, executed = false })); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        if (args.Length == 2 && args[0] == "--live")
        {
            try
            {
                var client = new AppUpdateClient(); var manifest = await client.CheckAsync(CancellationToken.None);
                var payload = await client.DownloadAsync(manifest, Path.GetFullPath(args[1]), null, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new { version = manifest.VersionText, size = manifest.Size, sha256 = manifest.Sha256, files = payload.Files.Count, package = payload.PackagePath, executed = false })); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        root = Path.Combine(Path.GetTempPath(), "gorilla-update-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            byte[] zip = Zip(Payload()); AppUpdateManifest manifest = Manifest(zip); string path = Save(zip);
            AppUpdatePayload valid = AppUpdatePackage.Validate(path, manifest);
            Check(valid.Files.Count == 11 && valid.Files.All(file => file.Length > 0 && file.Sha256.Length == 64), "A complete eleven-file update returns every checked file and hash");
            Check(valid.PackagePath == path && valid.Manifest.Version == new Version(1, 6, 6, 0), "The validated package records its exact path and normalized version");
            Check(AppUpdatePackage.CompareVersions(new Version(1, 6, 6), new Version(1, 6, 6, 0)) == 0, "Three- and four-part equivalent helper versions compare equally");
            Check(AppUpdatePackage.CompareVersions(new Version(1, 6, 5), new Version(1, 6, 6)) < 0 && AppUpdatePackage.CompareVersions(new Version(1, 6, 7), new Version(1, 6, 6)) > 0, "Update and downgrade comparisons use numeric version components");
            foreach (string version in new[] { "1.6", "v1.6.6", "1.6.6-beta", "1.6.-6", "1.6.65536", "1.6.6.0.0", "1.6.6\n" }) Reject(() => AppUpdatePackage.ParseVersion(version), "Reject an invalid app version: " + version);
            foreach (string url in new[] { Url.Replace("https:", "http:"), Url.Replace("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "main"), Url.Replace("janeg7207-droid", "another-user"), Url.Replace("raw.githubusercontent.com", "raw.githubusercontent.com.evil.test"), Url + "?download=1", Url + "#fragment", Url.Replace("https://", "https://user@"), Url.Replace(".com/", ".com:443/"), Url.Replace("/downloads/", "/downloads/../downloads/"), Url.Replace("/downloads/", "/downloads%2F"), Url.Replace("_Update.zip", ".zip") })
                Reject(() => AppUpdatePackage.CreateManifest("1.6.6", url, zip.Length, Sha(zip)), "Reject an untrusted or mutable package URL");
            foreach (long size in new[] { 0L, -1L, AppUpdatePackage.MaximumPackageBytes + 1, long.MaxValue }) Reject(() => AppUpdatePackage.CreateManifest("1.6.6", Url, size, Sha(zip)), "Reject an unsupported download size");
            foreach (string sha in new[] { "", new string('f', 63), new string('g', 64), " " + new string('a', 64) }) Reject(() => AppUpdatePackage.CreateManifest("1.6.6", Url, zip.Length, sha), "Reject an invalid advertised digest");
            Reject(() => AppUpdatePackage.CreateManifest("1.6.6", Url, zip.Length, Sha(zip), 2), "A future updater protocol requires the full ZIP");
            byte[] feed = Feed(manifest);
            Check(AppUpdatePackage.ParseManifest(feed).Sha256 == manifest.Sha256, "The production feed schema parses successfully");
            string json = Encoding.UTF8.GetString(feed);
            foreach (string malformed in new[] { "[]", "null", "{broken", json + "x", json.Replace("\"schema\":1", "\"schema\":2"), json.Replace("\"size\":" + zip.Length, "\"size\":\"" + zip.Length + "\""), json.Insert(1, "\"version\":\"1.6.5\","), json.Replace("\"schema\":1", "\"schema\":1,\"extra\":true") }) Reject(() => AppUpdatePackage.ParseManifest(Encoding.UTF8.GetBytes(malformed)), "Malformed, ambiguous or unsupported feed JSON is rejected");
            Reject(() => AppUpdatePackage.ParseManifest(new byte[AppUpdatePackage.MaximumManifestBytes + 1]), "Feed reads are bounded to 64 KiB");
            Reject(() => AppUpdatePackage.Validate(path, AppUpdatePackage.CreateManifest("1.6.6", Url, zip.Length, new string('0', 64))), "A wrong package SHA is rejected before opening its archive");
            Reject(() => AppUpdatePackage.Validate(path, AppUpdatePackage.CreateManifest("1.6.6", Url, zip.Length + 1, Sha(zip))), "A wrong package size is rejected");

            var missing = Payload(); missing.Remove("GorillaStudio.Updater.exe"); RejectZip(Zip(missing), "Old ten-file updates cannot omit the new updater");
            var extra = Payload(); extra["extra.dll"] = Encoding.UTF8.GetBytes("unapproved"); RejectZip(Zip(extra), "Extra executable files are rejected");
            foreach (string unsafeName in new[] { "../GorillaStudio.exe", "/GorillaStudio.exe", "C:/GorillaStudio.exe", "engine\\gr_render.py", "engine/../gr_render.py", "GorillaStudio.exe:alternate", "GorillaStudio.EXE" })
            { var items = Payload(); items.Remove("GorillaStudio.exe"); items[unsafeName] = Encoding.UTF8.GetBytes("bad"); RejectZip(Zip(items), "Unapproved traversal, stream, separator or case-alias path is rejected: " + unsafeName); }
            var duplicate = Payload().Where(pair => pair.Key != "Pfim.dll").ToList(); duplicate.Add(new KeyValuePair<string, byte[]>("GorillaStudio.exe", new byte[] { 1 }));
            RejectZip(Zip(duplicate), "Duplicate entries cannot replace a required DLL");
            RejectZip(Zip(Payload(), "GorillaStudio.exe", unchecked((int)(0xa1ffU << 16))), "Unix symbolic links are rejected");
            RejectZip(Zip(Payload(), "GorillaStudio.exe", 0x400), "Windows reparse entries are rejected");
            var empty = Payload(); empty["README_DISCOVERY.txt"] = new byte[0]; RejectZip(Zip(empty), "Empty payload files are rejected");
            byte[] oversized = (byte[])zip.Clone();
            int central = Signature(oversized, new byte[] { 0x50, 0x4b, 1, 2 }); Put32(oversized, central + 24, (uint)AppUpdatePackage.MaximumExpandedBytes + 1);
            RejectZip(oversized, "An oversized declared expanded entry is rejected before decompression");
            var versionMismatch = Payload(); versionMismatch["TEST_RESULTS.json"] = Encoding.UTF8.GetBytes("{\"helper_version\":\"1.6.5\"}"); Rehash(versionMismatch); RejectZip(Zip(versionMismatch), "The package helper version must match the feed");
            var hashMismatch = Payload(); hashMismatch["GorillaStudio.NameFolders.dll"][0] ^= 1; RejectZip(Zip(hashMismatch), "A payload hash mismatch is rejected even with a matching ZIP hash");
            var badBuild = Payload(); badBuild["BUILD_SHA256.json"] = Encoding.UTF8.GetBytes("{\"../escape.exe\":\"" + new string('a', 64) + "\"}"); RejectZip(Zip(badBuild), "Build hashes cannot introduce outside payload paths");
            var absentHash = Payload(); var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(absentHash["BUILD_SHA256.json"])); hashes.Remove("GorillaStudio.Updater.exe"); absentHash["BUILD_SHA256.json"] = JsonSerializer.SerializeToUtf8Bytes(hashes); RejectZip(Zip(absentHash), "The new updater itself requires a verified build hash");
            using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); Reject(() => AppUpdatePackage.Validate(path, manifest, canceled.Token), "Package checks respect cancellation"); }
            await ClientTests(zip, manifest, feed);
            Console.WriteLine(passed + " app update checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task ClientTests(byte[] zip, AppUpdateManifest manifest, byte[] feed)
    {
        Uri requested = null;
        var client = new AppUpdateClient((uri, token) => { requested = uri; return Task.FromResult(new AppUpdateResponse(200, new MemoryStream(feed), feed.Length, uri)); });
        Check((await client.CheckAsync(CancellationToken.None)).VersionText == "1.6.6" && requested.AbsoluteUri == AppUpdateClient.FeedUrl, "The client checks only the fixed repository feed");
        foreach (AppUpdateResponse response in new[] {
            new AppUpdateResponse(302, new MemoryStream(feed), feed.Length, new Uri(AppUpdateClient.FeedUrl)),
            new AppUpdateResponse(200, new MemoryStream(feed), feed.Length, new Uri("https://evil.test/feed")),
            new AppUpdateResponse(200, new MemoryStream(feed), AppUpdatePackage.MaximumManifestBytes + 1, new Uri(AppUpdateClient.FeedUrl)),
            new AppUpdateResponse(200, new MemoryStream(new byte[AppUpdatePackage.MaximumManifestBytes + 1]), null, new Uri(AppUpdateClient.FeedUrl)) })
            await RejectAsync(() => new AppUpdateClient((uri, token) => Task.FromResult(response)).CheckAsync(CancellationToken.None), "Feed redirects and oversized declared or streamed bodies are rejected");
        string cache = Path.Combine(root, "cache"); var progress = new ImmediateProgress();
        var download = new AppUpdateClient((uri, token) => Task.FromResult(new AppUpdateResponse(200, new MemoryStream(zip), zip.Length, uri)));
        AppUpdatePayload payload = await download.DownloadAsync(manifest, cache, progress, CancellationToken.None);
        Check(payload.Files.Count == 11 && File.ReadAllBytes(payload.PackagePath).SequenceEqual(zip), "The client downloads and validates the complete small update");
        Check(progress.Last == zip.Length && payload.PackagePath.StartsWith(cache + Path.DirectorySeparatorChar) && Path.GetDirectoryName(payload.PackagePath) != cache, "Progress reports exact bytes and each download uses its own cache directory");
        if (!OperatingSystem.IsWindows()) Check((File.GetUnixFileMode(Path.GetDirectoryName(payload.PackagePath)) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, "Unix update cache directories are private to the user");
        string failCache = Path.Combine(root, "failed-cache");
        foreach (byte[] body in new[] { zip.Take(zip.Length - 1).ToArray(), zip.Concat(new byte[] { 1 }).ToArray(), zip.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray() })
        {
            var broken = new AppUpdateClient((uri, token) => Task.FromResult(new AppUpdateResponse(200, new MemoryStream(body), null, uri)));
            await RejectAsync(() => broken.DownloadAsync(manifest, failCache, null, CancellationToken.None), "Truncated, excessive or corrupted streamed packages fail validation");
            Check(!Directory.GetFiles(failCache, "*", SearchOption.AllDirectories).Any() && !Directory.GetDirectories(failCache).Any(), "Failed downloads leave no partial package or private child directory");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var cancelProgress = new ImmediateProgress { OnValue = value => { if (value > 0) canceled.Cancel(); } };
            await RejectAsync(() => download.DownloadAsync(manifest, failCache, cancelProgress, canceled.Token), "Canceling an active download stops before validation or installation");
            Check(!Directory.GetFiles(failCache, "*", SearchOption.AllDirectories).Any(), "Canceled downloads leave no partial package");
        }
        var redirect = new AppUpdateClient((uri, token) => Task.FromResult(new AppUpdateResponse(200, new MemoryStream(zip), zip.Length, new Uri("https://evil.test/update.zip"))));
        await RejectAsync(() => redirect.DownloadAsync(manifest, failCache, null, CancellationToken.None), "Package responses cannot silently change download host");
    }
    private sealed class ImmediateProgress : IProgress<AppUpdateProgress> { internal long Last; internal Action<long> OnValue; public void Report(AppUpdateProgress value) { Last = value.BytesReceived; OnValue?.Invoke(value.BytesReceived); } }
    private static Dictionary<string, byte[]> Payload()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in new[] { "GorillaStudio.exe", "GorillaStudio.NameFolders.dll", "GorillaStudio.Updater.exe", "Pfim.dll", "README_DISCOVERY.txt", "rosters/Discovery_roster.json", "third_party/Pfim/LICENSE.txt", "third_party/Pfim/NOTICE.txt", "engine/gr_render.py" }) result[name] = Encoding.UTF8.GetBytes("fixture bytes for " + name);
        result["TEST_RESULTS.json"] = Encoding.UTF8.GetBytes("{\"helper_version\":\"1.6.6.0\",\"portable_checks\":{\"total\":42}}"); Rehash(result); return result;
    }
    private static void Rehash(Dictionary<string, byte[]> files) { var hashes = files.Where(pair => pair.Key != "BUILD_SHA256.json").ToDictionary(pair => pair.Key, pair => Sha(pair.Value)); hashes["engine/gs_engine.exe"] = new string('a', 64); files["BUILD_SHA256.json"] = JsonSerializer.SerializeToUtf8Bytes(hashes); }
    private static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> files, string modeName = null, int attributes = 0)
    { using (var bytes = new MemoryStream()) { using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var pair in files) { var entry = archive.CreateEntry(pair.Key, CompressionLevel.Fastest); if (pair.Key == modeName) entry.ExternalAttributes = attributes; using (var output = entry.Open()) output.Write(pair.Value); } return bytes.ToArray(); } }
    private static byte[] Feed(AppUpdateManifest manifest) { return JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = manifest.VersionText, minimumUpdaterVersion = 1, url = manifest.DownloadUri.AbsoluteUri, size = manifest.Size, sha256 = manifest.Sha256 }); }
    private static AppUpdateManifest Manifest(byte[] zip) { return AppUpdatePackage.CreateManifest("1.6.6", Url, zip.Length, Sha(zip)); }
    private static string Save(byte[] bytes) { string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip"); File.WriteAllBytes(path, bytes); return path; }
    private static string Sha(byte[] bytes) { return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); }
    private static void RejectZip(byte[] bytes, string description) { Reject(() => AppUpdatePackage.Validate(Save(bytes), Manifest(bytes)), description); }
    private static void Reject(Action action, string description) { try { action(); } catch (Exception error) when (error is InvalidDataException || error is NotSupportedException || error is ArgumentException || error is OperationCanceledException) { Check(true, description); return; } throw new Exception("FAIL accepted: " + description); }
    private static async Task RejectAsync(Func<Task> action, string description) { try { await action(); } catch (Exception error) when (error is InvalidDataException || error is NotSupportedException || error is ArgumentException || error is OperationCanceledException) { Check(true, description); return; } throw new Exception("FAIL accepted: " + description); }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS " + description); }
    private static int Signature(byte[] bytes, byte[] signature) { for (int i = 0; i <= bytes.Length - signature.Length; i++) if (bytes.AsSpan(i, signature.Length).SequenceEqual(signature)) return i; throw new Exception("ZIP central directory missing"); }
    private static void Put32(byte[] bytes, int at, uint value) { BitConverter.GetBytes(value).CopyTo(bytes, at); }
}
