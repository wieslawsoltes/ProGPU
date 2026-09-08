using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed partial class Win32NativeWindowPlatform
{
    public override bool SetEnabled(bool value)
    {
        var operations = new WindowEnabledOperations();
        return Win32WindowEnabledState.Apply(_hwnd, value, ref operations);
    }

    private readonly struct WindowEnabledOperations : IWin32WindowEnabledOperations
    {
        public bool IsLocalWindow(nint window)
        {
            uint thread = GetWindowThreadProcessId(window, out uint process);
            return thread != 0 && thread == GetCurrentThreadId() && process == (uint)Environment.ProcessId;
        }

        public void SetEnabled(nint window, bool enabled) => _ = EnableWindow(window, enabled ? 1 : 0);
        public bool IsEnabled(nint window) => IsWindowEnabled(window) != 0;
    }

    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int EnableWindow(nint window, int enabled);

    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int IsWindowEnabled(nint window);
}
