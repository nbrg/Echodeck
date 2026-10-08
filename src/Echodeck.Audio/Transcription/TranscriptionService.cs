using System.Net.Http;
using Echodeck.Audio.Replay;
using Echodeck.Core.Audio;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Echodeck.Core.Soundboard;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace Echodeck.Audio.Transcription;

/// <summary>What the transcriber is doing, for the Settings page.</summary>
/// <param name="Busy">Downloading or transcribing right now.</param>
/// <param name="Progress">0–1 while downloading, otherwise null.</param>
public sealed record TranscriptionStatus(string Text, bool Busy = false, double? Progress = null, bool Problem = false);

/// <summary>
/// Recognises what's said in each clip, entirely on this PC (whisper.cpp via Whisper.net), and
/// stores it as the clip's transcript so the PC and phone can search clips by their words.
/// <list type="bullet">
/// <item>Off until turned on in Settings; the model (142 MB or 466 MB) is downloaded once into
/// %AppData%\Echodeck\models.</item>
/// <item>Works through clips one at a time on a single low-priority background thread using two
/// CPU cores, so it doesn't compete with a game. A typical 5 s clip takes about a second.</item>
/// <item>New and trimmed clips are picked up automatically (their transcript is null).</item>
/// </list>
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    private const int Threads = 2;

    private readonly ClipLibraryStore _library;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<TranscriptionService> _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _download;
    private Thread? _worker;
    private (bool Enabled, string Model, string Language) _applied;
    private TranscriptionStatus _status = new("Off");
    private volatile string? _brokenModel; // failed to load: don't retry on every library change

    public TranscriptionService(ClipLibraryStore library, SettingsService settings, AppPaths paths, ILogger<TranscriptionService> logger)
    {
        _library = library;
        _settings = settings;
        _paths = paths;
        _logger = logger;
    }

    public TranscriptionStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Raised on a background thread when <see cref="Status"/> changes.</summary>
    public event EventHandler<TranscriptionStatus>? StatusChanged;

    public string ModelPath(string model) => Path.Combine(_paths.ModelsDirectory, SpeechModels.FileName(model));

    public bool IsModelDownloaded(string model) =>
        File.Exists(ModelPath(model)) && new FileInfo(ModelPath(model)).Length > SpeechModels.Get(model).ApproxBytes * 9 / 10;

    public void Start()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Echodeck transcription", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
        _library.Changed += OnLibraryChanged;
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current);
    }

    /// <summary>Deletes the downloaded model and downloads it again (in case it's damaged).</summary>
    public void Redownload()
    {
        string model = _settings.Current.TranscriptionModel;
        TryDelete(ModelPath(model));
        _brokenModel = null;
        lock (_lock) _applied = default;
        Apply(_settings.Current);
    }

    /// <summary>Clears every transcript so all clips are transcribed again (e.g. after switching model or language).</summary>
    public void RetranscribeAll()
    {
        foreach (var clip in _library.Clips.Where(c => c.Transcript is not null))
            _library.Update(clip.Id, c => c.Transcript = null);
        _wake.Release();
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => _wake.Release();

    private void OnSettingsChanged(object? sender, AppSettings s) => Apply(s);

    private void Apply(AppSettings s)
    {
        var wanted = (s.TranscriptionEnabled, s.TranscriptionModel, s.TranscriptionLanguage);
        lock (_lock)
        {
            if (wanted == _applied) return;
            _applied = wanted;
        }
        _brokenModel = null;
        if (!s.TranscriptionEnabled)
        {
            _download?.Cancel();
            SetStatus(new TranscriptionStatus("Off"));
            return;
        }
        if (!IsModelDownloaded(s.TranscriptionModel)) _ = DownloadAsync(s.TranscriptionModel);
        _wake.Release();
    }

    // ------------------------------------------------------------------ model download

    private async Task DownloadAsync(string model)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _download?.Cancel();
            _download = cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        }
        string target = ModelPath(model);
        string temp = target + ".download";
        var info = SpeechModels.Get(model);
        try
        {
            Directory.CreateDirectory(_paths.ModelsDirectory);
            SetStatus(new TranscriptionStatus($"Downloading the speech model ({info.ApproxBytes / 1_000_000} MB, one time)…", true, 0));
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Echodeck");
            using var response = await http.GetAsync(SpeechModels.DownloadUrl(model), HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.ApproxBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long done = 0, lastReport = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    done += read;
                    if (done - lastReport > 2_000_000)
                    {
                        lastReport = done;
                        SetStatus(new TranscriptionStatus($"Downloading the speech model… {done * 100 / Math.Max(1, total)} %", true, (double)done / total));
                    }
                }
            }
            if (new FileInfo(temp).Length < info.ApproxBytes * 9 / 10)
                throw new IOException("The download was incomplete.");
            File.Move(temp, target, overwrite: true);
            _logger.LogInformation("Speech model {Model} downloaded", model);
            SetStatus(new TranscriptionStatus("Speech model ready."));
            _wake.Release();
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            _logger.LogWarning(ex, "Speech model download failed");
            SetStatus(new TranscriptionStatus($"Couldn't download the speech model: {ex.Message}. Check your internet connection, then turn the option off and on again.", Problem: true));
        }
    }

    // ------------------------------------------------------------------ worker

    private void WorkerLoop()
    {
        WhisperFactory? factory = null;
        string? factoryModel = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try { _wake.Wait(TimeSpan.FromSeconds(30), _shutdown.Token); }
                catch (OperationCanceledException) { break; }
                while (_wake.CurrentCount > 0) _wake.Wait(0); // coalesce bursts of library changes

                var s = _settings.Current;
                if (!s.TranscriptionEnabled || !IsModelDownloaded(s.TranscriptionModel) || _brokenModel == s.TranscriptionModel) continue;

                var pending = _library.Clips.Where(c => c.Transcript is null).OrderByDescending(c => c.CreatedAt).ToList();
                if (pending.Count == 0)
                {
                    SetIdleStatus();
                    continue;
                }

                try
                {
                    if (factory is null || factoryModel != s.TranscriptionModel)
                    {
                        factory?.Dispose();
                        factory = WhisperFactory.FromPath(ModelPath(s.TranscriptionModel));
                        factoryModel = s.TranscriptionModel;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Loading the speech model failed");
                    SetStatus(new TranscriptionStatus($"Speech recognition couldn't start ({ex.Message}). Try \"Download again\"; the single-exe test build doesn't include it.", Problem: true));
                    _brokenModel = s.TranscriptionModel;
                    continue;
                }

                for (int i = 0; i < pending.Count && !_shutdown.IsCancellationRequested; i++)
                {
                    var current = _settings.Current;
                    if (!current.TranscriptionEnabled || current.TranscriptionModel != factoryModel) break;
                    SetStatus(new TranscriptionStatus($"Recognising speech in clips… {pending.Count - i} to go", true));
                    TranscribeOne(factory, pending[i], current.TranscriptionLanguage);
                }
                SetIdleStatus();
                _wake.Release(); // look again: clips may have arrived meanwhile
            }
        }
        finally
        {
            factory?.Dispose();
        }
    }

    private void TranscribeOne(WhisperFactory factory, SoundboardClip clip, string language)
    {
        try
        {
            string path = _library.FullPath(clip);
            if (!File.Exists(path)) return;
            var audio = ClipFileLoader.Load(path);
            var input = SpeechAudio.ToWhisperInput(audio);
            string text = "";
            if (input.Length >= SpeechAudio.SampleRate / 4) // shorter than 0.25 s: nothing to recognise
            {
                var segments = new List<string>();
                using var processor = factory.CreateBuilder()
                    .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                    .WithThreads(Threads)
                    .WithSegmentEventHandler(segment => segments.Add(segment.Text))
                    .Build();
                processor.Process(input); // synchronous: this thread is already the low-priority worker
                text = SpeechAudio.CleanTranscript(segments);
            }

            // Only store it if the clip wasn't trimmed/replaced meanwhile.
            _library.Update(clip.Id, c =>
            {
                if (c.FileName == clip.FileName && c.Transcript is null) c.Transcript = text;
            });
            _logger.LogDebug("Transcribed {Clip}: {Text}", clip.Name, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcribing {Clip} failed", clip.Name);
            // Mark it so a broken file isn't retried forever.
            _library.Update(clip.Id, c => { if (c.FileName == clip.FileName && c.Transcript is null) c.Transcript = ""; });
        }
    }

    private void SetIdleStatus()
    {
        var clips = _library.Clips;
        int withSpeech = clips.Count(c => !string.IsNullOrEmpty(c.Transcript));
        SetStatus(new TranscriptionStatus($"Up to date: speech recognised in {withSpeech} of {clips.Count} clips. Search finds clips by what's said."));
    }

    private void SetStatus(TranscriptionStatus status)
    {
        lock (_lock)
        {
            if (status == _status) return;
            _status = status;
        }
        StatusChanged?.Invoke(this, status);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* in use: ignore */ }
    }

    public void Dispose()
    {
        _library.Changed -= OnLibraryChanged;
        _settings.Changed -= OnSettingsChanged;
        _shutdown.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(3));
    }
}
