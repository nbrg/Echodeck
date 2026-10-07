using System.Windows;
using Echodeck.App.ViewModels;
using Echodeck.App.Views;
using Microsoft.Win32;

namespace Echodeck.App.Services;

/// <summary>Opens windows and dialogs so view models never create UI objects themselves.</summary>
public sealed class DialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

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

    /// <summary>Lets the user pick audio files to import. Returns an empty list if cancelled.</summary>
    public IReadOnlyList<string> PickAudioFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import clips",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|All files|*.*",
            Multiselect = true,
        };
        var owner = Owner;
        bool? ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FileNames : Array.Empty<string>();
    }

    /// <summary>
    /// Shows the clip editor modally. When Echodeck is in the tray (e.g. opened by a hotkey
    /// mid-game), the editor gets its own taskbar button and is pushed to the front.
    /// </summary>
    public void ShowEditor(ClipEditorViewModel viewModel)
    {
        var owner = Owner;
        var window = new ClipEditorWindow(viewModel) { Owner = owner };
        if (owner is null)
        {
            window.ShowInTaskbar = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = true;
            window.ContentRendered += (_, _) =>
            {
                window.Activate();
                window.Topmost = false;
            };
        }
        window.ShowDialog();
    }
}
