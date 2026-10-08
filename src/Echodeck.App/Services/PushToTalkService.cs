using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Echodeck.Audio.Mixing;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.Services;

/// <summary>
/// Push-to-talk support for people who use Push to Talk in Discord (instead of Voice Activity).
/// While a clip plays into Discord, Echodeck holds its own push-to-talk key (F13–F24, which no
/// keyboard has, so games never react to it) and lets go shortly after the clip ends. The clip
/// itself starts a moment after the key goes down, so Discord is already transmitting.
/// <para>
/// One-time setup: add that key as an extra Push to Talk keybind in Discord (Discord allows several,
/// so your own key keeps working). Because no keyboard has F13, Echodeck presses it for you while
/// Discord is recording the keybind ("Press it for Discord" button).
/// </para>
/// </summary>
public sealed class PushToTalkService : IDisposable
{
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(10); // a stuck key must never stay down forever

    private readonly AudioMixerService _mixer;
    private readonly SettingsService _settings;
    private readonly ILogger<PushToTalkService> _logger;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _enabled;
    private ushort _vk;
    private ushort _heldVk;           // 0 = not held
    private DateTime _heldSince, _lastPlaying;
    private bool _disposed;

    public PushToTalkService(AudioMixerService mixer, SettingsService settings, ILogger<PushToTalkService> logger)
    {
        _mixer = mixer;
        _settings = settings;
        _logger = logger;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The key is down right now (a clip is playing, or a test press).</summary>
    public bool IsHolding { get { lock (_lock) return _heldVk != 0; } }

    /// <summary>Raised (on any thread) when <see cref="IsHolding"/> changes.</summary>
    public event EventHandler? HoldingChanged;

    public void Start()
    {
        Apply(_settings.Current);
        _settings.Changed += OnSettingsChanged;
        _mixer.ClipStarted += OnClipStarted;
    }

    private void OnSettingsChanged(object? sender, AppSettings s) => Apply(s);

    private void Apply(AppSettings s)
    {
        lock (_lock)
        {
            _enabled = s.PushToTalkEnabled;
            _vk = PushToTalkKeys.VirtualKey(PushToTalkKeys.IsValid(s.PushToTalkKey) ? s.PushToTalkKey : PushToTalkKeys.Default);
            _mixer.DiscordStartDelay = _enabled ? TimeSpan.FromMilliseconds(s.PushToTalkLeadMs) : TimeSpan.Zero;
            if (!_enabled || (_heldVk != 0 && _heldVk != _vk)) ReleaseUnlocked();
        }
    }

    private void OnClipStarted(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!_enabled || _disposed) return;
            _lastPlaying = DateTime.UtcNow;
            PressUnlocked();
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (_heldVk == 0) return;
            var now = DateTime.UtcNow;
            if (_mixer.IsClipPlaying) _lastPlaying = now;
            if (now - _lastPlaying >= ReleaseAfter || now - _heldSince >= MaxHold) ReleaseUnlocked();
        }
    }

    /// <summary>
    /// Presses the key for <paramref name="hold"/> after <paramref name="countdown"/> (so you can
    /// click "Record keybind" in Discord first). Discord then records it as a push-to-talk key.
    /// </summary>
    public async Task TestPressAsync(TimeSpan countdown, TimeSpan hold)
    {
        await Task.Delay(countdown);
        lock (_lock)
        {
            if (_heldVk != 0) return; // a clip is holding it right now
            _lastPlaying = DateTime.UtcNow + hold; // Tick releases ReleaseAfter later
            PressUnlocked();
        }
    }

    /// <summary>The key Echodeck uses, e.g. "F13".</summary>
    public string KeyName => _settings.Current.PushToTalkKey;

    /// <summary>
    /// True when Discord runs as administrator and Echodeck doesn't: Windows then silently drops
    /// the key presses. Null when unknown.
    /// </summary>
    public static bool? IsDiscordElevated()
    {
        try
        {
            if (IsCurrentProcessElevated()) return false;
            foreach (var p in Process.GetProcessesByName("Discord").Concat(Process.GetProcessesByName("DiscordPTB")).Concat(Process.GetProcessesByName("DiscordCanary")))
            {
                using (p)
                {
                    IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        // A normal app can't open an elevated app's token.
                        if (!OpenProcessToken(handle, TokenQuery, out IntPtr token)) return Marshal.GetLastWin32Error() == 5;
                        CloseHandle(token);
                        return false;
                    }
                    finally { CloseHandle(handle); }
                }
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ------------------------------------------------------------------ key state (under _lock)

    private void PressUnlocked()
    {
        if (_heldVk != 0) return;
        if (!SendKey(_vk, keyUp: false)) return;
        _heldVk = _vk;
        _heldSince = DateTime.UtcNow;
        _timer.Change(50, 50);
        _logger.LogDebug("Push-to-talk key down");
        RaiseHoldingChanged();
    }

    private void ReleaseUnlocked()
    {
        if (_heldVk == 0) return;
        SendKey(_heldVk, keyUp: true);
        _heldVk = 0;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogDebug("Push-to-talk key up");
        RaiseHoldingChanged();
    }

    private void RaiseHoldingChanged() => ThreadPool.QueueUserWorkItem(_ => HoldingChanged?.Invoke(this, EventArgs.Empty));

    private bool SendKey(ushort vk, bool keyUp)
    {
        var input = new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, 0),
                    dwFlags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1) return true;
        _logger.LogWarning("Push-to-talk key {Direction} failed: {Error}", keyUp ? "up" : "down", new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return false;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _mixer.ClipStarted -= OnClipStarted;
        lock (_lock)
        {
            _disposed = true;
            ReleaseUnlocked(); // never leave the key down when Echodeck exits
        }
        _timer.Dispose();
    }

    // ------------------------------------------------------------------ Win32

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must include MOUSEINPUT (the largest member) so INPUT has the size Windows expects.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
