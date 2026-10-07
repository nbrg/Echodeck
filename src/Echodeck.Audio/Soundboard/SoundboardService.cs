using Echodeck.Audio.Mixing;
using Echodeck.Audio.Replay;
using Echodeck.Core.Audio;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Echodeck.Core.Soundboard;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Echodeck.Audio.Soundboard;

/// <summary>
/// The permanent soundboard: clip files + library metadata, and playing clips into Discord.
/// <para>
/// Every file operation keeps disk and <see cref="ClipLibraryStore"/> in step. Decoded audio is
/// cached (most recently used, capped at about 60 s of audio ≈ 23 MB), so a hotkey for a
/// favourite clip plays instantly without touching the disk.
/// </para>
/// </summary>
public sealed class SoundboardService : IDisposable
{
    private const long CacheMaxSamples = 48_000L * 2 * 60;

    private readonly ClipLibraryStore _library;
    private readonly AudioMixerService _mixer;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<SoundboardService> _logger;
    private readonly object _cacheLock = new();
    private readonly Dictionary<Guid, CacheEntry> _cache = new();
    private long _cachedSamples;
    private long _useCounter;

    private sealed record CacheEntry(AudioClip Clip, DateTime FileTime, long LastUse);

    public SoundboardService(ClipLibraryStore library, AudioMixerService mixer, SettingsService settings, AppPaths paths, ILogger<SoundboardService> logger)
    {
        _library = library;
        _mixer = mixer;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public ClipLibraryStore Library => _library;

    public string FullPath(SoundboardClip clip) => _library.FullPath(clip);

    /// <summary>Reads a WAV file's length (used by the library when adopting files). Null if unreadable.</summary>
    public static TimeSpan? ReadDuration(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            return reader.TotalTime;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ create

    /// <summary>Saves an in-memory clip (replay capture or editor result) as a new soundboard entry.</summary>
    public Task<SoundboardClip> AddAsync(AudioClip clip, string name, string? category = null) => Task.Run(() =>
    {
        string path = WriteNewFile(clip, name);
        var entry = new SoundboardClip
        {
            Name = Path.GetFileNameWithoutExtension(path),
            FileName = Path.GetFileName(path),
            CreatedAt = DateTime.Now,
            DurationSeconds = clip.Duration.TotalSeconds,
            Category = category,
        };
        _library.Add(entry);
        _logger.LogInformation("Clip saved: {Name} ({Duration:F2}s)", entry.Name, entry.DurationSeconds);
        return entry;
    });

    /// <summary>Imports audio files (WAV, MP3, …): decoded and stored as 48 kHz WAV in the clips folder.</summary>
    public Task<IReadOnlyList<SoundboardClip>> ImportAsync(IEnumerable<string> files) => Task.Run<IReadOnlyList<SoundboardClip>>(() =>
    {
        var added = new List<SoundboardClip>();
        foreach (string file in files)
        {
            try
            {
                var clip = ClipFileLoader.Load(file);
                string path = WriteNewFile(clip, Path.GetFileNameWithoutExtension(file));
                var entry = new SoundboardClip
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    FileName = Path.GetFileName(path),
                    DurationSeconds = clip.Duration.TotalSeconds,
                };
                _library.Add(entry);
                added.Add(entry);
                _logger.LogInformation("Imported {File} as {Name}", file, entry.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Import failed for {File}", file);
            }
        }
        return added;
    });

    public async Task<SoundboardClip> DuplicateAsync(Guid id)
    {
        var source = Require(id);
        var audio = await LoadAudioAsync(id);
        var copy = await AddAsync(audio, source.Name + " (copy)", source.Category);
        return _library.Update(copy.Id, c => c.Volume = source.Volume) ?? copy;
    }

    // ------------------------------------------------------------------ modify

    /// <summary>Replaces a clip's audio (after trimming) and optionally renames it; keeps its settings and date.</summary>
    public Task<SoundboardClip> ReplaceAudioAsync(Guid id, AudioClip clip, string newName) => Task.Run(() =>
    {
        var entry = Require(id);
        string oldPath = FullPath(entry);
        string target = RenamedPath(oldPath, newName);
        WavFileWriter.Write(target, clip.Samples, clip.Format, _settings.Current.ClipFileFormat);
        if (!PathsEqual(target, oldPath)) File.Delete(oldPath);
        Invalidate(id);
        var updated = _library.Update(id, c =>
        {
            c.FileName = Path.GetFileName(target);
            c.Name = Path.GetFileNameWithoutExtension(target);
            c.DurationSeconds = clip.Duration.TotalSeconds;
        })!;
        _logger.LogInformation("Clip updated: {Name} ({Duration:F2}s)", updated.Name, updated.DurationSeconds);
        return updated;
    });

    public SoundboardClip Rename(Guid id, string newName)
    {
        var entry = Require(id);
        string oldPath = FullPath(entry);
        string target = RenamedPath(oldPath, newName);
        if (!PathsEqual(target, oldPath)) File.Move(oldPath, target);
        _logger.LogInformation("Clip renamed: {Old} → {New}", entry.Name, Path.GetFileNameWithoutExtension(target));
        return _library.Update(id, c =>
        {
            c.FileName = Path.GetFileName(target);
            c.Name = Path.GetFileNameWithoutExtension(target);
        })!;
    }

    public void Delete(Guid id)
    {
        var entry = Require(id);
        File.Delete(FullPath(entry));
        Invalidate(id);
        _library.Remove(id);
        _logger.LogInformation("Clip deleted: {Name}", entry.Name);
    }

    /// <summary>Category, favourite, volume, hotkey — metadata only.</summary>
    public SoundboardClip? Update(Guid id, Action<SoundboardClip> change) => _library.Update(id, change);

    // ------------------------------------------------------------------ play

    public async Task<AudioClip> LoadAudioAsync(Guid id)
    {
        var entry = Require(id);
        string path = FullPath(entry);
        DateTime fileTime = File.GetLastWriteTimeUtc(path);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(id, out var hit) && hit.FileTime == fileTime)
            {
                _cache[id] = hit with { LastUse = ++_useCounter };
                return hit.Clip;
            }
        }

        var clip = await Task.Run(() => ClipFileLoader.Load(path));
        lock (_cacheLock)
        {
            if (_cache.Remove(id, out var stale)) _cachedSamples -= stale.Clip.Samples.Length;
            _cache[id] = new CacheEntry(clip, fileTime, ++_useCounter);
            _cachedSamples += clip.Samples.Length;
            while (_cachedSamples > CacheMaxSamples && _cache.Count > 1)
            {
                var oldest = _cache.MinBy(kv => kv.Value.LastUse);
                _cache.Remove(oldest.Key);
                _cachedSamples -= oldest.Value.Clip.Samples.Length;
            }
        }
        return clip;
    }

    /// <summary>Plays a soundboard clip into Discord at its own volume. Returns its name.</summary>
    public async Task<string> PlayToDiscordAsync(Guid id)
    {
        var entry = Require(id);
        var audio = await LoadAudioAsync(id);
        _mixer.PlayToDiscord(audio, entry.Name, (float)entry.Volume);
        return entry.Name;
    }

    // ------------------------------------------------------------------ helpers

    private SoundboardClip Require(Guid id) =>
        _library.Find(id) ?? throw new InvalidOperationException("That clip no longer exists.");

    private void Invalidate(Guid id)
    {
        lock (_cacheLock)
            if (_cache.Remove(id, out var e)) _cachedSamples -= e.Clip.Samples.Length;
    }

    private string WriteNewFile(AudioClip clip, string name)
    {
        Directory.CreateDirectory(_paths.ClipsDirectory);
        string path = UniquePath(_paths.ClipsDirectory, SanitizeFileName(name), ".wav");
        WavFileWriter.Write(path, clip.Samples, clip.Format, _settings.Current.ClipFileFormat);
        return path;
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
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "Clip" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    private static string UniquePath(string directory, string baseName, string extension)
    {
        string path = Path.Combine(directory, baseName + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{baseName} ({i}){extension}");
        return path;
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cachedSamples = 0;
        }
    }
}
