using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetingAssistant.Platform.Windows.Audio;

/// Activates an IAudioClient for WASAPI *process* loopback (Windows 10 2004+ / build 19041; documented
/// for 20348+): everything played on the computer except a process tree — here the app itself, so
/// its own playback never ends up in the call track (spec 01-recording, "System audio" Windows notes).
/// Mirrors Microsoft's ApplicationLoopback sample: `ActivateAudioInterfaceAsync` on the virtual device
/// `VAD\Process_Loopback` with a PROPVARIANT (VT_BLOB) holding AUDIOCLIENT_ACTIVATION_PARAMS.
internal static class ProcessLoopback
{
    const string VirtualDevice = "VAD\\Process_Loopback";               // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    const ushort VT_BLOB = 65;
    const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    const int PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1;

    /// AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType; union { AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    /// { DWORD TargetProcessId; PROCESS_LOOPBACK_MODE ProcessLoopbackMode; } } } — 12 bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// PROPVARIANT with the BLOB arm: vt + 3 reserved WORDs, then BLOB { ULONG cbSize; BYTE* pBlobData }
    /// at offset 8 (16 bytes on x86, 24 on x64 — the same as the native union layout).
    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantBlob
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public uint cbSize;
        public nint pBlobData;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// Marker interface: the completion handler is called on an arbitrary (MTA worker) thread.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComVisible(true)]
    sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Result = unchecked((int)0x80004005);   // E_FAIL until completed
        public object? Interface;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int hr = op.GetActivateResult(out var activateResult, out var iface);
                Result = hr < 0 ? hr : activateResult;
                Interface = iface;
            }
            catch (Exception e)
            {
                Result = e.HResult;
            }
            finally
            {
                Done.Set();
            }
            return 0;
        }
    }

    /// An IAudioClient capturing all audio except <paramref name="processId"/>'s process tree
    /// (or only that tree with <paramref name="include"/>). Not yet initialized.
    public static AudioClient Activate(int processId, bool include, TimeSpan timeout)
    {
        var ap = new ActivationParams
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = include ? PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE : PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE,
        };
        nint apPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        nint pvPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        var handler = new CompletionHandler();
        bool completed = false, pending = false;
        try
        {
            Marshal.StructureToPtr(ap, apPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = VT_BLOB, cbSize = (uint)Marshal.SizeOf<ActivationParams>(), pBlobData = apPtr }, pvPtr, false);
            int hr = ActivateAudioInterfaceAsync(VirtualDevice, IID_IAudioClient, pvPtr, handler, out var op);
            Marshal.ThrowExceptionForHR(hr);
            pending = true;
            completed = handler.Done.Wait(timeout);
            GC.KeepAlive(op);
            if (!completed) throw new TimeoutException("Process loopback activation timed out.");
            Marshal.ThrowExceptionForHR(handler.Result);
            if (handler.Interface is not NAudio.CoreAudioApi.Interfaces.IAudioClient client)
                throw new COMException("Process loopback returned no IAudioClient.");
            return new AudioClient(client);
        }
        finally
        {
            // The parameters must stay valid until the activation completed.
            if (!pending || completed || handler.Done.IsSet)
            {
                Marshal.FreeHGlobal(pvPtr);
                Marshal.FreeHGlobal(apPtr);
            }
        }
    }
}
