using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    public sealed class VerifiedTauntRecord
    {
        public string Clip { get; internal set; }
        public string Sha256 { get; internal set; }
        public string Provenance { get; internal set; }
        internal byte[] Bytes;
    }

    /// <summary>Exact body records verified from the supplied animation archive; never a model or name substitution.</summary>
    public sealed class VerifiedTauntRecordPack : IDisposable
    {
        private const string ResourceName = "GorillaStudio.NameFolders.VerifiedTauntRecords.zip";
        private const int MaximumPackBytes = 128 * 1024 * 1024;
        private const int MaximumRecordBytes = 128 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly Dictionary<string, VerifiedTauntRecord> validated = new Dictionary<string, VerifiedTauntRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> unsupported = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> warnings = new List<string>();
        private ZipArchive archive;
        private bool disposed;
        private static readonly Lazy<VerifiedTauntRecordPack> bundled = new Lazy<VerifiedTauntRecordPack>(LoadBundled);

        private sealed class Record { internal ZipArchiveEntry Entry; internal int Size; internal string Hash, Provenance; }
        public string Sha256 { get; private set; }
        public int Count { get { lock (gate) return records.Count; } }
        public int UnsupportedCount { get { lock (gate) return unsupported.Count; } }
        public IReadOnlyList<string> Warnings { get { lock (gate) return warnings.ToArray(); } }
        internal static VerifiedTauntRecordPack Bundled { get { return bundled.Value; } }

        private static VerifiedTauntRecordPack LoadBundled()
        {
            using (Stream source = typeof(VerifiedTauntRecordPack).Assembly.GetManifestResourceStream(ResourceName))
                return Read(source);
        }

        public static VerifiedTauntRecordPack Read(Stream source)
        {
            var result = new VerifiedTauntRecordPack();
            if (source == null) return result; // Older/source-only builds need no optional resource.
            try
            {
                byte[] bytes = ReadBounded(source, MaximumPackBytes);
                result.Sha256 = Hash(bytes);
                result.archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read, false);
                var entries = result.archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
                ZipArchiveEntry manifest;
                if (!entries.TryGetValue("manifest.json", out manifest) || manifest.Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("The verified taunt pack has no bounded provenance manifest.");
                Dictionary<string, object> metadata;
                using (Stream input = manifest.Open())
                    metadata = DiscoveryTauntLibrary.JsonObject(new UTF8Encoding(false, true).GetString(ReadBounded(input, 4 * 1024 * 1024)));
                object value;
                if (!metadata.TryGetValue("version", out value) || Convert.ToInt32(value, CultureInfo.InvariantCulture) != 1
                    || !metadata.TryGetValue("records", out value) || !(value is IEnumerable) || value is string)
                    throw new InvalidDataException("The verified taunt pack manifest version or record list is invalid.");
                long total = 0;
                foreach (object item in (IEnumerable)value)
                {
                    var row = item as Dictionary<string, object>;
                    string clip = Text(row, "clip"), sha = Text(row, "sha256");
                    if (!Regex.IsMatch(clip, @"\A[1-9][0-9]{0,9}a[0-9]{3,10}\z")
                        || !Regex.IsMatch(sha, @"\A[0-9a-fA-F]{64}\z")
                        || !string.Equals(Text(row, "role"), "body", StringComparison.Ordinal))
                        throw new InvalidDataException("The verified taunt pack contains an invalid canonical body-record claim.");
                    int size = Convert.ToInt32(row["size"], CultureInfo.InvariantCulture);
                    string entryName = "records/" + clip + ".clip";
                    ZipArchiveEntry entry;
                    if (size < 160 || size > MaximumRecordBytes || !entries.TryGetValue(entryName, out entry) || entry.Length != size)
                        throw new InvalidDataException("The verified body record " + clip + " is absent or has a different size.");
                    if (result.records.ContainsKey(clip)) throw new InvalidDataException("The verified taunt pack repeats " + clip + ".");
                    total += size;
                    if (total > MaximumPackBytes) throw new InvalidDataException("The verified taunt record payload exceeds its size limit.");
                    object sources;
                    if (!row.TryGetValue("sources", out sources) || !(sources is IEnumerable) || sources is string
                        || !((IEnumerable)sources).Cast<object>().Any())
                        throw new InvalidDataException("The verified body record " + clip + " has no source provenance.");
                    result.records.Add(clip, new Record { Entry = entry, Size = size, Hash = sha.ToLowerInvariant(),
                        Provenance = DiscoveryTauntLibrary.Json(row) });
                }
                if (metadata.TryGetValue("unsupported", out value))
                {
                    if (!(value is IEnumerable) || value is string)
                        throw new InvalidDataException("The verified taunt pack has an invalid unsupported-record report.");
                    foreach (object item in (IEnumerable)value)
                    {
                        var row = item as Dictionary<string, object>;
                        string clip = Text(row, "clip"), reason = Text(row, "reason");
                        if (!Regex.IsMatch(clip, @"\A[1-9][0-9]{0,9}a[0-9]{3,10}\z") || reason.Length == 0 || reason.Length > 8192
                            || Text(row, "role") != "body" || result.records.ContainsKey(clip) || result.unsupported.ContainsKey(clip))
                            throw new InvalidDataException("The verified taunt pack has an invalid unsupported-body claim.");
                        var archiveSource = metadata.ContainsKey("archive") ? metadata["archive"] as Dictionary<string, object> : null;
                        result.unsupported.Add(clip, "Body animation " + clip + " was located in "
                            + (Text(archiveSource, "name").Length > 0 ? Text(archiveSource, "name") : "the verified source archive")
                            + ", but the current animation decoder cannot read it: " + reason);
                    }
                }
            }
            catch (Exception error) when (Expected(error))
            {
                result.records.Clear();
                result.unsupported.Clear();
                if (result.archive != null) { result.archive.Dispose(); result.archive = null; }
                result.warnings.Add("Verified taunt pack could not be read: " + error.Message);
            }
            return result;
        }

        public bool TryRead(string clip, out VerifiedTauntRecord record)
        {
            record = null;
            if (clip == null) return false;
            lock (gate)
            {
                if (disposed || failed.Contains(clip)) return false;
                if (validated.TryGetValue(clip, out record)) return true;
                Record source;
                if (!records.TryGetValue(clip, out source)) return false;
                try
                {
                    byte[] bytes;
                    using (Stream input = source.Entry.Open()) bytes = ReadBounded(input, source.Size);
                    if (bytes.Length != source.Size || !string.Equals(Hash(bytes), source.Hash, StringComparison.Ordinal)
                        || BitConverter.ToUInt64(bytes, 0) != DiscoveryMotionAssets.ClipId(clip))
                        throw new InvalidDataException("The record's size, SHA-256 or canonical animation ID does not match its provenance.");
                    record = new VerifiedTauntRecord { Clip = clip, Sha256 = source.Hash, Bytes = bytes, Provenance = source.Provenance };
                    validated.Add(clip, record);
                    return true;
                }
                catch (Exception error) when (Expected(error))
                {
                    failed.Add(clip);
                    warnings.Add("Verified body record " + clip + " was ignored: " + error.Message);
                    return false;
                }
            }
        }

        public bool TryGetUnsupported(string clip, out string reason)
        {
            lock (gate)
            {
                reason = null;
                return !disposed && clip != null && unsupported.TryGetValue(clip, out reason);
            }
        }

        private static byte[] ReadBounded(Stream input, int maximum)
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[16384];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length > maximum - count) throw new InvalidDataException("The verified taunt pack record exceeds its size limit.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }

        private static string Text(Dictionary<string, object> row, string key)
        {
            object value;
            return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static bool Expected(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is ArgumentException
                || error is InvalidOperationException || error is FormatException || error is OverflowException
                || error is KeyNotFoundException
#if NET8_0_OR_GREATER
                || error is System.Text.Json.JsonException
#endif
                ;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; validated.Clear(); records.Clear(); unsupported.Clear();
                if (archive != null) archive.Dispose();
            }
        }
    }
}
