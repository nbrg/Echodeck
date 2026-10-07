using Echodeck.Audio.Devices;
using Echodeck.Audio.Mixing;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Echodeck.Audio.Output;

/// <summary>
/// Lets you hear the clips you play into Discord on your own headphones. Only clips go here,
/// never your microphone, so you don't hear yourself.
/// <para>
/// It uses the "headphones for previews" device, or the Windows default. The stream stays open
/// while the option is on, so clips start without delay; while nothing plays it renders silence,
/// which costs almost nothing. Echodeck's own playback is never recorded by per-process Discord
/// capture, so this can't feed back into replays.
/// </para>
/// </summary>
public sealed class HeadphoneClipMonitorService : MixerPlaybackEndpoint
{
    private readonly AudioMixerService _mixer;
    private (bool Enabled, string? DeviceId) _applied;

    public HeadphoneClipMonitorService(AudioMixerService mixer, SettingsService settings, AudioDeviceService devices, ILogger<HeadphoneClipMonitorService> logger)
        : base(settings, devices, logger)
    {
        _mixer = mixer;
        var s = settings.Current;
        _applied = (s.HearClipsInHeadphones, s.PreviewDeviceId);
        StatusChanged += (_, status) => _mixer.SetHeadphoneOutputActive(status.Active);
    }

    protected override (string? DeviceId, bool UseDefault, string? Error) ResolveDevice(AppSettings settings)
    {
        _applied = (settings.HearClipsInHeadphones, settings.PreviewDeviceId);
        if (!settings.HearClipsInHeadphones) return (null, false, "Off");
        return (settings.PreviewDeviceId, settings.PreviewDeviceId is null, null);
    }

    protected override ISampleProvider CreateSource() => _mixer.CreateHeadphoneSource();

    protected override string DescribeActive(WaveFormat mix) => "Clips you play are also heard here";

    protected override bool NeedsRestart(AppSettings settings) =>
        (settings.HearClipsInHeadphones, settings.PreviewDeviceId) != _applied;
}
