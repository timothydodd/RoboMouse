using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Forms;

/// <summary>
/// Click-through, per-pixel-alpha overlay across the whole desktop that briefly marks where the
/// mouse arrived: a thin border around everything, or a soft glow along the arrival edge. Drawn
/// with UpdateLayeredWindow so gradients blend with whatever is underneath, then faded out by
/// lowering the window's constant alpha.
/// </summary>
public sealed class EdgeHighlightForm : Form
{
    private static readonly Color Accent = Color.FromArgb(30, 100, 230);

    private Bitmap? _surface;
    private System.Windows.Forms.Timer? _fadeTimer;
    private int _alpha;

    public EdgeHighlightForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Shows the cue for <paramref name="edge"/> and fades it out over roughly a second.</summary>
    public void Flash(EdgeHighlightStyle style, ScreenPosition edge)
    {
        if (style == EdgeHighlightStyle.None)
            return;

        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;

        _surface?.Dispose();
        _surface = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            if (style == EdgeHighlightStyle.Border)
                DrawBorder(g, bounds.Size);
            else
                DrawEdgeGlow(g, bounds.Size, edge);
        }

        _alpha = 255;
        if (!Visible)
            Show();
        Push();

        _fadeTimer?.Dispose();
        _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        var holdUntil = Environment.TickCount64 + 350;
        _fadeTimer.Tick += (s, e) =>
        {
            if (Environment.TickCount64 < holdUntil)
                return;
            _alpha -= 10;
            if (_alpha <= 0)
            {
                _fadeTimer?.Stop();
                Hide();
                return;
            }
            Push();
        };
        _fadeTimer.Start();
    }

    private static void DrawBorder(Graphics g, Size size)
    {
        const int thickness = 4;
        using var pen = new Pen(Accent, thickness);
        g.DrawRectangle(pen, thickness / 2, thickness / 2, size.Width - thickness, size.Height - thickness);
    }

    private static void DrawEdgeGlow(Graphics g, Size size, ScreenPosition edge)
    {
        // Strip depth is a slice of the screen so it reads the same on any resolution.
        var vertical = edge is ScreenPosition.Left or ScreenPosition.Right;
        var depth = Math.Max(48, (int)((vertical ? size.Width : size.Height) * 0.07));

        Rectangle strip;
        LinearGradientMode mode;
        switch (edge)
        {
            case ScreenPosition.Left: strip = new Rectangle(0, 0, depth, size.Height); mode = LinearGradientMode.Horizontal; break;
            case ScreenPosition.Right: strip = new Rectangle(size.Width - depth, 0, depth, size.Height); mode = LinearGradientMode.Horizontal; break;
            case ScreenPosition.Top: strip = new Rectangle(0, 0, size.Width, depth); mode = LinearGradientMode.Vertical; break;
            default: strip = new Rectangle(0, size.Height - depth, size.Width, depth); mode = LinearGradientMode.Vertical; break;
        }

        var strong = Color.FromArgb(170, Accent);
        var clear = Color.FromArgb(0, Accent);
        var fromEdge = edge is ScreenPosition.Left or ScreenPosition.Top;
        using var brush = new LinearGradientBrush(strip, fromEdge ? strong : clear, fromEdge ? clear : strong, mode);
        // Ease the falloff so the glow is dense at the edge and thins quickly.
        brush.Blend = fromEdge
            ? new Blend { Positions = new[] { 0f, 0.35f, 1f }, Factors = new[] { 0f, 0.75f, 1f } }
            : new Blend { Positions = new[] { 0f, 0.65f, 1f }, Factors = new[] { 0f, 0.25f, 1f } };
        g.FillRectangle(brush, strip);

        // Crisp 2px line right on the edge.
        using var pen = new Pen(Color.FromArgb(230, Accent), 2);
        switch (edge)
        {
            case ScreenPosition.Left: g.DrawLine(pen, 1, 0, 1, size.Height); break;
            case ScreenPosition.Right: g.DrawLine(pen, size.Width - 1, 0, size.Width - 1, size.Height); break;
            case ScreenPosition.Top: g.DrawLine(pen, 0, 1, size.Width, 1); break;
            default: g.DrawLine(pen, 0, size.Height - 1, size.Width, size.Height - 1); break;
        }
    }

    /// <summary>Copies the surface to the layered window at the current alpha.</summary>
    private void Push()
    {
        if (_surface == null || !IsHandleCreated)
            return;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            hBitmap = _surface.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = _surface.Width, cy = _surface.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Clamp(_alpha, 0, 255), AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Layered windows are painted via UpdateLayeredWindow only.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer?.Dispose();
            _surface?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Native

    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    #endregion
}
