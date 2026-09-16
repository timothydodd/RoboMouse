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
/// Owns the tray icon and the service. The tray icon's colour reflects state; everything else lives
/// in the Settings window.
/// </summary>
public sealed class TrayController : IDisposable
{
    private enum TrayState { Disabled, Disconnected, Connected, Controlling, Controlled }

    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;
    private readonly ServiceBackend _backend;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenu _menu;
    private readonly Dictionary<TrayState, WindowIcon> _icons = new();

    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _enableItem;
    private readonly NativeMenuItem _peersItem;

    private SettingsWindow? _settingsWindow;
#if DEBUG
    private DebugPanelWindow? _debugPanel;
    private readonly DebugPanelViewModel _debugViewModel = new();
    private bool _debugPanelShown;
#endif
    private EdgeHighlightWindow? _highlight;
    private bool _wasControllingRemote;
    private ScreenPosition _lastControlledEdge = ScreenPosition.Right;
    private bool _disposed;

    public TrayController(AppSettings settings, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _settings = settings;
        _lifetime = lifetime;

        _service = new RoboMouseService(settings)
        {
            ClipboardImageCodec = new AvaloniaImageCodec()
        };
        _backend = new ServiceBackend(_service);

        foreach (var state in Enum.GetValues<TrayState>())
            _icons[state] = CreateIcon(state);

        _statusItem = new NativeMenuItem("Disconnected") { IsEnabled = false };
        _enableItem = new NativeMenuItem("Enabled") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.Enabled };
        _enableItem.Click += OnEnableToggled;
        _peersItem = new NativeMenuItem("Peers") { Menu = new NativeMenu() };
        var settingsItem = new NativeMenuItem("Settings...");
        settingsItem.Click += (s, e) => ShowSettings();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += OnExit;

        _menu = new NativeMenu();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(_enableItem);
        _menu.Items.Add(_peersItem);
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
        _service.Error += OnServiceError;
#if DEBUG
        _service.MouseDebugUpdate += OnMouseDebugUpdate;
#endif

        _service.Start();
        UpdateStatus();

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

    private async Task AddDiscoveredPeerAsync(DiscoveredPeer found, ScreenPosition position)
    {
        try
        {
            await _service.ConnectToPeerAsync(found, position);

            var config = _settings.Peers.FirstOrDefault(p => p.Id == found.MachineId);
            if (config == null)
            {
                _settings.Peers.Add(new PeerConfig
                {
                    Id = found.MachineId,
                    Name = found.MachineName,
                    Address = found.Address.ToString(),
                    Port = found.Port,
                    Position = position,
                    ScreenWidth = found.ScreenWidth,
                    ScreenHeight = found.ScreenHeight
                });
            }
            _settings.Save();
        }
        catch (Exception ex)
        {
            await new WindowDialogService(null, _backend).ErrorAsync($"Could not connect to {found.MachineName}: {ex.Message}");
        }
        UpdateStatus();
    }

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

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, _backend);
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
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
            _highlight ??= new EdgeHighlightWindow();
            _highlight.Flash(_settings.EdgeHighlight, _service.IsControlledByRemote ? _service.EntryEdge : _lastControlledEdge);
        }

        _wasControllingRemote = _service.IsControllingRemote;
    }

    private void OnServiceError(object? sender, Exception e)
    {
        SimpleLogger.Log("Error", e.ToString());
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

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _service.Dispose();
        _settingsWindow?.Close();
#if DEBUG
        _debugPanel?.Close();
#endif
        _highlight?.Close();
    }
}
