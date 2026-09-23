using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Views;

/// <summary>
/// <see cref="IDialogService"/> for view models hosted in a window: dialogs are modal to that window.
/// With no owner (tray menu actions) dialogs open standalone.
/// </summary>
public sealed class WindowDialogService : IDialogService
{
    private readonly IAppBackend? _backend;

    /// <summary>The window dialogs are modal to. Settable because a window's view model (which takes
    /// this service) is created before the window itself.</summary>
    public Window? Owner { get; set; }

    public WindowDialogService(Window? owner, IAppBackend? backend)
    {
        Owner = owner;
        _backend = backend;
    }

    public Task<DialogResult> ShowMessageAsync(string text, string title = "RoboMouse",
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.None)
    {
        var dialog = new MessageDialog(text, title, buttons, icon);
        return ShowAsync(dialog, () => dialog.Result);
    }

    public Task<PeerConfig?> ShowPeerSetupAsync(PeerConfig? peer, AppSettings settings)
    {
        // The dialog's own warnings (duplicate peer, edge in use) are modal to the dialog.
        var dialogs = new WindowDialogService(null, _backend);
        var dialog = new PeerSetupWindow(new PeerSetupViewModel(peer, settings, _backend, dialogs));
        dialogs.Owner = dialog;
        return ShowAsync(dialog, () => dialog.ViewModel.Result);
    }

    public async Task CopyTextAsync(string text)
    {
        var clipboard = Owner?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var storage = Owner?.StorageProvider;
        if (storage == null || !storage.CanSave)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(extension.ToUpperInvariant() + " file") { Patterns = new[] { "*." + extension } } }
        });
        return file?.TryGetLocalPath();
    }

    public void Open(string pathOrUrl)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RoboMouse.Core.Logging.SimpleLogger.Log("Shell", $"Could not open {pathOrUrl}: {ex.Message}");
        }
    }

    private async Task<T> ShowAsync<T>(Window dialog, Func<T> result)
    {
        if (Owner is { IsVisible: true } owner)
        {
            await dialog.ShowDialog(owner);

            return result();
        }

        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
        return result();
    }
}
