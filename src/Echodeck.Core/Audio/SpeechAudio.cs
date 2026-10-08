using System.Text.RegularExpressions;

namespace Echodeck.Core.Audio;

/// <summary>
/// Helpers for local speech recognition: converting clips to what Whisper expects (16 kHz mono)
/// and cleaning up what it returns.
/// </summary>
public static partial class SpeechAudio
{
    public const int SampleRate = 16_000;

    /// <summary>Longest stretch transcribed per clip (long imports, e.g. songs, are cut here).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    private const int Taps = 63;
    private static readonly Dictionary<int, float[]> FilterCache = new();

    /// <summary>
    /// Mixes to mono and resamples to 16 kHz with a windowed-sinc low-pass (so 8–24 kHz content
    /// doesn't fold back into the speech band). Only the first <see cref="MaxDuration"/> is kept.
    /// </summary>
    public static float[] ToWhisperInput(AudioClip clip)
    {
        int channels = clip.Format.Channels;
        int rate = clip.Format.SampleRate;
        int frames = Math.Min(clip.FrameCount, clip.Format.FramesFor(MaxDuration));
        if (frames == 0) return Array.Empty<float>();

        var mono = new float[frames];
        var src = clip.Samples;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += src[f * channels + c];
            mono[f] = sum / channels;
        }
        if (rate == SampleRate) return mono;

        double step = (double)rate / SampleRate;
        int outLength = (int)(frames / step);
        var output = new float[outLength];
        float[] kernel = Kernel(rate);
        int half = Taps / 2;
        for (int i = 0; i < outLength; i++)
        {
            int center = (int)Math.Round(i * step);
            double acc = 0;
            for (int k = 0; k < Taps; k++)
            {
                int idx = center + k - half;
                if ((uint)idx < (uint)frames) acc += mono[idx] * kernel[k];
            }
            output[i] = (float)acc;
        }
        return output;
    }

    /// <summary>Low-pass at 7.2 kHz (just under the new Nyquist of 8 kHz), Blackman window, unity gain.</summary>
    private static float[] Kernel(int rate)
    {
        lock (FilterCache)
        {
            if (FilterCache.TryGetValue(rate, out var cached)) return cached;
            double cutoff = 7200.0 / rate;
            var k = new double[Taps];
            int half = Taps / 2;
            for (int n = 0; n < Taps; n++)
            {
                int m = n - half;
                double sinc = m == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * m) / (Math.PI * m);
                double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (Taps - 1));
                k[n] = sinc * window;
            }
            double sum = k.Sum();
            var result = k.Select(v => (float)(v / sum)).ToArray();
            FilterCache[rate] = result;
            return result;
        }
    }

    /// <summary>
    /// Joins recognised segments and removes Whisper's non-speech markers ("[BLANK_AUDIO]",
    /// "(music)", "*laughs*") and the stock phrases it invents for silence.
    /// Returns "" when nothing real was said.
    /// </summary>
    public static string CleanTranscript(IEnumerable<string> segments)
    {
        string text = string.Join(" ", segments.Select(s => s.Trim()));
        text = Markers().Replace(text, " ");
        text = Spaces().Replace(text, " ").Trim();
        string bare = text.Trim(' ', '.', '!', '?', ',', '-').ToLowerInvariant();
        if (bare.Length == 0 || SilencePhrases.Contains(bare)) return "";
        return text.Length > 500 ? text[..500].TrimEnd() + "…" : text;
    }

    // What Whisper commonly "hears" in silence or noise (from subtitles in its training data).
    private static readonly HashSet<string> SilencePhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "you", "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching",
        "please subscribe", "bye", "okay", "uh", "um", "hmm", "mm", "oh",
    };

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪+")]
    private static partial Regex Markers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
