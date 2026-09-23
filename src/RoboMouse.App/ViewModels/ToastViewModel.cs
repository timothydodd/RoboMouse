using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using RoboMouse.App.Services;

namespace RoboMouse.App.ViewModels;

/// <summary>One toast: what it says and its buttons. Any button, or the close button, closes it.</summary>
public sealed partial class ToastViewModel
{
    public AppNotification Notification { get; }
    public string Title => Notification.Title;
    public string Message => Notification.Message;
    public IReadOnlyList<ToastActionViewModel> Actions { get; }
    public bool HasActions => Actions.Count > 0;

    public Symbol Icon => Notification.Kind switch
    {
        NotificationKind.Warning => Symbol.Warning,
        NotificationKind.Error => Symbol.ErrorCircle,
        NotificationKind.Request => Symbol.PersonQuestionMark,
        NotificationKind.Update => Symbol.ArrowDownload,
        _ => Symbol.Info
    };

    public StatusTone Tone => Notification.Kind switch
    {
        NotificationKind.Warning => StatusTone.Warning,
        NotificationKind.Error => StatusTone.Error,
        _ => StatusTone.Accent
    };

    /// <summary>Raised when the toast should close (a button was clicked, or it was dismissed).</summary>
    public event EventHandler? CloseRequested;

    public ToastViewModel(AppNotification notification)
    {
        Notification = notification;
        Actions = notification.Actions.Select(a => new ToastActionViewModel(a, this)).ToList();
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal void Run(NotificationAction action)
    {
        // Close first: the action may open a window that should end up on top.
        Close();
        try
        {
            action.Invoke();
        }
        catch (Exception ex)
        {
            Core.Logging.SimpleLogger.Log("Notify", $"{action.Label} failed: {ex.Message}");
        }
    }
}

/// <summary>A button on a toast.</summary>
public sealed partial class ToastActionViewModel
{
    private readonly ToastViewModel _owner;

    public NotificationAction Action { get; }
    public string Label => Action.Label;
    public bool IsPrimary => Action.Primary;

    public ToastActionViewModel(NotificationAction action, ToastViewModel owner)
    {
        Action = action;
        _owner = owner;
    }

    [RelayCommand]
    private void Run() => _owner.Run(Action);
}
