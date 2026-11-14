using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.7.2")]

namespace TwinSync.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
#if UNINSTALL
            RunUninstall(args);
#else
            if (args.Length > 0 && args[0] == "/uninstall") RunUninstall(args);
            else Application.Run(new InstallerForm());
#endif
        }

        private static void RunUninstall(string[] args)
        {
            try
            {
                var worker = args.Length >= 3 && args[0] == "/worker";
                var directory = worker ? args[1] : AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                if (!worker)
                {
                    if (!File.Exists(Path.Combine(directory, InstallEngine.ManifestName))) throw new Exception("没有找到有效的 TwinSync 安装记录。");
                    if (MessageBox.Show("卸载 TwinSync？配置、历史版本和同步文件将保留。", "TwinSync", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    InstallEngine.EnsureNotRunning(directory);
                    var temp = Path.Combine(Path.GetTempPath(), "TwinSyncUninstall-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    var copy = Path.Combine(temp, "TwinSyncUninstall.exe");
                    File.Copy(Assembly.GetExecutingAssembly().Location, copy);
                    Process.Start(new ProcessStartInfo(copy, "/worker \"" + directory + "\" " + Process.GetCurrentProcess().Id) { UseShellExecute = false, CreateNoWindow = true });
                    return;
                }
                int parentId;
                if (int.TryParse(args[2], out parentId)) { try { using (var parent = Process.GetProcessById(parentId)) parent.WaitForExit(10000); } catch (ArgumentException) { } }
                InstallEngine.Uninstall(directory, new WindowsIntegration());
                MessageBox.Show("TwinSync 已卸载，原有数据已保留。", "TwinSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error) { MessageBox.Show(error.Message, "TwinSync", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
