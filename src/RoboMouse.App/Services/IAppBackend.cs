using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;
using RoboMouse.Core.Screen;

namespace RoboMouse.App.Services;

/// <summary>A connected peer as the UI sees it.</summary>
public sealed record ConnectedPeerInfo(string PeerId, string PeerName, int RoundTripMs);

/// <summary>
/// What the view models need from the running service. Keeping this behind an interface lets the
/// windows be built and rendered without hooks, sockets or a Windows desktop (see tools/RoboMouse.UiPreview).
/// </summary>
public interface IAppBackend
{
    bool Enabled { get; }
    bool IsControllingRemote { get; }
    bool IsControlledByRemote { get; }
    string? ActivePeerName { get; }
    /// <summary>Why the controlled machine cannot apply our input right now, if it cannot.</summary>
    InputBlockReason RemoteInputBlockReason { get; }

    IReadOnlyList<ConnectedPeerInfo> ConnectedPeers { get; }
    IReadOnlyList<DiscoveredPeer> DiscoveredPeers { get; }

    ConnectedPeerInfo? GetConnection(string peerId);
    bool IsPeerConnected(string peerId);

    Task SetPeerEnabledAsync(PeerConfig peer, bool enabled);
    Task ConnectToPeerAsync(PeerConfig peer, CancellationToken ct);
    Task DisconnectFromPeerAsync(string peerId);
    Task<ConnectionTestResult> TestConnectionAsync(string address, int port, CancellationToken ct);

    /// <summary>Writes the settings file. View models save through here so tests never touch the real file.</summary>
    void SaveSettings();

    /// <summary>Applies the clipboard switches, size limit and each peer's "share clipboard" live.</summary>
    void ApplyClipboardSettings();

    /// <summary>Re-reads every global hotkey (toggle, cursor lock, lock all, the peers' jump hotkeys).</summary>
    void ApplyHotkeySetting();

    /// <summary>Applies the crossing guards (the full-screen check needs starting or stopping).</summary>
    void ApplyCrossingSettings();
    void ApplyPowerSetting();

    /// <summary>A short fingerprint of this PC's identity key, as paired peers show it.</summary>
    string IdentityFingerprint { get; }

    /// <summary>
    /// Forgets the identity key pinned for a peer, so the next connection pairs with the pairing code
    /// again. For a peer that was reinstalled. False when there is no such peer.
    /// </summary>
    Task<bool> ForgetPeerIdentityAsync(string peerId);

    /// <summary>Restarts the listener and discovery if the saved port numbers or machine name changed.</summary>
    void ApplyNetworkSettings();

    /// <summary>Call after saving a new pairing code: drops connections made with the old one. True when any were dropped.</summary>
    bool ApplyPairingCode();

    /// <summary>Why the last connect to this peer failed, or null when it connected or was not tried.</summary>
    PeerConnectFailure? GetLastConnectFailure(string peerId);

    /// <summary>Set when the listen port could not be opened.</summary>
    NetworkStartError? ListenerError { get; }

    /// <summary>Set when the discovery port could not be opened.</summary>
    NetworkStartError? DiscoveryError { get; }

    /// <summary>Unknown machines with the pairing code that asked to connect, oldest first.</summary>
    IReadOnlyList<PendingPeer> PendingPeers { get; }

    /// <summary>Adds a pending machine as a peer (on the first free edge unless given) and connects. Null when it is no longer pending.</summary>
    PeerConfig? AllowPendingPeer(string machineId, ScreenPosition? position = null);

    /// <summary>Refuses a pending machine for the rest of this session.</summary>
    void IgnorePendingPeer(string machineId);

    /// <summary>Disconnects, removes and blocks a configured peer, and saves.</summary>
    Task RemovePeerAsync(PeerConfig peer);

    /// <summary>Takes a machine off the blocked list (it was added again by hand). Saves.</summary>
    void UnblockMachine(string machineId);

    /// <summary>True when the separately installed desktop service (UAC / lock screen) is on this machine.</summary>
    bool DesktopServiceInstalled { get; }
    DesktopServiceState DesktopServiceState { get; }

    /// <summary>
    /// Starts or stops the desktop service as needed (one UAC prompt) and connects to or lets go of it.
    /// Reads the setting already stored. Returns false when the service could not be started.
    /// </summary>
    Task<bool> ApplyDesktopServiceSettingAsync(bool enabled);

    /// <summary>This PC's monitors as they are now.</summary>
    MonitorLayout LocalLayout { get; }

    /// <summary>The monitors a connected peer has, or null when it is not connected.</summary>
    IReadOnlyList<MonitorRect>? GetPeerMonitors(string peerId);

    /// <summary>
    /// Goes up each time a peer's monitors or this PC's change (and placements may have moved), so
    /// the Layout page can redraw. Read on the UI timer; the change itself happens on a background thread.
    /// </summary>
    int ScreensVersion { get; }

    /// <summary>Applies saved placements: rebuilds the layout the mouse crosses on and sends it to the peers.</summary>
    void ApplyLayout();

    /// <summary>Whether Windows starts RoboMouse at sign-in (Run key or Store startup task).</summary>
    Task<StartupState> GetStartupStateAsync();

    /// <summary>Turns "start with Windows" on or off; returns what Windows reports afterwards.</summary>
    Task<StartupState> ApplyStartupAsync(bool enabled);
}

/// <summary>The real backend: a thin adapter over <see cref="RoboMouseService"/>.</summary>
public sealed class ServiceBackend : IAppBackend
{
    private readonly RoboMouseService _service;
    private readonly AppSettings _settings;

    private int _screensVersion;

    public ServiceBackend(RoboMouseService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        _service.ScreensChanged += (_, _) => Interlocked.Increment(ref _screensVersion);
    }

    public void SaveSettings() => _settings.Save();

    public bool Enabled => _service.Enabled;
    public bool IsControllingRemote => _service.IsControllingRemote;
    public bool IsControlledByRemote => _service.IsControlledByRemote;
    public string? ActivePeerName => _service.ActivePeer?.Name;
    public InputBlockReason RemoteInputBlockReason => _service.RemoteInputBlockReason;

    public IReadOnlyList<ConnectedPeerInfo> ConnectedPeers =>
        _service.ConnectedPeers.Select(c => new ConnectedPeerInfo(c.PeerId, c.PeerName, c.RoundTripMs)).ToList();

    public IReadOnlyList<DiscoveredPeer> DiscoveredPeers => _service.DiscoveredPeers.ToList();

    public ConnectedPeerInfo? GetConnection(string peerId)
    {
        var c = _service.GetConnection(peerId);
        return c == null ? null : new ConnectedPeerInfo(c.PeerId, c.PeerName, c.RoundTripMs);
    }

    public bool IsPeerConnected(string peerId) => _service.IsPeerConnected(peerId);
    public Task SetPeerEnabledAsync(PeerConfig peer, bool enabled) => _service.SetPeerEnabledAsync(peer, enabled);
    public Task ConnectToPeerAsync(PeerConfig peer, CancellationToken ct) => _service.ConnectToPeerAsync(peer, ct);
    public Task DisconnectFromPeerAsync(string peerId) => _service.DisconnectFromPeerAsync(peerId);
    public Task<ConnectionTestResult> TestConnectionAsync(string address, int port, CancellationToken ct) => _service.TestConnectionAsync(address, port, ct);
    public void ApplyClipboardSettings() => _service.ApplyClipboardSettings();
    public void ApplyHotkeySetting() => _service.ApplyHotkeySetting();
    public void ApplyCrossingSettings() => _service.ApplyCrossingSettings();
    public string IdentityFingerprint => _service.IdentityFingerprint;
    public Task<bool> ForgetPeerIdentityAsync(string peerId) => _service.ForgetPeerIdentityAsync(peerId);
    public void ApplyPowerSetting() => _service.UpdatePowerFollowing();
    public void ApplyNetworkSettings() => _service.ApplyNetworkSettings();
    public bool ApplyPairingCode() => _service.ApplyPairingCode();
    public PeerConnectFailure? GetLastConnectFailure(string peerId) => _service.GetLastConnectFailure(peerId);
    public NetworkStartError? ListenerError => _service.ListenerError;
    public NetworkStartError? DiscoveryError => _service.DiscoveryError;
    public IReadOnlyList<PendingPeer> PendingPeers => _service.PendingPeers;
    public PeerConfig? AllowPendingPeer(string machineId, ScreenPosition? position = null) => _service.AllowPendingPeer(machineId, position);
    public void IgnorePendingPeer(string machineId) => _service.IgnorePendingPeer(machineId);
    public Task RemovePeerAsync(PeerConfig peer) => _service.RemovePeerAsync(peer);
    public void UnblockMachine(string machineId) => _service.UnblockMachine(machineId);

    public bool DesktopServiceInstalled => DesktopServiceControl.IsInstalled;
    public DesktopServiceState DesktopServiceState => _service.DesktopServiceState;

    public async Task<bool> ApplyDesktopServiceSettingAsync(bool enabled)
    {
        // Only touch the service (and prompt) when its running state has to change.
        var ok = DesktopServiceControl.IsRunning == enabled || await DesktopServiceControl.SetRunningAsync(enabled);
        _service.ApplyDesktopServiceSetting();
        return ok || !enabled;
    }

    public MonitorLayout LocalLayout => _service.LocalLayout;
    public IReadOnlyList<MonitorRect>? GetPeerMonitors(string peerId) => _service.GetPeerMonitors(peerId);
    public int ScreensVersion => Volatile.Read(ref _screensVersion);
    public void ApplyLayout() => _service.ApplyLayout();

    public Task<StartupState> GetStartupStateAsync() => StartupRegistration.GetStateAsync();
    public Task<StartupState> ApplyStartupAsync(bool enabled) => StartupRegistration.ApplyAsync(enabled);
}
