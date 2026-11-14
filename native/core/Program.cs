using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.7.2")]

namespace TwinSync.Native
{
    internal static class Program
    {
        internal static int ResolvePort(string[] args)
        {
            var value = Environment.GetEnvironmentVariable("TWINSYNC_PORT");
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index] == "--port") value = args[index + 1];
            }
            int portValue;
            return int.TryParse(value, out portValue) ? portValue : 55220;
        }

        internal static bool OpensPanel(string[] args) { return !Array.Exists(args, arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase)); }
        internal static string ResolveHome(string[] args, string appDir)
        {
            var value = Environment.GetEnvironmentVariable("TWINSYNC_HOME");
            for (var i = 0; i < args.Length - 1; i++) if (args[i] == "--home") value = Path.IsPathRooted(args[i + 1]) ? args[i + 1] : Path.Combine(appDir, args[i + 1]);
            return string.IsNullOrEmpty(value) ? Path.Combine(appDir, "data") : Path.GetFullPath(value);
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var port = ResolvePort(args);
            bool created;
            using (var mutex = new Mutex(true, "TwinSync.Native." + port, out created))
            {
                if (!created)
                {
                    if (OpensPanel(args)) { try { Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port) { UseShellExecute = true }); } catch { } }
                    return;
                }
                try
                {
                    using (var context = new TrayContext(args)) Application.Run(context);
                }
                catch (Exception error)
                {
                    MessageBox.Show(error.Message, "TwinSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        private readonly CoreService core;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly Icon icon;
        private readonly string appDir;
        private readonly int port;

        public TrayContext(string[] args)
        {
            appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            port = Program.ResolvePort(args);
            var home = Program.ResolveHome(args, appDir);
            core = new CoreService(appDir, home, port);
            core.Start();
            core.ReconcileStartup();
            icon = LoadIcon();
            menu = CreateTrayMenu(OpenPanel, OpenData, ExitAll);
            tray = new NotifyIcon
            {
                Text = "TwinSync · 同步服务",
                Icon = icon,
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += delegate { OpenPanel(); };
            if (Program.OpensPanel(args)) OpenPanel();
        }

        internal static ContextMenuStrip CreateTrayMenu(Action openPanel, Action openData, Action exit)
        {
            var menu = new ContextMenuStrip { Font = SystemFonts.MenuFont };
            menu.Items.Add("打开 TwinSync", null, delegate { openPanel(); });
            menu.Items.Add("打开数据目录", null, delegate { openData(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出同步服务", null, delegate { exit(); });
            return menu;
        }

        private Icon LoadIcon()
        {
            var file = Path.Combine(appDir, "TwinSync.ico");
            if (File.Exists(file))
            {
                try { return new Icon(file); } catch { }
            }
            return SystemIcons.Application;
        }

        private void OpenPanel()
        {
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(error.Message, "TwinSync", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void OpenData()
        {
            var data = core.Config.Home;
            if (!Directory.Exists(data)) Directory.CreateDirectory(data);
            try { ExplorerIntegration.Show(data); }
            catch (Exception error) { MessageBox.Show(error.Message, "TwinSync", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ExitAll()
        {
            try { core.Stop(); } catch { }
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { core.Stop(); } catch { }
                try { tray.Visible = false; tray.Dispose(); } catch { }
                try { menu.Dispose(); } catch { }
                try { icon.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
