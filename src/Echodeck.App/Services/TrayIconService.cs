using System.Windows;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Settings;
using Forms = System.Windows.Forms;

namespace Echodeck.App.Services;

/// <summary>
/// Notification-area icon so Echodeck can live in the background while gaming.
/// Double-click opens the window; the right-click menu has the quick controls.
/// Uses WinForms' NotifyIcon (part of .NET), which runs fine on the WPF UI thread.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppActions _actions;
    private readonly SettingsService _settings;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _mute;
    private readonly Forms.ToolStripMenuItem _record;

    public TrayIconService(AppActions actions, SettingsService settings)
    {
        _actions = actions;
        _settings = settings;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Echodeck", null, (_, _) => _actions.ShowWindow());
        menu.Items.Add("Soundboard", null, (_, _) => _actions.ShowWindow("Soundboard"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Replay last 5 s into Discord", null, async (_, _) => await _actions.ExecuteAsync(HotkeyActions.Replay5));
        menu.Items.Add("Open replay editor", null, async (_, _) => await _actions.ExecuteAsync(HotkeyActions.OpenEditor));
        menu.Items.Add("Stop clips", null, (_, _) => _actions.StopClips());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _mute = new Forms.ToolStripMenuItem("Mute microphone", null, (_, _) => _actions.ToggleMute());
        _record = new Forms.ToolStripMenuItem("Record replay buffer", null, (_, _) => _actions.SetReplayBufferEnabled(!_settings.Current.ReplayBufferEnabled));
        menu.Items.Add(_mute);
        menu.Items.Add(_record);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Echodeck", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        menu.Opening += (_, _) => RefreshChecks();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Echodeck",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _actions.ShowWindow();
        _settings.Changed += OnSettingsChanged;
        RefreshChecks();
    }

    public event EventHandler? ExitRequested;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private void OnSettingsChanged(object? sender, AppSettings s) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshChecks);

    private void RefreshChecks()
    {
        var s = _settings.Current;
        _mute.Checked = s.MicrophoneMuted;
        _record.Checked = s.ReplayBufferEnabled;
        // Tooltip max is 63 chars.
        _icon.Text = s.MicrophoneMuted ? "Echodeck — mic muted" : s.ReplayBufferEnabled ? "Echodeck" : "Echodeck — replay paused";
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/echodeck.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _icon.Visible = false; // otherwise a ghost icon stays until the mouse passes over it
        _icon.Dispose();
    }
}
