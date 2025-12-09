using RoboMouse.Core;
using RoboMouse.Core.Configuration;

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
    private CheckBox _clipboardEnabledCheck = null!;
    private CheckBox _startWithWindowsCheck = null!;
    private CheckBox _startMinimizedCheck = null!;
    private TextBox _hotkeyTextBox = null!;
    private ListBox _peersListBox = null!;

    public SettingsForm(AppSettings settings, RoboMouseService service)
    {
        _settings = settings;
        _service = service;

        InitializeComponent();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        Text = "RoboMouse Settings";
        Size = new Size(500, 500);
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
            RowCount = 6
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
        layout.Controls.Add(new Label { Text = "Toggle Hotkey:", AutoSize = true }, 0, row);
        _hotkeyTextBox = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_hotkeyTextBox, 1, row++);

        // Clipboard sync
        layout.Controls.Add(new Label { Text = "Clipboard:", AutoSize = true }, 0, row);
        _clipboardEnabledCheck = new CheckBox { Text = "Enable clipboard sharing", AutoSize = true };
        layout.Controls.Add(_clipboardEnabledCheck, 1, row++);

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
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

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
        layout.Controls.Add(infoLabel, 0, row);
        layout.SetColumnSpan(infoLabel, 2);

        tab.Controls.Add(layout);
        return tab;
    }

    private TabPage CreatePeersTab()
    {
        var tab = new TabPage("Configured Peers");
        tab.Padding = new Padding(10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        _peersListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            DisplayMember = "DisplayText"
        };
        layout.Controls.Add(_peersListBox, 0, 0);
        layout.SetRowSpan(_peersListBox, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown
        };

        var addButton = new Button { Text = "Add...", Width = 80 };
        addButton.Click += OnAddPeerClick;
        buttonPanel.Controls.Add(addButton);

        var editButton = new Button { Text = "Edit...", Width = 80 };
        editButton.Click += OnEditPeerClick;
        buttonPanel.Controls.Add(editButton);

        var removeButton = new Button { Text = "Remove", Width = 80 };
        removeButton.Click += OnRemovePeerClick;
        buttonPanel.Controls.Add(removeButton);

        layout.Controls.Add(buttonPanel, 1, 0);

        tab.Controls.Add(layout);
        return tab;
    }

    private void LoadSettings()
    {
        _machineNameTextBox.Text = _settings.MachineName;
        _portNumeric.Value = _settings.LocalPort;
        _discoveryPortNumeric.Value = _settings.DiscoveryPort;
        _clipboardEnabledCheck.Checked = _settings.Clipboard.Enabled;
        _startWithWindowsCheck.Checked = _settings.StartWithWindows;
        _startMinimizedCheck.Checked = _settings.StartMinimized;
        _hotkeyTextBox.Text = _settings.ToggleHotkey ?? "";

        RefreshPeersList();
    }

    private void RefreshPeersList()
    {
        _peersListBox.Items.Clear();
        foreach (var peer in _settings.Peers)
        {
            _peersListBox.Items.Add(new PeerListItem(peer));
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        _settings.MachineName = _machineNameTextBox.Text;
        _settings.LocalPort = (int)_portNumeric.Value;
        _settings.DiscoveryPort = (int)_discoveryPortNumeric.Value;
        _settings.Clipboard.Enabled = _clipboardEnabledCheck.Checked;
        _settings.StartWithWindows = _startWithWindowsCheck.Checked;
        _settings.StartMinimized = _startMinimizedCheck.Checked;
        _settings.ToggleHotkey = string.IsNullOrWhiteSpace(_hotkeyTextBox.Text) ? null : _hotkeyTextBox.Text;

        _settings.Save();

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
        using var dialog = new PeerSetupForm(null);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.PeerConfig != null)
        {
            _settings.Peers.Add(dialog.PeerConfig);
            RefreshPeersList();
        }
    }

    private void OnEditPeerClick(object? sender, EventArgs e)
    {
        if (_peersListBox.SelectedItem is PeerListItem item)
        {
            using var dialog = new PeerSetupForm(item.Peer);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                RefreshPeersList();
            }
        }
    }

    private void OnRemovePeerClick(object? sender, EventArgs e)
    {
        if (_peersListBox.SelectedItem is PeerListItem item)
        {
            _settings.Peers.Remove(item.Peer);
            RefreshPeersList();
        }
    }

    private class PeerListItem
    {
        public PeerConfig Peer { get; }
        public string DisplayText => $"{Peer.Name} ({Peer.Address}) - {Peer.Position}";

        public PeerListItem(PeerConfig peer)
        {
            Peer = peer;
        }

        public override string ToString() => DisplayText;
    }
}
