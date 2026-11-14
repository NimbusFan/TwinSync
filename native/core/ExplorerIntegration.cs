using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TwinSync.Native
{
    internal static class ExplorerIntegration
    {
        internal static string ExistingPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || (value.Length >= 2 && value[1] == ':' && (value.Length < 3 || (value[2] != '\\' && value[2] != '/')))) throw new Exception("请选择完整的文件或文件夹路径。");
            var path = Path.GetFullPath(value);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new Exception("文件或文件夹已不存在，请刷新列表后重试。");
            return path;
        }

        internal static void Show(string value)
        {
            var focus = FocusRequest.Capture();
            var path = ExistingPath(value);
            Exception failure = null;
            var thread = new Thread(delegate()
            {
                try { ShowOnSta(path, focus); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "TwinSync Explorer" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(5000)) throw new TimeoutException("资源管理器响应超时，请稍后重试。");
            if (failure != null) throw new Exception("无法打开资源管理器：" + failure.Message, failure);
        }

        private static void ShowOnSta(string path, FocusRequest focus)
        {
            var initialized = CoInitializeEx(IntPtr.Zero, 2); // COINIT_APARTMENTTHREADED
            Marshal.ThrowExceptionForHR(initialized);
            IntPtr pidl = IntPtr.Zero;
            try
            {
                var directory = Directory.Exists(path);
                uint attributes;
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out pidl, 0x20000000, out attributes));
                if (directory && (attributes & 0x20000000) == 0) throw new Exception("文件夹位置已发生变化，请刷新后重试。");
                focus.AllowShellActivation();
                if (!directory)
                {
                    // With no child array, the absolute PIDL selects the file in its parent folder.
                    Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0));
                }
                else
                {
                    // Open the directory itself, rather than selecting it in its parent directory.
                    var info = new ShellExecuteInfo
                    {
                        Size = Marshal.SizeOf(typeof(ShellExecuteInfo)),
                        Mask = 0x4 | 0x100 | 0x400, // IDLIST, NOASYNC, FLAG_NO_UI
                        Verb = "open", IdList = pidl, Show = 1
                    };
                    if (!ShellExecuteEx(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); CoUninitialize(); }
        }

        private struct FocusRequest
        {
            private IntPtr window;
            private uint lastInput;
            private Stopwatch elapsed;
            internal static FocusRequest Capture()
            {
                var input = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
                var window = GetForegroundWindow();
                return new FocusRequest { window = GetLastInputInfo(ref input) ? window : IntPtr.Zero, lastInput = input.Time, elapsed = Stopwatch.StartNew() };
            }
            internal void AllowShellActivation()
            {
                var current = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
                // Do not interrupt a later interaction or activate a delayed background request.
                if (window == IntPtr.Zero || elapsed.ElapsedMilliseconds > 1500 || GetForegroundWindow() != window || !GetLastInputInfo(ref current) || current.Time != lastInput) return;
                // A zero-action mouse event grants this explicit command foreground eligibility,
                // without moving the pointer or pressing/releasing any button or key.
                // This pattern is also used by Microsoft PowerToys (PR #14383).
                SendInput(1, new[] { new NativeInput() }, Marshal.SizeOf(typeof(NativeInput)));
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size, Time; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellExecuteInfo
        {
            public int Size; public uint Mask; public IntPtr Window;
            public string Verb, File, Parameters, Directory;
            public int Show; public IntPtr Instance, IdList;
            public string Class; public IntPtr ClassKey;
            public uint HotKey; public IntPtr Icon, Process;
        }
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, IntPtr context, out IntPtr pidl, uint mask, out uint attributes);
        [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr children, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo input);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] events, int size);
    }
}
