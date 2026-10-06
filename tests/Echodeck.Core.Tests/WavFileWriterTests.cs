using System.Buffers.Binary;
using System.Text;
using Echodeck.Core.Audio;

namespace Echodeck.Core.Tests;

public class WavFileWriterTests
{
    [Fact]
    public void Pcm16_HeaderAndSamplesAreCorrect()
    {
        var samples = new[] { 0f, 1f, -1f, 0.5f, 2f, -2f }; // 3 stereo frames, includes out-of-range
        using var ms = new MemoryStream();
        WavFileWriter.Write(ms, samples, new AudioFormat(48_000, 2), WavSampleFormat.Pcm16);
        byte[] b = ms.ToArray();

        Assert.Equal(44 + 12, b.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal(36u + 12u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(b, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(b, 12, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20)));       // PCM
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(22)));       // channels
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24))); // rate
        Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(28)));// byte rate
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(32)));       // block align
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(34)));      // bits
        Assert.Equal("data", Encoding.ASCII.GetString(b, 36, 4));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));

        short S(int i) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(44 + i * 2));
        Assert.Equal(0, S(0));
        Assert.Equal(short.MaxValue, S(1));
        Assert.Equal(-short.MaxValue, S(2));
        Assert.Equal(16384, S(3));
        Assert.Equal(short.MaxValue, S(4));   // clamped
        Assert.Equal(-short.MaxValue, S(5));  // clamped
    }

    [Fact]
    public void Float32_RoundTripsExactly()
    {
        var samples = new[] { 0.123f, -0.456f };
        using var ms = new MemoryStream();
        WavFileWriter.Write(ms, samples, new AudioFormat(48_000, 1), WavSampleFormat.Float32);
        byte[] b = ms.ToArray();

        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(20))); // IEEE float
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(34)));
        Assert.Equal(0.123f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(44)));
        Assert.Equal(-0.456f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(48)));
    }

    [Fact]
    public void WriteToFile_LeavesNoTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "echodeck-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "clip.wav");
            WavFileWriter.Write(path, new float[48_000 * 2], AudioFormat.Internal);

            Assert.True(File.Exists(path));
            Assert.Equal(44 + 48_000 * 2 * 2, new FileInfo(path).Length);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
