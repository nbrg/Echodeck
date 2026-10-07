using System.Windows.Threading;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Settings;
using Echodeck.Core.Soundboard;

namespace Echodeck.App.Services;

/// <summary>
/// Keeps the registered hotkeys equal to settings (global actions) + the soundboard library
/// (per-clip shortcuts), and runs the matching <see cref="AppActions"/> when one is pressed.
/// </summary>
public sealed class HotkeyCoordinator : IDisposable
{
    private readonly HotkeyService _hotkeys;
    private readonly SettingsService _settings;
    private readonly ClipLibraryStore _library;
    private readonly AppActions _actions;
    private readonly Dispatcher _dispatcher;
    private string _appliedSignature = "";

    public HotkeyCoordinator(HotkeyService hotkeys, SettingsService settings, ClipLibraryStore library, AppActions actions)
    {
        _hotkeys = hotkeys;
        _settings = settings;
        _library = library;
        _actions = actions;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        _hotkeys.Pressed += OnPressed;
        _settings.Changed += OnSourcesChanged;
        _library.Changed += OnSourcesChanged;
        Rebuild();
    }

    /// <summary>All bindings with display names, global actions first.</summary>
    public IReadOnlyList<HotkeyBinding> CurrentBindings()
    {
        var settings = _settings.Current;
        var list = new List<HotkeyBinding>();
        foreach (var action in HotkeyActions.Global)
        {
            if (settings.Hotkeys.TryGetValue(action.Id, out var text) && HotkeyGesture.ParseOrNull(text) is { } g)
                list.Add(new HotkeyBinding(action.Id, action.Name, g));
        }
        foreach (var clip in _library.Clips.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (HotkeyGesture.ParseOrNull(clip.Hotkey) is { } g)
                list.Add(new HotkeyBinding(HotkeyActions.ForClip(clip.Id), $"Clip: {clip.Name}", g));
        }
        return list;
    }

    private void OnSourcesChanged(object? sender, object? e) => _dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        var bindings = CurrentBindings();
        // Re-registering is cheap but not free (and briefly releases keys): skip when nothing changed.
        string signature = string.Join("|", bindings.Select(b => $"{b.ActionId}={b.Gesture}={b.Name}"));
        if (signature == _appliedSignature) return;
        _appliedSignature = signature;
        _hotkeys.SetBindings(bindings);
    }

    private async void OnPressed(object? sender, string actionId) => await _actions.ExecuteAsync(actionId);

    public void Dispose()
    {
        _hotkeys.Pressed -= OnPressed;
        _settings.Changed -= OnSourcesChanged;
        _library.Changed -= OnSourcesChanged;
    }
}
