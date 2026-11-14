using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrivacyScreen;

/// <summary>物理屏幕的保护层；解锁 UI 继续直接画在捕获排除窗口里，不创建 TextBox。</summary>
internal sealed class ShieldForm : Form
{
    private readonly AppConfig _config;
    private readonly List<string> _images;
    private readonly System.Windows.Forms.Timer? _slideshow;
    private Image? _current;
    private int _imageIndex = -1;
    private bool _allowClose;
    private bool _prompt;
    private bool _checking;
    private int _passwordLength;
    private string _status = "";

    internal ShieldForm(Screen screen, AppConfig config)
    {
        _config = config;
        _images = ImageLibrary.PlaylistPaths(config);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.FromArgb(36, 47, 72);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        // 不再维护每秒时钟；只有超过一张图时才创建轮播定时器。
        if (_images.Count > 1)
        {
            _slideshow = new System.Windows.Forms.Timer { Interval = Math.Clamp(config.SlideSeconds, 5, 3600) * 1000 };
            _slideshow.Tick += (_, _) => NextImage();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                         Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA))
            throw new InvalidOperationException("启用分层鼠标穿透失败：" + Marshal.GetLastWin32Error());
    }

    internal void ProtectAndStart()
    {
        if (!Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE))
            throw new InvalidOperationException("SetWindowDisplayAffinity 失败，Win32 错误 " + Marshal.GetLastWin32Error());
        if (!Native.GetWindowDisplayAffinity(Handle, out uint actual))
            throw new InvalidOperationException("GetWindowDisplayAffinity 失败，Win32 错误 " + Marshal.GetLastWin32Error());
        if (actual != Native.WDA_EXCLUDEFROMCAPTURE)
            throw new InvalidOperationException($"读取窗口捕获排除值不符：0x{actual:X8}");
        Log.Write($"Affinity OK: hwnd=0x{Handle.ToInt64():X}, value=0x{actual:X8}, bounds={Bounds}");
        NextImage();
        _slideshow?.Start();
    }

    private void NextImage()
    {
        _current?.Dispose();
        _current = null;
        if (_images.Count == 0) { Invalidate(); return; }
        for (int i = 0; i < _images.Count; i++)
        {
            _imageIndex = (_imageIndex + 1) % _images.Count;
            try
            {
                using var fs = new FileStream(_images[_imageIndex], FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var raw = Image.FromStream(fs);
                _current = new Bitmap(raw); // 避免轮播过程中持续占用图片文件。
                break;
            }
            catch (Exception ex) { Log.Write("Image skipped: " + _images[_imageIndex] + ": " + ex.Message); }
        }
        Invalidate();
    }

    internal void UpdatePrompt(bool visible, int length, bool checking, string status)
    {
        _prompt = visible;
        _passwordLength = length;
        _checking = checking;
        _status = status;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Rectangle r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;

        if (_current is not null)
        {
            float scale = Math.Max((float)r.Width / _current.Width, (float)r.Height / _current.Height);
            float srcW = r.Width / scale, srcH = r.Height / scale;
            g.DrawImage(_current, r, (int)((_current.Width - srcW) / 2),
                (int)((_current.Height - srcH) / 2), (int)srcW, (int)srcH, GraphicsUnit.Pixel);
            // 图片屏保不覆盖时钟、日期和自定义文字：保留原图的纯净展示。
        }
        else
        {
            using var gradient = new LinearGradientBrush(r,
                Color.FromArgb(29, 52, 91), Color.FromArgb(118, 70, 124), 33f);
            g.FillRectangle(gradient, r);
            using var glow = new SolidBrush(Color.FromArgb(28, 240, 228, 255));
            int diameter = (int)(Math.Min(r.Width, r.Height) * 0.8);
            g.FillEllipse(glow, r.Width - diameter / 2, -diameter / 3, diameter, diameter);
            if (!string.IsNullOrWhiteSpace(_config.Message))
            {
                float textSize = Math.Clamp(r.Width / 58f, 17, 33);
                using var font = new Font("Microsoft YaHei UI", textSize, FontStyle.Regular, GraphicsUnit.Pixel);
                using var pale = new SolidBrush(Color.FromArgb(239, 243, 253));
                using var shadow = new SolidBrush(Color.FromArgb(78, 8, 16, 39));
                using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var rect = new RectangleF(24, r.Height / 2f - 50, r.Width - 48, 100);
                g.DrawString(_config.Message, font, shadow,
                    new RectangleF(rect.X + 1.5f, rect.Y + 2f, rect.Width, rect.Height), centered);
                g.DrawString(_config.Message, font, pale, rect, centered);
            }
        }
        if (_prompt) DrawPassword(g, r);
    }

    private void DrawPassword(Graphics g, Rectangle screen)
    {
        // 轻度压暗 + 多层柔和投影营造虚化 / 磨砂质感；不做耗时的屏幕采样模糊。
        using (var dim = new SolidBrush(Color.FromArgb(134, 8, 15, 30)))
            g.FillRectangle(dim, screen);

        float scale = Math.Min(1f, Math.Min((screen.Width - 24f) / 500f, (screen.Height - 24f) / 352f));
        if (scale <= 0.15f) return;
        float w = 500 * scale, h = 352 * scale;
        var card = new RectangleF((screen.Width - w) / 2f, (screen.Height - h) / 2f, w, h);
        float rad = 28 * scale;

        for (int layer = 8; layer >= 1; layer--)
        {
            float spread = layer * 3.0f * scale;
            var shadowRect = new RectangleF(card.Left - spread, card.Top - spread + 10 * scale,
                card.Width + spread * 2, card.Height + spread * 2);
            using var shadow = new SolidBrush(Color.FromArgb(Math.Max(1, 16 - layer), 1, 6, 21));
            g.FillRoundedRectangle(shadow, shadowRect, rad + spread);
        }

        using (var cardGradient = new LinearGradientBrush(card,
            Color.FromArgb(246, 28, 39, 62), Color.FromArgb(242, 17, 26, 44), 90f))
            g.FillRoundedRectangle(cardGradient, card, rad);
        using (var outline = new Pen(Color.FromArgb(100, 208, 222, 246), 1.1f * scale))
            g.DrawRoundedRectangle(outline, card, rad);

        float cx = card.Left + card.Width / 2;
        float circleSize = 58 * scale;
        var iconCircle = new RectangleF(cx - circleSize / 2, card.Top + 28 * scale,
            circleSize, circleSize);
        using (var circleBg = new SolidBrush(Color.FromArgb(72, 124, 157, 211)))
            g.FillEllipse(circleBg, iconCircle);
        using (var circlePen = new Pen(Color.FromArgb(86, 194, 216, 255), 1.2f * scale))
            g.DrawEllipse(circlePen, iconCircle);
        DrawLockGlyph(g, cx, iconCircle.Top + circleSize * 0.51f, scale);

        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var titleFont = new Font("Microsoft YaHei UI", 23 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Microsoft YaHei UI", 12.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var passwordFont = new Font("Segoe UI", 24 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.FromArgb(251, 252, 255));
        using var secondaryBrush = new SolidBrush(Color.FromArgb(182, 201, 225));
        using var errorBrush = new SolidBrush(Color.FromArgb(255, 157, 163));
        using var checkingBrush = new SolidBrush(Color.FromArgb(157, 207, 255));

        g.DrawString("解锁隐私屏保", titleFont, titleBrush,
            new RectangleF(card.Left + 20 * scale, card.Top + 99 * scale, card.Width - 40 * scale, 40 * scale), centered);
        g.DrawString("请使用本机实体键盘输入密码", smallFont, secondaryBrush,
            new RectangleF(card.Left + 20 * scale, card.Top + 143 * scale, card.Width - 40 * scale, 27 * scale), centered);

        var input = new RectangleF(card.Left + 50 * scale, card.Top + 186 * scale,
            card.Width - 100 * scale, 62 * scale);
        using (var inputBackground = new SolidBrush(Color.FromArgb(220, 9, 18, 35)))
            g.FillRoundedRectangle(inputBackground, input, 18 * scale);
        using (var inputBorder = new Pen(Color.FromArgb(122, 147, 184, 231), 1.2f * scale))
            g.DrawRoundedRectangle(inputBorder, input, 18 * scale);

        if (_checking)
        {
            g.DrawString("正在验证…", smallFont, checkingBrush, input, centered);
        }
        else
        {
            // 每个圆点由屏保自己绘制；它不是可获得焦点的输入控件。
            string dots = new string('●', Math.Min(_passwordLength, 20));
            if (_passwordLength > 20) dots += "…";
            g.DrawString(dots, passwordFont, titleBrush, input, centered);
            float dotsWidth = _passwordLength == 0 ? 0f : g.MeasureString(dots, passwordFont).Width;
            float caretX = Math.Min(input.Right - 24 * scale, cx + dotsWidth / 2 + 4 * scale);
            using var caretPen = new Pen(Color.FromArgb(192, 223, 255), Math.Max(1.2f, 2f * scale));
            g.DrawLine(caretPen, caretX, input.Top + 18 * scale, caretX, input.Bottom - 18 * scale);
        }

        string status = string.IsNullOrEmpty(_status) ? "按 Enter 确认  ·  Esc 取消" : _status;
        Brush statusBrush = status.Contains("错误", StringComparison.Ordinal) ? errorBrush :
            (_checking ? checkingBrush : secondaryBrush);
        g.DrawString(status, smallFont, statusBrush,
            new RectangleF(card.Left + 22 * scale, card.Top + 271 * scale,
                card.Width - 44 * scale, 45 * scale), centered);
    }

    private static void DrawLockGlyph(Graphics g, float cx, float cy, float scale)
    {
        using var pen = new Pen(Color.FromArgb(240, 241, 250), 2.7f * scale)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        // 锁梁与锁体均自行矢量绘制，避免系统字形缺失或乱码。
        var shackle = new RectangleF(cx - 9 * scale, cy - 15 * scale, 18 * scale, 20 * scale);
        g.DrawArc(pen, shackle, 180, 180);
        using var body = new SolidBrush(Color.FromArgb(242, 240, 248, 255));
        g.FillRoundedRectangle(body,
            new RectangleF(cx - 14 * scale, cy - 3 * scale, 28 * scale, 22 * scale), 5 * scale);
        using var keyhole = new SolidBrush(Color.FromArgb(60, 91, 133));
        g.FillEllipse(keyhole, cx - 2.7f * scale, cy + 3.5f * scale, 5.4f * scale, 5.4f * scale);
        g.FillRectangle(keyhole, cx - 1.3f * scale, cy + 7f * scale, 2.6f * scale, 5f * scale);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    internal void CloseSafely()
    {
        _allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _slideshow?.Dispose(); _current?.Dispose(); }
        base.Dispose(disposing);
    }
}

internal static class GraphicsRoundRect
{
    internal static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF r, float rad)
    {
        using var path = MakePath(r, rad);
        g.FillPath(brush, path);
    }
    internal static void DrawRoundedRectangle(this Graphics g, Pen pen, RectangleF r, float rad)
    {
        using var path = MakePath(r, rad);
        g.DrawPath(pen, path);
    }
    private static GraphicsPath MakePath(RectangleF r, float rad)
    {
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
