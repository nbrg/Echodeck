using System.Runtime.InteropServices;
using System.Windows.Interop;
using Echodeck.Core.Hotkeys;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.Services;

public enum HotkeyState
{
    Registered,
    /// <summary>Another Echodeck action already uses this shortcut.</summary>
    Conflict,
    /// <summary>Another program registered it first (RegisterHotKey failed).</summary>
    TakenByOtherApp,
    Invalid,
}

public sealed record HotkeyRegistration(string ActionId, HotkeyGesture Gesture, HotkeyState State, string Message);

/// <summary>
/// System-wide hotkeys via Win32 <c>RegisterHotKey</c>.
/// <para>
/// Why this API: Windows delivers WM_HOTKEY to us no matter which window has focus, including
/// CS2 in fullscreen, and Echodeck never sees or hooks any other keystrokes, so it's
/// anti-cheat-friendly. The catch is that a registered combination is reserved for us, which is
/// why plain typing keys need a modifier (see <see cref="HotkeyGesture.ValidationError"/>).
/// </para>
/// <para>
/// Messages arrive at a hidden message-only window (parent HWND_MESSAGE) created on the UI
/// thread, so <see cref="Pressed"/> is raised on the UI thread. MOD_NOREPEAT stops a held key from
/// firing over and over.
/// </para>
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly ILogger<HotkeyService> _logger;
    private readonly HwndSource _window;
    private readonly Dictionary<int, string> _registeredIds = new();
    private IReadOnlyList<HotkeyBinding> _bindings = Array.Empty<HotkeyBinding>();
    private Dictionary<string, HotkeyRegistration> _registrations = new();
    private int _nextId = 1;
    private int _suspendCount;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _window = new HwndSource(new HwndSourceParameters("EchodeckHotkeys") { ParentWindow = HWND_MESSAGE, WindowStyle = 0 });
        _window.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread with the action id of the pressed hotkey.</summary>
    public event EventHandler<string>? Pressed;

    /// <summary>Raised after (re-)registration so the UI can show what worked.</summary>
    public event EventHandler? RegistrationsChanged;

    public IReadOnlyDictionary<string, HotkeyRegistration> Registrations => _registrations;

    public bool IsSuspended => _suspendCount > 0;

    /// <summary>Replaces all hotkeys with <paramref name="bindings"/>.</summary>
    public void SetBindings(IReadOnlyList<HotkeyBinding> bindings)
    {
        _bindings = bindings;
        if (!IsSuspended) Register();
        else Publish(bindings.ToDictionary(b => b.ActionId, b => new HotkeyRegistration(b.ActionId, b.Gesture, HotkeyState.Registered, "")));
    }

    /// <summary>
    /// Temporarily releases every hotkey, e.g. while the user is typing a new shortcut into a
    /// hotkey box (otherwise pressing F8 there would trigger the F8 action instead of being recorded).
    /// </summary>
    public void Suspend()
    {
        if (_suspendCount++ == 0) UnregisterAll();
    }

    public void Resume()
    {
        if (_suspendCount == 0) return;
        if (--_suspendCount == 0) Register();
    }

    private void Register()
    {
        UnregisterAll();
        var conflicts = HotkeyConflicts.Find(_bindings);
        var taken = new Dictionary<HotkeyGesture, string>();
        var results = new Dictionary<string, HotkeyRegistration>();

        foreach (var b in _bindings)
        {
            if (b.Gesture.ValidationError is { } invalid)
            {
                results[b.ActionId] = new(b.ActionId, b.Gesture, HotkeyState.Invalid, invalid);
                continue;
            }
            if (taken.TryGetValue(b.Gesture, out var winner))
            {
                results[b.ActionId] = new(b.ActionId, b.Gesture, HotkeyState.Conflict, $"{b.Gesture} is already used by \"{winner}\" — this one won't fire.");
                continue;
            }

            int id = _nextId++;
            if (RegisterHotKey(_window.Handle, id, (uint)b.Gesture.Modifiers | MOD_NOREPEAT, (uint)b.Gesture.VirtualKey))
            {
                _registeredIds[id] = b.ActionId;
                taken[b.Gesture] = b.Name;
                string note = conflicts.TryGetValue(b.ActionId, out var c) ? $"Works, but {c}." : "";
                results[b.ActionId] = new(b.ActionId, b.Gesture, HotkeyState.Registered, note);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                string message = error == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? $"{b.Gesture} is already taken by another program (e.g. Discord, GeForce Experience, OBS). Pick another shortcut."
                    : $"Windows refused {b.Gesture} (error {error}).";
                _logger.LogWarning("Hotkey {Gesture} for {Action} not registered: {Message}", b.Gesture, b.ActionId, message);
                results[b.ActionId] = new(b.ActionId, b.Gesture, HotkeyState.TakenByOtherApp, message);
            }
        }

        _logger.LogInformation("Hotkeys registered: {Count}/{Total}", _registeredIds.Count, _bindings.Count);
        Publish(results);
    }

    private void Publish(Dictionary<string, HotkeyRegistration> results)
    {
        _registrations = results;
        RegistrationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        foreach (int id in _registeredIds.Keys) UnregisterHotKey(_window.Handle, id);
        _registeredIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _registeredIds.TryGetValue(wParam.ToInt32(), out var actionId))
        {
            handled = true;
            _logger.LogDebug("Hotkey pressed: {Action}", actionId);
            Pressed?.Invoke(this, actionId);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
