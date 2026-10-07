using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Settings;

namespace Echodeck.App.ViewModels;

/// <summary>One row on the Hotkeys tab.</summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly Action<string, string?>? _onChanged;
    private bool _applying;

    public HotkeyRowViewModel(string actionId, string name, string? gesture, bool editable, Action<string, string?>? onChanged)
    {
        ActionId = actionId;
        Name = name;
        IsEditable = editable;
        _onChanged = onChanged;
        _applying = true;
        Gesture = gesture;
        _applying = false;
    }

    public string ActionId { get; }
    public string Name { get; }
    public bool IsEditable { get; }

    [ObservableProperty] private string? _gesture;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasProblem;

    partial void OnGestureChanged(string? value)
    {
        if (!_applying) _onChanged?.Invoke(ActionId, value);
    }
}

/// <summary>
/// "Hotkeys" tab: shortcuts for the global actions (editable here) and an overview of per-clip
/// shortcuts (edited on the Soundboard tab), each with its live registration status.
/// </summary>
public sealed partial class HotkeysViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly HotkeyCoordinator _coordinator;

    public HotkeysViewModel(SettingsService settings, HotkeyService hotkeys, HotkeyCoordinator coordinator)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _coordinator = coordinator;
        Build();
        _hotkeys.RegistrationsChanged += OnRegistrationsChanged;
    }

    public ObservableCollection<HotkeyRowViewModel> GlobalRows { get; } = new();
    public ObservableCollection<HotkeyRowViewModel> ClipRows { get; } = new();

    [ObservableProperty] private bool _hasClipRows;

    [RelayCommand]
    private void ResetToDefaults()
    {
        _settings.Update(s => s.Hotkeys = HotkeyActions.DefaultBindings());
        Build();
    }

    private void Build()
    {
        var current = _settings.Current.Hotkeys;
        GlobalRows.Clear();
        foreach (var action in HotkeyActions.Global)
        {
            current.TryGetValue(action.Id, out var gesture);
            GlobalRows.Add(new HotkeyRowViewModel(action.Id, action.Name, gesture, editable: true, OnGlobalChanged));
        }
        RefreshStatuses();
    }

    private void OnGlobalChanged(string actionId, string? gesture) =>
        _settings.Update(s =>
        {
            if (string.IsNullOrEmpty(gesture)) s.Hotkeys.Remove(actionId);
            else s.Hotkeys[actionId] = gesture;
        });

    private void OnRegistrationsChanged(object? sender, EventArgs e) => RefreshStatuses();

    private void RefreshStatuses()
    {
        var regs = _hotkeys.Registrations;
        foreach (var row in GlobalRows) ApplyStatus(row, regs);

        ClipRows.Clear();
        foreach (var binding in _coordinator.CurrentBindings().Where(b => HotkeyActions.TryGetClipId(b.ActionId, out _)))
        {
            var row = new HotkeyRowViewModel(binding.ActionId, binding.Name.Replace("Clip: ", ""), binding.Gesture.ToString(), editable: false, null);
            ApplyStatus(row, regs);
            ClipRows.Add(row);
        }
        HasClipRows = ClipRows.Count > 0;
    }

    private static void ApplyStatus(HotkeyRowViewModel row, IReadOnlyDictionary<string, HotkeyRegistration> regs)
    {
        if (string.IsNullOrEmpty(row.Gesture))
        {
            row.Status = "";
            row.HasProblem = false;
        }
        else if (regs.TryGetValue(row.ActionId, out var reg))
        {
            row.HasProblem = reg.State != HotkeyState.Registered || !string.IsNullOrEmpty(reg.Message);
            row.Status = string.IsNullOrEmpty(reg.Message) ? "✔ Active" : reg.Message;
        }
        else
        {
            row.Status = "Updating…";
            row.HasProblem = false;
        }
    }

    public void Dispose() => _hotkeys.RegistrationsChanged -= OnRegistrationsChanged;
}
