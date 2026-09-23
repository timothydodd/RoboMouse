using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;

namespace RoboMouse.App.ViewModels;

/// <summary>A navigation entry in the settings window.</summary>
public sealed record NavigationItem(string Title, Symbol Icon, PageViewModel Page);

/// <summary>A settings page. <see cref="IsActive"/> drives which page view is visible.</summary>
public abstract partial class PageViewModel : ValidatingObservableObject
{
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// The settings window: live status, navigation between pages, and Save/Cancel. Each page owns its
/// own fields; Save copies all of them into <see cref="AppSettings"/> at once, as before.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IAppBackend _backend;
    private readonly IDialogService _dialogs;

    public GeneralPageViewModel General { get; }
    public NetworkPageViewModel Network { get; }
    public PeersPageViewModel Peers { get; }
    public LayoutPageViewModel Layout { get; }

    public IReadOnlyList<NavigationItem> Pages { get; }

    [ObservableProperty]
    private NavigationItem? _selectedPage;

    public string Version { get; }
    public string MachineName => _settings.MachineName;

    [ObservableProperty]
    private string _statusText = "Not connected";

    [ObservableProperty]
    private StatusTone _statusTone = StatusTone.Idle;

    /// <summary>Raised when Save succeeded and the window should close.</summary>
    public event EventHandler? CloseRequested;

    public SettingsViewModel(AppSettings settings, IAppBackend backend, IDialogService dialogs, string version)
    {
        _settings = settings;
        _backend = backend;
        _dialogs = dialogs;
        Version = version;

        General = new GeneralPageViewModel(settings, backend.DesktopServiceInstalled, backend.DesktopServiceState);
        Network = new NetworkPageViewModel(settings, dialogs);
        Peers = new PeersPageViewModel(settings, backend, dialogs);
        Layout = new LayoutPageViewModel(settings);

        Pages = new[]
        {
            new NavigationItem("General", Symbol.Settings, General),
            new NavigationItem("Network", Symbol.Globe, Network),
            new NavigationItem("Peers", Symbol.People, Peers),
            new NavigationItem("Layout", Symbol.Board, Layout)
        };
        _selectedPage = Pages[0];
        General.IsActive = true;

        Peers.PeersChanged += (_, _) => Layout.Reload();
        Refresh();
        _ = LoadStartupStateAsync();
    }

    private async Task LoadStartupStateAsync()
    {
        try
        {
            General.ShowStartupState(await _backend.GetStartupStateAsync());
        }
        catch (Exception ex)
        {
            Core.Logging.SimpleLogger.Log("Startup", $"Could not read the startup state: {ex.Message}");
        }
    }

    /// <summary>Pulls the live status from the service. Called by the window on a timer.</summary>
    public void Refresh()
    {
        var connected = _backend.ConnectedPeers;
        (StatusText, StatusTone) = !_backend.Enabled ? ("Sharing off", StatusTone.Idle)
            : _backend.IsControllingRemote ? (RoboMouse.App.StatusText.Controlling(_backend.ActivePeerName, _backend.RemoteInputBlockReason),
                _backend.RemoteInputBlockReason == Core.Network.Protocol.InputBlockReason.None ? StatusTone.Accent : StatusTone.Warning)
            : _backend.IsControlledByRemote ? ("Being controlled", StatusTone.Warning)
            : connected.Count == 0 ? ("Not connected", StatusTone.Idle)
            : connected.Count == 1 ? ($"Connected to {connected[0].PeerName}", StatusTone.Ok)
            : ($"Connected to {connected.Count} peers", StatusTone.Ok);

        Peers.Refresh();
    }

    partial void OnSelectedPageChanged(NavigationItem? value)
    {
        foreach (var page in Pages)
            page.Page.IsActive = page == value;
        if (value?.Page == Layout)
            Layout.Reload();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var code = Network.PairingCode.Trim().ToUpperInvariant();
        if (code.Replace("-", "").Replace(" ", "").Length < 8)
        {
            SelectedPage = Pages.First(p => p.Page == Network);
            await _dialogs.WarnAsync("The pairing code must be at least 8 characters.");
            return;
        }

        // Without a hotkey there is no way back from a remote screen that stops answering.
        var hotkey = General.ToggleHotkey.Trim();
        if (Hotkey.Parse(hotkey) == null)
        {
            SelectedPage = Pages.First(p => p.Page == General);
            await _dialogs.WarnAsync(hotkey.Length == 0
                ? "Choose a toggle hotkey. It is how you get the mouse back if another screen stops responding."
                : "The hotkey must be a key with at least one modifier, for example Ctrl+Alt+M.");
            return;
        }

        if (Network.HasErrors)
        {
            SelectedPage = Pages.First(p => p.Page == Network);
            await _dialogs.WarnAsync("Some fields on the Network page are empty or out of range. Fix the highlighted fields, then save again.");
            return;
        }

        _settings.PairingCode = code;
        _settings.MachineName = string.IsNullOrWhiteSpace(General.MachineName) ? _settings.MachineName : General.MachineName.Trim();
        var startupChanged = _settings.StartWithWindows != General.StartWithWindows;
        _settings.StartWithWindows = General.StartWithWindows;
        _settings.StartMinimized = General.StartMinimized;
        _settings.ToggleHotkey = hotkey;
        _settings.Clipboard.Enabled = General.ShareClipboard;
        _settings.Clipboard.SyncFiles = General.ShareFiles;
        _settings.EdgeHighlight = General.SelectedHighlight.Style;
        _settings.WrapAround = General.WrapAround;
        _settings.WakeOnEdge = General.WakeOnEdge;
        _settings.FollowHostPower = General.FollowHostPower;
        _settings.DebugPanelEnabled = General.ShowDebugPanel;
        var desktopServiceChanged = _settings.UseDesktopService != General.UseDesktopService;
        _settings.UseDesktopService = General.UseDesktopService;
        _settings.LocalPort = (int)Network.LocalPort!.Value;
        _settings.DiscoveryPort = (int)Network.DiscoveryPort!.Value;
        Layout.Save();

        _settings.Save();
        _backend.ApplyClipboardSetting();
        _backend.ApplyHotkeySetting();
        _backend.ApplyPowerSetting();

        // Startup is applied whatever happens to the desktop service below.
        var startup = await _backend.ApplyStartupAsync(_settings.StartWithWindows);
        General.ShowStartupState(startup);
        if (startupChanged && _settings.StartWithWindows && startup is StartupState.DisabledByUser or StartupState.DisabledByPolicy)
        {
            await _dialogs.WarnAsync(startup == StartupState.DisabledByUser
                ? "RoboMouse was turned off in the Startup apps list (Task Manager or Settings > Apps > Startup), so it will not start with Windows until it is turned back on there."
                : "A policy on this PC stops RoboMouse from starting with Windows.");
        }

        if (desktopServiceChanged && !await _backend.ApplyDesktopServiceSettingAsync(_settings.UseDesktopService))
        {
            _settings.UseDesktopService = false;
            _settings.Save();
            General.UseDesktopService = false;
            await _dialogs.WarnAsync("The RoboMouse desktop service could not be started, so UAC prompts and the lock screen stay out of reach. Approve the Windows prompt when turning this on.");
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Colour family for a status indicator; the view maps it to a theme brush.</summary>
public enum StatusTone { Idle, Ok, Warning, Error, Accent }

/// <summary>General page: identity, startup, hotkey, clipboard, display.</summary>
public sealed partial class GeneralPageViewModel : PageViewModel
{
    public sealed record HighlightChoice(EdgeHighlightStyle Style, string Text)
    {
        public override string ToString() => Text;
    }

    public IReadOnlyList<HighlightChoice> HighlightChoices { get; } = new[]
    {
        new HighlightChoice(EdgeHighlightStyle.None, "Show nothing"),
        new HighlightChoice(EdgeHighlightStyle.Border, "Flash a border around the screen"),
        new HighlightChoice(EdgeHighlightStyle.Fade, "Glow along the edge it came in on")
    };

    [ObservableProperty] private string _machineName;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _toggleHotkey;
    [ObservableProperty] private bool _shareClipboard;
    [ObservableProperty] private bool _shareFiles;
    [ObservableProperty] private HighlightChoice _selectedHighlight;
    [ObservableProperty] private bool _wrapAround;
    [ObservableProperty] private bool _wakeOnEdge;
    [ObservableProperty] private bool _followHostPower;
    [ObservableProperty] private bool _showDebugPanel;
    [ObservableProperty] private bool _useDesktopService;

    /// <summary>Under "Start with Windows": says when Windows itself has it turned off.</summary>
    [ObservableProperty] private string? _startupDescription;

    /// <summary>Shows what Windows reports for the startup entry.</summary>
    public void ShowStartupState(StartupState state) => StartupDescription = state switch
    {
        StartupState.DisabledByUser => "Turned off in the Startup apps list (Task Manager or Settings > Apps > Startup). Turn it back on there.",
        StartupState.DisabledByPolicy => "Blocked by a policy on this PC.",
        _ => null
    };

    /// <summary>The desktop service is a separate download; the card only appears once it is installed.</summary>
    public bool DesktopServiceInstalled { get; }
    public string DesktopServiceDescription { get; }

    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public GeneralPageViewModel(AppSettings settings, bool desktopServiceInstalled = false, DesktopServiceState desktopServiceState = DesktopServiceState.Off)
    {
        DesktopServiceInstalled = desktopServiceInstalled;
        _useDesktopService = settings.UseDesktopService;
        DesktopServiceDescription = "Lets the PC controlling this one click UAC prompts, sign in at the lock screen and use windows running as administrator. " + desktopServiceState switch
        {
            DesktopServiceState.Active => "Working now.",
            DesktopServiceState.Connecting => "Waiting for the RoboMouse desktop service to answer.",
            DesktopServiceState.Incompatible => "The installed desktop service is a different version from this app; update it.",
            _ => "Windows asks for permission once when you turn this on."
        };
        _machineName = settings.MachineName;
        _startWithWindows = settings.StartWithWindows;
        _startMinimized = settings.StartMinimized;
        _toggleHotkey = settings.ToggleHotkey ?? string.Empty;
        _shareClipboard = settings.Clipboard.Enabled;
        _shareFiles = settings.Clipboard.SyncFiles;
        _selectedHighlight = HighlightChoices.FirstOrDefault(c => c.Style == settings.EdgeHighlight) ?? HighlightChoices[2];
        _wrapAround = settings.WrapAround;
        _wakeOnEdge = settings.WakeOnEdge;
        _followHostPower = settings.FollowHostPower;
        _showDebugPanel = settings.DebugPanelEnabled;
    }
}

/// <summary>Network page: pairing code, ports, firewall.</summary>
public sealed partial class NetworkPageViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;

    [ObservableProperty] private string _pairingCode;
    [ObservableProperty] private decimal? _localPort;
    [ObservableProperty] private decimal? _discoveryPort;

    partial void OnLocalPortChanged(decimal? value) => RequireValue(value, nameof(LocalPort), "Enter a port number.");
    partial void OnDiscoveryPortChanged(decimal? value) => RequireValue(value, nameof(DiscoveryPort), "Enter a port number.");
    public string MachineId { get; }

    /// <summary>This computer's IPv4 addresses, one per connected adapter, for typing into another machine.</summary>
    public IReadOnlyList<string> Addresses { get; } = GetLocalAddresses();
    public bool HasAddresses => Addresses.Count > 0;

    private static IReadOnlyList<string> GetLocalAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => $"{a.Address}  ·  {n.Name}"))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public NetworkPageViewModel(AppSettings settings, IDialogService dialogs)
    {
        _dialogs = dialogs;
        _pairingCode = settings.PairingCode;
        _localPort = settings.LocalPort;
        _discoveryPort = settings.DiscoveryPort;
        MachineId = settings.MachineId;
    }

    [RelayCommand]
    private Task CopyPairingCodeAsync() => _dialogs.CopyTextAsync(PairingCode);

    [RelayCommand]
    private async Task GeneratePairingCodeAsync()
    {
        if (await _dialogs.ConfirmAsync("Generate a new pairing code? Every other machine will need the new code before it can connect again."))
            PairingCode = Core.Network.SecureChannel.GeneratePairingCode();
    }

    /// <summary>
    /// The elevated command behind "Add rules": replaces any previous RoboMouse rules with inbound
    /// rules for this program only, on private and domain networks, from the local subnet.
    /// </summary>
    internal static string BuildFirewallScript(string programPath, int tcpPort, int udpPort)
    {
        const string scope = "remoteip=localsubnet profile=private,domain";
        var program = $"program=\"{programPath}\"";
        return
            "netsh advfirewall firewall delete rule name=\"RoboMouse (TCP)\" & " +
            "netsh advfirewall firewall delete rule name=\"RoboMouse (UDP)\" & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (TCP)\" dir=in action=allow protocol=TCP localport={tcpPort} {program} {scope} & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (UDP)\" dir=in action=allow protocol=UDP localport={udpPort} {program} {scope}";
    }

    [RelayCommand]
    private async Task AllowThroughFirewallAsync()
    {
        if (LocalPort is not { } localPort || DiscoveryPort is not { } discoveryPort || Environment.ProcessPath is not { } programPath)
        {
            await _dialogs.WarnAsync("Enter both port numbers first.");
            return;
        }
        var tcp = (int)localPort;
        var udp = (int)discoveryPort;
        var script = BuildFirewallScript(programPath, tcp, udp);

        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + script,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
            using (process)
            {
                // ExitCode throws while the process is still running, so only read it once it has exited.
                var exited = process != null && await Task.Run(() => process.WaitForExit(15000));
                if (exited && process!.ExitCode == 0)
                    await _dialogs.InfoAsync($"Firewall rules added for TCP {tcp} and UDP {udp} (private and domain networks, this subnet).");
                else if (process != null && !exited)
                    await _dialogs.WarnAsync("The firewall command is taking longer than expected. Check Windows Defender Firewall with Advanced Security for the RoboMouse rules.");
                else
                    await _dialogs.WarnAsync("The firewall command did not complete. You can add the rules manually in Windows Defender Firewall with Advanced Security.");
            }

        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the elevation prompt.
        }
        catch (Exception ex)
        {
            await _dialogs.ErrorAsync($"Could not update the firewall: {ex.Message}");
        }
    }
}

/// <summary>Peers page: configured peers and machines found on the network.</summary>
public sealed partial class PeersPageViewModel : PageViewModel
{
    private readonly AppSettings _settings;
    private readonly IAppBackend _backend;
    private readonly IDialogService _dialogs;

    public ObservableCollection<PeerItemViewModel> Peers { get; } = new();
    public ObservableCollection<DiscoveredPeerViewModel> Discovered { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditPeerCommand), nameof(RemovePeerCommand), nameof(ToggleEnabledCommand))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledText))]
    private PeerItemViewModel? _selectedPeer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDiscoveredCommand))]
    private DiscoveredPeerViewModel? _selectedDiscovered;

    public bool HasPeers => Peers.Count > 0;
    public bool HasDiscovered => Discovered.Count > 0;
    public string ToggleEnabledText => SelectedPeer is { IsEnabled: false } ? "Enable" : "Disable";

    /// <summary>Raised when peers were added, edited or removed (the layout canvas listens).</summary>
    public event EventHandler? PeersChanged;

    public PeersPageViewModel(AppSettings settings, IAppBackend backend, IDialogService dialogs)
    {
        _settings = settings;
        _backend = backend;
        _dialogs = dialogs;
        Peers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPeers));
        Discovered.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDiscovered));
        RebuildPeers();
    }

    private bool HasSelectedPeer => SelectedPeer != null;
    private bool HasSelectedDiscovered => SelectedDiscovered != null;

    private void RebuildPeers()
    {
        var selected = SelectedPeer?.Peer;
        Peers.Clear();
        foreach (var peer in _settings.Peers)
        {
            var item = new PeerItemViewModel(peer, this);
            Peers.Add(item);
            if (peer == selected)
                SelectedPeer = item;
        }
        Refresh();
        PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Updates connection status on each row and the discovered list.</summary>
    public void Refresh()
    {
        foreach (var item in Peers)
            item.Refresh(_backend.GetConnection(item.Peer.Id));
        OnPropertyChanged(nameof(ToggleEnabledText));

        var configuredIds = _settings.Peers.Select(p => p.Id).ToHashSet();
        var configuredAddresses = _settings.Peers.Select(p => p.Address).ToHashSet();
        var found = _backend.DiscoveredPeers
            .Where(p => p.MachineId != _settings.MachineId
                        && !configuredIds.Contains(p.MachineId)
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

    [RelayCommand]
    private async Task AddPeerAsync()
    {
        var result = await _dialogs.ShowPeerSetupAsync(null, _settings);
        if (result == null)
            return;
        await AddAndConnectAsync(result);
    }

    /// <summary>
    /// The one flow for a new peer (the tray menu uses it too): add the config and save, then connect.
    /// A failed connect keeps the peer; the background retry keeps trying.
    /// </summary>
    private async Task AddAndConnectAsync(PeerConfig peer)
    {
        _settings.Peers.Add(peer);
        _settings.Save();
        RebuildPeers();

        if (!peer.Enabled)
            return;

        var error = await PeerActions.ConnectNewPeerAsync(_backend, peer);
        if (error == null)
            _settings.Save(); // the connect learned its machine id and screen size
        else
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

        _settings.Save();
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

    [RelayCommand(CanExecute = nameof(HasSelectedPeer))]
    private async Task RemovePeerAsync()
    {
        if (SelectedPeer is not { } item)
            return;
        if (!await _dialogs.ConfirmAsync($"Remove {item.Peer.Name}?"))
            return;
        if (_backend.IsPeerConnected(item.Peer.Id))
        {
            try { await _backend.DisconnectFromPeerAsync(item.Peer.Id); } catch { }
        }
        _settings.Peers.Remove(item.Peer);
        _settings.Save();
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
    [ObservableProperty] private bool _isEnabled;

    public PeerItemViewModel(PeerConfig peer, PeersPageViewModel owner)
    {
        Peer = peer;
        _owner = owner;
        _positionText = PeerPositions.Describe(peer.Position);
        _isEnabled = peer.Enabled;
    }

    public void Refresh(ConnectedPeerInfo? connection)
    {
        (StatusText, StatusTone) = !Peer.Enabled ? ("Disabled", StatusTone.Idle)
            : connection == null ? ("Not connected", StatusTone.Idle)
            : connection.RoundTripMs >= 0 ? ($"Connected · {connection.RoundTripMs} ms", StatusTone.Ok)
            : ("Connected", StatusTone.Ok);
        PositionText = PeerPositions.Describe(Peer.Position);

        _syncing = true;
        IsEnabled = Peer.Enabled;
        _syncing = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing)
            _ = _owner.SetEnabledAsync(this, value);
    }
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
