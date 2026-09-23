using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.ViewModels;

/// <summary>Peers page: machines asking to connect, configured peers, and machines found on the network.</summary>
public sealed partial class PeersPageViewModel : PageViewModel
{
    private readonly AppSettings _settings;
    private readonly IAppBackend _backend;
    private readonly IDialogService _dialogs;

    public ObservableCollection<PeerItemViewModel> Peers { get; } = new();
    public ObservableCollection<DiscoveredPeerViewModel> Discovered { get; } = new();
    public ObservableCollection<PendingPeerViewModel> Pending { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditPeerCommand), nameof(RemovePeerCommand), nameof(ToggleEnabledCommand))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledText))]
    private PeerItemViewModel? _selectedPeer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDiscoveredCommand))]
    private DiscoveredPeerViewModel? _selectedDiscovered;

    public bool HasPeers => Peers.Count > 0;
    public bool HasDiscovered => Discovered.Count > 0;
    public bool HasPending => Pending.Count > 0;
    public string ToggleEnabledText => SelectedPeer is { IsEnabled: false } ? "Enable" : "Disable";

    /// <summary>Raised when peers were added, edited or removed (the layout canvas listens).</summary>
    public event EventHandler? PeersChanged;

    /// <summary>Raised after a pending machine was allowed: the window shows the Layout page so it can be placed.</summary>
    public event EventHandler<PeerConfig>? PeerAllowed;

    public PeersPageViewModel(AppSettings settings, IAppBackend backend, IDialogService dialogs)
    {
        _settings = settings;
        _backend = backend;
        _dialogs = dialogs;
        Peers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPeers));
        Discovered.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDiscovered));
        Pending.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPending));
        RebuildPeers();
    }

    private bool HasSelectedPeer => SelectedPeer != null;
    private bool HasSelectedDiscovered => SelectedDiscovered != null;

    private void RebuildPeers()
    {
        var selected = SelectedPeer?.Peer;
        Peers.Clear();
        foreach (var peer in _settings.Peers.ToList())
        {
            var item = new PeerItemViewModel(peer, this);
            Peers.Add(item);
            if (peer == selected)
                SelectedPeer = item;
        }
        Refresh();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Updates connection status on each row, the pending requests and the discovered list. The
    /// service can change the peer list itself (a request allowed from the tray, two entries for one
    /// machine merged), so the rows are rebuilt when they no longer match the settings.
    /// </summary>
    public void Refresh()
    {
        if (!Peers.Select(p => p.Peer).SequenceEqual(_settings.Peers.ToList()))
        {
            RebuildPeers();
            return;
        }

        foreach (var item in Peers)
            item.Refresh(_backend.GetConnection(item.Peer.Id), _backend.GetLastConnectFailure(item.Peer.Id));
        OnPropertyChanged(nameof(ToggleEnabledText));

        RefreshPending();
        RefreshDiscovered();
    }

    private void RefreshPending()
    {
        var pending = _backend.PendingPeers;
        if (Pending.Select(p => p.Peer).SequenceEqual(pending))
            return;
        Pending.Clear();
        foreach (var peer in pending)
            Pending.Add(new PendingPeerViewModel(peer, this));
    }

    private void RefreshDiscovered()
    {
        var configuredIds = _settings.Peers.Select(p => p.Id).ToHashSet();
        var configuredAddresses = _settings.Peers.Select(p => p.Address).ToHashSet();
        var pendingIds = Pending.Select(p => p.Peer.MachineId).ToHashSet();
        var found = _backend.DiscoveredPeers
            .Where(p => p.MachineId != _settings.MachineId
                        && !configuredIds.Contains(p.MachineId)
                        && !pendingIds.Contains(p.MachineId)
                        && !configuredAddresses.Contains(p.Address.ToString()))
            .OrderBy(p => p.MachineName)
            .ToList();

        if (Discovered.Select(d => d.Peer.MachineId).SequenceEqual(found.Select(p => p.MachineId)))
            return;

        var selectedId = SelectedDiscovered?.Peer.MachineId;
        Discovered.Clear();
        foreach (var peer in found)
        {
            var item = new DiscoveredPeerViewModel(peer);
            Discovered.Add(item);
            if (peer.MachineId == selectedId)
                SelectedDiscovered = item;
        }
    }

    internal async Task SetEnabledAsync(PeerItemViewModel item, bool enabled)
    {
        if (item.Peer.Enabled == enabled)
            return;
        try
        {
            await _backend.SetPeerEnabledAsync(item.Peer, enabled);
        }
        catch (Exception ex)
        {
            await _dialogs.ErrorAsync($"Could not {(enabled ? "enable" : "disable")} {item.Peer.Name}: {ex.Message}");
        }
        Refresh();
    }

    /// <summary>Allows a machine that asked to connect: it becomes a peer on the first free edge, then the Layout page opens.</summary>
    internal void Allow(PendingPeerViewModel item)
    {
        var config = _backend.AllowPendingPeer(item.Peer.MachineId);
        RebuildPeers();
        if (config != null)
            PeerAllowed?.Invoke(this, config);
    }

    /// <summary>Ignores a machine that asked to connect, for the rest of this session.</summary>
    internal void Ignore(PendingPeerViewModel item)
    {
        _backend.IgnorePendingPeer(item.Peer.MachineId);
        Refresh();
    }

    [RelayCommand]
    private async Task AddPeerAsync()
    {
        var result = await _dialogs.ShowPeerSetupAsync(null, _settings);
        if (result == null)
            return;
        await AddAndConnectAsync(result);
    }

    /// <summary>The one flow for a new peer (see <see cref="PeerActions.AddAndConnectAsync"/>).</summary>
    private async Task AddAndConnectAsync(PeerConfig peer)
    {
        var connect = PeerActions.AddAndConnectAsync(_settings, _backend, peer);
        RebuildPeers();
        var error = await connect;
        if (error != null)
            await _dialogs.WarnAsync($"Added {peer.Name}, but could not connect yet: {error}\n\nRoboMouse keeps trying in the background.");
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPeer))]
    private async Task EditPeerAsync()
    {
        if (SelectedPeer is not { } item)
            return;
        var peer = item.Peer;
        var wasEnabled = peer.Enabled;
        var result = await _dialogs.ShowPeerSetupAsync(peer, _settings);
        if (result == null)
            return;

        _backend.SaveSettings();
        if (wasEnabled != peer.Enabled)
        {
            // The dialog wrote the flag directly; put it back and go through the service so the
            // connection follows the new state.
            var wanted = peer.Enabled;
            peer.Enabled = wasEnabled;
            await SetEnabledAsync(item, wanted);
        }
        RebuildPeers();
    }

    /// <summary>
    /// The service disconnects, removes and blocks the machine, so it does not come straight back as
    /// a connection request. Adding it again by hand unblocks it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelectedPeer))]
    private async Task RemovePeerAsync()
    {
        if (SelectedPeer is not { } item)
            return;
        if (!await _dialogs.ConfirmAsync($"Remove {item.Peer.Name}?\n\nIt will not be able to connect to this PC until you add it again."))
            return;
        try
        {
            await _backend.RemovePeerAsync(item.Peer);
        }
        catch (Exception ex)
        {
            await _dialogs.ErrorAsync($"Could not remove {item.Peer.Name}: {ex.Message}");
        }
        SelectedPeer = null;
        RebuildPeers();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPeer))]
    private Task ToggleEnabledAsync() =>
        SelectedPeer is { } item ? SetEnabledAsync(item, !item.Peer.Enabled) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelectedDiscovered))]
    private async Task AddDiscoveredAsync()
    {
        if (SelectedDiscovered is not { } found)
            return;
        var draft = PeerActions.FromDiscovered(found.Peer, PeerActions.FirstFreeEdge(_settings) ?? ScreenPosition.Right);
        var result = await _dialogs.ShowPeerSetupAsync(draft, _settings);
        if (result == null)
            return;
        await AddAndConnectAsync(result);
    }
}

/// <summary>One configured peer in the list.</summary>
public sealed partial class PeerItemViewModel : ObservableObject
{
    private readonly PeersPageViewModel _owner;
    private bool _syncing;

    public PeerConfig Peer { get; }

    public string Name => Peer.Name;
    public string Address => $"{Peer.Address}:{Peer.Port}";

    [ObservableProperty] private string _positionText;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private StatusTone _statusTone = StatusTone.Idle;

    /// <summary>The full reason behind <see cref="StatusText"/> (tooltip), or null.</summary>
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private bool _isEnabled;

    public PeerItemViewModel(PeerConfig peer, PeersPageViewModel owner)
    {
        Peer = peer;
        _owner = owner;
        _positionText = PeerPositions.Describe(peer.Position);
        _isEnabled = peer.Enabled;
    }

    public void Refresh(ConnectedPeerInfo? connection, PeerConnectFailure? failure = null)
    {
        (StatusText, StatusTone, StatusDetail) = Describe(Peer, connection, failure);
        PositionText = PeerPositions.Describe(Peer.Position);

        _syncing = true;
        IsEnabled = Peer.Enabled;
        _syncing = false;
    }

    /// <summary>A short status for the row, its colour, and the longer reason when the last connect failed.</summary>
    internal static (string Text, StatusTone Tone, string? Detail) Describe(PeerConfig peer, ConnectedPeerInfo? connection, PeerConnectFailure? failure)
    {
        if (!peer.Enabled)
            return ("Disabled", StatusTone.Idle, null);
        if (connection != null)
            return (connection.RoundTripMs >= 0 ? $"Connected · {connection.RoundTripMs} ms" : "Connected", StatusTone.Ok, null);
        if (failure == null)
            return ("Not connected", StatusTone.Idle, null);

        var (text, tone) = failure.Kind switch
        {
            PeerFailureKind.PairingCodeMismatch => ("Pairing code doesn't match", StatusTone.Error),
            PeerFailureKind.VersionMismatch => ("Different RoboMouse version", StatusTone.Error),
            PeerFailureKind.AwaitingApproval => ($"Waiting for approval on {peer.Name}", StatusTone.Warning),
            PeerFailureKind.Blocked => ($"Blocked on {peer.Name}", StatusTone.Error),
            PeerFailureKind.DisabledThere => ($"Switched off on {peer.Name}", StatusTone.Warning),
            PeerFailureKind.SameMachine => ("That address is this PC", StatusTone.Error),
            PeerFailureKind.Refused => ("RoboMouse not running there", StatusTone.Warning),
            PeerFailureKind.TimedOut => ("No answer", StatusTone.Warning),
            PeerFailureKind.Unreachable => ("Unreachable", StatusTone.Warning),
            _ => ("Not connected", StatusTone.Warning)
        };
        return (text, tone, failure.Message);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing)
            _ = _owner.SetEnabledAsync(this, value);
    }
}

/// <summary>A machine with the pairing code that asked to connect and waits for Allow or Ignore.</summary>
public sealed partial class PendingPeerViewModel
{
    private readonly PeersPageViewModel _owner;

    public PendingPeer Peer { get; }
    public string Name => Peer.MachineName;
    public string Detail => $"{Peer.Address} · asked at {Peer.RequestedAt:t}";

    public PendingPeerViewModel(PendingPeer peer, PeersPageViewModel owner)
    {
        Peer = peer;
        _owner = owner;
    }

    [RelayCommand]
    private void Allow() => _owner.Allow(this);

    [RelayCommand]
    private void Ignore() => _owner.Ignore(this);
}

/// <summary>A machine found by discovery that is not configured yet.</summary>
public sealed class DiscoveredPeerViewModel
{
    public Core.Network.DiscoveredPeer Peer { get; }
    public string Name => Peer.MachineName;
    public string Address => $"{Peer.Address}:{Peer.Port}";
    public string Screen => $"{Peer.ScreenWidth} × {Peer.ScreenHeight}";

    public DiscoveredPeerViewModel(Core.Network.DiscoveredPeer peer) => Peer = peer;
}
