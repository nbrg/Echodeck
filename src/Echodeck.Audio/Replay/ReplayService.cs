using Echodeck.Audio.Capture;
using Echodeck.Core.Audio;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Echodeck.Audio.Replay;

public sealed record SavedClipInfo(string FilePath, string Name, DateTime CreatedAt, TimeSpan Duration);

/// <summary>
/// Instant-replay operations on top of the rolling buffer: freeze the last N seconds into an
/// in-memory <see cref="AudioClip"/> (recording continues), and persist clips as WAV.
/// Phase 3 adds the editor/trim path, Phase 4 moves the clip list into the soundboard library.
/// </summary>
public sealed class ReplayService
{
    private readonly RollingAudioBuffer _buffer;
    private readonly DiscordCaptureService _capture;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<ReplayService> _logger;

    public ReplayService(RollingAudioBuffer buffer, DiscordCaptureService capture, SettingsService settings, AppPaths paths, ILogger<ReplayService> logger)
    {
        _buffer = buffer;
        _capture = capture;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Copies the most recent <paramref name="duration"/> out of the buffer. Takes well under a
    /// millisecond for 30 s of audio and never interrupts recording.
    /// </summary>
    public AudioClip CaptureLast(TimeSpan duration)
    {
        _capture.SyncTimeline(); // include trailing silence up to "now"
        var samples = _buffer.Snapshot(duration);
        var clip = new AudioClip(samples, _buffer.Format, DateTimeOffset.Now);
        _logger.LogInformation("Replay captured: requested {Requested:F1}s, got {Actual:F2}s", duration.TotalSeconds, clip.Duration.TotalSeconds);
        return clip;
    }

    /// <summary>Writes the clip to the clips folder (off the UI thread) and returns its info.</summary>
    public Task<SavedClipInfo> SaveAsync(AudioClip clip, string? name = null)
    {
        WavSampleFormat format = _settings.Current.ClipFileFormat;
        return Task.Run(() =>
        {
            Directory.CreateDirectory(_paths.ClipsDirectory);
            string baseName = SanitizeFileName(name ?? $"Replay {clip.CapturedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}");
            string path = UniquePath(_paths.ClipsDirectory, baseName, ".wav");
            WavFileWriter.Write(path, clip.Samples, clip.Format, format);
            _logger.LogInformation("Clip saved: {Path} ({Duration:F2}s)", path, clip.Duration.TotalSeconds);
            return new SavedClipInfo(path, Path.GetFileNameWithoutExtension(path), File.GetCreationTime(path), clip.Duration);
        });
    }

    /// <summary>Lists saved WAV clips, newest first.</summary>
    public IReadOnlyList<SavedClipInfo> GetSavedClips()
    {
        if (!Directory.Exists(_paths.ClipsDirectory)) return Array.Empty<SavedClipInfo>();
        var result = new List<SavedClipInfo>();
        foreach (string path in Directory.EnumerateFiles(_paths.ClipsDirectory, "*.wav"))
        {
            TimeSpan duration = TimeSpan.Zero;
            try
            {
                using var reader = new WaveFileReader(path);
                duration = reader.TotalTime;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Unreadable clip {Path}: {Message}", path, ex.Message);
            }
            result.Add(new SavedClipInfo(path, Path.GetFileNameWithoutExtension(path), File.GetCreationTime(path), duration));
        }
        return result.OrderByDescending(c => c.CreatedAt).ToList();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Clip" : cleaned;
    }

    private static string UniquePath(string directory, string baseName, string extension)
    {
        string path = Path.Combine(directory, baseName + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{baseName} ({i}){extension}");
        return path;
    }
}
