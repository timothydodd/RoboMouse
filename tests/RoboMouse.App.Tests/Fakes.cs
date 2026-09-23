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
    public IReadOnlyList<DiscoveredPeer> DiscoveredPeers => Array.Empty<DiscoveredPeer>();
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
    public void ApplyClipboardSetting() { }
    public void ApplyHotkeySetting() { }
    public void ApplyPowerSetting() { }
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
