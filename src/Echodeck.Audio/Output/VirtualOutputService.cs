using Echodeck.Audio.Devices;
using Echodeck.Audio.Mixing;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Echodeck.Audio.Output;

/// <summary>
/// Plays the Discord mix (mic + clips) into the virtual cable ("CABLE Input"), which Discord
/// records from as "CABLE Output". Auto-detects VB-CABLE unless a device is chosen.
/// </summary>
public sealed class VirtualOutputService : MixerPlaybackEndpoint
{
    private readonly AudioMixerService _mixer;
    private string? _appliedDeviceId;

    public VirtualOutputService(AudioMixerService mixer, SettingsService settings, AudioDeviceService devices, ILogger<VirtualOutputService> logger)
        : base(settings, devices, logger)
    {
        _mixer = mixer;
        _appliedDeviceId = settings.Current.DiscordOutputDeviceId;
    }

    protected override (string? DeviceId, bool UseDefault, string? Error) ResolveDevice(AppSettings settings)
    {
        _appliedDeviceId = settings.DiscordOutputDeviceId;
        string? id = settings.DiscordOutputDeviceId ?? Devices.FindVirtualCable().CableInput?.Id;
        return id is null
            ? (null, false, "VB-CABLE not found — install it from vb-audio.com/Cable")
            : (id, false, null);
    }

    protected override ISampleProvider CreateSource() => _mixer.CreateDiscordSource();

    protected override string DescribeActive(WaveFormat mix) => $"Sending to Discord ({mix.SampleRate} Hz, {mix.Channels} ch)";

    protected override bool NeedsRestart(AppSettings settings) => settings.DiscordOutputDeviceId != _appliedDeviceId;
}
