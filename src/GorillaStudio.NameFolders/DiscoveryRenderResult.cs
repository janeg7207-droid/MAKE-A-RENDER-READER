using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GorillaStudio.NameFolders
{
    /// <summary>Finishes native render results with authoritative Discovery picture keys.</summary>
    public static class DiscoveryRenderResult
    {
        private const string MissingPictureWarning = "no render picture found for this wrestler: the match card works, the select screen card cannot be linked";

        /// <summary>
        /// Restores actual profile hashes after the native renderer has completed. Full
        /// renders also receive the current shared owners and complete installation map.
        /// A failed full-render update never claims success in the result dictionary.
        /// Preview results update their display links without writing installation files.
        /// </summary>
        public static bool Complete(IDictionary<string, object> result, string originalTable)
        {
            if (result == null || result.IsReadOnly || string.IsNullOrWhiteSpace(originalTable)) return false;
            string mapPath = null, mapBackup = null;
            byte[] mapContents = null;
            bool mapWritten = false, renderUpdated = false;
            try
            {
                object value;
                int id;
                if (!result.TryGetValue("id", out value) || !TryId(value, out id)) return false;
                var index = DiscoveryRenderLinks.Read(originalTable);
                List<ulong> selected;
                if (!index.TryGetValue(id, out selected)) return false;
                string[] hashes = selected.Select(hash => hash.ToString("X16", CultureInfo.InvariantCulture))
                    .OrderBy(hash => hash, StringComparer.Ordinal).ToArray();
                var selectedSet = new HashSet<ulong>(selected);
                int[] owners = index.Where(pair => pair.Key != id && pair.Value.Any(selectedSet.Contains))
                    .Select(pair => pair.Key).OrderBy(owner => owner).ToArray();
                var linked = new ArrayList(hashes);
                var shared = new ArrayList();
                var alsoFor = new ArrayList(owners.Select(owner => owner.ToString(CultureInfo.InvariantCulture)).ToArray());

                if (result.TryGetValue("gmr", out value))
                {
                    string gmr = value as string;
                    if (string.IsNullOrWhiteSpace(gmr)) return false;
                    gmr = Path.GetFullPath(gmr);
                    if (!File.Exists(gmr)) return false;
                    mapPath = Path.Combine(Path.GetDirectoryName(gmr), "_roster_pictures.txt");
                    mapContents = BuildMap(index);
                    WriteMap(mapPath, mapContents, out mapBackup);
                    mapWritten = true;
                    if (!GorillaRenderMetadata.SetRosterHashes(gmr, id, hashes, owners, new string[0])) return false;
                    renderUpdated = true;
                }

                result["linked"] = linked;
                result["shared"] = shared;
                result["also_for"] = alsoFor;
                if (hashes.Length > 0 && result.TryGetValue("warning", out value)
                    && string.Equals(value as string, MissingPictureWarning, StringComparison.Ordinal)) result.Remove("warning");
                return true;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException || error is InvalidOperationException || error is FormatException
                || error is OverflowException || error is NotSupportedException || error is System.Security.SecurityException)
            {
                return false;
            }
            finally
            {
                if (mapWritten && !renderUpdated) RestoreMap(mapPath, mapContents, ref mapBackup);
                if (renderUpdated && mapBackup != null) DeleteTemporary(mapBackup);
            }
        }

        private static bool TryId(object value, out int id)
        {
            id = 0;
            if (value == null || value.GetType().IsEnum) return false;
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Double: case TypeCode.Single:
                    double floating = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(floating) || double.IsInfinity(floating) || floating < 1 || floating > 65535
                        || floating != Math.Truncate(floating)) return false;
                    id = (int)floating;
                    return true;
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Decimal:
                    decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    if (number < 1 || number > 65535 || number != decimal.Truncate(number)) return false;
                    id = (int)number;
                    return true;
                default: return false;
            }
        }

        private static byte[] BuildMap(Dictionary<int, List<ulong>> index)
        {
            var owners = new SortedDictionary<ulong, SortedSet<int>>();
            foreach (var pair in index)
                foreach (ulong hash in pair.Value)
                {
                    SortedSet<int> ids;
                    if (!owners.TryGetValue(hash, out ids)) owners[hash] = ids = new SortedSet<int>();
                    ids.Add(pair.Key);
                }
            var text = new StringBuilder();
            foreach (var pair in owners)
            {
                text.Append(pair.Key.ToString("X16", CultureInfo.InvariantCulture));
                foreach (int owner in pair.Value) text.Append(' ').Append(owner.ToString(CultureInfo.InvariantCulture));
                text.Append('\n');
            }
            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        private static void WriteMap(string path, byte[] contents, out string backup)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path), ".roster-map-" + Guid.NewGuid().ToString("N") + ".tmp");
            backup = null;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    string prior = Path.Combine(Path.GetDirectoryName(path), ".roster-map-before-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.Replace(temporary, path, prior);
                    backup = prior;
                }
                else File.Move(temporary, path);
            }
            finally { DeleteTemporary(temporary); }
        }

        private static void RestoreMap(string path, byte[] written, ref string backup)
        {
            try
            {
                // Preserve a newer writer's map if another operation replaced ours.
                if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(written)) return;
                if (backup == null) File.Delete(path);
                else { File.Replace(backup, path, null); backup = null; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            // If restoring is blocked, retain the backup for recovery. The replacement
            // map still contains actual profile hashes and never synthetic aliases.
        }

        private static void DeleteTemporary(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
