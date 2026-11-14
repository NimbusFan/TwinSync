using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

namespace TwinSync.Setup
{
    internal static class InstallerTests
    {
        private static int checks;
        private static string root;
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static void Reject(Action action, string message) { try { action(); } catch { checks++; return; } throw new Exception(message); }
        private static void Write(string file, string value) { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, value); }

        private sealed class Integration : IInstallIntegration
        {
            internal string Registered, Unregistered;
            internal bool Desktop, Fail;
            public void Register(string directory, bool desktop) { if (Fail) throw new IOException("Integration failure"); Registered = directory; Desktop = desktop; }
            public void Unregister(string directory) { Unregistered = directory; }
        }

        private static Stream Payload(string version, string extra = null)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                foreach (var name in new[] { "TwinSync.exe", "TwinSyncUninstall.exe", "public/index.html", "public/中文.css" }.Concat(extra == null ? new string[0] : new[] { extra }))
                    using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(version + ":" + name);
            }
            stream.Position = 0; return stream;
        }

        private static void Install(string directory, Integration integration, string previous = null, string version = "v1", string extra = null)
        {
            var last = 0;
            InstallEngine.Install(directory, true, previous, integration, delegate(int value, string text) { Check(value >= last && value <= 100, "Progress went backwards"); last = value; }, Payload(version, extra));
            Check(last == 100, "Install did not reach completion");
        }

        private static void EngineChecks()
        {
            var chosen = Path.Combine(root, "自选磁盘", "应用 TwinSync");
            var integration = new Integration();
            Check(InstallEngine.ValidateDirectory(chosen + "\\") == chosen, "Custom Unicode path was not normalized");
            foreach (var path in new[] { "", "relative\\TwinSync", "C:relative", "\\\\server\\share", Path.GetPathRoot(root) }) Reject(delegate { InstallEngine.ValidateDirectory(path); }, "Unsafe location accepted");
            var unrelated = Path.Combine(root, "unrelated"); Write(Path.Combine(unrelated, "keep.txt"), "keep");
            Reject(delegate { InstallEngine.ValidateDirectory(unrelated); }, "Existing unrelated directory accepted");
            Reject(delegate { InstallEngine.ValidateDirectory(Path.Combine(unrelated, "keep.txt")); }, "File accepted as a directory");
            Install(chosen, integration);
            Check(File.ReadAllText(Path.Combine(chosen, "public", "中文.css")) == "v1:public/中文.css", "Installed file missing or corrupt");
            Check(integration.Registered == chosen && integration.Desktop, "Custom location did not reach shortcut/registry integration");
            Check(InstallEngine.Recognized(chosen), "Install record missing");
            Write(Path.Combine(chosen, "data", "settings.json"), "private-settings");
            Write(Path.Combine(chosen, "data", "history", "file.txt"), "history");
            Reject(delegate { Install(Path.Combine(chosen, "data", "new-install"), integration, chosen); }, "Recursive data migration accepted");
            Check(!Directory.Exists(Path.Combine(chosen, "data", "new-install")), "Recursive migration wrote inside old data");
            Install(chosen, integration, chosen, "v2");
            Check(File.ReadAllText(Path.Combine(chosen, "TwinSync.exe")) == "v2:TwinSync.exe", "Upgrade did not replace app");
            Check(File.ReadAllText(Path.Combine(chosen, "data", "settings.json")) == "private-settings", "Upgrade overwrote settings");
            var moved = Path.Combine(root, "另一个位置", "TwinSync");
            Install(moved, integration, chosen, "v3");
            Check(File.ReadAllText(Path.Combine(moved, "data", "history", "file.txt")) == "history", "New location lost history");
            Check(File.ReadAllText(Path.Combine(chosen, "data", "settings.json")) == "private-settings", "Moving install deleted original settings");
            Write(Path.Combine(moved, "own-notes.txt"), "unrelated");
            Write(Path.Combine(moved, "public", "中文.css"), "user modified");
            InstallEngine.Uninstall(moved, integration);
            Check(!File.Exists(Path.Combine(moved, "TwinSync.exe")), "Uninstall retained unchanged app file");
            Check(File.Exists(Path.Combine(moved, "data", "settings.json")) && File.Exists(Path.Combine(moved, "data", "history", "file.txt")), "Uninstall deleted user data");
            Check(File.ReadAllText(Path.Combine(moved, "own-notes.txt")) == "unrelated" && File.ReadAllText(Path.Combine(moved, "public", "中文.css")) == "user modified", "Uninstall deleted unrelated/modified files");
            Check(integration.Unregistered == moved, "Uninstall used wrong integration path");
            Install(moved, integration);
            Check(File.Exists(Path.Combine(moved, "TwinSync.exe")) && File.Exists(Path.Combine(moved, "data", "settings.json")), "Reinstall with retained data failed");

            var retry = Path.Combine(root, "retry");
            var failure = new Integration { Fail = true };
            Reject(delegate { Install(retry, failure); }, "Failed integration not reported");
            Check(InstallEngine.Recognized(retry), "Interrupted install cannot be retried");
            failure.Fail = false; Install(retry, failure);
            Check(failure.Registered == retry, "Retry did not succeed");
            foreach (var relative in new[] { "../outside.txt", "data/settings.json", "DATA", "TWINSYNC-INSTALL.XML", "public/../../outside.txt", "data./settings.json", "data /settings.json" })
            {
                var invalid = Path.Combine(root, "bad-" + Guid.NewGuid().ToString("N"));
                Reject(delegate { Install(invalid, integration, null, "bad", relative); }, "Unsafe payload accepted: " + relative);
                Check(!Directory.Exists(invalid), "Unsafe payload partially installed");
            }
            var tampered = new XmlDocument(); var manifest = Path.Combine(chosen, InstallEngine.ManifestName); tampered.Load(manifest);
            var file = tampered.CreateElement("file"); file.SetAttribute("path", "../unrelated/keep.txt"); file.SetAttribute("hash", "ignored"); tampered.DocumentElement.AppendChild(file); tampered.Save(manifest);
            Reject(delegate { InstallEngine.Uninstall(chosen, integration); }, "Unsafe uninstall manifest accepted");
            Check(File.Exists(Path.Combine(chosen, "TwinSync.exe")) && File.ReadAllText(Path.Combine(unrelated, "keep.txt")) == "keep", "Unsafe uninstall changed files before validation");
        }

        private static void ShortcutChecks()
        {
            var location = Path.Combine(root, "含空格的 应用路径");
            Directory.CreateDirectory(location);
            var link = Path.Combine(root, "shortcuts", "TwinSync.lnk");
            typeof(WindowsIntegration).GetMethod("Shortcut", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { link, location });
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
            try
            {
                var target = (string)shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                var working = (string)shortcut.GetType().InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, shortcut, null);
                Check(string.Equals(target, Path.Combine(location, "TwinSync.exe"), StringComparison.OrdinalIgnoreCase), "Shortcut target ignored custom path");
                Check(string.Equals(working, location, StringComparison.OrdinalIgnoreCase), "Shortcut working directory ignored custom path");
            }
            finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
            var remove = typeof(WindowsIntegration).GetMethod("RemoveShortcut", BindingFlags.NonPublic | BindingFlags.Static);
            remove.Invoke(null, new object[] { link, root });
            Check(File.Exists(link), "Uninstall removed another installation's shortcut");
            remove.Invoke(null, new object[] { link, location });
            Check(!File.Exists(link), "Uninstall retained own shortcut");
        }

        private static void ControlEvent(Control control, string method, EventArgs args)
        {
            control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { args });
        }

        private static void RepaintChecks(InstallerForm form)
        {
            using (var surface = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                using (var graphics = Graphics.FromImage(surface)) form.PaintSurface(graphics);
                foreach (var control in form.Controls.Cast<Control>().Where(item => item is RoundButton || item is InstallOption))
                {
                    foreach (var state in new[] { "normal", "hover", "pressed", "released", "disabled", "restored" })
                    {
                        if (state == "hover") ControlEvent(control, "OnMouseEnter", EventArgs.Empty);
                        if (state == "pressed") ControlEvent(control, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, control.Width / 2, control.Height / 2, 0));
                        if (state == "released")
                        {
                            control.Enabled = false;
                            ControlEvent(control, "OnMouseUp", new MouseEventArgs(MouseButtons.None, 0, -1, -1, 0));
                            control.Capture = false;
                            control.Enabled = true;
                        }
                        if (state == "disabled") control.Enabled = false;
                        if (state == "restored") { control.Enabled = true; ControlEvent(control, "OnMouseLeave", EventArgs.Empty); }
                        var option = control as CheckBox;
                        if (option != null) option.Checked = state != "pressed" && state != "released";
                        // WM_PAINT may start with an uninitialized double buffer. Painting only the
                        // foreground must still replace every corner and the area beside a checkbox.
                        foreach (var dirty in new[] { Color.Black, Color.White })
                        using (var bitmap = new Bitmap(control.Width, control.Height))
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            graphics.Clear(dirty);
                            ControlEvent(control, "OnPaint", new PaintEventArgs(graphics, control.ClientRectangle));
                            var samples = new[] { new Point(0, 0), new Point(control.Width - 1, 0), new Point(0, control.Height - 1), new Point(control.Width - 1, control.Height - 1) };
                            if (option != null) samples = samples.Concat(new[] { new Point(control.Width - 10, control.Height / 2) }).ToArray();
                            var matches = samples.All(point =>
                            {
                                var expected = surface.GetPixel(control.Left + point.X, control.Top + point.Y);
                                var actual = bitmap.GetPixel(point.X, point.Y);
                                return actual.A == 255 && Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2;
                            });
                            Check(matches, "Control left a black/white rectangle or mismatched background: " + control.Text + ", " + state + ", " + dirty.Name);
                        }
                    }
                }
            }
        }

        private static void ShowOffscreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Control.CheckForIllegalCrossThreadCalls = true;
        }

        private static void ButtonChecks()
        {
            var integration = new Integration();
            var chosen = Path.Combine(root, "浏览选择", "TwinSync");
            var selected = chosen;
            var calls = 0;
            IntPtr owner = IntPtr.Zero; string initial = null;
            using (var form = new InstallerForm(null, integration, delegate(IntPtr window, string path)
            {
                calls++; owner = window; initial = path;
                if (selected == "error") throw new IOException("test folder picker error");
                return selected;
            }))
            {
                ShowOffscreen(form);
                var before = form.Destination.Text;
                form.BrowseButton.PerformClick();
                Check(calls == 1 && owner == form.Handle && initial == before, "Browse button did not open picker with correct owner/path");
                Check(form.Destination.Text == chosen, "Browse selection did not update installation path");
                selected = null; form.BrowseButton.PerformClick();
                Check(calls == 2 && form.Destination.Text == chosen, "Cancelling folder picker changed installation path");
                selected = "error"; form.BrowseButton.PerformClick();
                Check(form.Controls.OfType<Label>().Any(label => label.Text.Contains("test folder picker error")), "Folder picker error was not shown inline");
                Check(form.Destination.Text == chosen && form.BrowseButton.Enabled, "Folder picker error disabled browse or changed the path");
                form.Controls.OfType<Button>().Single(button => button.Text == "取消").PerformClick();
                Check(form.IsDisposed && integration.Registered == null && !Directory.Exists(chosen), "Cancel button did not close without installing");
            }
            using (var form = new InstallerForm(null, integration))
            {
                ShowOffscreen(form);
                var handled = (bool)typeof(Form).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { Keys.Escape });
                Check(handled && form.IsDisposed, "Escape key did not cancel installer");
            }
        }

        private static void UiChecks(string previews)
        {
            var picker = FolderPicker.Create(Path.Combine(root, "not-created", "TwinSync"));
            try
            {
                uint options; picker.GetOptions(out options);
                Check((options & 0x60) == 0x60, "Native dialog not restricted to filesystem folders");
                FolderPicker.IShellItem selected; picker.GetFolder(out selected);
                try { string selectedPath; selected.GetDisplayName(0x80058000, out selectedPath); Check(string.Equals(selectedPath, root, StringComparison.OrdinalIgnoreCase), "Folder picker did not start at nearest existing directory"); }
                finally { Marshal.FinalReleaseComObject(selected); }
            }
            finally { Marshal.FinalReleaseComObject(picker); }
            using (var form = new InstallerForm(null, new Integration()))
            {
                try
                {
                ShowOffscreen(form);
                var handle = form.Handle;
                Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle), new IntPtr(-4)), "Installer is not PerMonitorV2 DPI aware");
                Check(!form.Destination.ReadOnly && form.BrowseButton.Enabled, "Installation location cannot be changed");
                foreach (var factor in new[] { 1f, 1.5f, 2f })
                {
                    form.ApplyScale(factor);
                    Check(form.ClientSize == new Size((int)(740 * factor), (int)(480 * factor)), "DPI layout size incorrect");
                    foreach (Control control in form.Controls)
                    {
                        Check(form.ClientRectangle.Contains(control.Bounds), "Control extends beyond client area: " + control.Text);
                        Check(control.Font.Unit == GraphicsUnit.Pixel && control.Font.Size >= 14f * factor - .1f, "Font below minimum readable size: " + control.Text);
                        if (control is Button || control is CheckBox)
                        {
                            var measured = TextRenderer.MeasureText(control.Text, control.Font);
                            Check(measured.Height <= control.Height, "Text clipped vertically: " + control.Text);
                            Check(measured.Width + (control is CheckBox ? 34 * factor : 0) <= control.Width, "Text clipped horizontally: " + control.Text);
                        }
                    }
                    RepaintChecks(form);
                    if (!string.IsNullOrEmpty(previews))
                    {
                        Directory.CreateDirectory(previews);
                        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(previews, "installer-" + (int)(factor * 100) + ".png"), ImageFormat.Png); }
                    }
                }
                form.ApplyScale(2, new Size(1334, 664));
                Check(form.ClientSize.Width <= 1334 && form.ClientSize.Height <= 664, "Small-screen high DPI window does not fit");
                foreach (Control control in form.Controls) Check(form.ClientRectangle.Contains(control.Bounds), "Compact layout clips a control");
                var optionsInCompact = form.Controls.OfType<CheckBox>().ToArray();
                Check(!optionsInCompact[0].Bounds.IntersectsWith(optionsInCompact[1].Bounds), "Compact options overlap");
                if (!string.IsNullOrEmpty(previews)) using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(previews, "installer-compact.png"), ImageFormat.Png); }
                form.ApplyScale(1);
                var install = form.Controls.OfType<Button>().Single(button => button.Text == "安装");
                form.Destination.Text = "invalid";
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(install, new object[] { EventArgs.Empty });
                Check(form.Controls.OfType<Label>().Any(label => label.Text.Contains("完整路径")), "Invalid location not shown inline");
                form.Destination.Text = Path.Combine(root, "UI 安装");
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(install, new object[] { EventArgs.Empty });
                Check(!install.Enabled && !form.BrowseButton.Enabled, "Installer controls remain active during install");
                var timer = Stopwatch.StartNew();
                while ((install.Text != "完成" || !install.Enabled) && timer.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(10); }
                Check(install.Text == "完成" && install.Enabled, "Install button did not finish successfully: " + string.Join(" | ", form.Controls.OfType<Label>().Select(label => label.Text)));
                Check(File.Exists(Path.Combine(form.Destination.Text, "TwinSync.exe")), "UI installed to wrong location");
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TwinSyncPayload"))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    foreach (var entry in archive.Entries.Where(item => !item.FullName.EndsWith("/", StringComparison.Ordinal)))
                    {
                        var installed = Path.Combine(form.Destination.Text, entry.FullName.Replace('/', '\\'));
                        using (var contents = new MemoryStream())
                        using (var input = entry.Open())
                        { input.CopyTo(contents); Check(File.Exists(installed) && File.ReadAllBytes(installed).SequenceEqual(contents.ToArray()), "UI install changed payload file: " + entry.FullName); }
                    }
                Check(!form.BrowseButton.Enabled && form.Destination.ReadOnly, "Completed location still editable");
                if (!string.IsNullOrEmpty(previews)) using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(previews, "installer-complete.png"), ImageFormat.Png); }
                }
                catch (Exception error) { Console.Error.WriteLine("UI check failed: " + error); throw; }
            }
        }

        [STAThread] private static int Main(string[] args)
        {
            try
            {
                root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                EngineChecks(); ShortcutChecks(); ButtonChecks(); UiChecks(args.Length > 1 ? args[1] : null);
                Console.WriteLine("Installer regressions passed: " + checks + " checks (custom location, upgrade, migration, safe uninstall, retry, DPI 100/150/200%, control repaint states, browse/cancel, UI install)."); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
