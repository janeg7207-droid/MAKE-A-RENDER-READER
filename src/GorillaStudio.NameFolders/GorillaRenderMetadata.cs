using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
#if NET8_0_OR_GREATER
using System.Text.Json;
#else
using System.Web.Script.Serialization;
#endif

namespace GorillaStudio.NameFolders
{
    /// <summary>Updates roster picture keys without re-encoding a completed Gorilla Render.</summary>
    public static class GorillaRenderMetadata
    {
        private const int MaximumMetadataBytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Changes only the screen setting of an already completed render. Callers
        /// that preserve a saved result must invoke this on their private copy.
        /// Video bytes, picture associations, logical ID and ending are retained.
        /// </summary>
        public static bool SetScreens(string path, int wrestlerId, string screens)
        {
            if (string.IsNullOrWhiteSpace(path) || wrestlerId < 1 || wrestlerId > 65535 || (screens != "select" && screens != "both")) return false;
            string temporary = null;
            try
            {
                string full = Path.GetFullPath(path);
                var original = new FileInfo(full);
                if (!original.Exists) return false;
                long length = original.Length, ticks = original.LastWriteTimeUtc.Ticks;
                using (var source = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    long videoLength;
                    Dictionary<string, object> metadata;
                    if (!Read(source, out videoLength, out metadata) || !Supported(metadata, wrestlerId)
                        || !FullVideo(source, videoLength)) return false;
                    metadata["screens"] = screens;
                    byte[] json = Utf8.GetBytes(Serialize(metadata));
                    if (json.Length == 0 || json.Length > 65536) return false;
                    temporary = Path.Combine(Path.GetDirectoryName(full), "." + Path.GetFileName(full) + ".screens-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        source.Position = 0;
                        CopyExactly(source, destination, videoLength);
                        destination.Write(json, 0, json.Length);
                        using (var footer = new BinaryWriter(destination, Utf8, true))
                        { footer.Write((uint)json.Length); footer.Write(new byte[] { (byte)'G', (byte)'M', (byte)'R', (byte)'1' }); footer.Flush(); }
                        destination.Flush(true);
                    }
                    original.Refresh();
                    if (!original.Exists || original.Length != length || original.LastWriteTimeUtc.Ticks != ticks) return false;
                    File.Replace(temporary, full, null); temporary = null;
                    return true;
                }
            }
            catch (Exception error) when (MetadataError(error)) { return false; }
            finally
            {
                if (temporary != null) try { File.Delete(temporary); } catch (Exception error) when (MetadataError(error)) { }
            }
        }

        internal static bool InspectCompleted(string path, int wrestlerId, out long videoLength, out Dictionary<string, object> metadata)
        {
            videoLength = 0; metadata = null;
            try
            {
                using (var source = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read))
                    return Read(source, out videoLength, out metadata) && Supported(metadata, wrestlerId) && FullVideo(source, videoLength);
            }
            catch (Exception error) when (MetadataError(error)) { return false; }
        }

        internal static bool SameExceptScreens(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            if (left == null || right == null) return false;
            var a = new Dictionary<string, object>(left, StringComparer.Ordinal);
            var b = new Dictionary<string, object>(right, StringComparer.Ordinal);
            a.Remove("screens"); b.Remove("screens");
            return Serialize(a) == Serialize(b);
        }

        private static bool Supported(Dictionary<string, object> metadata, int wrestlerId)
        {
            object format, version;
            if (metadata == null || !metadata.TryGetValue("format", out format) || (format as string) != "GorillaRender"
                || !metadata.TryGetValue("version", out version) || !MatchesWrestler(metadata, wrestlerId)) return false;
            if (version == null || version is string || version is bool) return false;
            try { return Convert.ToDecimal(version, CultureInfo.InvariantCulture) == 1; }
            catch (Exception error) when (error is InvalidCastException || error is FormatException || error is OverflowException) { return false; }
        }

        private static bool FullVideo(FileStream stream, long videoLength)
        {
            if (stream.Length - videoLength - 8 > 65536 || stream.Length >= 2147483648L) return false;
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, Utf8, true))
            {
                if (reader.ReadUInt32() != 0x694B4942 || reader.ReadUInt32() + 8L != videoLength) return false;
                uint frames = reader.ReadUInt32(), largest = reader.ReadUInt32();
                if (frames == 0 || frames > 100000 || reader.ReadUInt32() != frames || reader.ReadUInt32() != 1024 || reader.ReadUInt32() != 1024
                    || reader.ReadUInt32() != 30 || reader.ReadUInt32() != 1 || (reader.ReadUInt32() & 1048576) == 0 || reader.ReadUInt32() != 0) return false;
                uint previous = reader.ReadUInt32();
                if ((previous & 1) == 0 || (previous & ~1U) != 44L + (frames + 1L) * 4) return false;
                for (uint frame = 0; frame < frames; frame++)
                {
                    uint next = reader.ReadUInt32(), offset = next & ~1U, old = previous & ~1U;
                    if (offset <= old || offset - old > largest || offset > videoLength || (frame + 1 < frames && (next & 1) == 0)) return false;
                    previous = next;
                }
                return (previous & ~1U) == videoLength;
            }
        }

        private static bool MetadataError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException
                || error is InvalidOperationException || error is FormatException || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                ;
        }

        /// <summary>
        /// Replaces the picture keys for the specified logical wrestler. Hashes must be
        /// exactly sixteen hexadecimal characters. Invalid or unreadable inputs return
        /// false and leave the original file untouched. Named keys are removed because
        /// the original writer also derives hashes from names; the supplied hashes are
        /// authoritative. The Bink video and every other metadata field are preserved.
        /// </summary>
        public static bool SetRosterHashes(string path, int wrestlerId, IEnumerable<string> hashes)
        {
            return SetRosterHashes(path, wrestlerId, hashes, null, null, false);
        }

        /// <summary>
        /// Also replaces the shared owners and shared-picture keys with current profile
        /// data. Owner IDs are deduplicated and the selected wrestler is excluded.
        /// Supplying empty collections explicitly clears stale shared associations.
        /// </summary>
        public static bool SetRosterHashes(string path, int wrestlerId, IEnumerable<string> hashes,
            IEnumerable<int> extraIds, IEnumerable<string> sharedHashes)
        {
            if (extraIds == null || sharedHashes == null) return false;
            return SetRosterHashes(path, wrestlerId, hashes, extraIds, sharedHashes, true);
        }

        private static bool SetRosterHashes(string path, int wrestlerId, IEnumerable<string> hashes,
            IEnumerable<int> extraIds, IEnumerable<string> sharedHashes, bool replaceShared)
        {
            if (string.IsNullOrWhiteSpace(path) || wrestlerId < 1 || wrestlerId > 65535 || hashes == null) return false;
            string temporary = null;
            try
            {
                List<string> normalized;
                if (!NormalizeHashes(hashes, out normalized)) return false;
                var normalizedOwners = new List<int>();
                List<string> normalizedShared = null;
                if (replaceShared)
                {
                    if (!NormalizeHashes(sharedHashes, out normalizedShared)) return false;
                    var seenOwners = new HashSet<int>();
                    foreach (int owner in extraIds)
                    {
                        if (owner < 1 || owner > 65535) return false;
                        if (owner != wrestlerId && seenOwners.Add(owner)) normalizedOwners.Add(owner);
                    }
                }

                string fullPath = Path.GetFullPath(path);
                var original = new FileInfo(fullPath);
                if (!original.Exists) return false;
                long originalLength = original.Length, originalTicks = original.LastWriteTimeUtc.Ticks;
                using (var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    long videoLength;
                    Dictionary<string, object> metadata;
                    if (!Read(source, out videoLength, out metadata) || !MatchesWrestler(metadata, wrestlerId)) return false;
                    metadata["render_hashes"] = normalized;
                    metadata.Remove("render_names");
                    if (replaceShared)
                    {
                        metadata["extra_ids"] = normalizedOwners;
                        metadata["shared_hashes"] = normalizedShared;
                    }
                    byte[] json = Utf8.GetBytes(Serialize(metadata));
                    if (json.Length == 0 || json.Length > MaximumMetadataBytes) return false;

                    temporary = Path.Combine(Path.GetDirectoryName(fullPath), "." + Path.GetFileName(fullPath)
                        + ".roster-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        source.Position = 0;
                        CopyExactly(source, destination, videoLength);
                        destination.Write(json, 0, json.Length);
                        using (var footer = new BinaryWriter(destination, Utf8, true))
                        {
                            footer.Write((uint)json.Length);
                            footer.Write(new byte[] { (byte)'G', (byte)'M', (byte)'R', (byte)'1' });
                            footer.Flush();
                        }
                        destination.Flush(true);
                    }

                    // Never replace a different render if the output changed during inspection.
                    original.Refresh();
                    if (!original.Exists || original.Length != originalLength || original.LastWriteTimeUtc.Ticks != originalTicks) return false;
                    File.Replace(temporary, fullPath, null);
                    temporary = null;
                    return true;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is InvalidOperationException || error is FormatException
                || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException
#if NET8_0_OR_GREATER
                || error is JsonException
#endif
                )
            {
                return false;
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool Read(FileStream source, out long videoLength, out Dictionary<string, object> metadata)
        {
            videoLength = 0; metadata = null;
            if (source.Length < 52 + 8) return false;
            using (var reader = new BinaryReader(source, Utf8, true))
            {
                source.Position = source.Length - 8;
                uint size = reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x31524D47 || size == 0 || size > MaximumMetadataBytes || size > source.Length - 8 - 52) return false;
                videoLength = source.Length - 8 - size;
                source.Position = 0;
                if (reader.ReadUInt32() != 0x694B4942 || (long)reader.ReadUInt32() + 8 != videoLength) return false;
                uint frames = reader.ReadUInt32();
                reader.ReadUInt32(); // Largest encoded frame; preserved with the video.
                uint repeatedFrames = reader.ReadUInt32();
                if (frames == 0 || frames != repeatedFrames || 44L + ((long)frames + 1) * 4 > videoLength) return false;
                source.Position = videoLength;
                byte[] json = reader.ReadBytes((int)size);
                if (json.Length != size) return false;
                metadata = Deserialize(Utf8.GetString(json));
                return metadata != null;
            }
        }

        private static bool MatchesWrestler(Dictionary<string, object> metadata, int wrestlerId)
        {
            object value;
            if (!metadata.TryGetValue("wrestler_id", out value) || value == null || value is string || value is bool) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Decimal: case TypeCode.Double: case TypeCode.Single:
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture) == wrestlerId;
                default: return false;
            }
        }

        private static bool ValidHash(string value)
        {
            if (value == null || value.Length != 16) return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F'))) return false;
            return true;
        }

        private static bool NormalizeHashes(IEnumerable<string> hashes, out List<string> normalized)
        {
            normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string hash in hashes)
            {
                if (!ValidHash(hash)) return false;
                string value = hash.ToUpperInvariant();
                if (seen.Add(value)) normalized.Add(value);
            }
            return true;
        }

        private static void CopyExactly(Stream source, Stream destination, long length)
        {
            byte[] buffer = new byte[81920];
            while (length > 0)
            {
                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (count == 0) throw new EndOfStreamException("The render video changed while its roster keys were being saved.");
                destination.Write(buffer, 0, count);
                length -= count;
            }
        }

        private static Dictionary<string, object> Deserialize(string json)
        {
#if NET8_0_OR_GREATER
            using (JsonDocument document = JsonDocument.Parse(json)) return ConvertJson(document.RootElement) as Dictionary<string, object>;
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumMetadataBytes, RecursionLimit = 100 }.DeserializeObject(json) as Dictionary<string, object>;
#endif
        }

        private static string Serialize(Dictionary<string, object> metadata)
        {
#if NET8_0_OR_GREATER
            return JsonSerializer.Serialize(metadata);
#else
            return new JavaScriptSerializer { MaxJsonLength = MaximumMetadataBytes, RecursionLimit = 100 }.Serialize(metadata);
#endif
        }

#if NET8_0_OR_GREATER
        private static object ConvertJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject()) dictionary.Add(property.Name, ConvertJson(property.Value));
                    return dictionary;
                case JsonValueKind.Array:
                    var array = new List<object>();
                    foreach (JsonElement value in element.EnumerateArray()) array.Add(ConvertJson(value));
                    return array;
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    long integer; ulong unsigned; decimal number;
                    if (element.TryGetInt64(out integer)) return integer;
                    if (element.TryGetUInt64(out unsigned)) return unsigned;
                    if (element.TryGetDecimal(out number)) return number;
                    return element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
