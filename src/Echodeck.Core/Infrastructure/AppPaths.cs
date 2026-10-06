namespace Echodeck.Core.Infrastructure;

/// <summary>
/// All on-disk locations. Everything lives under %AppData%\Echodeck — no registry, no cloud.
/// <code>
/// %AppData%\Echodeck\
///   settings.json
///   clips.json          (Phase 4: soundboard library index)
///   clips\              saved WAV clips
///   logs\               rolling diagnostic logs (size-capped)
/// </code>
/// </summary>
public sealed class AppPaths
{
    public AppPaths() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Echodeck"))
    {
    }

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ClipLibraryFile => Path.Combine(Root, "clips.json");
    public string ClipsDirectory => Path.Combine(Root, "clips");
    public string LogsDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ClipsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// Removes leftovers from interrupted writes (*.tmp). Called at startup so temporary files can
    /// never accumulate across sessions.
    /// </summary>
    public int CleanupTemporaryFiles()
    {
        int removed = 0;
        foreach (string dir in new[] { Root, ClipsDirectory })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                try { File.Delete(file); removed++; } catch { /* in use or locked: ignore */ }
            }
        }
        return removed;
    }
}
