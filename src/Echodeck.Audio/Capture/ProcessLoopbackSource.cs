using System.Runtime.InteropServices;
using Echodeck.Audio.Interop;
using Echodeck.Core.Audio;
using Microsoft.Extensions.Logging;
using static Echodeck.Audio.Interop.AudioClientConstants;

namespace Echodeck.Audio.Capture;

/// <summary>
/// Preferred capture method: WASAPI <b>process loopback</b> on the Discord process tree.
/// <para>
/// Captures exactly what Discord renders — friends' voices, Discord's own sounds — and nothing
/// from CS2, Spotify, browsers, Windows notifications or Echodeck itself. It is also independent
/// of which output device Discord uses, so switching headsets in Discord needs no restart.
/// </para>
/// <para>
/// Threading: one dedicated MTA thread does activation, initialisation and the event-driven read
/// loop, and is the only thread that touches the COM objects. <see cref="Start"/> waits for that
/// thread to report success or failure so the caller can fall back synchronously.
/// </para>
/// </summary>
public sealed class ProcessLoopbackSource : IDiscordAudioSource
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);
    private const long BufferDuration100ns = 1_000_000; // 100 ms WASAPI buffer; we drain every ~10 ms

    private readonly int _processId;
    private readonly ILogger _logger;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly AutoResetEvent _audioEvent = new(false);
    private Thread? _thread;
    private Exception? _startError;
    private volatile bool _stopping;
    private int _faulted;

    public ProcessLoopbackSource(int processId, string exeName, ILogger logger)
    {
        _processId = processId;
        _logger = logger;
        Description = $"{exeName} (PID {processId}) process tree";
    }

    /// <summary>
    /// Process loopback was introduced in Windows 10 build 20348 (officially) and works on most
    /// 2004+ (19041) consumer builds. On older systems we skip straight to the fallbacks.
    /// </summary>
    public static bool IsSupportedByOs => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public CaptureMethod Method => CaptureMethod.ProcessLoopback;
    public string Description { get; }
    public CaptureFormat? NegotiatedFormat { get; private set; }

    public event AudioDataHandler? DataAvailable;
    public event EventHandler<Exception>? Faulted;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Already started.");
        _thread = new Thread(CaptureThread)
        {
            IsBackground = true,
            Name = $"Echodeck process loopback ({_processId})",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_started.Wait(ActivationTimeout + TimeSpan.FromSeconds(2)))
        {
            _stopping = true;
            throw new AudioCaptureException("Process loopback did not start in time.", 0);
        }
        if (_startError is not null)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
            throw _startError;
        }
    }

    private void CaptureThread()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? captureClient = null;
        try
        {
            CaptureFormat format;
            (client, format) = ActivateAndInitialize();
            NegotiatedFormat = format;
            var converter = new CaptureFormatConverter(format);

            int hr = client.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle());
            ThrowIfFailed(hr, "IAudioClient.SetEventHandle");

            Guid iid = IID_IAudioCaptureClient;
            hr = client.GetService(ref iid, out object service);
            ThrowIfFailed(hr, "IAudioClient.GetService(IAudioCaptureClient)");
            captureClient = (IAudioCaptureClient)service;

            ThrowIfFailed(client.Start(), "IAudioClient.Start");
            _logger.LogInformation("Process loopback started for {Target} as {Format}", Description, format);
            _started.Set();

            AudioDataHandler emit = samples => DataAvailable?.Invoke(samples);
            while (!_stopping)
            {
                // Signalled roughly every 10 ms while Discord renders audio. When Discord is silent
                // no packets arrive at all; the timeout just lets us notice _stopping.
                _audioEvent.WaitOne(100);
                if (_stopping) break;
                Drain(captureClient, converter, format, emit);
            }

            client.Stop();
        }
        catch (Exception ex)
        {
            if (!_started.IsSet)
            {
                _startError = ex as AudioCaptureException ?? new AudioCaptureException(ex.Message, ex.HResult, ex);
                _started.Set();
            }
            else if (!_stopping)
            {
                _logger.LogWarning(ex, "Process loopback capture failed for {Target}", Description);
                RaiseFaulted(ex);
            }
        }
        finally
        {
            if (captureClient is not null) Marshal.FinalReleaseComObject(captureClient);
            if (client is not null) Marshal.FinalReleaseComObject(client);
        }
    }

    private unsafe void Drain(IAudioCaptureClient captureClient, CaptureFormatConverter converter, CaptureFormat format, AudioDataHandler emit)
    {
        while (true)
        {
            int hr = captureClient.GetNextPacketSize(out uint packetFrames);
            ThrowIfFailed(hr, "GetNextPacketSize");
            if (packetFrames == 0) return;

            hr = captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
            if (hr == AUDCLNT_S_BUFFER_EMPTY) return;
            ThrowIfFailed(hr, "GetBuffer");

            try
            {
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                    converter.ProcessSilence((int)frames, emit);
                else
                    converter.Process(new ReadOnlySpan<byte>((void*)data, (int)frames * format.BlockAlign), (int)frames, emit);
            }
            finally
            {
                captureClient.ReleaseBuffer(frames);
            }
        }
    }

    /// <summary>
    /// Tries formats from best to most conservative. 48 kHz float is ideal (no conversion at all);
    /// the last option mirrors Microsoft's ApplicationLoopback sample exactly, which is known to
    /// work everywhere process loopback exists. A fresh client is activated per attempt because a
    /// failed Initialize can leave the client unusable.
    /// </summary>
    private unsafe (IAudioClient Client, CaptureFormat Format) ActivateAndInitialize()
    {
        const uint baseFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        const uint convertFlags = baseFlags | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        var attempts = new (CaptureFormat Format, uint Flags)[]
        {
            (new CaptureFormat(SampleEncoding.Float32, 48_000, 2), baseFlags),
            (new CaptureFormat(SampleEncoding.Float32, 48_000, 2), convertFlags),
            (new CaptureFormat(SampleEncoding.Pcm16, 48_000, 2), baseFlags),
            (new CaptureFormat(SampleEncoding.Pcm16, 44_100, 2), baseFlags),
        };

        int lastHr = 0;
        foreach (var (format, flags) in attempts)
        {
            if (_stopping) break;
            IAudioClient client = ProcessLoopbackActivator.Activate(
                _processId, ProcessLoopbackMode.IncludeTargetProcessTree, ActivationTimeout);

            var wfx = WaveFormatEx.Create(
                format.Encoding == SampleEncoding.Float32 ? WAVE_FORMAT_IEEE_FLOAT : WAVE_FORMAT_PCM,
                format.SampleRate, format.Channels, format.BytesPerSample * 8);

            int hr = client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration100ns, 0, (IntPtr)(&wfx), IntPtr.Zero);
            if (hr >= 0) return (client, format);

            lastHr = hr;
            _logger.LogDebug("Process loopback Initialize rejected {Format} flags 0x{Flags:X8}: 0x{Hr:X8}", format, flags, hr);
            Marshal.FinalReleaseComObject(client);
        }
        throw new AudioCaptureException($"Process loopback rejected every capture format (last HRESULT 0x{lastHr:X8}).", lastHr);
    }

    private static void ThrowIfFailed(int hr, string call)
    {
        if (hr < 0) throw new AudioCaptureException($"{call} failed (0x{hr:X8}).", hr);
    }

    private void RaiseFaulted(Exception ex)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
            Faulted?.Invoke(this, ex);
    }

    public void Dispose()
    {
        _stopping = true;
        _audioEvent.Set();
        DataAvailable = null;
        Faulted = null;
        if (_thread is not null && _thread != Thread.CurrentThread && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // Leave the wait handles to the finalizer: the thread (and WASAPI) may still use them.
            _logger.LogWarning("Process loopback thread for {Target} did not exit in time", Description);
            return;
        }
        _audioEvent.Dispose();
        _started.Dispose();
    }
}
