using Avalonia;
using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

/// <summary>
/// Shows notifications as <see cref="ToastWindow"/>s stacked up from the bottom-right corner of the
/// main display's working area (above the taskbar, next to the tray), newest at the bottom. At most
/// <see cref="MaxVisible"/> are up at once; the oldest makes way. Call on the UI thread.
/// </summary>
public sealed class ToastPresenter : INotifier
{
    public const int MaxVisible = 4;
    private const int Gap = 8;
    private const int EdgeMargin = 12;

    private readonly List<ToastWindow> _open = new();

    public void Show(AppNotification notification)
    {
        Dismiss(notification.Key);
        while (_open.Count >= MaxVisible)
        {
            var oldest = _open[0];
            _open.RemoveAt(0);
            oldest.Close();
        }

        var window = new ToastWindow(new ToastViewModel(notification));
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Dispatcher.UIThread.Post(Relayout);
        };
        window.Opened += (_, _) => Dispatcher.UIThread.Post(Relayout);
        // Start off-screen: the height is only known once it is laid out.
        window.Position = new PixelPoint(-32000, -32000);
        _open.Add(window);
        window.Show();
    }

    public void Dismiss(string key)
    {
        foreach (var window in _open.Where(w => w.ViewModel.Notification.Key == key).ToList())
            window.Close();
    }

    /// <summary>Closes every toast (at exit).</summary>
    public void CloseAll()
    {
        foreach (var window in _open.ToList())
            window.Close();
    }

    private void Relayout()
    {
        if (_open.Count == 0)
            return;
        var screen = _open[^1].Screens.Primary;
        if (screen == null)
            return;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;

        var bottom = area.Bottom - (int)(EdgeMargin * scaling);
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var window = _open[i];
            var width = (int)Math.Ceiling(window.Width * scaling);
            var height = (int)Math.Ceiling((window.ClientSize.Height > 0 ? window.ClientSize.Height : 120) * scaling);
            bottom -= height;
            window.Position = new PixelPoint(area.Right - width - (int)(EdgeMargin * scaling), bottom);
            bottom -= (int)(Gap * scaling);
        }
    }
}
