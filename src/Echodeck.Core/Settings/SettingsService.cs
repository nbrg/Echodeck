using System.Text.Json;
using System.Text.Json.Serialization;
using Echodeck.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Echodeck.Core.Settings;

/// <summary>
/// Loads/saves settings.json. A corrupt file is moved aside (settings.json.bad) and defaults are
/// used, so a bad edit can never stop the app from starting. Saves are atomic (temp file + move).
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
        _current = Load();
    }

    /// <summary>Raised (on the caller's thread) after settings are changed via <see cref="Update"/>.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Returns a copy; modify it through <see cref="Update"/>.</summary>
    public AppSettings Current
    {
        get { lock (_lock) return _current.Clone(); }
    }

    public void Update(Action<AppSettings> change)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            var copy = _current.Clone();
            change(copy);
            copy.Normalize();
            _current = copy;
            snapshot = copy.Clone();
            Save(copy);
        }
        Changed?.Invoke(this, snapshot);
    }

    private AppSettings Load()
    {
        string path = _paths.SettingsFile;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
                loaded.Normalize();
                _logger.LogInformation("Settings loaded from {Path}", path);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file unreadable; using defaults and keeping a copy as settings.json.bad");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* ignore */ }
        }
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_paths.Root);
            string temp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _paths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
    }
}
