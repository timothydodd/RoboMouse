using Avalonia.Controls;
using Avalonia.Threading;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

/// <summary>The first-run pairing wizard. Refreshes the list of machines every second while open.</summary>
public partial class PairingWizardWindow : Window
{
    public PairingWizardViewModel ViewModel { get; }

    public PairingWizardWindow(PairingWizardViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => viewModel.Refresh();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }
}
