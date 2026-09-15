using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Windows;

/// <summary>
/// Dialog for adding or editing a peer configuration. Closes with the resulting <see cref="PeerConfig"/>
/// (the same instance when editing), or null when cancelled.
/// </summary>
public sealed class PeerSetupWindow : Window
{
    public PeerConfig? PeerConfig { get; private set; }

    private readonly RoboMouseService? _service;
    private readonly AppSettings? _settings;

    private readonly TextBox _nameTextBox;
    private readonly TextBox _addressTextBox;
    private readonly NumericUpDown _portNumeric;
    private readonly ComboBox _positionCombo;
    private readonly NumericUpDown _offsetXNumeric;
    private readonly NumericUpDown _offsetYNumeric;
    private readonly Button _testButton;
    private readonly TextBlock _testResultLabel;
    private readonly CheckBox _enabledCheck;

    public PeerSetupWindow(PeerConfig? existingPeer, RoboMouseService? service = null, AppSettings? settings = null)
    {
        PeerConfig = existingPeer;
        _service = service;
        _settings = settings;

        Ui.Style(this);
        Title = PeerConfig == null ? "Add peer" : "Edit peer";
        Width = 480;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;

        var grid = Ui.FormGrid(110);

        _nameTextBox = Ui.TextBox();
        Ui.Row(grid, "Name", _nameTextBox);

        var addressRow = new Grid();
        addressRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        addressRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _addressTextBox = Ui.TextBox();
        addressRow.Children.Add(_addressTextBox);
        _testButton = Ui.Button("Test");
        _testButton.IsEnabled = _service != null;
        _testButton.Margin = new Thickness(Ui.Gap, 3, 0, 3);
        _testButton.Click += OnTestClick;
        Grid.SetColumn(_testButton, 1);
        addressRow.Children.Add(_testButton);
        Ui.Row(grid, "Address", addressRow);

        _portNumeric = Ui.Number(1024, 65535);
        _portNumeric.Value = 24800;
        Ui.Row(grid, "Port", _portNumeric);

        _testResultLabel = new TextBlock
        {
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Ui.SmallSize,
            Foreground = Ui.MutedBrush,
            Margin = new Thickness(0, 2, 0, Ui.Gap),
            Text = _service != null ? "Click Test to check the machine answers on this address and port." : ""
        };
        Ui.Row(grid, "", _testResultLabel);

        _positionCombo = Ui.Combo(170);
        _positionCombo.ItemsSource = new[]
        {
            new PositionItem(ScreenPosition.Left, "Left of my screen"),
            new PositionItem(ScreenPosition.Right, "Right of my screen"),
            new PositionItem(ScreenPosition.Top, "Above my screen"),
            new PositionItem(ScreenPosition.Bottom, "Below my screen")
        };
        _positionCombo.SelectedIndex = 1; // Default to Right
        Ui.Row(grid, "Position", _positionCombo);

        var offsets = Ui.Inline();
        _offsetXNumeric = Ui.Number(-10000, 10000, 100);
        _offsetYNumeric = Ui.Number(-10000, 10000, 100);
        offsets.Children.Add(Ui.Label("X"));
        offsets.Children.Add(_offsetXNumeric);
        var yLabel = Ui.Label("Y");
        yLabel.Margin = new Thickness(Ui.Gap + 4, 0, Ui.Gap, 0);
        offsets.Children.Add(yLabel);
        offsets.Children.Add(_offsetYNumeric);
        Ui.Row(grid, "Offset", offsets);
        Ui.Row(grid, "", Ui.Hint("Pixels to shift the other screen along the shared edge. Easier to set by dragging in Screen layout.", 300));

        _enabledCheck = Ui.Check("Enabled");
        _enabledCheck.IsChecked = true;
        Ui.Row(grid, "", _enabledCheck);
        Ui.Row(grid, "", Ui.Hint("A disabled peer keeps its settings but is never connected to and cannot take control of this screen.", 300));

        var cancelButton = Ui.Button("Cancel", 100);
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) => Close(null);
        var okButton = Ui.PrimaryButton(PeerConfig == null ? "Add" : "Save", 100);
        okButton.IsDefault = true;
        okButton.Click += OnOkClick;

        var body = new DockPanel { LastChildFill = true };
        body.Children.Add(Ui.ActionBar(okButton, cancelButton));
        body.Children.Add(new Border { Padding = new Thickness(Ui.Pad), Child = grid });
        Content = body;

        if (existingPeer != null)
            LoadPeer(existingPeer);
    }

    private void LoadPeer(PeerConfig peer)
    {
        _nameTextBox.Text = peer.Name;
        _addressTextBox.Text = peer.Address;
        _portNumeric.Value = peer.Port;
        _offsetXNumeric.Value = peer.OffsetX;
        _offsetYNumeric.Value = peer.OffsetY;
        _enabledCheck.IsChecked = peer.Enabled;

        var items = (PositionItem[])_positionCombo.ItemsSource!;
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].Position == peer.Position)
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

        var address = (_addressTextBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(address))
        {
            _testResultLabel.Foreground = new SolidColorBrush(Ui.Orange);
            _testResultLabel.Text = "Enter an address first.";
            return;
        }

        var port = (int)(_portNumeric.Value ?? 24800);
        _testButton.IsEnabled = false;
        _testResultLabel.Foreground = Ui.MutedBrush;
        _testResultLabel.Text = $"Testing {address}:{port}...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _service.TestConnectionAsync(address, port, cts.Token);

            if (result.Success)
            {
                _testResultLabel.Foreground = new SolidColorBrush(Ui.Green);
                _testResultLabel.Text = $"OK: {result.PeerName} ({result.PeerScreenWidth}x{result.PeerScreenHeight}), round trip {result.RoundTripMs} ms";

                if (string.IsNullOrWhiteSpace(_nameTextBox.Text) && !string.IsNullOrEmpty(result.PeerName))
                    _nameTextBox.Text = result.PeerName;
            }
            else
            {
                _testResultLabel.Foreground = new SolidColorBrush(Ui.Red);
                _testResultLabel.Text = result.Error ?? "Failed.";
            }
        }
        catch (Exception ex)
        {
            _testResultLabel.Foreground = new SolidColorBrush(Ui.Red);
            _testResultLabel.Text = ex.Message;
        }
        finally
        {
            _testButton.IsEnabled = true;
        }
    }

    private async void OnOkClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_nameTextBox.Text))
        {
            await Dialogs.WarnAsync(this, "Please enter a name.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_addressTextBox.Text))
        {
            await Dialogs.WarnAsync(this, "Please enter an address.");
            return;
        }

        var position = (_positionCombo.SelectedItem as PositionItem)?.Position ?? ScreenPosition.Right;

        // Another configured peer on the same edge would never be reachable; offer a swap.
        var occupant = _settings?.Peers.FirstOrDefault(p => p != PeerConfig && p.Position == position);
        if (occupant != null)
        {
            var answer = await Dialogs.ShowAsync(this,
                $"{occupant.Name} is already {PeerPositions.Describe(position).ToLower()} of this screen.\n\n" +
                $"Swap them so {occupant.Name} moves {PeerPositions.Describe(PeerConfig?.Position ?? ScreenPosition.Right).ToLower()}?",
                "Edge already in use", DialogButtons.YesNoCancel, DialogIcon.Question);

            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
                occupant.Position = PeerConfig?.Position ?? ScreenPosition.Right;
        }

        PeerConfig ??= new PeerConfig();

        PeerConfig.Name = _nameTextBox.Text.Trim();
        PeerConfig.Address = _addressTextBox.Text.Trim();
        PeerConfig.Port = (int)(_portNumeric.Value ?? 24800);
        PeerConfig.Position = position;
        PeerConfig.OffsetX = (int)(_offsetXNumeric.Value ?? 0);
        PeerConfig.OffsetY = (int)(_offsetYNumeric.Value ?? 0);
        PeerConfig.Enabled = _enabledCheck.IsChecked == true;

        Close(PeerConfig);
    }

    private sealed class PositionItem
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
