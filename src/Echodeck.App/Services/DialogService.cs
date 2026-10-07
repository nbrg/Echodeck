using System.Windows;
using Echodeck.App.ViewModels;
using Echodeck.App.Views;

namespace Echodeck.App.Services;

/// <summary>Opens windows and dialogs so view models never create UI objects themselves.</summary>
public sealed class DialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    /// <summary>Asks for a line of text; returns null if cancelled.</summary>
    public string? Prompt(string title, string message, string initialValue)
    {
        var window = new TextPromptWindow(title, message, initialValue) { Owner = Owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public bool Confirm(string message, string title)
    {
        var owner = Owner;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    /// <summary>Shows the clip editor modally (it closes itself on save/cancel).</summary>
    public void ShowEditor(ClipEditorViewModel viewModel)
    {
        var window = new ClipEditorWindow(viewModel) { Owner = Owner };
        window.ShowDialog();
    }
}
