using Echodeck.Core.Audio;

namespace Echodeck.Core.Mixing;

/// <summary>
/// Measures how loud you normally talk, in LUFS (same K-weighted scale as
/// <see cref="ClipPolish.MeasureLoudness"/>), from the live mic on the render thread.
/// <para>
/// The mic is cut into 400 ms blocks. Only blocks that sound like speech count (above −50 LUFS
/// and no more than 12 LU below your usual level, so breaths, keyboard and room noise are
/// ignored). They feed a slow average that follows your last minute or so of talking. Clips can
/// then be played at the same loudness as your voice. Discord processes the voice and the clips
/// identically, so friends hear them at the same level.
/// </para>
/// Allocation-free; <see cref="Process"/> must be called from one thread only.
/// </summary>
public sealed class VoiceLevelMeter
{
    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromSeconds(5);
    private const double AbsoluteGateLufs = -50;
    private const double RelativeGateLu = -12;
    private const double TimeConstantSeconds = 60;

    private readonly int _channels;
    private readonly int _blockFrames;
    private readonly ClipPolish.Biquad[] _shelf;
    private readonly ClipPolish.Biquad[] _highPass;
    private readonly int _minBlocks;
    private readonly double _blockSeconds;
    private double _blockPower;
    private int _blockFill;
    private int _speechBlocks;
    private double _average;   // mean square (power) of speech blocks
    private double _lufs = double.NaN;

    public VoiceLevelMeter(AudioFormat format)
    {
        _channels = format.Channels;
        _blockFrames = format.FramesFor(TimeSpan.FromMilliseconds(400));
        _blockSeconds = 0.4;
        _minBlocks = (int)Math.Ceiling(MinimumSpeech.TotalSeconds / _blockSeconds);
        _shelf = Enumerable.Range(0, _channels).Select(_ => ClipPolish.Biquad.HighShelf(format.SampleRate)).ToArray();
        _highPass = Enumerable.Range(0, _channels).Select(_ => ClipPolish.Biquad.HighPass(format.SampleRate)).ToArray();
    }

    /// <summary>Your typical speaking loudness (LUFS), or null until about 5 s of talking was heard.</summary>
    public double? Lufs
    {
        get
        {
            double v = Volatile.Read(ref _lufs);
            return double.IsNaN(v) ? null : v;
        }
    }

    /// <summary>Seconds of speech measured so far (for the UI).</summary>
    public double SpeechSeconds => Volatile.Read(ref _speechBlocks) * _blockSeconds;

    /// <summary>Feeds interleaved mic samples (before any gain).</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / _channels;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                double y = _highPass[c].Process(_shelf[c].Process(samples[f * _channels + c]));
                sum += y * y;
            }
            _blockPower += sum;
            if (++_blockFill == _blockFrames) CompleteBlock();
        }
    }

    private void CompleteBlock()
    {
        double z = _blockPower / _blockFrames;
        _blockPower = 0;
        _blockFill = 0;
        if (z <= 0) return;

        double block = ToLufs(z);
        if (block < AbsoluteGateLufs) return;
        if (_speechBlocks >= _minBlocks && block < ToLufs(_average) + RelativeGateLu) return;

        int n = _speechBlocks + 1;
        // Plain average to start with, then an exponential average over about a minute of speech.
        double weight = Math.Max(1.0 / n, _blockSeconds / TimeConstantSeconds);
        _average += (z - _average) * weight;
        Volatile.Write(ref _speechBlocks, n);
        if (n >= _minBlocks) Volatile.Write(ref _lufs, ToLufs(_average));
    }

    private static double ToLufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);
}

/// <summary>How loud to play a clip so it matches your voice.</summary>
public static class ClipLevel
{
    /// <summary>Assumed speaking level until your voice has been measured (a typical headset mic).</summary>
    public const double DefaultVoiceLufs = -24;

    public const double MaxBoostDb = 12;
    public const double MaxCutDb = -24;

    /// <summary>
    /// Gain (dB) that brings a clip of <paramref name="clipLufs"/> to your voice's loudness plus
    /// <paramref name="offsetDb"/>. 0 for silent clips. Limited to −24…+12 dB.
    /// </summary>
    public static double MatchGainDb(double? clipLufs, double? voiceLufs, double offsetDb)
    {
        if (clipLufs is null || !double.IsFinite(clipLufs.Value)) return 0;
        double target = (voiceLufs ?? DefaultVoiceLufs) + offsetDb;
        return Math.Clamp(target - clipLufs.Value, MaxCutDb, MaxBoostDb);
    }

    public static float ToLinear(double db) => (float)Math.Pow(10, db / 20);
}
