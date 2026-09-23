using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Network;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// First-run pairing: (1) this PC's name, addresses and pairing code, with the option to take the
/// other PC's code instead; (2) pick the other PC (found on the network, asking to connect, or typed
/// by address); (3) put it on an edge. Finishing adds it through the same flow as the Peers page.
/// </summary>
public sealed partial class PairingWizardViewModel : ObservableObject
{
    public const int StepCount = 3;

    private readonly AppSettings _settings;
    private readonly IAppBackend _backend;
    private readonly IDialogService _dialogs;

    public string MachineName => _settings.MachineName;
    public IReadOnlyList<string> Addresses { get; } = LocalAddresses.Describe();
    public bool HasAddresses => Addresses.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep), nameof(IsFindStep), nameof(IsPlaceStep), nameof(StepText), nameof(NextText), nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private int _step;

    public bool IsFirstStep => Step == 0;
    public bool IsFindStep => Step == 1;
    public bool IsPlaceStep => Step == 2;
    public bool CanGoBack => Step > 0;
    public string StepText => $"Step {Step + 1} of {StepCount}";
    public string NextText => IsPlaceStep ? "Finish" : "Next";

    // Step 1: the code.
    [ObservableProperty] private string _pairingCode;
    [ObservableProperty] private string _enteredCode = string.Empty;
    [ObservableProperty] private string? _enteredCodeError;

    partial void OnEnteredCodeChanged(string value) => EnteredCodeError = null;

    // Step 2: the other PC.
    public ObservableCollection<WizardMachineViewModel> Machines { get; } = new();
    public bool HasMachines => Machines.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private WizardMachineViewModel? _selectedMachine;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string _manualAddress = string.Empty;

    [ObservableProperty] private decimal? _manualPort = 24800;

    partial void OnSelectedMachineChanged(WizardMachineViewModel? value)
    {
        if (value != null)
            ManualAddress = string.Empty;
    }

    partial void OnManualAddressChanged(string value)
    {
        if (value.Length > 0)
            SelectedMachine = null;
    }

    // Step 3: where it sits.
    public IReadOnlyList<PeerSetupViewModel.PositionChoice> Positions { get; } = new[]
    {
        new PeerSetupViewModel.PositionChoice(ScreenPosition.Left, "Left of this screen"),
        new PeerSetupViewModel.PositionChoice(ScreenPosition.Right, "Right of this screen"),
        new PeerSetupViewModel.PositionChoice(ScreenPosition.Top, "Above this screen"),
        new PeerSetupViewModel.PositionChoice(ScreenPosition.Bottom, "Below this screen")
    };

    [ObservableProperty] private PeerSetupViewModel.PositionChoice _selectedPosition;

    /// <summary>The name of the machine picked in step 2, for the step 3 wording.</summary>
    public string OtherName => SelectedMachine?.Name ?? (ManualAddress.Trim().Length > 0 ? ManualAddress.Trim() : "the other PC");

    [ObservableProperty] private bool _isBusy;

    /// <summary>The peer added by Finish, or null when the wizard was closed without one.</summary>
    public PeerConfig? Result { get; private set; }

    /// <summary>Raised when the wizard should close.</summary>
    public event EventHandler? CloseRequested;

    public PairingWizardViewModel(AppSettings settings, IAppBackend backend, IDialogService dialogs)
    {
        _settings = settings;
        _backend = backend;
        _dialogs = dialogs;
        _pairingCode = settings.PairingCode;
        var edge = PeerActions.FirstFreeEdge(settings) ?? ScreenPosition.Right;
        _selectedPosition = Positions.First(p => p.Position == edge);
        Machines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMachines));
        Refresh();
    }

    /// <summary>Updates the list of machines: requests to connect first, then those found on the network. Called on a timer.</summary>
    public void Refresh()
    {
        var configured = _settings.Peers.Select(p => p.Id).ToHashSet();
        var list = new List<WizardMachineViewModel>();
        foreach (var pending in _backend.PendingPeers.Where(p => !configured.Contains(p.MachineId)))
            list.Add(WizardMachineViewModel.From(pending));
        foreach (var found in _backend.DiscoveredPeers.OrderBy(p => p.MachineName))
        {
            if (found.MachineId == _settings.MachineId || configured.Contains(found.MachineId) || list.Any(m => m.MachineId == found.MachineId))
                continue;
            list.Add(WizardMachineViewModel.From(found));
        }

        if (Machines.Select(m => (m.MachineId, m.IsRequest)).SequenceEqual(list.Select(m => (m.MachineId, m.IsRequest))))
            return;
        var selectedId = SelectedMachine?.MachineId;
        Machines.Clear();
        foreach (var machine in list)
            Machines.Add(machine);
        SelectedMachine = Machines.FirstOrDefault(m => m.MachineId == selectedId);
    }

    [RelayCommand]
    private Task CopyPairingCodeAsync() => _dialogs.CopyTextAsync(PairingCode);

    /// <summary>Takes the code the other PC shows, so both use one. Saved and applied straight away so step 2 can connect.</summary>
    [RelayCommand]
    private void UseEnteredCode()
    {
        if (!Core.Network.PairingCode.TryFormat(EnteredCode, out var code))
        {
            EnteredCodeError = NetworkPageViewModel.CodeFormatHint;
            return;
        }
        EnteredCode = string.Empty;
        PairingCode = code;
        if (_settings.PairingCode == code)
            return;
        _settings.PairingCode = code;
        _backend.SaveSettings();
        _backend.ApplyPairingCode();
    }

    private bool CanGoNext => !IsBusy && (Step != 1 || SelectedMachine != null || ManualAddress.Trim().Length > 0);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (Step < StepCount - 1)
        {
            Step++;
            OnPropertyChanged(nameof(OtherName));
            return;
        }
        await FinishAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (Step > 0)
            Step--;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async Task FinishAsync()
    {
        var position = SelectedPosition.Position;
        IsBusy = true;
        NextCommand.NotifyCanExecuteChanged();
        try
        {
            if (SelectedMachine is { IsRequest: true } request)
            {
                // It already connected to us with the right code; allowing it is all that is left.
                Result = _backend.AllowPendingPeer(request.MachineId, position);
                if (Result == null)
                {
                    await _dialogs.WarnAsync($"{request.Name} stopped asking to connect. Wait for it to ask again, or pick it from the machines found on the network.");
                    Refresh();
                    return;
                }
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            PeerConfig config;
            if (SelectedMachine?.Found is { } found)
            {
                config = PeerActions.FromDiscovered(found, position);
            }
            else
            {
                var address = ManualAddress.Trim();
                if (ManualPort is not { } port)
                {
                    await _dialogs.WarnAsync("Enter the other PC's port (24800 unless it was changed there).");
                    return;
                }
                if (_settings.Peers.Any(p => p.Port == (int)port && string.Equals(p.Address, address, StringComparison.OrdinalIgnoreCase)))
                {
                    await _dialogs.WarnAsync($"{address} is already set up. Close this window and see Settings > Peers.");
                    return;
                }
                // Added by address: recognised by that address until it has connected once and its id is known.
                config = new PeerConfig { Name = address, Address = address, Port = (int)port, Position = position, HasConnected = false };
            }

            var error = await PeerActions.AddAndConnectAsync(_settings, _backend, config);
            Result = config;
            if (error != null)
            {
                await _dialogs.WarnAsync(
                    $"Added {config.Name}, but it has not connected yet: {error}\n\n" +
                    "If the other PC shows that this one wants to connect, click Allow there. RoboMouse keeps trying in the background.");
            }
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
            NextCommand.NotifyCanExecuteChanged();
        }
    }
}

/// <summary>A machine the wizard can pair with: one asking to connect, or one found by discovery.</summary>
public sealed class WizardMachineViewModel
{
    public required string MachineId { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }

    /// <summary>It asked to connect to this PC (it already has the code); finishing allows it.</summary>
    public bool IsRequest { get; init; }
    public DiscoveredPeer? Found { get; init; }

    public static WizardMachineViewModel From(PendingPeer pending) => new()
    {
        MachineId = pending.MachineId,
        Name = pending.MachineName,
        Detail = $"{pending.Address} · asking to connect to this PC",
        IsRequest = true
    };

    public static WizardMachineViewModel From(DiscoveredPeer found) => new()
    {
        MachineId = found.MachineId,
        Name = found.MachineName,
        Detail = $"{found.Address} · {found.ScreenWidth} × {found.ScreenHeight}",
        Found = found
    };
}
