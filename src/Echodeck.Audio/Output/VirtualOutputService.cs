using Echodeck.Audio.Devices;
using Echodeck.Audio.Dsp;
using Echodeck.Audio.Mixing;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Echodeck.Audio.Output;

/// <summary>
/// Plays the mixer's output into the virtual cable ("CABLE Input"), which Discord records from
/// as "CABLE Output". Shared mode, event-driven, 30 ms latency.
/// <para>
/// The stream is converted to the device's own mix format here (sample rate via WDL resampler,
/// channel count via <see cref="FromStereoSampleProvider"/>), so Windows never has to reject or
/// silently convert our format — VB-CABLE often defaults to 44.1 kHz.
/// </para>
/// </summary>
public sealed class VirtualOutputService : SupervisedEndpoint
{
    private const int LatencyMs = 30;

    private readonly AudioMixerService _mixer;
    private string? _appliedDeviceId;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiOut? _output;

    public VirtualOutputService(AudioMixerService mixer, SettingsService settings, AudioDeviceService devices, ILogger<VirtualOutputService> logger)
        : base(settings, devices, logger)
    {
        _mixer = mixer;
        _appliedDeviceId = settings.Current.DiscordOutputDeviceId;
    }

    protected override DataFlow Flow => DataFlow.Render;

    protected override EndpointStatus Open(AppSettings settings)
    {
        _appliedDeviceId = settings.DiscordOutputDeviceId;
        string? id = settings.DiscordOutputDeviceId ?? Devices.FindVirtualCable().CableInput?.Id;
        if (id is null)
            return new EndpointStatus(false, null, "VB-CABLE not found — install it from vb-audio.com/Cable");

        _enumerator = new MMDeviceEnumerator();
        try { _device = _enumerator.GetDevice(id); }
        catch { _device = null; }
        if (_device is null || _device.State != DeviceState.Active)
        {
            _device?.Dispose();
            _device = null;
            return new EndpointStatus(false, null, "Selected Discord output device is not available — waiting for it");
        }

        OpenDeviceId = _device.ID;
        OpenedDefaultDevice = false;

        WaveFormat mix;
        using (var client = _device.AudioClient) mix = client.MixFormat;

        ISampleProvider source = _mixer.CreateSampleProvider();
        if (mix.SampleRate != source.WaveFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, mix.SampleRate);
        if (mix.Channels != 2)
            source = new FromStereoSampleProvider(source, mix.Channels);

        _output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, LatencyMs);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(source);
        _output.Play();

        return new EndpointStatus(true, _device.FriendlyName, $"Sending to Discord ({mix.SampleRate} Hz, {mix.Channels} ch)");
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (ReferenceEquals(sender, _output)) ReportFault(e.Exception);
    }

    protected override void Close()
    {
        var output = _output;
        _output = null;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { /* device gone */ }
            output.Dispose();
        }
        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    protected override bool NeedsRestart(AppSettings settings) => settings.DiscordOutputDeviceId != _appliedDeviceId;
}
