namespace Echodeck.Core.Audio;

/// <summary>
/// Fixed-size circular buffer of interleaved float audio holding the most recent N seconds.
/// <para>
/// Memory is allocated once (48 kHz × 2 ch × 4 bytes ≈ 384 KB per second, so 60 s ≈ 23 MB) and
/// never grows: writes overwrite the oldest audio. <see cref="Snapshot"/> copies out the tail
/// without stopping the writer, which is what lets Instant Replay "freeze" the last N seconds
/// while recording continues.
/// </para>
/// <para>
/// Thread-safety: one capture thread writes, any thread may snapshot. A plain lock is used —
/// writes happen roughly every 10 ms and copy a few KB, so contention is negligible.
/// </para>
/// </summary>
public sealed class RollingAudioBuffer
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 300;

    private readonly object _lock = new();
    private float[] _samples;
    private int _writePos;          // next sample index to write
    private long _availableSamples; // how many valid samples are in the buffer (<= capacity)
    private long _totalFramesWritten;

    public RollingAudioBuffer(AudioFormat format, TimeSpan duration)
    {
        if (format.Channels <= 0 || format.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(format));
        Format = format;
        _samples = new float[CapacitySamplesFor(format, duration)];
    }

    public AudioFormat Format { get; }

    public TimeSpan Capacity
    {
        get { lock (_lock) return Format.DurationOfSamples(_samples.Length); }
    }

    /// <summary>How much audio is currently held (grows to <see cref="Capacity"/> then stays there).</summary>
    public TimeSpan Available
    {
        get { lock (_lock) return Format.DurationOfSamples(_availableSamples); }
    }

    /// <summary>Monotonic count of frames ever written (useful for diagnostics / timeline checks).</summary>
    public long TotalFramesWritten
    {
        get { lock (_lock) return _totalFramesWritten; }
    }

    /// <summary>Appends interleaved samples. Length must be a whole number of frames.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.Length % Format.Channels != 0)
            throw new ArgumentException("Sample count must be a multiple of the channel count.", nameof(samples));
        if (samples.IsEmpty) return;

        lock (_lock)
        {
            _totalFramesWritten += samples.Length / Format.Channels;

            // If a single write is larger than the whole buffer, only its tail can survive.
            if (samples.Length >= _samples.Length)
                samples = samples[^_samples.Length..];

            int first = Math.Min(samples.Length, _samples.Length - _writePos);
            samples[..first].CopyTo(_samples.AsSpan(_writePos));
            int second = samples.Length - first;
            if (second > 0)
                samples[first..].CopyTo(_samples.AsSpan(0, second));

            _writePos = (_writePos + samples.Length) % _samples.Length;
            _availableSamples = Math.Min(_samples.Length, _availableSamples + samples.Length);
        }
    }

    /// <summary>Appends digital silence (used to keep the timeline accurate when the source delivers nothing).</summary>
    public void WriteSilence(int frames)
    {
        if (frames <= 0) return;
        lock (_lock)
        {
            long samples = (long)frames * Format.Channels;
            _totalFramesWritten += frames;
            int count = (int)Math.Min(samples, _samples.Length);

            int first = Math.Min(count, _samples.Length - _writePos);
            Array.Clear(_samples, _writePos, first);
            if (count > first)
                Array.Clear(_samples, 0, count - first);

            _writePos = (int)((_writePos + samples) % _samples.Length);
            _availableSamples = Math.Min(_samples.Length, _availableSamples + samples);
        }
    }

    /// <summary>
    /// Copies the most recent <paramref name="duration"/> of audio (or less, if the buffer has not
    /// filled yet) into a new array, oldest sample first. The buffer keeps recording.
    /// </summary>
    public float[] Snapshot(TimeSpan duration)
    {
        lock (_lock)
        {
            long wanted = Math.Max(0, (long)Format.FramesFor(duration) * Format.Channels);
            return SnapshotUnlocked((int)Math.Min(wanted, _availableSamples));
        }
    }

    /// <summary>
    /// Changes the buffer length, preserving as much of the most recent audio as fits.
    /// Allocates a new array once; the old one becomes garbage.
    /// </summary>
    public void Resize(TimeSpan duration)
    {
        int newCapacity = CapacitySamplesFor(Format, duration);
        lock (_lock)
        {
            if (newCapacity == _samples.Length) return;

            float[] keep = SnapshotUnlocked(Math.Min(newCapacity, (int)_availableSamples));
            _samples = new float[newCapacity];
            keep.CopyTo(_samples, 0);
            _writePos = keep.Length % newCapacity;
            _availableSamples = keep.Length;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_samples);
            _writePos = 0;
            _availableSamples = 0;
        }
    }

    private float[] SnapshotUnlocked(int count)
    {
        var result = new float[count];
        if (count == 0) return result;

        int start = _writePos - count;
        if (start >= 0)
        {
            Array.Copy(_samples, start, result, 0, count);
        }
        else
        {
            // Wrapped: tail of the array first, then the head.
            int tailLength = -start;
            Array.Copy(_samples, _samples.Length - tailLength, result, 0, tailLength);
            Array.Copy(_samples, 0, result, tailLength, count - tailLength);
        }
        return result;
    }

    private static int CapacitySamplesFor(AudioFormat format, TimeSpan duration)
    {
        double seconds = Math.Clamp(duration.TotalSeconds, MinSeconds, MaxSeconds);
        return format.FramesFor(TimeSpan.FromSeconds(seconds)) * format.Channels;
    }
}
