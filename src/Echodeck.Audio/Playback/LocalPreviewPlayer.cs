using Echodeck.Core.Audio;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Echodeck.Audio.Playback;

/// <summary>
/// "Preview locally": plays a clip on the user's headphones only. It never goes to the virtual
/// cable, so friends never hear previews.
/// <para>
/// Feedback safety: this audio is rendered by Echodeck's own process, so per-process Discord
/// capture can never record it. (Only the whole-device fallback could, if previews play on the
/// same device being captured — documented in ARCHITECTURE.md.)
/// </para>
/// One preview at a time; starting a new one stops the previous. Playback objects are created on
/// a thread-pool (MTA) thread so no WASAPI object lives on the WPF UI thread.
/// </summary>
public sealed class LocalPreviewPlayer : IDisposable
{
    private readonly ILogger<LocalPreviewPlayer> _logger;
    private readonly object _lock = new();
    private WasapiOut? _output;
    private IDisposable? _sourceToDispose;
    private MMDevice? _device;
    private MMDeviceEnumerator? _enumerator;

    public LocalPreviewPlayer(ILogger<LocalPreviewPlayer> logger)
    {
        _logger = logger;
    }

    public bool IsPlaying
    {
        get { lock (_lock) return _output?.PlaybackState == PlaybackState.Playing; }
    }

    /// <summary>Raised when playback ends or fails (on a background thread).</summary>
    public event EventHandler? PlaybackEnded;

    public Task PlayFileAsync(string path, string? deviceId) => Task.Run(() =>
    {
        var reader = new AudioFileReader(path);
        Play(reader, reader, deviceId, Path.GetFileName(path));
    });

    public Task PlayClipAsync(AudioClip clip, string? deviceId) => Task.Run(() =>
    {
        var provider = new FloatArraySampleProvider(clip.Samples, WaveFormat.CreateIeeeFloatWaveFormat(clip.Format.SampleRate, clip.Format.Channels));
        Play(provider, null, deviceId, $"clip {clip.Duration.TotalSeconds:F1}s");
    });

    public void Stop()
    {
        lock (_lock) StopUnlocked();
    }

    private void Play(ISampleProvider provider, IDisposable? owner, string? deviceId, string what)
    {
        lock (_lock)
        {
            StopUnlocked();
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _device = TryGetDevice(_enumerator, deviceId) ?? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                // Shared mode, event-driven, 60 ms latency. NAudio converts sample rate/channels to
                // the device mix format when needed.
                var output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 60);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(provider);
                output.Play();
                _output = output;
                _sourceToDispose = owner;
                _logger.LogInformation("Local preview: {What} on '{Device}'", what, _device.FriendlyName);
            }
            catch (Exception ex)
            {
                owner?.Dispose();
                StopUnlocked();
                _logger.LogError(ex, "Local preview failed for {What}", what);
                throw;
            }
        }
    }

    private MMDevice? TryGetDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (deviceId is null) return null;
        try
        {
            var device = enumerator.GetDevice(deviceId);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Preview device {Id} unavailable ({Message}); using default output", deviceId, ex.Message);
        }
        return null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) _logger.LogWarning(e.Exception, "Local preview stopped with error");

        // This runs on NAudio's playback thread. Taking _lock here could deadlock against Stop(),
        // which holds _lock while joining this very thread — so clean up on the thread pool instead.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lock)
            {
                if (ReferenceEquals(sender, _output)) StopUnlocked();
            }
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void StopUnlocked()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { /* device gone */ }
            _output.Dispose();
            _output = null;
        }
        _sourceToDispose?.Dispose();
        _sourceToDispose = null;
        _device?.Dispose();
        _device = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    public void Dispose() => Stop();

    /// <summary>Plays an in-memory float array once.</summary>
    private sealed class FloatArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public FloatArraySampleProvider(float[] samples, WaveFormat format)
        {
            _samples = samples;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _samples.Length - _position);
            if (n <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
