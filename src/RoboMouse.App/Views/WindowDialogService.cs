using Avalonia.Controls;
using Avalonia.Input.Platform;
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
    private readonly Window? _owner;
    private readonly IAppBackend? _backend;

    public WindowDialogService(Window? owner, IAppBackend? backend)
    {
        _owner = owner;
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
        var dialog = new PeerSetupWindow(new PeerSetupViewModel(peer, settings, _backend, new WindowDialogService(null, _backend)));
        return ShowAsync(dialog, () => dialog.ViewModel.Result);
    }

    public async Task CopyTextAsync(string text)
    {
        var clipboard = _owner?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    private async Task<T> ShowAsync<T>(Window dialog, Func<T> result)
    {
        if (_owner is { IsVisible: true })
        {
            await dialog.ShowDialog(_owner);
            return result();
        }

        var closed = new TaskCompletionSource();
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        await closed.Task;
        return result();
    }
}
