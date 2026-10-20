using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GorillaStudio.NameFolders
{
    /// <summary>Compares decoded motion, independently of ZIP packaging and NPY padding.</summary>
    public static class MotionNpzSemanticComparer
    {
        private const long MaximumEntryBytes = 128L * 1024 * 1024;
        private const long MaximumExpandedBytes = 256L * 1024 * 1024;
        private const int MaximumHeaderBytes = 65536;
        private const double QuaternionRounding = 0.000000125;
        // The actual stock t62542 source has 26 rounded samples down to
        // norm 0.9999753423; retain a near-unit guard above that measured bound.
        private const double UnitQuaternionTolerance = 0.00003;
        private static readonly string[] Required = { "ids.npy", "P.npy", "sids.npy", "sc.npy", "hd.npy" };
        private static readonly Regex Descriptor = new Regex(@"'descr'\s*:\s*'(?<value>[<>=|][A-Za-z][0-9]+)'", RegexOptions.CultureInvariant);
        private static readonly Regex Order = new Regex(@"'fortran_order'\s*:\s*(?<value>False|True)", RegexOptions.CultureInvariant);
        private static readonly Regex Shape = new Regex(@"'shape'\s*:\s*\((?<value>[0-9,\s]*)\)", RegexOptions.CultureInvariant);

        public static bool Equivalent(string left, string right)
        {
            try
            {
                using (var first = File.OpenRead(left))
                using (var second = File.OpenRead(right))
                using (var a = new ZipArchive(first, ZipArchiveMode.Read))
                using (var b = new ZipArchive(second, ZipArchiveMode.Read))
                {
                    Dictionary<string, ZipArchiveEntry> x = Entries(a), y = Entries(b);
                    if (x.Count != y.Count || x.Keys.Any(name => !y.ContainsKey(name))) return false;
                    Dictionary<string, object> metadataA, metadataB;
                    int framesA, framesB;
                    if (!Metadata(x["hd.npy"], out metadataA, out framesA)
                        || !Metadata(y["hd.npy"], out metadataB, out framesB)
                        || !SameJson(metadataA, metadataB) || framesA != framesB) return false;
                    Npy idsA = Header(x["ids.npy"]), idsB = Header(y["ids.npy"]);
                    Npy scalesA = Header(x["sids.npy"]), scalesB = Header(y["sids.npy"]);
                    if (!Vector(idsA, "u8") || !Vector(idsB, "u8") || !Vector(scalesA, "u8") || !Vector(scalesB, "u8")
                        || idsA.Dimensions[0] < 1 || idsA.Dimensions[0] > 4096 || scalesA.Dimensions[0] > 4096
                        || !SameShape(idsA, idsB) || !SameShape(scalesA, scalesB)
                        || !SamePayload(x["ids.npy"], idsA, y["ids.npy"], idsB)
                        || !SamePayload(x["sids.npy"], scalesA, y["sids.npy"], scalesB)) return false;
                    Npy posesA = Header(x["P.npy"]), posesB = Header(y["P.npy"]);
                    if (!ArrayShape(posesA, "f4", idsA.Dimensions[0], framesA, 7)
                        || !SameShape(posesA, posesB) || posesB.Type != "f4"
                        || !SamePoses(x["P.npy"], posesA, y["P.npy"], posesB)) return false;
                    Npy scA = Header(x["sc.npy"]), scB = Header(y["sc.npy"]);
                    if (!ArrayShape(scA, "f4", scalesA.Dimensions[0], framesA) || !SameShape(scA, scB) || scB.Type != "f4"
                        || !SameScales(x["sc.npy"], scA, y["sc.npy"], scB)) return false;
                    foreach (string name in x.Keys.Where(name => !Required.Contains(name, StringComparer.Ordinal)))
                        if (!SameBytes(x[name], y[name])) return false;
                    return true;
                }
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException
                || error is OverflowException || error is FormatException || error is UnauthorizedAccessException)
            { return false; }
        }

        private sealed class Npy
        {
            internal string Type;
            internal int[] Dimensions;
            internal long Offset, Bytes;
        }

        private static Dictionary<string, ZipArchiveEntry> Entries(ZipArchive archive)
        {
            if (archive.Entries.Count < Required.Length || archive.Entries.Count > 32) throw new InvalidDataException();
            var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName) || entry.Length < 0 || entry.Length > MaximumEntryBytes
                    || (total = checked(total + entry.Length)) > MaximumExpandedBytes || result.ContainsKey(entry.FullName))
                    throw new InvalidDataException();
                result.Add(entry.FullName, entry);
            }
            if (Required.Any(name => !result.ContainsKey(name))) throw new InvalidDataException();
            return result;
        }

        private static Npy Header(ZipArchiveEntry entry)
        {
            using (var stream = entry.Open())
            using (var reader = new BinaryReader(stream, Encoding.ASCII))
            {
                byte[] magic = reader.ReadBytes(8);
                if (magic.Length != 8 || magic[0] != 0x93 || Encoding.ASCII.GetString(magic, 1, 5) != "NUMPY"
                    || magic[7] != 0 || magic[6] < 1 || magic[6] > 3) throw new InvalidDataException();
                long length = magic[6] == 1 ? reader.ReadUInt16() : reader.ReadUInt32();
                long offset = (magic[6] == 1 ? 10 : 12) + length;
                if (length < 1 || length > MaximumHeaderBytes || offset > entry.Length) throw new InvalidDataException();
                byte[] bytes = reader.ReadBytes((int)length);
                if (bytes.Length != length || bytes.Any(value => value > 127)) throw new InvalidDataException();
                string text = Encoding.ASCII.GetString(bytes);
                MatchCollection types = Descriptor.Matches(text), orders = Order.Matches(text), shapes = Shape.Matches(text);
                if (types.Count != 1 || orders.Count != 1 || shapes.Count != 1 || orders[0].Groups["value"].Value != "False"
                    || !Regex.IsMatch(Shape.Replace(Order.Replace(Descriptor.Replace(text, ""), ""), ""), @"\A\s*\{[\s,]*\}\s*\z"))
                    throw new InvalidDataException();
                string descriptor = types[0].Groups["value"].Value;
                if (descriptor[0] != '<' && !(descriptor[0] == '=' && BitConverter.IsLittleEndian)) throw new InvalidDataException();
                string type = descriptor.Substring(1);
                int width = int.Parse(type.Substring(1), CultureInfo.InvariantCulture);
                if (width < 1 || width > MaximumHeaderBytes || (type[0] != 'U' && type != "u8" && type != "f4")) throw new InvalidDataException();
                if (type[0] == 'U') width = checked(width * 4);
                string shape = shapes[0].Groups["value"].Value.Trim();
                int[] dimensions = shape.Length == 0 ? new int[0] : shape.TrimEnd().TrimEnd(',').Split(',')
                    .Select(value => int.Parse(value.Trim(), CultureInfo.InvariantCulture)).ToArray();
                if (dimensions.Length > 8 || dimensions.Any(value => value < 0)) throw new InvalidDataException();
                long count = 1;
                foreach (int value in dimensions) count = checked(count * value);
                long payload = checked(count * width);
                if (payload > MaximumEntryBytes || offset + payload != entry.Length) throw new InvalidDataException();
                return new Npy { Type = type, Dimensions = dimensions, Offset = offset, Bytes = payload };
            }
        }

        private static bool Vector(Npy array, string type) { return array.Type == type && array.Dimensions.Length == 1; }
        private static bool ArrayShape(Npy array, string type, params int[] shape)
        { return array.Type == type && array.Dimensions.SequenceEqual(shape); }
        private static bool SameShape(Npy a, Npy b) { return a.Dimensions.SequenceEqual(b.Dimensions); }

        private static BinaryReader Payload(ZipArchiveEntry entry, Npy header)
        {
            var reader = new BinaryReader(entry.Open(), Encoding.ASCII);
            try
            {
                for (long left = header.Offset; left > 0;)
                {
                    int count = (int)Math.Min(left, 4096);
                    if (reader.ReadBytes(count).Length != count) throw new EndOfStreamException();
                    left -= count;
                }
                return reader;
            }
            catch { reader.Dispose(); throw; }
        }

        private static bool SamePayload(ZipArchiveEntry a, Npy ah, ZipArchiveEntry b, Npy bh)
        {
            if (ah.Bytes != bh.Bytes) return false;
            using (var x = Payload(a, ah)) using (var y = Payload(b, bh)) return EqualStreams(x.BaseStream, y.BaseStream, ah.Bytes);
        }
        private static bool SameBytes(ZipArchiveEntry a, ZipArchiveEntry b)
        {
            if (a.Length != b.Length) return false;
            using (var x = a.Open()) using (var y = b.Open()) return EqualStreams(x, y, a.Length);
        }
        private static bool EqualStreams(Stream a, Stream b, long length)
        {
            var left = new byte[8192]; var right = new byte[8192];
            for (long done = 0; done < length;)
            {
                int count = (int)Math.Min(left.Length, length - done);
                ReadExactly(a, left, count); ReadExactly(b, right, count);
                for (int i = 0; i < count; i++) if (left[i] != right[i]) return false;
                done += count;
            }
            return a.ReadByte() == -1 && b.ReadByte() == -1;
        }
        private static void ReadExactly(Stream stream, byte[] bytes, int count)
        {
            int offset = 0;
            while (offset < count) { int read = stream.Read(bytes, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
        }

        private static bool SamePoses(ZipArchiveEntry a, Npy ah, ZipArchiveEntry b, Npy bh)
        {
            using (var x = Payload(a, ah)) using (var y = Payload(b, bh))
            {
                for (long sample = 0; sample < ah.Bytes / 28; sample++)
                {
                    double normA = 0, normB = 0; bool rounded = false;
                    for (int channel = 0; channel < 7; channel++)
                    {
                        float left = x.ReadSingle(), right = y.ReadSingle();
                        if (!Finite(left) || !Finite(right)) return false;
                        if (channel < 3) { if (left != right) return false; }
                        else
                        {
                            normA += (double)left * left; normB += (double)right * right;
                            if (left != right)
                            {
                                // The production .clip decoder and NumPy stock
                                // decode differ by <=1.25e-7 in quaternion math.
                                if (Math.Abs((double)left - right) > QuaternionRounding) return false;
                                rounded = true;
                            }
                        }
                    }
                    if (rounded && (Math.Abs(Math.Sqrt(normA) - 1) > UnitQuaternionTolerance
                        || Math.Abs(Math.Sqrt(normB) - 1) > UnitQuaternionTolerance)) return false;
                }
                return x.BaseStream.ReadByte() == -1 && y.BaseStream.ReadByte() == -1;
            }
        }
        private static bool SameScales(ZipArchiveEntry a, Npy ah, ZipArchiveEntry b, Npy bh)
        {
            using (var x = Payload(a, ah)) using (var y = Payload(b, bh))
            {
                for (long index = 0; index < ah.Bytes / 4; index++)
                { float left = x.ReadSingle(), right = y.ReadSingle(); if (!Finite(left) || !Finite(right) || left != right) return false; }
                return x.BaseStream.ReadByte() == -1 && y.BaseStream.ReadByte() == -1;
            }
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static bool Metadata(ZipArchiveEntry entry, out Dictionary<string, object> values, out int frames)
        {
            values = null; frames = 0;
            Npy header = Header(entry);
            if (!header.Type.StartsWith("U", StringComparison.Ordinal) || header.Dimensions.Length != 0 || header.Bytes > MaximumHeaderBytes) return false;
            string json;
            using (var reader = Payload(entry, header))
            {
                byte[] bytes = reader.ReadBytes((int)header.Bytes);
                if (bytes.Length != header.Bytes || reader.BaseStream.ReadByte() != -1) return false;
                json = new UTF32Encoding(false, false, true).GetString(bytes);
            }
            values = new JsonReader(json).Read() as Dictionary<string, object>;
            object count, fps, flags;
            if (values == null || !values.TryGetValue("frames", out count) || !(count is decimal)
                || !values.TryGetValue("fps", out fps) || !(fps is decimal)
                || !values.TryGetValue("u32_0x40", out flags) || !(flags is decimal)) return false;
            decimal number = (decimal)count, rate = (decimal)fps, bits = (decimal)flags;
            if (number < 1 || number > 100000 || decimal.Truncate(number) != number || rate <= 0 || rate > 1000
                || bits < 0 || bits > uint.MaxValue || decimal.Truncate(bits) != bits) return false;
            frames = (int)number; return true;
        }

        private static bool SameJson(object left, object right)
        {
            if (left == null || right == null) return left == right;
            var a = left as Dictionary<string, object>; var b = right as Dictionary<string, object>;
            if (a != null || b != null) return a != null && b != null && a.Count == b.Count
                && a.All(pair => b.ContainsKey(pair.Key) && SameJson(pair.Value, b[pair.Key]));
            var x = left as List<object>; var y = right as List<object>;
            if (x != null || y != null) return x != null && y != null && x.Count == y.Count
                && x.Select((value, index) => SameJson(value, y[index])).All(value => value);
            return left.Equals(right);
        }

        // hd.npy contains a small JSON scalar. Keep this parser self-contained
        // so net48 and test runtimes reject duplicate keys in the same way.
        private sealed class JsonReader
        {
            private readonly string text; private int position;
            internal JsonReader(string value) { text = value; }
            internal object Read() { object value = Value(0); Space(); if (position != text.Length) throw new InvalidDataException(); return value; }
            private void Space() { while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++; }
            private bool Take(char value) { Space(); if (position < text.Length && text[position] == value) { position++; return true; } return false; }
            private object Value(int depth)
            {
                if (depth > 16) throw new InvalidDataException();
                Space(); if (position >= text.Length) throw new InvalidDataException();
                if (text[position] == '"') return String();
                if (Take('{'))
                {
                    var result = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) return result;
                    do { string key = String(); if (!Take(':') || result.ContainsKey(key)) throw new InvalidDataException(); result.Add(key, Value(depth + 1)); } while (Take(','));
                    if (!Take('}')) throw new InvalidDataException(); return result;
                }
                if (Take('['))
                {
                    var result = new List<object>(); if (Take(']')) return result;
                    do { if (result.Count > 4096) throw new InvalidDataException(); result.Add(Value(depth + 1)); } while (Take(','));
                    if (!Take(']')) throw new InvalidDataException(); return result;
                }
                foreach (string word in new[] { "true", "false", "null" })
                    if (position + word.Length <= text.Length && string.CompareOrdinal(text, position, word, 0, word.Length) == 0)
                    { position += word.Length; return word == "null" ? null : (object)(word == "true"); }
                Match number = Regex.Match(text.Substring(position), @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?");
                decimal parsed;
                if (!number.Success || !decimal.TryParse(number.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) throw new InvalidDataException();
                position += number.Length; return parsed;
            }
            private string String()
            {
                if (!Take('"')) throw new InvalidDataException();
                var result = new StringBuilder();
                while (position < text.Length)
                {
                    char value = text[position++]; if (value == '"') return result.ToString();
                    if (value < 32) throw new InvalidDataException();
                    if (value == '\\')
                    {
                        if (position >= text.Length) throw new InvalidDataException();
                        char escaped = text[position++];
                        int index = "\"\\/bfnrt".IndexOf(escaped);
                        if (index >= 0) value = "\"\\/\b\f\n\r\t"[index];
                        else if (escaped == 'u' && position + 4 <= text.Length)
                        { value = (char)ushort.Parse(text.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture); position += 4; }
                        else throw new InvalidDataException();
                    }
                    result.Append(value);
                }
                throw new InvalidDataException();
            }
        }
    }
}
