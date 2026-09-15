using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RoboMouse.App.Services;
using RoboMouse.App.ViewModels;
using RoboMouse.Core.Configuration;

namespace RoboMouse.App.Views;

/// <summary>
/// Settings window: navigation pane on the left, one page per area, Save/Cancel in the footer.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _refreshTimer;

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(AppSettings settings, IAppBackend backend)
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        ViewModel = new SettingsViewModel(settings, backend, new WindowDialogService(this, backend), version);
        DataContext = ViewModel;
        InitializeComponent();

        ViewModel.CloseRequested += (_, _) => Close();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => ViewModel.Refresh();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
