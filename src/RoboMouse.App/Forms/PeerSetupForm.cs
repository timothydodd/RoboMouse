using System.ComponentModel;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Forms;

/// <summary>
/// Form for adding or editing a peer configuration.
/// </summary>
public partial class PeerSetupForm : Form
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PeerConfig? PeerConfig { get; private set; }

    private readonly RoboMouseService? _service;
    private readonly AppSettings? _settings;

    private TextBox _nameTextBox = null!;
    private TextBox _addressTextBox = null!;
    private NumericUpDown _portNumeric = null!;
    private ComboBox _positionCombo = null!;
    private NumericUpDown _offsetXNumeric = null!;
    private NumericUpDown _offsetYNumeric = null!;
    private TrackBar _speedTrack = null!;
    private Label _speedLabel = null!;
    private Button _testButton = null!;
    private Label _testResultLabel = null!;

    public PeerSetupForm(PeerConfig? existingPeer, RoboMouseService? service = null, AppSettings? settings = null)
    {
        PeerConfig = existingPeer;
        _service = service;
        _settings = settings;
        InitializeComponent();

        if (existingPeer != null)
        {
            LoadPeer(existingPeer);
        }
    }

    private void InitializeComponent()
    {
        Text = PeerConfig == null ? "Add Peer" : "Edit Peer";
        Size = new Size(420, 430);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 10
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

        // Name
        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true }, 0, row);
        _nameTextBox = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_nameTextBox, 1, row++);

        // Address + Test
        layout.Controls.Add(new Label { Text = "Address:", AutoSize = true }, 0, row);
        var addressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _addressTextBox = new TextBox { Dock = DockStyle.Fill };
        addressPanel.Controls.Add(_addressTextBox, 0, 0);
        _testButton = new Button { Text = "Test", Width = 60, Enabled = _service != null };
        _testButton.Click += OnTestClick;
        addressPanel.Controls.Add(_testButton, 1, 0);
        layout.Controls.Add(addressPanel, 1, row++);

        // Port
        layout.Controls.Add(new Label { Text = "Port:", AutoSize = true }, 0, row);
        _portNumeric = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Value = 24800,
            Width = 100
        };
        layout.Controls.Add(_portNumeric, 1, row++);

        // Test result
        layout.Controls.Add(new Label(), 0, row);
        _testResultLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = 36,
            ForeColor = Color.Gray,
            Text = _service != null ? "Click Test to check the machine answers on this address and port." : ""
        };
        layout.Controls.Add(_testResultLabel, 1, row++);

        // Position
        layout.Controls.Add(new Label { Text = "Position:", AutoSize = true }, 0, row);
        _positionCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150
        };
        _positionCombo.Items.AddRange(new object[]
        {
            new PositionItem(ScreenPosition.Left, "Left of my screen"),
            new PositionItem(ScreenPosition.Right, "Right of my screen"),
            new PositionItem(ScreenPosition.Top, "Above my screen"),
            new PositionItem(ScreenPosition.Bottom, "Below my screen")
        });
        _positionCombo.SelectedIndex = 1; // Default to Right
        layout.Controls.Add(_positionCombo, 1, row++);

        // Offset X
        layout.Controls.Add(new Label { Text = "Offset X:", AutoSize = true }, 0, row);
        _offsetXNumeric = new NumericUpDown
        {
            Minimum = -10000,
            Maximum = 10000,
            Value = 0,
            Width = 100
        };
        layout.Controls.Add(_offsetXNumeric, 1, row++);

        // Offset Y
        layout.Controls.Add(new Label { Text = "Offset Y:", AutoSize = true }, 0, row);
        _offsetYNumeric = new NumericUpDown
        {
            Minimum = -10000,
            Maximum = 10000,
            Value = 0,
            Width = 100
        };
        layout.Controls.Add(_offsetYNumeric, 1, row++);

        // Speed
        layout.Controls.Add(new Label { Text = "Mouse speed:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) }, 0, row);
        var speedPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        speedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        speedPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        _speedTrack = new TrackBar
        {
            Minimum = 25,     // 0.25x
            Maximum = 400,    // 4.0x
            Value = 100,
            TickFrequency = 25,
            SmallChange = 5,
            LargeChange = 25,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 32
        };
        _speedLabel = new Label { Text = "1.00x", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        _speedTrack.ValueChanged += (s, e) => _speedLabel.Text = $"{_speedTrack.Value / 100.0:0.00}x";
        speedPanel.Controls.Add(_speedTrack, 0, 0);
        speedPanel.Controls.Add(_speedLabel, 1, 0);
        layout.Controls.Add(speedPanel, 1, row++);

        layout.Controls.Add(new Label(), 0, row);
        layout.Controls.Add(new Label
        {
            Text = "1.00x sends motion unchanged; the other machine's own pointer speed applies. Double-click the slider to reset.",
            AutoSize = true,
            ForeColor = Color.Gray,
            MaximumSize = new Size(280, 0)
        }, 1, row++);
        _speedTrack.MouseDoubleClick += (s, e) => _speedTrack.Value = 100;

        // Buttons
        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill
        };

        var cancelButton = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = "OK", Width = 80 };
        okButton.Click += OnOkClick;

        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(okButton);
        layout.Controls.Add(buttonPanel, 1, row++);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private void LoadPeer(PeerConfig peer)
    {
        _nameTextBox.Text = peer.Name;
        _addressTextBox.Text = peer.Address;
        _portNumeric.Value = peer.Port;
        _offsetXNumeric.Value = peer.OffsetX;
        _offsetYNumeric.Value = peer.OffsetY;
        _speedTrack.Value = Math.Clamp((int)Math.Round(peer.SpeedMultiplier * 100), _speedTrack.Minimum, _speedTrack.Maximum);
        _speedLabel.Text = $"{_speedTrack.Value / 100.0:0.00}x";

        for (int i = 0; i < _positionCombo.Items.Count; i++)
        {
            if (_positionCombo.Items[i] is PositionItem item && item.Position == peer.Position)
            {
                _positionCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private async void OnTestClick(object? sender, EventArgs e)
    {
        if (_service == null)
            return;

        var address = _addressTextBox.Text.Trim();
        if (string.IsNullOrEmpty(address))
        {
            _testResultLabel.ForeColor = Color.DarkOrange;
            _testResultLabel.Text = "Enter an address first.";
            return;
        }

        _testButton.Enabled = false;
        _testResultLabel.ForeColor = Color.Gray;
        _testResultLabel.Text = $"Testing {address}:{(int)_portNumeric.Value}...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _service.TestConnectionAsync(address, (int)_portNumeric.Value, cts.Token);

            if (result.Success)
            {
                _testResultLabel.ForeColor = Color.Green;
                _testResultLabel.Text = $"OK: {result.PeerName} ({result.PeerScreenWidth}x{result.PeerScreenHeight}), round trip {result.RoundTripMs} ms";

                if (string.IsNullOrWhiteSpace(_nameTextBox.Text) && !string.IsNullOrEmpty(result.PeerName))
                    _nameTextBox.Text = result.PeerName;
            }
            else
            {
                _testResultLabel.ForeColor = Color.Firebrick;
                _testResultLabel.Text = result.Error ?? "Failed.";
            }
        }
        finally
        {
            _testButton.Enabled = true;
        }
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
        {
            MessageBox.Show("Please enter a name.", "Validation Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_addressTextBox.Text))
        {
            MessageBox.Show("Please enter an address.", "Validation Error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var position = (_positionCombo.SelectedItem as PositionItem)?.Position ?? ScreenPosition.Right;

        // Another configured peer on the same edge would never be reachable; offer a swap.
        var occupant = _settings?.Peers.FirstOrDefault(p => p != PeerConfig && p.Position == position);
        if (occupant != null)
        {
            var answer = MessageBox.Show(this,
                $"{occupant.Name} is already {PeerPositions.Describe(position).ToLower()} of this screen.\n\n" +
                $"Swap them so {occupant.Name} moves {PeerPositions.Describe(PeerConfig?.Position ?? ScreenPosition.Right).ToLower()}?",
                "Edge already in use", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
                occupant.Position = PeerConfig?.Position ?? ScreenPosition.Right;
        }

        if (PeerConfig == null)
        {
            PeerConfig = new PeerConfig();
        }

        PeerConfig.Name = _nameTextBox.Text.Trim();
        PeerConfig.Address = _addressTextBox.Text.Trim();
        PeerConfig.Port = (int)_portNumeric.Value;
        PeerConfig.Position = position;
        PeerConfig.OffsetX = (int)_offsetXNumeric.Value;
        PeerConfig.OffsetY = (int)_offsetYNumeric.Value;
        PeerConfig.SpeedMultiplier = _speedTrack.Value / 100.0;

        DialogResult = DialogResult.OK;
        Close();
    }

    private class PositionItem
    {
        public ScreenPosition Position { get; }
        public string DisplayText { get; }

        public PositionItem(ScreenPosition position, string displayText)
        {
            Position = position;
            DisplayText = displayText;
        }

        public override string ToString() => DisplayText;
    }
}
