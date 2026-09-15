using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.Windows;

/// <summary>
/// Click-through, transparent overlay across the whole desktop that briefly marks where the mouse
/// arrived: a thin border around everything, or a soft glow along the arrival edge. Faded out by
/// lowering the window's opacity.
/// </summary>
public sealed partial class EdgeHighlightWindow : Window
{
    private static readonly Color Accent = Color.FromRgb(30, 100, 230);

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

    /// <summary>Shows the cue for <paramref name="edge"/> and fades it out over roughly a second.</summary>
    public void Flash(EdgeHighlightStyle style, ScreenPosition edge)
    {
        if (style == EdgeHighlightStyle.None)
            return;

        var bounds = ScreenInfo.GetVirtualScreen();
        var scaling = Screens.Primary?.Scaling ?? 1.0;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;

        _surface.Style = style;
        _surface.Edge = edge;
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
            {
                _fadeTimer?.Stop();
                Hide();
            }
        };
        _fadeTimer.Start();
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

    /// <summary>Draws the border or the edge glow over the transparent window.</summary>
    private sealed class HighlightSurface : Control
    {
        public EdgeHighlightStyle Style { get; set; }
        public ScreenPosition Edge { get; set; }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            if (Style == EdgeHighlightStyle.Border)
                DrawBorder(context, size);
            else if (Style == EdgeHighlightStyle.Fade)
                DrawEdgeGlow(context, size, Edge);
        }

        private static void DrawBorder(DrawingContext context, Size size)
        {
            const double thickness = 4;
            var pen = new Pen(new SolidColorBrush(Accent), thickness);
            context.DrawRectangle(null, pen, new Rect(thickness / 2, thickness / 2, size.Width - thickness, size.Height - thickness));
        }

        private static void DrawEdgeGlow(DrawingContext context, Size size, ScreenPosition edge)
        {
            // Strip depth is a slice of the screen so it reads the same on any resolution.
            var vertical = edge is ScreenPosition.Left or ScreenPosition.Right;
            var depth = Math.Max(48, (vertical ? size.Width : size.Height) * 0.07);

            Rect strip;
            RelativePoint start, end;
            switch (edge)
            {
                case ScreenPosition.Left:
                    strip = new Rect(0, 0, depth, size.Height);
                    start = new RelativePoint(0, 0, RelativeUnit.Relative); end = new RelativePoint(1, 0, RelativeUnit.Relative);
                    break;
                case ScreenPosition.Right:
                    strip = new Rect(size.Width - depth, 0, depth, size.Height);
                    start = new RelativePoint(1, 0, RelativeUnit.Relative); end = new RelativePoint(0, 0, RelativeUnit.Relative);
                    break;
                case ScreenPosition.Top:
                    strip = new Rect(0, 0, size.Width, depth);
                    start = new RelativePoint(0, 0, RelativeUnit.Relative); end = new RelativePoint(0, 1, RelativeUnit.Relative);
                    break;
                default:
                    strip = new Rect(0, size.Height - depth, size.Width, depth);
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
