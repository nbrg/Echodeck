namespace Echodeck.Core.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer ring of interleaved float samples.
/// <para>
/// Used between the microphone capture thread (producer) and the output render thread
/// (consumer). Neither side ever blocks or allocates. If the consumer stops reading, the
/// producer drops new audio (and counts it) instead of growing the buffer.
/// </para>
/// Positions are monotonically increasing 64-bit counters; the index into the array is
/// <c>position &amp; mask</c>. Only the producer writes <c>_write</c>, only the consumer writes
/// <c>_read</c>, and Volatile reads/writes publish the data between the two threads.
/// </summary>
public sealed class SpscAudioRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private readonly int _channels;
    private long _write;
    private long _read;
    private long _droppedSamples;

    public SpscAudioRing(int channels, int minCapacityFrames)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        int capacity = 1;
        while (capacity < minCapacityFrames * channels) capacity <<= 1;
        // A power-of-two capacity is always a multiple of an even channel count; for odd
        // counts (mono) every sample is a frame, so whole-frame writes stay aligned either way.
        _buffer = new float[capacity];
        _mask = capacity - 1;
        _channels = channels;
    }

    public int Channels => _channels;
    public int CapacitySamples => _buffer.Length;

    /// <summary>Samples currently readable. Safe to call from either thread (may be slightly stale).</summary>
    public int Count => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>Producer only. Writes as many whole frames as fit; returns samples written.</summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        long write = _write;
        long read = Volatile.Read(ref _read);
        int free = _buffer.Length - (int)(write - read);
        int n = Math.Min(samples.Length, free);
        n -= n % _channels;

        CopyIn(samples[..n], (int)(write & _mask));
        Volatile.Write(ref _write, write + n);

        if (n < samples.Length) Interlocked.Add(ref _droppedSamples, samples.Length - n);
        return n;
    }

    /// <summary>Consumer only. Reads up to <c>destination.Length</c> samples; returns samples read.</summary>
    public int Read(Span<float> destination)
    {
        long read = _read;
        long write = Volatile.Read(ref _write);
        int n = Math.Min(destination.Length, (int)(write - read));
        n -= n % _channels;

        int start = (int)(read & _mask);
        int first = Math.Min(n, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        if (n > first) _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);

        Volatile.Write(ref _read, read + n);
        return n;
    }

    /// <summary>Consumer only. Discards up to <paramref name="samples"/> samples; returns how many were discarded.</summary>
    public int Skip(int samples)
    {
        long read = _read;
        long write = Volatile.Read(ref _write);
        int n = Math.Min(samples, (int)(write - read));
        n -= n % _channels;
        Volatile.Write(ref _read, read + n);
        return n;
    }

    private void CopyIn(ReadOnlySpan<float> source, int start)
    {
        int first = Math.Min(source.Length, _buffer.Length - start);
        source[..first].CopyTo(_buffer.AsSpan(start));
        if (source.Length > first) source[first..].CopyTo(_buffer);
    }
}
