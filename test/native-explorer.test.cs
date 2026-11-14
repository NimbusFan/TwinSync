using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace TwinSync.Native
{
    internal static class ExplorerTests
    {
        private static int checks;
        private static readonly HashSet<long> originalWindows = new HashSet<long>();
        private static string root;
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static object Com(object value, string member, BindingFlags flags, params object[] args) { return value.GetType().InvokeMember(member, flags, null, value, args); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        private static void EachWindow(Action<object, IntPtr, string> action)
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); object windows = null;
            try
            {
                windows = Com(shell, "Windows", BindingFlags.InvokeMethod);
                var count = Convert.ToInt32(Com(windows, "Count", BindingFlags.GetProperty));
                for (var index = count - 1; index >= 0; index--)
                {
                    object window = null, document = null, folder = null, self = null;
                    try
                    {
                        window = Com(windows, "Item", BindingFlags.InvokeMethod, index);
                        var handle = new IntPtr(Convert.ToInt64(Com(window, "HWND", BindingFlags.GetProperty)));
                        document = Com(window, "Document", BindingFlags.GetProperty);
                        folder = Com(document, "Folder", BindingFlags.GetProperty);
                        self = Com(folder, "Self", BindingFlags.GetProperty);
                        action(window, handle, Convert.ToString(Com(self, "Path", BindingFlags.GetProperty)));
                    }
                    catch (COMException) { } catch (TargetInvocationException) { }
                    finally { Release(self); Release(folder); Release(document); Release(window); }
                }
            }
            finally { Release(windows); Release(shell); }
        }
        private static IntPtr WaitForFolder(string path, bool foreground)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                IntPtr found = IntPtr.Zero;
                EachWindow(delegate(object window, IntPtr handle, string folder)
                {
                    if (string.Equals(folder.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && (!foreground || (GetForegroundWindow() == handle && !IsIconic(handle)))) found = handle;
                });
                if (found != IntPtr.Zero) return found;
                Thread.Sleep(50);
            } while (timer.ElapsedMilliseconds < 5000);
            throw new Exception("Explorer folder was not " + (foreground ? "activated/restored" : "opened") + ": " + path);
        }
        private static string SelectedPath(IntPtr target)
        {
            string selected = null;
            EachWindow(delegate(object window, IntPtr handle, string folder)
            {
                if (target != handle) return;
                object document = null, items = null, item = null;
                try
                {
                    document = Com(window, "Document", BindingFlags.GetProperty);
                    items = Com(document, "SelectedItems", BindingFlags.InvokeMethod);
                    if (Convert.ToInt32(Com(items, "Count", BindingFlags.GetProperty)) != 1) return;
                    item = Com(items, "Item", BindingFlags.InvokeMethod, 0);
                    selected = Convert.ToString(Com(item, "Path", BindingFlags.GetProperty));
                }
                finally { Release(item); Release(items); Release(document); }
            });
            return selected;
        }
        [STAThread] private static int Main(string[] args)
        {
            var previous = GetForegroundWindow();
            try
            {
                root = Path.GetFullPath(args[0]);
                EachWindow(delegate(object window, IntPtr existingHandle, string folder) { originalWindows.Add(existingHandle.ToInt64()); });
                var directory = Path.Combine(root, "定位目录");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "报告, #1 %&.txt");
                File.WriteAllText(file, "Disposable Explorer integration fixture.");
                ExplorerIntegration.Show(file);
                var handle = WaitForFolder(directory, true);
                Check(!originalWindows.Contains(handle.ToInt64()), "Desktop test requires its own new Explorer window");
                Check(string.Equals(SelectedPath(handle), file, StringComparison.OrdinalIgnoreCase), "New window selected the wrong Unicode/punctuation filename");
                Check(previous != IntPtr.Zero && SetForegroundWindow(previous), "Cannot place the test window behind the previous application");
                ExplorerIntegration.Show(file);
                Check(WaitForFolder(directory, true) == handle, "Reveal did not activate/reuse the existing folder window");
                Check(string.Equals(SelectedPath(handle), file, StringComparison.OrdinalIgnoreCase), "Reused window lost the selected file");
                ShowWindowAsync(handle, 6);
                var timer = Stopwatch.StartNew();
                while (!IsIconic(handle) && timer.ElapsedMilliseconds < 2000) Thread.Sleep(20);
                Check(IsIconic(handle), "Could not minimize the test window");
                SetForegroundWindow(previous);
                ExplorerIntegration.Show(file);
                Check(WaitForFolder(directory, true) == handle, "Reveal did not restore/activate the minimized window");
                Check(string.Equals(SelectedPath(handle), file, StringComparison.OrdinalIgnoreCase), "Restored window selected the wrong file");
                var openDirectory = Path.Combine(root, "只打开文件夹");
                Directory.CreateDirectory(openDirectory);
                SetForegroundWindow(previous);
                ExplorerIntegration.Show(openDirectory);
                var folderHandle = WaitForFolder(openDirectory, true);
                Check(!originalWindows.Contains(folderHandle.ToInt64()), "Directory test unexpectedly reused a pre-existing user window");
                Console.WriteLine("Explorer desktop integration passed: " + checks + " checks (new window, existing window, minimized window, exact selection, folder itself).");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                if (root != null) EachWindow(delegate(object window, IntPtr handle, string folder)
                {
                    if (!originalWindows.Contains(handle.ToInt64()) && folder.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) Com(window, "Quit", BindingFlags.InvokeMethod);
                });
                if (previous != IntPtr.Zero) SetForegroundWindow(previous);
            }
        }
    }
}
