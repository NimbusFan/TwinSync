using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TwinSync.Setup
{
    internal sealed class InstallerForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(239, 244, 255);
        private static readonly Color Muted = Color.FromArgb(160, 177, 204);
        private static readonly Color Field = Color.FromArgb(18, 31, 55);
        internal readonly TextBox Destination = new TextBox();
        internal readonly RoundButton BrowseButton = new RoundButton();
        private readonly RoundButton install = new RoundButton { Primary = true };
        private readonly RoundButton cancel = new RoundButton();
        private readonly InstallOption desktop = new InstallOption();
        private readonly InstallOption launch = new InstallOption();
        private readonly Label hint = new Label();
        private readonly Label status = new Label();
        private readonly IInstallIntegration integration;
        private readonly Func<IntPtr, string, string> selectFolder;
        private readonly string previous;
        private float scale = 1;
        private float logoY, titleY, subtitleY, labelY, fieldY, fieldHeight, hintY, checkY, launchY, lineY, footerY, buttonHeight, progressY;
        private int layoutWidth = 740;
        private int progress;
        private bool busy, complete, layingOut;
        private string installedDirectory;
        private Font brandFont, bodyFont;

        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal InstallerForm() : this(WindowsIntegration.FindExistingDirectory(), new WindowsIntegration()) { }

        internal InstallerForm(string existingDirectory, IInstallIntegration installIntegration, Func<IntPtr, string, string> folderSelection = null)
        {
            previous = existingDirectory;
            integration = installIntegration;
            selectFolder = folderSelection ?? FolderPicker.Select;
            Text = "TwinSync 安装";
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(8, 17, 35);
            ForeColor = Ink;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            Destination.Text = existingDirectory ?? InstallEngine.DefaultDirectory;
            Destination.BorderStyle = BorderStyle.None;
            Destination.BackColor = Field;
            Destination.ForeColor = Ink;
            Destination.AccessibleName = "安装位置";
            Destination.TextChanged += delegate { if (!busy && !complete) { hint.ForeColor = Muted; UpdateHint(); } };
            BrowseButton.Text = "浏览…";
            BrowseButton.AccessibleName = "选择安装文件夹";
            BrowseButton.Click += Browse;
            desktop.Text = "创建桌面快捷方式";
            desktop.Checked = true;
            launch.Text = "完成后启动 TwinSync";
            launch.Checked = true;
            install.Text = "安装";
            install.Click += Install;
            cancel.Text = "取消";
            cancel.Click += delegate { Close(); };
            foreach (var label in new[] { hint, status }) { label.ForeColor = Muted; label.BackColor = Color.Transparent; label.UseMnemonic = false; }
            status.Text = "仅为当前用户安装";
            hint.Text = "可直接输入路径，或浏览选择其他文件夹。";
            Controls.AddRange(new Control[] { Destination, BrowseButton, desktop, launch, hint, status, cancel, install });
            Destination.TabIndex = 0; BrowseButton.TabIndex = 1; desktop.TabIndex = 2; launch.TabIndex = 3; install.TabIndex = 4; cancel.TabIndex = 5;
            AcceptButton = install;
            CancelButton = cancel;
            ApplyScale(1);
            ActiveControl = install;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            var rounded = 2;
            DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
            try { FitToWorkArea(GetDpiForWindow(Handle) / 96f); }
            catch (EntryPointNotFoundException) { using (var graphics = CreateGraphics()) FitToWorkArea(graphics.DpiX / 96f); }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x02E0)
            {
                var factor = (message.WParam.ToInt64() & 0xffff) / 96f;
                var bounds = (NativeRect)Marshal.PtrToStructure(message.LParam, typeof(NativeRect));
                base.WndProc(ref message);
                Location = new Point(bounds.Left, bounds.Top);
                FitToWorkArea(factor);
                return;
            }
            base.WndProc(ref message);
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

        // Real monitor changes and offscreen layout checks share this exact path.
        private void FitToWorkArea(float factor)
        {
            var work = Screen.FromHandle(Handle).WorkingArea;
            ApplyScale(factor, new Size(work.Width - (Width - ClientSize.Width) - 16, work.Height - (Height - ClientSize.Height) - 16));
        }

        internal void ApplyScale(float factor, Size maximumClient = default(Size))
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                scale = Math.Max(1, factor);
                layoutWidth = maximumClient.IsEmpty ? 740 : Math.Max(540, Math.Min(740, (int)(maximumClient.Width / scale)));
                var height = maximumClient.IsEmpty ? 480 : Math.Max(330, Math.Min(480, (int)(maximumClient.Height / scale)));
                var compact = height < 400;
                var ratio = height / 480f;
                logoY = compact ? 16 : 34 * ratio;
                titleY = compact ? 13 : 31 * ratio;
                subtitleY = compact ? 65 : 98 * ratio;
                labelY = compact ? 105 : 153 * ratio;
                fieldY = compact ? 134 : 184 * ratio;
                fieldHeight = compact ? 50 : 54;
                hintY = compact ? 196 : 250 * ratio;
                checkY = compact ? height - 92 : 304 * ratio;
                launchY = compact || complete ? checkY : 344 * ratio;
                lineY = compact ? height - 60 : 397 * ratio;
                footerY = compact ? height - 50 : 418 * ratio;
                buttonHeight = compact ? 38 : 44;
                progressY = lineY - 8;
                SuspendLayout();
                ClientSize = new Size(P(layoutWidth), P(height));
                if (brandFont != null) brandFont.Dispose();
                if (bodyFont != null) bodyFont.Dispose();
                brandFont = new Font("Segoe UI", 30 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                bodyFont = new Font("Microsoft YaHei UI", 16 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                foreach (Control control in Controls)
                {
                    var old = control.Font;
                    control.Font = new Font("Microsoft YaHei UI", (control == hint || control == status ? 14 : 16) * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                    if (old != DefaultFont) old.Dispose();
                }
                var textHeight = Destination.PreferredHeight;
                Destination.SetBounds(P(56), P(fieldY) + (P(fieldHeight) - textHeight) / 2, P(layoutWidth - 230), textHeight);
                BrowseButton.SetBounds(P(layoutWidth - 158), P(fieldY + (fieldHeight - 38) / 2), P(110), P(38));
                desktop.SetBounds(P(40), P(checkY), P(compact ? (layoutWidth - 96) / 2f : layoutWidth - 80), P(30));
                launch.SetBounds(P(compact && !complete ? layoutWidth / 2f + 8 : 40), P(launchY), P(compact && !complete ? (layoutWidth - 96) / 2f : layoutWidth - 80), P(30));
                hint.SetBounds(P(40), P(hintY), P(layoutWidth - 80), P(compact ? 38 : 44));
                status.SetBounds(P(40), P(footerY), P(layoutWidth - 370), P(buttonHeight));
                cancel.SetBounds(P(layoutWidth - 300), P(footerY), P(92), P(buttonHeight));
                install.SetBounds(P(layoutWidth - 192), P(footerY), P(152), P(buttonHeight));
                BrowseButton.ScaleFactor = cancel.ScaleFactor = install.ScaleFactor = scale;
                desktop.ScaleFactor = launch.ScaleFactor = scale;
                ResumeLayout(false);
                Invalidate(true);
            }
            finally { layingOut = false; }
        }

        private int P(float value) { return (int)Math.Round(value * scale); }

        protected override void OnPaintBackground(PaintEventArgs e) { PaintSurface(e.Graphics); }

        internal void PaintSurface(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(16, 32, 60), Color.FromArgb(6, 14, 30), 55f)) g.FillRectangle(background, ClientRectangle);
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(P(330), P(-245), P(650), P(550));
                using (var brush = new PathGradientBrush(glow)) { brush.CenterColor = Color.FromArgb(27, 82, 121, 226); brush.SurroundColors = new[] { Color.Transparent }; g.FillPath(brush, glow); }
            }
            using (var field = Drawing.Round(new RectangleF(P(40), P(fieldY), P(layoutWidth - 80), P(fieldHeight)), P(12)))
            using (var fill = new SolidBrush(Field))
            using (var border = new Pen(Color.FromArgb(62, 84, 120), Math.Max(1, scale)))
            { g.FillPath(fill, field); g.DrawPath(border, field); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PaintLogo(g, new RectangleF(P(40), P(logoY), P(44), P(44)));
            TextRenderer.DrawText(g, "TwinSync", brandFont, new Rectangle(P(99), P(titleY), P(350), P(50)), Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, complete ? "安装完成，随时开始同步。" : "在这台电脑上安装 TwinSync", bodyFont, new Rectangle(P(40), P(subtitleY), P(layoutWidth - 80), P(30)), Muted, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "安装位置", bodyFont, new Rectangle(P(40), P(labelY), P(layoutWidth - 80), P(28)), Ink, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            using (var line = new Pen(Color.FromArgb(38, 56, 83), scale)) g.DrawLine(line, P(40), P(lineY), P(layoutWidth - 40), P(lineY));
            if (busy || complete)
            {
                using (var rail = Drawing.Round(new RectangleF(P(40), P(progressY), P(layoutWidth - 80), P(3)), P(1.5f)))
                using (var brush = new SolidBrush(Color.FromArgb(33, 52, 82))) g.FillPath(brush, rail);
                if (progress > 0) using (var fill = Drawing.Round(new RectangleF(P(40), P(progressY), P((layoutWidth - 80) * progress / 100f), P(3)), P(1.5f)))
                    using (var brush = new SolidBrush(Color.FromArgb(144, 182, 255))) g.FillPath(brush, fill);
            }
        }

        private static void PaintLogo(Graphics graphics, RectangleF box)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(box.X, box.Y);
            graphics.ScaleTransform(box.Width / 64, box.Height / 64);
            using (var pen = new Pen(Color.FromArgb(228, 238, 255), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var path = new GraphicsPath())
            {
                path.AddLine(34, 13, 24, 13);
                path.AddArc(10, 13, 28, 28, 270, -180);
                path.AddLine(24, 41, 29, 41);
                graphics.DrawPath(pen, path);
                path.Reset();
                path.AddLine(30, 51, 40, 51);
                path.AddArc(26, 23, 28, 28, 90, -180);
                path.AddLine(40, 23, 35, 23);
                graphics.DrawPath(pen, path);
            }
            graphics.Restore(state);
        }

        private void UpdateHint()
        {
            hint.Text = !string.IsNullOrEmpty(previous) && !string.Equals(Destination.Text.TrimEnd('\\'), previous.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                ? "更换安装位置时会复制原有配置和历史版本，旧目录中的数据会保留。"
                : "可直接输入路径，或浏览选择其他文件夹。";
        }

        private void Browse(object sender, EventArgs e)
        {
            try { var selected = selectFolder(Handle, Destination.Text); if (selected != null) Destination.Text = selected; }
            catch (Exception error) { ShowError("无法打开文件夹选择器：" + error.Message + " 也可直接输入路径。"); }
        }

        private async void Install(object sender, EventArgs e)
        {
            if (complete)
            {
                try { if (launch.Checked) WindowsIntegration.Launch(installedDirectory); Close(); }
                catch (Exception error) { ShowError("启动失败：" + error.Message); }
                return;
            }
            string directory;
            try { directory = InstallEngine.ValidateDirectory(Destination.Text); Destination.Text = directory; }
            catch (Exception error) { ShowError(error.Message); Destination.Focus(); return; }
            var createDesktop = desktop.Checked;
            SetBusy(true);
            try
            {
                await Task.Run(delegate
                {
                    InstallEngine.Install(directory, createDesktop, previous, integration, delegate(int value, string message)
                    { BeginInvoke((Action)delegate { progress = value; status.Text = message; Invalidate(); }); });
                });
                installedDirectory = directory;
                complete = true;
                progress = 100;
                desktop.Visible = false;
                hint.ForeColor = Muted;
                hint.Text = "已安装到此文件夹。卸载时会保留配置、历史版本和同步文件。";
                status.Text = "已准备就绪";
                install.Text = "完成";
                cancel.Text = "关闭";
                SetBusy(false);
                FitToWorkArea(scale);
                install.Focus();
            }
            catch (Exception error) { SetBusy(false); ShowError(error.Message); }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            Destination.ReadOnly = value || complete;
            BrowseButton.Enabled = !value && !complete;
            desktop.Enabled = !value;
            launch.Enabled = !value;
            install.Enabled = cancel.Enabled = !value;
            if (value) { hint.ForeColor = Muted; hint.Text = "正在安装，请稍候…"; status.Text = "正在准备…"; }
            Invalidate(true);
        }

        private void ShowError(string message) { hint.Text = message; hint.ForeColor = Color.FromArgb(255, 170, 158); status.Text = "请检查后重试"; }
        protected override void OnFormClosing(FormClosingEventArgs e) { if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; base.OnFormClosing(e); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (brandFont != null) brandFont.Dispose(); if (bodyFont != null) bodyFont.Dispose(); foreach (Control control in Controls) control.Font.Dispose(); if (Icon != null) Icon.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class Drawing
    {
        internal static void PaintControlSurface(Control control, Graphics graphics)
        {
            var form = control.Parent as InstallerForm;
            if (form == null) { graphics.Clear(control.BackColor); return; }
            var state = graphics.Save();
            try
            {
                graphics.TranslateTransform(-control.Left, -control.Top);
                form.PaintSurface(graphics);
            }
            finally { graphics.Restore(state); }
        }

        internal static GraphicsPath Round(RectangleF rectangle, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
            if (diameter <= 0) { path.AddRectangle(rectangle); return path; }
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
    }

    internal sealed class RoundButton : Button
    {
        internal bool Primary;
        internal float ScaleFactor = 1;
        private bool hover, pressed;
        internal RoundButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true); BackColor = Color.FromArgb(8, 17, 35); Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            // Each buffered frame is opaque, including the space outside the rounded shape.
            Drawing.PaintControlSurface(this, e.Graphics);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = Primary ? Color.FromArgb(231, 239, 255) : Color.FromArgb(29, 45, 71);
            if (hover) color = Primary ? Color.White : Color.FromArgb(44, 63, 92);
            if (pressed) color = Primary ? Color.FromArgb(184, 206, 242) : Color.FromArgb(18, 31, 55);
            if (!Enabled) color = Color.FromArgb(36, 52, 77);
            using (var path = Drawing.Round(new RectangleF(1, 1, Width - 2, Height - 2), 11 * ScaleFactor))
            using (var brush = new SolidBrush(color))
            { e.Graphics.FillPath(brush, path); if (Focused && ShowFocusCues) using (var pen = new Pen(Color.FromArgb(110, 157, 239), 2 * ScaleFactor)) e.Graphics.DrawPath(pen, path); }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, !Enabled ? Color.FromArgb(135, 154, 183) : Primary ? Color.FromArgb(15, 28, 49) : Color.FromArgb(229, 237, 250), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint supplies the complete background. */ }
    }

    internal sealed class InstallOption : CheckBox
    {
        internal float ScaleFactor = 1;
        internal InstallOption() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true); BackColor = Color.FromArgb(8, 17, 35); Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Drawing.PaintControlSurface(this, e.Graphics);
            var s = ScaleFactor;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new RectangleF(s, (Height - 20 * s) / 2, 20 * s, 20 * s);
            using (var path = Drawing.Round(box, 5 * s))
            using (var fill = new SolidBrush(Checked ? Color.FromArgb(177, 202, 246) : Color.FromArgb(18, 31, 55)))
            using (var border = new Pen(Color.FromArgb(99, 124, 163), s))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path); }
            if (Checked) using (var pen = new Pen(Color.FromArgb(16, 34, 62), 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                e.Graphics.DrawLines(pen, new[] { new PointF(box.X + 5 * s, box.Y + 10 * s), new PointF(box.X + 9 * s, box.Y + 14 * s), new PointF(box.X + 16 * s, box.Y + 6 * s) });
            var text = new Rectangle((int)(34 * s), 0, Width - (int)(34 * s), Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? Color.FromArgb(218, 229, 247) : Color.FromArgb(129, 150, 181), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, text);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint supplies the complete background. */ }
    }
}
