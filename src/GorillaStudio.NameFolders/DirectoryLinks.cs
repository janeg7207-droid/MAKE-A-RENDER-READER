using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GorillaStudio.NameFolders
{
    internal static class DirectoryLinks
    {
        private const uint BackupSemantics = 0x02000000;
        private const uint OpenReparsePoint = 0x00200000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
            StringBuilder path, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode,
            byte[] input, int inputLength, IntPtr output, int outputLength,
            out int bytesReturned, IntPtr overlapped);

        public static string Resolve(string path)
        {
            using (SafeFileHandle handle = CreateFile(Path.GetFullPath(path), 0, 7,
                IntPtr.Zero, 3, BackupSemantics, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the character folder.");
                var buffer = new StringBuilder(32768);
                uint result = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (result == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve the character folder.");
                if (result >= buffer.Capacity) throw new IOException("The character folder path is too long.");
                string full = buffer.ToString();
                if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + full.Substring(8);
                if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full.Substring(4);
                return full;
            }
        }

        public static void EnsureJunction(string link, string target)
        {
            target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
            if (target.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Character folders and imported Discovery data must be on local Windows drives for compatibility links. Copy this folder from the network share to a local drive first.");
            if (Directory.Exists(link))
            {
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0
                    || !string.Equals(Resolve(link).TrimEnd('\\'), Resolve(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A different folder already exists at the compatibility link: " + link);
                return;
            }
            if (File.Exists(link)) throw new IOException("A file already exists at the compatibility link: " + link);
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            byte[] print = Encoding.Unicode.GetBytes(target);
            int pathLength = substitute.Length + print.Length + 4;
            if (pathLength + 16 > 16384) throw new IOException("The character folder path is too long for a Windows directory junction.");
            byte[] data = new byte[16 + pathLength];
            using (var stream = new MemoryStream(data))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0xA0000003u);
                writer.Write((ushort)(pathLength + 8));
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)substitute.Length);
                writer.Write((ushort)(substitute.Length + 2));
                writer.Write((ushort)print.Length);
                writer.Write(substitute);
                writer.Write((ushort)0);
                writer.Write(print);
                writer.Write((ushort)0);
            }
            Directory.CreateDirectory(link);
            bool created = false;
            try
            {
                using (SafeFileHandle handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero,
                    3, BackupSemantics | OpenReparsePoint, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    int returned;
                    if (!DeviceIoControl(handle, 0x000900A4, data, data.Length,
                        IntPtr.Zero, 0, out returned, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the compatibility link. Use a local NTFS drive.");
                    created = true;
                }
            }
            finally
            {
                // Never traverse or remove a character folder when link creation fails.
                if (!created && Directory.Exists(link)) Directory.Delete(link, false);
            }
        }
    }
}
