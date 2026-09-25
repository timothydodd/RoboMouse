using System.Drawing;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// One screen on the Layout page: a monitor of this PC (fixed where Windows has it) or of a peer
/// (movable). Positions are in layout units, which are this PC's virtual-screen pixels.
/// </summary>
public sealed class LayoutItem
{
    /// <summary>The peer the monitor belongs to, or null for this PC.</summary>
    public PeerConfig? Peer { get; init; }
    public string MonitorId { get; init; } = string.Empty;

    /// <summary>1-based, in the machine's own order; shown when it has more than one monitor.</summary>
    public int Number { get; init; }
    public int MonitorCount { get; init; } = 1;
    public bool IsPrimary { get; init; }

    /// <summary>The monitor's resolution on its own machine.</summary>
    public Size PixelSize { get; init; }

    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The peer is connected (its monitors are the ones it has now).</summary>
    public bool IsConnected { get; init; }

    /// <summary>
    /// A peer that has never reported its monitors, drawn where it was added. It cannot be moved: its
    /// monitors are placed on their own once it connects.
    /// </summary>
    public bool IsPlaceholder { get; init; }

    public bool IsLocal => Peer == null;
    public bool IsMovable => !IsLocal && !IsPlaceholder;
    public Rectangle Rect => new(X, Y, Width, Height);

    public string Name => Peer?.Name ?? "This PC";

    /// <summary>"Laptop", "Laptop 2", "This PC 1 (main)".</summary>
    public string Label => MonitorCount > 1
        ? $"{Name} {Number}{(IsLocal && IsPrimary ? " (main)" : string.Empty)}"
        : Name;
}

/// <summary>
/// Layout page: every monitor of this PC and of each peer on one canvas. This PC's monitors sit where
/// Windows has them; each peer monitor is dragged anywhere against another screen, so a peer's
/// monitors can be split up or put in another order, and several peers can share one edge. Drags change
/// the items only; <see cref="Save"/> writes them into <see cref="PeerConfig.Monitors"/>, and closing
/// the window without saving drops them.
/// </summary>
public sealed class LayoutPageViewModel : PageViewModel
{
    /// <summary>How close (layout units) a side must come to another screen's to line up with it.</summary>
    public const int AlignThreshold = 120;

    private readonly IAppBackend? _backend;
    private readonly List<LayoutItem> _items = new();

    // Unsaved moves, by peer and monitor, with the placement the move was made from. A placement that
    // no longer matches was changed elsewhere (the service placed it again), so the move is dropped.
    private readonly Dictionary<(PeerConfig Peer, string MonitorId), (Point Moved, Point From)> _drafts = new();

    public AppSettings Settings { get; }

    /// <summary>Every screen, this PC's first.</summary>
    public IReadOnlyList<LayoutItem> Items => _items;

    /// <summary>This PC's monitors: from the backend, or a stand-in where the monitor API is missing.</summary>
    public Func<MonitorLayout> LocalLayoutSource { get; set; }

    /// <summary>Set by the view: asks the canvas to rebuild from <see cref="Items"/>.</summary>
    public Action? ReloadRequested { get; set; }

    public LayoutPageViewModel(AppSettings settings, IAppBackend? backend = null, Func<MonitorLayout>? localLayout = null)
    {
        Settings = settings;
        _backend = backend;
        LocalLayoutSource = localLayout ?? (() => backend?.LocalLayout ?? ReadLocalLayout());
        Sync();
    }

    private static MonitorLayout ReadLocalLayout()
    {
        try
        {
            return ScreenInfo.ReadLayout();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            var main = new Rectangle(0, 0, 2560, 1440);
            return new MonitorLayout(new[] { new MonitorRect(main, main, true) });
        }
    }

    /// <summary>True when there are moves not saved yet.</summary>
    public bool IsModified => _drafts.Count > 0;

    /// <summary>
    /// Follows the peer list and the monitors (a peer connected or reported new ones, this PC's changed)
    /// and redraws. Unsaved moves survive unless that monitor was placed again meanwhile.
    /// </summary>
    public void Reload()
    {
        Sync();
        ReloadRequested?.Invoke();
    }

    private void Sync()
    {
        _items.Clear();
        var local = LocalLayoutSource();
        var number = 0;
        foreach (var m in local.Monitors)
        {
            _items.Add(new LayoutItem
            {
                MonitorId = m.Id,
                Number = ++number,
                MonitorCount = local.Monitors.Count,
                IsPrimary = m.Primary,
                PixelSize = m.Bounds.Size,
                X = m.Bounds.X,
                Y = m.Bounds.Y,
                Width = m.Bounds.Width,
                Height = m.Bounds.Height,
                IsConnected = true
            });
        }

        var livePeers = new HashSet<PeerConfig>();
        foreach (var peer in Settings.Peers)
        {
            livePeers.Add(peer);
            var reported = _backend?.GetPeerMonitors(peer.Id);
            var present = reported?.Select(r => r.Id).ToHashSet();
            var placements = peer.Monitors.ToList();
            if (placements.Count == 0)
            {
                _items.Add(Placeholder(peer, local.VirtualBounds));
                continue;
            }

            var shown = placements.Where(p => present == null || present.Contains(p.Id))
                .OrderBy(p => p.RemoteX).ThenBy(p => p.RemoteY).ToList();
            number = 0;
            foreach (var p in shown)
            {
                var item = new LayoutItem
                {
                    Peer = peer,
                    MonitorId = p.Id,
                    Number = ++number,
                    MonitorCount = shown.Count,
                    IsPrimary = p.Primary,
                    PixelSize = new Size(p.RemoteWidth, p.RemoteHeight),
                    X = p.X,
                    Y = p.Y,
                    Width = p.Width,
                    Height = p.Height,
                    IsConnected = present != null
                };
                if (_drafts.TryGetValue((peer, p.Id), out var draft))
                {
                    if (draft.From == new Point(p.X, p.Y))
                        (item.X, item.Y) = (draft.Moved.X, draft.Moved.Y);
                    else
                        _drafts.Remove((peer, p.Id));
                }
                _items.Add(item);
            }
        }

        // Moves on peers that have since been removed.
        foreach (var key in _drafts.Keys.Where(k => !livePeers.Contains(k.Peer)).ToList())
            _drafts.Remove(key);
    }

    /// <summary>A peer with no placements yet: one screen of its handshake size on the side it was added.</summary>
    private static LayoutItem Placeholder(PeerConfig peer, Rectangle local)
    {
        var (w, h) = (Math.Max(1, peer.ScreenWidth), Math.Max(1, peer.ScreenHeight));
        var (x, y) = peer.Position switch
        {
            ScreenPosition.Left => (local.Left - w, local.Top + peer.OffsetY),
            ScreenPosition.Top => (local.Left + peer.OffsetX, local.Top - h),
            ScreenPosition.Bottom => (local.Left + peer.OffsetX, local.Bottom),
            _ => (local.Right, local.Top + peer.OffsetY)
        };
        return new LayoutItem { Peer = peer, PixelSize = new Size(w, h), X = x, Y = y, Width = w, Height = h, IsPlaceholder = true };
    }

    /// <summary>The screens a moved one must not overlap: every real one except itself.</summary>
    private List<Rectangle> Others(LayoutItem moving) =>
        _items.Where(i => i != moving && !i.IsPlaceholder).Select(i => i.Rect).ToList();

    /// <summary>
    /// Drops a peer monitor at <paramref name="proposed"/> (its size unchanged): it settles against the
    /// nearest screen edge, overlapping nothing, lined up when close. Returns where it went.
    /// </summary>
    public Rectangle Move(LayoutItem item, Point proposed)
    {
        if (!item.IsMovable)
            return item.Rect;
        var snapped = VirtualDesktop.Snap(new Rectangle(proposed, new Size(item.Width, item.Height)), Others(item), AlignThreshold);
        SetPosition(item, snapped.Location);
        return snapped;
    }

    /// <summary>
    /// Moves a peer monitor by a step from the keyboard, to the nearest place in that direction where
    /// it still touches another screen and overlaps none. Returns false when it cannot go further.
    /// </summary>
    public bool Nudge(LayoutItem item, int dx, int dy)
    {
        if (!item.IsMovable || (dx == 0 && dy == 0))
            return false;
        var others = Others(item);
        var start = item.Rect;
        // Try ever bigger steps, so a screen blocked by a neighbour jumps past it rather than stopping.
        for (var k = 1; k <= 64; k++)
        {
            var proposed = new Rectangle(start.X + dx * k, start.Y + dy * k, start.Width, start.Height);
            var snapped = VirtualDesktop.Snap(proposed, others, alignThreshold: 0);
            // It must have moved the way asked, not been pulled back or sideways past where it was.
            var progress = dx != 0 ? Math.Sign(snapped.X - start.X) == Math.Sign(dx) : Math.Sign(snapped.Y - start.Y) == Math.Sign(dy);
            if (snapped != start && progress)
            {
                SetPosition(item, snapped.Location);
                return true;
            }
        }
        return false;
    }

    private void SetPosition(LayoutItem item, Point location)
    {
        item.X = location.X;
        item.Y = location.Y;
        var key = (item.Peer!, item.MonitorId);
        var saved = item.Peer!.Monitors.FirstOrDefault(p => p.Id == item.MonitorId);
        var from = saved == null ? location : new Point(saved.X, saved.Y);
        if (from == location)
            _drafts.Remove(key);
        else
            _drafts[key] = (location, from);
    }

    /// <summary>Where a screen sits, in words (for screen readers): its size and what it touches on each side.</summary>
    public string Describe(LayoutItem item)
    {
        var parts = new List<string> { $"{item.Label}, {item.PixelSize.Width} by {item.PixelSize.Height}" };
        var r = item.Rect;
        foreach (var other in _items)
        {
            if (other == item || other.IsPlaceholder || !VirtualDesktop.Touches(r, other.Rect))
                continue;
            var o = other.Rect;
            var side = r.Right == o.Left ? "left of" : r.Left == o.Right ? "right of" : r.Bottom == o.Top ? "above" : "below";
            parts.Add($"{side} {other.Label}");
        }
        if (item.IsPlaceholder)
            parts.Add("not arranged yet, connect it first");
        else if (!item.IsLocal && !item.IsConnected)
            parts.Add("offline");
        if (item.Peer is { Enabled: false })
            parts.Add("disabled");
        return string.Join(", ", parts);
    }

    /// <summary>Writes every move into its peer's placements. The caller saves the settings file and applies the layout.</summary>
    public void Save()
    {
        foreach (var peer in _drafts.Keys.Select(k => k.Peer).Distinct().ToList())
        {
            peer.Monitors = peer.Monitors.Select(p =>
            {
                var copy = p.Clone();
                if (_drafts.TryGetValue((peer, p.Id), out var draft))
                    (copy.X, copy.Y) = (draft.Moved.X, draft.Moved.Y);
                return copy;
            }).ToList();
        }
        _drafts.Clear();
    }
}
