using CommunityToolkit.Mvvm.ComponentModel;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// General page: what most people set once. This PC's name, startup, the clipboard, moving between
/// screens and following the controlling PC. Fine-tuning lives on <see cref="AdvancedPageViewModel"/>.
/// </summary>
public sealed partial class GeneralPageViewModel : PageViewModel
{
    [ObservableProperty] private string _machineName;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _toggleHotkey;

    [ObservableProperty] private bool _shareClipboard;
    [ObservableProperty] private bool _syncText;
    [ObservableProperty] private bool _syncImages;
    [ObservableProperty] private bool _shareFiles;

    [ObservableProperty] private bool _wrapAround;
    [ObservableProperty] private bool _followHostPower;
    [ObservableProperty] private bool _lockWithHost;
    [ObservableProperty] private bool _screensaverWithHost;

    /// <summary>Under "Start with Windows": says when Windows itself has it turned off.</summary>
    [ObservableProperty] private string? _startupDescription;

    /// <summary>Shows what Windows reports for the startup entry.</summary>
    public void ShowStartupState(StartupState state) => StartupDescription = state switch
    {
        StartupState.DisabledByUser => "Turned off in the Startup apps list (Task Manager or Settings > Apps > Startup). Turn it back on there.",
        StartupState.DisabledByPolicy => "Blocked by a policy on this PC.",
        _ => null
    };

    /// <summary>Writes the clipboard switches into the settings (the size limit is on the Advanced page).</summary>
    internal void SaveClipboard(ClipboardSettings clipboard)
    {
        clipboard.Enabled = ShareClipboard;
        clipboard.SyncText = SyncText;
        clipboard.SyncImages = SyncImages;
        clipboard.SyncFiles = ShareFiles;
    }

    public GeneralPageViewModel(AppSettings settings)
    {
        _machineName = settings.MachineName;
        _startWithWindows = settings.StartWithWindows;
        _startMinimized = settings.StartMinimized;
        _toggleHotkey = settings.ToggleHotkey ?? string.Empty;
        _shareClipboard = settings.Clipboard.Enabled;
        _syncText = settings.Clipboard.SyncText;
        _syncImages = settings.Clipboard.SyncImages;
        _shareFiles = settings.Clipboard.SyncFiles;
        _wrapAround = settings.WrapAround;
        _followHostPower = settings.FollowHostPower;
        _lockWithHost = settings.LockWithHost;
        _screensaverWithHost = settings.ScreensaverWithHost;
    }
}
