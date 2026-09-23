using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RoboMouse.App.ViewModels;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.Views;

/// <summary>
/// Custom-drawn canvas for visual screen layout editing: drag a peer screen to the edge of this
/// screen it sits on. It edits the page's <see cref="PeerPlacement"/>s, never the peer configs.
/// </summary>
public sealed class ScreenLayoutControl : Control
{
    public static readonly StyledProperty<LayoutPageViewModel?> LayoutProperty =
        AvaloniaProperty.Register<ScreenLayoutControl, LayoutPageViewModel?>(nameof(Layout));

    /// <summary>The page whose placements are laid out and edited.</summary>
    public LayoutPageViewModel? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private readonly List<ScreenRect> _screens = new();
    private ScreenRect? _localScreen;
    private MonitorLayout? _localLayout;
    private ScreenRect? _selectedScreen;
    private ScreenRect? _draggingScreen;
    private Point _dragOffset;

    // Real pixels per canvas unit. Chosen on each rebuild so the whole arrangement fits the canvas.
    private int _scaleFactor = 8;
    private const int CanvasMargin = 36;

    private static readonly IBrush CanvasBrush = new SolidColorBrush(Color.FromRgb(24, 30, 44));
    private static readonly IBrush DotBrush = new SolidColorBrush(Color.FromRgb(48, 56, 74));
    private static readonly Typeface TitleTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SubTypeface = new(FontFamily.Default);

    public ScreenLayoutControl()
    {
        ClipToBounds = true;
        InitializeScreens();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty)
            Reload();
    }

    /// <summary>Rebuilds the canvas from the placements (after peers are added, edited or removed).</summary>
    public void Reload()
    {
        _selectedScreen = null;
        _draggingScreen = null;
        InitializeScreens();
        InvalidateVisual();
    }

    /// <summary>The monitor arrangement, or a stand-in where the monitor API is unavailable (headless previews).</summary>
    private static MonitorLayout GetLocalLayout()
    {
        try
        {
            return ScreenInfo.ReadLayout();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            var main = new System.Drawing.Rectangle(0, 0, 2560, 1440);
            return new MonitorLayout(new[] { new MonitorRect(main, main, true) });
        }
    }

    /// <summary>Test/preview hook: supplies the monitor arrangement instead of reading it from Windows.</summary>
    public static Func<MonitorLayout>? LayoutSource { get; set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Monitors may have changed since the control was built. Drags live in the placements.
        InitializeScreens();
        InvalidateVisual();
    }

    private double CanvasWidth => Bounds.Width > 0 ? Bounds.Width : 600;
    private double CanvasHeight => Bounds.Height > 0 ? Bounds.Height : 400;

    private IReadOnlyList<PeerPlacement> Placements => Layout?.Placements ?? Array.Empty<PeerPlacement>();

    private void InitializeScreens()
    {
        _screens.Clear();

        // The whole desktop (all monitors), since edges are detected on the virtual screen.
        _localLayout = LayoutSource?.Invoke() ?? GetLocalLayout();
        var localBounds = _localLayout.VirtualBounds;
        _scaleFactor = FitScale(localBounds);

        _localScreen = new ScreenRect
        {
            Name = "This PC",
            IsLocal = true,
            OriginalWidth = localBounds.Width,
            OriginalHeight = localBounds.Height,
            DisplayBounds = new Rect(0, 0, localBounds.Width / _scaleFactor, localBounds.Height / _scaleFactor)
        };
        _screens.Add(_localScreen);

        foreach (var placement in Placements)
        {
            var peer = placement.Peer;
            var peerRect = new ScreenRect
            {
                Name = peer.Name,
                Placement = placement,
                OriginalWidth = peer.ScreenWidth,
                OriginalHeight = peer.ScreenHeight,
                DisplayBounds = new Rect(0, 0, peer.ScreenWidth / _scaleFactor, peer.ScreenHeight / _scaleFactor)
            };

            PositionPeerScreen(peerRect, placement.Position, placement.OffsetX, placement.OffsetY);
            _screens.Add(peerRect);
        }

        CenterScreens();
    }

    /// <summary>
    /// Smallest scale divisor at which the local screen plus every peer (at its configured edge and
    /// offset) fits inside the canvas with a margin. Never larger than 1:1.
    /// </summary>
    private int FitScale(System.Drawing.Rectangle local)
    {
        var minX = 0; var minY = 0; var maxX = local.Width; var maxY = local.Height;
        foreach (var peer in Placements)
        {
            var size = new System.Drawing.Size(peer.Peer.ScreenWidth, peer.Peer.ScreenHeight);
            System.Drawing.Rectangle r = peer.Position switch
            {
                ScreenPosition.Left => new(-size.Width, peer.OffsetY, size.Width, size.Height),
                ScreenPosition.Right => new(local.Width, peer.OffsetY, size.Width, size.Height),
                ScreenPosition.Top => new(peer.OffsetX, -size.Height, size.Width, size.Height),
                _ => new(peer.OffsetX, local.Height, size.Width, size.Height)
            };
            minX = Math.Min(minX, r.Left); minY = Math.Min(minY, r.Top);
            maxX = Math.Max(maxX, r.Right); maxY = Math.Max(maxY, r.Bottom);
        }

        // Leave room on every side so a screen can be dragged to an empty edge.
        var extentW = (maxX - minX) + local.Width;
        var extentH = (maxY - minY) + local.Height;
        var availW = Math.Max(100, CanvasWidth - CanvasMargin * 2);
        var availH = Math.Max(100, CanvasHeight - CanvasMargin * 2);
        var scale = Math.Max(extentW / availW, extentH / availH);
        return Math.Max(1, (int)Math.Ceiling(scale));
    }

    private void PositionPeerScreen(ScreenRect peer, ScreenPosition position, int offsetX, int offsetY)
    {
        if (_localScreen == null)
            return;

        var local = _localScreen.DisplayBounds;
        var scaledOffsetX = (double)offsetX / _scaleFactor;
        var scaledOffsetY = (double)offsetY / _scaleFactor;
        var w = peer.DisplayBounds.Width;
        var h = peer.DisplayBounds.Height;

        peer.DisplayBounds = position switch
        {
            ScreenPosition.Left => new Rect(local.Left - w, local.Top + scaledOffsetY, w, h),
            ScreenPosition.Right => new Rect(local.Right, local.Top + scaledOffsetY, w, h),
            ScreenPosition.Top => new Rect(local.Left + scaledOffsetX, local.Top - h, w, h),
            _ => new Rect(local.Left + scaledOffsetX, local.Bottom, w, h)
        };
    }

    private void CenterScreens()
    {
        if (_screens.Count == 0)
            return;

        var minX = _screens.Min(s => s.DisplayBounds.Left);
        var minY = _screens.Min(s => s.DisplayBounds.Top);
        var maxX = _screens.Max(s => s.DisplayBounds.Right);
        var maxY = _screens.Max(s => s.DisplayBounds.Bottom);

        var offsetX = (CanvasWidth - (maxX - minX)) / 2 - minX;
        var offsetY = (CanvasHeight - (maxY - minY)) / 2 - minY;

        foreach (var screen in _screens)
            screen.DisplayBounds = screen.DisplayBounds.Translate(new Vector(offsetX, offsetY));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(CanvasBrush, bounds);

        // Dotted grid so the canvas reads as a workspace.
        for (var y = 12.0; y < bounds.Height; y += 24)
            for (var x = 12.0; x < bounds.Width; x += 24)
                context.FillRectangle(DotBrush, new Rect(x, y, 2, 2));

        foreach (var screen in _screens)
            DrawScreen(context, screen);
    }

    private void DrawScreen(DrawingContext context, ScreenRect screen)
    {
        var rect = screen.DisplayBounds;
        var disabled = screen.Placement is { Peer.Enabled: false };
        var selected = screen == _selectedScreen;

        Color fill, edge;
        if (screen.IsLocal)
        {
            fill = this.FindResource("SystemAccentColor") is Color accent ? accent : Color.FromRgb(30, 100, 230);
            edge = this.FindResource("SystemAccentColorLight1") is Color light ? light : Color.FromRgb(120, 170, 255);
        }
        else if (disabled)
        {
            fill = Color.FromRgb(52, 58, 70);
            edge = Color.FromRgb(90, 98, 112);
        }
        else
        {
            fill = selected ? Color.FromRgb(70, 96, 140) : Color.FromRgb(56, 72, 104);
            edge = selected ? Colors.White : Color.FromRgb(120, 140, 175);
        }

        if (screen.IsLocal && _localLayout is { Monitors.Count: > 1 } layout)
        {
            DrawLocalMonitors(context, rect, layout, fill, edge);
            return;
        }

        context.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(edge), selected ? 2 : 1), rect, 8, 8);

        // Bezel line to hint "screen"
        context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))),
            new Point(rect.Left + 10, rect.Top + 6), new Point(rect.Right - 10, rect.Top + 6));

        var textColor = disabled ? Color.FromRgb(170, 176, 188) : Colors.White;
        var text = screen.IsLocal ? "This PC" : screen.Name;
        var title = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TitleTypeface, 13.5, new SolidColorBrush(textColor));
        var textX = rect.X + (rect.Width - title.Width) / 2;
        var textY = rect.Y + (rect.Height - title.Height) / 2 - 8;
        context.DrawText(title, new Point(textX, textY));

        var sub = $"{screen.OriginalWidth} × {screen.OriginalHeight}";
        if (disabled)
            sub += "  ·  disabled";
        var subText = new FormattedText(sub, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, SubTypeface, 11.5,
            new SolidColorBrush(Color.FromArgb(200, textColor.R, textColor.G, textColor.B)));
        context.DrawText(subText, new Point(rect.X + (rect.Width - subText.Width) / 2, textY + title.Height + 2));
    }

    /// <summary>
    /// This PC with several monitors: each one drawn where Windows has it inside the desktop's
    /// bounding box (dashed), the main display labelled.
    /// </summary>
    private void DrawLocalMonitors(DrawingContext context, Rect rect, MonitorLayout layout, Color fill, Color edge)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(110, edge.R, edge.G, edge.B)), 1, new DashStyle(new double[] { 4, 4 }, 0));
        context.DrawRectangle(null, outline, rect, 8, 8);

        var origin = layout.VirtualBounds.Location;
        var number = 0;
        foreach (var monitor in layout.Monitors)
        {
            number++;
            var b = monitor.Bounds;
            var m = new Rect(
                rect.X + (double)(b.Left - origin.X) / _scaleFactor,
                rect.Y + (double)(b.Top - origin.Y) / _scaleFactor,
                (double)b.Width / _scaleFactor,
                (double)b.Height / _scaleFactor).Deflate(1);

            var monitorFill = monitor.Primary ? fill : Color.FromArgb(170, fill.R, fill.G, fill.B);
            context.DrawRectangle(new SolidColorBrush(monitorFill), new Pen(new SolidColorBrush(edge), 1), m, 6, 6);

            var name = monitor.Primary ? $"This PC · {number} (main)" : $"This PC · {number}";
            var title = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TitleTypeface, 12.5, Brushes.White)
            {
                MaxTextWidth = Math.Max(20, m.Width - 8),
                TextAlignment = TextAlignment.Center,
                MaxLineCount = 2,
                Trimming = TextTrimming.CharacterEllipsis
            };
            var size = new FormattedText($"{b.Width} × {b.Height}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, SubTypeface, 11,
                new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)));

            var top = m.Y + (m.Height - title.Height - size.Height - 2) / 2;
            context.DrawText(title, new Point(m.X + 4, top));
            if (size.Width < m.Width - 8)
                context.DrawText(size, new Point(m.X + (m.Width - size.Width) / 2, top + title.Height + 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var position = e.GetPosition(this);
        foreach (var screen in _screens.AsEnumerable().Reverse())
        {
            if (screen.DisplayBounds.Contains(position) && !screen.IsLocal)
            {
                _selectedScreen = screen;
                _draggingScreen = screen;
                _dragOffset = position - screen.DisplayBounds.Position;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
        }

        _selectedScreen = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_draggingScreen != null)
        {
            var position = e.GetPosition(this);
            _draggingScreen.DisplayBounds = new Rect(position - _dragOffset, _draggingScreen.DisplayBounds.Size);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_draggingScreen != null)
            e.Pointer.Capture(null); // ends the drag through OnPointerCaptureLost
        EndDrag();
    }

    /// <summary>
    /// Capture ends on release, but also when the window loses focus or another control takes the
    /// pointer mid-drag; either way the screen snaps to the nearest edge instead of staying stuck.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_draggingScreen == null)
            return;
        var screen = _draggingScreen;
        _draggingScreen = null;
        SnapToEdge(screen);
        InvalidateVisual();
    }

    private void SnapToEdge(ScreenRect peer)
    {
        if (_localScreen == null || peer.Placement == null || Layout == null)
            return;

        var local = _localScreen.DisplayBounds;
        var peerBounds = peer.DisplayBounds;
        var dx = peerBounds.Center.X - local.Center.X;
        var dy = peerBounds.Center.Y - local.Center.Y;

        ScreenPosition position;
        double newX, newY;

        if (Math.Abs(dx) > Math.Abs(dy))
        {
            if (dx < 0)
            {
                position = ScreenPosition.Left;
                newX = local.Left - peerBounds.Width;
                newY = peerBounds.Y;
            }
            else
            {
                position = ScreenPosition.Right;
                newX = local.Right;
                newY = peerBounds.Y;
            }
        }
        else
        {
            if (dy < 0)
            {
                position = ScreenPosition.Top;
                newX = peerBounds.X;
                newY = local.Top - peerBounds.Height;
            }
            else
            {
                position = ScreenPosition.Bottom;
                newX = peerBounds.X;
                newY = local.Bottom;
            }
        }

        peer.DisplayBounds = new Rect(newX, newY, peerBounds.Width, peerBounds.Height);

        // Offset along the edge in real pixels; the other axis is unused for that edge.
        var horizontal = position is ScreenPosition.Left or ScreenPosition.Right;
        var offsetX = horizontal ? 0 : (int)Math.Round((newX - local.X) * _scaleFactor);
        var offsetY = horizontal ? (int)Math.Round((newY - local.Y) * _scaleFactor) : 0;

        // Dropping on an occupied edge swaps the two; redraw the one that moved out of the way.
        var swapped = Layout.MoveToEdge(peer.Placement, position, offsetX, offsetY);
        var swappedRect = swapped == null ? null : _screens.FirstOrDefault(s => s.Placement == swapped);
        if (swapped != null && swappedRect != null)
            PositionPeerScreen(swappedRect, swapped.Position, swapped.OffsetX, swapped.OffsetY);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_draggingScreen != null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;

        // Rebuild at a scale that fits the new size; drags are already in the placements.
        var selected = _selectedScreen?.Placement;
        InitializeScreens();
        _selectedScreen = selected == null ? null : _screens.FirstOrDefault(s => s.Placement == selected);
        InvalidateVisual();
    }

    private sealed class ScreenRect
    {
        public string Name { get; set; } = string.Empty;
        public bool IsLocal { get; set; }
        public PeerPlacement? Placement { get; set; }

        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public Rect DisplayBounds { get; set; }
    }
}
