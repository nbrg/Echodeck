using Echodeck.Core.Mixing;

namespace Echodeck.Core.Audio;

public static class AudioMixdown
{
    /// <summary>
    /// Sums two interleaved buffers of the same format that both <b>end at the same moment</b>
    /// (e.g. "the last 10 s" of two recordings). If one is shorter — a buffer that hasn't filled
    /// yet — it is placed at the end. The sum passes through a limiter so loud overlaps never clip.
    /// </summary>
    public static float[] SumEndAligned(float[] a, float[] b, AudioFormat format)
    {
        int length = Math.Max(a.Length, b.Length);
        length -= length % format.Channels;
        var result = new float[length];
        a.AsSpan(Math.Max(0, a.Length - length)).CopyTo(result.AsSpan(length - Math.Min(a.Length, length)));

        int offset = length - Math.Min(b.Length, length);
        var bTail = b.AsSpan(Math.Max(0, b.Length - length));
        for (int i = 0; i < bTail.Length; i++) result[offset + i] += bTail[i];

        new SoftLimiter(format.SampleRate, format.Channels).Process(result);
        return result;
    }
}
