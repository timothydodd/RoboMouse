using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App.Forms;

/// <summary>
/// Settings configuration form.
/// </summary>
public partial class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;

    // Controls
    private TextBox _machineNameTextBox = null!;
    private NumericUpDown _portNumeric = null!;
    private NumericUpDown _discoveryPortNumeric = null!;
    private TextBox _pairingCodeTextBox = null!;
    private CheckBox _clipboardEnabledCheck = null!;
    private CheckBox _shareFilesCheck = null!;
    private CheckBox _borderHighlightCheck = null!;
#if DEBUG
    private CheckBox _debugPanelCheck = null!;
#endif
    private ListView _discoveredList = null!;
    private Button _addDiscoveredButton = null!;
    private CheckBox _startWithWindowsCheck = null!;
    private CheckBox _startMinimizedCheck = null!;
    private TextBox _hotkeyTextBox = null!;
    private ListView _peersList = null!;
    private Button _testButton = null!;
    private Button _connectButton = null!;
    private Button _editButton = null!;
    private Button _removeButton = null!;
    private ComboBox _positionCombo = null!;
    private Label _peerHintLabel = null!;
    private System.Windows.Forms.Timer _statusTimer = null!;
    private bool _suppressPositionChange;

    public SettingsForm(AppSettings settings, RoboMouseService service)
    {
        _settings = settings;
        _service = service;

        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        Text = $"RoboMouse Settings  (v{typeof(SettingsForm).Assembly.GetName().Version?.ToString(3)})";
        Size = new Size(560, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill
        };

        tabControl.TabPages.Add(CreateGeneralTab());
        tabControl.TabPages.Add(CreateNetworkTab());
        tabControl.TabPages.Add(CreatePeersTab());

        Controls.Add(tabControl);

        // Buttons panel
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50
        };

        var saveButton = new Button
        {
            Text = "Save",
            Width = 80,
            Height = 30,
            Location = new Point(Width - 200, 10)
        };
        saveButton.Click += OnSaveClick;

        var cancelButton = new Button
        {
            Text = "Cancel",
            Width = 80,
            Height = 30,
            Location = new Point(Width - 100, 10)
        };
        cancelButton.Click += (s, e) => Close();

        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(cancelButton);
        Controls.Add(buttonPanel);
    }

    private TabPage CreateGeneralTab()
    {
        var tab = new TabPage("General");
        tab.Padding = new Padding(10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

        // Machine name
        layout.Controls.Add(new Label { Text = "Machine Name:", AutoSize = true }, 0, row);
        _machineNameTextBox = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_machineNameTextBox, 1, row++);

        // Start with Windows
        layout.Controls.Add(new Label { Text = "Startup:", AutoSize = true }, 0, row);
        _startWithWindowsCheck = new CheckBox { Text = "Start with Windows", AutoSize = true };
        layout.Controls.Add(_startWithWindowsCheck, 1, row++);

        // Start minimized
        layout.Controls.Add(new Label(), 0, row);
        _startMinimizedCheck = new CheckBox { Text = "Start minimized to tray", AutoSize = true };
        layout.Controls.Add(_startMinimizedCheck, 1, row++);

        // Toggle hotkey
        layout.Controls.Add(new Label { Text = "Hotkey:", AutoSize = true }, 0, row);
        var hotkeyPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
        _hotkeyTextBox = new TextBox { Width = 200 };
        hotkeyPanel.Controls.Add(_hotkeyTextBox);
        hotkeyPanel.Controls.Add(new Label
        {
            Text = "Releases control of another screen if you are stuck there; otherwise turns sharing on or off.",
            AutoSize = true,
            ForeColor = Color.Gray
        });
        layout.Controls.Add(hotkeyPanel, 1, row++);

        // Clipboard sync
        layout.Controls.Add(new Label { Text = "Clipboard:", AutoSize = true }, 0, row);
        var clipboardPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
        _clipboardEnabledCheck = new CheckBox { Text = "Share text and images", AutoSize = true };
        clipboardPanel.Controls.Add(_clipboardEnabledCheck);
        _shareFilesCheck = new CheckBox { Text = "Share copied files (paste them in Explorer on the other machine)", AutoSize = true };
        clipboardPanel.Controls.Add(_shareFilesCheck);
        clipboardPanel.Controls.Add(new Label
        {
            Text = "Files transfer only when you paste, straight from the machine you copied them on.",
            AutoSize = true,
            ForeColor = Color.Gray
        });
        layout.Controls.Add(clipboardPanel, 1, row++);

        // Border highlight
        layout.Controls.Add(new Label { Text = "Screen border:", AutoSize = true }, 0, row);
        _borderHighlightCheck = new CheckBox { Text = "Flash a border when the mouse arrives on this screen", AutoSize = true };
        layout.Controls.Add(_borderHighlightCheck, 1, row++);

#if DEBUG
        // Debug panel (debug builds only)
        layout.Controls.Add(new Label { Text = "Debug:", AutoSize = true }, 0, row);
        _debugPanelCheck = new CheckBox { Text = "Show debug panel while controlling another screen", AutoSize = true };
        layout.Controls.Add(_debugPanelCheck, 1, row++);
#endif

        // Tray legend
        layout.Controls.Add(new Label { Text = "Tray icon:", AutoSize = true }, 0, row);
        layout.Controls.Add(new Label
        {
            Text = "Grey signal = no peers connected, green = connected,\nblue = controlling another screen, orange = being controlled, faded = disabled.",
            AutoSize = true,
            ForeColor = Color.Gray
        }, 1, row++);

        tab.Controls.Add(layout);
        return tab;
    }

    private TabPage CreateNetworkTab()
    {
        var tab = new TabPage("Network");
        tab.Padding = new Padding(10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

        // Pairing code
        layout.Controls.Add(new Label { Text = "Pairing code:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) }, 0, row);
        var pairingPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        pairingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pairingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pairingPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _pairingCodeTextBox = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 11), CharacterCasing = CharacterCasing.Upper };
        pairingPanel.Controls.Add(_pairingCodeTextBox, 0, 0);
        var copyCodeButton = new Button { Text = "Copy", AutoSize = true };
        copyCodeButton.Click += (s, e) => { try { Clipboard.SetText(_pairingCodeTextBox.Text); } catch { } };
        pairingPanel.Controls.Add(copyCodeButton, 1, 0);
        var newCodeButton = new Button { Text = "New", AutoSize = true };
        newCodeButton.Click += (s, e) =>
        {
            if (MessageBox.Show(this, "Generate a new pairing code? Every other machine will need the new code before it can connect again.",
                    "RoboMouse", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _pairingCodeTextBox.Text = RoboMouse.Core.Network.SecureChannel.GeneratePairingCode();
        };
        pairingPanel.Controls.Add(newCodeButton, 2, 0);
        layout.Controls.Add(pairingPanel, 1, row++);

        layout.Controls.Add(new Label(), 0, row);
        layout.Controls.Add(new Label
        {
            Text = "Enter the same code on every machine. It authenticates peers and encrypts all traffic;\n" +
                   "a machine with a different code cannot connect. Existing connections keep their session until they reconnect.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(3, 0, 3, 10)
        }, 1, row++);

        // Local port
        layout.Controls.Add(new Label { Text = "Listen Port:", AutoSize = true }, 0, row);
        _portNumeric = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Width = 100
        };
        layout.Controls.Add(_portNumeric, 1, row++);

        // Discovery port
        layout.Controls.Add(new Label { Text = "Discovery Port:", AutoSize = true }, 0, row);
        _discoveryPortNumeric = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Width = 100
        };
        layout.Controls.Add(_discoveryPortNumeric, 1, row++);

        // Machine ID (read-only)
        layout.Controls.Add(new Label { Text = "Machine ID:", AutoSize = true }, 0, row);
        var idTextBox = new TextBox
        {
            Text = _settings.MachineId,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Control
        };
        layout.Controls.Add(idTextBox, 1, row++);

        // Info label
        var infoLabel = new Label
        {
            Text = "Note: Port changes require restart to take effect.",
            AutoSize = true,
            ForeColor = Color.Gray
        };
        layout.Controls.Add(infoLabel, 0, row++);
        layout.SetColumnSpan(infoLabel, 2);

        // Firewall
        layout.Controls.Add(new Label { Text = "Firewall:", AutoSize = true, Margin = new Padding(0, 12, 0, 0) }, 0, row);
        var firewallPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 8, 0, 0) };
        var firewallButton = new Button { Text = "Allow RoboMouse through Windows Firewall...", AutoSize = true };
        firewallButton.Click += OnFirewallClick;
        firewallPanel.Controls.Add(firewallButton);
        firewallPanel.Controls.Add(new Label
        {
            Text = "Adds inbound rules for the ports above from any address, on all network profiles.\n" +
                   "Needed when the other machine is on a different subnet: the rule Windows creates\n" +
                   "automatically is often limited to the local subnet. Requires administrator approval.",
            AutoSize = true,
            ForeColor = Color.Gray
        });
        firewallPanel.Controls.Add(new Label
        {
            Text = "Automatic discovery uses broadcast and never crosses subnets; add such peers by IP.",
            AutoSize = true,
            ForeColor = Color.Gray
        });
        layout.Controls.Add(firewallPanel, 1, row++);

        tab.Controls.Add(layout);
        return tab;
    }

    private void OnFirewallClick(object? sender, EventArgs e)
    {
        var tcp = (int)_portNumeric.Value;
        var udp = (int)_discoveryPortNumeric.Value;

        // One elevated cmd that replaces any previous RoboMouse rules.
        var script =
            $"netsh advfirewall firewall delete rule name=\"RoboMouse (TCP)\" & " +
            $"netsh advfirewall firewall delete rule name=\"RoboMouse (UDP)\" & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (TCP)\" dir=in action=allow protocol=TCP localport={tcp} remoteip=any profile=any & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (UDP)\" dir=in action=allow protocol=UDP localport={udp} remoteip=any profile=any";

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
            process?.WaitForExit(15000);

            if (process?.ExitCode == 0)
            {
                MessageBox.Show(this, $"Firewall rules added for TCP {tcp} and UDP {udp}.", "RoboMouse",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "The firewall command did not complete. You can add the rules manually in Windows Defender Firewall with Advanced Security.",
                    "RoboMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the elevation prompt.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not update the firewall: {ex.Message}", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private TabPage CreatePeersTab()
    {
        var tab = new TabPage("Configured Peers");
        tab.Padding = new Padding(10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));

        _peersList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false
        };
        _peersList.Columns.Add("Name", 110);
        _peersList.Columns.Add("Address", 120);
        _peersList.Columns.Add("Position", 60);
        _peersList.Columns.Add("Status", 100);
        _peersList.SelectedIndexChanged += (s, e) => UpdatePeerButtons();
        _peersList.DoubleClick += OnEditPeerClick;
        layout.Controls.Add(_peersList, 0, 0);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown
        };

        var addButton = new Button { Text = "Add...", Width = 110 };
        addButton.Click += OnAddPeerClick;
        buttonPanel.Controls.Add(addButton);

        _editButton = new Button { Text = "Edit...", Width = 110 };
        _editButton.Click += OnEditPeerClick;
        buttonPanel.Controls.Add(_editButton);

        _removeButton = new Button { Text = "Remove", Width = 110 };
        _removeButton.Click += OnRemovePeerClick;
        buttonPanel.Controls.Add(_removeButton);

        buttonPanel.Controls.Add(new Label { Height = 8 });

        _testButton = new Button { Text = "Test Connection", Width = 110 };
        _testButton.Click += OnTestPeerClick;
        buttonPanel.Controls.Add(_testButton);

        _connectButton = new Button { Text = "Connect", Width = 110 };
        _connectButton.Click += OnConnectPeerClick;
        buttonPanel.Controls.Add(_connectButton);

        buttonPanel.Controls.Add(new Label { Height = 8 });

        var layoutButton = new Button { Text = "Screen Layout...", Width = 110 };
        layoutButton.Click += (s, e) =>
        {
            using var form = new ScreenLayoutForm(_settings, _service);
            form.ShowDialog(this);
            RefreshPeersList();
        };
        buttonPanel.Controls.Add(layoutButton);

        layout.Controls.Add(buttonPanel, 1, 0);

        // Quick position change
        var positionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        positionPanel.Controls.Add(new Label { Text = "Selected peer is:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        _positionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        foreach (var position in PeerPositions.All)
            _positionCombo.Items.Add(new PositionChoice(position));
        _positionCombo.SelectedIndexChanged += OnQuickPositionChanged;
        positionPanel.Controls.Add(_positionCombo);
        positionPanel.Controls.Add(new Label { Text = "of this screen", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
        layout.Controls.Add(positionPanel, 0, 1);
        layout.SetColumnSpan(positionPanel, 2);

        _peerHintLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(0, 4, 0, 0),
            Text = "Position changes apply immediately. Peers on another subnet must be added by IP."
        };
        layout.Controls.Add(_peerHintLabel, 0, 2);
        layout.SetColumnSpan(_peerHintLabel, 2);

        // Discovered peers (same subnet only)
        layout.Controls.Add(new Label
        {
            Text = "Found on this network (not yet configured):",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 2)
        }, 0, 3);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 3)!, 2);

        _discoveredList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false
        };
        _discoveredList.Columns.Add("Name", 140);
        _discoveredList.Columns.Add("Address", 150);
        _discoveredList.Columns.Add("Screen", 90);
        _discoveredList.SelectedIndexChanged += (s, e) => _addDiscoveredButton.Enabled = _discoveredList.SelectedItems.Count > 0;
        _discoveredList.DoubleClick += OnAddDiscoveredClick;
        layout.Controls.Add(_discoveredList, 0, 4);

        var discoveredButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        _addDiscoveredButton = new Button { Text = "Add...", Width = 110, Enabled = false };
        _addDiscoveredButton.Click += OnAddDiscoveredClick;
        discoveredButtons.Controls.Add(_addDiscoveredButton);
        layout.Controls.Add(discoveredButtons, 1, 4);

        tab.Controls.Add(layout);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (s, e) =>
        {
            RefreshPeerStatus();
            RefreshDiscoveredList();
        };
        _statusTimer.Start();

        return tab;
    }

    private void RefreshDiscoveredList()
    {
        if (IsDisposed)
            return;

        var configuredIds = _settings.Peers.Select(p => p.Id).ToHashSet();
        var configuredAddresses = _settings.Peers.Select(p => p.Address).ToHashSet();
        var peers = _service.DiscoveredPeers
            .Where(p => p.MachineId != _settings.MachineId
                        && !configuredIds.Contains(p.MachineId)
                        && !configuredAddresses.Contains(p.Address.ToString()))
            .OrderBy(p => p.MachineName)
            .ToList();

        var current = _discoveredList.Items.Cast<ListViewItem>().Select(i => (i.Tag as DiscoveredPeer)?.MachineId).ToList();
        if (current.SequenceEqual(peers.Select(p => p.MachineId)))
            return;

        var selectedId = (_discoveredList.SelectedItems.Count > 0 ? _discoveredList.SelectedItems[0].Tag as DiscoveredPeer : null)?.MachineId;
        _discoveredList.BeginUpdate();
        _discoveredList.Items.Clear();
        foreach (var peer in peers)
        {
            var item = new ListViewItem(peer.MachineName) { Tag = peer };
            item.SubItems.Add($"{peer.Address}:{peer.Port}");
            item.SubItems.Add($"{peer.ScreenWidth}x{peer.ScreenHeight}");
            _discoveredList.Items.Add(item);
            if (peer.MachineId == selectedId)
                item.Selected = true;
        }
        _discoveredList.EndUpdate();
        _addDiscoveredButton.Enabled = _discoveredList.SelectedItems.Count > 0;
    }

    private async void OnAddDiscoveredClick(object? sender, EventArgs e)
    {
        if (_discoveredList.SelectedItems.Count == 0 || _discoveredList.SelectedItems[0].Tag is not DiscoveredPeer discovered)
            return;

        var draft = new PeerConfig
        {
            Id = discovered.MachineId,
            Name = discovered.MachineName,
            Address = discovered.Address.ToString(),
            Port = discovered.Port,
            ScreenWidth = discovered.ScreenWidth,
            ScreenHeight = discovered.ScreenHeight,
            Position = PeerPositions.All.FirstOrDefault(pos => _settings.Peers.All(p => p.Position != pos), ScreenPosition.Right)
        };

        using var dialog = new PeerSetupForm(draft, _service, _settings);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.PeerConfig == null)
            return;

        _settings.Peers.Add(dialog.PeerConfig);
        _settings.Save();
        RefreshPeersList();
        RefreshDiscoveredList();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _service.ConnectToPeerAsync(dialog.PeerConfig, cts.Token);
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Added {dialog.PeerConfig.Name}, but could not connect yet: {ex.Message}",
                "RoboMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        RefreshPeersList();
    }

    private PeerConfig? SelectedPeer => _peersList.SelectedItems.Count > 0 ? _peersList.SelectedItems[0].Tag as PeerConfig : null;

    private void RefreshPeersList()
    {
        var selected = SelectedPeer;
        _peersList.BeginUpdate();
        _peersList.Items.Clear();
        foreach (var peer in _settings.Peers)
        {
            var item = new ListViewItem(peer.Name) { Tag = peer };
            item.SubItems.Add($"{peer.Address}:{peer.Port}");
            item.SubItems.Add(PeerPositions.Describe(peer.Position));
            item.SubItems.Add(string.Empty);
            _peersList.Items.Add(item);
            if (peer == selected)
                item.Selected = true;
        }
        _peersList.EndUpdate();
        RefreshPeerStatus();
        UpdatePeerButtons();
    }

    private void RefreshPeerStatus()
    {
        if (IsDisposed)
            return;

        foreach (ListViewItem item in _peersList.Items)
        {
            if (item.Tag is not PeerConfig peer)
                continue;

            var connection = _service.GetConnection(peer.Id);
            var status = connection == null
                ? "Not connected"
                : connection.RoundTripMs >= 0 ? $"Connected, {connection.RoundTripMs} ms" : "Connected";

            if (item.SubItems[3].Text != status)
            {
                item.SubItems[3].Text = status;
                item.ForeColor = connection == null ? SystemColors.GrayText : SystemColors.WindowText;
            }
            var position = PeerPositions.Describe(peer.Position);
            if (item.SubItems[2].Text != position)
                item.SubItems[2].Text = position;
        }

        var selected = SelectedPeer;
        if (selected != null)
            _connectButton.Text = _service.IsPeerConnected(selected.Id) ? "Disconnect" : "Connect";
    }

    private void UpdatePeerButtons()
    {
        var peer = SelectedPeer;
        var has = peer != null;
        _editButton.Enabled = has;
        _removeButton.Enabled = has;
        _testButton.Enabled = has;
        _connectButton.Enabled = has;
        _positionCombo.Enabled = has;

        _suppressPositionChange = true;
        _positionCombo.SelectedIndex = peer == null ? -1 : Array.IndexOf(PeerPositions.All, peer.Position);
        _suppressPositionChange = false;

        if (peer != null)
            _connectButton.Text = _service.IsPeerConnected(peer.Id) ? "Disconnect" : "Connect";
    }

    private void OnQuickPositionChanged(object? sender, EventArgs e)
    {
        if (_suppressPositionChange || SelectedPeer is not PeerConfig peer || _positionCombo.SelectedItem is not PositionChoice choice)
            return;

        if (!PeerPositions.TrySet(_settings, peer, choice.Position, this))
        {
            // Declined swap or no change: put the combo back.
            _suppressPositionChange = true;
            _positionCombo.SelectedIndex = Array.IndexOf(PeerPositions.All, peer.Position);
            _suppressPositionChange = false;
            return;
        }

        RefreshPeersList();
    }

    private async void OnTestPeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is not PeerConfig peer)
            return;

        _testButton.Enabled = false;
        _testButton.Text = "Testing...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _service.TestConnectionAsync(peer, cts.Token);
            MessageBox.Show(this, result.Summary, $"Connection test: {peer.Name}",
                MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _testButton.Text = "Test Connection";
            UpdatePeerButtons();
        }
    }

    private async void OnConnectPeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is not PeerConfig peer)
            return;

        _connectButton.Enabled = false;
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
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, $"Connecting to {peer.Address}:{peer.Port} timed out. Use Test Connection for details.",
                "RoboMouse", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not connect to {peer.Name}: {ex.Message}", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshPeersList();
        }
    }

    private class PositionChoice
    {
        public ScreenPosition Position { get; }
        public PositionChoice(ScreenPosition position) => Position = position;
        public override string ToString() => PeerPositions.Describe(Position);
    }

    private void LoadSettings()
    {
        _machineNameTextBox.Text = _settings.MachineName;
        _portNumeric.Value = _settings.LocalPort;
        _discoveryPortNumeric.Value = _settings.DiscoveryPort;
        _pairingCodeTextBox.Text = _settings.PairingCode;
        _clipboardEnabledCheck.Checked = _settings.Clipboard.Enabled;
        _shareFilesCheck.Checked = _settings.Clipboard.SyncFiles;
        _borderHighlightCheck.Checked = _settings.ShowBorderHighlight;
#if DEBUG
        _debugPanelCheck.Checked = _settings.DebugPanelEnabled;
#endif
        _startWithWindowsCheck.Checked = _settings.StartWithWindows;
        _startMinimizedCheck.Checked = _settings.StartMinimized;
        _hotkeyTextBox.Text = _settings.ToggleHotkey ?? "";

        RefreshPeersList();
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        var code = _pairingCodeTextBox.Text.Trim();
        if (code.Replace("-", "").Replace(" ", "").Length < 8)
        {
            MessageBox.Show(this, "The pairing code must be at least 8 characters.", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.PairingCode = code;

        if (!string.IsNullOrWhiteSpace(_hotkeyTextBox.Text) && RoboMouse.Core.Input.Hotkey.Parse(_hotkeyTextBox.Text) == null)
        {
            MessageBox.Show(this, "The hotkey must be a key with at least one modifier, for example Ctrl+Alt+M.", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.MachineName = _machineNameTextBox.Text;
        _settings.LocalPort = (int)_portNumeric.Value;
        _settings.DiscoveryPort = (int)_discoveryPortNumeric.Value;
        _settings.Clipboard.Enabled = _clipboardEnabledCheck.Checked;
        _settings.Clipboard.SyncFiles = _shareFilesCheck.Checked;
        _settings.ShowBorderHighlight = _borderHighlightCheck.Checked;
#if DEBUG
        _settings.DebugPanelEnabled = _debugPanelCheck.Checked;
#endif
        _settings.StartWithWindows = _startWithWindowsCheck.Checked;
        _settings.StartMinimized = _startMinimizedCheck.Checked;
        _settings.ToggleHotkey = string.IsNullOrWhiteSpace(_hotkeyTextBox.Text) ? null : _hotkeyTextBox.Text;

        _settings.Save();
        _service.ApplyClipboardSetting();
        _service.ApplyHotkeySetting();

        // Update startup registry
        UpdateStartupRegistry();

        Close();
    }

    private void UpdateStartupRegistry()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

            if (key != null)
            {
                if (_settings.StartWithWindows)
                {
                    key.SetValue("RoboMouse", $"\"{Application.ExecutablePath}\"");
                }
                else
                {
                    key.DeleteValue("RoboMouse", false);
                }
                key.Close();
            }
        }
        catch
        {
            // Ignore registry errors
        }
    }

    private void OnAddPeerClick(object? sender, EventArgs e)
    {
        using var dialog = new PeerSetupForm(null, _service, _settings);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.PeerConfig != null)
        {
            _settings.Peers.Add(dialog.PeerConfig);
            _settings.Save();
            RefreshPeersList();
        }
    }

    private void OnEditPeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
        {
            using var dialog = new PeerSetupForm(peer, _service, _settings);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _settings.Save();
                RefreshPeersList();
            }
        }
    }

    private async void OnRemovePeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
        {
            if (_service.IsPeerConnected(peer.Id))
            {
                try { await _service.DisconnectFromPeerAsync(peer.Id); } catch { }
            }
            _settings.Peers.Remove(peer);
            _settings.Save();
            RefreshPeersList();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
