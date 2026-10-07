using Echodeck.Audio.Capture;
using Echodeck.Audio.Mixing;
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
    private readonly AudioMixerService _mixer;

    public ReplayService(RollingAudioBuffer buffer, DiscordCaptureService capture, AudioMixerService mixer, SettingsService settings, AppPaths paths, ILogger<ReplayService> logger)
    {
        _mixer = mixer;
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
        float[] samples = _buffer.Snapshot(duration);
        bool includeOwn = _settings.Current.IncludeOwnAudioInReplays;
        if (includeOwn)
        {
            // Friends (Discord playback) + your side (mic + clips sent to Discord), both ending now.
            samples = AudioMixdown.SumEndAligned(samples, _mixer.SnapshotOwnAudio(duration), _buffer.Format);
        }
        var clip = new AudioClip(samples, _buffer.Format, DateTimeOffset.Now);
        _logger.LogInformation("Replay captured: requested {Requested:F1}s, got {Actual:F2}s{Own}",
            duration.TotalSeconds, clip.Duration.TotalSeconds, includeOwn ? " (incl. own audio)" : "");
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

    /// <summary>
    /// Replaces a saved clip's audio (e.g. after trimming) and optionally renames it.
    /// The original creation date is kept so the clip doesn't jump to the top of the list.
    /// </summary>
    public Task<SavedClipInfo> OverwriteAsync(SavedClipInfo existing, AudioClip clip, string newName)
    {
        WavSampleFormat format = _settings.Current.ClipFileFormat;
        return Task.Run(() =>
        {
            DateTime created = File.Exists(existing.FilePath) ? File.GetCreationTime(existing.FilePath) : DateTime.Now;
            string target = RenamedPath(existing.FilePath, newName);
            WavFileWriter.Write(target, clip.Samples, clip.Format, format);
            if (!PathsEqual(target, existing.FilePath)) File.Delete(existing.FilePath);
            File.SetCreationTime(target, created);
            _logger.LogInformation("Clip updated: {Path} ({Duration:F2}s)", target, clip.Duration.TotalSeconds);
            return new SavedClipInfo(target, Path.GetFileNameWithoutExtension(target), created, clip.Duration);
        });
    }

    /// <summary>Renames a saved clip's file. Returns the updated info.</summary>
    public SavedClipInfo Rename(SavedClipInfo clip, string newName)
    {
        string target = RenamedPath(clip.FilePath, newName);
        if (PathsEqual(target, clip.FilePath)) return clip;
        File.Move(clip.FilePath, target);
        _logger.LogInformation("Clip renamed: {Old} → {New}", Path.GetFileName(clip.FilePath), Path.GetFileName(target));
        return clip with { FilePath = target, Name = Path.GetFileNameWithoutExtension(target) };
    }

    public void Delete(SavedClipInfo clip)
    {
        File.Delete(clip.FilePath);
        _logger.LogInformation("Clip deleted: {Path}", clip.FilePath);
    }

    /// <summary>Decodes a saved clip into memory in the internal 48 kHz stereo format.</summary>
    public Task<AudioClip> LoadAsync(SavedClipInfo clip) => Task.Run(() => ClipFileLoader.Load(clip.FilePath));

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

    private static string RenamedPath(string currentPath, string newName)
    {
        string directory = Path.GetDirectoryName(currentPath)!;
        string baseName = SanitizeFileName(newName);
        string target = Path.Combine(directory, baseName + ".wav");
        return PathsEqual(target, currentPath) ? currentPath : UniquePath(directory, baseName, ".wav");
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
