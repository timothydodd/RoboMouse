using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

/// <summary>
/// One notification near the tray. It never takes focus from what the user is doing (no activation,
/// no taskbar button) but its buttons still work with the mouse. The same choices are always
/// reachable from Settings, for keyboard and screen-reader users.
/// </summary>
public partial class ToastWindow : Window
{
    private DispatcherTimer? _timer;
    private bool _stylesApplied;

    public ToastViewModel ViewModel { get; }

    public ToastWindow(ToastViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
        Opened += (_, _) =>
        {
            ApplyNoActivate();
            StartTimer();
        };
    }

    /// <summary>Starts (or restarts) the auto-close countdown, if this notification has one.</summary>
    private void StartTimer()
    {
        _timer?.Stop();
        if (ViewModel.Notification.Duration is not { } duration)
            return;
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
        _timer.Start();
    }

    // Reading a toast with the pointer on it must not have it vanish mid-sentence.
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _timer?.Stop();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        StartTimer();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        base.OnClosed(e);
    }

    /// <summary>Marks the native window as a tool window that is never activated.</summary>
    private void ApplyNoActivate()
    {
        if (_stylesApplied || !OperatingSystem.IsWindows())
            return;
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
            return;
        var style = GetWindowLongPtrW(handle, GWL_EXSTYLE);
        SetWindowLongPtrW(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        _stylesApplied = true;
    }

    #region Native

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOOLWINDOW = 0x80;

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    #endregion
}
