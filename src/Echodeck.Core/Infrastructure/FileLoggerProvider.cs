using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Echodeck.Core.Infrastructure;

/// <summary>
/// Small, bounded file logger.
/// <list type="bullet">
/// <item>Log calls never block the caller (important on audio threads): lines go into a bounded
/// queue; if it is full the line is dropped and counted.</item>
/// <item>One background thread writes to <c>logs\echodeck.log</c>; at 1 MB the file rolls to
/// echodeck.1.log … echodeck.4.log, so disk usage is capped at ~5 MB forever.</item>
/// <item>The last few hundred lines are kept in memory for "Copy diagnostics".</item>
/// </list>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxRolledFiles = 4;
    private const int RecentLineCapacity = 400;

    private readonly string _directory;
    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writerThread;
    private readonly ConcurrentQueue<string> _recent = new();
    private long _dropped;
    private int _recentCount;
    private volatile bool _disposed;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _path = Path.Combine(directory, "echodeck.log");
        MinimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "Echodeck log writer", Priority = ThreadPriority.BelowNormal };
        _writerThread.Start();
    }

    public LogLevel MinimumLevel { get; }
    public string CurrentLogFile => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    /// <summary>Most recent log lines (oldest first) for the diagnostics report.</summary>
    public IReadOnlyList<string> GetRecentLines() => _recent.ToArray();

    internal void Enqueue(string line)
    {
        _recent.Enqueue(line);
        if (Interlocked.Increment(ref _recentCount) > RecentLineCapacity && _recent.TryDequeue(out _))
            Interlocked.Decrement(ref _recentCount);

        if (_disposed) return;
        try
        {
            if (!_queue.TryAdd(line))
                Interlocked.Increment(ref _dropped);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Logged during shutdown: the line is still in the in-memory list.
        }
    }

    private void WriterLoop()
    {
        StreamWriter? writer = null;
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    writer ??= OpenWriter();
                    long dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0) writer.WriteLine($"[logger] {dropped} log lines dropped (queue full)");
                    writer.WriteLine(line);
                    if (_queue.Count == 0) writer.Flush();

                    if (writer.BaseStream.Length > MaxFileBytes)
                    {
                        writer.Dispose();
                        writer = null;
                        Roll();
                    }
                }
                catch (IOException)
                {
                    // Disk full / file locked: drop the writer and try again on the next line.
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter OpenWriter()
    {
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false));
    }

    private void Roll()
    {
        string Rolled(int i) => Path.Combine(_directory, $"echodeck.{i}.log");
        try
        {
            File.Delete(Rolled(MaxRolledFiles));
            for (int i = MaxRolledFiles - 1; i >= 1; i--)
                if (File.Exists(Rolled(i))) File.Move(Rolled(i), Rolled(i + 1));
            File.Move(_path, Rolled(1));
        }
        catch (IOException)
        {
            // If rolling fails we just keep appending; the next roll attempt will retry.
        }
    }

    private static string ShortCategory(string category)
    {
        int dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        // The writer drains what is queued and exits; the collection itself is left for the GC
        // so a late log call from another thread can never hit a disposed object.
        _writerThread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message = formatter(state, exception);
            var line = new StringBuilder(128)
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ').Append(LevelTag(logLevel))
                .Append(" [").Append(_category).Append("] ")
                .Append(message);
            if (exception is not null)
                line.Append(Environment.NewLine).Append(exception);
            _provider.Enqueue(line.ToString());
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
