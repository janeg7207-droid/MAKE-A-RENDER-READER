using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GorillaStudio.NameFolders
{
    // The common item dialog keeps Explorer's address bar, search, recent locations and sidebar.
    // The interfaces below include every preceding COM method so their vtable slots stay correct.
    internal static class WindowsFolderPicker
    {
        private const int Cancelled = unchecked((int)0x800704C7);

        internal static string Pick(IWin32Window owner, string title, string initialPath)
        {
            IFileOpenDialog dialog = null;
            IShellItem initialFolder = null;
            IShellItem result = null;
            try
            {
                Type dialogType = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), true);
                dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType);
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | 0x20u | 0x40u | 0x800u | 0x8u | 0x02000000u);
                dialog.SetTitle(title ?? "Choose a folder");
                dialog.SetOkButtonLabel("Use folder");
                string folder = FolderLocationDialog.NormalizeExistingDirectory(initialPath);
                if (folder != null)
                {
                    Guid shellItemId = typeof(IShellItem).GUID;
                    int createResult = SHCreateItemFromParsingName(folder, IntPtr.Zero, ref shellItemId, out initialFolder);
                    if (createResult >= 0 && initialFolder != null) dialog.SetFolder(initialFolder);
                }

                int showResult = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (showResult == Cancelled) return null;
                Marshal.ThrowExceptionForHR(showResult);
                dialog.GetResult(out result);
                IntPtr path = IntPtr.Zero;
                try
                {
                    result.GetDisplayName(0x80058000u, out path); // SIGDN_FILESYSPATH
                    return path == IntPtr.Zero ? null : Marshal.PtrToStringUni(path);
                }
                finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
            }
            finally
            {
                Release(result);
                Release(initialFolder);
                Release(dialog);
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr bindContext, ref Guid interfaceId, out IShellItem item);

        [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr filterSpecifications);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, uint placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IntPtr items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint displayName, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
