using System.Net;
using RoboMouse.App.Services;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.UiPreview;

/// <summary>Canned service state so the windows render with realistic content.</summary>
internal sealed class FakeBackend : IAppBackend
{
    public bool Enabled => true;
    public bool IsControllingRemote => false;
    public bool IsControlledByRemote => false;
    public string? ActivePeerName => null;
    public RoboMouse.Core.Network.Protocol.InputBlockReason RemoteInputBlockReason => RoboMouse.Core.Network.Protocol.InputBlockReason.None;

    public IReadOnlyList<ConnectedPeerInfo> ConnectedPeers { get; } = new[]
    {
        new ConnectedPeerInfo("laptop", "Laptop", 3)
    };

    public IReadOnlyList<DiscoveredPeer> DiscoveredPeers { get; } = new[]
    {
        new DiscoveredPeer { MachineId = "studio", MachineName = "STUDIO-PC", Address = IPAddress.Parse("192.168.1.42"), Port = 24800, ScreenWidth = 3840, ScreenHeight = 2160 }
    };

    public ConnectedPeerInfo? GetConnection(string peerId) => ConnectedPeers.FirstOrDefault(c => c.PeerId == peerId);
    public bool IsPeerConnected(string peerId) => GetConnection(peerId) != null;
    public Task SetPeerEnabledAsync(PeerConfig peer, bool enabled) { peer.Enabled = enabled; return Task.CompletedTask; }
    public Task ConnectToPeerAsync(PeerConfig peer, CancellationToken ct) => Task.CompletedTask;
    public Task DisconnectFromPeerAsync(string peerId) => Task.CompletedTask;
    public Task<ConnectionTestResult> TestConnectionAsync(string address, int port, CancellationToken ct) =>
        Task.FromResult(new ConnectionTestResult { Success = true, PeerName = "Laptop", PeerScreenWidth = 2560, PeerScreenHeight = 1440, RoundTripMs = 2 });
    public void ApplyClipboardSetting() { }
    public void ApplyHotkeySetting() { }
    public bool DesktopServiceInstalled => true;
    public RoboMouse.Core.Input.DesktopServiceState DesktopServiceState => RoboMouse.Core.Input.DesktopServiceState.Active;
    public Task<bool> ApplyDesktopServiceSettingAsync(bool enabled) => Task.FromResult(true);

    public static AppSettings SampleSettings() => new()
    {
        MachineName = "DESKTOP-TIM",
        PairingCode = "K7PQ-M2XW-9DHR",
        Peers =
        {
            new PeerConfig { Id = "laptop", Name = "Laptop", Address = "192.168.1.20", Position = ScreenPosition.Left, ScreenWidth = 2560, ScreenHeight = 1440, OffsetY = 120 },
            new PeerConfig { Id = "mac", Name = "Mac mini", Address = "192.168.1.31", Position = ScreenPosition.Right, ScreenWidth = 1920, ScreenHeight = 1080, Enabled = false }
        }
    };
}
