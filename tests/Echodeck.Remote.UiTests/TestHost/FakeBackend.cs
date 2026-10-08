using System.Collections.Concurrent;
using Echodeck.Remote;

namespace Echodeck.Remote.TestHost;

/// <summary>
/// An in-memory stand-in for the Windows app behind the phone page. It behaves like the real
/// thing where the page can tell (clips are added, trimmed, renamed, categorised; playback runs
/// for the clip's length; Stop stops it) and records every command so tests can assert on them.
/// </summary>
public sealed class FakeBackend : IRemoteBackend
{
    public const string Token = "0123456789abcdef0123456789abcdef";

    private readonly object _lock = new();
    private List<RemoteClip> _clips = new();
    private List<string> _categories = new();
    private long _version;
    private bool _muted;
    private DateTime _discordUntil, _pcUntil;

    public FakeBackend() => Reset();

    public ConcurrentQueue<string> Calls { get; private set; } = new();
    public List<RemoteProblem> Problems { get; set; } = new();

    /// <summary>Action name ("play", "save", "trim"…) → error message returned instead of doing it.</summary>
    public ConcurrentDictionary<string, string> Failures { get; } = new();

    /// <summary>Action name → warning message returned alongside a successful result.</summary>
    public ConcurrentDictionary<string, string> Warnings { get; } = new();

    public static readonly Guid DefinitelyB = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TrustMe = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid GoodNight = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid LatestReplay = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public void Reset()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            _clips = new List<RemoteClip>
            {
                new(DefinitelyB, "He's definitely B", "CS2", true, 1.8, now.AddDays(-3), "He's definitely B, I heard him on the stairs"),
                new(TrustMe, "Trust me", "Bob", false, 2.4, now.AddDays(-2), "Trust me bro, it's fine"),
                new(GoodNight, "Hyvää yötä", "Matti", false, 3.1, now.AddDays(-1), "Hyvää yötä kaikille"),
                new(LatestReplay, "Replay 2026-10-07 14-32-09", null, false, 5.0, now.AddMinutes(-5)),
            };
            _categories = new List<string> { "Alice", "Bob", "CS2", "Matti" };
            _version++;
            _muted = false;
            _discordUntil = _pcUntil = DateTime.MinValue;
            Calls = new ConcurrentQueue<string>();
            Problems = new List<RemoteProblem>();
            Failures.Clear();
            Warnings.Clear();
        }
    }

    public void SetTranscript(string name, string? transcript) =>
        Change(_clips.First(c => c.Name == name).Id, c => c with { Transcript = transcript });

    // ------------------------------------------------------------------ IRemoteBackend

    public RemoteState GetState()
    {
        lock (_lock)
            return new RemoteState(_muted, true, true, DateTime.Now < _discordUntil, _version, new[] { 5, 30 },
                DateTime.Now < _pcUntil, Problems.ToList());
    }

    public IReadOnlyList<RemoteClip> GetClips() { lock (_lock) return _clips.ToList(); }

    public IReadOnlyList<string> GetCategories()
    {
        lock (_lock)
            return _categories.Concat(_clips.Select(c => c.Category).OfType<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<RemoteResult> PlayClipAsync(Guid id) => Act("play", () =>
    {
        var clip = Find(id);
        _discordUntil = DateTime.Now.AddSeconds(clip.Duration);
        return Ok($"Playing \"{clip.Name}\" into Discord.", id);
    });

    public Task<RemoteResult> PlayLastAsync() => Act("play-last", () =>
    {
        var newest = _clips.MaxBy(c => c.CreatedAt)!;
        _discordUntil = DateTime.Now.AddSeconds(newest.Duration);
        return Ok($"Playing \"{newest.Name}\" into Discord.", newest.Id);
    });

    public Task<RemoteResult> SaveLastAsync(int seconds) => Act($"save", () =>
    {
        var clip = new RemoteClip(Guid.NewGuid(), $"Replay {DateTime.Now:yyyy-MM-dd HH-mm-ss}", null, false, seconds, DateTime.Now);
        _clips.Add(clip);
        _version++;
        return Ok($"Saved \"{clip.Name}\" ({seconds:0.0} s).", clip.Id);
    }, detail: seconds.ToString());

    public Task<RemoteResult> StopAsync() => Act("stop", () =>
    {
        _discordUntil = _pcUntil = DateTime.MinValue;
        return Ok("Stopped");
    });

    public Task<RemoteResult> ToggleMuteAsync() => Act("mute", () =>
    {
        _muted = !_muted;
        return Ok(_muted ? "Mic muted" : "Mic on");
    });

    public Task<RemoteWaveform?> GetWaveformAsync(Guid id, int buckets)
    {
        RemoteClip? clip;
        lock (_lock) clip = _clips.FirstOrDefault(c => c.Id == id);
        if (clip is null) return Task.FromResult<RemoteWaveform?>(null);
        var peaks = Enumerable.Range(0, buckets).Select(i => (int)(50 + 45 * Math.Sin(i / 12.0))).ToArray();
        return Task.FromResult<RemoteWaveform?>(new RemoteWaveform(clip.Duration, peaks));
    }

    public Task<byte[]?> GetAudioAsync(Guid id)
    {
        RemoteClip? clip;
        lock (_lock) clip = _clips.FirstOrDefault(c => c.Id == id);
        return Task.FromResult(clip is null ? null : Wav(clip.Duration));
    }

    public Task<RemoteResult> PlayRangeToDiscordAsync(Guid id, RangeRequest r) => Act("play-range", () =>
    {
        _discordUntil = DateTime.Now.AddSeconds(r.End - r.Start);
        return Ok($"Playing {r.End - r.Start:0.0} s into Discord.", id);
    }, detail: Range(r));

    public Task<RemoteResult> PreviewRangeOnPcAsync(Guid id, RangeRequest r) => Act("preview-range", () =>
    {
        _pcUntil = DateTime.Now.AddSeconds(r.End - r.Start);
        return Ok("Playing in your PC headphones", id);
    }, detail: Range(r));

    public Task<RemoteResult> SaveTrimAsync(Guid id, RangeRequest r) => Act("trim", () =>
    {
        var source = Find(id);
        string name = string.IsNullOrWhiteSpace(r.Name) ? source.Name : r.Name.Trim();
        double duration = Math.Round(r.End - r.Start, 2);
        if (r.AsCopy)
        {
            var copy = source with { Id = Guid.NewGuid(), Name = name == source.Name ? name + " (trim)" : name, Duration = duration, CreatedAt = DateTime.Now, Transcript = null };
            _clips.Add(copy);
            _version++;
            return Ok($"Saved \"{copy.Name}\" ({duration:0.0} s)", copy.Id);
        }
        Change(id, c => c with { Name = name, Duration = duration, Transcript = null });
        return Ok($"Saved \"{name}\" ({duration:0.0} s)", id);
    }, detail: $"{Range(r)} name={r.Name} copy={r.AsCopy}");

    public Task<RemoteResult> RenameAsync(Guid id, string name) => Act("rename", () =>
    {
        if (string.IsNullOrWhiteSpace(name)) return new RemoteResult(false, "Name can't be empty");
        Change(id, c => c with { Name = name.Trim() });
        return Ok($"Renamed to \"{name.Trim()}\"", id);
    }, detail: name);

    public Task<RemoteResult> DeleteAsync(Guid id) => Act("delete", () =>
    {
        Find(id);
        _clips.RemoveAll(c => c.Id == id);
        _version++;
        return Ok("Deleted");
    });

    public Task<RemoteResult> SetFavoriteAsync(Guid id, bool favorite) => Act("favorite", () =>
    {
        Change(id, c => c with { Favorite = favorite });
        return Ok(favorite ? "★ Favourite" : "No longer a favourite", id);
    }, detail: favorite.ToString().ToLowerInvariant());

    public Task<RemoteResult> SetCategoryAsync(Guid id, string? category) => Act("category", () =>
    {
        string? clean = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        if (clean is not null)
        {
            clean = _categories.FirstOrDefault(c => string.Equals(c, clean, StringComparison.OrdinalIgnoreCase)) ?? clean;
            if (!_categories.Contains(clean)) _categories.Add(clean);
        }
        Change(id, c => c with { Category = clean });
        return Ok(clean is null ? "No category" : $"→ {clean}", id);
    }, detail: category ?? "<none>");

    public Task<RemoteResult> AddCategoryAsync(string name) => Act("add-category", () =>
    {
        if (string.IsNullOrWhiteSpace(name)) return new RemoteResult(false, "A category needs a name");
        _categories.Add(name.Trim());
        _version++;
        return Ok($"Category \"{name.Trim()}\" ready");
    }, detail: name);

    // ------------------------------------------------------------------ helpers

    private Task<RemoteResult> Act(string action, Func<RemoteResult> body, string? detail = null)
    {
        Calls.Enqueue(detail is null ? action : $"{action}:{detail}");
        if (Failures.TryGetValue(action, out var error)) return Task.FromResult(new RemoteResult(false, error));
        RemoteResult result;
        lock (_lock) result = body();
        if (result.Ok && Warnings.TryGetValue(action, out var warning)) result = result with { Message = warning, Warning = true };
        return Task.FromResult(result);
    }

    private static RemoteResult Ok(string message, Guid? id = null) => new(true, message, id);

    private static string Range(RangeRequest r) => $"{r.Start:0.00}-{r.End:0.00}";

    private RemoteClip Find(Guid id) =>
        _clips.FirstOrDefault(c => c.Id == id) ?? throw new InvalidOperationException("That clip no longer exists");

    private void Change(Guid id, Func<RemoteClip, RemoteClip> change)
    {
        lock (_lock)
        {
            int i = _clips.FindIndex(c => c.Id == id);
            if (i < 0) throw new InvalidOperationException("That clip no longer exists");
            _clips[i] = change(_clips[i]);
            _version++;
        }
    }

    /// <summary>A quiet 440 Hz tone of the given length (16-bit stereo, 48 kHz) for phone previews.</summary>
    private static byte[] Wav(double seconds)
    {
        const int rate = 48_000;
        int frames = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(frames * 4);
        for (int i = 0; i < frames; i++)
        {
            short v = (short)(3000 * Math.Sin(i * 2 * Math.PI * 440 / rate));
            w.Write(v); w.Write(v);
        }
        return ms.ToArray();
    }
}
