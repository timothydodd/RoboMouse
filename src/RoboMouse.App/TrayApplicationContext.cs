using RoboMouse.App.Forms;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App;

/// <summary>
/// Application context for the system tray application.
/// </summary>
public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;
    private readonly ContextMenuStrip _contextMenu;

    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _enableItem = null!;
    private ToolStripMenuItem _discoveredPeersItem = null!;

    private SettingsForm? _settingsForm;
    private ScreenLayoutForm? _layoutForm;

    public TrayApplicationContext(AppSettings settings)
    {
        _settings = settings;
        _service = new RoboMouseService(settings);

        _service.PeerConnected += OnPeerConnected;
        _service.PeerDisconnected += OnPeerDisconnected;
        _service.PeerDiscovered += OnPeerDiscovered;
        _service.ControlStateChanged += OnControlStateChanged;
        _service.Error += OnServiceError;

        _contextMenu = CreateContextMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = CreateDefaultIcon(),
            Text = "RoboMouse",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _trayIcon.DoubleClick += (s, e) => ShowSettings();

        // Start the service
        _service.Start();
        UpdateStatus();

        // Auto-connect to configured peers
        _ = AutoConnectAsync();
    }

    private async Task AutoConnectAsync()
    {
        // Small delay to let the UI initialize
        await Task.Delay(1000);

        try
        {
            await _service.ConnectToConfiguredPeersAsync();
            if (InvokeRequired(() => UpdateStatus()))
                return;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Auto-connect failed: {ex.Message}");
        }
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        // Status item (disabled, just for display)
        _statusItem = new ToolStripMenuItem("Status: Disconnected")
        {
            Enabled = false
        };
        menu.Items.Add(_statusItem);

        menu.Items.Add(new ToolStripSeparator());

        // Discovered peers submenu
        _discoveredPeersItem = new ToolStripMenuItem("Connect to...");
        menu.Items.Add(_discoveredPeersItem);

        // Add peer by IP
        var addPeerItem = new ToolStripMenuItem("Add Peer by IP...");
        addPeerItem.Click += OnAddPeerByIp;
        menu.Items.Add(addPeerItem);

        // Screen layout
        var layoutItem = new ToolStripMenuItem("Screen Layout...");
        layoutItem.Click += (s, e) => ShowScreenLayout();
        menu.Items.Add(layoutItem);

        // Settings
        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (s, e) => ShowSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        // Enable/Disable toggle
        _enableItem = new ToolStripMenuItem("Enabled")
        {
            Checked = _settings.Enabled,
            CheckOnClick = true
        };
        _enableItem.CheckedChanged += OnEnableToggled;
        menu.Items.Add(_enableItem);

        menu.Items.Add(new ToolStripSeparator());

        // Exit
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExit;
        menu.Items.Add(exitItem);

        // Update discovered peers when menu opens
        menu.Opening += (s, e) => UpdateDiscoveredPeersMenu();

        return menu;
    }

    private void UpdateDiscoveredPeersMenu()
    {
        _discoveredPeersItem.DropDownItems.Clear();

        var peers = _service.DiscoveredPeers;

        if (peers.Count == 0)
        {
            var noneItem = new ToolStripMenuItem("No peers found")
            {
                Enabled = false
            };
            _discoveredPeersItem.DropDownItems.Add(noneItem);
            return;
        }

        foreach (var peer in peers)
        {
            var peerItem = new ToolStripMenuItem($"{peer.MachineName} ({peer.Address})")
            {
                Tag = peer
            };

            // Position submenu
            var leftItem = new ToolStripMenuItem("Position on Left");
            leftItem.Click += (s, e) => ConnectToPeer(peer, ScreenPosition.Left);

            var rightItem = new ToolStripMenuItem("Position on Right");
            rightItem.Click += (s, e) => ConnectToPeer(peer, ScreenPosition.Right);

            var topItem = new ToolStripMenuItem("Position Above");
            topItem.Click += (s, e) => ConnectToPeer(peer, ScreenPosition.Top);

            var bottomItem = new ToolStripMenuItem("Position Below");
            bottomItem.Click += (s, e) => ConnectToPeer(peer, ScreenPosition.Bottom);

            peerItem.DropDownItems.AddRange(new ToolStripItem[] { leftItem, rightItem, topItem, bottomItem });
            _discoveredPeersItem.DropDownItems.Add(peerItem);
        }
    }

    private async void ConnectToPeer(DiscoveredPeer peer, ScreenPosition position)
    {
        try
        {
            await _service.ConnectToPeerAsync(peer, position);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to connect: {ex.Message}", "Connection Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void OnAddPeerByIp(object? sender, EventArgs e)
    {
        using var form = new PeerSetupForm(null);
        if (form.ShowDialog() == DialogResult.OK && form.PeerConfig != null)
        {
            try
            {
                await _service.ConnectToAddressAsync(
                    form.PeerConfig.Address,
                    form.PeerConfig.Port,
                    form.PeerConfig.Position);

                _settings.Save();
                UpdateStatus();
                ShowBalloon($"Connected to {form.PeerConfig.Address}", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to {form.PeerConfig.Address}:\n{ex.Message}",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void UpdateStatus()
    {
        var connectedPeers = _service.ConnectedPeers;

        if (connectedPeers.Count == 0)
        {
            _statusItem.Text = "Status: Disconnected";
            _trayIcon.Text = "RoboMouse - Disconnected";
        }
        else if (connectedPeers.Count == 1)
        {
            var peer = connectedPeers.First();
            _statusItem.Text = $"Status: Connected to {peer.PeerName}";
            _trayIcon.Text = $"RoboMouse - {peer.PeerName}";
        }
        else
        {
            _statusItem.Text = $"Status: Connected ({connectedPeers.Count} peers)";
            _trayIcon.Text = $"RoboMouse - {connectedPeers.Count} peers";
        }

        if (_service.IsControllingRemote)
        {
            _trayIcon.Text += " [Controlling]";
        }
        else if (_service.IsControlledByRemote)
        {
            _trayIcon.Text += " [Controlled]";
        }
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

    private void ShowScreenLayout()
    {
        if (_layoutForm == null || _layoutForm.IsDisposed)
        {
            _layoutForm = new ScreenLayoutForm(_settings, _service);
        }

        _layoutForm.Show();
        _layoutForm.BringToFront();
        _layoutForm.Activate();
    }

    private void OnEnableToggled(object? sender, EventArgs e)
    {
        _settings.Enabled = _enableItem.Checked;
        _service.Enabled = _enableItem.Checked;
    }

    private void OnPeerConnected(object? sender, PeerConnection connection)
    {
        if (InvokeRequired(() => OnPeerConnected(sender, connection)))
            return;

        UpdateStatus();
        ShowBalloon($"Connected to {connection.PeerName}", ToolTipIcon.Info);
    }

    private void OnPeerDisconnected(object? sender, string peerId)
    {
        if (InvokeRequired(() => OnPeerDisconnected(sender, peerId)))
            return;

        UpdateStatus();
        ShowBalloon("Peer disconnected", ToolTipIcon.Info);
    }

    private void OnPeerDiscovered(object? sender, DiscoveredPeer peer)
    {
        // Just update the menu when it opens
    }

    private void OnControlStateChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired(() => OnControlStateChanged(sender, e)))
            return;

        UpdateStatus();
    }

    private void OnServiceError(object? sender, Exception e)
    {
        if (InvokeRequired(() => OnServiceError(sender, e)))
            return;

        ShowBalloon($"Error: {e.Message}", ToolTipIcon.Error);
    }

    private void ShowBalloon(string message, ToolTipIcon icon)
    {
        _trayIcon.BalloonTipTitle = "RoboMouse";
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3000);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _service.Dispose();
        Application.Exit();
    }

    private bool InvokeRequired(Action action)
    {
        if (_contextMenu.InvokeRequired)
        {
            _contextMenu.Invoke(action);
            return true;
        }
        return false;
    }

    private static Icon CreateDefaultIcon()
    {
        // Create a simple icon programmatically
        // In production, you'd use an actual icon file
        var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);

            // Draw a mouse cursor shape
            using var brush = new SolidBrush(Color.FromArgb(64, 158, 255));
            using var pen = new Pen(Color.FromArgb(40, 120, 200), 2);

            // Arrow shape
            var points = new Point[]
            {
                new(4, 4),
                new(4, 24),
                new(10, 18),
                new(16, 28),
                new(20, 26),
                new(14, 16),
                new(22, 16),
            };

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
            _settingsForm?.Dispose();
            _layoutForm?.Dispose();
        }
        base.Dispose(disposing);
    }
}
