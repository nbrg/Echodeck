using Echodeck.Audio.Dsp;
using Echodeck.Core.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Echodeck.Audio.Replay;

/// <summary>
/// Decodes an audio file (WAV now; anything Media Foundation can read works too) into an
/// in-memory clip in the internal format (48 kHz stereo float), ready to mix or edit.
/// </summary>
public static class ClipFileLoader
{
    /// <summary>Refuse absurdly long files so a mistake can't eat gigabytes of RAM (10 min ≈ 230 MB).</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

    public static AudioClip Load(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.TotalTime > MaxDuration)
            throw new InvalidOperationException($"Clip is longer than {MaxDuration.TotalMinutes:F0} minutes.");

        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels != 2) provider = new ToStereoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != AudioFormat.Internal.SampleRate)
            provider = new WdlResamplingSampleProvider(provider, AudioFormat.Internal.SampleRate);

        int estimate = AudioFormat.Internal.SamplesFor(reader.TotalTime) + 4096;
        var samples = new List<float>(estimate);
        var chunk = new float[16_384];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            samples.AddRange(new ArraySegment<float>(chunk, 0, read - read % 2));

        return new AudioClip(samples.ToArray(), AudioFormat.Internal, File.GetCreationTime(path));
    }
}
