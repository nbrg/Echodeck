namespace Echodeck.Audio.Capture;

/// <summary>Which technique is currently delivering Discord audio.</summary>
public enum CaptureMethod
{
    None,
    /// <summary>WASAPI process loopback on the Discord process tree — Discord audio only.</summary>
    ProcessLoopback,
    /// <summary>Loopback of the output device Discord is playing to — includes other apps on that device.</summary>
    DiscordOutputDevice,
    /// <summary>Loopback of a user-selected/default output device — includes everything on that device.</summary>
    SelectedDevice,
}

/// <summary>
/// One way of capturing incoming Discord audio. Implementations are interchangeable so better
/// capture methods can be added later without touching the buffer/replay code.
/// <para>
/// Contract:
/// <list type="bullet">
/// <item><see cref="Start"/> either begins capturing or throws — callers use the exception to fall
/// back to the next method.</item>
/// <item><see cref="DataAvailable"/> delivers audio already converted to
/// <see cref="Echodeck.Core.Audio.AudioFormat.Internal"/>, on the source's own capture thread.</item>
/// <item><see cref="Faulted"/> fires (at most once) if capture dies later, e.g. device unplugged.</item>
/// <item><see cref="IDisposable.Dispose"/> stops capture and releases every COM object/thread.</item>
/// </list>
/// </para>
/// </summary>
public interface IDiscordAudioSource : IDisposable
{
    CaptureMethod Method { get; }

    /// <summary>Human-readable target, e.g. "Discord.exe (PID 1234)" or a device name.</summary>
    string Description { get; }

    event AudioDataHandler? DataAvailable;
    event EventHandler<Exception>? Faulted;

    void Start();
}
