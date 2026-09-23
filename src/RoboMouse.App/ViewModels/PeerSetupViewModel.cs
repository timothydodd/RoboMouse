using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// Add/edit peer dialog. <see cref="Result"/> is the config to keep (the same instance when editing),
/// set only when the user confirms. A config that is not in the settings yet (a machine picked from
/// discovery) is a new peer: the dialog says "Add".
/// </summary>
public sealed partial class PeerSetupViewModel : ValidatingObservableObject
{
    public sealed record PositionChoice(ScreenPosition Position, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly PeerConfig? _existing;
    private readonly bool _isNew;
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
    [ObservableProperty] private decimal? _port = 24800;
    [ObservableProperty] private PositionChoice _selectedPosition;
    [ObservableProperty] private decimal? _offsetX = 0;
    [ObservableProperty] private decimal? _offsetY = 0;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _shareClipboard = true;

    /// <summary>The chord that jumps straight to this peer's screen; empty for none.</summary>
    [ObservableProperty] private string _jumpHotkey = string.Empty;

    // What the jump box started with; left unchanged, a peer on its default keeps following its place in the list.
    private readonly string _initialJumpHotkey;

    /// <summary>The peer's pinned identity fingerprint, or that it has not paired yet.</summary>
    public string IdentityText { get; }

    // A cleared number box is flagged on the field rather than silently keeping the old number.
    partial void OnPortChanged(decimal? value) => RequireValue(value, nameof(Port), "Enter a port number.");
    partial void OnOffsetXChanged(decimal? value) => RequireValue(value, nameof(OffsetX), "Enter an offset (0 for none).");
    partial void OnOffsetYChanged(decimal? value) => RequireValue(value, nameof(OffsetY), "Enter an offset (0 for none).");


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

        _isNew = peer == null || settings?.Peers.Contains(peer) == false;
        Title = _isNew ? "Add peer" : "Edit peer";
        ConfirmText = _isNew ? "Add" : "Save";
        var defaultEdge = settings != null ? PeerActions.FirstFreeEdge(settings) ?? ScreenPosition.Right : ScreenPosition.Right;
        _selectedPosition = Positions.First(p => p.Position == defaultEdge);
        _testResult = backend != null ? "Test checks that the machine answers on this address and port." : string.Empty;

        if (peer != null)
        {
            _name = peer.Name;
            _address = peer.Address;
            _port = peer.Port;
            _offsetX = peer.OffsetX;
            _offsetY = peer.OffsetY;
            _isEnabled = peer.Enabled;
            _shareClipboard = peer.ShareClipboard;
            _selectedPosition = Positions.FirstOrDefault(p => p.Position == peer.Position) ?? Positions[1];
        }

        _jumpHotkey = _initialJumpHotkey = settings != null
            ? HotkeySet.EffectiveJumpHotkey(settings, peer ?? new PeerConfig()) ?? string.Empty
            : peer?.JumpHotkey ?? string.Empty;
        IdentityText = peer == null ? "Not paired yet" : PeerItemViewModel.DescribeIdentity(peer);
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

        if (Port is not { } portValue)
        {
            TestTone = StatusTone.Warning;
            TestResult = "Enter a port first.";
            return;
        }
        var port = (int)portValue;
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
        if (Port is not { } port || OffsetX is not { } offsetX || OffsetY is not { } offsetY)
        {
            await _dialogs.WarnAsync("Some number fields are empty. Fill in the highlighted fields.");
            return;
        }

        var address = Address.Trim();
        if (FindDuplicate(address, (int)port) is { } duplicate)
        {
            await _dialogs.WarnAsync(duplicate);
            return;
        }

        var jumpHotkey = JumpHotkey.Trim();
        if (FindHotkeyConflict(jumpHotkey) is { } conflict)
        {
            await _dialogs.WarnAsync(conflict);
            return;
        }

        var position = SelectedPosition.Position;

        // Another configured peer on the same edge would never be reachable; offer a swap. A new
        // peer has no edge of its own to swap with, so the other one moves to a free edge.
        var occupant = _settings?.Peers.FirstOrDefault(p => p != _existing && p.Position == position);
        if (occupant != null)
        {
            var current = _isNew || _existing!.Position == position ? PeerActions.FirstFreeEdge(_settings!) : _existing.Position;
            DialogResult answer;
            if (current is { } target && target != position)
            {
                answer = await _dialogs.ShowMessageAsync(
                    $"{occupant.Name} is already {PeerPositions.Describe(position).ToLower()} of this screen.\n\n" +
                    $"Swap them so {occupant.Name} moves {PeerPositions.Describe(target).ToLower()}?",
                    "Edge already in use", DialogButtons.YesNoCancel, DialogIcon.Question);
                if (answer == DialogResult.Yes)
                    occupant.Position = target;
            }
            else
            {
                answer = await _dialogs.ShowMessageAsync(
                    $"{occupant.Name} is already {PeerPositions.Describe(position).ToLower()} of this screen and there is no free edge to move it to, " +
                    "so only one of them can be reached there.\n\nKeep both on this edge?",
                    "Edge already in use", DialogButtons.YesNo, DialogIcon.Question);
                if (answer == DialogResult.No)
                    answer = DialogResult.Cancel;
            }
            if (answer == DialogResult.Cancel)
                return;
        }

        var config = _existing ?? new PeerConfig();
        config.Name = Name.Trim();
        config.Address = address;
        config.Port = (int)port;
        config.Position = position;
        config.OffsetX = (int)offsetX;
        config.OffsetY = (int)offsetY;
        config.Enabled = IsEnabled;
        config.ShareClipboard = ShareClipboard;
        // Untouched, a peer on its default keeps following its place in the list.
        if (!(config.JumpHotkey == null && jumpHotkey == _initialJumpHotkey))
            config.JumpHotkey = jumpHotkey;

        Result = config;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Why the jump hotkey cannot be used (another hotkey already has that chord), or null.</summary>
    private string? FindHotkeyConflict(string jumpHotkey)
    {
        if (_settings == null || Hotkey.Parse(jumpHotkey) == null)
            return null;
        var hotkeys = new List<(string, string?)>
        {
            ("The toggle hotkey", _settings.ToggleHotkey),
            ("Lock the cursor", _settings.LockCursorHotkey),
            ("Lock all PCs", _settings.LockAllHotkey)
        };
        var peers = _settings.Peers.ToList();
        for (var i = 0; i < peers.Count; i++)
        {
            if (peers[i] != _existing)
                hotkeys.Add(($"Jump to {peers[i].Name}", HotkeySet.EffectiveJumpHotkey(peers[i], i)));
        }
        hotkeys.Add(("this PC's jump hotkey", jumpHotkey));
        return HotkeySet.FindConflict(hotkeys);
    }

    /// <summary>
    /// Why this peer would duplicate another configured one (same address and port, or the same
    /// machine), or null. Two configs for one machine fight over one connection.
    /// </summary>
    private string? FindDuplicate(string address, int port)
    {
        if (_settings == null)
            return null;

        if (_existing != null && _existing.Id == _settings.MachineId)
            return "That is this PC. Add the other computer instead.";

        foreach (var other in _settings.Peers)
        {
            if (other == _existing)
                continue;
            if (other.Port == port && string.Equals(other.Address.Trim(), address, StringComparison.OrdinalIgnoreCase))
                return $"{other.Name} already uses {address}:{port}. Edit that peer instead of adding it twice.";
            if (_existing != null && other.Id == _existing.Id)
                return $"This machine is already set up as {other.Name}. Edit that peer instead of adding it twice.";
        }
        return null;
    }

    [RelayCommand]
    private void Cancel()

    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
