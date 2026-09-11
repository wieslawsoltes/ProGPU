using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed partial class Win32NativeWindowPlatform
{
    internal static bool TryShowSystemMenu(nint owner, NativeWindowPoint position)
    {
        var api = new SystemMenuOperations();
        return Win32SystemMenu.Show(owner, position, ref api);
    }

    private readonly struct SystemMenuOperations : IWin32SystemMenuOperations
    {
        public bool TryGetLocalMenu(nint owner, out nint menu)
        {
            menu = 0;
            uint thread = GetWindowThreadProcessId(owner, out uint process);
            if (thread == 0 || thread != GetCurrentThreadId() || process != (uint)Environment.ProcessId)
                return false;
            uint style = unchecked((uint)GetWindowLongPtr(owner, -16));
            if ((style & 0x40000000u) != 0) return false; // WS_CHILD
            menu = GetSystemMenu(owner, 0);
            return menu != 0;
        }

        public bool RightAligned => GetSystemMetrics(40) != 0; // SM_MENUDROPALIGNMENT

        public bool TryTrack(nint owner, nint menu, NativeWindowPoint position, uint flags, out uint command)
        {
            Marshal.SetLastPInvokeError(0);
            command = TrackPopupMenuEx(menu, flags, position.X, position.Y, owner, 0);
            return command != 0 || Marshal.GetLastPInvokeError() == 0;
        }

        public bool PostSystemCommand(nint owner, uint command) =>
            PostMessage(owner, 0x0112, unchecked((nint)command), 0) != 0;
    }

    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial nint GetSystemMenu(nint owner, int revert);

    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
}
