using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

/// <summary>
/// Custom-drawn canvas for arranging screens: every monitor of this PC (fixed) and of each peer
/// (draggable) at its place on the layout. A dropped monitor settles against the nearest screen edge
/// without overlapping. It edits the page's <see cref="LayoutItem"/>s, never the peer configs.
/// Keyboard: Tab / Shift+Tab select peer monitors, arrow keys move the selected one (Shift for small
/// steps), Escape clears the selection. Each change is announced through the automation name.
/// </summary>
public sealed class ScreenLayoutControl : Control
{
    public static readonly StyledProperty<LayoutPageViewModel?> LayoutProperty =
        AvaloniaProperty.Register<ScreenLayoutControl, LayoutPageViewModel?>(nameof(Layout));

    /// <summary>The page whose screens are laid out and edited.</summary>
    public LayoutPageViewModel? Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private LayoutItem? _selected;
    private LayoutItem? _dragging;
    private Point _dragOffset;      // pointer position inside the dragged screen, in canvas units
    private Point _dragPosition;    // the dragged screen's top-left while dragging, in canvas units

    // Canvas units per layout unit, and where layout (0, 0) sits on the canvas. Chosen on each rebuild
    // so everything fits with room to drag round the outside; kept while a drag is in progress.
    private double _scale = 0.1;
    private Point _origin;
    private const double CanvasMargin = 36;

    private static readonly IBrush CanvasBrush = new SolidColorBrush(Color.FromRgb(24, 30, 44));
    private static readonly IBrush DotBrush = new SolidColorBrush(Color.FromRgb(48, 56, 74));
    private static readonly Typeface TitleTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SubTypeface = new(FontFamily.Default);

    // One colour per peer, so its monitors read as a set however they are spread out.
    private static readonly Color[] PeerColors =
    {
        Color.FromRgb(56, 72, 104),
        Color.FromRgb(40, 92, 84),
        Color.FromRgb(92, 66, 108),
        Color.FromRgb(104, 80, 46),
        Color.FromRgb(58, 92, 58)
    };

    /// <summary>Keyboard step, in layout units (pixels of this PC); Shift gives <see cref="FineNudge"/>.</summary>
    public const int Nudge = 100;
    public const int FineNudge = 10;

    private const string HelpText =
        "Tab selects a peer screen. Arrow keys move it; it stays against another screen and never overlaps one. Hold Shift for small steps. Changes apply when you save.";

    public ScreenLayoutControl()
    {
        ClipToBounds = true;
        Focusable = true;
        AutomationProperties.SetName(this, "Screen layout");
        AutomationProperties.SetHelpText(this, HelpText);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty)
            Reload();
    }

    /// <summary>Rebuilds from the page's items (peers added or removed, monitors changed).</summary>
    public void Reload()
    {
        _dragging = null;
        var selected = _selected;
        Fit();
        Select(selected == null ? null : Items.FirstOrDefault(i => i.Peer == selected.Peer && i.MonitorId == selected.MonitorId));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Fit();
        InvalidateVisual();
    }

    private double CanvasWidth => Bounds.Width > 0 ? Bounds.Width : 600;
    private double CanvasHeight => Bounds.Height > 0 ? Bounds.Height : 400;

    private IReadOnlyList<LayoutItem> Items => Layout?.Items ?? Array.Empty<LayoutItem>();

    /// <summary>
    /// Scale and origin at which every screen fits, with half a main-display's room round the outside
    /// so a screen can be dragged to any free side.
    /// </summary>
    private void Fit()
    {
        var items = Items;
        if (items.Count == 0)
            return;
        var minX = items.Min(i => i.X);
        var minY = items.Min(i => i.Y);
        var maxX = items.Max(i => i.X + i.Width);
        var maxY = items.Max(i => i.Y + i.Height);
        var local = items.FirstOrDefault(i => i.IsLocal && i.IsPrimary) ?? items[0];
        var padX = local.Width * 0.45;
        var padY = local.Height * 0.45;

        var extentW = maxX - minX + padX * 2;
        var extentH = maxY - minY + padY * 2;
        var availW = Math.Max(100, CanvasWidth - CanvasMargin * 2);
        var availH = Math.Max(100, CanvasHeight - CanvasMargin * 2);
        _scale = Math.Min(availW / extentW, availH / extentH);

        // Centre the arrangement.
        var centreX = (minX + maxX) / 2.0;
        var centreY = (minY + maxY) / 2.0;
        _origin = new Point(CanvasWidth / 2 - centreX * _scale, CanvasHeight / 2 - centreY * _scale);
    }

    private Rect ToCanvas(LayoutItem item) =>
        item == _dragging
            ? new Rect(_dragPosition, new Size(item.Width * _scale, item.Height * _scale))
            : new Rect(_origin.X + item.X * _scale, _origin.Y + item.Y * _scale, item.Width * _scale, item.Height * _scale);

    private System.Drawing.Point ToLayout(Point canvas) =>
        new((int)Math.Round((canvas.X - _origin.X) / _scale), (int)Math.Round((canvas.Y - _origin.Y) / _scale));

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(CanvasBrush, bounds);

        // Dotted grid so the canvas reads as a workspace.
        for (var y = 12.0; y < bounds.Height; y += 24)
            for (var x = 12.0; x < bounds.Width; x += 24)
                context.FillRectangle(DotBrush, new Rect(x, y, 2, 2));

        // The dragged screen last, so it is drawn over the others.
        foreach (var item in Items.Where(i => i != _dragging))
            DrawScreen(context, item);
        if (_dragging != null)
            DrawScreen(context, _dragging);

        // Keyboard focus: a dashed ring round the selected screen, or round the canvas when none is.
        if (IsFocused)
        {
            var ring = new Pen(Brushes.White, 2, new DashStyle(new double[] { 3, 2 }, 0));
            var target = _selected != null ? ToCanvas(_selected).Inflate(4) : bounds.Deflate(3);
            context.DrawRectangle(null, ring, target, 10, 10);
        }
    }

    private Color PeerColor(LayoutItem item)
    {
        if (item.Peer == null || Layout == null)
            return PeerColors[0];
        var index = Layout.Settings.Peers.IndexOf(item.Peer);
        return PeerColors[Math.Max(0, index) % PeerColors.Length];
    }

    private void DrawScreen(DrawingContext context, LayoutItem item)
    {
        var rect = ToCanvas(item).Deflate(1);
        var faded = item.Peer is { Enabled: false } || item.IsPlaceholder || (!item.IsLocal && !item.IsConnected);
        var selected = item == _selected;

        Color fill, edge;
        if (item.IsLocal)
        {
            fill = this.FindResource("SystemAccentColor") is Color accent ? accent : Color.FromRgb(30, 100, 230);
            edge = this.FindResource("SystemAccentColorLight1") is Color light ? light : Color.FromRgb(120, 170, 255);
            if (!item.IsPrimary)
                fill = Color.FromArgb(170, fill.R, fill.G, fill.B);
        }
        else
        {
            var baseColor = PeerColor(item);
            fill = selected ? Lighten(baseColor, 0.25) : baseColor;
            edge = selected ? Colors.White : Lighten(baseColor, 0.45);
            if (faded)
            {
                fill = Color.FromArgb(110, fill.R, fill.G, fill.B);
                edge = Color.FromArgb(150, edge.R, edge.G, edge.B);
            }
        }

        var pen = item.IsPlaceholder
            ? new Pen(new SolidColorBrush(edge), 1, new DashStyle(new double[] { 4, 4 }, 0))
            : new Pen(new SolidColorBrush(edge), selected ? 2 : 1);
        context.DrawRectangle(new SolidColorBrush(fill), pen, rect, 6, 6);

        // Bezel line to hint "screen".
        if (rect.Width > 30)
        {
            context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))),
                new Point(rect.Left + 8, rect.Top + 5), new Point(rect.Right - 8, rect.Top + 5));
        }

        var textColor = faded ? Color.FromRgb(190, 196, 208) : Colors.White;
        var title = new FormattedText(item.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TitleTypeface, 12.5, new SolidColorBrush(textColor))
        {
            MaxTextWidth = Math.Max(20, rect.Width - 8),
            TextAlignment = TextAlignment.Center,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var status = item.IsPlaceholder ? "connect to arrange"
            : item.Peer is { Enabled: false } ? "disabled"
            : !item.IsLocal && !item.IsConnected ? "offline"
            : null;
        var sub = $"{item.PixelSize.Width} × {item.PixelSize.Height}" + (status == null ? string.Empty : $"  ·  {status}");
        var subText = new FormattedText(sub, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, SubTypeface, 11,
            new SolidColorBrush(Color.FromArgb(200, textColor.R, textColor.G, textColor.B)))
        {
            MaxTextWidth = Math.Max(20, rect.Width - 8),
            TextAlignment = TextAlignment.Center,
            MaxLineCount = 2,
            Trimming = TextTrimming.CharacterEllipsis
        };

        var top = rect.Y + (rect.Height - title.Height - subText.Height - 2) / 2;
        context.DrawText(title, new Point(rect.X + 4, top));
        if (top + title.Height + subText.Height < rect.Bottom)
            context.DrawText(subText, new Point(rect.X + 4, top + title.Height + 2));
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;

        switch (e.Key)
        {
            case Key.Tab:
                e.Handled = SelectNext(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Key.Escape when _selected != null:
                Select(null);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                e.Handled = MoveSelected(e.Key, e.KeyModifiers);
                break;
        }
    }

    /// <summary>
    /// Selects the next (or previous) peer screen. Past the last one the selection clears and false
    /// lets Tab carry on to the next control, so focus is never trapped here.
    /// </summary>
    private bool SelectNext(bool backwards)
    {
        var movable = Items.Where(i => i.IsMovable).ToList();
        if (movable.Count == 0)
            return false;
        var index = _selected == null ? (backwards ? movable.Count : -1) : movable.IndexOf(_selected);
        index += backwards ? -1 : 1;
        if (index < 0 || index >= movable.Count)
        {
            Select(null);
            return false;
        }
        Select(movable[index]);
        return true;
    }

    private bool MoveSelected(Key key, KeyModifiers modifiers)
    {
        if (_selected is not { IsMovable: true } item || Layout == null)
            return false;

        var step = modifiers.HasFlag(KeyModifiers.Shift) ? FineNudge : Nudge;
        var (dx, dy) = key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            _ => (0, step)
        };
        Layout.Nudge(item, dx, dy);
        Fit();
        Select(item); // announce where it is now; swallows the key even when it could not move
        return true;
    }

    private void Select(LayoutItem? item)
    {
        _selected = item;
        AutomationProperties.SetName(this, item != null && Layout != null
            ? "Screen layout. Selected " + Layout.Describe(item)
            : "Screen layout");
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        Focus();
        var position = e.GetPosition(this);
        var hit = Items.LastOrDefault(i => i.IsMovable && ToCanvas(i).Contains(position));
        Select(hit);
        if (hit == null)
            return;

        var rect = ToCanvas(hit);
        _dragging = hit;
        _dragOffset = position - rect.Position;
        _dragPosition = rect.Position;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging == null)
            return;
        _dragPosition = e.GetPosition(this) - _dragOffset;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging != null)
            e.Pointer.Capture(null); // ends the drag through OnPointerCaptureLost
        EndDrag();
    }

    /// <summary>
    /// Capture ends on release, but also when the window loses focus or another control takes the
    /// pointer mid-drag; either way the screen settles where it was dropped instead of staying stuck.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragging is not { } item)
            return;
        _dragging = null;
        Layout?.Move(item, ToLayout(_dragPosition));
        Fit();
        Select(item);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_dragging != null || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;
        Fit();
        InvalidateVisual();
    }
}
