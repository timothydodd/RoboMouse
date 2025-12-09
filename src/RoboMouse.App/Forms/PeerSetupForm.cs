using System.ComponentModel;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Forms;

/// <summary>
/// Form for adding or editing a peer configuration.
/// </summary>
public partial class PeerSetupForm : Form
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PeerConfig? PeerConfig { get; private set; }

    private TextBox _nameTextBox = null!;
    private TextBox _addressTextBox = null!;
    private NumericUpDown _portNumeric = null!;
    private ComboBox _positionCombo = null!;
    private NumericUpDown _offsetXNumeric = null!;
    private NumericUpDown _offsetYNumeric = null!;

    public PeerSetupForm(PeerConfig? existingPeer)
    {
        PeerConfig = existingPeer;
        InitializeComponent();

        if (existingPeer != null)
        {
            LoadPeer(existingPeer);
        }
    }

    private void InitializeComponent()
    {
        Text = PeerConfig == null ? "Add Peer" : "Edit Peer";
        Size = new Size(400, 300);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

        // Name
        layout.Controls.Add(new Label { Text = "Name:", AutoSize = true }, 0, row);
        _nameTextBox = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_nameTextBox, 1, row++);

        // Address
        layout.Controls.Add(new Label { Text = "Address:", AutoSize = true }, 0, row);
        _addressTextBox = new TextBox { Dock = DockStyle.Fill };
        layout.Controls.Add(_addressTextBox, 1, row++);

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

        for (int i = 0; i < _positionCombo.Items.Count; i++)
        {
            if (_positionCombo.Items[i] is PositionItem item && item.Position == peer.Position)
            {
                _positionCombo.SelectedIndex = i;
                break;
            }
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
