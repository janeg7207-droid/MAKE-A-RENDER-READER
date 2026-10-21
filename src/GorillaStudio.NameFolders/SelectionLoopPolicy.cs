using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    public sealed class SelectionLoopPolicyRecord
    {
        public int Schema { get; internal set; }
        public int WrestlerId { get; internal set; }
        public string Sha256 { get; internal set; }
        public int VideoFrames { get; internal set; }
        public int MotionFrames { get; internal set; }
        public int Fps { get; internal set; }
        public string Ending { get; internal set; }
    }

    /// <summary>Authorizes looping the clean prefix of one newly completed freeze movie.</summary>
    /// <remarks>
    /// The original native producer appends ten flash frames and one clean held frame.
    /// Its RESULT.frames excludes those eleven frames. This class requires that actual
    /// completion result; it never infers a policy from a legacy movie alone. The clean
    /// prefix is shorter than the original complete taunt. No GMR bytes are changed.
    /// </remarks>
    public static class SelectionLoopPolicy
    {
        private const int TailFrames = 11;
        private const int MaximumMetadataBytes = 16 * 1024 * 1024;
        private const int MaximumPolicyBytes = 16 * 1024;
        private const int MaximumFrames = 1000000;
        // The native Bink size field is u32; metadata has its separate bounded footer.
        private const long MaximumGmrBytes = (long)uint.MaxValue + 8 + MaximumMetadataBytes + 8;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex PolicyKeys = new Regex("\"(?:schema|wrestlerId|sha256|videoFrames|motionFrames|fps|ending)\"\\s*:",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex AllPolicyKeys = new Regex("\"(?:[^\"\\\\]|\\\\.)*\"\\s*:",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        public static string SidecarPath(string gmrPath)
        {
            return Path.GetFullPath(gmrPath) + ".selection-loop.json";
        }

        public static bool TryWrite(string gmrPath, int wrestlerId, IDictionary<string, object> result)
        {
            string temporary = null;
            try
            {
                int motionFrames, resultId;
                object id;
                if (!ValidId(wrestlerId) || result == null || Text(result, "end") != "freeze"
                    || !Integer(result, "frames", 2, MaximumFrames - TailFrames, out motionFrames)
                    || (result.TryGetValue("id", out id) && (!Integer(id, 1, 65535, out resultId) || resultId != wrestlerId))) return false;
                string source = Path.GetFullPath(gmrPath), target = SidecarPath(source);
                using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Snapshot snapshot;
                    if (!Inspect(stream, wrestlerId, out snapshot) || snapshot.Frames != motionFrames + TailFrames) return false;
                    string hash = HashExactly(stream, snapshot.Length);
                    var policy = new Dictionary<string, object> {
                        { "schema", 1 }, { "wrestlerId", wrestlerId }, { "sha256", hash },
                        { "videoFrames", snapshot.Frames }, { "motionFrames", motionFrames },
                        { "fps", 30 }, { "ending", "freeze" }
                    };
                    byte[] bytes = Utf8.GetBytes(WriteJson(policy));
                    if (bytes.Length == 0 || bytes.Length > MaximumPolicyBytes) return false;
                    temporary = Path.Combine(Path.GetDirectoryName(target), "." + Path.GetFileName(target)
                        + ".pending-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        destination.Write(bytes, 0, bytes.Length);
                        destination.Flush(true);
                    }
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                    temporary = null;
                    return true;
                }
            }
            catch (Exception error) when (SourceError(error)) { return false; }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (SourceError(error)) { }
                }
            }
        }

        public static bool Validate(string gmrPath, string sidecarPath, int wrestlerId, out SelectionLoopPolicyRecord policy)
        {
            policy = null;
            try
            {
                if (!ValidId(wrestlerId)) return false;
                SelectionLoopPolicyRecord parsed;
                if (!ReadPolicy(Path.GetFullPath(sidecarPath), out parsed) || parsed.WrestlerId != wrestlerId) return false;
                using (var stream = new FileStream(Path.GetFullPath(gmrPath), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Snapshot snapshot;
                    if (!Inspect(stream, wrestlerId, out snapshot) || snapshot.Frames != parsed.VideoFrames
                        || parsed.MotionFrames + TailFrames != snapshot.Frames
                        || HashExactly(stream, snapshot.Length) != parsed.Sha256) return false;
                    policy = parsed;
                    return true;
                }
            }
            catch (Exception error) when (SourceError(error)) { return false; }
        }

        /// <summary>Copies a policy only when both GMR files contain the same final bytes.</summary>
        public static bool TryCopy(string sourceGmr, string destinationGmr, int wrestlerId)
        {
            string temporary = null;
            try
            {
                string source = SidecarPath(sourceGmr), target = SidecarPath(destinationGmr);
                SelectionLoopPolicyRecord sourcePolicy, destinationPolicy, capturedPolicy;
                byte[] bytes;
                if (!Validate(sourceGmr, source, wrestlerId, out sourcePolicy)
                    || !Validate(destinationGmr, source, wrestlerId, out destinationPolicy)
                    || !ReadPolicy(source, out capturedPolicy, out bytes)
                    || !SamePolicy(sourcePolicy, destinationPolicy) || !SamePolicy(sourcePolicy, capturedPolicy)) return false;
                temporary = Path.Combine(Path.GetDirectoryName(target), "." + Path.GetFileName(target)
                    + ".pending-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    destination.Write(bytes, 0, bytes.Length);
                    destination.Flush(true);
                }
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                temporary = null;
                SelectionLoopPolicyRecord installed;
                return Validate(destinationGmr, target, wrestlerId, out installed) && SamePolicy(capturedPolicy, installed);
            }
            catch (Exception error) when (SourceError(error)) { return false; }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (SourceError(error)) { }
                }
            }
        }

        private static bool SamePolicy(SelectionLoopPolicyRecord a, SelectionLoopPolicyRecord b)
        {
            return a != null && b != null && a.Schema == b.Schema && a.WrestlerId == b.WrestlerId && a.Sha256 == b.Sha256
                && a.VideoFrames == b.VideoFrames && a.MotionFrames == b.MotionFrames && a.Fps == b.Fps && a.Ending == b.Ending;
        }

        private sealed class Snapshot
        {
            internal long Length;
            internal int Frames;
        }

        private static bool Inspect(FileStream stream, int wrestlerId, out Snapshot snapshot)
        {
            snapshot = null;
            long length = stream.Length;
            if (length < 60 || length > MaximumGmrBytes) return false;
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                stream.Position = length - 8;
                uint metadataLength = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x31524D47 || metadataLength == 0 || metadataLength > MaximumMetadataBytes
                    || metadataLength > length - 8 - 52) return false;
                long videoLength = length - 8 - metadataLength;
                stream.Position = 0;
                if (reader.ReadUInt32() != 0x694B4942 || (long)reader.ReadUInt32() + 8 != videoLength) return false;
                uint frames = reader.ReadUInt32(), largest = reader.ReadUInt32();
                if (frames < TailFrames + 2 || frames > MaximumFrames || reader.ReadUInt32() != frames
                    || reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 1024
                    || reader.ReadUInt32() != 30 || reader.ReadUInt32() != 1
                    || reader.ReadUInt32() != 1048576 || reader.ReadUInt32() != 0) return false;
                long tableEnd = 44 + ((long)frames + 1) * 4;
                if (tableEnd >= videoLength) return false;
                uint prior = reader.ReadUInt32();
                if ((prior & 1) == 0 || (long)(prior & ~1u) != tableEnd) return false;
                uint maximumPacket = 0;
                for (uint i = 1; i <= frames; i++)
                {
                    uint current = reader.ReadUInt32();
                    long start = prior & ~1u, end = current & ~1u;
                    if (end <= start || end > videoLength || (i < frames && (current & 1) == 0)
                        || (i == frames && ((current & 1) != 0 || end != videoLength))) return false;
                    maximumPacket = Math.Max(maximumPacket, (uint)(end - start));
                    prior = current;
                }
                if (largest != maximumPacket) return false;
                stream.Position = videoLength;
                byte[] bytes = reader.ReadBytes((int)metadataLength);
                if (bytes.Length != metadataLength) return false;
                Dictionary<string, object> metadata = ReadJson(Utf8.GetString(bytes));
                int id, version, declaredFrames, fps;
                if (metadata == null || Text(metadata, "format") != "GorillaRender" || Text(metadata, "end") != "freeze"
                    || !Integer(metadata, "version", 1, 1, out version)
                    || !Integer(metadata, "wrestler_id", 1, 65535, out id) || id != wrestlerId
                    || !Integer(metadata, "frames", 1, MaximumFrames, out declaredFrames) || declaredFrames != frames
                    || !Integer(metadata, "fps", 30, 30, out fps)) return false;
                snapshot = new Snapshot { Length = length, Frames = (int)frames };
                return true;
            }
        }

        private static bool ReadPolicy(string path, out SelectionLoopPolicyRecord policy)
        {
            byte[] bytes;
            return ReadPolicy(path, out policy, out bytes);
        }

        private static bool ReadPolicy(string path, out SelectionLoopPolicyRecord policy, out byte[] bytes)
        {
            policy = null;
            bytes = null;
            string json;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumPolicyBytes) return false;
                bytes = new byte[(int)stream.Length];
                int count = 0, read;
                while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) != 0) count += read;
                if (count != bytes.Length || stream.ReadByte() != -1) return false;
                json = Utf8.GetString(bytes);
            }
            Dictionary<string, object> values = ReadJson(json);
            int schema, wrestler, video, motion, fps;
            string hash = Text(values, "sha256");
            // Policies emitted here are flat, have seven unique literal keys and
            // scalar values. Counting the literal keys also rejects duplicate or
            // escaped keys consistently across the two framework JSON parsers.
            if (values == null || values.Count != 7 || PolicyKeys.Matches(json).Count != 7 || AllPolicyKeys.Matches(json).Count != 7
                || !Integer(values, "schema", 1, 1, out schema)
                || !Integer(values, "wrestlerId", 1, 65535, out wrestler)
                || !Integer(values, "videoFrames", TailFrames + 2, MaximumFrames, out video)
                || !Integer(values, "motionFrames", 2, MaximumFrames - TailFrames, out motion)
                || video != motion + TailFrames || !Integer(values, "fps", 30, 30, out fps)
                || Text(values, "ending") != "freeze" || hash == null || hash.Length != 64
                || hash.Any(character => !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))) return false;
            policy = new SelectionLoopPolicyRecord { Schema = schema, WrestlerId = wrestler, Sha256 = hash,
                VideoFrames = video, MotionFrames = motion, Fps = fps, Ending = "freeze" };
            return true;
        }

        private static string HashExactly(FileStream stream, long length)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536];
                long remaining = length;
                while (remaining > 0)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw new EndOfStreamException("The completed render changed while it was being inspected.");
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    remaining -= read;
                }
                if (stream.ReadByte() != -1 || stream.Length != length) throw new IOException("The completed render changed size.");
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Concat(sha.Hash.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static bool ValidId(int id) { return id >= 1 && id <= 65535; }
        private static string Text(IDictionary<string, object> values, string key)
        {
            object value;
            return values != null && values.TryGetValue(key, out value) ? value as string : null;
        }
        private static bool Integer(IDictionary<string, object> values, string key, int minimum, int maximum, out int number)
        {
            object value;
            number = 0;
            return values != null && values.TryGetValue(key, out value) && Integer(value, minimum, maximum, out number);
        }
        private static bool Integer(object value, int minimum, int maximum, out int number)
        {
            number = 0;
            if (value == null || value.GetType().IsEnum || value is string || value is bool || value is char) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Single: case TypeCode.Double: case TypeCode.Decimal:
                    double numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < minimum || numeric > maximum
                        || numeric != Math.Truncate(numeric)) return false;
                    number = (int)numeric; return true;
                default: return false;
            }
        }

        private static Dictionary<string, object> ReadJson(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json)) return JsonValue(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumMetadataBytes, RecursionLimit = 64 }.DeserializeObject(json) as Dictionary<string, object>;
#endif
        }
        private static string WriteJson(object value)
        {
#if NET8_0_OR_GREATER
            return JsonSerializer.Serialize(value);
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumMetadataBytes }.Serialize(value);
#endif
        }
#if NET8_0_OR_GREATER
        private static object JsonValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: return value.EnumerateObject().ToDictionary(item => item.Name, item => JsonValue(item.Value));
                case JsonValueKind.Array: return value.EnumerateArray().Select(JsonValue).ToArray();
                case JsonValueKind.String: return value.GetString();
                case JsonValueKind.Number: long integer; return value.TryGetInt64(out integer) ? (object)integer : value.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
        private static bool SourceError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is FormatException || error is OverflowException
                || error is NotSupportedException || error is System.Security.SecurityException
                || error is RegexMatchTimeoutException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                ;
        }
    }
}
