namespace RoboMouse.App.Forms;

/// <summary>
/// Debug panel that shows forwarded motion and link latency while controlling a remote machine.
/// </summary>
public class DebugPanelForm : Form
{
    private readonly Label _statusLabel;
    private readonly Label _peerLabel;
    private readonly Label _peerPositionLabel;
    private readonly Label _rttLabel;
    private readonly Label _rateLabel;
    private readonly Label _deltaLabel;
    private readonly Label _totalLabel;
    private readonly Panel _directionIndicator;
    private readonly ListBox _historyList;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly List<string> _history = new(12);
    private readonly object _lock = new();

    private MouseDebugData? _latest;
    private int _samplesSinceTick;
    private int _samplesPerSecond;
    private long _totalDx;
    private long _totalDy;
    private int _lastDx;
    private int _lastDy;
    private bool _dirty;

    public DebugPanelForm()
    {
        Text = "RoboMouse Debug";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, 520);
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.White;

        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(screen.Right - Width - 10, screen.Top + 10);

        var padding = 10;
        var labelHeight = 24;
        var y = padding;

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

        _statusLabel = CreateLabel("Status: Idle", ref y, labelHeight, padding);
        _peerLabel = CreateLabel("Peer: None", ref y, labelHeight, padding);
        _peerPositionLabel = CreateLabel("Position: -", ref y, labelHeight, padding);

        y += 8;
        AddHeader("Link", ref y, padding);
        _rttLabel = CreateLabel("Round trip: -", ref y, labelHeight, padding);
        _rateLabel = CreateLabel("Motion rate: 0 /s", ref y, labelHeight, padding);

        y += 8;
        AddHeader("Motion (raw counts)", ref y, padding);
        _deltaLabel = CreateLabel("Last: (0, 0)", ref y, labelHeight, padding);
        _totalLabel = CreateLabel("Total: (0, 0)", ref y, labelHeight, padding);

        y += 8;
        var indicatorLabel = new Label
        {
            Text = "Direction:",
            Font = new Font("Segoe UI", 9),
            Location = new Point(padding, y + 30),
            Size = new Size(70, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(indicatorLabel);

        _directionIndicator = new Panel
        {
            Location = new Point(padding + 80, y),
            Size = new Size(80, 80),
            BackColor = Color.FromArgb(40, 40, 40)
        };
        _directionIndicator.Paint += DirectionIndicator_Paint;
        Controls.Add(_directionIndicator);
        y += 90;

        AddHeader("Recent samples", ref y, padding);
        _historyList = new ListBox
        {
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2 - 10, 120),
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Font = new Font("Consolas", 8),
            BorderStyle = BorderStyle.None
        };
        Controls.Add(_historyList);
        y += 125;

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

        // Raw input arrives up to 1000 times a second; repaint on a timer instead of per sample.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    private void AddHeader(string text, ref int y, int padding)
    {
        var header = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Location = new Point(padding, y),
            Size = new Size(Width - padding * 2, 20),
            ForeColor = Color.FromArgb(150, 150, 150)
        };
        Controls.Add(header);
        y += 22;
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

    /// <summary>
    /// Records a motion sample. Safe to call from any thread at any rate; the UI updates on a timer.
    /// </summary>
    public void UpdateData(MouseDebugData data)
    {
        lock (_lock)
        {
            _latest = data;
            _dirty = true;

            if (data.DeltaX != 0 || data.DeltaY != 0)
            {
                _samplesSinceTick++;
                _totalDx += data.DeltaX;
                _totalDy += data.DeltaY;
                _lastDx = data.DeltaX;
                _lastDy = data.DeltaY;

                _history.Insert(0, $"{GetDirectionArrow(data.DeltaX, data.DeltaY)} ({data.DeltaX,4:+0;-0;0},{data.DeltaY,4:+0;-0;0})  rtt {FormatRtt(data.RoundTripMs)}");
                if (_history.Count > 12)
                    _history.RemoveAt(12);
            }
        }
    }

    private int _ticksSinceRateUpdate;

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        MouseDebugData? data;
        List<string> history;
        int rate;
        long totalDx, totalDy;
        int lastDx, lastDy;

        lock (_lock)
        {
            _ticksSinceRateUpdate++;
            if (_ticksSinceRateUpdate >= 10)
            {
                _samplesPerSecond = _samplesSinceTick;
                _samplesSinceTick = 0;
                _ticksSinceRateUpdate = 0;
                _dirty = true;
            }

            if (!_dirty)
                return;
            _dirty = false;

            data = _latest;
            history = new List<string>(_history);
            rate = _samplesPerSecond;
            totalDx = _totalDx;
            totalDy = _totalDy;
            lastDx = _lastDx;
            lastDy = _lastDy;
        }

        var controlling = data?.IsControlling ?? false;
        _statusLabel.Text = $"Status: {(controlling ? "Controlling" : "Idle")}";
        _statusLabel.ForeColor = controlling ? Color.FromArgb(100, 255, 100) : Color.White;
        _peerLabel.Text = $"Peer: {data?.PeerName ?? "None"}";
        _peerPositionLabel.Text = $"Position: {data?.PeerPosition ?? "-"}";

        var rtt = data?.RoundTripMs ?? -1;
        _rttLabel.Text = $"Round trip: {FormatRtt(rtt)}";
        _rttLabel.ForeColor = rtt switch
        {
            < 0 => Color.Gray,
            < 5 => Color.FromArgb(100, 255, 100),
            < 20 => Color.FromArgb(255, 220, 100),
            _ => Color.FromArgb(255, 120, 100)
        };

        _rateLabel.Text = $"Motion rate: {rate} /s";
        _deltaLabel.Text = $"Last: ({lastDx:+0;-0;0}, {lastDy:+0;-0;0})";
        _totalLabel.Text = $"Total: ({totalDx}, {totalDy})";

        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        foreach (var entry in history)
            _historyList.Items.Add(entry);
        _historyList.EndUpdate();

        _directionIndicator.Invalidate();
    }

    private static string FormatRtt(int rtt) => rtt < 0 ? "-" : $"{rtt} ms";

    private void DirectionIndicator_Paint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var center = new PointF(_directionIndicator.Width / 2f, _directionIndicator.Height / 2f);
        var radius = Math.Min(_directionIndicator.Width, _directionIndicator.Height) / 2f - 5;

        using var bgBrush = new SolidBrush(Color.FromArgb(50, 50, 50));
        g.FillEllipse(bgBrush, center.X - radius, center.Y - radius, radius * 2, radius * 2);

        int dx, dy;
        lock (_lock)
        {
            dx = _lastDx;
            dy = _lastDy;
        }

        var magnitude = (float)Math.Sqrt(dx * dx + dy * dy);
        if (magnitude > 0)
        {
            var arrowLength = Math.Min(radius * 0.85f, radius * 0.3f + magnitude / 40f * radius * 0.5f);
            var endX = center.X + dx / magnitude * arrowLength;
            var endY = center.Y + dy / magnitude * arrowLength;

            using var pen = new Pen(Color.FromArgb(100, 180, 255), 3);
            pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;
            g.DrawLine(pen, center.X, center.Y, endX, endY);
        }

        using var centerBrush = new SolidBrush(Color.FromArgb(100, 180, 255));
        g.FillEllipse(centerBrush, center.X - 4, center.Y - 4, 8, 8);
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

    private void CopyButton_Click(object? sender, EventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== RoboMouse Debug ===");
        sb.AppendLine(_statusLabel.Text);
        sb.AppendLine(_peerLabel.Text);
        sb.AppendLine(_peerPositionLabel.Text);
        sb.AppendLine(_rttLabel.Text);
        sb.AppendLine(_rateLabel.Text);
        sb.AppendLine(_deltaLabel.Text);
        sb.AppendLine(_totalLabel.Text);
        sb.AppendLine();
        sb.AppendLine("=== Recent samples ===");
        lock (_lock)
        {
            foreach (var entry in _history)
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

    /// <summary>
    /// Shows the panel, positioning it on the appropriate edge of the screen.
    /// </summary>
    public void ShowOnEdge(string? edge = null)
    {
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Data structure for debug panel updates.
/// </summary>
public class MouseDebugData
{
    public bool IsControlling { get; set; }
    public string? PeerName { get; set; }
    public string? PeerPosition { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int RoundTripMs { get; set; }
}
