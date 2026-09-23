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

/// <summary>The settings pages, for opening the window on a particular one.</summary>
public enum SettingsPage { General, Network, Peers, Layout, About }

/// <summary>
/// The settings window: live status, navigation between pages, and Save/Cancel. Each page owns its
/// own fields; Save copies all of them into <see cref="AppSettings"/> at once and applies them to the
/// running service, so nothing waits for a restart.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IAppBackend _backend;
    private readonly IDialogService _dialogs;
    private readonly AppState _appState;

    public GeneralPageViewModel General { get; }
    public NetworkPageViewModel Network { get; }
    public PeersPageViewModel Peers { get; }
    public LayoutPageViewModel Layout { get; }
    public AboutPageViewModel About { get; }

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

    /// <param name="appState">App-only settings (update check); a throwaway one when null (tests, previews).</param>
    /// <param name="updates">The update checker, or null in the Store build.</param>
    /// <param name="diagnostics">Where "Export diagnostics" collects from; the real folders when null.</param>
    public SettingsViewModel(AppSettings settings, IAppBackend backend, IDialogService dialogs, string version,
        AppState? appState = null, UpdateChecker? updates = null, Func<DiagnosticsSources>? diagnostics = null)
    {
        _settings = settings;
        _backend = backend;
        _dialogs = dialogs;
        _appState = appState ?? new AppState();
        Version = version;

        General = new GeneralPageViewModel(settings, backend.DesktopServiceInstalled, backend.DesktopServiceState);
        Network = new NetworkPageViewModel(settings, dialogs, backend);
        Peers = new PeersPageViewModel(settings, backend, dialogs);
        Layout = new LayoutPageViewModel(settings);
        About = new AboutPageViewModel(dialogs, _appState, updates, diagnostics ?? (() => DefaultDiagnostics(null)),
            System.Version.TryParse(version, out var v) ? v : UpdateChecker.CurrentVersion);

        Pages = new[]
        {
            new NavigationItem("General", Symbol.Settings, General),
            new NavigationItem("Network", Symbol.Globe, Network),
            new NavigationItem("Peers", Symbol.People, Peers),
            new NavigationItem("Layout", Symbol.Board, Layout),
            new NavigationItem("About", Symbol.Info, About)
        };
        _selectedPage = Pages[0];
        General.IsActive = true;

        Peers.PeersChanged += (_, _) => Layout.Reload();
        // A machine just allowed has been put on a free edge; show where so it can be moved.
        Peers.PeerAllowed += (_, _) => ShowPage(SettingsPage.Layout);
        Refresh();
        _ = LoadStartupStateAsync();
    }

    /// <summary>The real diagnostics sources: this user's data folder, the service log folder and a system summary.</summary>
    public static DiagnosticsSources DefaultDiagnostics(Core.Screen.MonitorLayout? layout)
    {
        if (layout == null)
        {
            try { layout = Core.Screen.ScreenInfo.ReadLayout(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException) { }
        }
        var packaged = OperatingSystem.IsWindows() && StartupRegistration.IsPackaged;
        return new DiagnosticsSources(Diagnostics.DataFolder, Diagnostics.ServiceLogFolder,
            Diagnostics.DescribeSystem(UpdateChecker.CurrentVersion, packaged, layout));
    }

    /// <summary>Switches to a page.</summary>
    public void ShowPage(SettingsPage page)
    {
        PageViewModel target = page switch
        {
            SettingsPage.Network => Network,
            SettingsPage.Peers => Peers,
            SettingsPage.Layout => Layout,
            SettingsPage.About => About,
            _ => General
        };
        SelectedPage = Pages.First(p => p.Page == target);
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
        Network.Refresh();
    }

    partial void OnSelectedPageChanged(NavigationItem? value)
    {
        foreach (var page in Pages)
            page.Page.IsActive = page == value;
        if (value?.Page == Layout)
            Layout.Reload();
    }

    /// <summary>Every global hotkey as it would be saved, named for a conflict message.</summary>
    private IEnumerable<(string Name, string? Hotkey)> HotkeysInUse(string toggleHotkey)
    {
        yield return ("The toggle hotkey", toggleHotkey);
        yield return ("Lock the cursor", General.LockCursorHotkey);
        yield return ("Lock all PCs", General.LockAllHotkey);
        var peers = _settings.Peers.ToList();
        for (var i = 0; i < peers.Count; i++)
            yield return ($"Jump to {peers[i].Name}", HotkeySet.EffectiveJumpHotkey(peers[i], i));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // Without a hotkey there is no way back from a remote screen that stops answering.
        var hotkey = General.ToggleHotkey.Trim();
        if (Hotkey.Parse(hotkey) == null)
        {
            ShowPage(SettingsPage.General);
            await _dialogs.WarnAsync(hotkey.Length == 0
                ? "Choose a toggle hotkey. It is how you get the mouse back if another screen stops responding."
                : "The hotkey must be a key with at least one modifier, for example Ctrl+Alt+M. Only Scroll Lock, Pause and F13-F24 work alone.");
            return;
        }

        // Two actions on one chord: only the first would ever run.
        var conflict = HotkeySet.FindConflict(HotkeysInUse(hotkey));
        if (conflict != null)
        {
            ShowPage(SettingsPage.General);
            await _dialogs.WarnAsync(conflict);
            return;
        }

        if (General.HasErrors)
        {
            ShowPage(SettingsPage.General);
            await _dialogs.WarnAsync("Some fields on the General page are empty or out of range. Fix the highlighted fields, then save again.");
            return;
        }

        if (Network.HasErrors)
        {
            ShowPage(SettingsPage.Network);
            await _dialogs.WarnAsync("Some fields on the Network page are empty or out of range. Fix the highlighted fields, then save again.");
            return;
        }

        var pairingChanged = Network.PairingCodeChanged;
        _settings.PairingCode = Network.PairingCode;
        _settings.MachineName = string.IsNullOrWhiteSpace(General.MachineName) ? _settings.MachineName : General.MachineName.Trim();
        var startupChanged = _settings.StartWithWindows != General.StartWithWindows;
        _settings.StartWithWindows = General.StartWithWindows;
        _settings.StartMinimized = General.StartMinimized;
        _settings.ToggleHotkey = hotkey;
        _settings.LockCursorHotkey = General.LockCursorHotkey.Trim();
        _settings.LockAllHotkey = General.LockAllHotkey.Trim();
        General.SaveCrossing(_settings.Crossing);
        General.SaveClipboard(_settings.Clipboard);
        _settings.EdgeHighlight = General.SelectedHighlight.Style;
        _settings.WrapAround = General.WrapAround;
        _settings.WakeOnEdge = General.WakeOnEdge;
        _settings.FollowHostPower = General.FollowHostPower;
        _settings.LockWithHost = General.LockWithHost;
        _settings.ScreensaverWithHost = General.ScreensaverWithHost;
        _settings.DebugPanelEnabled = General.ShowDebugPanel;
        var desktopServiceChanged = _settings.UseDesktopService != General.UseDesktopService;
        _settings.UseDesktopService = General.UseDesktopService;
        _settings.LocalPort = (int)Network.LocalPort!.Value;
        _settings.DiscoveryPort = (int)Network.DiscoveryPort!.Value;
        Layout.Save();

        _backend.SaveSettings();
        About.Save();
        _appState.Save();

        // Everything takes effect now.
        _backend.ApplyClipboardSettings();
        _backend.ApplyHotkeySetting();
        _backend.ApplyCrossingSettings();
        _backend.ApplyPowerSetting();
        _backend.ApplyNetworkSettings();
        if (pairingChanged)
            _backend.ApplyPairingCode();

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
            _backend.SaveSettings();
            General.UseDesktopService = false;
            await _dialogs.WarnAsync("The RoboMouse desktop service could not be started, so UAC prompts and the lock screen stay out of reach. Approve the Windows prompt when turning this on.");
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Colour family for a status indicator; the view maps it to a theme brush.</summary>
public enum StatusTone { Idle, Ok, Warning, Error, Accent }
