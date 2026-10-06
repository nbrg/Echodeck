using System.Buffers.Binary;
using Echodeck.Core.Audio;
using NAudio.Dsp;
using NAudio.Wave;

namespace Echodeck.Audio.Capture;

public enum SampleEncoding
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>Describes raw capture data as delivered by WASAPI.</summary>
public readonly record struct CaptureFormat(SampleEncoding Encoding, int SampleRate, int Channels)
{
    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {Encoding}";

    /// <summary>Maps an NAudio WaveFormat (incl. WAVE_FORMAT_EXTENSIBLE) to a CaptureFormat.</summary>
    public static CaptureFormat FromWaveFormat(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format is WaveFormatExtensible ext)
            isFloat = ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;

        SampleEncoding encoding = (isFloat, format.BitsPerSample) switch
        {
            (true, 32) => SampleEncoding.Float32,
            (false, 16) => SampleEncoding.Pcm16,
            (false, 24) => SampleEncoding.Pcm24,
            (false, 32) => SampleEncoding.Pcm32,
            _ => throw new NotSupportedException($"Unsupported capture format: {format}"),
        };
        return new CaptureFormat(encoding, format.SampleRate, format.Channels);
    }
}

public delegate void AudioDataHandler(ReadOnlySpan<float> samples);

/// <summary>
/// Converts whatever a capture source delivers into Echodeck's internal format
/// (<see cref="AudioFormat.Internal"/>: 48 kHz, stereo, float).
/// <list type="bullet">
/// <item>Decoding: float32 / PCM16 / PCM24 / PCM32 → float.</item>
/// <item>Channels: mono is duplicated; surround keeps front L/R and folds in centre (where voice
/// usually lives on upmixed output).</item>
/// <item>Sample rate: only resampled when the source isn't already 48 kHz (process loopback is
/// requested at 48 kHz, so normally this is skipped entirely).</item>
/// </list>
/// Scratch buffers grow to the largest packet seen and are then reused: no per-packet allocation.
/// Not thread-safe; each capture source owns one instance and calls it from its capture thread.
/// </summary>
public sealed class CaptureFormatConverter
{
    private const int OutChannels = 2;
    private readonly CaptureFormat _input;
    private readonly WdlResampler? _resampler;
    private readonly double _rateRatio;
    private float[] _stereo = Array.Empty<float>();
    private float[] _resampled = Array.Empty<float>();

    public CaptureFormatConverter(CaptureFormat input)
    {
        if (input.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(input));
        _input = input;
        if (input.SampleRate != AudioFormat.Internal.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);   // interpolate, 2 filter passes, no sinc
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);         // input-driven: we push whatever arrives
            _resampler.SetRates(input.SampleRate, AudioFormat.Internal.SampleRate);
            _rateRatio = (double)AudioFormat.Internal.SampleRate / input.SampleRate;
        }
    }

    public CaptureFormat InputFormat => _input;

    /// <summary>Converts <paramref name="frames"/> frames of raw data and passes the result to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<byte> data, int frames, AudioDataHandler output)
    {
        if (frames <= 0) return;
        EnsureCapacity(ref _stereo, frames * OutChannels);
        Span<float> stereo = _stereo.AsSpan(0, frames * OutChannels);

        int channels = _input.Channels;
        int bytesPerSample = _input.BytesPerSample;
        int blockAlign = _input.BlockAlign;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<byte> frame = data.Slice(f * blockAlign, blockAlign);
            float left = Decode(frame, 0, bytesPerSample);
            float right;
            if (channels == 1)
            {
                right = left;
            }
            else
            {
                right = Decode(frame, 1, bytesPerSample);
                if (channels >= 3)
                {
                    float centre = Decode(frame, 2, bytesPerSample) * 0.7071f;
                    left += centre;
                    right += centre;
                }
            }
            stereo[f * 2] = left;
            stereo[f * 2 + 1] = right;
        }

        Emit(frames, output);
    }

    /// <summary>Emits <paramref name="frames"/> frames of silence (WASAPI "silent" packets).</summary>
    public void ProcessSilence(int frames, AudioDataHandler output)
    {
        if (frames <= 0) return;
        EnsureCapacity(ref _stereo, frames * OutChannels);
        _stereo.AsSpan(0, frames * OutChannels).Clear();
        Emit(frames, output);
    }

    private void Emit(int frames, AudioDataHandler output)
    {
        if (_resampler is null)
        {
            output(_stereo.AsSpan(0, frames * OutChannels));
            return;
        }

        int wanted = _resampler.ResamplePrepare(frames, OutChannels, out float[] inBuffer, out int inOffset);
        Array.Copy(_stereo, 0, inBuffer, inOffset, Math.Min(wanted, frames) * OutChannels);

        int maxOut = (int)Math.Ceiling(frames * _rateRatio) + 32;
        EnsureCapacity(ref _resampled, maxOut * OutChannels);
        int produced = _resampler.ResampleOut(_resampled, 0, Math.Min(wanted, frames), maxOut, OutChannels);
        if (produced > 0)
            output(_resampled.AsSpan(0, produced * OutChannels));
    }

    private float Decode(ReadOnlySpan<byte> frame, int channel, int bytesPerSample)
    {
        ReadOnlySpan<byte> s = frame.Slice(channel * bytesPerSample, bytesPerSample);
        return _input.Encoding switch
        {
            SampleEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
            SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
            SampleEncoding.Pcm24 => ((s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8) / 8388608f,
            SampleEncoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            _ => 0f,
        };
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new float[length];
    }
}
