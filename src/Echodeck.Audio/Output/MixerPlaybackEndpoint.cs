using Echodeck.Audio.Devices;
using Echodeck.Audio.Dsp;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Echodeck.Audio.Output;

/// <summary>
/// Plays a never-ending mixer stream on one output device, kept alive by <see cref="SupervisedEndpoint"/>.
/// Shared mode, event-driven, 30 ms latency. The stream is converted to the device's own mix
/// format (sample rate via WDL resampler, channel count via <see cref="FromStereoSampleProvider"/>),
/// so Windows never has to reject or silently convert it.
/// </summary>
public abstract class MixerPlaybackEndpoint : SupervisedEndpoint
{
    public const int LatencyMs = 30;

    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiOut? _output;

    protected MixerPlaybackEndpoint(SettingsService settings, AudioDeviceService devices, ILogger logger)
        : base(settings, devices, logger)
    {
    }

    protected sealed override DataFlow Flow => DataFlow.Render;

    /// <summary>The device to open: an endpoint ID, null for the Windows default, or an error to show.</summary>
    protected abstract (string? DeviceId, bool UseDefault, string? Error) ResolveDevice(AppSettings settings);

    /// <summary>The 48 kHz stereo stream to play.</summary>
    protected abstract ISampleProvider CreateSource();

    protected abstract string DescribeActive(WaveFormat mixFormat);

    protected sealed override EndpointStatus Open(AppSettings settings)
    {
        var (id, useDefault, error) = ResolveDevice(settings);
        if (error is not null) return new EndpointStatus(false, null, error);

        _enumerator = new MMDeviceEnumerator();
        if (useDefault)
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                return new EndpointStatus(false, null, "No output device found");
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        else
        {
            try { _device = _enumerator.GetDevice(id); }
            catch { _device = null; }
            if (_device is null || _device.State != DeviceState.Active)
            {
                _device?.Dispose();
                _device = null;
                return new EndpointStatus(false, null, "Selected output device is not available — waiting for it");
            }
        }

        OpenDeviceId = _device.ID;
        OpenedDefaultDevice = useDefault;

        WaveFormat mix;
        using (var client = _device.AudioClient) mix = client.MixFormat; // a new client per call; safe to dispose

        ISampleProvider source = CreateSource();
        if (mix.SampleRate != source.WaveFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, mix.SampleRate);
        if (mix.Channels != 2)
            source = new FromStereoSampleProvider(source, mix.Channels);

        _output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, LatencyMs);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(source);
        _output.Play();

        return new EndpointStatus(true, _device.FriendlyName, DescribeActive(mix));
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (ReferenceEquals(sender, _output)) ReportFault(e.Exception);
    }

    protected sealed override void Close()
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
}
