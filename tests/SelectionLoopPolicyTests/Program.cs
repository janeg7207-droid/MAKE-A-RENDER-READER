using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static string root;
    private static int passed;
    private static int Main(string[] args)
    {
        root = Path.Combine(Path.GetTempPath(), "gorilla-selection-loop-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string gmr = Fixture("valid");
            byte[] original = File.ReadAllBytes(gmr);
            DateTime timestamp = File.GetLastWriteTimeUtc(gmr);
            Check(SelectionLoopPolicy.SidecarPath(gmr) == Path.GetFullPath(gmr) + ".selection-loop.json", "The sidecar stays adjacent to its exact GMR");
            Check(SelectionLoopPolicy.TryWrite(gmr, 161, Result()), "A newly completed native-contract freeze can opt into a clean-prefix loop");
            string sidecar = SelectionLoopPolicy.SidecarPath(gmr);
            SelectionLoopPolicyRecord policy;
            Check(SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy), "A freshly written sidecar validates against its source");
            Check(policy.Schema == 1 && policy.WrestlerId == 161 && policy.VideoFrames == 15 && policy.MotionFrames == 4
                && policy.Fps == 30 && policy.Ending == "freeze", "The clean prefix and complete video counts stay distinct");
            Check(policy.Sha256 == Hash(gmr), "The policy binds every byte of the final GMR, including metadata");
            using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(sidecar)))
                Check(json.RootElement.EnumerateObject().Select(item => item.Name).OrderBy(name => name).SequenceEqual(
                    new[] { "schema", "wrestlerId", "sha256", "videoFrames", "motionFrames", "fps", "ending" }.OrderBy(name => name)), "The public sidecar contains only the seven agreed primitive fields");
            Check(original.SequenceEqual(File.ReadAllBytes(gmr)) && timestamp == File.GetLastWriteTimeUtc(gmr), "Publishing and validation preserve all GMR bytes and its timestamp");
            byte[] firstPolicy = File.ReadAllBytes(sidecar);
            Check(SelectionLoopPolicy.TryWrite(gmr, 161, Result()) && firstPolicy.SequenceEqual(File.ReadAllBytes(sidecar)), "Atomic replacement is deterministic for unchanged output");
            Check(!SelectionLoopPolicy.Validate(gmr, sidecar, 162, out policy) && policy == null, "A policy is not reused for another wrestler context");

            foreach (object frames in new object[] { null, "4", true, -1, 0, 1, 3, 5, 4.5, double.NaN, double.PositiveInfinity, int.MaxValue, ulong.MaxValue })
            {
                var result = Result(); result["frames"] = frames;
                RejectResult(gmr, result, 161, "Reject invalid, mismatched, or one-frame completion count: " + frames);
            }
            foreach (object ending in new object[] { null, "loop", "still", "Freeze", 1 })
            {
                var result = Result(); result["end"] = ending;
                RejectResult(gmr, result, 161, "Only exact native freeze completions can publish: " + ending);
            }
            var missingFrames = Result(); missingFrames.Remove("frames"); RejectResult(gmr, missingFrames, 161, "A legacy GMR alone cannot establish motion frame count");
            var missingEnd = Result(); missingEnd.Remove("end"); RejectResult(gmr, missingEnd, 161, "The actual completion ending is required");
            foreach (object id in new object[] { null, "161", true, 162, -1, 161.5 })
            {
                var result = Result(); result["id"] = id;
                RejectResult(gmr, result, 161, "An optional completion ID must match the supplied logical wrestler: " + id);
            }
            foreach (int id in new[] { -1, 0, 162, 65536 }) RejectResult(gmr, Result(), id, "A bad or different wrestler context cannot publish: " + id);
            RejectResult(gmr, null, 161, "A missing completion cannot enable a legacy render");
            Check(!SelectionLoopPolicy.TryWrite(null, 161, Result()) && !SelectionLoopPolicy.TryWrite(Path.Combine(root, "absent.gmr"), 161, Result()), "Missing paths fail without side effects");

            byte[] changed = original.ToArray(); changed[108] ^= 1;
            File.WriteAllBytes(gmr, changed); File.SetLastWriteTimeUtc(gmr, timestamp);
            Check(!SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy), "Same-length, same-time movie edits invalidate the entire-file binding");
            Check(SelectionLoopPolicy.TryWrite(gmr, 161, Result()) && SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy)
                && policy.Sha256 != Hash(original), "A fresh owned completion can atomically replace a stale policy");
            Check(GorillaRenderMetadata.SetRosterHashes(gmr, 161, new[] { "0123456789ABCDEF" }), "The existing final roster metadata update succeeds");
            Check(!SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy), "A later roster metadata update invalidates a policy written too early");
            Check(SelectionLoopPolicy.TryWrite(gmr, 161, Result()) && SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy)
                && policy.Sha256 == Hash(gmr), "Publishing after final roster metadata binds the final installed bytes");

            string validPolicy = File.ReadAllText(sidecar);
            foreach (KeyValuePair<string, string> change in new Dictionary<string, string> {
                { "unknown schema", validPolicy.Replace("\"schema\":1", "\"schema\":2") },
                { "wrong ID", validPolicy.Replace("\"wrestlerId\":161", "\"wrestlerId\":162") },
                { "wrong motion length", validPolicy.Replace("\"motionFrames\":4", "\"motionFrames\":3") },
                { "one frame", validPolicy.Replace("\"motionFrames\":4", "\"motionFrames\":1") },
                { "wrong full video length", validPolicy.Replace("\"videoFrames\":15", "\"videoFrames\":16") },
                { "wrong rate", validPolicy.Replace("\"fps\":30", "\"fps\":60") },
                { "wrong ending", validPolicy.Replace("\"ending\":\"freeze\"", "\"ending\":\"loop\"") },
                { "string count", validPolicy.Replace("\"motionFrames\":4", "\"motionFrames\":\"4\"") },
                { "duplicate key", validPolicy.Insert(1, "\"motionFrames\":2,") },
                { "escaped duplicate key", validPolicy.Insert(1, "\"motion\\u0046rames\":4,") },
                { "unknown field", validPolicy.Insert(1, "\"unknown\":0,") },
                { "escaped key", validPolicy.Replace("\"schema\"", "\"sch\\u0065ma\"") },
                { "uppercase hash", validPolicy.Replace(policy.Sha256, policy.Sha256.ToUpperInvariant()) },
                { "nonhex hash", validPolicy.Replace(policy.Sha256, new string('z', 64)) },
                { "truncated JSON", validPolicy.Substring(0, validPolicy.Length - 1) },
                { "array JSON", "[]" }
            })
            {
                File.WriteAllText(sidecar, change.Value);
                Check(!SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy) && policy == null, "Reject policy " + change.Key);
            }
            File.WriteAllBytes(sidecar, new byte[] { 0xff, 0xfe, 0x7b });
            Check(!SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy), "Malformed UTF8 sidecars fail safely");
            File.WriteAllText(sidecar, new string(' ', 16385));
            Check(!SelectionLoopPolicy.Validate(gmr, sidecar, 161, out policy), "Policy reading is bounded before JSON allocation");
            File.WriteAllText(sidecar, validPolicy);

            foreach (KeyValuePair<int, uint> header in new Dictionary<int, uint> {
                { 0, 0 }, { 4, 1 }, { 8, 14 }, { 12, 5 }, { 16, 14 }, { 20, 512 }, { 24, 512 },
                { 28, 60 }, { 32, 2 }, { 36, 0 }, { 40, 1 }, { 44, 108 }, { 48, 112 }, { 52, 111 }, { 104, 169 }
            })
                Mutated("header-" + header.Key, bytes => WriteU32(bytes, header.Key, header.Value), "Reject corrupt/unsupported Bink field at byte " + header.Key);
            Mutated("footer", bytes => bytes[bytes.Length - 1] = (byte)'2', "Unknown GMR footer versions are rejected");
            Mutated("footer-empty", bytes => WriteU32(bytes, bytes.Length - 8, 0), "An empty metadata footer is rejected");
            Mutated("footer-overflow", bytes => WriteU32(bytes, bytes.Length - 8, uint.MaxValue), "An oversized or overlapping footer is rejected before allocation");
            foreach (KeyValuePair<string, string> metadata in new Dictionary<string, string> {
                { "format", Metadata().Replace("GorillaRender", "UnknownFormat") },
                { "version", Metadata().Replace("\"version\":1", "\"version\":2") },
                { "ending", Metadata().Replace("\"end\":\"freeze\"", "\"end\":\"loop\"") },
                { "ID", Metadata().Replace("\"wrestler_id\":161", "\"wrestler_id\":162") },
                { "ID-type", Metadata().Replace("\"wrestler_id\":161", "\"wrestler_id\":\"161\"") },
                { "frames", Metadata().Replace("\"frames\":15", "\"frames\":14") },
                { "fps", Metadata().Replace("\"fps\":30", "\"fps\":60") },
                { "truncated", "{\"wrestler_id\":161," }, { "array", "[]" }
            })
            {
                string bad = Fixture("metadata-" + metadata.Key, metadata.Value);
                RejectResult(bad, Result(), 161, "Reject native metadata " + metadata.Key);
            }
            string one = Fixture("single-motion-frame", Metadata().Replace("\"frames\":15", "\"frames\":12"), 12);
            var single = Result(); single["frames"] = 1;
            RejectResult(one, single, 161, "A one-frame clean prefix is not published as a moving loop");
            string truncated = Path.Combine(root, "truncated.gmr"); File.WriteAllBytes(truncated, original.Take(50).ToArray());
            RejectResult(truncated, Result(), 161, "Truncated movie/header/index data fail without changes");
            string huge = Path.Combine(root, "oversized.gmr"); using (var file = File.Create(huge)) file.SetLength(5L * 1024 * 1024 * 1024);
            Check(!SelectionLoopPolicy.TryWrite(huge, 161, Result()), "An out-of-format sparse GMR length is rejected before hashing");
            string blocked = Fixture("blocked-sidecar");
            Directory.CreateDirectory(SelectionLoopPolicy.SidecarPath(blocked));
            byte[] blockedBytes = File.ReadAllBytes(blocked);
            Check(!SelectionLoopPolicy.TryWrite(blocked, 161, Result()) && blockedBytes.SequenceEqual(File.ReadAllBytes(blocked)), "A failed atomic publication leaves the completed movie unchanged");
            Check(!SelectionLoopPolicy.Validate(null, sidecar, 161, out policy)
                && !SelectionLoopPolicy.Validate(gmr, null, 161, out policy), "Validation rejects missing paths safely");

            string renamed = Path.Combine(root, "installed-renamed.gmr"); File.Copy(gmr, renamed);
            byte[] sourceBeforeCopy = File.ReadAllBytes(gmr), destinationBeforeCopy = File.ReadAllBytes(renamed);
            Check(SelectionLoopPolicy.TryCopy(gmr, renamed, 161), "A renamed byte-identical installed GMR can receive its bound policy");
            Check(File.ReadAllBytes(sidecar).SequenceEqual(File.ReadAllBytes(SelectionLoopPolicy.SidecarPath(renamed)))
                && SelectionLoopPolicy.Validate(renamed, SelectionLoopPolicy.SidecarPath(renamed), 161, out policy), "The installed policy retains the complete source hash and validates at its new path");
            Check(sourceBeforeCopy.SequenceEqual(File.ReadAllBytes(gmr)) && destinationBeforeCopy.SequenceEqual(File.ReadAllBytes(renamed)), "Copying a policy changes neither source nor destination GMR bytes");
            Check(SelectionLoopPolicy.TryCopy(gmr, renamed, 161), "An existing matching installed policy can be atomically replaced");
            RejectCopy(gmr, renamed, 162, "A copy with a mismatched wrestler context cannot overwrite a policy");
            string other = Fixture("other-installed");
            File.WriteAllText(SelectionLoopPolicy.SidecarPath(other), "preserve this existing policy");
            RejectCopy(gmr, other, 161, "A destination with different movie or metadata bytes cannot receive the policy");
            string otherId = Fixture("other-id", Metadata().Replace("\"wrestler_id\":161", "\"wrestler_id\":162"));
            RejectCopy(gmr, otherId, 161, "A destination belonging to another wrestler cannot receive the policy");
            string loop = Fixture("loop-source", Metadata().Replace("\"end\":\"freeze\"", "\"end\":\"loop\""));
            File.WriteAllText(SelectionLoopPolicy.SidecarPath(loop), validPolicy);
            RejectCopy(loop, renamed, 161, "A loop movie does not authorize a freeze policy copy");
            string noPolicy = Fixture("no-policy");
            RejectCopy(noPolicy, renamed, 161, "A legacy freeze with no completion policy cannot overwrite an installed policy");
            File.WriteAllText(sidecar, validPolicy.Replace("\"schema\":1", "\"schema\":2"));
            RejectCopy(gmr, renamed, 161, "An unknown source policy cannot overwrite a destination");
            File.WriteAllText(sidecar, validPolicy);
            Check(!SelectionLoopPolicy.TryCopy(null, renamed, 161) && !SelectionLoopPolicy.TryCopy(gmr, null, 161), "Missing copy paths fail safely");

            // Optional actual native encoder/packager fixture; the ordinary fixture
            // above is structural and does not claim its synthetic packets decode.
            if (args.Length > 0)
            {
                string actual = Path.Combine(root, "actual-native-freeze.gmr"); File.Copy(args[0], actual);
                byte[] actualBytes = File.ReadAllBytes(actual);
                Check(SelectionLoopPolicy.TryWrite(actual, 161, Result()), "The independently decoded actual native 1024px/30fps/15frame fixture is accepted");
                Check(SelectionLoopPolicy.Validate(actual, SelectionLoopPolicy.SidecarPath(actual), 161, out policy)
                    && policy.MotionFrames == 4 && policy.VideoFrames == 15, "The actual native fixture validates without inferring or changing its frame counts");
                Check(actualBytes.SequenceEqual(File.ReadAllBytes(actual)), "Actual native Bink packets and metadata remain untouched");
                if (args.Length > 1) File.Copy(SelectionLoopPolicy.SidecarPath(actual), args[1], true);
            }
            Check(!Directory.EnumerateFiles(root).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)), "No partial sidecar remains after success or failure");
            Console.WriteLine(passed + " selection loop policy checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static Dictionary<string, object> Result()
    {
        return new Dictionary<string, object> { { "id", 161 }, { "end", "freeze" }, { "frames", 4 } };
    }
    private static string Metadata()
    {
        return "{\"format\":\"GorillaRender\",\"version\":1,\"wrestler_id\":161,\"frames\":15,\"fps\":30,\"end\":\"freeze\",\"render_hashes\":[]}";
    }
    private static string Fixture(string name, string metadata = null, int frames = 15)
    {
        string path = Path.Combine(root, name + ".gmr");
        byte[] json = Encoding.UTF8.GetBytes(metadata ?? Metadata());
        uint tableEnd = (uint)(44 + (frames + 1) * 4), videoLength = tableEnd + (uint)frames * 4;
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("BIKi"));
            foreach (uint field in new uint[] { videoLength - 8, (uint)frames, 4, (uint)frames, 1024, 1024, 30, 1, 1048576, 0 }) writer.Write(field);
            for (int i = 0; i < frames; i++) writer.Write((tableEnd + (uint)i * 4) | 1u);
            writer.Write(videoLength);
            for (int i = 0; i < frames; i++) writer.Write(new byte[] { 1, 2, 3, 4 });
            writer.Write(json); writer.Write((uint)json.Length); writer.Write(Encoding.ASCII.GetBytes("GMR1"));
        }
        return path;
    }
    private static void RejectResult(string gmr, IDictionary<string, object> result, int id, string message)
    {
        byte[] before = File.ReadAllBytes(gmr);
        string sidecar = SelectionLoopPolicy.SidecarPath(gmr);
        byte[] beforePolicy = File.Exists(sidecar) ? File.ReadAllBytes(sidecar) : null;
        Check(!SelectionLoopPolicy.TryWrite(gmr, id, result), message);
        Check(before.SequenceEqual(File.ReadAllBytes(gmr)) && (beforePolicy == null ? !File.Exists(sidecar) : beforePolicy.SequenceEqual(File.ReadAllBytes(sidecar))), "Rejected publication leaves existing files untouched");
    }
    private static void Mutated(string name, Action<byte[]> change, string message)
    {
        string path = Fixture(name); byte[] bytes = File.ReadAllBytes(path); change(bytes); File.WriteAllBytes(path, bytes);
        RejectResult(path, Result(), 161, message);
    }
    private static void RejectCopy(string source, string destination, int id, string message)
    {
        byte[] sourceBytes = File.ReadAllBytes(source), destinationBytes = File.ReadAllBytes(destination);
        string target = SelectionLoopPolicy.SidecarPath(destination);
        byte[] existing = File.Exists(target) ? File.ReadAllBytes(target) : null;
        Check(!SelectionLoopPolicy.TryCopy(source, destination, id), message);
        Check(sourceBytes.SequenceEqual(File.ReadAllBytes(source)) && destinationBytes.SequenceEqual(File.ReadAllBytes(destination))
            && (existing == null ? !File.Exists(target) : existing.SequenceEqual(File.ReadAllBytes(target))), "Rejected policy copy preserves both movies and any existing destination policy");
    }
    private static void WriteU32(byte[] bytes, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static string Hash(string path) { return Hash(File.ReadAllBytes(path)); }
    private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant(); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAILED: " + message);
        passed++;
    }
}
