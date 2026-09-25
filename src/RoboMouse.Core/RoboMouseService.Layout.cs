using RoboMouse.Core.Configuration;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using RoboMouse.Core.Screen;

namespace RoboMouse.Core;

/// <summary>
/// The layout: where every peer monitor sits around this PC's own. Each machine announces its monitors
/// (<see cref="ScreenInfoMessage"/>) on connect and whenever they change; this side keeps each peer's
/// placements (<see cref="PeerConfig.Monitors"/>) in step with them, builds the <see cref="VirtualDesktop"/>
/// the mouse hook crosses on, and sends every connected peer the layout as it sees it
/// (<see cref="VirtualLayoutMessage"/>) so that, while controlled from here, it can tell which edges
/// lead where. What a controller sends is kept per connection for when that controller is in control.
/// </summary>
public sealed partial class RoboMouseService
{
    private const int DisplayPollMs = 2000;

    private readonly object _layoutLock = new();

    // The monitors each connected peer reported on its current connection, by peer id.
    private readonly Dictionary<string, List<MonitorRect>> _peerMonitors = new();

    // The layout each controller sent, for deciding crossings while it is in control.
    private readonly Dictionary<PeerConnection, VirtualDesktop> _controllerLayouts = new();

    // This PC's layout, rebuilt whole on any change and read lock-free by the mouse hook.
    private volatile VirtualDesktop _desktop = VirtualDesktop.Empty;

    private System.Threading.Timer? _displayTimer;
    private string _localSignature = string.Empty;

    /// <summary>
    /// Raised (on a background thread) when a peer's monitors arrived or changed, a peer with monitors
    /// disconnected, or this PC's own monitors changed. Placements may have been updated and saved.
    /// </summary>
    public event EventHandler? ScreensChanged;

    /// <summary>This PC's monitors as they are now.</summary>
    public MonitorLayout LocalLayout => CurrentLocalLayout();

    /// <summary>The monitors a connected peer reported, or null when it is not connected (or has not said yet).</summary>
    public IReadOnlyList<MonitorRect>? GetPeerMonitors(string peerId)
    {
        lock (_layoutLock)
        {
            return _peerMonitors.TryGetValue(peerId, out var monitors) ? monitors.ToList() : null;
        }
    }

    /// <summary>
    /// Takes up placement changes saved on the Layout page (or a peer's side changed on the Peers
    /// page, which clears its placements): fills in placements for connected peers that have none,
    /// rebuilds the layout and sends it to every peer.
    /// </summary>
    public void ApplyLayout()
    {
        var changed = false;
        foreach (var peer in _settings.Peers.ToList())
            changed |= ReconcilePeer(peer);
        if (changed)
            SaveSettings("layout");
        RebuildLayout();
    }

    /// <summary>The peer list changed: the layout follows (new peers placed, removed ones gone), then the UI hears.</summary>
    private void RaisePeersChanged()
    {
        ApplyLayout();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static MonitorLayout CurrentLocalLayout()
    {
        try
        {
            return ScreenInfo.ReadLayout();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new MonitorLayout(Array.Empty<MonitorRect>());
        }
    }

    private void HandlePeerScreens(ScreenInfoMessage msg, PeerConnection connection)
    {
        var monitors = new MonitorLayout(msg.Monitors).Monitors.ToList(); // distinct, non-empty ids
        lock (_layoutLock)
        {
            _peerMonitors[connection.PeerId] = monitors;
        }
        SimpleLogger.Log("Layout", $"{connection.PeerName} has {monitors.Count} monitor(s): " +
            string.Join(", ", monitors.Select(m => $"{m.Id} {m.Bounds.Width}x{m.Bounds.Height} at {m.Bounds.X},{m.Bounds.Y} {m.Scale}%{(m.Primary ? " main" : "")}")));

        var peer = _settings.Peers.ToList().FirstOrDefault(p => p.Id == connection.PeerId);
        if (peer != null && ReconcilePeer(peer))
            SaveSettings("peer monitors");
        RebuildLayout();
        ScreensChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleControllerLayout(VirtualLayoutMessage msg, PeerConnection connection)
    {
        var desktop = msg.ToDesktop();
        lock (_layoutLock)
        {
            _controllerLayouts[connection] = desktop;
        }
    }

    /// <summary>The layout a controller sent, or null before it sent one.</summary>
    private VirtualDesktop? GetControllerLayout(PeerConnection controller)
    {
        lock (_layoutLock)
        {
            return _controllerLayouts.TryGetValue(controller, out var desktop) ? desktop : null;
        }
    }

    private void ForgetPeerScreens(PeerConnection connection)
    {
        bool hadMonitors;
        lock (_layoutLock)
        {
            _controllerLayouts.Remove(connection);
            // A newer connection to the same peer may already have reported; only drop our own report.
            hadMonitors = _registry.Get(connection.PeerId) == null && _peerMonitors.Remove(connection.PeerId);
        }
        if (!hadMonitors)
            return;
        RebuildLayout();
        ScreensChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings one peer's placements in step with the monitors it reported (sizes, new monitors, nothing
    /// overlapping). Returns true when they changed; the caller saves. Nothing happens for a peer that
    /// is not connected.
    /// </summary>
    private bool ReconcilePeer(PeerConfig peer)
    {
        List<MonitorRect>? reported;
        lock (_layoutLock)
        {
            _peerMonitors.TryGetValue(peer.Id, out reported);
        }
        if (reported == null)
            return false;

        var local = CurrentLocalLayout();
        var obstacles = local.Monitors.Select(m => m.Bounds).ToList();
        foreach (var other in _settings.Peers.ToList())
        {
            if (!ReferenceEquals(other, peer) && other.Id != peer.Id)
                obstacles.AddRange(VisiblePlacements(other).Select(p => p.Rect));
        }

        var updated = PlacementPlanner.Reconcile(peer.Monitors, reported, local.PrimaryMonitor.Scale, obstacles,
            local.VirtualBounds, peer.Position, peer.OffsetX, peer.OffsetY);
        if (SamePlacements(peer.Monitors, updated))
            return false;
        peer.Monitors = updated;
        return true;
    }

    /// <summary>
    /// The placements of a peer that are on the layout: while it is connected, those of the monitors
    /// it has now; while it is not, all of them (they may all come back).
    /// </summary>
    private IEnumerable<MonitorPlacement> VisiblePlacements(PeerConfig peer)
    {
        HashSet<string>? present;
        lock (_layoutLock)
        {
            present = _peerMonitors.TryGetValue(peer.Id, out var monitors) ? monitors.Select(m => m.Id).ToHashSet() : null;
        }
        return peer.Monitors.ToList().Where(p => present == null || present.Contains(p.Id));
    }

    private static bool SamePlacements(IReadOnlyList<MonitorPlacement> a, IReadOnlyList<MonitorPlacement> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Id != y.Id || x.Rect != y.Rect || x.RemoteRect != y.RemoteRect || x.Scale != y.Scale || x.Primary != y.Primary)
                return false;
        }
        return true;
    }

    /// <summary>Builds this PC's layout from its monitors and every enabled peer's placements, then sends it out.</summary>
    private void RebuildLayout()
    {
        var local = CurrentLocalLayout();
        var screens = local.Monitors.Select(m => new LayoutScreen(VirtualDesktop.Local, m.Id, m.Bounds)).ToList();
        foreach (var peer in _settings.Peers.ToList())
        {
            if (!peer.Enabled)
                continue;
            foreach (var p in VisiblePlacements(peer))
            {
                // Placements never overlap, but a hand-edited settings file could make them; the first wins.
                if (screens.Any(s => s.Rect.IntersectsWith(p.Rect)))
                    continue;
                screens.Add(new LayoutScreen(peer.Id, p.Id, p.Rect));
            }
        }
        _desktop = new VirtualDesktop(screens);
        SendLayouts();
    }

    /// <summary>Sends each connected peer this PC's layout: its own monitors by id, every other connected screen unnamed.</summary>
    private void SendLayouts()
    {
        var desktop = _desktop;
        foreach (var connection in _registry.Snapshot())
        {
            lock (_layoutLock)
            {
                if (!_peerMonitors.ContainsKey(connection.PeerId))
                    continue; // it has not said what it has yet
            }
            var message = new VirtualLayoutMessage();
            foreach (var screen in desktop.Screens)
            {
                if (screen.Owner == connection.PeerId)
                    message.Screens.Add(new VirtualLayoutMessage.LayoutEntry(screen.MonitorId, screen.Rect));
                else if (screen.IsLocal || _registry.Contains(screen.Owner))
                    message.Screens.Add(new VirtualLayoutMessage.LayoutEntry(null, screen.Rect));
            }
            connection.Post(message);
        }
    }

    private void StartDisplayWatch()
    {
        _localSignature = CurrentLocalLayout().Signature;
        RebuildLayout();
        _displayTimer = new System.Threading.Timer(_ => CheckLocalDisplays(), null, DisplayPollMs, DisplayPollMs);
    }

    private void StopDisplayWatch()
    {
        _displayTimer?.Dispose();
        _displayTimer = null;
    }

    /// <summary>
    /// Polled: this PC's monitors changed (plugged in, removed, resolution, scaling, main display).
    /// Tells every peer, and moves peer monitors that now overlap one of ours out of the way.
    /// Message-only windows never get WM_DISPLAYCHANGE, hence the poll.
    /// </summary>
    private void CheckLocalDisplays()
    {
        MonitorLayout local;
        try
        {
            local = CurrentLocalLayout();
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Layout", $"Could not read the monitors: {ex.Message}");
            return;
        }
        if (local.Signature == _localSignature)
            return;
        _localSignature = local.Signature;
        SimpleLogger.Log("Layout", $"This PC's monitors changed: {local.Signature}");

        var info = ScreenInfoMessage.From(local);
        foreach (var connection in _registry.Snapshot())
            connection.Post(info);

        // Connected peers are reconciled (their sizes follow this PC's scaling); the rest only pushed clear.
        var ours = local.Monitors.Select(m => m.Bounds).ToList();
        var changed = false;
        foreach (var peer in _settings.Peers.ToList())
        {
            if (ReconcilePeer(peer))
            {
                changed = true;
            }
            else if (PlacementPlanner.PushOut(peer.Monitors, ours) is { } moved)
            {
                peer.Monitors = moved;
                changed = true;
            }
        }
        if (changed)
            SaveSettings("layout after a display change");
        RebuildLayout();
        ScreensChanged?.Invoke(this, EventArgs.Empty);
    }
}
