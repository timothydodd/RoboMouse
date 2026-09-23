using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;

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

    void ApplyClipboardSetting();
    void ApplyHotkeySetting();
    void ApplyPowerSetting();

    /// <summary>True when the separately installed desktop service (UAC / lock screen) is on this machine.</summary>
    bool DesktopServiceInstalled { get; }
    DesktopServiceState DesktopServiceState { get; }

    /// <summary>
    /// Starts or stops the desktop service as needed (one UAC prompt) and connects to or lets go of it.
    /// Reads the setting already stored. Returns false when the service could not be started.
    /// </summary>
    Task<bool> ApplyDesktopServiceSettingAsync(bool enabled);

    /// <summary>Whether Windows starts RoboMouse at sign-in (Run key or Store startup task).</summary>
    Task<StartupState> GetStartupStateAsync();

    /// <summary>Turns "start with Windows" on or off; returns what Windows reports afterwards.</summary>
    Task<StartupState> ApplyStartupAsync(bool enabled);
}

/// <summary>The real backend: a thin adapter over <see cref="RoboMouseService"/>.</summary>
public sealed class ServiceBackend : IAppBackend
{
    private readonly RoboMouseService _service;

    public ServiceBackend(RoboMouseService service) => _service = service;

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
    public void ApplyClipboardSetting() => _service.ApplyClipboardSetting();
    public void ApplyHotkeySetting() => _service.ApplyHotkeySetting();
    public void ApplyPowerSetting() => _service.UpdatePowerFollowing();

    public bool DesktopServiceInstalled => DesktopServiceControl.IsInstalled;
    public DesktopServiceState DesktopServiceState => _service.DesktopServiceState;

    public async Task<bool> ApplyDesktopServiceSettingAsync(bool enabled)
    {
        // Only touch the service (and prompt) when its running state has to change.
        var ok = DesktopServiceControl.IsRunning == enabled || await DesktopServiceControl.SetRunningAsync(enabled);
        _service.ApplyDesktopServiceSetting();
        return ok || !enabled;
    }

    public Task<StartupState> GetStartupStateAsync() => StartupRegistration.GetStateAsync();
    public Task<StartupState> ApplyStartupAsync(bool enabled) => StartupRegistration.ApplyAsync(enabled);

}
