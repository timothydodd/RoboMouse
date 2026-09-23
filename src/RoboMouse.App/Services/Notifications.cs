using RoboMouse.Core;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Services;

/// <summary>What a notification is about; picks its icon and colour.</summary>
public enum NotificationKind { Info, Warning, Error, Request, Update }

/// <summary>A button on a notification. Clicking it runs <see cref="Invoke"/> and closes the notification.</summary>
public sealed record NotificationAction(string Label, Action Invoke, bool Primary = false);

/// <summary>
/// A toast near the tray. <see cref="Key"/> identifies what it is about: showing another with the
/// same key replaces it, and <see cref="INotifier.Dismiss"/> takes it down. A null
/// <see cref="Duration"/> keeps it up until it is answered or closed.
/// </summary>
public sealed record AppNotification(
    string Key,
    NotificationKind Kind,
    string Title,
    string Message,
    IReadOnlyList<NotificationAction> Actions,
    TimeSpan? Duration);

/// <summary>Shows notifications. The tray implements it with small windows (the tray icon itself has no balloons).</summary>
public interface INotifier
{
    void Show(AppNotification notification);
    void Dismiss(string key);
}

/// <summary>The notifications the app shows, in one place so their wording and behaviour can be tested.</summary>
public static class Notifications
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(15);

    public static string PendingKey(string machineId) => "pending:" + machineId;

    /// <summary>A machine with the pairing code asks to connect. Stays up until answered.</summary>
    public static AppNotification PendingPeer(PendingPeer peer, Action allow, Action ignore) => new(
        PendingKey(peer.MachineId),
        NotificationKind.Request,
        $"{peer.MachineName} wants to connect",
        $"{(peer.Address.Length > 0 ? peer.Address : "A machine")} has this PC's pairing code. Allow it to share the mouse and keyboard with this PC? You can place it next to this screen afterwards.",
        new[] { new NotificationAction("Allow", allow, Primary: true), new NotificationAction("Ignore", ignore) },
        Duration: null);

    /// <summary>The listen or discovery port could not be opened.</summary>
    public static AppNotification NetworkError(NetworkStartError error, Action openNetworkSettings) => new(
        "network:" + error.Kind,
        NotificationKind.Error,
        error.InUse ? $"Port {error.Port} is in use" : "Network problem",
        error.Kind == NetworkErrorKind.ListenPort
            ? $"{error.Message} Until then other machines cannot connect to this PC."
            : $"{error.Message} Until then other machines are not found automatically.",
        new[] { new NotificationAction("Network settings", openNetworkSettings, Primary: true) },
        Duration: null);

    /// <summary>Something failed inside the service; it keeps running.</summary>
    public static AppNotification ServiceError(Exception error, Action openLogFolder) => new(
        "error",
        NotificationKind.Error,
        "Something went wrong",
        $"{error.GetBaseException().Message}\nRoboMouse is still running; the details are in its log.",
        new[] { new NotificationAction("Open log folder", openLogFolder) },
        Long);

    /// <summary>A wake packet went out, from the edge or the tray menu.</summary>
    public static AppNotification WakeSent(PeerConfig peer) => new(
        "wake:" + peer.Id,
        NotificationKind.Info,
        $"Waking {peer.Name}",
        "Sent a Wake-on-LAN packet. It connects by itself once it is up.",
        Array.Empty<NotificationAction>(),
        Short);

    /// <summary>The settings file could not be read at startup; null when it was fine.</summary>
    public static AppNotification? SettingsLoad(SettingsLoadNotice notice, string? corruptFilePath, Action openFolder)
    {
        var kept = corruptFilePath != null ? $" The unreadable file was kept as {Path.GetFileName(corruptFilePath)}." : string.Empty;
        return notice switch
        {
            SettingsLoadNotice.RestoredFromBackup => new AppNotification("settings-load", NotificationKind.Warning,
                "Settings restored from backup",
                "The settings file could not be read, so the copy from the save before it was used. Recent changes may be missing." + kept,
                new[] { new NotificationAction("Open folder", openFolder) }, Duration: null),
            SettingsLoadNotice.Reset => new AppNotification("settings-load", NotificationKind.Error,
                "Settings were reset",
                "The settings file and its backup could not be read, so RoboMouse started with the defaults. Peers and the pairing code need setting up again." + kept,
                new[] { new NotificationAction("Open folder", openFolder) }, Duration: null),
            _ => null
        };
    }

    /// <summary>A newer release is out (direct-download builds only).</summary>
    public static AppNotification UpdateAvailable(UpdateInfo update, Version current, Action download) => new(
        "update",
        NotificationKind.Update,
        $"RoboMouse {update.Version.ToString(3)} is available",
        $"You have {current.ToString(3)}. Download the new version from its release page.",
        new[] { new NotificationAction("Download", download, Primary: true) },
        Duration: null);
}

/// <summary>
/// Stops one kind of notification from repeating too often: a fault that recurs on every message
/// must not bury the screen in toasts. The log still gets every occurrence.
/// </summary>
public sealed class NotificationThrottle
{
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, DateTime> _last = new();

    public NotificationThrottle(TimeSpan interval) => _interval = interval;

    /// <summary>True (and remembered) when <paramref name="key"/> was not shown within the interval.</summary>
    public bool ShouldShow(string key, DateTime now)
    {
        if (_last.TryGetValue(key, out var last) && now - last < _interval && now >= last)
            return false;
        _last[key] = now;
        return true;
    }
}
