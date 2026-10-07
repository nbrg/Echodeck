using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Echodeck.App.ViewModels;

namespace Echodeck.App;

/// <summary>
/// Deliberately thin: all behaviour lives in <see cref="MainViewModel"/> and the services.
/// Only input gestures on the clip list (double-click, F2, Delete, Enter) are mapped here.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        StateChanged += (_, _) => viewModel.SetMeterActive(WindowState != WindowState.Minimized);
        ClipList.MouseDoubleClick += OnClipDoubleClick;
        ClipList.KeyDown += OnClipListKeyDown;
    }

    /// <summary>Brings the window to the front (used when a second copy of Echodeck is started).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Toggling Topmost is the reliable way to get past Windows' focus-stealing prevention.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the row buttons and on empty space.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ClipList, source) is ListViewItem)
            _viewModel.EditClipCommand.Execute(null);
    }

    private void OnClipListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2: _viewModel.RenameClipCommand.Execute(null); break;
            case Key.Delete: _viewModel.DeleteClipCommand.Execute(null); break;
            case Key.Enter: _viewModel.PlayClipToDiscordCommand.Execute(null); break;
            case Key.Space: _viewModel.PreviewClipCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
