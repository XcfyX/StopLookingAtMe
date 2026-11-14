using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrivacyScreen;

/// <summary>
/// 远程可见的独立分层窗口：与 ShieldForm 不同，绝不设置 WDA_EXCLUDEFROMCAPTURE。
/// 原生 UpdateLayeredWindow + 32 位预乘 Alpha DIB，不使用 TransparencyKey。
/// </summary>
internal sealed class IndicatorWindow : Form
{
    private const float EdgeClip = 0.28f; // 右/左裁出 28%，处于要求的 20–35% 范围内
    private const int BaseSize = 104;      // 100% DPI 下 104×104 px，实际露出约 75 px
    private readonly Screen _screen;
    private readonly bool _left;
    private string _path;
    internal string Source { get; private set; }
    internal string PositionName => _left ? "LeftTop" : "RightTop";

    internal IndicatorWindow(AppConfig config)
    {
        _screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        _left = config.IndicatorPosition == "LeftTop";
        _path = IndicatorAssets.ResolveImage(config, out string source);
        Source = source;
        // 坏的自定义图回退至内置图，不影响屏保成功锁定。
        if (source == "user")
        {
            try { using var test = Image.FromFile(_path); if (test.Width <= 0) throw new InvalidDataException(); }
            catch (Exception ex)
            {
                Log.Write("Indicator custom image decode failed, fallback to default: " + ex.Message);
                _path = IndicatorAssets.EnsureDefault();
                Source = "default";
            }
        }
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // 在主屏幕内部创建句柄，以取得正确的主屏 DPI，之后再移到屏幕边缘外。
        Bounds = new Rectangle(_screen.Bounds.Left + 2, _screen.Bounds.Top + 2, BaseSize, BaseSize);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT |
                         Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            return p;
        }
    }

    internal void ShowIndicator()
    {
        uint dpi = Native.GetDpiForWindow(Handle);
        if (dpi is < 72 or > 768) dpi = 96;
        int size = Math.Clamp((int)Math.Round(BaseSize * (double)dpi / 96.0), 72, 512);
        int cut = (int)Math.Round(size * EdgeClip);
        int x = _left ? _screen.Bounds.Left - cut : _screen.Bounds.Right - size + cut;
        int y = _screen.Bounds.Top; // 顶边贴齐，不留浮窗空隙
        Bounds = new Rectangle(x, y, size, size);

        using var canvas = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            using var loaded = Image.FromFile(_path);
            float factor = Math.Min((float)size / loaded.Width, (float)size / loaded.Height);
            float w = loaded.Width * factor;
            float h = loaded.Height * factor;
            // 屏幕左侧按需求镜像，使图片视觉朝向屏幕中央。原图正脸，镜像仅影响细节朝向。
            if (_left)
            {
                g.TranslateTransform(size, 0);
                g.ScaleTransform(-1, 1);
            }
            g.DrawImage(loaded, new RectangleF((size - w) / 2, (size - h) / 2, w, h));
        }

        Show();
        RenderLayered(canvas, Bounds.Location);
        if (!Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, size, size, Native.SWP_NOACTIVATE))
            throw new InvalidOperationException("置顶状态指示窗失败：Win32 " + Marshal.GetLastWin32Error());
        // 本类未调用 SetWindowDisplayAffinity：正常由捕获程序看到该窗口。
    }

    protected override void OnPaint(PaintEventArgs e) { /* 位图由 UpdateLayeredWindow 独立提交。*/ }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    private void RenderLayered(Bitmap canvas, Point location)
    {
        int width = canvas.Width, height = canvas.Height;
        nint destinationDC = nint.Zero, memoryDC = nint.Zero, dib = nint.Zero, previous = nint.Zero;
        try
        {
            destinationDC = Native.GetDC(nint.Zero);
            if (destinationDC == nint.Zero) throw new InvalidOperationException("GetDC 失败");
            memoryDC = Native.CreateCompatibleDC(destinationDC);
            if (memoryDC == nint.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = width, biHeight = -height, // top-down DIB
                    biPlanes = 1, biBitCount = 32, biCompression = 0
                }
            };
            dib = Native.CreateDIBSection(memoryDC, ref info, 0, out nint pixels, nint.Zero, 0);
            if (dib == nint.Zero || pixels == nint.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败：Win32 " + Marshal.GetLastWin32Error());
            var rectangle = new Rectangle(0, 0, width, height);
            var imageData = canvas.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                byte[] scanline = new byte[width * 4];
                for (int row = 0; row < height; row++)
                {
                    Marshal.Copy(IntPtr.Add(imageData.Scan0, row * imageData.Stride), scanline, 0, scanline.Length);
                    Marshal.Copy(scanline, 0, IntPtr.Add(pixels, row * width * 4), scanline.Length);
                }
            }
            finally { canvas.UnlockBits(imageData); }

            previous = Native.SelectObject(memoryDC, dib);
            if (previous == nint.Zero || previous == new nint(-1))
                throw new InvalidOperationException("SelectObject 失败：Win32 " + Marshal.GetLastWin32Error());
            var dest = new Native.POINT { X = location.X, Y = location.Y };
            var size = new Native.SIZE { cx = width, cy = height };
            var source = new Native.POINT();
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1
            };
            if (!Native.UpdateLayeredWindow(Handle, destinationDC, ref dest, ref size,
                    memoryDC, ref source, 0, ref blend, 0x00000002))
                throw new InvalidOperationException("UpdateLayeredWindow 失败：Win32 " + Marshal.GetLastWin32Error());
        }
        finally
        {
            if (previous != nint.Zero && previous != new nint(-1) && memoryDC != nint.Zero)
                Native.SelectObject(memoryDC, previous);
            if (dib != nint.Zero) Native.DeleteObject(dib);
            if (memoryDC != nint.Zero) Native.DeleteDC(memoryDC);
            if (destinationDC != nint.Zero) Native.ReleaseDC(nint.Zero, destinationDC);
        }
    }
}
