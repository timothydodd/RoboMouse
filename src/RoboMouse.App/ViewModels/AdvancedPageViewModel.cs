using CommunityToolkit.Mvvm.ComponentModel;
using RoboMouse.Core.Configuration;
using RoboMouse.Core.Input;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// Advanced page: settings most people never change. When a crossing happens (the guards), the extra
/// hotkeys, the clipboard size limit, waking sleeping PCs, the secure desktop, and display cues.
/// </summary>
public sealed partial class AdvancedPageViewModel : PageViewModel
{
    public sealed record HighlightChoice(EdgeHighlightStyle Style, string Text)
    {
        public override string ToString() => Text;
    }

    public sealed record ModifierChoice(CrossingModifier Modifier, string Text)
    {
        public override string ToString() => Text;
    }

    public IReadOnlyList<ModifierChoice> ModifierChoices { get; } = new[]
    {
        new ModifierChoice(CrossingModifier.None, "No key needed"),
        new ModifierChoice(CrossingModifier.Ctrl, "Ctrl"),
        new ModifierChoice(CrossingModifier.Alt, "Alt"),
        new ModifierChoice(CrossingModifier.Shift, "Shift"),
        new ModifierChoice(CrossingModifier.Win, "Windows key")
    };

    /// <summary>Upper bounds for the crossing number boxes.</summary>
    public const int MaxCornerDeadZone = 500;
    public const int MaxCrossingDelayMs = 2000;
    public const int MaxPushDistance = 500;

    public IReadOnlyList<HighlightChoice> HighlightChoices { get; } = new[]
    {
        new HighlightChoice(EdgeHighlightStyle.None, "Show nothing"),
        new HighlightChoice(EdgeHighlightStyle.Border, "Flash a border around the screen"),
        new HighlightChoice(EdgeHighlightStyle.Fade, "Glow along the edge it came in on")
    };

    [ObservableProperty] private string _lockCursorHotkey;
    [ObservableProperty] private string _lockAllHotkey;

    // Switching screens (crossing guards).
    [ObservableProperty] private bool _blockWhileButtonHeld;
    [ObservableProperty] private decimal? _cornerDeadZone;
    [ObservableProperty] private decimal? _pushDistance;
    [ObservableProperty] private bool _doubleTap;
    [ObservableProperty] private decimal? _crossingDelayMs;
    [ObservableProperty] private ModifierChoice _selectedCrossingModifier;
    [ObservableProperty] private bool _blockWhileFullScreen;

    partial void OnCornerDeadZoneChanged(decimal? value) =>
        RequireValue(value, nameof(CornerDeadZone), "Enter a size in pixels (0 for none).");
    partial void OnPushDistanceChanged(decimal? value) =>
        RequireValue(value, nameof(PushDistance), "Enter a distance in pixels (0 for none).");
    partial void OnCrossingDelayMsChanged(decimal? value) =>
        RequireValue(value, nameof(CrossingDelayMs), "Enter a delay in milliseconds (0 for none).");

    /// <summary>Writes the switching-screens fields into the settings.</summary>
    internal void SaveCrossing(CrossingSettings crossing)
    {
        crossing.BlockWhileButtonHeld = BlockWhileButtonHeld;
        if (CornerDeadZone is { } zone)
            crossing.CornerDeadZone = (int)Math.Clamp(zone, 0, MaxCornerDeadZone);
        if (PushDistance is { } push)
            crossing.PushDistance = (int)Math.Clamp(push, 0, MaxPushDistance);
        crossing.DoubleTap = DoubleTap;
        if (CrossingDelayMs is { } delay)
            crossing.DelayMs = (int)Math.Clamp(delay, 0, MaxCrossingDelayMs);
        crossing.RequiredModifier = SelectedCrossingModifier.Modifier;
        crossing.BlockWhileFullScreen = BlockWhileFullScreen;
    }

    /// <summary>Largest text or image sent through the clipboard, in MB. Files are not limited by it.</summary>
    [ObservableProperty] private decimal? _clipboardMaxMegabytes;

    partial void OnClipboardMaxMegabytesChanged(decimal? value) =>
        RequireValue(value, nameof(ClipboardMaxMegabytes), "Enter a size in MB.");

    /// <summary>Upper bound for <see cref="ClipboardMaxMegabytes"/>; the protocol carries one message per copy.</summary>
    public const int MaxClipboardMegabytes = 100;

    /// <summary>Writes the clipboard size limit into the settings.</summary>
    internal void SaveClipboard(ClipboardSettings clipboard)
    {
        if (ClipboardMaxMegabytes is { } mb)
            clipboard.MaxSizeBytes = (long)(Math.Clamp(mb, 1, MaxClipboardMegabytes) * 1024 * 1024);
    }

    [ObservableProperty] private bool _wakeOnEdge;
    [ObservableProperty] private HighlightChoice _selectedHighlight;
    [ObservableProperty] private bool _showDebugPanel;
    [ObservableProperty] private bool _useDesktopService;

    /// <summary>The desktop service is a separate download; its card only appears once it is installed.</summary>
    public bool DesktopServiceInstalled { get; }
    public string DesktopServiceDescription { get; }

    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public AdvancedPageViewModel(AppSettings settings, bool desktopServiceInstalled = false, DesktopServiceState desktopServiceState = DesktopServiceState.Off)
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
        _lockCursorHotkey = settings.LockCursorHotkey ?? string.Empty;
        _lockAllHotkey = settings.LockAllHotkey ?? string.Empty;
        var crossing = settings.Crossing;
        _blockWhileButtonHeld = crossing.BlockWhileButtonHeld;
        _cornerDeadZone = crossing.CornerDeadZone;
        _pushDistance = crossing.PushDistance;
        _doubleTap = crossing.DoubleTap;
        _crossingDelayMs = crossing.DelayMs;
        _selectedCrossingModifier = ModifierChoices.FirstOrDefault(c => c.Modifier == crossing.RequiredModifier) ?? ModifierChoices[0];
        _blockWhileFullScreen = crossing.BlockWhileFullScreen;
        _clipboardMaxMegabytes = Math.Max(1, Math.Round(settings.Clipboard.MaxSizeBytes / (1024m * 1024m), 1));
        _wakeOnEdge = settings.WakeOnEdge;
        _selectedHighlight = HighlightChoices.FirstOrDefault(c => c.Style == settings.EdgeHighlight) ?? HighlightChoices[2];
        _showDebugPanel = settings.DebugPanelEnabled;
    }
}
