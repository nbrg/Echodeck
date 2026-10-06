using System.Runtime.InteropServices;

namespace Echodeck.Audio.Interop;

// Minimal hand-written Core Audio (WASAPI) COM definitions needed for *process loopback*.
//
// Why not NAudio for this part? NAudio's AudioClient wrapper is built around an IMMDevice and
// calls GetMixFormat()/GetDevicePeriod(), which the virtual process-loopback device does not
// implement (E_NOTIMPL). Activating via ActivateAudioInterfaceAsync and driving IAudioClient
// directly is the approach used by Microsoft's own ApplicationLoopback sample.
//
// All methods use [PreserveSig] so HRESULTs come back as ints and we decide how to react
// (log + fall back) instead of getting COMExceptions thrown from deep inside the capture loop.

internal static class AudioClientConstants
{
    // Header: audioclientactivationparams.h
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;

    public const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;

    public const ushort VT_BLOB = 65;

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1,
}

internal enum ProcessLoopbackMode
{
    /// <summary>Capture the target process and all of its child processes.</summary>
    IncludeTargetProcessTree = 0,
    /// <summary>Capture everything except the target process tree.</summary>
    ExcludeTargetProcessTree = 1,
}

/// <summary>AUDIOCLIENT_ACTIVATION_PARAMS with the PROCESS_LOOPBACK union member inlined.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

/// <summary>
/// A PROPVARIANT holding a VT_BLOB. Sequential layout gives the correct offsets on both x64
/// (vt@0, cbSize@8, pBlobData@16, size 24) and x86 (vt@0, cbSize@8, pBlobData@12, size 16).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public uint cbSize;
    public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public static WaveFormatEx Create(ushort formatTag, int sampleRate, int channels, int bitsPerSample)
    {
        ushort blockAlign = (ushort)(channels * bitsPerSample / 8);
        return new WaveFormatEx
        {
            wFormatTag = formatTag,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * blockAlign),
            nBlockAlign = blockAlign,
            wBitsPerSample = (ushort)bitsPerSample,
            cbSize = 0,
        };
    }
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>
/// Marker interface. ActivateAudioInterfaceAsync requires the completion handler to be
/// free-threaded ("agile"), otherwise activation fails with E_ILLEGAL_METHOD_CALL.
/// Implementing this (method-less) interface on our managed handler makes QueryInterface for
/// IAgileObject succeed.
/// </summary>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

internal static class MmDevApi
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams, // PROPVARIANT*
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}
