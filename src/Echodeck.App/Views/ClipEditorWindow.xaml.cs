using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Echodeck.App.ViewModels;

namespace Echodeck.App.Views;

/// <summary>
/// Hosts <see cref="ClipEditorViewModel"/>. Code-behind only maps keys to view-model actions,
/// since the shortcuts must work no matter which button has focus.
/// </summary>
public partial class ClipEditorWindow : Window
{
    private readonly ClipEditorViewModel _viewModel;

    public ClipEditorWindow(ClipEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool typing = Keyboard.FocusedElement is TextBox;

        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.CancelCommand.Execute(null);
                break;
            case Key.S when ctrl:
                _viewModel.SaveCommand.Execute(null);
                break;
            case Key.Enter:
                _viewModel.PlayToDiscordCommand.Execute(null);
                break;
            case Key.Space when !typing:
                _viewModel.TogglePreviewCommand.Execute(null);
                break;
            case Key.Left when !typing:
                _viewModel.Nudge(endMarker: shift, ctrl ? -0.1 : -0.01);
                break;
            case Key.Right when !typing:
                _viewModel.Nudge(endMarker: shift, ctrl ? 0.1 : 0.01);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
