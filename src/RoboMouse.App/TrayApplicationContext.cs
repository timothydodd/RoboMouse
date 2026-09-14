using RoboMouse.App.Forms;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network;

namespace RoboMouse.App;

/// <summary>
/// Application context for the system tray application. The tray icon's colour reflects state;
/// everything else lives in the Settings window.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private enum TrayState { Disabled, Disconnected, Connected, Controlling, Controlled }

    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Dictionary<TrayState, Icon> _icons = new();

    // Service callbacks arrive on network threads. A ContextMenuStrip has no window handle until it is first
    // shown, so its InvokeRequired lies until then; this control has its handle forced at startup.
    private readonly Control _uiMarshal;

    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _enableItem = null!;

    private SettingsForm? _settingsForm;
    private DebugPanelForm? _debugPanel;
    private BorderOverlayForm? _borderOverlay;
    private bool _wasControllingRemote;

    public TrayApplicationContext(AppSettings settings)
    {
        _settings = settings;
        _uiMarshal = new Control();
        _uiMarshal.CreateControl();
        _ = _uiMarshal.Handle;

        _service = new RoboMouseService(settings);

        _service.PeerConnected += (s, e) => OnUi(UpdateStatus);
        _service.PeerDisconnected += (s, e) => OnUi(UpdateStatus);
        _service.ControlStateChanged += (s, e) => OnUi(OnControlStateChanged);
        _service.Error += OnServiceError;
        _service.MouseDebugUpdate += OnMouseDebugUpdate;

        foreach (var state in Enum.GetValues<TrayState>())
            _icons[state] = CreateIcon(state);

        _contextMenu = CreateContextMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = _icons[TrayState.Disconnected],
            Text = "RoboMouse",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _trayIcon.DoubleClick += (s, e) => ShowSettings();

        _service.Start();
        UpdateStatus();

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

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        menu.Items.Add(_statusItem);

        menu.Items.Add(new ToolStripSeparator());

        _enableItem = new ToolStripMenuItem("Enabled")
        {
            Checked = _settings.Enabled,
            CheckOnClick = true
        };
        _enableItem.CheckedChanged += OnEnableToggled;
        menu.Items.Add(_enableItem);

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (s, e) => ShowSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExit;
        menu.Items.Add(exitItem);

        menu.Opening += (s, e) => UpdateStatus();

        return menu;
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
            status += $" (controlling {_service.ActivePeer?.Name})";
        }
        else if (_service.IsControlledByRemote)
        {
            state = TrayState.Controlled;
            status += " (being controlled)";
        }
        else
            state = connectedPeers.Count > 0 ? TrayState.Connected : TrayState.Disconnected;

        _statusItem.Text = status;
        _trayIcon.Icon = _icons[state];
        _trayIcon.Text = $"RoboMouse - {status}".Length > 63 ? $"RoboMouse - {status}"[..63] : $"RoboMouse - {status}";
    }

    private void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, _service);
        }

        _settingsForm.Show();
        _settingsForm.BringToFront();
        _settingsForm.Activate();
    }

    private void OnEnableToggled(object? sender, EventArgs e)
    {
        _settings.Enabled = _enableItem.Checked;
        _service.Enabled = _enableItem.Checked;
        _settings.Save();
        UpdateStatus();
    }

    private void OnControlStateChanged()
    {
        UpdateStatus();

        // Flash the border when the mouse arrives on this screen: either a remote took control of it,
        // or we just came back from controlling a remote.
        var isLocalAgain = _wasControllingRemote && !_service.IsControllingRemote && !_service.IsControlledByRemote;
        if (_settings.ShowBorderHighlight && (_service.IsControlledByRemote || isLocalAgain))
        {
            if (_borderOverlay == null || _borderOverlay.IsDisposed)
            {
                _borderOverlay = new BorderOverlayForm();
            }
            _borderOverlay.ShowBorder();
            _borderOverlay.HideBorder(fadeOut: true);
        }

        _wasControllingRemote = _service.IsControllingRemote;
    }

    private void OnServiceError(object? sender, Exception e)
    {
        SimpleLogger.Log("Error", e.ToString());
    }

    private void OnMouseDebugUpdate(object? sender, MouseDebugEventArgs e)
    {
        if (!_settings.DebugPanelEnabled)
        {
            if (_debugPanel is { IsDisposed: false, Visible: true })
                _debugPanel.Hide();
            return;
        }

        if (_debugPanel == null || _debugPanel.IsDisposed)
        {
            _debugPanel = new DebugPanelForm();
        }

        if (e.IsControlling && !_debugPanel.Visible)
        {
            _debugPanel.ShowOnEdge(_service.ActivePeer?.Position.ToString());
        }
        else if (!e.IsControlling && _debugPanel.Visible)
        {
            _debugPanel.Hide();
        }

        _debugPanel.UpdateData(new MouseDebugData
        {
            IsControlling = e.IsControlling,
            PeerName = e.PeerName,
            PeerPosition = e.PeerPosition,
            DeltaX = e.DeltaX,
            DeltaY = e.DeltaY,
            RoundTripMs = e.RoundTripMs
        });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _service.Dispose();
        Application.Exit();
    }

    /// <summary>Runs an action on the UI thread, now if already there, otherwise queued.</summary>
    private void OnUi(Action action)
    {
        if (_uiMarshal.IsDisposed)
            return;

        if (_uiMarshal.InvokeRequired)
            _uiMarshal.BeginInvoke(action);
        else
            action();
    }

    /// <summary>
    /// Draws the cursor-arrow tray icon in a colour for the given state:
    /// grey = disabled, hollow = no peers, blue = connected, green = controlling a remote, orange = being controlled.
    /// </summary>
    private static Icon CreateIcon(TrayState state)
    {
        var (fill, outline, hollow) = state switch
        {
            TrayState.Disabled => (Color.FromArgb(130, 130, 130), Color.FromArgb(90, 90, 90), false),
            TrayState.Disconnected => (Color.FromArgb(64, 158, 255), Color.FromArgb(64, 158, 255), true),
            TrayState.Connected => (Color.FromArgb(64, 158, 255), Color.FromArgb(40, 120, 200), false),
            TrayState.Controlling => (Color.FromArgb(70, 200, 110), Color.FromArgb(40, 150, 80), false),
            TrayState.Controlled => (Color.FromArgb(255, 165, 50), Color.FromArgb(200, 120, 30), false),
            _ => (Color.Gray, Color.DimGray, false)
        };

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var points = new Point[]
            {
                new(6, 3),
                new(6, 25),
                new(11, 20),
                new(16, 29),
                new(20, 27),
                new(15, 18),
                new(23, 18),
            };

            using var brush = new SolidBrush(hollow ? Color.FromArgb(40, 40, 40) : fill);
            using var pen = new Pen(outline, 2.5f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };

            g.FillPolygon(brush, points);
            g.DrawPolygon(pen, points);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _service.Dispose();
            _uiMarshal.Dispose();
            _settingsForm?.Dispose();
            _debugPanel?.Dispose();
            _borderOverlay?.Dispose();
            foreach (var icon in _icons.Values)
                icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
