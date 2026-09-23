using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Services;

public enum DialogResult { None, OK, Cancel, Yes, No }

public enum DialogButtons { OK, YesNo, YesNoCancel }

public enum DialogIcon { None, Information, Question, Warning, Error }

/// <summary>
/// UI interactions a view model needs but must not perform itself: message boxes, the peer dialog, the
/// clipboard, file pickers and opening folders or links. Implemented by the window that hosts the view model.
/// </summary>
public interface IDialogService
{
    Task<DialogResult> ShowMessageAsync(string text, string title = "RoboMouse",
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.None);

    /// <summary>Opens the add/edit peer dialog. Returns the resulting config, or null if cancelled.</summary>
    Task<PeerConfig?> ShowPeerSetupAsync(PeerConfig? peer, AppSettings settings);

    Task CopyTextAsync(string text);

    /// <summary>Asks where to save a file. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension);

    /// <summary>Opens a folder in Explorer or a web page in the browser.</summary>
    void Open(string pathOrUrl);
}

public static class DialogServiceExtensions
{
    public static Task InfoAsync(this IDialogService dialogs, string text) => dialogs.ShowMessageAsync(text, icon: DialogIcon.Information);
    public static Task WarnAsync(this IDialogService dialogs, string text) => dialogs.ShowMessageAsync(text, icon: DialogIcon.Warning);
    public static Task ErrorAsync(this IDialogService dialogs, string text) => dialogs.ShowMessageAsync(text, icon: DialogIcon.Error);

    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string text, string title = "RoboMouse") =>
        await dialogs.ShowMessageAsync(text, title, DialogButtons.YesNo, DialogIcon.Question) == DialogResult.Yes;
}
