namespace RoboMouse.App.Forms;

/// <summary>
/// Debug panel that shows mouse movement information when controlling a remote machine.
/// </summary>
public class DebugPanelForm : Form
{
    private readonly Label _statusLabel;
    private readonly Label _prevPosLabel;
    private readonly Label _positionLabel;
    private readonly Label _virtualPosLabel;
    private readonly Label _deltaLabel;
    private readonly Label _velocityLabel;
    private readonly Label _directionLabel;
    private readonly Panel _directionIndicator;
    private readonly Label _peerLabel;
    private readonly Label _remotePosLabel;
    private readonly Label _peerScreenLabel;
    private readonly Label _captureLabel;
    private readonly Label _peerPositionLabel;
    private readonly ListBox _historyList;

    private float _lastVelocityX;
    private float _lastVelocityY;
    private readonly List<string> _history = new(10);
    private MouseDebugData? _lastData;

    public DebugPanelForm()
    {
        Text = "RoboMouse Debug";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, 780);
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;

        // Position in top-right corner of screen
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(screen.Right - Width - 10, screen.Top + 10);

        var padding = 10;
        var labelHeight = 24;
        var y = padding;

        // Title
        var titleLabel = new Label
        {
            Text = "Mouse Debug Info",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, 28),
            ForeColor = Color.FromArgb(100, 180, 255)
        };
        Controls.Add(titleLabel);
        y += 32;

        // Status
        _statusLabel = CreateLabel("Status: Idle", ref y, labelHeight, padding);

        // Connected peer
        _peerLabel = CreateLabel("Peer: None", ref y, labelHeight, padding);

        y += 8; // spacing

        // Position section
        var posHeader = new Label
        {
            Text = "Position",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(posHeader);
        y += 22;

        _prevPosLabel = CreateLabel("Prev: (0, 0)", ref y, labelHeight, padding);
        _positionLabel = CreateLabel("Curr: (0, 0)", ref y, labelHeight, padding);
        _virtualPosLabel = CreateLabel("Virtual: (0.00, 0.00)", ref y, labelHeight, padding);
        _remotePosLabel = CreateLabel("Remote: (0, 0)", ref y, labelHeight, padding);

        y += 8; // spacing

        // Peer info section
        var peerHeader = new Label
        {
            Text = "Peer Info",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(peerHeader);
        y += 22;

        _peerPositionLabel = CreateLabel("Position: -", ref y, labelHeight, padding);
        _peerScreenLabel = CreateLabel("Screen: 0x0", ref y, labelHeight, padding);
        _captureLabel = CreateLabel("Capture: (0, 0)", ref y, labelHeight, padding);

        y += 8; // spacing

        // Movement section
        var moveHeader = new Label
        {
            Text = "Movement",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(moveHeader);
        y += 22;

        _deltaLabel = CreateLabel("Delta: (0, 0)", ref y, labelHeight, padding);
        _velocityLabel = CreateLabel("Velocity: 0 px/s", ref y, labelHeight, padding);
        _directionLabel = CreateLabel("Direction: -", ref y, labelHeight, padding);

        // Direction indicator (visual arrow)
        y += 8;
        var indicatorLabel = new Label
        {
            Text = "Direction:",
            Font = new Font("Segoe UI", 9),
            Location = new Point(padding, y),
            Size = new Size(70, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(indicatorLabel);

        _directionIndicator = new Panel
        {
            Location = new Point(padding + 80, y - 20),
            Size = new Size(80, 80),
            BackColor = Color.FromArgb(40, 40, 40)
        };
        _directionIndicator.Paint += DirectionIndicator_Paint;
        Controls.Add(_directionIndicator);

        y += 70;

        // History section
        var historyHeader = new Label
        {
            Text = "Last 10 Movements",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(historyHeader);
        y += 22;

        _historyList = new ListBox
        {
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2 - 10, 140),
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Font = new Font("Consolas", 8),
            BorderStyle = BorderStyle.None
        };
        Controls.Add(_historyList);
        y += 145;

        // Copy button
        var copyButton = new Button
        {
            Text = "Copy to Clipboard",
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2 - 10, 28),
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        copyButton.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
        copyButton.Click += CopyButton_Click;
        Controls.Add(copyButton);
    }

    private void CopyButton_Click(object? sender, EventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== RoboMouse Debug ===");
        sb.AppendLine(_statusLabel.Text);
        sb.AppendLine(_peerLabel.Text);
        sb.AppendLine();
        sb.AppendLine("=== Position ===");
        sb.AppendLine(_prevPosLabel.Text);
        sb.AppendLine(_positionLabel.Text);
        sb.AppendLine(_virtualPosLabel.Text);
        sb.AppendLine(_remotePosLabel.Text);
        sb.AppendLine();
        sb.AppendLine("=== Peer Info ===");
        sb.AppendLine(_peerPositionLabel.Text);
        sb.AppendLine(_peerScreenLabel.Text);
        sb.AppendLine(_captureLabel.Text);
        sb.AppendLine();
        sb.AppendLine("=== Movement ===");
        sb.AppendLine(_deltaLabel.Text);
        sb.AppendLine(_velocityLabel.Text);
        sb.AppendLine(_directionLabel.Text);
        sb.AppendLine();
        sb.AppendLine("=== History ===");
        foreach (var entry in _history)
        {
            sb.AppendLine(entry);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            if (sender is Button btn)
            {
                var originalText = btn.Text;
                btn.Text = "Copied!";
                var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += (_, _) => { btn.Text = originalText; timer.Stop(); timer.Dispose(); };
                timer.Start();
            }
        }
        catch { }
    }

    private Label CreateLabel(string text, ref int y, int height, int padding)
    {
        var label = new Label
        {
            Text = text,
            Font = new Font("Consolas", 10),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, height),
            ForeColor = Color.White
        };
        Controls.Add(label);
        y += height;
        return label;
    }

    private void DirectionIndicator_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var center = new PointF(_directionIndicator.Width / 2f, _directionIndicator.Height / 2f);
        var radius = Math.Min(_directionIndicator.Width, _directionIndicator.Height) / 2f - 5;

        // Draw circle background
        using var bgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));
        g.FillEllipse(bgBrush, center.X - radius, center.Y - radius, radius * 2, radius * 2);

        // Draw direction arrow if there's velocity
        var speed = (float)Math.Sqrt(_lastVelocityX * _lastVelocityX + _lastVelocityY * _lastVelocityY);
        if (speed > 10)
        {
            // Normalize velocity to get direction
            var dirX = _lastVelocityX / speed;
            var dirY = _lastVelocityY / speed;

            // Scale arrow length based on speed (capped)
            var arrowLength = Math.Min(radius * 0.8f, radius * 0.3f + speed / 500f * radius * 0.5f);

            var endX = center.X + dirX * arrowLength;
            var endY = center.Y + dirY * arrowLength;

            // Arrow color based on speed
            var colorIntensity = Math.Min(255, (int)(speed / 20));
            using var pen = new Pen(Color.FromArgb(colorIntensity, 255 - colorIntensity / 2, 100), 3);
            pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;

            g.DrawLine(pen, center.X, center.Y, endX, endY);

            // Draw center dot
            using var centerBrush = new SolidBrush(Color.FromArgb(100, 180, 255));
            g.FillEllipse(centerBrush, center.X - 4, center.Y - 4, 8, 8);
        }
        else
        {
            // Just draw center dot when idle
            using var centerBrush = new SolidBrush(Color.FromArgb(100, 100, 100));
            g.FillEllipse(centerBrush, center.X - 4, center.Y - 4, 8, 8);
        }
    }

    /// <summary>
    /// Updates the debug panel with current mouse movement data.
    /// </summary>
    public void UpdateData(MouseDebugData data)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateData(data));
            return;
        }

        if (data.IsIgnored)
        {
            _statusLabel.Text = "Status: IGNORED (warp)";
            _statusLabel.ForeColor = Color.FromArgb(255, 165, 0); // Orange for ignored
        }
        else
        {
            _statusLabel.Text = $"Status: {(data.IsControlling ? "Controlling" : "Idle")}";
            _statusLabel.ForeColor = data.IsControlling ? Color.FromArgb(100, 255, 100) : Color.White;
        }

        _peerLabel.Text = $"Peer: {data.PeerName ?? "None"}";

        _prevPosLabel.Text = $"Prev: ({data.PrevX}, {data.PrevY})";
        _positionLabel.Text = $"Curr: ({data.LocalX}, {data.LocalY})";
        _virtualPosLabel.Text = $"Virtual: ({data.VirtualX:F3}, {data.VirtualY:F3})";
        _remotePosLabel.Text = $"Remote: ({data.RemoteX}, {data.RemoteY})";

        _peerPositionLabel.Text = $"Position: {data.PeerPosition ?? "-"}";
        _peerScreenLabel.Text = $"Screen: {data.PeerScreenWidth}x{data.PeerScreenHeight}";
        _captureLabel.Text = $"Capture: ({data.CaptureX}, {data.CaptureY})";

        _deltaLabel.Text = $"Delta: ({data.DeltaX:+0;-0;0}, {data.DeltaY:+0;-0;0})";
        _deltaLabel.ForeColor = data.IsIgnored ? Color.FromArgb(255, 165, 0) : Color.White;

        var speed = (float)Math.Sqrt(data.VelocityX * data.VelocityX + data.VelocityY * data.VelocityY);
        _velocityLabel.Text = $"Velocity: {speed:F0} px/s";

        // Determine direction
        var direction = GetDirectionString(data.VelocityX, data.VelocityY);
        _directionLabel.Text = $"Direction: {direction}";

        _lastVelocityX = data.VelocityX;
        _lastVelocityY = data.VelocityY;
        _lastData = data;
        _directionIndicator.Invalidate();

        // Add to history (only non-ignored movements with actual delta)
        if (!data.IsIgnored && (data.DeltaX != 0 || data.DeltaY != 0))
        {
            var arrow = GetDirectionArrow(data.DeltaX, data.DeltaY);
            var historyEntry = $"{arrow} Δ({data.DeltaX:+00;-00},{data.DeltaY:+00;-00}) v{speed:000} →({data.RemoteX,4},{data.RemoteY,4})";

            _history.Insert(0, historyEntry);
            if (_history.Count > 10)
                _history.RemoveAt(10);

            _historyList.Items.Clear();
            foreach (var entry in _history)
                _historyList.Items.Add(entry);
        }
    }

    private static string GetDirectionArrow(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return "·";
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        return angle switch
        {
            >= -22.5 and < 22.5 => "→",
            >= 22.5 and < 67.5 => "↘",
            >= 67.5 and < 112.5 => "↓",
            >= 112.5 and < 157.5 => "↙",
            >= 157.5 or < -157.5 => "←",
            >= -157.5 and < -112.5 => "↖",
            >= -112.5 and < -67.5 => "↑",
            >= -67.5 and < -22.5 => "↗",
            _ => "?"
        };
    }

    private static string GetDirectionString(float vx, float vy)
    {
        var speed = (float)Math.Sqrt(vx * vx + vy * vy);
        if (speed < 10)
            return "Stationary";

        var angle = Math.Atan2(vy, vx) * 180 / Math.PI;

        return angle switch
        {
            >= -22.5f and < 22.5f => "Right →",
            >= 22.5f and < 67.5f => "Down-Right ↘",
            >= 67.5f and < 112.5f => "Down ↓",
            >= 112.5f and < 157.5f => "Down-Left ↙",
            >= 157.5f or < -157.5f => "Left ←",
            >= -157.5f and < -112.5f => "Up-Left ↖",
            >= -112.5f and < -67.5f => "Up ↑",
            >= -67.5f and < -22.5f => "Up-Right ↗",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Shows the panel, positioning it on the appropriate edge of the screen.
    /// </summary>
    public void ShowOnEdge(string? edge = null)
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

        // Position based on which edge the mouse is going to
        Location = edge?.ToLower() switch
        {
            "left" => new Point(screen.Left + 10, screen.Top + 10),
            "right" => new Point(screen.Right - Width - 10, screen.Top + 10),
            "top" => new Point(screen.Right - Width - 10, screen.Top + 10),
            "bottom" => new Point(screen.Right - Width - 10, screen.Bottom - Height - 10),
            _ => new Point(screen.Right - Width - 10, screen.Top + 10)
        };

        if (!Visible)
        {
            Show();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }
}

/// <summary>
/// Data structure for debug panel updates.
/// </summary>
public class MouseDebugData
{
    public bool IsControlling { get; set; }
    public string? PeerName { get; set; }
    public int LocalX { get; set; }
    public int LocalY { get; set; }
    public int PrevX { get; set; }
    public int PrevY { get; set; }
    public float VirtualX { get; set; }
    public float VirtualY { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public bool IsIgnored { get; set; }

    // Extra debug info
    public int RemoteX { get; set; }
    public int RemoteY { get; set; }
    public int PeerScreenWidth { get; set; }
    public int PeerScreenHeight { get; set; }
    public int CaptureX { get; set; }
    public int CaptureY { get; set; }
    public string? PeerPosition { get; set; }
    public float InitialVirtualX { get; set; }
    public float InitialVirtualY { get; set; }
}
