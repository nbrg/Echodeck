using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Echodeck.App.ViewModels;
using Echodeck.Core.Settings;

namespace Echodeck.App;

/// <summary>
/// Deliberately thin: behaviour lives in the view models and services. This class only handles
/// window concerns (tray minimise/close, bringing itself to the front) and key/mouse gestures on
/// the clip list.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;

    public MainWindow(MainViewModel viewModel, SettingsService settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        StateChanged += OnStateChanged;
        IsVisibleChanged += (_, _) => UpdateMeterActivity();
        SoundboardList.MouseDoubleClick += OnClipDoubleClick;
        SoundboardList.KeyDown += OnClipListKeyDown;
    }

    /// <summary>Set by the app right before a real exit, so Close isn't turned into "hide to tray".</summary>
    public bool IsExiting { get; set; }

    /// <summary>Raised when the window hides itself into the tray.</summary>
    public event EventHandler? HiddenToTray;

    /// <summary>Shows the window (from the tray, or when a second copy of Echodeck is launched) and focuses it.</summary>
    public void BringToFront(string? tab = null)
    {
        _viewModel.ShowTab(tab);
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Toggling Topmost is the reliable way past Windows' focus-stealing prevention.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        UpdateMeterActivity();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsExiting && _settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    private void UpdateMeterActivity() =>
        _viewModel.SetMeterActive(IsVisible && WindowState != WindowState.Minimized);

    private void OnClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the row buttons and on empty space.
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(SoundboardList, source) is ListViewItem &&
            source is not System.Windows.Controls.Primitives.ButtonBase)
            _viewModel.Soundboard.EditCommand.Execute(null);
    }

    private void OnClipListKeyDown(object sender, KeyEventArgs e)
    {
        var sb = _viewModel.Soundboard;
        switch (e.Key)
        {
            case Key.F2: sb.RenameCommand.Execute(null); break;
            case Key.Delete: sb.DeleteCommand.Execute(null); break;
            case Key.Enter: sb.PlayToDiscordCommand.Execute(null); break;
            case Key.Space: sb.PreviewCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
