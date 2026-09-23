using Avalonia.Controls;
using RoboMouse.App.ViewModels;

namespace RoboMouse.App.Views;

public partial class LayoutPageView : UserControl
{
    public LayoutPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LayoutPageViewModel vm)
            {
                vm.ReloadRequested = Canvas.Reload;
            }
        };
    }
}
