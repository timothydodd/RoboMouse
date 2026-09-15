using Avalonia.Controls;
using Avalonia.Input;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

public partial class PeersPageView : UserControl
{
    public PeersPageView()
    {
        InitializeComponent();
    }

    private void OnPeerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PeersPageViewModel vm && vm.EditPeerCommand.CanExecute(null))
            vm.EditPeerCommand.Execute(null);
    }

    private void OnDiscoveredDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PeersPageViewModel vm && vm.AddDiscoveredCommand.CanExecute(null))
            vm.AddDiscoveredCommand.Execute(null);
    }
}
