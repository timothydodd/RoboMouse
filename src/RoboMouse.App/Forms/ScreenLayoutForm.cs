using RoboMouse.Core;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Forms;

/// <summary>
/// Visual screen layout editor.
/// </summary>
public partial class ScreenLayoutForm : Form
{
    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;
    private readonly ScreenLayoutPanel _layoutPanel;

    public ScreenLayoutForm(AppSettings settings, RoboMouseService service)
    {
        _settings = settings;
        _service = service;
        _layoutPanel = new ScreenLayoutPanel(settings);

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "Screen Layout";
        Size = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;

        // Instructions
        var instructionLabel = new Label
        {
            Text = "Drag peer screens to position them relative to your local screen. " +
                   "Click a peer to select it and view/edit its settings.",
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(240, 240, 240)
        };
        Controls.Add(instructionLabel);

        // Layout panel
        _layoutPanel.Dock = DockStyle.Fill;
        _layoutPanel.BackColor = Color.FromArgb(30, 30, 30);
        Controls.Add(_layoutPanel);

        // Buttons
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50
        };

        var saveButton = new Button
        {
            Text = "Save Layout",
            Width = 100,
            Height = 30,
            Location = new Point(Width - 230, 10)
        };
        saveButton.Click += OnSaveClick;

        var closeButton = new Button
        {
            Text = "Close",
            Width = 80,
            Height = 30,
            Location = new Point(Width - 110, 10)
        };
        closeButton.Click += (s, e) => Close();

        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(closeButton);
        Controls.Add(buttonPanel);

        // Bring layout panel to front (above instruction label)
        _layoutPanel.BringToFront();
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        _layoutPanel.SaveLayout();
        _settings.Save();
        MessageBox.Show("Layout saved.", "RoboMouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

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

    private const int ScaleFactor = 8; // ScaleFactor down screens for display

    public ScreenLayoutPanel(AppSettings settings)
    {
        _settings = settings;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        InitializeScreens();
    }

    private void InitializeScreens()
    {
        _screens.Clear();

        // Add local screen at center
        var localBounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        _localScreen = new ScreenRect
        {
            Name = "This PC",
            IsLocal = true,
            OriginalBounds = localBounds,
            DisplayBounds = new Rectangle(0, 0, localBounds.Width / ScaleFactor, localBounds.Height / ScaleFactor)
        };
        _screens.Add(_localScreen);

        // Add configured peers
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
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        foreach (var screen in _screens)
        {
            DrawScreen(g, screen);
        }
    }

    private void DrawScreen(Graphics g, ScreenRect screen)
    {
        var rect = screen.DisplayBounds;

        // Background
        Color bgColor;
        if (screen.IsLocal)
            bgColor = Color.FromArgb(40, 100, 160);
        else if (screen == _selectedScreen)
            bgColor = Color.FromArgb(100, 140, 180);
        else
            bgColor = Color.FromArgb(60, 80, 100);

        using var brush = new SolidBrush(bgColor);
        g.FillRectangle(brush, rect);

        // Border
        var borderColor = screen == _selectedScreen ? Color.White : Color.FromArgb(100, 130, 160);
        using var pen = new Pen(borderColor, screen == _selectedScreen ? 2 : 1);
        g.DrawRectangle(pen, rect);

        // Label
        using var font = new Font("Segoe UI", 10, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        var text = screen.Name;
        if (screen.IsLocal)
            text += " (Local)";

        var textSize = g.MeasureString(text, font);
        var textX = rect.X + (rect.Width - textSize.Width) / 2;
        var textY = rect.Y + (rect.Height - textSize.Height) / 2;
        g.DrawString(text, font, textBrush, textX, textY);

        // Resolution
        using var smallFont = new Font("Segoe UI", 8);
        var resText = $"{screen.OriginalBounds.Width}x{screen.OriginalBounds.Height}";
        var resSize = g.MeasureString(resText, smallFont);
        g.DrawString(resText, smallFont, textBrush,
            rect.X + (rect.Width - resSize.Width) / 2,
            textY + textSize.Height + 2);
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
        CenterScreens();
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
