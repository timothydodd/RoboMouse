using System.Net;
using RoboMouse.App.Services;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;
using RoboMouse.Core.Screen;

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
    public void ApplyClipboardSettings() { }
    public void ApplyHotkeySetting() { }
    public void ApplyCrossingSettings() { }
    public string IdentityFingerprint => "1A2B-3C4D-5E6F-7A8B-9C0D";
    public Task<bool> ForgetPeerIdentityAsync(string peerId) => Task.FromResult(true);
    public void ApplyPowerSetting() { }
    public void SaveSettings() { }
    public void ApplyNetworkSettings() { }
    public bool ApplyPairingCode() => false;

    /// <summary>Mac mini is switched on for the error-state previews; this is why it did not connect.</summary>
    public PeerConnectFailure? GetLastConnectFailure(string peerId) => peerId switch
    {
        "mac" => new PeerConnectFailure(PeerFailureKind.PairingCodeMismatch, "The pairing code doesn't match. Enter the same code on both machines (Settings > Network).", DateTime.Now),
        "nas" => new PeerConnectFailure(PeerFailureKind.IdentityMismatch, "The machine at 192.168.1.50 claims to be NAS-BOX but does not have its identity key.", DateTime.Now),
        _ => null
    };
    public NetworkStartError? ListenerError { get; set; }
    public NetworkStartError? DiscoveryError { get; set; }
    public List<PendingPeer> Pending { get; } = new();
    public IReadOnlyList<PendingPeer> PendingPeers => Pending;
    public PeerConfig? AllowPendingPeer(string machineId, ScreenPosition? position = null) { Pending.RemoveAll(p => p.MachineId == machineId); return null; }
    public void IgnorePendingPeer(string machineId) => Pending.RemoveAll(p => p.MachineId == machineId);
    public Task RemovePeerAsync(PeerConfig peer) => Task.CompletedTask;
    public void UnblockMachine(string machineId) { }
    public bool DesktopServiceInstalled => true;
    public RoboMouse.Core.Input.DesktopServiceState DesktopServiceState => RoboMouse.Core.Input.DesktopServiceState.Active;
    public Task<bool> ApplyDesktopServiceSettingAsync(bool enabled) => Task.FromResult(true);
    /// <summary>This PC: one 2560x1440 display unless a preview sets another arrangement.</summary>
    public Func<MonitorLayout> LocalLayoutSource { get; set; } = () => new MonitorLayout(new[]
    {
        new MonitorRect(new System.Drawing.Rectangle(0, 0, 2560, 1440), default, true, "\\\\.\\DISPLAY1")
    });

    public MonitorLayout LocalLayout => LocalLayoutSource();

    /// <summary>The laptop is connected with two monitors; the Mac mini is offline.</summary>
    public IReadOnlyList<MonitorRect>? GetPeerMonitors(string peerId) => peerId == "laptop"
        ? new[]
        {
            new MonitorRect(new System.Drawing.Rectangle(0, 0, 2560, 1440), default, true, "\\\\.\\DISPLAY1"),
            new MonitorRect(new System.Drawing.Rectangle(2560, 0, 1920, 1080), default, false, "\\\\.\\DISPLAY2")
        }
        : null;

    public int ScreensVersion => 0;
    public void ApplyLayout() { }

    public Task<RoboMouse.App.StartupState> GetStartupStateAsync() => Task.FromResult(RoboMouse.App.StartupState.On);
    public Task<RoboMouse.App.StartupState> ApplyStartupAsync(bool enabled) =>
        Task.FromResult(enabled ? RoboMouse.App.StartupState.On : RoboMouse.App.StartupState.Off);

    public static AppSettings SampleSettings() => new()
    {
        MachineName = "DESKTOP-TIM",
        PairingCode = "K7PQ-M2XW-9DHR",
        Peers =
        {
            // Its two monitors are split up: the big one left of this PC, the small one right of it.
            new PeerConfig
            {
                Id = "laptop", Name = "Laptop", Address = "192.168.1.20", Position = ScreenPosition.Left, ScreenWidth = 4480, ScreenHeight = 1440, OffsetY = 120,
                Monitors =
                {
                    new MonitorPlacement { Id = "\\\\.\\DISPLAY1", X = -2560, Y = 120, Width = 2560, Height = 1440, RemoteWidth = 2560, RemoteHeight = 1440, Primary = true },
                    new MonitorPlacement { Id = "\\\\.\\DISPLAY2", X = 2560, Y = 0, Width = 1920, Height = 1080, RemoteX = 2560, RemoteWidth = 1920, RemoteHeight = 1080 }
                }
            },
            // Sharing the right-hand side with the laptop's second monitor, below it.
            new PeerConfig
            {
                Id = "mac", Name = "Mac mini", Address = "192.168.1.31", Position = ScreenPosition.Right, ScreenWidth = 1920, ScreenHeight = 1080, Enabled = false,
                Monitors = { new MonitorPlacement { Id = "\\\\.\\DISPLAY1", X = 2560, Y = 1080, Width = 1920, Height = 1080, RemoteWidth = 1920, RemoteHeight = 1080, Primary = true } }
            }
        }
    };
}
