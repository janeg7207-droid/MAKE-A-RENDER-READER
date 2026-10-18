using System;
using System.Collections.Generic;
using System.IO;

namespace GorillaStudio.NameFolders
{
    public sealed class DecodedDiscoveryMotion
    {
        public ulong[] Ids { get; internal set; }
        public float[,,] Poses { get; internal set; }
        public ulong[] ScaleIds { get; internal set; }
        public float[,] Scales { get; internal set; }
        public int Frames { get; internal set; }
        public float Fps { get; internal set; }
        public uint Flags { get; internal set; }
    }

    /// <summary>
    /// Reads Discovery body-motion records: six quantized channels per bone,
    /// little-endian bit streams, held/dense segments and entropy split keys.
    /// Rotation keys contain the quaternion logarithm (axis times half-angle).
    /// Unsupported extra channel sections fail explicitly instead of guessing.
    /// </summary>
    public static class DiscoveryMotionDecoder
    {
        public static DecodedDiscoveryMotion Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (data.Length < 0xA0) throw Invalid("The Discovery motion header is truncated.");
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Discovery motion decoding requires a little-endian system.");

            uint tracks = U32(data, 0x20), frames = U32(data, 0x44);
            uint extra38 = U32(data, 0x38), extra60 = U32(data, 0x60);
            uint descriptors = U32(data, 0x78), words = U32(data, 0x90);
            if (tracks == 0 || tracks > 4096 || frames == 0 || frames > 100000
                || (long)tracks * frames * 7 > 32000000)
                throw Invalid("The Discovery motion has an unsupported bone or frame count.");
            if (extra38 != 0 || extra60 != 0)
                throw Invalid("This Discovery motion includes additional channel sections that are not supported yet.");
            if (U32(data, 0x24) != tracks || U32(data, 0x7C) != descriptors || U32(data, 0x94) != words
                || descriptors != tracks * 6)
                throw Invalid("The Discovery motion channel table is inconsistent.");
            long descriptorStart = 0xA0L + tracks * 8L;
            long packedStart = descriptorStart + descriptors * 8L;
            if (packedStart + words * 8L != data.LongLength)
                throw Invalid("The Discovery motion section lengths do not match the record.");
            float fps = F32(data, 0x0C);
            if (!Finite(fps) || fps <= 0 || fps > 1000)
                throw Invalid("The Discovery motion frame rate is invalid.");

            var ids = new ulong[(int)tracks];
            for (int index = 0; index < ids.Length; index++)
                ids[index] = BitConverter.ToUInt64(data, 0xA0 + index * 8);
            var channels = new Channel[(int)descriptors];
            for (int index = 0; index < channels.Length; index++)
            {
                int offset = checked((int)descriptorStart + index * 8);
                int width = data[offset + 1];
                float basis = F32(data, offset + 4);
                if (width > 32 || !Finite(basis) || data[offset + 2] != 0 || data[offset + 3] != 0)
                    throw Invalid("The Discovery motion contains an unsupported channel descriptor.");
                channels[index] = new Channel
                {
                    Exponent = unchecked((sbyte)data[offset]), Width = width, Basis = basis
                };
            }

            var bits = new Bits(data, checked((int)packedStart));
            var poses = new float[(int)tracks, (int)frames, 7];
            for (int bone = 0; bone < ids.Length; bone++)
            {
                double[][] positions = ReadGroup(bits, (int)frames, channels, bone * 6, false);
                double[][] rotations = ReadGroup(bits, (int)frames, channels, bone * 6 + 3, true);
                for (int frame = 0; frame < frames; frame++)
                {
                    for (int axis = 0; axis < 3; axis++) poses[bone, frame, axis] = AsFloat(positions[frame][axis]);
                    for (int axis = 0; axis < 4; axis++) poses[bone, frame, axis + 3] = AsFloat(rotations[frame][axis]);
                }
            }
            if (bits.Length - bits.Position >= 64)
                throw Invalid("The Discovery motion has an unread channel stream.");
            return new DecodedDiscoveryMotion
            {
                Ids = ids, Poses = poses, ScaleIds = new ulong[0], Scales = new float[0, (int)frames],
                Frames = (int)frames, Fps = fps, Flags = U32(data, 0x40)
            };
        }

        private static double[][] ReadGroup(Bits reader, int frames, Channel[] descriptors, int start, bool rotation)
        {
            int stride = descriptors[start].Width + descriptors[start + 1].Width + descriptors[start + 2].Width;
            var result = new double[frames][];
            if (stride == 0)
            {
                double[] constant = { descriptors[start].Basis, descriptors[start + 1].Basis, descriptors[start + 2].Basis };
                if (rotation) constant = Quaternion(constant);
                for (int frame = 0; frame < frames; frame++) result[frame] = constant;
                return result;
            }

            var runs = new List<Run>();
            int covered = 0;
            while (covered < frames)
            {
                bool held = reader.Read(1) != 0;
                bool explicitCount = reader.Read(1) != 0;
                int count = explicitCount ? (int)reader.Read(16) : frames - covered;
                if (count <= 0 || count > frames - covered || runs.Count >= 15)
                    throw Invalid("The Discovery motion timing runs are invalid.");
                runs.Add(new Run { Held = held, Count = count });
                covered += count;
            }

            int mode = (int)reader.Read(8), quantum = mode + 3;
            int entropyWidth = BitWidth((mode + 2) * (mode + 1) / 2 - 1);
            bool compressed = entropyWidth < mode * stride;
            long entropyStart = reader.Position;
            int codeCount = 0, keyCount = 0;
            foreach (Run run in runs)
            {
                run.KeyStart = keyCount;
                run.CodeStart = codeCount;
                keyCount += run.Held ? 1 : compressed ? (run.Count / quantum) * 3 + Math.Min(run.Count % quantum, 3) : run.Count;
                if (!run.Held && compressed) codeCount += (run.Count + quantum - 1) / quantum;
            }
            long valueStart = entropyStart + (compressed ? (long)codeCount * entropyWidth : 0);
            long end = valueStart + (long)keyCount * stride;
            if (keyCount <= 0 || end > reader.Length)
                throw Invalid("The Discovery motion key data is truncated.");
            reader.Position = end;
            var keys = new Dictionary<int, double[]>();
            Func<int, double[]> key = index =>
            {
                if (index < 0 || index >= keyCount) throw Invalid("The Discovery motion references a missing key.");
                double[] values;
                if (keys.TryGetValue(index, out values)) return values;
                values = new double[3];
                long offset = valueStart + (long)index * stride;
                for (int axis = 0; axis < 3; axis++)
                {
                    Channel descriptor = descriptors[start + axis];
                    values[axis] = descriptor.Basis + reader.At(offset, descriptor.Width) * Math.Pow(2, descriptor.Exponent);
                    offset += descriptor.Width;
                }
                if (rotation) values = Quaternion(values);
                keys.Add(index, values);
                return values;
            };

            int output = 0;
            foreach (Run run in runs)
            {
                for (int local = 0; local < run.Count; local++)
                {
                    if (run.Held) { result[output++] = key(run.KeyStart); continue; }
                    if (!compressed) { result[output++] = key(run.KeyStart + local); continue; }

                    int q = local / quantum, offset = local % quantum;
                    int limit = Math.Min(quantum, run.Count - q * quantum);
                    uint code = reader.At(entropyStart + (long)(run.CodeStart + q) * entropyWidth, entropyWidth);
                    if (code >= (uint)((mode + 2) * (mode + 1) / 2))
                        throw Invalid("The Discovery motion contains an invalid entropy split.");
                    int high = 2;
                    while ((long)high * (high - 1) / 2 <= code) high++;
                    int low = checked((int)code - (high - 1) * (high - 2) / 2 + 1);
                    high = Math.Min(high, limit);
                    int part;
                    double fraction;
                    if (offset < low) { part = 0; fraction = (double)offset / Math.Max(low, 1); }
                    else if (offset < high) { part = 1; fraction = (double)(offset - low) / Math.Max(high - low, 1); }
                    else { part = 2; fraction = (double)(offset - high) / Math.Max(limit - high, 1); }
                    int index = run.KeyStart + q * 3 + part;
                    double[] first = key(index);
                    if (fraction == 0 || descriptors[start + 2].Exponent == -127)
                        result[output++] = first;
                    else
                    {
                        double[] second = key(Math.Min(index + 1, keyCount - 1));
                        result[output++] = rotation ? InterpolateRotation(first, second, fraction) : Interpolate(first, second, fraction);
                    }
                }
            }
            return result;
        }

        private static double[] Quaternion(double[] vector)
        {
            double length = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
            double scale = length == 0 ? 1 : Math.Sin(length) / length;
            return new[] { vector[0] * scale, vector[1] * scale, vector[2] * scale, Math.Cos(length) };
        }

        private static double[] InterpolateRotation(double[] first, double[] second, double fraction)
        {
            double dot = 0;
            for (int axis = 0; axis < 4; axis++) dot += first[axis] * second[axis];
            double sign = dot < 0 ? -1 : 1;
            dot = Math.Abs(dot);
            double firstWeight = 1 - fraction, secondWeight = fraction;
            // The original decoded game data uses the linear limit without
            // renormalizing when the quaternions are nearly identical.
            if (dot < 0.9999)
            {
                double angle = Math.Acos(Math.Min(dot, 1)), denominator = Math.Sin(angle);
                firstWeight = Math.Sin((1 - fraction) * angle) / denominator;
                secondWeight = Math.Sin(fraction * angle) / denominator;
            }
            var result = new double[4];
            for (int axis = 0; axis < 4; axis++) result[axis] = first[axis] * firstWeight + second[axis] * secondWeight * sign;
            return result;
        }

        private static double[] Interpolate(double[] first, double[] second, double fraction)
        {
            var result = new double[first.Length];
            for (int axis = 0; axis < result.Length; axis++) result[axis] = first[axis] + (second[axis] - first[axis]) * fraction;
            return result;
        }

        private static uint U32(byte[] bytes, int offset) { return BitConverter.ToUInt32(bytes, offset); }
        private static float F32(byte[] bytes, int offset) { return BitConverter.ToSingle(bytes, offset); }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static float AsFloat(double value)
        {
            float result = (float)value;
            if (!Finite(result)) throw Invalid("The Discovery motion decoded to an invalid pose.");
            return result;
        }
        private static int BitWidth(int number) { int bits = 0; while (number > 0) { number >>= 1; bits++; } return bits; }
        private static InvalidDataException Invalid(string text) { return new InvalidDataException(text); }

        private sealed class Channel { internal int Exponent; internal int Width; internal double Basis; }
        private sealed class Run { internal bool Held; internal int Count; internal int KeyStart; internal int CodeStart; }
        private sealed class Bits
        {
            private readonly byte[] data;
            private readonly int start;
            internal Bits(byte[] data, int start) { this.data = data; this.start = start; Length = (data.LongLength - start) * 8; }
            internal long Position;
            internal long Length { get; private set; }
            internal uint Read(int count) { uint value = At(Position, count); Position += count; return value; }
            internal uint At(long position, int count)
            {
                if (count < 0 || count > 32 || position < 0 || position + count > Length)
                    throw Invalid("The Discovery motion bit stream is truncated.");
                uint value = 0;
                for (int bit = 0; bit < count; bit++)
                {
                    long at = position + bit;
                    value |= (uint)((data[start + checked((int)(at >> 3))] >> (int)(at & 7)) & 1) << bit;
                }
                return value;
            }
        }
    }
}
