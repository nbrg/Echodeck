using Echodeck.Audio.Devices;
using Echodeck.Core.Mixing;
using Echodeck.Core.Settings;
using Echodeck.Core.Setup;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Echodeck.Audio.Capture;

/// <summary>
/// Captures the physical microphone (shared mode, event-driven, 20 ms buffers), converts it to
/// 48 kHz stereo float and hands it to the mixer through <see cref="MicJitterBuffer"/>.
/// Your voice is not processed in any way apart from the volume you set.
/// </summary>
public sealed class MicrophoneCaptureService : SupervisedEndpoint
{
    private readonly MicJitterBuffer _jitter;
    private string? _appliedDeviceId;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private CaptureFormatConverter? _converter;

    public MicrophoneCaptureService(MicJitterBuffer jitter, SettingsService settings, AudioDeviceService devices, ILogger<MicrophoneCaptureService> logger)
        : base(settings, devices, logger)
    {
        _jitter = jitter;
        _appliedDeviceId = settings.Current.MicrophoneDeviceId;
    }

    protected override DataFlow Flow => DataFlow.Capture;

    protected override EndpointStatus Open(AppSettings settings)
    {
        _appliedDeviceId = settings.MicrophoneDeviceId;
        _enumerator = new MMDeviceEnumerator();

        if (settings.MicrophoneDeviceId is { } id)
        {
            try { _device = _enumerator.GetDevice(id); }
            catch { _device = null; }
            if (_device is null || _device.State != DeviceState.Active)
            {
                // Don't silently switch to another mic: wait for this one (USB reconnect keeps the ID).
                _device?.Dispose();
                _device = null;
                return new EndpointStatus(false, null, "Selected microphone is not connected — waiting for it");
            }
        }
        else
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
                return new EndpointStatus(false, null, "No microphone found");
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        OpenDeviceId = _device.ID;
        OpenedDefaultDevice = settings.MicrophoneDeviceId is null;

        if (AudioSetupRules.IsVirtualCable(_device.FriendlyName))
        {
            // Recording the cable we play into would feed Discord's audio back into itself.
            return new EndpointStatus(false, _device.FriendlyName,
                "The microphone is the virtual cable (feedback loop) — choose your real microphone");
        }

        _capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 20);
        _converter = new CaptureFormatConverter(CaptureFormat.FromWaveFormat(_capture.WaveFormat));
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _jitter.RequestReset();
        _capture.StartRecording();

        return new EndpointStatus(true, _device.FriendlyName, $"Capturing ({_converter.InputFormat})");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var converter = _converter;
        if (converter is null || e.BytesRecorded <= 0) return;
        int frames = e.BytesRecorded / converter.InputFormat.BlockAlign;
        converter.Process(e.Buffer.AsSpan(0, frames * converter.InputFormat.BlockAlign), frames, _jitter.Write);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (ReferenceEquals(sender, _capture)) ReportFault(e.Exception);
    }

    protected override void Close()
    {
        var capture = _capture;
        _capture = null;
        _converter = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { /* device gone */ }
            capture.Dispose();
        }
        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    protected override bool NeedsRestart(AppSettings settings) => settings.MicrophoneDeviceId != _appliedDeviceId;
}
