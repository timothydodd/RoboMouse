using RoboMouse.App;
using RoboMouse.App.Services;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;
using RoboMouse.Core.Network;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.App.Tests;

/// <summary>A backend with nothing connected. <see cref="ConnectError"/> makes connects fail.</summary>
internal sealed class FakeBackend : IAppBackend
{
    public Exception? ConnectError { get; set; }
    public List<PeerConfig> Connected { get; } = new();
    public List<bool> StartupApplied { get; } = new();

    public bool Enabled => true;
    public bool IsControllingRemote => false;
    public bool IsControlledByRemote => false;
    public string? ActivePeerName => null;
    public InputBlockReason RemoteInputBlockReason => InputBlockReason.None;
    public IReadOnlyList<ConnectedPeerInfo> ConnectedPeers => Array.Empty<ConnectedPeerInfo>();
    public List<DiscoveredPeer> Discovered { get; } = new();
    public IReadOnlyList<DiscoveredPeer> DiscoveredPeers => Discovered.ToList();
    public ConnectedPeerInfo? GetConnection(string peerId) => null;
    public bool IsPeerConnected(string peerId) => Connected.Any(p => p.Id == peerId);
    public Task SetPeerEnabledAsync(PeerConfig peer, bool enabled) { peer.Enabled = enabled; return Task.CompletedTask; }

    public Task ConnectToPeerAsync(PeerConfig peer, CancellationToken ct)
    {
        if (ConnectError != null)
            throw ConnectError;
        Connected.Add(peer);
        return Task.CompletedTask;
    }

    public Task DisconnectFromPeerAsync(string peerId) => Task.CompletedTask;
    public Task<ConnectionTestResult> TestConnectionAsync(string address, int port, CancellationToken ct) =>
        Task.FromResult(new ConnectionTestResult { Success = false, Error = "fake" });
    public int Saves { get; private set; }
    public void SaveSettings() => Saves++;

    public int ClipboardApplied { get; private set; }
    public int NetworkApplied { get; private set; }
    public int PairingApplied { get; private set; }
    public List<PeerConfig> Removed { get; } = new();
    public List<string> Unblocked { get; } = new();
    public List<string> Ignored { get; } = new();
    public List<PendingPeer> Pending { get; } = new();
    public Dictionary<string, PeerConnectFailure> Failures { get; } = new();
    public AppSettings? Settings { get; set; }

    public void ApplyClipboardSetting() => ClipboardApplied++;
    public void ApplyHotkeySetting() { }
    public void ApplyPowerSetting() { }
    public void ApplyNetworkSettings() => NetworkApplied++;
    public bool ApplyPairingCode() { PairingApplied++; return true; }
    public PeerConnectFailure? GetLastConnectFailure(string peerId) => Failures.GetValueOrDefault(peerId);
    public NetworkStartError? ListenerError { get; set; }
    public NetworkStartError? DiscoveryError { get; set; }
    public IReadOnlyList<PendingPeer> PendingPeers => Pending.ToList();

    /// <summary>Like the service: the pending machine becomes a peer on <paramref name="position"/> or the first free edge.</summary>
    public PeerConfig? AllowPendingPeer(string machineId, ScreenPosition? position = null)
    {
        var pending = Pending.FirstOrDefault(p => p.MachineId == machineId);
        if (pending == null)
            return null;
        Pending.Remove(pending);
        var config = new PeerConfig
        {
            Id = pending.MachineId,
            Name = pending.MachineName,
            Address = pending.Address,
            Port = pending.Port,
            Position = position ?? (Settings != null ? PeerActions.FirstFreeEdge(Settings) ?? ScreenPosition.Right : ScreenPosition.Right)
        };
        Settings?.Peers.Add(config);
        return config;
    }

    public void IgnorePendingPeer(string machineId)
    {
        Ignored.Add(machineId);
        Pending.RemoveAll(p => p.MachineId == machineId);
    }

    public Task RemovePeerAsync(PeerConfig peer)
    {
        Removed.Add(peer);
        Settings?.Peers.Remove(peer);
        Settings?.BlockedMachineIds.Add(peer.Id);
        return Task.CompletedTask;
    }

    public void UnblockMachine(string machineId) => Unblocked.Add(machineId);
    public bool DesktopServiceInstalled => false;
    public DesktopServiceState DesktopServiceState => DesktopServiceState.Off;
    public Task<bool> ApplyDesktopServiceSettingAsync(bool enabled) => Task.FromResult(true);
    public Task<StartupState> GetStartupStateAsync() => Task.FromResult(StartupState.Off);

    public Task<StartupState> ApplyStartupAsync(bool enabled)
    {
        StartupApplied.Add(enabled);
        return Task.FromResult(enabled ? StartupState.On : StartupState.Off);
    }
}

/// <summary>Records every message and answers questions with <see cref="Answer"/>.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public List<string> Messages { get; } = new();
    public DialogResult Answer { get; set; } = DialogResult.Yes;

    public Task<DialogResult> ShowMessageAsync(string text, string title = "RoboMouse",
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.None)
    {
        Messages.Add(text);
        return Task.FromResult(buttons == DialogButtons.OK ? DialogResult.OK : Answer);
    }

    public Task<PeerConfig?> ShowPeerSetupAsync(PeerConfig? peer, AppSettings settings) => Task.FromResult<PeerConfig?>(null);
    public Task CopyTextAsync(string text) => Task.CompletedTask;

    /// <summary>What <see cref="PickSaveFileAsync"/> answers; null acts as Cancel.</summary>
    public string? SavePath { get; set; }
    public List<string> Opened { get; } = new();

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult(SavePath);
    public void Open(string pathOrUrl) => Opened.Add(pathOrUrl);
}

internal static class Samples
{
    /// <summary>Two peers, Laptop on the left (shifted down 120 px) and Mac mini on the right.</summary>
    public static AppSettings Settings() => new()
    {
        MachineId = "this-pc",
        MachineName = "DESKTOP",
        PairingCode = "K7PQ-M2XW-9DHR",
        ToggleHotkey = "Ctrl+Alt+M",
        Peers =
        {
            new PeerConfig { Id = "laptop", Name = "Laptop", Address = "192.168.1.20", Port = 24800, Position = ScreenPosition.Left, OffsetY = 120 },
            new PeerConfig { Id = "mac", Name = "Mac mini", Address = "192.168.1.31", Port = 24800, Position = ScreenPosition.Right }
        }
    };
}
