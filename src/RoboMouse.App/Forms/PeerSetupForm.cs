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
    private Button _testButton = null!;
    private Label _testResultLabel = null!;
    private CheckBox _enabledCheck = null!;

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
        Ui.Style(this);
        Text = PeerConfig == null ? "Add peer" : "Edit peer";
        Icon = Ui.AppIcon();
        ClientSize = new Size(460, 440);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.Pad) };

        var grid = Ui.FormGrid(110);
        grid.Dock = DockStyle.Top;

        _nameTextBox = Ui.TextBox();
        Ui.Row(grid, "Name", _nameTextBox);

        var addressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _addressTextBox = Ui.TextBox();
        addressRow.Controls.Add(_addressTextBox, 0, 0);
        _testButton = Ui.Button("Test");
        _testButton.Enabled = _service != null;
        _testButton.Margin = new Padding(Ui.Gap, 3, 0, 3);
        _testButton.Click += OnTestClick;
        addressRow.Controls.Add(_testButton, 1, 0);
        Ui.Row(grid, "Address", addressRow);

        _portNumeric = Ui.Number(1024, 65535);
        _portNumeric.Value = 24800;
        Ui.Row(grid, "Port", _portNumeric);

        _testResultLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Font = Ui.Small,
            ForeColor = Ui.Muted,
            Margin = new Padding(0, 2, 0, Ui.Gap),
            Text = _service != null ? "Click Test to check the machine answers on this address and port." : ""
        };
        Ui.Row(grid, "", _testResultLabel);

        _positionCombo = Ui.Combo(170);
        _positionCombo.Items.AddRange(new object[]
        {
            new PositionItem(ScreenPosition.Left, "Left of my screen"),
            new PositionItem(ScreenPosition.Right, "Right of my screen"),
            new PositionItem(ScreenPosition.Top, "Above my screen"),
            new PositionItem(ScreenPosition.Bottom, "Below my screen")
        });
        _positionCombo.SelectedIndex = 1; // Default to Right
        Ui.Row(grid, "Position", _positionCombo);

        var offsets = Ui.Inline();
        _offsetXNumeric = Ui.Number(-10000, 10000, 90);
        _offsetYNumeric = Ui.Number(-10000, 10000, 90);
        offsets.Controls.Add(Ui.Label("X"));
        offsets.Controls.Add(_offsetXNumeric);
        var yLabel = Ui.Label("Y");
        yLabel.Margin = new Padding(Ui.Gap + 4, 5, Ui.Gap, 0);
        offsets.Controls.Add(yLabel);
        offsets.Controls.Add(_offsetYNumeric);
        Ui.Row(grid, "Offset", offsets);
        Ui.Row(grid, "", Ui.Hint("Pixels to shift the other screen along the shared edge. Easier to set by dragging in Screen layout.", 300));

        _enabledCheck = Ui.Check("Enabled");
        Ui.Row(grid, "", _enabledCheck);
        Ui.Row(grid, "", Ui.Hint("A disabled peer keeps its settings but is never connected to and cannot take control of this screen.", 300));

        body.Controls.Add(grid);

        var cancelButton = Ui.Button("Cancel", 100);
        cancelButton.DialogResult = DialogResult.Cancel;
        var okButton = Ui.PrimaryButton(PeerConfig == null ? "Add" : "Save", 100);
        okButton.Click += OnOkClick;

        Controls.Add(body);
        Controls.Add(Ui.ActionBar(okButton, cancelButton));
        body.BringToFront();
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
        _enabledCheck.Checked = peer.Enabled;

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
            _testResultLabel.ForeColor = Ui.Orange;
            _testResultLabel.Text = "Enter an address first.";
            return;
        }

        _testButton.Enabled = false;
        _testResultLabel.ForeColor = Ui.Muted;
        _testResultLabel.Text = $"Testing {address}:{(int)_portNumeric.Value}...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _service.TestConnectionAsync(address, (int)_portNumeric.Value, cts.Token);

            if (result.Success)
            {
                _testResultLabel.ForeColor = Ui.Green;
                _testResultLabel.Text = $"OK: {result.PeerName} ({result.PeerScreenWidth}x{result.PeerScreenHeight}), round trip {result.RoundTripMs} ms";

                if (string.IsNullOrWhiteSpace(_nameTextBox.Text) && !string.IsNullOrEmpty(result.PeerName))
                    _nameTextBox.Text = result.PeerName;
            }
            else
            {
                _testResultLabel.ForeColor = Ui.Red;
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
            MessageBox.Show(this, "Please enter a name.", "RoboMouse",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_addressTextBox.Text))
        {
            MessageBox.Show(this, "Please enter an address.", "RoboMouse",
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
        PeerConfig.Enabled = _enabledCheck.Checked;

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
