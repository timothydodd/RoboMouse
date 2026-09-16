using Avalonia.Controls;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

/// <summary>Add/edit peer dialog. Read <see cref="PeerSetupViewModel.Result"/> after it closes.</summary>
public partial class PeerSetupWindow : Window
{
    public PeerSetupViewModel ViewModel { get; }

    public PeerSetupWindow(PeerSetupViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += (_, _) => Close();
    }
}
