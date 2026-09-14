using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Forms;

/// <summary>
/// Custom panel for visual screen layout editing.
/// </summary>
public class ScreenLayoutPanel : Panel
{
    private readonly AppSettings _settings;
    private readonly List<ScreenRect> _screens = new();
    private ScreenRect? _localScreen;
    private ScreenRect? _selectedScreen;
    private ScreenRect? _draggingScreen;
    private Point _dragOffset;

    // Real pixels per canvas pixel. Chosen on each rebuild so the whole arrangement fits the canvas.
    private int ScaleFactor = 8;
    private const int CanvasMargin = 36;

    public ScreenLayoutPanel(AppSettings settings)
    {
        _settings = settings;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(24, 30, 44);

        InitializeScreens();
    }

    /// <summary>Rebuilds the canvas from the current peer list (after peers are added, edited or removed).</summary>
    public void Reload()
    {
        _selectedScreen = null;
        _draggingScreen = null;
        InitializeScreens();
        Invalidate();
    }

    private void InitializeScreens()
    {
        _screens.Clear();

        // The whole desktop (all monitors), since edges are detected on the virtual screen.
        var localBounds = SystemInformation.VirtualScreen;
        ScaleFactor = FitScale(localBounds);

        _localScreen = new ScreenRect
        {
            Name = "This PC",
            IsLocal = true,
            OriginalBounds = localBounds,
            DisplayBounds = new Rectangle(0, 0, localBounds.Width / ScaleFactor, localBounds.Height / ScaleFactor)
        };
        _screens.Add(_localScreen);

        foreach (var peer in _settings.Peers)
        {
            var peerRect = new ScreenRect
            {
                Name = peer.Name,
                PeerConfig = peer,
                OriginalBounds = new Rectangle(0, 0, peer.ScreenWidth, peer.ScreenHeight),
                DisplayBounds = new Rectangle(0, 0, peer.ScreenWidth / ScaleFactor, peer.ScreenHeight / ScaleFactor)
            };

            PositionPeerScreen(peerRect, peer.Position, peer.OffsetX, peer.OffsetY);
            _screens.Add(peerRect);
        }

        CenterScreens();
    }

    /// <summary>
    /// Smallest scale divisor at which the local screen plus every peer (at its configured edge and
    /// offset) fits inside the canvas with a margin. Never larger than 1:1.
    /// </summary>
    private int FitScale(Rectangle local)
    {
        var minX = 0; var minY = 0; var maxX = local.Width; var maxY = local.Height;
        foreach (var peer in _settings.Peers)
        {
            Rectangle r = peer.Position switch
            {
                ScreenPosition.Left => new Rectangle(-peer.ScreenWidth, peer.OffsetY, peer.ScreenWidth, peer.ScreenHeight),
                ScreenPosition.Right => new Rectangle(local.Width, peer.OffsetY, peer.ScreenWidth, peer.ScreenHeight),
                ScreenPosition.Top => new Rectangle(peer.OffsetX, -peer.ScreenHeight, peer.ScreenWidth, peer.ScreenHeight),
                _ => new Rectangle(peer.OffsetX, local.Height, peer.ScreenWidth, peer.ScreenHeight)
            };
            minX = Math.Min(minX, r.Left); minY = Math.Min(minY, r.Top);
            maxX = Math.Max(maxX, r.Right); maxY = Math.Max(maxY, r.Bottom);
        }

        // Leave room on every side so a screen can be dragged to an empty edge.
        var extentW = (maxX - minX) + local.Width;
        var extentH = (maxY - minY) + local.Height;
        var availW = Math.Max(100, Width - CanvasMargin * 2);
        var availH = Math.Max(100, Height - CanvasMargin * 2);
        var scale = Math.Max((double)extentW / availW, (double)extentH / availH);
        return Math.Max(1, (int)Math.Ceiling(scale));
    }

    private void PositionPeerScreen(ScreenRect peer, ScreenPosition position, int offsetX, int offsetY)
    {
        if (_localScreen == null)
            return;

        var local = _localScreen.DisplayBounds;
        var scaledOffsetX = offsetX / ScaleFactor;
        var scaledOffsetY = offsetY / ScaleFactor;

        switch (position)
        {
            case ScreenPosition.Left:
                peer.DisplayBounds = new Rectangle(
                    local.Left - peer.DisplayBounds.Width,
                    local.Top + scaledOffsetY,
                    peer.DisplayBounds.Width,
                    peer.DisplayBounds.Height);
                break;

            case ScreenPosition.Right:
                peer.DisplayBounds = new Rectangle(
                    local.Right,
                    local.Top + scaledOffsetY,
                    peer.DisplayBounds.Width,
                    peer.DisplayBounds.Height);
                break;

            case ScreenPosition.Top:
                peer.DisplayBounds = new Rectangle(
                    local.Left + scaledOffsetX,
                    local.Top - peer.DisplayBounds.Height,
                    peer.DisplayBounds.Width,
                    peer.DisplayBounds.Height);
                break;

            case ScreenPosition.Bottom:
                peer.DisplayBounds = new Rectangle(
                    local.Left + scaledOffsetX,
                    local.Bottom,
                    peer.DisplayBounds.Width,
                    peer.DisplayBounds.Height);
                break;
        }
    }

    private void CenterScreens()
    {
        if (_screens.Count == 0)
            return;

        // Find bounding box of all screens
        var minX = _screens.Min(s => s.DisplayBounds.Left);
        var minY = _screens.Min(s => s.DisplayBounds.Top);
        var maxX = _screens.Max(s => s.DisplayBounds.Right);
        var maxY = _screens.Max(s => s.DisplayBounds.Bottom);

        var totalWidth = maxX - minX;
        var totalHeight = maxY - minY;

        var offsetX = (Width - totalWidth) / 2 - minX;
        var offsetY = (Height - totalHeight) / 2 - minY;

        foreach (var screen in _screens)
        {
            screen.DisplayBounds = new Rectangle(
                screen.DisplayBounds.X + offsetX,
                screen.DisplayBounds.Y + offsetY,
                screen.DisplayBounds.Width,
                screen.DisplayBounds.Height);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        // Dotted grid so the canvas reads as a workspace.
        using (var dotBrush = new SolidBrush(Color.FromArgb(48, 56, 74)))
        {
            for (var y = 12; y < Height; y += 24)
                for (var x = 12; x < Width; x += 24)
                    g.FillRectangle(dotBrush, x, y, 2, 2);
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        foreach (var screen in _screens)
        {
            DrawScreen(g, screen);
        }
    }

    private void DrawScreen(Graphics g, ScreenRect screen)
    {
        var rect = screen.DisplayBounds;
        var disabled = screen.PeerConfig is { Enabled: false };
        var selected = screen == _selectedScreen;

        Color fill, edge;
        if (screen.IsLocal)
        {
            fill = Color.FromArgb(30, 100, 230);
            edge = Color.FromArgb(120, 170, 255);
        }
        else if (disabled)
        {
            fill = Color.FromArgb(52, 58, 70);
            edge = Color.FromArgb(90, 98, 112);
        }
        else
        {
            fill = selected ? Color.FromArgb(70, 96, 140) : Color.FromArgb(56, 72, 104);
            edge = selected ? Color.White : Color.FromArgb(120, 140, 175);
        }

        using var path = RoundedRect(rect, 8);
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        using (var pen = new Pen(edge, selected ? 2 : 1))
            g.DrawPath(pen, path);

        // Bezel line to hint "screen"
        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255)))
            g.DrawLine(pen, rect.Left + 10, rect.Top + 6, rect.Right - 10, rect.Top + 6);

        var textColor = disabled ? Color.FromArgb(170, 176, 188) : Color.White;
        using var font = new Font("Segoe UI Semibold", 10);
        using var textBrush = new SolidBrush(textColor);
        var text = screen.IsLocal ? "This PC" : screen.Name;
        var textSize = g.MeasureString(text, font);
        var textX = rect.X + (rect.Width - textSize.Width) / 2;
        var textY = rect.Y + (rect.Height - textSize.Height) / 2 - 8;
        g.DrawString(text, font, textBrush, textX, textY);

        using var smallFont = new Font("Segoe UI", 8.5f);
        using var subBrush = new SolidBrush(Color.FromArgb(200, textColor));
        var sub = $"{screen.OriginalBounds.Width} × {screen.OriginalBounds.Height}";
        if (disabled)
            sub += "  ·  disabled";
        var subSize = g.MeasureString(sub, smallFont);
        g.DrawString(sub, smallFont, subBrush,
            rect.X + (rect.Width - subSize.Width) / 2,
            textY + textSize.Height + 2);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // Find clicked screen
        foreach (var screen in _screens.AsEnumerable().Reverse())
        {
            if (screen.DisplayBounds.Contains(e.Location) && !screen.IsLocal)
            {
                _selectedScreen = screen;
                _draggingScreen = screen;
                _dragOffset = new Point(
                    e.X - screen.DisplayBounds.X,
                    e.Y - screen.DisplayBounds.Y);
                Invalidate();
                return;
            }
        }

        _selectedScreen = null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_draggingScreen != null)
        {
            _draggingScreen.DisplayBounds = new Rectangle(
                e.X - _dragOffset.X,
                e.Y - _dragOffset.Y,
                _draggingScreen.DisplayBounds.Width,
                _draggingScreen.DisplayBounds.Height);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_draggingScreen != null)
        {
            SnapToEdge(_draggingScreen);
            _draggingScreen = null;
            Invalidate();
        }
    }

    private void SnapToEdge(ScreenRect peer)
    {
        if (_localScreen == null || peer.PeerConfig == null)
            return;

        var local = _localScreen.DisplayBounds;
        var peerBounds = peer.DisplayBounds;
        var centerX = peerBounds.X + peerBounds.Width / 2;
        var centerY = peerBounds.Y + peerBounds.Height / 2;

        // Determine which edge to snap to based on position
        var localCenterX = local.X + local.Width / 2;
        var localCenterY = local.Y + local.Height / 2;

        var dx = centerX - localCenterX;
        var dy = centerY - localCenterY;

        ScreenPosition position;
        int newX, newY;

        if (Math.Abs(dx) > Math.Abs(dy))
        {
            // Snap to left or right
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
            // Snap to top or bottom
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

        peer.DisplayBounds = new Rectangle(newX, newY, peerBounds.Width, peerBounds.Height);
        peer.PeerConfig.Position = position;
    }

    public void SaveLayout()
    {
        if (_localScreen == null)
            return;

        foreach (var screen in _screens)
        {
            if (screen.IsLocal || screen.PeerConfig == null)
                continue;

            var local = _localScreen.DisplayBounds;
            var peer = screen.DisplayBounds;

            // Calculate offsets based on position
            switch (screen.PeerConfig.Position)
            {
                case ScreenPosition.Left:
                case ScreenPosition.Right:
                    screen.PeerConfig.OffsetY = (peer.Y - local.Y) * ScaleFactor;
                    screen.PeerConfig.OffsetX = 0;
                    break;

                case ScreenPosition.Top:
                case ScreenPosition.Bottom:
                    screen.PeerConfig.OffsetX = (peer.X - local.X) * ScaleFactor;
                    screen.PeerConfig.OffsetY = 0;
                    break;
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_draggingScreen != null || Width <= 0 || Height <= 0)
            return;

        // Keep any unsaved drags, then rebuild at a scale that fits the new size.
        SaveLayout();
        var selected = _selectedScreen?.PeerConfig;
        InitializeScreens();
        _selectedScreen = _screens.FirstOrDefault(s => s.PeerConfig == selected);
        Invalidate();
    }

    private class ScreenRect
    {
        public string Name { get; set; } = string.Empty;
        public bool IsLocal { get; set; }
        public PeerConfig? PeerConfig { get; set; }
        public Rectangle OriginalBounds { get; set; }
        public Rectangle DisplayBounds { get; set; }
    }
}
