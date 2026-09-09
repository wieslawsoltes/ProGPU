using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend;

internal sealed partial class Win32CaretOperations : INativeWindowCaretOperations
{
    public bool IsLocalWindow(nint window)
    {
        uint thread = GetWindowThreadProcessId(window, out uint process);
        return window != 0 && thread == GetCurrentThreadId() && process == (uint)Environment.ProcessId;
    }

    public unsafe bool TryGetCaretWindow(out nint window)
    {
        GuiThreadInfo info = new() { Size = (uint)sizeof(GuiThreadInfo) };
        bool success = GetGUIThreadInfo(GetCurrentThreadId(), &info) != 0;
        window = info.Caret;
        return success;
    }

    public bool Create(nint window, int width, int height) => CreateCaret(window, 0, width, height) != 0;
    public bool SetPosition(int x, int y) => SetCaretPos(x, y) != 0;
    public bool Destroy() => DestroyCaret() != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CreateCaret(nint window, nint bitmap, int width, int height);
    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SetCaretPos(int x, int y);
    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DestroyCaret();
    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int GetGUIThreadInfo(uint thread, GuiThreadInfo* info);
    [LibraryImport("user32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetCurrentThreadId();
}
