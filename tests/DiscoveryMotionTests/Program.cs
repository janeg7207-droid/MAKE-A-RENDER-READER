using System;
using System.Collections.Generic;
using System.IO;
using GorillaStudio.NameFolders;

internal static class Program
{
    private static int passed;

    private static void Main(string[] args)
    {
        byte[] constant = Fixture(3, null);
        SetFloat(constant, Descriptor(1) + 4, 2.5f);
        DecodedDiscoveryMotion motion = DiscoveryMotionDecoder.Decode(constant);
        Equal("Static motion retains its frame count", 3, motion.Frames);
        Equal("Static motion retains its frame rate", 60f, motion.Fps);
        Equal("Static motion retains its source flags", 24u, motion.Flags);
        Equal("Bone hashes remain 64-bit values", 0xFEDCBA9876543210UL, motion.Ids[0]);
        Equal("Static translation is held across frames", 2.5f, motion.Poses[0, 2, 1]);
        Equal("A zero rotation decodes to identity", 1f, motion.Poses[0, 0, 6]);
        Equal("A body-only clip has no invented scale tracks", 0, motion.ScaleIds.Length);
        Equal("Empty scale data retains the frame dimension", 3, motion.Scales.GetLength(1));

        var dense = new BitWriter(); dense.Write(0, 1); dense.Write(0, 1); dense.Write(0, 8);
        dense.Write(1, 3); dense.Write(3, 3); dense.Write(5, 3);
        byte[] denseData = Fixture(3, dense);
        Describe(denseData, 0, -1, 3, 10);
        motion = DiscoveryMotionDecoder.Decode(denseData);
        Equal("Dense keys use the signed quantization exponent", 10.5f, motion.Poses[0, 0, 0]);
        Equal("Dense keys are read little-endian across bit boundaries", 11.5f, motion.Poses[0, 1, 0]);
        Equal("Dense keys retain their last frame", 12.5f, motion.Poses[0, 2, 0]);

        var held = new BitWriter(); held.Write(1, 1); held.Write(0, 1); held.Write(0, 8); held.Write(5, 3);
        byte[] heldData = Fixture(3, held); Describe(heldData, 0, 0, 3, 0);
        motion = DiscoveryMotionDecoder.Decode(heldData);
        Equal("Held timing reads one key for all frames", 5f, motion.Poses[0, 2, 0]);

        byte[] entropyData = Entropy(false);
        motion = DiscoveryMotionDecoder.Decode(entropyData);
        Equal("Entropy keys retain their first key", 0f, motion.Poses[0, 0, 0]);
        Equal("Entropy timing linearly interpolates translation keys", 1f, motion.Poses[0, 1, 0]);
        Equal("Entropy timing reaches the middle stored key", 2f, motion.Poses[0, 2, 0]);
        Equal("Entropy timing interpolates the next segment", 3f, motion.Poses[0, 3, 0]);
        Equal("Entropy timing holds the final key safely", 4f, motion.Poses[0, 5, 0]);

        motion = DiscoveryMotionDecoder.Decode(Entropy(true));
        Near("Rotation keys use quaternion half-angle coordinates", Math.Sin(1), motion.Poses[0, 2, 3], 1e-7);
        Near("Rotation interpolation uses SLERP before writing the pose", Math.Sin(0.5), motion.Poses[0, 1, 3], 1e-7);
        Near("Quaternion scalar interpolation follows the same shortest path", Math.Cos(1.5), motion.Poses[0, 3, 6], 1e-7);

        byte[] malformed = (byte[])constant.Clone(); SetU32(malformed, 0x38, 1);
        Fails("Additional channel sections fail clearly", malformed);
        malformed = (byte[])constant.Clone(); SetU32(malformed, 0x60, 1);
        Fails("Unknown scale sections are never silently dropped", malformed);
        malformed = (byte[])constant.Clone(); SetU32(malformed, 0x24, 2);
        Fails("Inconsistent bone counts are rejected", malformed);
        malformed = (byte[])constant.Clone(); SetU32(malformed, 0x78, 5); SetU32(malformed, 0x7C, 5);
        Fails("Incorrect descriptor counts are rejected", malformed);
        malformed = (byte[])constant.Clone(); SetU32(malformed, 0x44, 0);
        Fails("Zero-frame clips are rejected", malformed);
        malformed = (byte[])constant.Clone(); SetU32(malformed, 0x44, 100001);
        Fails("Oversized frame counts are rejected before allocation", malformed);
        malformed = (byte[])constant.Clone(); SetFloat(malformed, 0x0C, float.NaN);
        Fails("Invalid frame rates are rejected", malformed);
        malformed = (byte[])constant.Clone(); Describe(malformed, 0, 0, 33, 0);
        Fails("Oversized bit widths are rejected", malformed);
        malformed = (byte[])constant.Clone(); SetFloat(malformed, Descriptor(0) + 4, float.PositiveInfinity);
        Fails("Non-finite channel values are rejected", malformed);
        malformed = new byte[0x9F]; Fails("Truncated headers are rejected", malformed);
        malformed = new byte[denseData.Length - 1]; Array.Copy(denseData, malformed, malformed.Length);
        Fails("Truncated packed streams are rejected", malformed);
        var zero = new BitWriter(); zero.Write(0, 1); zero.Write(1, 1); zero.Write(0, 16);
        malformed = Fixture(3, zero); Describe(malformed, 0, 0, 3, 0);
        Fails("Zero-length timing runs cannot loop indefinitely", malformed);

        if (args.Length == 2) CompareActual(args[0], args[1]);
        if (args.Length == 3)
        {
            CompareActual(args[0], args[1]);
            DecodedDiscoveryMotion added = DiscoveryMotionDecoder.Decode(File.ReadAllBytes(args[2]));
            Equal("Undisputed 2 decodes all actual 281 frames", 281, added.Frames);
            Equal("Undisputed 2 decodes all actual 26 body bones", 26, added.Ids.Length);
            Equal("Undisputed 2 retains actual 60-fps timing", 60f, added.Fps);
            bool moves = false;
            for (int bone = 0; bone < added.Ids.Length; bone++)
                for (int axis = 0; axis < 7; axis++)
                    if (Math.Abs(added.Poses[bone, 0, axis] - added.Poses[bone, added.Frames - 1, axis]) > 0.001) moves = true;
            Equal("Undisputed 2 is decoded motion, not a stationary replacement", true, moves);
        }
        Console.WriteLine("PASS " + passed + " Discovery motion checks");
    }

    private static void CompareActual(string clip, string expected)
    {
        DecodedDiscoveryMotion motion = DiscoveryMotionDecoder.Decode(File.ReadAllBytes(clip));
        using (var reader = new BinaryReader(File.OpenRead(expected)))
        {
            int bones = reader.ReadInt32(), frames = reader.ReadInt32();
            Equal("Actual clip bone count matches the original decoded NPZ", bones, motion.Ids.Length);
            Equal("Actual clip frame count matches the original decoded NPZ", frames, motion.Frames);
            bool hashes = true;
            for (int bone = 0; bone < bones; bone++) hashes &= reader.ReadUInt64() == motion.Ids[bone];
            Equal("Every actual bone hash matches the original decoded NPZ", true, hashes);
            double translationError = 0, rotationError = 0;
            for (int bone = 0; bone < bones; bone++)
                for (int frame = 0; frame < frames; frame++)
                    for (int axis = 0; axis < 7; axis++)
                    {
                        double error = Math.Abs(reader.ReadSingle() - motion.Poses[bone, frame, axis]);
                        if (axis < 3) translationError = Math.Max(translationError, error);
                        else rotationError = Math.Max(rotationError, error);
                    }
            Equal("Every actual translation matches the original decoded NPZ exactly", 0d, translationError);
            Near("Every actual quaternion matches the original decoded NPZ", 0, rotationError, 0.000001);
            Console.WriteLine("Actual NPZ maximum quaternion error: " + rotationError.ToString("G9"));
        }
    }

    private static byte[] Entropy(bool rotation)
    {
        var writer = new BitWriter(); writer.Write(0, 1); writer.Write(0, 1); writer.Write(3, 8);
        writer.Write(4, 4); // mode 3, six-frame quantum: split at frames 2 and 4.
        writer.Write(0, 3); writer.Write(2, 3); writer.Write(4, 3);
        byte[] data = Fixture(6, writer);
        Describe(data, rotation ? 3 : 0, rotation ? -1 : 0, 3, 0);
        return data;
    }

    private static byte[] Fixture(int frames, BitWriter writer)
    {
        byte[] packed = writer == null ? new byte[0] : writer.Bytes();
        byte[] data = new byte[0xA0 + 8 + 48 + packed.Length];
        SetU32(data, 0x20, 1); SetU32(data, 0x24, 1); SetU32(data, 0x40, 24);
        SetU32(data, 0x44, (uint)frames); SetFloat(data, 0x0C, 60);
        SetU32(data, 0x78, 6); SetU32(data, 0x7C, 6);
        SetU32(data, 0x90, (uint)packed.Length / 8); SetU32(data, 0x94, (uint)packed.Length / 8);
        Array.Copy(BitConverter.GetBytes(0xFEDCBA9876543210UL), 0, data, 0xA0, 8);
        Array.Copy(packed, 0, data, 0xA0 + 8 + 48, packed.Length);
        return data;
    }
    private static int Descriptor(int index) { return 0xA8 + index * 8; }
    private static void Describe(byte[] bytes, int channel, int exponent, int width, float basis)
    {
        bytes[Descriptor(channel)] = unchecked((byte)(sbyte)exponent);
        bytes[Descriptor(channel) + 1] = (byte)width;
        SetFloat(bytes, Descriptor(channel) + 4, basis);
    }
    private static void SetU32(byte[] bytes, int offset, uint value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static void SetFloat(byte[] bytes, int offset, float value) { Array.Copy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
    private static void Equal<T>(string label, T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
    private static void Near(string label, double expected, double actual, double tolerance)
    {
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance) throw new Exception(label + ": expected " + expected + ", got " + actual);
        passed++; Console.WriteLine("PASS " + label);
    }
    private static void Fails(string label, byte[] data)
    {
        try { DiscoveryMotionDecoder.Decode(data); }
        catch (InvalidDataException) { passed++; Console.WriteLine("PASS " + label); return; }
        throw new Exception(label + ": invalid motion was accepted");
    }
    private sealed class BitWriter
    {
        private readonly List<bool> bits = new List<bool>();
        internal void Write(uint value, int width) { for (int index = 0; index < width; index++) bits.Add(((value >> index) & 1) != 0); }
        internal byte[] Bytes()
        {
            var bytes = new byte[((bits.Count + 63) / 64) * 8];
            for (int bit = 0; bit < bits.Count; bit++) if (bits[bit]) bytes[bit / 8] |= (byte)(1 << (bit % 8));
            return bytes;
        }
    }
}
