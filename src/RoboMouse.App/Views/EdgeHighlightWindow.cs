using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Screen;
using Rectangle = System.Drawing.Rectangle;

namespace RoboMouse.App.Views;

/// <summary>
/// Briefly marks where the mouse arrived: a thin border around the monitor it landed on, or a soft
/// glow along the arrival edge. Made of windows no bigger than what is drawn. A transparent window
/// over the whole desktop takes a playing video off its hardware overlay while it is up, which shows
/// as a flicker each time the cue appears and hides.
/// </summary>
public sealed class EdgeHighlight
{
    private const int BorderThickness = 4;

    // The glow needs one window, the border one per side.
    private readonly EdgeHighlightWindow?[] _windows = new EdgeHighlightWindow?[4];

    /// <summary>Shows the cue for <paramref name="edge"/> and fades it out over roughly a second.</summary>
    public void Flash(EdgeHighlightStyle style, ScreenPosition edge)
    {
        if (style == EdgeHighlightStyle.None)
            return;

        var (x, y) = InputSimulator.GetCursorPosition();
        var monitor = ScreenInfo.ReadLayout().GetScreenAt(x, y);

        var strips = new List<(Rectangle Bounds, ScreenPosition? GlowEdge)>();
        if (style == EdgeHighlightStyle.Fade)
        {
            // Strip depth is a slice of the screen so it reads the same on any resolution.
            var vertical = edge is ScreenPosition.Left or ScreenPosition.Right;
            var depth = Math.Max(48, (int)((vertical ? monitor.Width : monitor.Height) * 0.07));
            strips.Add((EdgeStrip(monitor, edge, depth), edge));
        }
        else
        {
            foreach (var side in new[] { ScreenPosition.Left, ScreenPosition.Right, ScreenPosition.Top, ScreenPosition.Bottom })
                strips.Add((EdgeStrip(monitor, side, BorderThickness), null));
        }

        for (var i = 0; i < _windows.Length; i++)
        {
            if (i < strips.Count)
                (_windows[i] ??= new EdgeHighlightWindow()).Flash(strips[i].Bounds, strips[i].GlowEdge);
            else
                _windows[i]?.Cancel();
        }
    }

    public void Close()
    {
        foreach (var window in _windows)
            window?.Close();
    }

    private static Rectangle EdgeStrip(Rectangle monitor, ScreenPosition edge, int depth) => edge switch
    {
        ScreenPosition.Left => new Rectangle(monitor.Left, monitor.Top, depth, monitor.Height),
        ScreenPosition.Right => new Rectangle(monitor.Right - depth, monitor.Top, depth, monitor.Height),
        ScreenPosition.Top => new Rectangle(monitor.Left, monitor.Top, monitor.Width, depth),
        _ => new Rectangle(monitor.Left, monitor.Bottom - depth, monitor.Width, depth)
    };
}

/// <summary>
/// One click-through, transparent strip of the <see cref="EdgeHighlight"/> cue. Faded out by lowering
/// the window's opacity.
/// </summary>
public sealed partial class EdgeHighlightWindow : Window
{
    private static Color Accent =>
        Application.Current?.TryGetResource("SystemAccentColor", null, out var value) == true && value is Color color
            ? color
            : Color.FromRgb(30, 100, 230);

    private readonly HighlightSurface _surface = new();
    private DispatcherTimer? _fadeTimer;
    private bool _stylesApplied;

    public EdgeHighlightWindow()
    {
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Focusable = false;
        IsHitTestVisible = false;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Content = _surface;

        Opened += (_, _) => ApplyClickThrough();
    }

    /// <summary>
    /// Covers <paramref name="bounds"/> (screen pixels) with the glow for <paramref name="glowEdge"/>,
    /// or with solid accent colour when it is null, then fades out.
    /// </summary>
    public void Flash(Rectangle bounds, ScreenPosition? glowEdge)
    {
        var origin = new PixelPoint(bounds.X, bounds.Y);
        var scaling = Screens.ScreenFromPoint(origin)?.Scaling ?? Screens.Primary?.Scaling ?? 1.0;
        Position = origin;
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;

        _surface.GlowEdge = glowEdge;
        _surface.InvalidateVisual();

        Opacity = 1;
        if (!IsVisible)
            Show();
        else
            ApplyClickThrough();

        _fadeTimer?.Stop();
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var holdUntil = Environment.TickCount64 + 350;
        _fadeTimer.Tick += (s, e) =>
        {
            if (Environment.TickCount64 < holdUntil)
                return;
            Opacity -= 10 / 255.0;
            if (Opacity <= 0)
                Cancel();
        };
        _fadeTimer.Start();
    }

    /// <summary>Hides the strip straight away.</summary>
    public void Cancel()
    {
        _fadeTimer?.Stop();
        if (IsVisible)
            Hide();
    }

    /// <summary>Marks the native window as layered, transparent to input and never activated.</summary>
    private void ApplyClickThrough()
    {
        if (_stylesApplied)
            return;
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
            return;
        var style = GetWindowLongPtrW(handle, GWL_EXSTYLE);
        SetWindowLongPtrW(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        _stylesApplied = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _fadeTimer?.Stop();
        base.OnClosing(e);
    }

    /// <summary>Fills the strip: the edge glow, or solid accent colour for a border side.</summary>
    private sealed class HighlightSurface : Control
    {
        public ScreenPosition? GlowEdge { get; set; }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            if (GlowEdge is { } edge)
                DrawEdgeGlow(context, size, edge);
            else
                context.DrawRectangle(new SolidColorBrush(Accent), null, new Rect(size));
        }

        private static void DrawEdgeGlow(DrawingContext context, Size size, ScreenPosition edge)
        {
            var strip = new Rect(size);
            RelativePoint start, end;
            switch (edge)
            {
                case ScreenPosition.Left:
                    start = new RelativePoint(0, 0, RelativeUnit.Relative); end = new RelativePoint(1, 0, RelativeUnit.Relative);
                    break;
                case ScreenPosition.Right:
                    start = new RelativePoint(1, 0, RelativeUnit.Relative); end = new RelativePoint(0, 0, RelativeUnit.Relative);
                    break;
                case ScreenPosition.Top:
                    start = new RelativePoint(0, 0, RelativeUnit.Relative); end = new RelativePoint(0, 1, RelativeUnit.Relative);
                    break;
                default:
                    start = new RelativePoint(0, 1, RelativeUnit.Relative); end = new RelativePoint(0, 0, RelativeUnit.Relative);
                    break;
            }

            // Dense at the edge, thinning quickly: the same easing the old GDI blend used.
            var brush = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(170, Accent.R, Accent.G, Accent.B), 0),
                    new GradientStop(Color.FromArgb(42, Accent.R, Accent.G, Accent.B), 0.35),
                    new GradientStop(Color.FromArgb(0, Accent.R, Accent.G, Accent.B), 1)
                }
            };
            context.DrawRectangle(brush, null, strip);

            // Crisp 2px line right on the edge.
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, Accent.R, Accent.G, Accent.B)), 2);
            switch (edge)
            {
                case ScreenPosition.Left: context.DrawLine(pen, new Point(1, 0), new Point(1, size.Height)); break;
                case ScreenPosition.Right: context.DrawLine(pen, new Point(size.Width - 1, 0), new Point(size.Width - 1, size.Height)); break;
                case ScreenPosition.Top: context.DrawLine(pen, new Point(0, 1), new Point(size.Width, 1)); break;
                default: context.DrawLine(pen, new Point(0, size.Height - 1), new Point(size.Width, size.Height - 1)); break;
            }
        }
    }

    #region Native

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TRANSPARENT = 0x20;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOOLWINDOW = 0x80;

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    #endregion
}
