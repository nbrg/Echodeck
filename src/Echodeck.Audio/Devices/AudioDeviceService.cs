using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Echodeck.Audio.Devices;

/// <summary>Plain snapshot of an endpoint — safe to hand to the UI (no COM objects).</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>Where Discord's audio session currently lives.</summary>
public sealed record DiscordSessionDevice(string DeviceId, string DeviceName, bool SessionActive);

/// <summary>Status of one of Echodeck's own audio endpoints (microphone, Discord output).</summary>
public sealed record EndpointStatus(bool Active, string? DeviceName, string Message)
{
    public static EndpointStatus Starting { get; } = new(false, null, "Starting…");
}

/// <summary>
/// Device enumeration, hot-plug notifications and audio-session lookup.
/// <para>
/// Every query creates a short-lived MMDeviceEnumerator on the calling thread and returns plain
/// records, never MMDevice objects. That keeps COM apartment rules trivial (the WPF UI thread is
/// STA, capture threads are MTA) and guarantees nothing leaks between calls.
/// </para>
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    private readonly ILogger<AudioDeviceService> _logger;
    private readonly MMDeviceEnumerator _notificationEnumerator;
    private readonly NotificationClient _notificationClient;
    private readonly Timer _debounce;

    public AudioDeviceService(ILogger<AudioDeviceService> logger)
    {
        _logger = logger;
        _debounce = new Timer(_ => RaiseDevicesChanged(), null, Timeout.Infinite, Timeout.Infinite);
        _notificationClient = new NotificationClient(this);
        _notificationEnumerator = new MMDeviceEnumerator();
        try
        {
            _notificationEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Could not register for audio device notifications; hot-plug recovery will rely on polling");
        }
    }

    /// <summary>
    /// Raised (on a thread-pool thread, debounced by 500 ms) when devices are added/removed,
    /// change state, or the default device changes. A USB headset reconnect produces a burst of
    /// notifications; debouncing turns that into one event.
    /// </summary>
    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => GetDevices(DataFlow.Render);

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => GetDevices(DataFlow.Capture);

    private IReadOnlyList<AudioDeviceInfo> GetDevices(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }
        catch (COMException ex)
        {
            _logger.LogError(ex, "Failed to enumerate {Flow} devices", flow);
        }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Finds the output device on which any of <paramref name="processIds"/> has an audio session.
    /// Active sessions win over inactive ones; among inactive ones <paramref name="preferredDeviceId"/>
    /// wins so we don't flap between devices while Discord is silent.
    /// </summary>
    public DiscordSessionDevice? FindDeviceForProcesses(IReadOnlyCollection<int> processIds, string? preferredDeviceId) =>
        FindDeviceForProcesses(processIds, preferredDeviceId, DataFlow.Render, activeOnly: false);

    /// <summary>
    /// Like <see cref="FindDeviceForProcesses(IReadOnlyCollection{int}, string?)"/> for either
    /// direction. With <paramref name="activeOnly"/>, stale sessions (a device Discord used earlier)
    /// are ignored — used by the setup check so it only reports what Discord is using right now.
    /// </summary>
    public DiscordSessionDevice? FindDeviceForProcesses(IReadOnlyCollection<int> processIds, string? preferredDeviceId, DataFlow flow, bool activeOnly)
    {
        if (processIds.Count == 0) return null;
        var pids = processIds.ToHashSet();
        DiscordSessionDevice? inactiveMatch = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (!pids.Contains((int)session.GetProcessID)) continue;

                        if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                            return new DiscordSessionDevice(device.ID, device.FriendlyName, true);

                        if (activeOnly) continue;
                        if (inactiveMatch is null || device.ID == preferredDeviceId)
                            inactiveMatch = new DiscordSessionDevice(device.ID, device.FriendlyName, false);
                    }
                }
            }
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Audio session enumeration failed");
        }
        return inactiveMatch;
    }

    /// <summary>The current Windows default output or input device (Multimedia role), if any.</summary>
    public AudioDeviceInfo? GetDefaultDevice(DataFlow flow) =>
        (flow == DataFlow.Render ? GetOutputDevices() : GetInputDevices()).FirstOrDefault(d => d.IsDefault);

    /// <summary>VB-CABLE detection for the setup page / diagnostics.</summary>
    public (AudioDeviceInfo? CableInput, AudioDeviceInfo? CableOutput) FindVirtualCable()
    {
        var input = GetOutputDevices().FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        var output = GetInputDevices().FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        return (input, output);
    }

    private void OnNotification(string what)
    {
        _logger.LogInformation("Audio device notification: {What}", what);
        _debounce.Change(500, Timeout.Infinite);
    }

    private void RaiseDevicesChanged()
    {
        try { DevicesChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogError(ex, "DevicesChanged handler failed"); }
    }

    public void Dispose()
    {
        try { _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationClient); }
        catch (COMException) { /* already gone */ }
        _notificationEnumerator.Dispose();
        _debounce.Dispose();
    }

    /// <summary>
    /// IMMNotificationClient callbacks arrive on an arbitrary system thread and must return quickly
    /// without calling back into the enumerator — so we only log and kick the debounce timer.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly AudioDeviceService _owner;
        public NotificationClient(AudioDeviceService owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.OnNotification($"state {newState}: {deviceId}");
        public void OnDeviceAdded(string pwstrDeviceId) => _owner.OnNotification($"added: {pwstrDeviceId}");
        public void OnDeviceRemoved(string deviceId) => _owner.OnNotification($"removed: {deviceId}");
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) _owner.OnNotification($"default {flow} → {defaultDeviceId}");
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
