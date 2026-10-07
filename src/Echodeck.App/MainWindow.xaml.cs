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
        foreach (var list in new[] { SoundboardList, RecentList })
        {
            list.MouseDoubleClick += OnClipDoubleClick;
            // Preview (tunnelling) so the ListView itself can never swallow Del/F2/Enter/Space.
            list.PreviewKeyDown += OnClipListKeyDown;
            // Only the list the user is working in (selecting in one list also moves the other's selection).
            list.SelectionChanged += (_, _) => { if (list.IsKeyboardFocusWithin) SyncSelection(list); };
            list.GotKeyboardFocus += (_, _) => SyncSelection(list);
            list.ContextMenuOpening += (_, _) => SyncSelection(list);
        }
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

    /// <summary>
    /// Tells the soundboard view model which clips are selected in whichever list the user is
    /// using, so Delete / Rename / the right-click menu act on exactly those.
    /// </summary>
    private void SyncSelection(ListView list)
    {
        var selected = list.SelectedItems.OfType<ClipItemViewModel>().ToList();
        var sb = _viewModel.Soundboard;
        sb.SelectedClips = selected;
        if (list == RecentList && selected.Count > 0) sb.SelectedClip = selected[^1];
    }

    private void OnClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only real rows: ignore the row buttons and empty space.
        if (sender is ListView list && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(list, source) is ListViewItem { DataContext: ClipItemViewModel clip } &&
            FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is null)
            _viewModel.Soundboard.EditCommand.Execute(clip);
    }

    private void OnClipListKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListView list) return;
        SyncSelection(list);
        var sb = _viewModel.Soundboard;
        switch (e.Key)
        {
            case Key.Delete: sb.DeleteCommand.Execute(null); break;
            case Key.F2: sb.RenameCommand.Execute(null); break;
            case Key.Enter: sb.PlayToDiscordCommand.Execute(null); break;
            case Key.Space: sb.PreviewCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
