using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Platform;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.App.Views;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network;

namespace RoboMouse.App;

/// <summary>
/// Owns the tray icon and the service. The tray icon's colour reflects state; notifications (a machine
/// asking to connect, errors, updates) appear as toasts next to it; everything else lives in the
/// Settings window.
/// </summary>
public sealed class TrayController : IDisposable
{
    private enum TrayState { Disabled, Disconnected, Connected, Controlling, Controlled }

    private readonly AppSettings _settings;
    private readonly AppState _appState;
    private readonly RoboMouseService _service;
    private readonly ToastPresenter _toasts = new();
    private readonly NotificationThrottle _errorThrottle = new(TimeSpan.FromMinutes(1));
    private readonly Dictionary<NetworkErrorKind, NetworkStartError> _shownNetworkErrors = new();
    private readonly HashSet<string> _pendingToasts = new();
    private readonly UpdateChecker? _updates;
    private readonly DispatcherTimer? _updateTimer;
    private readonly ServiceBackend _backend;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenu _menu;
    private readonly Dictionary<TrayState, WindowIcon> _icons = new();

    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _enableItem;
    private readonly NativeMenuItem _peersItem;

    private SettingsWindow? _settingsWindow;
    private PairingWizardWindow? _wizard;
#if DEBUG
    private DebugPanelWindow? _debugPanel;
    private readonly DebugPanelViewModel _debugViewModel = new();
    private bool _debugPanelShown;
#endif
    private EdgeHighlight? _highlight;
    private bool _wasControllingRemote;
    private ScreenPosition _lastControlledEdge = ScreenPosition.Right;
    private bool _disposed;

    public TrayController(AppSettings settings, IClassicDesktopStyleApplicationLifetime lifetime, AppState appState)
    {
        _settings = settings;
        _appState = appState;
        _lifetime = lifetime;
        // The Store updates its own copy; only the direct-download build looks on GitHub.
        _updates = StartupRegistration.IsPackaged ? null : new UpdateChecker();

        _service = new RoboMouseService(settings)
        {
            ClipboardImageCodec = new AvaloniaImageCodec()
        };
        _backend = new ServiceBackend(_service, settings);

        foreach (var state in Enum.GetValues<TrayState>())
            _icons[state] = CreateIcon(state);

        _statusItem = new NativeMenuItem("Disconnected") { IsEnabled = false };
        _enableItem = new NativeMenuItem("Enabled") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.Enabled };
        _enableItem.Click += OnEnableToggled;
        _peersItem = new NativeMenuItem("Peers") { Menu = new NativeMenu() };
        var pairItem = new NativeMenuItem("Pair with another PC...");
        pairItem.Click += (s, e) => ShowPairingWizard();
        var settingsItem = new NativeMenuItem("Settings...");
        settingsItem.Click += (s, e) => ShowSettings();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += OnExit;

        _menu = new NativeMenu();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_enableItem);
        _menu.Items.Add(_peersItem);
        _menu.Items.Add(pairItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(exitItem);
        _menu.NeedsUpdate += (s, e) =>
        {
            UpdateStatus();
            UpdatePeersMenu();
        };

        _trayIcon = new TrayIcon
        {
            Icon = _icons[TrayState.Disconnected],
            ToolTipText = "RoboMouse",
            Menu = _menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (s, e) => ShowSettings();
        TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { _trayIcon });

        _service.PeerConnected += (s, e) => OnUi(UpdateStatus);
        _service.PeerDisconnected += (s, e) => OnUi(UpdateStatus);
        _service.ControlStateChanged += (s, e) => OnUi(OnControlStateChanged);
        _service.EnabledChanged += (s, e) => OnUi(() =>
        {
            _enableItem.IsChecked = _service.Enabled;
            _settings.Save();
            UpdateStatus();
        });
        _service.Error += (s, e) => OnUi(() => OnServiceError(e));
        _service.PendingPeerRequested += (s, e) => OnUi(() => OnPendingPeerRequested(e));
        _service.PendingPeersChanged += (s, e) => OnUi(OnPendingPeersChanged);
        _service.PeerWakeSent += (s, e) => OnUi(() => _toasts.Show(Notifications.WakeSent(e)));
        _service.NetworkStatusChanged += (s, e) => OnUi(ShowNetworkErrors);
        _service.PeersChanged += (s, e) => OnUi(UpdateStatus);
#if DEBUG
        _service.MouseDebugUpdate += OnMouseDebugUpdate;
#endif

        _service.Start();
        UpdateStatus();
        ShowNetworkErrors();
        ShowSettingsLoadNotice();

        if (_updates != null)
        {
            // First look a minute after start (not competing with connecting), then hourly to see if a day has passed.
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _updateTimer.Tick += (s, e) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(1);
                _ = CheckForUpdatesAsync();
            };
            _updateTimer.Start();
        }

        // A second launch of the app asks us to bring up Settings instead of running itself.
        if (Program.Instance is { } instance)
        {
            instance.ShowRequested += () => OnUi(ShowSettings);
            instance.Listen();
        }

        _ = AutoConnectAsync();
    }

    private async Task AutoConnectAsync()
    {
        await Task.Delay(1000);

        try
        {
            await _service.ConnectToConfiguredPeersAsync();
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Connect", $"Auto-connect failed: {ex.Message}");
        }

        OnUi(UpdateStatus);
    }

    /// <summary>
    /// Configured peers (ticked when enabled; click to switch one off or on), then any machine
    /// discovered on the network that is not configured yet (pick an edge to add and connect).
    /// </summary>
    private void UpdatePeersMenu()
    {
        var items = _peersItem.Menu!.Items;
        items.Clear();

        foreach (var peer in _settings.Peers)
        {
            var connection = _service.GetConnection(peer.Id);
            var detail = !peer.Enabled
                ? "disabled"
                : connection == null
                    ? "not connected"
                    : connection.RoundTripMs >= 0 ? $"{connection.RoundTripMs} ms" : "connected";

            var item = new NativeMenuItem($"{peer.Name}  ({PeerPositions.Describe(peer.Position)}, {detail})")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = peer.Enabled,
                ToolTip = peer.Enabled ? "Click to disable this peer" : "Click to enable this peer"
            };
            var captured = peer;
            item.Click += async (s, e) =>
            {
                try { await _service.SetPeerEnabledAsync(captured, !captured.Enabled); }
                catch (Exception ex) { SimpleLogger.Log("Peers", $"Toggle {captured.Name}: {ex.Message}"); }
                OnUi(UpdateStatus);
            };
            items.Add(item);

            if (peer.Enabled && connection == null && RoboMouseService.CanWake(peer))
            {
                var wake = new NativeMenuItem($"Wake {peer.Name}")
                {
                    ToolTip = "Send a Wake-on-LAN packet; it reconnects by itself once it is up"
                };
                wake.Click += (s, e) => Task.Run(() => _service.WakePeer(captured));
                items.Add(wake);
            }
        }

        var configuredIds = _settings.Peers.Select(p => p.Id).ToHashSet();
        var configuredAddresses = _settings.Peers.Select(p => p.Address).ToHashSet();
        var discovered = _service.DiscoveredPeers
            .Where(p => p.MachineId != _settings.MachineId
                        && !configuredIds.Contains(p.MachineId)
                        && !configuredAddresses.Contains(p.Address.ToString()))
            .OrderBy(p => p.MachineName)
            .ToList();

        if (_settings.Peers.Count > 0 && discovered.Count > 0)
            items.Add(new NativeMenuItemSeparator());

        foreach (var found in discovered)
        {
            var submenu = new NativeMenu();
            foreach (var position in PeerPositions.All)
            {
                var captured = position;
                var taken = _settings.Peers.FirstOrDefault(p => p.Position == position);
                var positionItem = new NativeMenuItem(
                    taken == null
                        ? $"Add {PeerPositions.Describe(position).ToLower()} of this screen"
                        : $"{PeerPositions.Describe(position)} of this screen (used by {taken.Name})")
                {
                    IsEnabled = taken == null
                };
                positionItem.Click += (s, e) => _ = AddDiscoveredPeerAsync(found, captured);
                submenu.Items.Add(positionItem);
            }
            items.Add(new NativeMenuItem($"{found.MachineName}  ({found.Address}, new)") { Menu = submenu });
        }

        if (items.Count == 0)
        {
            items.Add(new NativeMenuItem("No peers configured or found") { IsEnabled = false });
        }
    }

    /// <summary>
    /// Same flow as the Peers page: the config is added and saved first, so a peer that is slow to
    /// answer is not lost, then connected.
    /// </summary>
    private async Task AddDiscoveredPeerAsync(DiscoveredPeer found, ScreenPosition position)
    {
        var config = _settings.Peers.FirstOrDefault(p => p.Id == found.MachineId) ?? PeerActions.FromDiscovered(found, position);
        var error = await PeerActions.AddAndConnectAsync(_settings, _backend, config);
        if (error != null)
            await Dialogs().WarnAsync($"Added {config.Name}, but could not connect yet: {error}\n\nRoboMouse keeps trying in the background.");
        UpdateStatus();
    }

    #region Notifications

    /// <summary>An unknown machine with the code asks to connect: Allow adds it and opens the Layout page.</summary>
    private void OnPendingPeerRequested(PendingPeer peer)
    {
        _pendingToasts.Add(peer.MachineId);
        _toasts.Show(Notifications.PendingPeer(peer,
            allow: () =>
            {
                if (_service.AllowPendingPeer(peer.MachineId) != null)
                    ShowSettings(SettingsPage.Layout);
            },
            ignore: () => _service.IgnorePendingPeer(peer.MachineId)));
    }

    /// <summary>A request answered elsewhere (the Peers page, the wizard) takes its toast down too.</summary>
    private void OnPendingPeersChanged()
    {
        var pending = _service.PendingPeers.Select(p => p.MachineId).ToHashSet();
        foreach (var id in _pendingToasts.Where(id => !pending.Contains(id)).ToList())
        {
            _pendingToasts.Remove(id);
            _toasts.Dismiss(Notifications.PendingKey(id));
        }
    }

    private void OnServiceError(Exception e)
    {
        SimpleLogger.Log("Error", e.ToString());
        if (_errorThrottle.ShouldShow(e.GetType().FullName ?? "error", DateTime.UtcNow))
            _toasts.Show(Notifications.ServiceError(e, () => Dialogs().Open(Diagnostics.DataFolder)));
    }

    /// <summary>A port that could not be opened (once per distinct error); a fixed one takes its toast down.</summary>
    private void ShowNetworkErrors()
    {
        foreach (var (kind, error) in new[] { (NetworkErrorKind.ListenPort, _service.ListenerError), (NetworkErrorKind.DiscoveryPort, _service.DiscoveryError) })
        {
            if (error == null)
            {
                if (_shownNetworkErrors.Remove(kind))
                    _toasts.Dismiss("network:" + kind);
                continue;
            }
            if (_shownNetworkErrors.TryGetValue(kind, out var shown) && shown == error)
                continue;
            _shownNetworkErrors[kind] = error;
            _toasts.Show(Notifications.NetworkError(error, () => ShowSettings(SettingsPage.Network)));
        }
    }

    /// <summary>Once at startup: the settings file had to be restored or reset.</summary>
    private void ShowSettingsLoadNotice()
    {
        var notice = Notifications.SettingsLoad(_settings.LoadNotice, _settings.CorruptFilePath, () => Dialogs().Open(Diagnostics.DataFolder));
        if (notice != null)
            _toasts.Show(notice);
    }

    /// <summary>At most once a day, when turned on: a newer release gets one notification per version.</summary>
    private async Task CheckForUpdatesAsync()
    {
        if (_updates == null || !_appState.CheckForUpdates || !UpdateChecker.IsDue(_appState.LastUpdateCheckUtc, DateTime.UtcNow))
            return;
        var current = UpdateChecker.CurrentVersion;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await _updates.CheckAsync(current, cts.Token);
            if (update != null && _appState.LastNotifiedVersion != update.Version.ToString(3))
            {
                _appState.LastNotifiedVersion = update.Version.ToString(3);
                _toasts.Show(Notifications.UpdateAvailable(update, current, () => Dialogs().Open(update.PageUrl)));
            }
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Update", $"Check failed: {ex.Message}");
        }
        _appState.LastUpdateCheckUtc = DateTime.UtcNow;
        _appState.Save();
    }

    #endregion

    /// <summary>Dialogs from the tray: modal to the Settings window when it is open.</summary>
    private WindowDialogService Dialogs() => new(_settingsWindow, _backend);


    private void UpdateStatus()
    {
        var connectedPeers = _service.ConnectedPeers;

        string status;
        if (!_service.Enabled)
            status = "Disabled";
        else if (connectedPeers.Count == 0)
            status = "Not connected";
        else if (connectedPeers.Count == 1)
            status = $"Connected to {connectedPeers.First().PeerName}";
        else
            status = $"Connected to {connectedPeers.Count} peers";

        TrayState state;
        if (!_service.Enabled)
            state = TrayState.Disabled;
        else if (_service.IsControllingRemote)
        {
            state = TrayState.Controlling;
            status += $" ({StatusText.Controlling(_service.ActivePeer?.Name, _service.RemoteInputBlockReason)})";
        }
        else if (_service.IsControlledByRemote)
        {
            state = TrayState.Controlled;
            status += " (being controlled)";
        }
        else
            state = connectedPeers.Count > 0 ? TrayState.Connected : TrayState.Disconnected;

        _statusItem.Header = status;
        _trayIcon.Icon = _icons[state];
        var tip = $"RoboMouse - {status}";
        _trayIcon.ToolTipText = tip.Length > 63 ? tip[..63] : tip;
    }

    /// <summary>Opens Settings (or brings it to the front), optionally on a given page.</summary>
    public void ShowSettings() => ShowSettings(null);

    public void ShowSettings(SettingsPage? page)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, _backend, _appState, _updates);
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            if (page is { } first)
                _settingsWindow.ViewModel.ShowPage(first);
            _settingsWindow.Show();
        }
        else
        {
            if (page is { } target)
            {
                _settingsWindow.ViewModel.Refresh();
                _settingsWindow.ViewModel.ShowPage(target);
            }
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
    }

    /// <summary>The pairing wizard: shown at the first start (no peers yet) and from the tray menu.</summary>
    public void ShowPairingWizard()
    {
        if (_wizard != null)
        {
            _wizard.Activate();
            return;
        }
        var dialogs = new WindowDialogService(null, _backend);
        _wizard = new PairingWizardWindow(new PairingWizardViewModel(_settings, _backend, dialogs));
        dialogs.Owner = _wizard;
        _wizard.Closed += (s, e) =>
        {
            var added = _wizard?.ViewModel.Result;
            _wizard = null;
            UpdateStatus();
            // Show where the new peer landed; the edge can be adjusted there.
            if (added != null)
                ShowSettings(SettingsPage.Layout);
        };
        _wizard.Show();
    }

    private void OnEnableToggled(object? sender, EventArgs e)
    {
        var enabled = !_service.Enabled;
        _enableItem.IsChecked = enabled;
        _settings.Enabled = enabled;
        _service.Enabled = enabled;
        _settings.Save();
        UpdateStatus();
    }

    private void OnControlStateChanged()
    {
        UpdateStatus();

        // Mark where the mouse arrived on this screen: either a remote took control of it (it came in
        // on the entry edge), or we just came back from controlling a remote (it came in on that peer's edge).
        if (_service.IsControllingRemote && _service.ActivePeer != null)
            _lastControlledEdge = _service.ActivePeer.Position;

        var isLocalAgain = _wasControllingRemote && !_service.IsControllingRemote && !_service.IsControlledByRemote;
        if (_settings.EdgeHighlight != EdgeHighlightStyle.None && (_service.IsControlledByRemote || isLocalAgain))
        {
            _highlight ??= new EdgeHighlight();
            _highlight.Flash(_settings.EdgeHighlight, _service.IsControlledByRemote ? _service.EntryEdge : _lastControlledEdge);
        }

        _wasControllingRemote = _service.IsControllingRemote;
    }

#if DEBUG
    private void OnMouseDebugUpdate(object? sender, MouseDebugEventArgs e)
    {
        // Samples arrive on the input thread at up to 1000 Hz; the view model batches them and the
        // panel repaints on a timer.
        var data = new MouseDebugData
        {
            IsControlling = e.IsControlling,
            PeerName = e.PeerName,
            PeerPosition = e.PeerPosition,
            DeltaX = e.DeltaX,
            DeltaY = e.DeltaY,
            RoundTripMs = e.RoundTripMs
        };
        _debugViewModel.Record(data);

        var show = _settings.DebugPanelEnabled && data.IsControlling;
        if (show == _debugPanelShown)
            return;
        _debugPanelShown = show;

        OnUi(() =>
        {
            if (show)
            {
                _debugPanel ??= new DebugPanelWindow(_debugViewModel);
                _debugPanel.ShowOnEdge(_service.ActivePeer?.Position.ToString());
            }
            else if (_debugPanel is { IsVisible: true })
            {
                _debugPanel.Hide();
            }
        });
    }
#endif

    private void OnExit(object? sender, EventArgs e)
    {
        _updateTimer?.Stop();
        _toasts.CloseAll();
        _trayIcon.IsVisible = false;
        _service.Dispose();
        _lifetime.Shutdown();
    }

    /// <summary>Runs an action on the UI thread, now if already there, otherwise queued.</summary>
    private void OnUi(Action action)
    {
        if (_disposed)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Loads the embedded tray icon for a state. Windows picks the best size from the multi-size .ico
    /// for the current DPI.
    /// </summary>
    private static WindowIcon CreateIcon(TrayState state)
    {
        var name = state switch
        {
            TrayState.Disabled => "disabled",
            TrayState.Disconnected => "offline",
            TrayState.Connected => "online",
            TrayState.Controlling => "controlling",
            TrayState.Controlled => "controlled",
            _ => "offline"
        };

        using var stream = AssetLoader.Open(new Uri($"avares://RoboMouse.App/Assets/{name}.ico"));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _updateTimer?.Stop();
        _toasts.CloseAll();
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _service.Dispose();
        _settingsWindow?.Close();
        _wizard?.Close();
#if DEBUG
        _debugPanel?.Close();
#endif
        _highlight?.Close();
    }
}
