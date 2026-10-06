namespace Echodeck.Core.Audio;

/// <summary>
/// Lock-free peak accumulator. The audio thread calls <see cref="Process"/>, the UI polls
/// <see cref="ReadAndReset"/> at ~15 Hz. No allocations, no locks on the audio path.
/// </summary>
public sealed class PeakMeter
{
    private int _peakBits; // float stored as int bits so it can be updated with Interlocked

    public void Process(ReadOnlySpan<float> samples)
    {
        float max = 0f;
        foreach (float s in samples)
        {
            float a = Math.Abs(s);
            if (a > max) max = a;
        }
        if (max <= 0f) return;

        while (true)
        {
            int currentBits = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(currentBits) >= max) return;
            if (Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(max), currentBits) == currentBits)
                return;
        }
    }

    /// <summary>Returns the peak (0..1+, linear) since the previous call.</summary>
    public float ReadAndReset() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));
}
