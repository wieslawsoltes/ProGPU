using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed partial class Win32NativeWindowPlatform
{
    private const nuint PopupSubclassId = 0x50525050;
    private const uint PopupNoOwnerZOrder = 0x0200;
    internal static bool TryConfigurePopupOwner(nint owner, nint popup)
    {
        var api = new PopupOperations();
        if (Win32PopupConfiguration.Apply(owner, popup, ref api,
                out Win32PopupConfigurationFailure failure)) return true;
        TracePopupOwnerRejection(owner, popup, failure);
        return false;
    }

    internal static void TracePopupOwnerRejection(nint owner, nint popup,
        Win32PopupConfigurationFailure failure)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(
                "PROGPU_NATIVE_TRACE_POPUP_OWNER"), "1", StringComparison.Ordinal)) return;

        uint ownerProcess = 0;
        uint popupProcess = 0;
        uint ownerThread = owner != 0
            ? GetWindowThreadProcessId(owner, out ownerProcess)
            : 0;
        uint popupThread = popup != 0
            ? GetWindowThreadProcessId(popup, out popupProcess)
            : 0;
        // Include the actual native thread/process pairing only on rejected,
        // explicitly traced attempts. The ordinary popup path retains its
        // existing hidden-owner admission and never performs diagnostic reads.
        Console.Error.WriteLine(
            $"[ProGPU NativePopup Win32] rejected reason={failure} " +
            $"owner=0x{unchecked((nuint)owner):X} popup=0x{unchecked((nuint)popup):X} " +
            $"currentThread={GetCurrentThreadId()} ownerThread={ownerThread} popupThread={popupThread} " +
            $"currentProcess={Environment.ProcessId} ownerProcess={ownerProcess} popupProcess={popupProcess}");
    }

    private readonly unsafe struct PopupOperations : IWin32PopupOperations
    {
        public bool AreLocalWindows(nint owner, nint popup)
        {
            uint ownerThread = GetWindowThreadProcessId(owner, out uint ownerProcess);
            uint popupThread = GetWindowThreadProcessId(popup, out uint popupProcess);
            return ownerThread != 0 && ownerThread == popupThread && ownerThread == GetCurrentThreadId() &&
                ownerProcess == (uint)Environment.ProcessId && popupProcess == ownerProcess;
        }

        public bool TryRead(nint window, int index, out nint value)
        {
            // A zero previous value is valid; distinguish it from native failure.
            Marshal.SetLastPInvokeError(0);
            value = GetWindowLongPtr(window, index);
            return value != 0 || Marshal.GetLastPInvokeError() == 0;
        }

        public bool TryWrite(nint window, int index, nint value)
        {
            Marshal.SetLastPInvokeError(0);
            nint previous = SetWindowLongPtr(window, index, value);
            return previous != 0 || Marshal.GetLastPInvokeError() == 0;
        }

        public bool RefreshFrame(nint window) => SetWindowPos(window, 0, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | PopupNoOwnerZOrder | SwpNoActivate | SwpFrameChanged);

        public bool InstallNonActivationHook(nint window) =>
            SetWindowSubclass(window, &PopupSubclassProcedure, PopupSubclassId, 0) != 0;

        public void RemoveNonActivationHook(nint window) =>
            _ = RemoveWindowSubclass(window, &PopupSubclassProcedure, PopupSubclassId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint PopupSubclassProcedure(nint window, uint message,
        nuint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        // Keep the owner's active input queue and still deliver the mouse click.
        if (message == 0x0021) return 3; // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        if (message == 0x0082) // WM_NCDESTROY: remove only our lifetime hook.
            _ = RemoveWindowSubclass(window, &PopupSubclassProcedure, subclassId);
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [LibraryImport("comctl32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int SetWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int RemoveWindowSubclass(nint window,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);

    [LibraryImport("comctl32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetCurrentThreadId();
}
