using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TwinSync.Native
{
    internal static class TrayTests
    {
        private static int checks;
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }

        private static void CheckStartup()
        {
            var original = StartupRegistration.Read();
            var key = @"Software\TwinSync\Tests\Startup-" + Guid.NewGuid().ToString("N");
            var stage = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var oldDir = Path.Combine(stage, "old app"); var newDir = Path.Combine(stage, "new app");
            Directory.CreateDirectory(oldDir); Directory.CreateDirectory(newDir);
            var oldExe = Path.Combine(oldDir, "TwinSync.exe"); var newExe = Path.Combine(newDir, "TwinSync.exe");
            File.WriteAllText(oldExe, "Test fixture, never executed."); File.WriteAllText(newExe, "Test fixture, never executed.");
            try
            {
                StartupRegistration.SetEnabled(true, oldExe, Path.Combine(oldDir, "data"), 55221, key);
                var args = StartupRegistration.Arguments(StartupRegistration.Read(key));
                Check(args[0] == oldExe && !Program.OpensPanel(args), "Login command does not suppress the browser");
                Check(Program.ResolvePort(args) == 55221 && Program.ResolveHome(args, oldDir) == Path.Combine(oldDir, "data"), "Login command lost custom port/home");
                Check(StartupRegistration.Arguments("launcher " + StartupRegistration.Quote("Unicode 中文 \\\"quoted\\\" trailing\\"))[1] == "Unicode 中文 \\\"quoted\\\" trailing\\", "Command line quoting failed to round trip");
                StartupRegistration.Retarget(oldDir, newDir, key);
                args = StartupRegistration.Arguments(StartupRegistration.Read(key));
                Check(args[0] == newExe && Program.ResolveHome(args, newDir) == Path.Combine(newDir, "data"), "Moving installation did not retarget startup/home");
                StartupRegistration.RemoveForExecutable(oldExe, key);
                Check(StartupRegistration.Read(key) != null, "Old uninstall removed the newer installation's startup entry");
                StartupRegistration.SetEnabled(false, newExe, Path.Combine(newDir, "data"), 55221, key);
                Check(StartupRegistration.Read(key) == null, "Disabling startup retained the Run value");
                Check(Program.OpensPanel(new string[0]), "Normal startup no longer opens the panel");
                Check(StartupRegistration.Read() == original, "Registry regression changed the real startup setting");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(key, false); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Tray UI is not running in STA");
                var entry = typeof(Program).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
                Check(entry.IsDefined(typeof(STAThreadAttribute), false), "Application entry point is missing STAThread");
                Check(AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName == ".NETFramework,Version=v4.7.2", "Wrong runtime target framework");
                CheckStartup();
                var panelCalls = 0; var dataCalls = 0; var exitCalls = 0;
                using (var menu = TrayContext.CreateTrayMenu(delegate { panelCalls++; }, delegate { dataCalls++; }, delegate { exitCalls++; }))
                {
                    var handle = menu.Handle;
                    Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle), new IntPtr(-4)), "Tray menu is not PerMonitorV2 aware; Windows may stretch its bitmap");
                    var dpi = GetDpiForWindow(handle);
                    Check(menu.DeviceDpi == dpi, "WinForms and Windows disagree about menu DPI");
                    var names = menu.Items.OfType<ToolStripMenuItem>().Select(item => item.Text).ToArray();
                    Check(names.SequenceEqual(new[] { "打开 TwinSync", "打开数据目录", "退出同步服务" }), "Tray menu actions changed");
                    Check(menu.Items.Count == 4 && menu.Items[2] is ToolStripSeparator, "Tray menu separator missing");
                    Check(menu.Font.FontFamily.Name == SystemFonts.MenuFont.FontFamily.Name && Math.Abs(menu.Font.SizeInPoints - SystemFonts.MenuFont.SizeInPoints) < .01f, "Tray menu ignores the Windows menu font");
                    menu.Size = menu.GetPreferredSize(Size.Empty);
                    menu.PerformLayout();
                    foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
                    {
                        var text = TextRenderer.MeasureText(item.Text, item.Font);
                        Check(item.Height >= text.Height, "Tray menu text clipped vertically: " + item.Text);
                        Check(item.Width >= text.Width, "Tray menu text clipped horizontally: " + item.Text);
                    }
                    if (args.Length > 0)
                        using (var bitmap = new Bitmap(menu.Width, menu.Height))
                        { menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[0], ImageFormat.Png); }
                    menu.Items[0].PerformClick(); menu.Items[1].PerformClick(); menu.Items[3].PerformClick();
                    Check(panelCalls == 1 && dataCalls == 1 && exitCalls == 1, "Tray menu actions were not dispatched correctly");
                    Console.WriteLine("Tray DPI regressions passed: " + checks + " checks. Native window: PerMonitorV2; DPI=" + dpi + "; font=" + menu.Font.Name + " " + menu.Font.SizeInPoints + "pt.");
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
