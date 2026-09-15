using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RoboMouse.App.ViewModels;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.Views;

/// <summary>
/// Debug panel that shows forwarded motion and link latency while controlling a remote machine.
/// Closing it hides it; the tray controller decides when it is shown.
/// </summary>
public partial class DebugPanelWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;

    public DebugPanelViewModel ViewModel { get; }

    public DebugPanelWindow(DebugPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Raw input arrives up to 1000 times a second; publish to the UI on a timer instead of per sample.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += (_, _) => ViewModel.Flush();
        _refreshTimer.Start();
    }

    /// <summary>Shows the panel, positioning it on the appropriate edge of the primary screen.</summary>
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
            e.Cancel = true;
            Hide();
            return;
        }
        _refreshTimer.Stop();
        base.OnClosing(e);
    }
}

/// <summary>Draws an arrow showing the direction and rough magnitude of the last motion sample.</summary>
public sealed class DirectionIndicator : Control
{
    public static readonly StyledProperty<int> DeltaXProperty = AvaloniaProperty.Register<DirectionIndicator, int>(nameof(DeltaX));
    public static readonly StyledProperty<int> DeltaYProperty = AvaloniaProperty.Register<DirectionIndicator, int>(nameof(DeltaY));

    static DirectionIndicator()
    {
        AffectsRender<DirectionIndicator>(DeltaXProperty, DeltaYProperty);
    }

    public int DeltaX
    {
        get => GetValue(DeltaXProperty);
        set => SetValue(DeltaXProperty, value);
    }

    public int DeltaY
    {
        get => GetValue(DeltaYProperty);
        set => SetValue(DeltaYProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var accent = this.FindResource("SystemControlHighlightAccentBrush") as IBrush ?? Brushes.DodgerBlue;
        var ring = this.FindResource("SystemControlBackgroundBaseLowBrush") as IBrush ?? Brushes.LightGray;

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;
        context.DrawEllipse(null, new Pen(ring, 1.5), center, radius, radius);

        var dx = DeltaX;
        var dy = DeltaY;
        var magnitude = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (magnitude > 0)
        {
            var arrowLength = Math.Min(radius * 0.85, radius * 0.3 + magnitude / 40 * radius * 0.5);
            var end = new Point(center.X + dx / magnitude * arrowLength, center.Y + dy / magnitude * arrowLength);
            var pen = new Pen(accent, 2.5, lineCap: PenLineCap.Round);
            context.DrawLine(pen, center, end);

            var angle = Math.Atan2(end.Y - center.Y, end.X - center.X);
            const double head = 7;
            context.DrawLine(pen, end, new Point(end.X - head * Math.Cos(angle - Math.PI / 6), end.Y - head * Math.Sin(angle - Math.PI / 6)));
            context.DrawLine(pen, end, new Point(end.X - head * Math.Cos(angle + Math.PI / 6), end.Y - head * Math.Sin(angle + Math.PI / 6)));
        }

        context.DrawEllipse(accent, null, center, 3.5, 3.5);
    }
}
