using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// Add/edit peer dialog. <see cref="Result"/> is the config to keep (the same instance when editing),
/// set only when the user confirms.
/// </summary>
public sealed partial class PeerSetupViewModel : ObservableObject
{
    public sealed record PositionChoice(ScreenPosition Position, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly PeerConfig? _existing;
    private readonly AppSettings? _settings;
    private readonly IAppBackend? _backend;
    private readonly IDialogService _dialogs;

    public string Title { get; }
    public string ConfirmText { get; }
    public bool CanTest => _backend != null;

    public IReadOnlyList<PositionChoice> Positions { get; } = new[]
    {
        new PositionChoice(ScreenPosition.Left, "Left of my screen"),
        new PositionChoice(ScreenPosition.Right, "Right of my screen"),
        new PositionChoice(ScreenPosition.Top, "Above my screen"),
        new PositionChoice(ScreenPosition.Bottom, "Below my screen")
    };

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private decimal _port = 24800;
    [ObservableProperty] private PositionChoice _selectedPosition;
    [ObservableProperty] private decimal _offsetX;
    [ObservableProperty] private decimal _offsetY;
    [ObservableProperty] private bool _isEnabled = true;

    [ObservableProperty] private string _testResult;
    [ObservableProperty] private StatusTone _testTone = StatusTone.Idle;
    [ObservableProperty] private bool _isTesting;

    public PeerConfig? Result { get; private set; }

    /// <summary>Raised when the dialog should close (after confirm or cancel).</summary>
    public event EventHandler? CloseRequested;

    public PeerSetupViewModel(PeerConfig? peer, AppSettings? settings, IAppBackend? backend, IDialogService dialogs)
    {
        _existing = peer;
        _settings = settings;
        _backend = backend;
        _dialogs = dialogs;

        Title = peer == null ? "Add peer" : "Edit peer";
        ConfirmText = peer == null ? "Add" : "Save";
        _selectedPosition = Positions[1];
        _testResult = backend != null ? "Test checks that the machine answers on this address and port." : string.Empty;

        if (peer != null)
        {
            _name = peer.Name;
            _address = peer.Address;
            _port = peer.Port;
            _offsetX = peer.OffsetX;
            _offsetY = peer.OffsetY;
            _isEnabled = peer.Enabled;
            _selectedPosition = Positions.FirstOrDefault(p => p.Position == peer.Position) ?? Positions[1];
        }
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (_backend == null)
            return;

        var address = Address.Trim();
        if (address.Length == 0)
        {
            TestTone = StatusTone.Warning;
            TestResult = "Enter an address first.";
            return;
        }

        var port = (int)Port;
        IsTesting = true;
        TestTone = StatusTone.Idle;
        TestResult = $"Testing {address}:{port}...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await _backend.TestConnectionAsync(address, port, cts.Token);
            if (result.Success)
            {
                TestTone = StatusTone.Ok;
                TestResult = $"OK: {result.PeerName} ({result.PeerScreenWidth}×{result.PeerScreenHeight}), round trip {result.RoundTripMs} ms";
                if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrEmpty(result.PeerName))
                    Name = result.PeerName;
            }
            else
            {
                TestTone = StatusTone.Error;
                TestResult = result.Error ?? "Failed.";
            }
        }
        catch (Exception ex)
        {
            TestTone = StatusTone.Error;
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            await _dialogs.WarnAsync("Please enter a name.");
            return;
        }
        if (string.IsNullOrWhiteSpace(Address))
        {
            await _dialogs.WarnAsync("Please enter an address.");
            return;
        }

        var position = SelectedPosition.Position;

        // Another configured peer on the same edge would never be reachable; offer a swap.
        var occupant = _settings?.Peers.FirstOrDefault(p => p != _existing && p.Position == position);
        if (occupant != null)
        {
            var current = _existing?.Position ?? ScreenPosition.Right;
            var answer = await _dialogs.ShowMessageAsync(
                $"{occupant.Name} is already {PeerPositions.Describe(position).ToLower()} of this screen.\n\n" +
                $"Swap them so {occupant.Name} moves {PeerPositions.Describe(current).ToLower()}?",
                "Edge already in use", DialogButtons.YesNoCancel, DialogIcon.Question);
            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
                occupant.Position = current;
        }

        var config = _existing ?? new PeerConfig();
        config.Name = Name.Trim();
        config.Address = Address.Trim();
        config.Port = (int)Port;
        config.Position = position;
        config.OffsetX = (int)OffsetX;
        config.OffsetY = (int)OffsetY;
        config.Enabled = IsEnabled;

        Result = config;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
