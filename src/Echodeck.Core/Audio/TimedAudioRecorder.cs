namespace Echodeck.Core.Audio;

/// <summary>
/// A <see cref="RollingAudioBuffer"/> kept aligned with wall-clock time.
/// <para>
/// Used for "my side" of the conversation: everything Echodeck sends to Discord (your mic plus
/// clips you play), written from the output render thread. If the output stops (cable unplugged,
/// device restart), the gap is filled with silence <b>at the moment audio resumes</b>, and again
/// before every snapshot. That keeps this buffer in step with the Discord-incoming buffer, so the
/// two can be mixed into one replay.
/// </para>
/// Thread-safe: one writer, any number of snapshot callers.
/// </summary>
public sealed class TimedAudioRecorder
{
    private readonly object _lock = new();
    private readonly RollingAudioBuffer _buffer;
    private readonly CaptureTimeline _timeline;
    private readonly Func<TimeSpan> _clock;

    /// <param name="clock">Monotonic time source (Stopwatch elapsed in production; fake in tests).</param>
    public TimedAudioRecorder(AudioFormat format, TimeSpan capacity, Func<TimeSpan> clock)
    {
        _buffer = new RollingAudioBuffer(format, capacity);
        _timeline = CaptureTimeline.CreateDefault(format.SampleRate);
        _clock = clock;
        _timeline.Reset(clock());
    }

    public AudioFormat Format => _buffer.Format;
    public TimeSpan Capacity => _buffer.Capacity;

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            TimeSpan now = _clock();
            PadUnlocked(now); // a gap since the last write belongs *before* this audio
            _buffer.Write(samples);
            _timeline.OnFramesWritten(samples.Length / _buffer.Format.Channels, now);
        }
    }

    /// <summary>The most recent <paramref name="duration"/> of audio, ending now (silence-padded).</summary>
    public float[] Snapshot(TimeSpan duration)
    {
        lock (_lock)
        {
            PadUnlocked(_clock());
            return _buffer.Snapshot(duration);
        }
    }

    public void Resize(TimeSpan capacity)
    {
        lock (_lock) _buffer.Resize(capacity);
    }

    private void PadUnlocked(TimeSpan now)
    {
        int silence = _timeline.SilenceFramesNeeded(now);
        if (silence <= 0) return;
        _buffer.WriteSilence(silence);
        _timeline.OnFramesWritten(silence, now);
    }
}
