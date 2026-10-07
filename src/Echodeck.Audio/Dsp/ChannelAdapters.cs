using NAudio.Wave;

namespace Echodeck.Audio.Dsp;

/// <summary>Converts any channel count to stereo: mono is duplicated, extra channels are dropped.</summary>
public sealed class ToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _inChannels;
    private float[] _scratch = Array.Empty<float>();

    public ToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _inChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (_scratch.Length < frames * _inChannels) _scratch = new float[frames * _inChannels];
        int read = _source.Read(_scratch, 0, frames * _inChannels) / _inChannels;
        for (int f = 0; f < read; f++)
        {
            float left = _scratch[f * _inChannels];
            float right = _inChannels > 1 ? _scratch[f * _inChannels + 1] : left;
            buffer[offset + f * 2] = left;
            buffer[offset + f * 2 + 1] = right;
        }
        return read * 2;
    }
}

/// <summary>
/// Converts stereo to the channel count an output device wants: mono = average of L/R,
/// more than two = L/R on the front pair and silence elsewhere.
/// </summary>
public sealed class FromStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _outChannels;
    private float[] _scratch = Array.Empty<float>();

    public FromStereoSampleProvider(ISampleProvider source, int outChannels)
    {
        if (source.WaveFormat.Channels != 2) throw new ArgumentException("Source must be stereo.", nameof(source));
        _source = source;
        _outChannels = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outChannels;
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
        int read = _source.Read(_scratch, 0, frames * 2) / 2;
        for (int f = 0; f < read; f++)
        {
            float l = _scratch[f * 2], r = _scratch[f * 2 + 1];
            int o = offset + f * _outChannels;
            if (_outChannels == 1)
            {
                buffer[o] = (l + r) * 0.5f;
                continue;
            }
            buffer[o] = l;
            buffer[o + 1] = r;
            for (int c = 2; c < _outChannels; c++) buffer[o + c] = 0f;
        }
        return read * _outChannels;
    }
}
