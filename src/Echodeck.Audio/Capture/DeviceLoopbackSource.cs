using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Echodeck.Audio.Capture;

/// <summary>
/// Classic WASAPI loopback of a whole output device (via NAudio's WasapiLoopbackCapture).
/// Used by both fallback methods:
/// <list type="bullet">
/// <item><see cref="CaptureMethod.DiscordOutputDevice"/>: the device Discord is playing to
/// (found through its audio session). Also captures any other app on that device.</item>
/// <item><see cref="CaptureMethod.SelectedDevice"/>: a device chosen in Settings, or the Windows
/// default output. Captures everything on that device.</item>
/// </list>
/// The device is opened by ID on the calling (MTA) thread rather than passed in as an MMDevice,
/// so no COM object ever crosses from the WPF UI thread's STA into the capture thread.
/// </summary>
public sealed class DeviceLoopbackSource : IDiscordAudioSource
{
    private readonly string? _deviceId;
    private readonly ILogger _logger;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private CaptureFormatConverter? _converter;
    private volatile bool _stopping;
    private int _faulted;

    /// <param name="deviceId">Endpoint ID, or null for the current default output device.</param>
    public DeviceLoopbackSource(CaptureMethod method, string? deviceId, ILogger logger)
    {
        if (method is not (CaptureMethod.DiscordOutputDevice or CaptureMethod.SelectedDevice))
            throw new ArgumentOutOfRangeException(nameof(method));
        Method = method;
        _deviceId = deviceId;
        _logger = logger;
        Description = deviceId ?? "Default output device";
    }

    public CaptureMethod Method { get; }
    public string Description { get; private set; }

    /// <summary>Endpoint ID actually opened (resolved from "default" when no ID was given).</summary>
    public string? DeviceId { get; private set; }

    public event AudioDataHandler? DataAvailable;
    public event EventHandler<Exception>? Faulted;

    public void Start()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _deviceId is null
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : _enumerator.GetDevice(_deviceId);
            if (_device.State != DeviceState.Active)
                throw new AudioCaptureException($"Output device '{_device.FriendlyName}' is not active ({_device.State}).", 0);

            DeviceId = _device.ID;
            Description = _device.FriendlyName;

            _capture = new WasapiLoopbackCapture(_device);
            _converter = new CaptureFormatConverter(CaptureFormat.FromWaveFormat(_capture.WaveFormat));
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            _logger.LogInformation("{Method} loopback started on '{Device}' ({Format})",
                Method, Description, _converter.InputFormat);
        }
        catch (Exception ex)
        {
            DisposeResources();
            throw ex as AudioCaptureException ?? new AudioCaptureException(
                $"Could not start loopback on '{Description}': {ex.Message}", ex.HResult, ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var converter = _converter;
        if (_stopping || converter is null || e.BytesRecorded <= 0) return;
        int frames = e.BytesRecorded / converter.InputFormat.BlockAlign;
        converter.Process(e.Buffer.AsSpan(0, frames * converter.InputFormat.BlockAlign), frames,
            samples => DataAvailable?.Invoke(samples));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;
        var ex = e.Exception ?? new AudioCaptureException($"Loopback on '{Description}' stopped unexpectedly.", 0);
        _logger.LogWarning(ex, "Loopback capture stopped on '{Device}'", Description);
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            Faulted?.Invoke(this, ex);
    }

    public void Dispose()
    {
        _stopping = true;
        DataAvailable = null;
        Faulted = null;
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { /* device may already be gone */ }
            _capture.Dispose();
            _capture = null;
        }
        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
