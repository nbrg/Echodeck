using System.Text.Json;
using Echodeck.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Echodeck.Core.Soundboard;

/// <summary>One soundboard entry. The audio itself is a WAV file in the clips folder.</summary>
public sealed class SoundboardClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>File name inside the clips folder (not a full path, so the folder can move).</summary>
    public string FileName { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public double DurationSeconds { get; set; }

    /// <summary>Per-clip volume, 1.0 = as recorded.</summary>
    public double Volume { get; set; } = 1.0;

    public string? Category { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>Global shortcut that plays this clip into Discord, e.g. "Ctrl+NumPad1".</summary>
    public string? Hotkey { get; set; }

    public SoundboardClip Clone() => (SoundboardClip)MemberwiseClone();
}

/// <summary>
/// The soundboard index (clips.json): names, categories, favourites, volumes, hotkeys.
/// <para>
/// The clips folder stays the source of truth for audio. On load, WAV files that aren't in the
/// index (older Echodeck versions, files dropped in by hand) are added, and entries whose file
/// has gone are removed. Saves are atomic. All methods are thread-safe and return copies.
/// </para>
/// </summary>
public sealed class ClipLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly AppPaths _paths;
    private readonly Func<string, TimeSpan?> _readDuration;
    private readonly ILogger<ClipLibraryStore> _logger;
    private readonly object _lock = new();
    private List<SoundboardClip> _clips = new();
    private long _version;

    /// <param name="readDuration">Reads a WAV file's duration (null if unreadable). Injected so this stays platform-neutral.</param>
    public ClipLibraryStore(AppPaths paths, Func<string, TimeSpan?> readDuration, ILogger<ClipLibraryStore> logger)
    {
        _paths = paths;
        _readDuration = readDuration;
        _logger = logger;
    }

    /// <summary>Raised (on the calling thread) after any change.</summary>
    public event EventHandler? Changed;

    /// <summary>Increments on every change (the phone remote uses it to know when to refresh).</summary>
    public long Version => Interlocked.Read(ref _version);

    public IReadOnlyList<SoundboardClip> Clips
    {
        get { lock (_lock) return _clips.Select(c => c.Clone()).ToList(); }
    }

    public SoundboardClip? Find(Guid id)
    {
        lock (_lock) return _clips.FirstOrDefault(c => c.Id == id)?.Clone();
    }

    public string FullPath(SoundboardClip clip) => Path.Combine(_paths.ClipsDirectory, clip.FileName);

    public IReadOnlyList<string> Categories
    {
        get
        {
            lock (_lock)
                return _clips.Select(c => c.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Loads clips.json and reconciles it with the files on disk.</summary>
    public void Load()
    {
        lock (_lock)
        {
            _clips = ReadIndex();
            bool changed = Reconcile();
            if (changed) SaveUnlocked();
            _version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(SoundboardClip clip) => Mutate(list => list.Add(clip.Clone()));

    public void Remove(Guid id) => Mutate(list => list.RemoveAll(c => c.Id == id));

    /// <summary>Applies <paramref name="change"/> to the stored clip. Returns the updated copy, or null if not found.</summary>
    public SoundboardClip? Update(Guid id, Action<SoundboardClip> change)
    {
        SoundboardClip? updated = null;
        Mutate(list =>
        {
            var clip = list.FirstOrDefault(c => c.Id == id);
            if (clip is null) return;
            change(clip);
            updated = clip.Clone();
        });
        return updated;
    }

    private void Mutate(Action<List<SoundboardClip>> change)
    {
        lock (_lock)
        {
            change(_clips);
            SaveUnlocked();
            _version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<SoundboardClip> ReadIndex()
    {
        string path = _paths.ClipLibraryFile;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<SoundboardClip>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "clips.json unreadable; rebuilding it from the clips folder (old file kept as clips.json.bad)");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* ignore */ }
        }
        return new();
    }

    private bool Reconcile()
    {
        bool changed = false;
        Directory.CreateDirectory(_paths.ClipsDirectory);

        // Drop entries whose audio file is gone (deleted in Explorer), and duplicates.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int before = _clips.Count;
        _clips.RemoveAll(c => string.IsNullOrEmpty(c.FileName) || !File.Exists(FullPath(c)) || !seen.Add(c.FileName));
        if (_clips.Count != before)
        {
            _logger.LogInformation("Removed {Count} library entries whose file no longer exists", before - _clips.Count);
            changed = true;
        }

        // Adopt WAV files nobody knows about yet.
        foreach (string file in Directory.EnumerateFiles(_paths.ClipsDirectory, "*.wav"))
        {
            string name = Path.GetFileName(file);
            if (seen.Contains(name)) continue;
            _clips.Add(new SoundboardClip
            {
                Name = Path.GetFileNameWithoutExtension(file),
                FileName = name,
                CreatedAt = File.GetCreationTime(file),
                DurationSeconds = _readDuration(file)?.TotalSeconds ?? 0,
            });
            changed = true;
        }
        return changed;
    }

    private void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            string temp = _paths.ClipLibraryFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_clips, JsonOptions));
            File.Move(temp, _paths.ClipLibraryFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save clips.json");
        }
    }
}
