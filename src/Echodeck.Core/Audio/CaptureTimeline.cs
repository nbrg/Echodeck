namespace Echodeck.Core.Audio;

/// <summary>
/// Keeps the rolling buffer aligned with wall-clock time.
/// <para>
/// WASAPI loopback (both whole-device and per-process) delivers <b>no packets at all</b> while the
/// captured stream is silent. Without correction, ten seconds of nobody talking would occupy zero
/// seconds of buffer, and "the last 5 seconds" would actually reach back minutes. This class
/// compares frames received against elapsed time and tells the caller how much silence to insert.
/// </para>
/// <para>
/// If the source runs slightly fast (device clock vs. wall clock) or late packets arrive after
/// silence was already inserted, the anchor is moved forward instead of dropping audio, so the
/// timeline never drifts and never loses real audio.
/// </para>
/// Not thread-safe: callers serialise access (DiscordCaptureService does so under its write lock).
/// </summary>
public sealed class CaptureTimeline
{
    private readonly int _sampleRate;
    private readonly long _toleranceFrames;
    private readonly long _allowedLagFrames;

    private TimeSpan _anchorTime;
    private long _anchorFrames;
    private long _framesWritten;

    /// <param name="sampleRate">Frames per second of the buffer being fed.</param>
    /// <param name="tolerance">How far behind the source may fall before silence is inserted.
    /// Must exceed normal packet jitter (loopback packets are ~10 ms).</param>
    /// <param name="allowedLag">How far behind the wall clock the timeline is left after padding,
    /// so a packet that was merely late does not immediately push it ahead again.</param>
    public CaptureTimeline(int sampleRate, TimeSpan tolerance, TimeSpan allowedLag)
    {
        if (allowedLag > tolerance) throw new ArgumentException("allowedLag must not exceed tolerance.");
        _sampleRate = sampleRate;
        _toleranceFrames = (long)Math.Round(tolerance.TotalSeconds * sampleRate);
        _allowedLagFrames = (long)Math.Round(allowedLag.TotalSeconds * sampleRate);
    }

    public static CaptureTimeline CreateDefault(int sampleRate) =>
        new(sampleRate, TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(40));

    public long FramesWritten => _framesWritten;

    /// <summary>Starts a new timeline (call when a capture source starts).</summary>
    public void Reset(TimeSpan now)
    {
        _anchorTime = now;
        _anchorFrames = 0;
        _framesWritten = 0;
    }

    /// <summary>Records that real (or padded) frames were written to the buffer.</summary>
    public void OnFramesWritten(long frames, TimeSpan now)
    {
        _framesWritten += frames;
        long expected = Expected(now);
        if (_framesWritten > expected)
        {
            // Source is ahead of the wall clock: re-anchor so future gaps are measured correctly.
            _anchorTime = now;
            _anchorFrames = _framesWritten;
        }
    }

    /// <summary>Returns how many frames of silence must be inserted now (0 if none).</summary>
    public int SilenceFramesNeeded(TimeSpan now)
    {
        long deficit = Expected(now) - _framesWritten;
        if (deficit <= _toleranceFrames) return 0;
        long pad = deficit - _allowedLagFrames;
        // Never insert more than 10 minutes in one go (e.g. after the PC resumed from sleep).
        return (int)Math.Min(pad, (long)_sampleRate * 600);
    }

    private long Expected(TimeSpan now) =>
        _anchorFrames + (long)Math.Round((now - _anchorTime).TotalSeconds * _sampleRate);
}
