using System.Windows;
using Echodeck.App.ViewModels;

namespace Echodeck.App;

/// <summary>Deliberately thin: all behaviour lives in <see cref="MainViewModel"/> and the services.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        StateChanged += (_, _) => viewModel.SetMeterActive(WindowState != WindowState.Minimized);
    }
}
