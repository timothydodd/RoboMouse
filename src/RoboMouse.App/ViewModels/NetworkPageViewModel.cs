using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.ViewModels;

/// <summary>Network page: pairing code, ports, firewall.</summary>
public sealed partial class NetworkPageViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IAppBackend? _backend;
    private readonly string _savedPairingCode;

    /// <summary>
    /// The code this PC will use once saved. Only ever generated here or taken from another PC
    /// (<see cref="UseEnteredCodeCommand"/>), never free text, so it always has the generated strength.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairingCodeWeak), nameof(PairingCodeChanged))]
    private string _pairingCode;

    [ObservableProperty] private decimal? _localPort;
    [ObservableProperty] private decimal? _discoveryPort;

    /// <summary>True while the "code from another PC" box is open.</summary>
    [ObservableProperty] private bool _isEnteringCode;
    [ObservableProperty] private string _enteredCode = string.Empty;
    [ObservableProperty] private string? _enteredCodeError;

    /// <summary>Why the listen or discovery port could not be opened, or null.</summary>
    [ObservableProperty] private string? _networkError;

    partial void OnLocalPortChanged(decimal? value) => RequireValue(value, nameof(LocalPort), "Enter a port number.");
    partial void OnDiscoveryPortChanged(decimal? value) => RequireValue(value, nameof(DiscoveryPort), "Enter a port number.");
    partial void OnEnteredCodeChanged(string value) => EnteredCodeError = null;

    public string MachineId { get; }

    /// <summary>A code typed by hand in an older version (not the generated format) is easy to guess.</summary>
    public bool IsPairingCodeWeak => !PairingCodeFormat.IsStrong(PairingCode);

    /// <summary>The code differs from the saved one: saving drops every connection made with the old code.</summary>
    public bool PairingCodeChanged => !string.Equals(PairingCode, _savedPairingCode, StringComparison.Ordinal);

    /// <summary>This computer's IPv4 addresses, one per connected adapter, for typing into another machine.</summary>
    public IReadOnlyList<string> Addresses { get; } = LocalAddresses.Describe();
    public bool HasAddresses => Addresses.Count > 0;

    public NetworkPageViewModel(AppSettings settings, IDialogService dialogs, IAppBackend? backend = null)
    {
        _dialogs = dialogs;
        _backend = backend;
        _pairingCode = _savedPairingCode = settings.PairingCode;
        _localPort = settings.LocalPort;
        _discoveryPort = settings.DiscoveryPort;
        MachineId = settings.MachineId;
        Refresh();
    }

    /// <summary>Picks up port errors from the service. Called by the window on a timer.</summary>
    public void Refresh()
    {
        var errors = new[] { _backend?.ListenerError?.Message, _backend?.DiscoveryError?.Message }.Where(m => m != null);
        var text = string.Join("\n", errors);
        NetworkError = text.Length == 0 ? null : text;
    }

    [RelayCommand]
    private Task CopyPairingCodeAsync() => _dialogs.CopyTextAsync(PairingCode);

    [RelayCommand]
    private async Task GeneratePairingCodeAsync()
    {
        if (await _dialogs.ConfirmAsync("Generate a new pairing code? When you save, every other machine is disconnected until it uses the new code too."))
            PairingCode = Core.Network.SecureChannel.GeneratePairingCode();
    }

    [RelayCommand]
    private void BeginEnterCode()
    {
        EnteredCode = string.Empty;
        EnteredCodeError = null;
        IsEnteringCode = true;
    }

    [RelayCommand]
    private void CancelEnterCode()
    {
        IsEnteringCode = false;
        EnteredCodeError = null;
    }

    /// <summary>Takes the code shown on another PC, so both use the same one. It must be a generated code.</summary>
    [RelayCommand]
    private void UseEnteredCode()
    {
        if (!PairingCodeFormat.TryNormalize(EnteredCode, out var code))
        {
            EnteredCodeError = PairingCodeFormat.FormatHint;
            return;
        }
        PairingCode = code;
        IsEnteringCode = false;
    }

    /// <summary>
    /// The elevated command behind "Add rules": replaces any previous RoboMouse rules with inbound
    /// rules for this program only, on private and domain networks. Any remote address is allowed:
    /// the button is there for peers the automatic setup cannot see, which are often on another
    /// subnet, and the pairing code still decides who may connect.
    /// </summary>
    internal static string BuildFirewallScript(string programPath, int tcpPort, int udpPort)
    {
        const string scope = "profile=private,domain";
        var program = $"program=\"{programPath}\"";
        return
            "netsh advfirewall firewall delete rule name=\"RoboMouse (TCP)\" & " +
            "netsh advfirewall firewall delete rule name=\"RoboMouse (UDP)\" & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (TCP)\" dir=in action=allow protocol=TCP localport={tcpPort} {program} {scope} & " +
            $"netsh advfirewall firewall add rule name=\"RoboMouse (UDP)\" dir=in action=allow protocol=UDP localport={udpPort} {program} {scope}";
    }

    [RelayCommand]
    private async Task AllowThroughFirewallAsync()
    {
        if (LocalPort is not { } localPort || DiscoveryPort is not { } discoveryPort || Environment.ProcessPath is not { } programPath)
        {
            await _dialogs.WarnAsync("Enter both port numbers first.");
            return;
        }
        var tcp = (int)localPort;
        var udp = (int)discoveryPort;
        var script = BuildFirewallScript(programPath, tcp, udp);

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
            using (process)
            {
                // ExitCode throws while the process is still running, so only read it once it has exited.
                var exited = process != null && await Task.Run(() => process.WaitForExit(15000));
                if (exited && process!.ExitCode == 0)
                    await _dialogs.InfoAsync($"Firewall rules added for TCP {tcp} and UDP {udp} (private and domain networks).");
                else if (process != null && !exited)
                    await _dialogs.WarnAsync("The firewall command is taking longer than expected. Check Windows Defender Firewall with Advanced Security for the RoboMouse rules.");
                else
                    await _dialogs.WarnAsync("The firewall command did not complete. You can add the rules manually in Windows Defender Firewall with Advanced Security.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the elevation prompt.
        }
        catch (Exception ex)
        {
            await _dialogs.ErrorAsync($"Could not update the firewall: {ex.Message}");
        }
    }
}

/// <summary>
/// The shape of a code from <see cref="Core.Network.SecureChannel.GeneratePairingCode"/>: 12 characters
/// from a 32-letter alphabet (60 bits), shown as XXXX-XXXX-XXXX.
/// </summary>
/// <remarks>
/// A local copy of the rule until the core exposes its own strength check (IsPairingCodeStrong);
/// switch to that once it lands so the two cannot drift apart.
/// </remarks>
internal static class PairingCodeFormat
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 12;

    public const string FormatHint = "That is not a RoboMouse code. It has 12 letters and digits, like K7PQ-M2XW-9DHR (no 0, 1, I or O). Copy it from Settings > Network on the other PC.";

    /// <summary>True for a code in the generated format (dashes, spaces and case ignored).</summary>
    public static bool IsStrong(string? code) => TryNormalize(code, out _);

    /// <summary>Accepts a generated code typed or pasted loosely and returns it as XXXX-XXXX-XXXX.</summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = string.Empty;
        if (input == null)
            return false;
        var chars = input.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray();
        if (chars.Length != Length || chars.Any(c => !Alphabet.Contains(c)))
            return false;
        var raw = new string(chars);
        code = $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
        return true;
    }
}
