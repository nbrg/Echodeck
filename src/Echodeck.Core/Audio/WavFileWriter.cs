using System.Buffers.Binary;

namespace Echodeck.Core.Audio;

public enum WavSampleFormat
{
    /// <summary>16-bit PCM: half the size, plays everywhere, plenty for Discord voice.</summary>
    Pcm16,
    /// <summary>32-bit IEEE float: lossless copy of the internal format.</summary>
    Float32,
}

/// <summary>
/// Minimal, dependency-free RIFF/WAVE writer. Files are written to a temporary name and then
/// moved into place, so a crash mid-write never leaves a truncated clip behind.
/// </summary>
public static class WavFileWriter
{
    public static void Write(string path, ReadOnlySpan<float> samples, AudioFormat format, WavSampleFormat sampleFormat = WavSampleFormat.Pcm16)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                Write(stream, samples, format, sampleFormat);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, AudioFormat format, WavSampleFormat sampleFormat = WavSampleFormat.Pcm16)
    {
        int bytesPerSample = sampleFormat == WavSampleFormat.Pcm16 ? 2 : 4;
        ushort formatTag = sampleFormat == WavSampleFormat.Pcm16 ? (ushort)1 : (ushort)3;
        long dataBytes = (long)samples.Length * bytesPerSample;
        if (dataBytes > uint.MaxValue - 44) throw new ArgumentException("Clip too large for a WAV file.");

        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);                 // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(format.SampleRate * format.Channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(format.Channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)(bytesPerSample * 8));
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        stream.Write(header);

        // Convert in chunks to keep memory flat regardless of clip length.
        const int chunkSamples = 8192;
        byte[] chunk = new byte[chunkSamples * bytesPerSample];
        for (int offset = 0; offset < samples.Length; offset += chunkSamples)
        {
            var slice = samples.Slice(offset, Math.Min(chunkSamples, samples.Length - offset));
            int bytes = 0;
            if (sampleFormat == WavSampleFormat.Pcm16)
            {
                foreach (float s in slice)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(bytes), FloatToPcm16(s));
                    bytes += 2;
                }
            }
            else
            {
                foreach (float s in slice)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(chunk.AsSpan(bytes), s);
                    bytes += 4;
                }
            }
            stream.Write(chunk, 0, bytes);
        }
    }

    internal static short FloatToPcm16(float sample)
    {
        float clamped = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
