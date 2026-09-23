using RoboMouse.Core.Configuration;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// Where one peer sits on the Layout page before Save: its edge and its offset along that edge.
/// The canvas edits these, never the <see cref="PeerConfig"/> itself, so Cancel leaves the settings alone.
/// </summary>
public sealed class PeerPlacement
{
    public PeerConfig Peer { get; }
    public ScreenPosition Position { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }

    // What the config held when this placement was taken. A config that no longer matches was changed
    // elsewhere (the Peers page saves edits straight away), so the placement is retaken from it.
    private ScreenPosition _originalPosition;
    private int _originalOffsetX;
    private int _originalOffsetY;

    public PeerPlacement(PeerConfig peer)
    {
        Peer = peer;
        Retake();
    }

    /// <summary>True when the placement differs from what is saved in the config.</summary>
    public bool IsModified => Position != Peer.Position || OffsetX != Peer.OffsetX || OffsetY != Peer.OffsetY;

    internal bool ConfigChangedElsewhere =>
        Peer.Position != _originalPosition || Peer.OffsetX != _originalOffsetX || Peer.OffsetY != _originalOffsetY;

    /// <summary>Discards any edit and reads the placement from the config again.</summary>
    internal void Retake()
    {
        Position = _originalPosition = Peer.Position;
        OffsetX = _originalOffsetX = Peer.OffsetX;
        OffsetY = _originalOffsetY = Peer.OffsetY;
    }

    /// <summary>Writes the placement into the config.</summary>
    internal void Apply()
    {
        Peer.Position = Position;
        Peer.OffsetX = OffsetX;
        Peer.OffsetY = OffsetY;
        Retake();
    }
}

/// <summary>
/// Layout page: the drag-to-arrange canvas. Drags change <see cref="Placements"/> only; <see cref="Save"/>
/// writes them into the peer configs, and closing the window without saving simply drops them.
/// </summary>
public sealed class LayoutPageViewModel : PageViewModel
{
    private readonly List<PeerPlacement> _placements = new();

    public AppSettings Settings { get; }

    /// <summary>One entry per configured peer, in the order of <see cref="AppSettings.Peers"/>.</summary>
    public IReadOnlyList<PeerPlacement> Placements => _placements;

    /// <summary>Set by the view: asks the canvas to rebuild from <see cref="Placements"/>.</summary>
    public Action? ReloadRequested { get; set; }

    public LayoutPageViewModel(AppSettings settings)
    {
        Settings = settings;
        Sync();
    }

    /// <summary>
    /// Follows the peer list (peers added, removed or edited on the Peers page) and redraws. Unsaved
    /// drags survive unless that peer's config was changed in the meantime.
    /// </summary>
    public void Reload()
    {
        Sync();
        ReloadRequested?.Invoke();
    }

    private void Sync()
    {
        var existing = _placements.ToDictionary(p => p.Peer);
        _placements.Clear();
        foreach (var peer in Settings.Peers)
        {
            if (existing.TryGetValue(peer, out var placement))
            {
                if (placement.ConfigChangedElsewhere)
                    placement.Retake();
            }
            else
            {
                placement = new PeerPlacement(peer);
            }
            _placements.Add(placement);
        }
    }

    /// <summary>
    /// Puts a peer on an edge at the given offset. Another peer already on that edge would never be
    /// reached, so the two swap: it moves to the edge (and offset) the dropped peer came from.
    /// Returns the peer that moved out of the way, or null.
    /// </summary>
    public PeerPlacement? MoveToEdge(PeerPlacement moved, ScreenPosition position, int offsetX, int offsetY)
    {
        var (fromPosition, fromX, fromY) = (moved.Position, moved.OffsetX, moved.OffsetY);
        moved.Position = position;
        moved.OffsetX = offsetX;
        moved.OffsetY = offsetY;

        if (fromPosition == position)
            return null;

        var occupant = _placements.FirstOrDefault(p => p != moved && p.Position == position);
        if (occupant == null)
            return null;

        occupant.Position = fromPosition;
        occupant.OffsetX = fromX;
        occupant.OffsetY = fromY;
        return occupant;
    }

    /// <summary>Writes every placement into its peer config. The caller saves the settings file.</summary>
    public void Save()
    {
        foreach (var placement in _placements)
            placement.Apply();
    }
}
