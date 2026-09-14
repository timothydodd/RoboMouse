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
    private ToolStripMenuItem _configuredPeersItem = null!;

    private SettingsForm? _settingsForm;
    private ScreenLayoutForm? _layoutForm;
    private DebugPanelForm? _debugPanel;
    private BorderOverlayForm? _borderOverlay;

    public TrayApplicationContext(AppSettings settings)
    {
        _settings = settings;
        _service = new RoboMouseService(settings);

        _service.PeerConnected += OnPeerConnected;
        _service.PeerDisconnected += OnPeerDisconnected;
        _service.PeerDiscovered += OnPeerDiscovered;
        _service.ControlStateChanged += OnControlStateChanged;
        _service.Error += OnServiceError;
        _service.MouseDebugUpdate += OnMouseDebugUpdate;

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

        // Configured peers submenu (status, position, test, connect)
        _configuredPeersItem = new ToolStripMenuItem("Peers");
        menu.Items.Add(_configuredPeersItem);

        // Discovered peers submenu
        _discoveredPeersItem = new ToolStripMenuItem("Connect to discovered...");
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

        // Debug panel toggle
        var debugItem = new ToolStripMenuItem("Debug Panel")
        {
            CheckOnClick = true,
            Checked = _settings.DebugPanelEnabled
        };
        debugItem.CheckedChanged += OnDebugPanelToggled;
        menu.Items.Add(debugItem);

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

        // Update peer submenus when menu opens
        menu.Opening += (s, e) =>
        {
            UpdateConfiguredPeersMenu();
            UpdateDiscoveredPeersMenu();
        };

        return menu;
    }

    private void UpdateConfiguredPeersMenu()
    {
        _configuredPeersItem.DropDownItems.Clear();

        if (_settings.Peers.Count == 0)
        {
            _configuredPeersItem.DropDownItems.Add(new ToolStripMenuItem("No peers configured") { Enabled = false });
            return;
        }

        foreach (var peer in _settings.Peers)
        {
            var connection = _service.GetConnection(peer.Id);
            var status = connection == null
                ? "not connected"
                : connection.RoundTripMs >= 0 ? $"{connection.RoundTripMs} ms" : "connected";

            var peerItem = new ToolStripMenuItem($"{peer.Name}  ({PeerPositions.Describe(peer.Position)}, {status})");

            peerItem.DropDownItems.Add(new ToolStripMenuItem($"{peer.Address}:{peer.Port}") { Enabled = false });
            peerItem.DropDownItems.Add(new ToolStripSeparator());

            foreach (var position in PeerPositions.All)
            {
                var captured = position;
                var positionItem = new ToolStripMenuItem($"{PeerPositions.Describe(position)} of this screen")
                {
                    Checked = peer.Position == position
                };
                positionItem.Click += (s, e) => PeerPositions.TrySet(_settings, peer, captured, null);
                peerItem.DropDownItems.Add(positionItem);
            }

            peerItem.DropDownItems.Add(new ToolStripSeparator());

            var testItem = new ToolStripMenuItem("Test Connection");
            testItem.Click += (s, e) => TestPeer(peer);
            peerItem.DropDownItems.Add(testItem);

            var connectItem = new ToolStripMenuItem(connection == null ? "Connect" : "Disconnect");
            connectItem.Click += (s, e) => ToggleConnection(peer);
            peerItem.DropDownItems.Add(connectItem);

            _configuredPeersItem.DropDownItems.Add(peerItem);
        }
    }

    private async void TestPeer(PeerConfig peer)
    {
        ShowBalloon($"Testing connection to {peer.Name}...", ToolTipIcon.Info);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _service.TestConnectionAsync(peer, cts.Token);
            MessageBox.Show(result.Summary, $"Connection test: {peer.Name}",
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Test failed: {ex.Message}", "RoboMouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void ToggleConnection(PeerConfig peer)
    {
        try
        {
            if (_service.IsPeerConnected(peer.Id))
            {
                await _service.DisconnectFromPeerAsync(peer.Id);
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _service.ConnectToPeerAsync(peer, cts.Token);
                _settings.Save();
            }
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show($"Connecting to {peer.Address}:{peer.Port} timed out. Use Test Connection for details.",
                "RoboMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not connect to {peer.Name}: {ex.Message}", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        using var form = new PeerSetupForm(null, _service, _settings);
        if (form.ShowDialog() == DialogResult.OK && form.PeerConfig != null)
        {
            var address = form.PeerConfig.Address;
            var port = form.PeerConfig.Port;

            ShowBalloon($"Connecting to {address}:{port}...", ToolTipIcon.Info);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                var peer = await _service.ConnectToAddressAsync(
                    address,
                    port,
                    form.PeerConfig.Position,
                    cts.Token);

                _settings.Save();
                UpdateStatus();
                ShowBalloon($"Connected to {peer.Name} ({address})", ToolTipIcon.Info);
            }
            catch (OperationCanceledException)
            {
                MessageBox.Show($"Connection to {address}:{port} timed out after 10 seconds.\n\nMake sure:\n- RoboMouse is running on the other computer\n- Firewall allows port {port}\n- The IP address is correct",
                    "Connection Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to {address}:{port}\n\n{ex.GetType().Name}: {ex.Message}",
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
        UpdateBorderOverlay();
    }

    private void UpdateBorderOverlay()
    {
        // Show border flash when mouse enters this screen (either from remote control, or returning from controlling remote)
        bool wasControllingRemote = _borderOverlay?.Tag as string == "controlling";
        bool isNowLocal = !_service.IsControlledByRemote && !_service.IsControllingRemote;

        // Flash border when: entering from remote, OR returning from controlling remote
        if (_service.IsControlledByRemote || (wasControllingRemote && isNowLocal))
        {
            if (_borderOverlay == null || _borderOverlay.IsDisposed)
            {
                _borderOverlay = new BorderOverlayForm();
            }
            _borderOverlay.Tag = _service.IsControllingRemote ? "controlling" : null;
            _borderOverlay.ShowBorder();
            _borderOverlay.HideBorder(fadeOut: true); // Immediately start fading
        }

        // Track if we're controlling remote for next state change
        if (_service.IsControllingRemote)
        {
            if (_borderOverlay == null || _borderOverlay.IsDisposed)
            {
                _borderOverlay = new BorderOverlayForm();
            }
            _borderOverlay.Tag = "controlling";
        }
    }

    private void OnServiceError(object? sender, Exception e)
    {
        if (InvokeRequired(() => OnServiceError(sender, e)))
            return;

        ShowBalloon($"Error: {e.Message}", ToolTipIcon.Error);
    }

    private void OnDebugPanelToggled(object? sender, EventArgs e)
    {
        var item = sender as ToolStripMenuItem;
        _settings.DebugPanelEnabled = item?.Checked ?? false;
        _settings.Save();

        if (_settings.DebugPanelEnabled)
        {
            // Create panel if needed and show if currently controlling
            if (_debugPanel == null || _debugPanel.IsDisposed)
            {
                _debugPanel = new DebugPanelForm();
            }

            if (_service.IsControllingRemote)
            {
                _debugPanel.ShowOnEdge(_service.ActivePeer?.Position.ToString());
            }
        }
        else
        {
            _debugPanel?.Hide();
        }
    }

    private void OnMouseDebugUpdate(object? sender, MouseDebugEventArgs e)
    {
        if (!_settings.DebugPanelEnabled)
            return;

        if (_debugPanel == null || _debugPanel.IsDisposed)
        {
            _debugPanel = new DebugPanelForm();
        }

        // Show panel if controlling and not visible
        if (e.IsControlling && !_debugPanel.Visible)
        {
            _debugPanel.ShowOnEdge(_service.ActivePeer?.Position.ToString());
        }
        // Hide panel if no longer controlling
        else if (!e.IsControlling && _debugPanel.Visible)
        {
            _debugPanel.Hide();
        }

        // Update the data
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
            _debugPanel?.Dispose();
            _borderOverlay?.Dispose();
        }
        base.Dispose(disposing);
    }
}
