using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.Windows;

/// <summary>
/// Debug panel that shows forwarded motion and link latency while controlling a remote machine.
/// </summary>
public sealed class DebugPanelWindow : Window
{
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _peerLabel;
    private readonly TextBlock _peerPositionLabel;
    private readonly TextBlock _rttLabel;
    private readonly TextBlock _rateLabel;
    private readonly TextBlock _deltaLabel;
    private readonly TextBlock _totalLabel;
    private readonly DirectionIndicator _directionIndicator;
    private readonly ListBox _historyList;
    private readonly DispatcherTimer _refreshTimer;

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
    private int _ticksSinceRateUpdate;

    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly IBrush BoxBrush = new SolidColorBrush(Color.FromRgb(40, 40, 40));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(150, 150, 150));
    private static readonly IBrush BlueBrush = new SolidColorBrush(Color.FromRgb(100, 180, 255));

    public DebugPanelWindow()
    {
        Title = "RoboMouse Debug";
        Width = 320;
        Height = 540;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = PanelBrush;
        Foreground = Brushes.White;
        FontFamily = Ui.BodyFont;
        Icon = Ui.AppIcon();

        var stack = new StackPanel { Margin = new Thickness(10), Spacing = 2 };

        stack.Children.Add(new TextBlock
        {
            Text = "Mouse Debug Info",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = BlueBrush,
            Margin = new Thickness(0, 0, 0, 6)
        });

        _statusLabel = AddValue(stack, "Status: Idle");
        _peerLabel = AddValue(stack, "Peer: None");
        _peerPositionLabel = AddValue(stack, "Position: -");

        AddHeader(stack, "Link");
        _rttLabel = AddValue(stack, "Round trip: -");
        _rateLabel = AddValue(stack, "Motion rate: 0 /s");

        AddHeader(stack, "Motion (raw counts)");
        _deltaLabel = AddValue(stack, "Last: (0, 0)");
        _totalLabel = AddValue(stack, "Total: (0, 0)");

        var directionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Spacing = 10 };
        directionRow.Children.Add(new TextBlock { Text = "Direction:", Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, Width = 70 });
        _directionIndicator = new DirectionIndicator { Width = 80, Height = 80 };
        directionRow.Children.Add(_directionIndicator);
        stack.Children.Add(directionRow);

        AddHeader(stack, "Recent samples");
        _historyList = new ListBox
        {
            Height = 150,
            Background = BoxBrush,
            Foreground = Brushes.White,
            FontFamily = Ui.MonoFont,
            FontSize = 11,
            BorderThickness = new Thickness(0)
        };
        stack.Children.Add(_historyList);

        var copyButton = new Button
        {
            Content = "Copy to Clipboard",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
            Foreground = Brushes.White
        };
        copyButton.Click += CopyButton_Click;
        stack.Children.Add(copyButton);

        Content = stack;

        // Raw input arrives up to 1000 times a second; repaint on a timer instead of per sample.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    private static void AddHeader(StackPanel stack, string text)
    {
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Foreground = DimBrush,
            Margin = new Thickness(0, 8, 0, 2)
        });
    }

    private static TextBlock AddValue(StackPanel stack, string text)
    {
        var label = new TextBlock { Text = text, FontFamily = Ui.MonoFont, FontSize = 13, Foreground = Brushes.White };
        stack.Children.Add(label);
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
        _statusLabel.Foreground = controlling ? new SolidColorBrush(Color.FromRgb(100, 255, 100)) : Brushes.White;
        _peerLabel.Text = $"Peer: {data?.PeerName ?? "None"}";
        _peerPositionLabel.Text = $"Position: {data?.PeerPosition ?? "-"}";

        var rtt = data?.RoundTripMs ?? -1;
        _rttLabel.Text = $"Round trip: {FormatRtt(rtt)}";
        _rttLabel.Foreground = rtt switch
        {
            < 0 => Brushes.Gray,
            < 5 => new SolidColorBrush(Color.FromRgb(100, 255, 100)),
            < 20 => new SolidColorBrush(Color.FromRgb(255, 220, 100)),
            _ => new SolidColorBrush(Color.FromRgb(255, 120, 100))
        };

        _rateLabel.Text = $"Motion rate: {rate} /s";
        _deltaLabel.Text = $"Last: ({lastDx:+0;-0;0}, {lastDy:+0;-0;0})";
        _totalLabel.Text = $"Total: ({totalDx}, {totalDy})";

        _historyList.ItemsSource = history;

        _directionIndicator.Set(lastDx, lastDy);
    }

    private static string FormatRtt(int rtt) => rtt < 0 ? "-" : $"{rtt} ms";

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

    private async void CopyButton_Click(object? sender, EventArgs e)
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
            if (Clipboard != null)
                await Clipboard.SetTextAsync(sb.ToString());
            if (sender is Button btn)
            {
                var originalText = btn.Content;
                btn.Content = "Copied!";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) => { btn.Content = originalText; timer.Stop(); };
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
        var screen = ScreenInfo.GetPrimaryWorkingArea();
        var scaling = Screens.Primary?.Scaling ?? 1.0;
        var width = (int)(Width * scaling);
        var height = (int)(Height * scaling);

        Position = edge?.ToLowerInvariant() switch
        {
            "left" => new PixelPoint(screen.Left + 10, screen.Top + 10),
            "bottom" => new PixelPoint(screen.Right - width - 10, screen.Bottom - height - 10),
            _ => new PixelPoint(screen.Right - width - 10, screen.Top + 10)
        };

        if (!IsVisible)
            Show();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            // The user closed it: hide instead so it can come back while the debug option is on.
            e.Cancel = true;
            Hide();
            return;
        }
        _refreshTimer.Stop();
        base.OnClosing(e);
    }

    /// <summary>Draws an arrow showing the direction of the last motion sample.</summary>
    private sealed class DirectionIndicator : Control
    {
        private int _dx;
        private int _dy;

        public void Set(int dx, int dy)
        {
            _dx = dx;
            _dy = dy;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(BoxBrush, new Rect(Bounds.Size));

            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 5;
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(50, 50, 50)), null, center, radius, radius);

            var magnitude = Math.Sqrt((double)_dx * _dx + (double)_dy * _dy);
            if (magnitude > 0)
            {
                var arrowLength = Math.Min(radius * 0.85, radius * 0.3 + magnitude / 40 * radius * 0.5);
                var end = new Point(center.X + _dx / magnitude * arrowLength, center.Y + _dy / magnitude * arrowLength);
                var pen = new Pen(BlueBrush, 3);
                context.DrawLine(pen, center, end);
                // Arrow head
                var angle = Math.Atan2(end.Y - center.Y, end.X - center.X);
                var head = 8.0;
                var left = new Point(end.X - head * Math.Cos(angle - Math.PI / 6), end.Y - head * Math.Sin(angle - Math.PI / 6));
                var right = new Point(end.X - head * Math.Cos(angle + Math.PI / 6), end.Y - head * Math.Sin(angle + Math.PI / 6));
                context.DrawLine(pen, end, left);
                context.DrawLine(pen, end, right);
            }

            context.DrawEllipse(BlueBrush, null, center, 4, 4);
        }
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
