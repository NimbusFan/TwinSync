using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TwinSync.Native
{
    internal static class StartupRegistration
    {
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "TwinSync";

        internal static string Quote(string value)
        {
            var result = new StringBuilder("\""); var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; result.Append(c);
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
        internal static string Command(string executable, string home, int port)
        {
            var appDir = Path.GetDirectoryName(Path.GetFullPath(executable)).TrimEnd('\\') + "\\";
            var fullHome = Path.GetFullPath(home);
            var homeArgument = fullHome.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) ? fullHome.Substring(appDir.Length) : fullHome;
            return Quote(Path.GetFullPath(executable)) + " --background --home " + Quote(homeArgument) + " --port " + port;
        }
        internal static void SetEnabled(bool enabled, string executable, string home, int port, string keyPath = RunKey)
        {
            if (!enabled) { RemoveForExecutable(executable, keyPath); return; }
            if (!File.Exists(executable)) throw new Exception("找不到 TwinSync 程序，无法设置开机启动。");
            var command = Command(executable, home, port);
            if (command.Length > 260) throw new Exception("安装或数据目录过长，Windows 启动项无法保存；请选择较短的目录。");
            if (string.Equals(Read(keyPath), command, StringComparison.Ordinal)) return;
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        internal static string Read(string keyPath = RunKey)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath)) return key == null ? null : key.GetValue(ValueName) as string;
        }
        internal static string[] Arguments(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return new string[0];
            int count; var argv = CommandLineToArgvW(command, out count);
            if (argv == IntPtr.Zero) return new string[0];
            try { var args = new string[count]; for (var i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)); return args; }
            finally { LocalFree(argv); }
        }
        internal static void RemoveForExecutable(string executable, string keyPath = RunKey)
        {
            var args = Arguments(Read(keyPath));
            if (args.Length == 0 || !string.Equals(args[0], executable, StringComparison.OrdinalIgnoreCase)) return;
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)) if (key != null) key.DeleteValue(ValueName, false);
        }
        internal static void Retarget(string previous, string directory, string keyPath = RunKey)
        {
            if (string.IsNullOrEmpty(previous)) return;
            var args = Arguments(Read(keyPath));
            if (args.Length == 0 || !string.Equals(args[0], Path.Combine(previous, "TwinSync.exe"), StringComparison.OrdinalIgnoreCase)) return;
            var home = Path.Combine(directory, "data"); var port = 55220;
            for (var i = 1; i < args.Length - 1; i++)
            {
                if (args[i] == "--port") int.TryParse(args[i + 1], out port);
                if (args[i] == "--home")
                {
                    var boundary = Path.GetFullPath(previous).TrimEnd('\\') + "\\";
                    var oldHome = Path.GetFullPath(Path.IsPathRooted(args[i + 1]) ? args[i + 1] : Path.Combine(previous, args[i + 1]));
                    home = oldHome.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ? Path.Combine(directory, oldHome.Substring(boundary.Length)) : oldHome;
                }
            }
            if (port < 1 || port > 65535) port = 55220;
            SetEnabled(true, Path.Combine(directory, "TwinSync.exe"), home, port, keyPath);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
