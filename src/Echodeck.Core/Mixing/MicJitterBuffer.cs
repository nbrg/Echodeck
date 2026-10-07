using Echodeck.Core.Audio;

namespace Echodeck.Core.Mixing;

/// <summary>
/// Bridges the microphone (one device clock) to the virtual-cable output (another device clock)
/// with a small, <b>bounded</b> latency.
/// <para>
/// Two sound cards never run at exactly the same rate. If the mic is 0.01 % faster than the
/// output, an unmanaged buffer gains ~0.4 s of delay over an hour; if slower, it runs dry and
/// crackles. This class keeps the backlog near <see cref="TargetFrames"/>:
/// <list type="bullet">
/// <item>Start / after an underrun: output silence until the target backlog has built up ("priming").</item>
/// <item>Backlog a little high: drop a single frame per render call (inaudible on voice).</item>
/// <item>Backlog far too high (e.g. after the output stalled): jump straight back to the target.</item>
/// </list>
/// </para>
/// Producer: <see cref="Write"/> on the mic thread. Consumer: <see cref="Read"/> on the output thread.
/// </summary>
public sealed class MicJitterBuffer
{
    private readonly SpscAudioRing _ring;
    private readonly int _channels;
    private bool _primed;
    private volatile bool _resetRequested;
    private long _underruns;
    private long _correctedFrames;

    public MicJitterBuffer(AudioFormat format, TimeSpan target, TimeSpan softWindow, TimeSpan hardLimit)
    {
        _channels = format.Channels;
        TargetFrames = format.FramesFor(target);
        SoftLimitFrames = TargetFrames + format.FramesFor(softWindow);
        HardLimitFrames = format.FramesFor(hardLimit);
        if (HardLimitFrames <= SoftLimitFrames) throw new ArgumentException("hardLimit must exceed target + softWindow.");
        _ring = new SpscAudioRing(format.Channels, format.FramesFor(TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>20 ms target, ±20 ms tolerance, hard reset beyond 150 ms.</summary>
    public static MicJitterBuffer CreateDefault(AudioFormat format) =>
        new(format, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(150));

    public int TargetFrames { get; }
    public int SoftLimitFrames { get; }
    public int HardLimitFrames { get; }

    public long Underruns => Interlocked.Read(ref _underruns);
    public long CorrectedFrames => Interlocked.Read(ref _correctedFrames);
    public int BufferedFrames => _ring.Count / _channels;
    public long DroppedOnOverflowFrames => _ring.DroppedSamples / _channels;

    /// <summary>Producer side: mic audio in the internal format.</summary>
    public void Write(ReadOnlySpan<float> samples) => _ring.Write(samples);

    /// <summary>Asks the consumer to discard everything and re-prime (e.g. the mic device changed).</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>
    /// Consumer side: always fills <paramref name="destination"/> completely (silence where no mic
    /// audio is available). Returns the number of frames that contain real mic audio.
    /// </summary>
    public int Read(Span<float> destination)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            _ring.Skip(int.MaxValue);
            _primed = false;
        }

        int need = destination.Length / _channels;
        int available = _ring.Count / _channels;

        if (!_primed)
        {
            if (available < need + TargetFrames)
            {
                destination.Clear();
                return 0;
            }
            _primed = true;
        }

        if (available < need)
        {
            // Underrun: play what we have, then rebuild the cushion.
            int got = _ring.Read(destination) / _channels;
            destination[(got * _channels)..].Clear();
            _primed = false;
            Interlocked.Increment(ref _underruns);
            return got;
        }

        int leftover = available - need;
        if (leftover > HardLimitFrames)
        {
            int skip = leftover - TargetFrames;
            _ring.Skip(skip * _channels);
            Interlocked.Add(ref _correctedFrames, skip);
        }
        else if (leftover > SoftLimitFrames)
        {
            _ring.Skip(_channels);
            Interlocked.Increment(ref _correctedFrames);
        }

        return _ring.Read(destination) / _channels;
    }
}
