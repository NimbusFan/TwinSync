using System;
using System.IO;
using System.Runtime.InteropServices;

namespace TwinSync.Setup
{
    // Windows' current folder dialog, with an address bar, search and New folder.
    internal static class FolderPicker
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        internal static IFileDialog Create(string initial)
        {
            var dialog = (IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
            try
            {
                uint options; dialog.GetOptions(out options);
                dialog.SetOptions(options | 0x20 | 0x40 | 0x8 | 0x2000000); // PICKFOLDERS, FORCEFILESYSTEM, NOCHANGEDIR, DONTADDTORECENT
                dialog.SetTitle("选择 TwinSync 安装文件夹");
                dialog.SetOkButtonLabel("选择文件夹");
                string folder = null;
                try
                {
                    folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(initial.Trim().Trim('"')));
                    while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
                }
                catch (ArgumentException) { } catch (NotSupportedException) { }
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    var id = typeof(IShellItem).GUID; IShellItem item;
                    SHCreateItemFromParsingName(folder, IntPtr.Zero, ref id, out item);
                    try { dialog.SetFolder(item); } finally { Marshal.FinalReleaseComObject(item); }
                }
                return dialog;
            }
            catch { Marshal.FinalReleaseComObject(dialog); throw; }
        }

        internal static string Select(IntPtr owner, string initial)
        {
            var dialog = Create(initial);
            try
            {
                var result = dialog.Show(owner);
                if (result == unchecked((int)0x800704C7)) return null;
                Marshal.ThrowExceptionForHR(result);
                IShellItem item; dialog.GetResult(out item);
                try { string path; item.GetDisplayName(0x80058000, out path); return path; }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            finally { Marshal.FinalReleaseComObject(dialog); }
        }

        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr value);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem item, uint hint, out int order);
        }
    }
}
