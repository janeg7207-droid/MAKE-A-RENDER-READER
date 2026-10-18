using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GorillaStudio.NameFolders
{
    internal sealed class FlatBufferFile
    {
        private readonly byte[] data;
        public int Root { get; private set; }
        public FlatBufferFile(byte[] bytes, string identifier)
        {
            data = bytes ?? throw new ArgumentNullException("bytes");
            Need(0, 8);
            if (identifier.Length != 4 || Encoding.ASCII.GetString(data, 4, 4) != identifier)
                throw Invalid("Unexpected file identifier.");
            uint root = U32(0);
            if (root < 8 || root > int.MaxValue) throw Invalid("Invalid root.");
            Root = (int)root;
            FieldCount(Root);
        }

        public int FieldCount(int table)
        {
            int vtable = VTable(table);
            ushort length = U16(vtable);
            if (length < 4 || (length & 1) != 0) throw Invalid("Invalid vtable length.");
            Need(vtable, length);
            ushort size = U16(vtable + 2);
            if (size < 4) throw Invalid("Invalid object size.");
            Need(table, size);
            return (length - 4) / 2;
        }

        public int Field(int table, int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException("index");
            int count = FieldCount(table);
            if (index >= count) return 0;
            int vtable = VTable(table);
            ushort offset = U16(vtable + 4 + 2 * index);
            if (offset == 0) return 0;
            ushort size = U16(vtable + 2);
            if (offset < 4 || offset >= size) throw Invalid("Invalid field offset.");
            return checked(table + offset);
        }

        public IEnumerable<int> Tables(int table, int field)
        {
            int location = Field(table, field);
            if (location == 0) yield break;
            uint displacement = U32(location);
            long vector = (long)location + displacement;
            if (displacement < 4 || vector > int.MaxValue) throw Invalid("Invalid vector offset.");
            int position = (int)vector;
            uint count = U32(position);
            if (count > (data.Length - (long)position - 4) / 4) throw Invalid("Truncated table vector.");
            for (uint i = 0; i < count; i++)
            {
                int element = checked(position + 4 + 4 * (int)i);
                uint offset = U32(element);
                long target = (long)element + offset;
                if (offset < 4 || target > int.MaxValue) throw Invalid("Invalid table vector element.");
                int child = (int)target;
                FieldCount(child);
                yield return child;
            }
        }

        private int VTable(int table)
        {
            Need(table, 4);
            long address = (long)table - I32(table);
            if (address < 8 || address > int.MaxValue) throw Invalid("Invalid signed vtable offset.");
            Need((int)address, 4);
            return (int)address;
        }

        public ushort U16(int offset) { Need(offset, 2); return (ushort)(data[offset] | data[offset + 1] << 8); }
        public uint U32(int offset)
        {
            Need(offset, 4);
            return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
        }
        public int I32(int offset) { return unchecked((int)U32(offset)); }
        public ulong U64(int offset) { return U32(offset) | (ulong)U32(checked(offset + 4)) << 32; }
        public byte U8(int offset) { Need(offset, 1); return data[offset]; }
        private void Need(int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > data.Length - (long)count) throw Invalid("Data is truncated or contains an invalid offset.");
        }
        private static InvalidDataException Invalid(string message) { return new InvalidDataException("Invalid Discovery JSFB: " + message); }
    }
}
