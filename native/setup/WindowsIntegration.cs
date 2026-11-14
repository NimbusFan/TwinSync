using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TwinSync.Setup
{
    internal sealed class WindowsIntegration : IInstallIntegration
    {
        private const string ProductKey = @"Software\TwinSync";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TwinSync";
        private static string StartShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TwinSync.lnk"); } }
        private static string DesktopShortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "TwinSync.lnk"); } }

        internal static string FindExistingDirectory()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(ProductKey))
            {
                var value = key == null ? null : key.GetValue("InstallDirectory") as string;
                if (!string.IsNullOrEmpty(value) && InstallEngine.Recognized(value) && File.Exists(Path.Combine(value, "TwinSync.exe"))) return value;
            }
            return InstallEngine.Recognized(InstallEngine.DefaultDirectory) && File.Exists(Path.Combine(InstallEngine.DefaultDirectory, "TwinSync.exe")) ? InstallEngine.DefaultDirectory : null;
        }

        public void Register(string directory, bool desktopShortcut)
        {
            var previous = FindExistingDirectory();
            Shortcut(StartShortcut, directory);
            if (desktopShortcut) Shortcut(DesktopShortcut, directory);
            else
            {
                RemoveShortcut(DesktopShortcut, directory);
                if (!string.IsNullOrEmpty(previous)) RemoveShortcut(DesktopShortcut, previous);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(ProductKey)) key.SetValue("InstallDirectory", directory);
            TwinSync.Native.StartupRegistration.Retarget(previous, directory);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "TwinSync");
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "TwinSync");
                key.SetValue("InstallLocation", directory);
                key.SetValue("DisplayIcon", Path.Combine(directory, "TwinSync.exe"));
                key.SetValue("UninstallString", "\"" + Path.Combine(directory, "TwinSyncUninstall.exe") + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public void Unregister(string directory)
        {
            RemoveShortcut(StartShortcut, directory);
            RemoveShortcut(DesktopShortcut, directory);
            TwinSync.Native.StartupRegistration.RemoveForExecutable(Path.Combine(directory, "TwinSync.exe"));
            using (var key = Registry.CurrentUser.OpenSubKey(ProductKey))
                if (key == null || !string.Equals(key.GetValue("InstallDirectory") as string, directory, StringComparison.OrdinalIgnoreCase)) return;
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(ProductKey, false);
        }

        private static object Invoke(object target, string member, BindingFlags flags, params object[] arguments)
        {
            return target.GetType().InvokeMember(member, flags, null, target, arguments);
        }

        private static void Shortcut(string path, string directory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object shortcut = null;
            try
            {
                shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, path);
                Invoke(shortcut, "TargetPath", BindingFlags.SetProperty, Path.Combine(directory, "TwinSync.exe"));
                Invoke(shortcut, "WorkingDirectory", BindingFlags.SetProperty, directory);
                Invoke(shortcut, "IconLocation", BindingFlags.SetProperty, Path.Combine(directory, "TwinSync.ico"));
                Invoke(shortcut, "Description", BindingFlags.SetProperty, "TwinSync");
                Invoke(shortcut, "Save", BindingFlags.InvokeMethod);
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }

        private static void RemoveShortcut(string path, string directory)
        {
            if (!File.Exists(path)) return;
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object shortcut = null;
            try
            {
                shortcut = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, path);
                var target = Invoke(shortcut, "TargetPath", BindingFlags.GetProperty) as string;
                if (string.Equals(target, Path.Combine(directory, "TwinSync.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        }

        internal static void Launch(string directory)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(directory, "TwinSync.exe")) { WorkingDirectory = directory, UseShellExecute = true });
        }
    }
}
