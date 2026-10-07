namespace Echodeck.App.Services;

/// <summary>
/// Ensures only one Echodeck runs per Windows user session. Two copies would both open the
/// virtual cable and microphone, doubling your voice in Discord.
/// <para>
/// The first instance owns a named mutex and waits on a named event. A second instance fails
/// to get the mutex, signals the event (so the first one brings its window to the front), and exits.
/// "Local\" scopes both names to the current logon session.
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\Echodeck.SingleInstance";
    private const string ActivateEventName = @"Local\Echodeck.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activateEvent, Action onActivateRequested)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            activateEvent, (_, _) => onActivateRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Returns a guard if this is the first instance. Otherwise asks the running instance to show
    /// itself and returns null — the caller should exit.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(Action onActivateRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other instance is still starting up or shutting down; nothing to activate.
            }
            return null;
        }

        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstanceGuard(mutex, activateEvent, onActivateRequested);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activateEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread */ }
        _mutex.Dispose();
    }
}
