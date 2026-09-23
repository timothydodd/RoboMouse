using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;

namespace RoboMouse.App.ViewModels;

/// <summary>General page: identity, startup, hotkey, clipboard, display.</summary>
public sealed partial class GeneralPageViewModel : PageViewModel
{
    public sealed record HighlightChoice(EdgeHighlightStyle Style, string Text)
    {
        public override string ToString() => Text;
    }

    public IReadOnlyList<HighlightChoice> HighlightChoices { get; } = new[]
    {
        new HighlightChoice(EdgeHighlightStyle.None, "Show nothing"),
        new HighlightChoice(EdgeHighlightStyle.Border, "Flash a border around the screen"),
        new HighlightChoice(EdgeHighlightStyle.Fade, "Glow along the edge it came in on")
    };

    [ObservableProperty] private string _machineName;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private string _toggleHotkey;
    [ObservableProperty] private bool _shareClipboard;
    [ObservableProperty] private bool _syncText;
    [ObservableProperty] private bool _syncImages;
    [ObservableProperty] private bool _shareFiles;

    /// <summary>Largest text or image sent through the clipboard, in MB. Files are not limited by it.</summary>
    [ObservableProperty] private decimal? _clipboardMaxMegabytes;

    partial void OnClipboardMaxMegabytesChanged(decimal? value) =>
        RequireValue(value, nameof(ClipboardMaxMegabytes), "Enter a size in MB.");

    /// <summary>Upper bound for <see cref="ClipboardMaxMegabytes"/>; the protocol carries one message per copy.</summary>
    public const int MaxClipboardMegabytes = 100;

    /// <summary>Writes the clipboard fields into the settings.</summary>
    internal void SaveClipboard(ClipboardSettings clipboard)
    {
        clipboard.Enabled = ShareClipboard;
        clipboard.SyncText = SyncText;
        clipboard.SyncImages = SyncImages;
        clipboard.SyncFiles = ShareFiles;
        if (ClipboardMaxMegabytes is { } mb)
            clipboard.MaxSizeBytes = (long)(Math.Clamp(mb, 1, MaxClipboardMegabytes) * 1024 * 1024);
    }
    [ObservableProperty] private HighlightChoice _selectedHighlight;
    [ObservableProperty] private bool _wrapAround;
    [ObservableProperty] private bool _wakeOnEdge;
    [ObservableProperty] private bool _followHostPower;
    [ObservableProperty] private bool _showDebugPanel;
    [ObservableProperty] private bool _useDesktopService;

    /// <summary>Under "Start with Windows": says when Windows itself has it turned off.</summary>
    [ObservableProperty] private string? _startupDescription;

    /// <summary>Shows what Windows reports for the startup entry.</summary>
    public void ShowStartupState(StartupState state) => StartupDescription = state switch
    {
        StartupState.DisabledByUser => "Turned off in the Startup apps list (Task Manager or Settings > Apps > Startup). Turn it back on there.",
        StartupState.DisabledByPolicy => "Blocked by a policy on this PC.",
        _ => null
    };

    /// <summary>The desktop service is a separate download; the card only appears once it is installed.</summary>
    public bool DesktopServiceInstalled { get; }
    public string DesktopServiceDescription { get; }

    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public GeneralPageViewModel(AppSettings settings, bool desktopServiceInstalled = false, DesktopServiceState desktopServiceState = DesktopServiceState.Off)
    {
        DesktopServiceInstalled = desktopServiceInstalled;
        _useDesktopService = settings.UseDesktopService;
        DesktopServiceDescription = "Lets the PC controlling this one click UAC prompts, sign in at the lock screen and use windows running as administrator. " + desktopServiceState switch
        {
            DesktopServiceState.Active => "Working now.",
            DesktopServiceState.Connecting => "Waiting for the RoboMouse desktop service to answer.",
            DesktopServiceState.Incompatible => "The installed desktop service is a different version from this app; update it.",
            _ => "Windows asks for permission once when you turn this on."
        };
        _machineName = settings.MachineName;
        _startWithWindows = settings.StartWithWindows;
        _startMinimized = settings.StartMinimized;
        _toggleHotkey = settings.ToggleHotkey ?? string.Empty;
        _shareClipboard = settings.Clipboard.Enabled;
        _syncText = settings.Clipboard.SyncText;
        _syncImages = settings.Clipboard.SyncImages;
        _shareFiles = settings.Clipboard.SyncFiles;
        _clipboardMaxMegabytes = Math.Max(1, Math.Round(settings.Clipboard.MaxSizeBytes / (1024m * 1024m), 1));
        _selectedHighlight = HighlightChoices.FirstOrDefault(c => c.Style == settings.EdgeHighlight) ?? HighlightChoices[2];
        _wrapAround = settings.WrapAround;
        _wakeOnEdge = settings.WakeOnEdge;
        _followHostPower = settings.FollowHostPower;
        _showDebugPanel = settings.DebugPanelEnabled;
    }
}
