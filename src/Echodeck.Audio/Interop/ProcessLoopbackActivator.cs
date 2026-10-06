using System.Runtime.InteropServices;
using Echodeck.Audio.Capture;

namespace Echodeck.Audio.Interop;

/// <summary>
/// Creates an IAudioClient that captures one process tree, using the "VAD\Process_Loopback"
/// virtual device (Windows 10 2004+/Windows 11).
/// <para>
/// How it works: ActivateAudioInterfaceAsync takes a PROPVARIANT whose VT_BLOB points at an
/// AUDIOCLIENT_ACTIVATION_PARAMS { type = PROCESS_LOOPBACK, pid, INCLUDE_TARGET_PROCESS_TREE }.
/// Activation is asynchronous: Windows calls our completion handler on a worker thread, after
/// which GetActivateResult hands back the IAudioClient. We block (with a timeout) until then, so
/// callers get a simple synchronous API on their own MTA capture thread.
/// </para>
/// </summary>
internal static class ProcessLoopbackActivator
{
    public static IAudioClient Activate(int processId, ProcessLoopbackMode mode, TimeSpan timeout)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = mode,
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        bool freeMemory = true;
        IActivateAudioInterfaceAsyncOperation? operation = null;

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propVariant = new PropVariantBlob
            {
                vt = AudioClientConstants.VT_BLOB,
                cbSize = (uint)paramsSize,
                pBlobData = paramsPtr,
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new CompletionHandler();
            Guid iid = AudioClientConstants.IID_IAudioClient;
            int hr = MmDevApi.ActivateAudioInterfaceAsync(
                AudioClientConstants.VirtualAudioDeviceProcessLoopback, ref iid, propVariantPtr, handler, out operation);
            if (hr < 0)
                throw new AudioCaptureException($"ActivateAudioInterfaceAsync failed (0x{hr:X8}).", hr);

            if (!handler.Wait(timeout))
            {
                // Windows may still touch the parameter memory later; leaking 40 bytes is safer.
                freeMemory = false;
                throw new AudioCaptureException("Timed out waiting for process-loopback activation.", 0);
            }

            hr = operation.GetActivateResult(out int activateResult, out object? activated);
            if (hr < 0)
                throw new AudioCaptureException($"GetActivateResult failed (0x{hr:X8}).", hr);
            if (activateResult < 0 || activated is null)
                throw new AudioCaptureException($"Process-loopback activation failed (0x{activateResult:X8}).", activateResult);

            return (IAudioClient)activated;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new AudioCaptureException("This version of Windows does not support ActivateAudioInterfaceAsync.", 0, ex);
        }
        finally
        {
            if (operation is not null) Marshal.ReleaseComObject(operation);
            if (freeMemory)
            {
                Marshal.FreeHGlobal(paramsPtr);
                Marshal.FreeHGlobal(propVariantPtr);
            }
        }
    }

    /// <summary>
    /// Receives the activation callback. Must implement IAgileObject (see CoreAudioInterop.cs)
    /// or Windows rejects it.
    /// </summary>
    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _completed = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            _completed.Set();
            return 0; // S_OK
        }

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);
    }
}
