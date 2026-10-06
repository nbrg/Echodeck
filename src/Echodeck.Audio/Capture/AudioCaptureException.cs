namespace Echodeck.Audio.Capture;

/// <summary>Raised when a capture method cannot be started; carries the HRESULT for diagnostics.</summary>
public sealed class AudioCaptureException : Exception
{
    public AudioCaptureException(string message, int hresult, Exception? inner = null) : base(message, inner)
    {
        HResult = hresult;
    }
}
