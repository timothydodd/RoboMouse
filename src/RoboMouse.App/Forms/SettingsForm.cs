using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App.Forms;

/// <summary>
/// Settings window: a header with the live status, a navigation rail on the left and one page per
/// area (General, Network, Peers).
/// </summary>
public partial class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly RoboMouseService _service;

    // Header
    private Label _statusLabel = null!;
    private Panel _statusDot = null!;
    private Color _statusColor = Ui.Grey;

    // Navigation
    private readonly List<(Button button, Panel page)> _pages = new();

    // General
    private TextBox _machineNameTextBox = null!;
    private CheckBox _startWithWindowsCheck = null!;
    private CheckBox _startMinimizedCheck = null!;
    private TextBox _hotkeyTextBox = null!;
    private CheckBox _clipboardEnabledCheck = null!;
    private CheckBox _shareFilesCheck = null!;
    private ComboBox _highlightCombo = null!;
#if DEBUG
    private CheckBox _debugPanelCheck = null!;
#endif

    // Network
    private NumericUpDown _portNumeric = null!;
    private NumericUpDown _discoveryPortNumeric = null!;
    private TextBox _pairingCodeTextBox = null!;

    // Peers
    private ListView _peersList = null!;
    private ListView _discoveredList = null!;
    private Button _addDiscoveredButton = null!;
    private Button _editButton = null!;
    private Button _removeButton = null!;
    private Button _enableButton = null!;
    private bool _suppressItemCheck;

    // Layout
    private ScreenLayoutPanel _layoutPanel = null!;
    private System.Windows.Forms.Timer _statusTimer = null!;

    public SettingsForm(AppSettings settings, RoboMouseService service)
    {
        _settings = settings;
        _service = service;

        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        Ui.Style(this);
        Text = "RoboMouse";
        Icon = Ui.AppIcon();
        ClientSize = new Size(940, 720);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var saveButton = Ui.PrimaryButton("Save", 100);
        saveButton.Click += OnSaveClick;
        var cancelButton = Ui.Button("Cancel", 100);
        cancelButton.Click += (s, e) => Close();
        CancelButton = cancelButton;

        Controls.Add(CreateHeader());
        Controls.Add(Ui.ActionBar(saveButton, cancelButton));

        var body = new Panel { Dock = DockStyle.Fill };
        var rail = CreateNavRail();
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.Pad + 4, Ui.Pad, Ui.Pad, Ui.Pad) };
        body.Controls.Add(content);
        body.Controls.Add(rail);
        Controls.Add(body);
        body.BringToFront();

        AddPage("General", CreateGeneralPage(), rail, content);
        AddPage("Network", CreateNetworkPage(), rail, content);
        AddPage("Peers", CreatePeersPage(), rail, content);
        AddPage("Layout", CreateLayoutPage(), rail, content);
        ShowPage(0);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (s, e) =>
        {
            RefreshStatus();
            RefreshPeerStatus();
            RefreshDiscoveredList();
        };
        _statusTimer.Start();
    }

    // ------------------------------------------------------------------ chrome

    private Control CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Ui.Card, Padding = new Padding(Ui.Pad, 0, Ui.Pad, 0) };
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Border });

        var logo = new PictureBox
        {
            Image = Ui.AppImage(40),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(40, 40),
            Location = new Point(Ui.Pad, 16)
        };
        header.Controls.Add(logo);

        var title = new Label { Text = "RoboMouse", Font = Ui.Title, AutoSize = true, ForeColor = Ui.Text, Location = new Point(Ui.Pad + 52, 14) };
        header.Controls.Add(title);

        var version = new Label
        {
            Text = $"Version {typeof(SettingsForm).Assembly.GetName().Version?.ToString(3)}  ·  {_settings.MachineName}",
            Font = Ui.Small,
            AutoSize = true,
            ForeColor = Ui.Muted,
            Location = new Point(Ui.Pad + 54, 44)
        };
        header.Controls.Add(version);

        // Status pill, right-aligned.
        var pill = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(0), Margin = new Padding(0) };
        _statusDot = new Panel { Width = 10, Height = 10, Margin = new Padding(0, 8, 8, 0) };
        _statusDot.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_statusColor);
            e.Graphics.FillEllipse(brush, 0, 0, 9, 9);
        };
        _statusLabel = new Label { Text = "Not connected", AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 4, 0, 0) };
        pill.Controls.Add(_statusDot);
        pill.Controls.Add(_statusLabel);
        header.Controls.Add(pill);
        header.Resize += (s, e) => pill.Location = new Point(header.ClientSize.Width - pill.Width - Ui.Pad, 26);
        pill.SizeChanged += (s, e) => pill.Location = new Point(header.ClientSize.Width - pill.Width - Ui.Pad, 26);

        return header;
    }

    private Panel CreateNavRail()
    {
        var rail = new Panel { Dock = DockStyle.Left, Width = 168, BackColor = Ui.Sidebar, Padding = new Padding(12, 16, 12, 12) };
        rail.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Ui.Border });
        return rail;
    }

    private void AddPage(string name, Panel page, Panel rail, Panel content)
    {
        var index = _pages.Count;
        var button = new Button
        {
            Text = "   " + name,
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            Height = 36,
            Dock = DockStyle.Top,
            BackColor = Ui.Sidebar,
            ForeColor = Ui.Text,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(230, 235, 243);
        button.Click += (s, e) => ShowPage(index);
        button.Paint += (s, e) =>
        {
            if (page.Visible)
            {
                using var brush = new SolidBrush(Ui.Accent);
                e.Graphics.FillRectangle(brush, 0, 8, 3, button.Height - 16);
            }
        };

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        content.Controls.Add(page);

        // Docked Top controls stack in reverse order of addition; insert at 0 keeps declaration order.
        rail.Controls.Add(button);
        button.BringToFront();
        _pages.Add((button, page));
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            var (button, page) = _pages[i];
            var active = i == index;
            page.Visible = active;
            if (active && page == _layoutPanel.Parent?.Parent)
                _layoutPanel.Reload();
            button.BackColor = active ? Ui.Card : Ui.Sidebar;
            button.Font = active ? Ui.Strong : Ui.Body;
            button.Invalidate();
        }
    }

    /// <summary>A settings page: scrolls vertically when its sections do not fit the window.</summary>
    private static Panel Page()
    {
        var page = new Panel { AutoScroll = true, Padding = new Padding(0, 0, Ui.Pad, 0) };
        // Never scroll sideways: the sections are laid out to the page width.
        page.HorizontalScroll.Enabled = false;
        page.HorizontalScroll.Visible = false;
        return page;
    }

    /// <summary>Stacks docked-top controls so they appear in the given order.</summary>
    private static void Fill(Panel page, params Control[] controls)
    {
        foreach (var c in controls.Reverse())
        {
            if (c.Dock == DockStyle.None)
                c.Dock = DockStyle.Top;
            if (c is Label heading && !heading.AutoSize)
            {
                // Docked controls ignore Margin: fold the heading's spacing into its own height.
                heading.Padding = new Padding(0, heading.Margin.Top, 0, heading.Margin.Bottom);
                heading.Height += heading.Margin.Top + heading.Margin.Bottom;
                heading.Margin = Padding.Empty;
            }
            page.Controls.Add(c);
        }
    }

    private void RefreshStatus()
    {
        if (IsDisposed)
            return;

        var connected = _service.ConnectedPeers.Count;
        string text;
        Color color;
        if (!_service.Enabled) { text = "Sharing off"; color = Ui.Grey; }
        else if (_service.IsControllingRemote) { text = $"Controlling {_service.ActivePeer?.Name}"; color = Ui.Accent; }
        else if (_service.IsControlledByRemote) { text = "Being controlled"; color = Ui.Orange; }
        else if (connected == 0) { text = "Not connected"; color = Ui.Grey; }
        else if (connected == 1) { text = $"Connected to {_service.ConnectedPeers.First().PeerName}"; color = Ui.Green; }
        else { text = $"Connected to {connected} peers"; color = Ui.Green; }

        if (_statusLabel.Text != text)
            _statusLabel.Text = text;
        if (_statusColor != color)
        {
            _statusColor = color;
            _statusDot.Invalidate();
        }
    }

    // ------------------------------------------------------------------ General

    private Panel CreateGeneralPage()
    {
        var page = Page();

        var identity = Ui.FormGrid();
        _machineNameTextBox = Ui.TextBox(260);
        Ui.Row(identity, "Machine name", _machineNameTextBox);
        Ui.Row(identity, "", Ui.Hint("How this computer appears on the other machines."));

        var startup = Ui.Stack();
        _startWithWindowsCheck = Ui.Check("Start with Windows");
        _startMinimizedCheck = Ui.Check("Start minimized to the tray");
        startup.Controls.Add(_startWithWindowsCheck);
        startup.Controls.Add(_startMinimizedCheck);

        var hotkey = Ui.FormGrid();
        _hotkeyTextBox = Ui.TextBox(200);
        Ui.Row(hotkey, "Toggle hotkey", _hotkeyTextBox);
        Ui.Row(hotkey, "", Ui.Hint("For example Ctrl+Alt+M. Releases control of another screen if you are stuck there; otherwise turns sharing on or off.", 460));

        var clipboard = Ui.Stack();
        _clipboardEnabledCheck = Ui.Check("Share text and images");
        _shareFilesCheck = Ui.Check("Share copied files (paste them in Explorer on the other machine)");
        clipboard.Controls.Add(_clipboardEnabledCheck);
        clipboard.Controls.Add(_shareFilesCheck);
        clipboard.Controls.Add(Ui.Hint("Files transfer only when you paste, straight from the machine you copied them on.", 460));

        var display = Ui.Stack();
        var highlightRow = Ui.Inline();
        highlightRow.Controls.Add(Ui.Label("When the mouse arrives on this screen"));
        _highlightCombo = Ui.Combo(200);
        _highlightCombo.Items.AddRange(new object[]
        {
            new HighlightChoice(EdgeHighlightStyle.None, "Show nothing"),
            new HighlightChoice(EdgeHighlightStyle.Border, "Flash a border around the screen"),
            new HighlightChoice(EdgeHighlightStyle.Fade, "Glow along the edge it came in on"),
        });
        highlightRow.Controls.Add(_highlightCombo);
        display.Controls.Add(highlightRow);
#if DEBUG
        _debugPanelCheck = Ui.Check("Show the debug panel while controlling another screen");
        display.Controls.Add(_debugPanelCheck);
#endif

        var legend = Ui.Stack();
        legend.Controls.Add(Ui.Hint("The border colour of the tray icon shows what RoboMouse is doing:"));
        foreach (var (color, text) in new[]
        {
            (Ui.Grey, "No peers connected"),
            (Ui.Green, "Connected"),
            (Ui.Accent, "Controlling another screen"),
            (Ui.Orange, "Being controlled"),
        })
        {
            var row = Ui.Inline();
            row.Margin = new Padding(0, 0, 0, 2);
            row.Controls.Add(Ui.Dot(color));
            row.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Ui.Text, Margin = new Padding(0, 2, 0, 0) });
            legend.Controls.Add(row);
        }
        legend.Controls.Add(Ui.Hint("A faded icon means sharing is switched off."));

        Fill(page,
            Ui.SectionHeading("This computer", first: true), identity,
            Ui.SectionHeading("Startup"), startup,
            Ui.SectionHeading("Hotkey"), hotkey,
            Ui.SectionHeading("Clipboard"), clipboard,
            Ui.SectionHeading("Display"), display,
            Ui.SectionHeading("Tray icon"), legend);
        return page;
    }

    // ------------------------------------------------------------------ Network

    private Panel CreateNetworkPage()
    {
        var page = Page();

        var pairing = Ui.Stack();
        var codeRow = Ui.Inline();
        _pairingCodeTextBox = Ui.TextBox(230);
        _pairingCodeTextBox.Font = Ui.Mono;
        _pairingCodeTextBox.CharacterCasing = CharacterCasing.Upper;
        codeRow.Controls.Add(_pairingCodeTextBox);
        var copyCodeButton = Ui.Button("Copy");
        copyCodeButton.Margin = new Padding(Ui.Gap, 3, 0, 3);
        copyCodeButton.Click += (s, e) => { try { Clipboard.SetText(_pairingCodeTextBox.Text); } catch { } };
        codeRow.Controls.Add(copyCodeButton);
        var newCodeButton = Ui.Button("Generate new");
        newCodeButton.Margin = new Padding(Ui.Gap, 3, 0, 3);
        newCodeButton.Click += (s, e) =>
        {
            if (MessageBox.Show(this, "Generate a new pairing code? Every other machine will need the new code before it can connect again.",
                    "RoboMouse", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _pairingCodeTextBox.Text = SecureChannel.GeneratePairingCode();
        };
        codeRow.Controls.Add(newCodeButton);
        pairing.Controls.Add(codeRow);
        pairing.Controls.Add(Ui.Hint(
            "Enter the same code on every machine. It authenticates peers and encrypts all traffic; a machine with a " +
            "different code cannot connect. Existing connections keep their session until they reconnect.", 480));

        var ports = Ui.FormGrid();
        _portNumeric = Ui.Number(1024, 65535);
        Ui.Row(ports, "Listen port", _portNumeric);
        _discoveryPortNumeric = Ui.Number(1024, 65535);
        Ui.Row(ports, "Discovery port", _discoveryPortNumeric);
        var idTextBox = Ui.TextBox(300);
        idTextBox.Text = _settings.MachineId;
        idTextBox.ReadOnly = true;
        idTextBox.BackColor = Ui.Window;
        idTextBox.ForeColor = Ui.Muted;
        Ui.Row(ports, "Machine ID", idTextBox);
        Ui.Row(ports, "", Ui.Hint("Port changes take effect after RoboMouse is restarted."));

        var firewall = Ui.Stack();
        var firewallButton = Ui.Button("Allow RoboMouse through Windows Firewall…");
        firewallButton.Click += OnFirewallClick;
        firewall.Controls.Add(firewallButton);
        firewall.Controls.Add(Ui.Hint(
            "Adds inbound rules for the ports above from any address, on all network profiles. Needed when the other " +
            "machine is on a different subnet: the rule Windows creates automatically is often limited to the local " +
            "subnet. Requires administrator approval.", 480));
        firewall.Controls.Add(Ui.Hint("Automatic discovery uses broadcast and never crosses subnets; add such peers by IP address.", 480));

        Fill(page,
            Ui.SectionHeading("Pairing code", first: true), pairing,
            Ui.SectionHeading("Ports"), ports,
            Ui.SectionHeading("Firewall"), firewall);
        return page;
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

    // ------------------------------------------------------------------ Peers

    private Panel CreatePeersPage()
    {
        var page = Page();

        // Configured peers: the list takes the spare height, everything else is auto-sized.
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // heading
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); // peers list
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // toolbar
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // hint
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // discovered heading
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 38)); // discovered list
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // discovered toolbar

        var heading = Ui.SectionHeading("Configured peers", first: true);
        grid.Controls.Add(heading, 0, 0);

        _peersList = Ui.List();
        _peersList.CheckBoxes = true;
        _peersList.Columns.Add("Name", 170);
        _peersList.Columns.Add("Address", 150);
        _peersList.Columns.Add("Position", 80);
        _peersList.Columns.Add("Status", 150);
        _peersList.SelectedIndexChanged += (s, e) => UpdatePeerButtons();
        _peersList.DoubleClick += OnEditPeerClick;
        _peersList.ItemCheck += OnPeerItemCheck;
        _peersList.Resize += (s, e) => StretchLastColumn(_peersList);
        grid.Controls.Add(_peersList, 0, 1);

        var toolbar = Ui.Inline();
        toolbar.Margin = new Padding(0, Ui.Gap, 0, 0);
        var addButton = Ui.Button("Add…");
        addButton.Click += OnAddPeerClick;
        _editButton = Ui.Button("Edit…");
        _editButton.Click += OnEditPeerClick;
        _enableButton = Ui.Button("Disable");
        _enableButton.Click += OnToggleEnabledClick;
        _removeButton = Ui.DangerButton("Remove");
        _removeButton.Click += OnRemovePeerClick;
        foreach (var b in new[] { addButton, _editButton, _enableButton, _removeButton })
            toolbar.Controls.Add(b);
        grid.Controls.Add(toolbar, 0, 2);

        grid.Controls.Add(Ui.Hint("Untick a peer to switch it off: it keeps its settings but is never connected to and cannot take control of this screen. " +
                                  "Change which edge a peer sits on under Layout, or by editing it.", 560), 0, 3);

        grid.Controls.Add(Ui.SectionHeading("Found on this network"), 0, 4);

        _discoveredList = Ui.List();
        _discoveredList.Columns.Add("Name", 170);
        _discoveredList.Columns.Add("Address", 150);
        _discoveredList.Columns.Add("Screen", 110);
        _discoveredList.SelectedIndexChanged += (s, e) => _addDiscoveredButton.Enabled = _discoveredList.SelectedItems.Count > 0;
        _discoveredList.DoubleClick += OnAddDiscoveredClick;
        _discoveredList.Resize += (s, e) => StretchLastColumn(_discoveredList);
        grid.Controls.Add(_discoveredList, 0, 5);

        var discoveredBar = Ui.Inline();
        discoveredBar.Margin = new Padding(0, Ui.Gap, 0, 0);
        _addDiscoveredButton = Ui.Button("Add selected…");
        _addDiscoveredButton.Enabled = false;
        _addDiscoveredButton.Click += OnAddDiscoveredClick;
        discoveredBar.Controls.Add(_addDiscoveredButton);
        discoveredBar.Controls.Add(Ui.Hint("Machines running RoboMouse on this subnet that are not configured yet. Peers elsewhere must be added by IP address."));
        grid.Controls.Add(discoveredBar, 0, 6);

        page.Controls.Add(grid);
        return page;
    }

    private static Control Spacer() => new Panel { Width = 12, Height = 1, Margin = new Padding(0) };

    // ------------------------------------------------------------------ Layout

    private Panel CreateLayoutPage()
    {
        var page = Page();

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        grid.Controls.Add(Ui.SectionHeading("Screen layout", first: true), 0, 0);
        grid.Controls.Add(Ui.Hint("Drag a peer screen to the edge of this screen where that computer sits. Its position along the edge is kept too. " +
                                  "Faded screens are disabled peers. Changes are applied when you click Save.", 560), 0, 1);

        _layoutPanel = new ScreenLayoutPanel(_settings) { Dock = DockStyle.Fill, Margin = new Padding(0, Ui.Gap, 0, 0) };
        grid.Controls.Add(_layoutPanel, 0, 2);

        page.Controls.Add(grid);
        return page;
    }

    private static void StretchLastColumn(ListView list)
    {
        if (list.Columns.Count == 0)
            return;
        var used = 0;
        for (var i = 0; i < list.Columns.Count - 1; i++)
            used += list.Columns[i].Width;
        var last = list.Columns[^1];
        var target = list.ClientSize.Width - used - 4;
        if (target > 60 && last.Width != target)
            last.Width = target;
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
            item.SubItems.Add($"{peer.ScreenWidth} × {peer.ScreenHeight}");
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

        if (!dialog.PeerConfig.Enabled)
            return;

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
        _suppressItemCheck = true;
        _peersList.BeginUpdate();
        _peersList.Items.Clear();
        foreach (var peer in _settings.Peers)
        {
            var item = new ListViewItem(peer.Name) { Tag = peer, Checked = peer.Enabled };
            item.SubItems.Add($"{peer.Address}:{peer.Port}");
            item.SubItems.Add(PeerPositions.Describe(peer.Position));
            item.SubItems.Add(string.Empty);
            _peersList.Items.Add(item);
            if (peer == selected)
                item.Selected = true;
        }
        _peersList.EndUpdate();
        _suppressItemCheck = false;
        StretchLastColumn(_peersList);
        _layoutPanel?.Reload();
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
            string status;
            Color color;
            if (!peer.Enabled) { status = "Disabled"; color = Ui.Grey; }
            else if (connection == null) { status = "Not connected"; color = Ui.Muted; }
            else if (connection.RoundTripMs >= 0) { status = $"Connected · {connection.RoundTripMs} ms"; color = Ui.Green; }
            else { status = "Connected"; color = Ui.Green; }

            if (item.SubItems[3].Text != status)
            {
                item.UseItemStyleForSubItems = false;
                item.SubItems[3].Text = status;
                item.SubItems[3].ForeColor = color;
                item.ForeColor = peer.Enabled ? Ui.Text : Ui.Grey;
                foreach (ListViewItem.ListViewSubItem sub in item.SubItems)
                    if (sub != item.SubItems[3])
                        sub.ForeColor = item.ForeColor;
            }
            var position = PeerPositions.Describe(peer.Position);
            if (item.SubItems[2].Text != position)
                item.SubItems[2].Text = position;
            if (item.Checked != peer.Enabled)
            {
                _suppressItemCheck = true;
                item.Checked = peer.Enabled;
                _suppressItemCheck = false;
            }
        }
    }

    private void UpdatePeerButtons()
    {
        var peer = SelectedPeer;
        var has = peer != null;
        _editButton.Enabled = has;
        _removeButton.Enabled = has;
        _enableButton.Enabled = has;
        _enableButton.Text = peer is { Enabled: false } ? "Enable" : "Disable";
    }

    private void OnPeerItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_suppressItemCheck)
            return;
        if (_peersList.Items[e.Index].Tag is PeerConfig peer)
            _ = SetPeerEnabledAsync(peer, e.NewValue == CheckState.Checked);
    }

    private void OnToggleEnabledClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
            _ = SetPeerEnabledAsync(peer, !peer.Enabled);
    }

    private async Task SetPeerEnabledAsync(PeerConfig peer, bool enabled)
    {
        try
        {
            await _service.SetPeerEnabledAsync(peer, enabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not {(enabled ? "enable" : "disable")} {peer.Name}: {ex.Message}", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        if (!IsDisposed)
        {
            RefreshPeerStatus();
            UpdatePeerButtons();
        }
    }

    private sealed record HighlightChoice(EdgeHighlightStyle Style, string Text)
    {
        public override string ToString() => Text;
    }

    // ------------------------------------------------------------------ load / save

    private void LoadSettings()
    {
        _machineNameTextBox.Text = _settings.MachineName;
        _portNumeric.Value = _settings.LocalPort;
        _discoveryPortNumeric.Value = _settings.DiscoveryPort;
        _pairingCodeTextBox.Text = _settings.PairingCode;
        _clipboardEnabledCheck.Checked = _settings.Clipboard.Enabled;
        _shareFilesCheck.Checked = _settings.Clipboard.SyncFiles;
        _highlightCombo.SelectedIndex = Math.Max(0, _highlightCombo.Items.Cast<HighlightChoice>().ToList().FindIndex(c => c.Style == _settings.EdgeHighlight));
#if DEBUG
        _debugPanelCheck.Checked = _settings.DebugPanelEnabled;
#endif
        _startWithWindowsCheck.Checked = _settings.StartWithWindows;
        _startMinimizedCheck.Checked = _settings.StartMinimized;
        _hotkeyTextBox.Text = _settings.ToggleHotkey ?? "";

        RefreshStatus();
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
        _settings.EdgeHighlight = (_highlightCombo.SelectedItem as HighlightChoice)?.Style ?? EdgeHighlightStyle.Fade;
#if DEBUG
        _settings.DebugPanelEnabled = _debugPanelCheck.Checked;
#endif
        _settings.StartWithWindows = _startWithWindowsCheck.Checked;
        _settings.StartMinimized = _startMinimizedCheck.Checked;
        _settings.ToggleHotkey = string.IsNullOrWhiteSpace(_hotkeyTextBox.Text) ? null : _hotkeyTextBox.Text;
        _layoutPanel.SaveLayout();

        _settings.Save();
        _service.ApplyClipboardSetting();
        _service.ApplyHotkeySetting();

        _ = StartupRegistration.ApplyAsync(_settings.StartWithWindows);

        Close();
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
            var wasEnabled = peer.Enabled;
            using var dialog = new PeerSetupForm(peer, _service, _settings);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _settings.Save();
                if (wasEnabled != peer.Enabled)
                {
                    // The dialog wrote the flag directly; put it back and go through the service so the
                    // connection follows the new state.
                    var wanted = peer.Enabled;
                    peer.Enabled = wasEnabled;
                    _ = SetPeerEnabledAsync(peer, wanted);
                }
                RefreshPeersList();
            }
        }
    }

    private async void OnRemovePeerClick(object? sender, EventArgs e)
    {
        if (SelectedPeer is PeerConfig peer)
        {
            if (MessageBox.Show(this, $"Remove {peer.Name}?", "RoboMouse", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
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
