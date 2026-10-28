using System.Runtime.InteropServices;
using ProGPU.Backend;

internal static partial class Program
{
    private static int Main()
    {
        Console.WriteLine("Parent=b65f592211a9f9b8c883ca06c99dd5f46583e829; no-input hidden STATIC probe; original process limit=60s");
        if (!OperatingSystem.IsWindows()) return 2;
        RunRaw();
        return RunProduct() ? 0 : 1;
    }

    private static void RunRaw()
    {
        nint owner = 0, popup = 0;
        nint foreground = GetForegroundWindow();
        try
        {
            CreatePair(out owner, out popup);
            Snapshot("raw-created", owner, popup, foreground);
            uint style = unchecked((uint)Read(popup, -16));
            uint extended = unchecked((uint)Read(popup, -20));
            uint wantedStyle = (style | 0x80000000u) & ~0x00CF0000u;
            uint wantedExtended = (extended | 0x08000080u) & ~0x00040000u;
            Console.WriteLine($"requested style=0x{wantedStyle:X8} extended=0x{wantedExtended:X8} owner=0x{(nuint)owner:X}");
            Write("owner", popup, -8, owner);
            Snapshot("raw-after-owner", owner, popup, foreground);
            Write("style", popup, -16, unchecked((nint)(int)wantedStyle));
            Snapshot("raw-after-style", owner, popup, foreground);
            Write("extended", popup, -20, unchecked((nint)(int)wantedExtended));
            Snapshot("raw-after-extended", owner, popup, foreground);
            SetLastError(0);
            int framed = SetWindowPos(popup, 0, 0, 0, 0, 0, 0x0237);
            int error = Marshal.GetLastPInvokeError();
            Console.WriteLine($"frame flags=0x00000237 result={framed} error={error}");
            Snapshot("raw-after-frame", owner, popup, foreground);
            if (framed == 0) throw new InvalidOperationException("Original frame refresh failed: " + error);
        }
        finally { DestroyPair(owner, popup); }
    }

    private static bool RunProduct()
    {
        nint owner = 0, popup = 0;
        nint foreground = GetForegroundWindow();
        try
        {
            CreatePair(out owner, out popup);
            Snapshot("product-created", owner, popup, foreground);
            bool accepted = NativePopupWindow.TryConfigureOwner(
                new(NativeWindowKind.Win32, owner, 0, "HWND"),
                new(NativeWindowKind.Win32, popup, 0, "HWND"));
            Console.WriteLine($"product accepted={accepted}");
            Snapshot("product-returned", owner, popup, foreground);
            return accepted;
        }
        finally { DestroyPair(owner, popup); }
    }

    private static void CreatePair(out nint owner, out nint popup)
    {
        owner = CreateWindowExW(0, "STATIC", "ProGPU owner fixture", 0, 0, 0, 32, 32, 0, 0, 0, 0);
        popup = CreateWindowExW(0x00040000, "STATIC", "ProGPU popup fixture", 0x00CF0000,
            0, 0, 16, 16, 0, 0, 0, 0);
        if (owner == 0 || popup == 0) throw new InvalidOperationException("CreateWindowExW failed.");
    }

    private static nint Read(nint window, int index)
    {
        SetLastError(0);
        nint value = GetWindowLongPtrW(window, index);
        int error = Marshal.GetLastPInvokeError();
        if (value == 0 && error != 0) throw new InvalidOperationException($"Native attribute read {index} failed: {error}");
        return value;
    }

    private static void Write(string name, nint window, int index, nint value)
    {
        SetLastError(0);
        nint previous = SetWindowLongPtrW(window, index, value);
        int error = Marshal.GetLastPInvokeError();
        Console.WriteLine($"write {name} index={index} requested=0x{(nuint)value:X} previous=0x{(nuint)previous:X} error={error}");
        if (previous == 0 && error != 0) throw new InvalidOperationException("Original attribute write failed.");
    }

    private static void Snapshot(string stage, nint owner, nint popup, nint originalForeground)
    {
        uint ownerThread = GetWindowThreadProcessId(owner, out uint ownerProcess);
        uint popupThread = GetWindowThreadProcessId(popup, out uint popupProcess);
        nint foreground = GetForegroundWindow();
        int ownerVisible = IsWindowVisible(owner), popupVisible = IsWindowVisible(popup);
        Console.WriteLine($"{stage}: style=0x{unchecked((uint)Read(popup, -16)):X8} extended=0x{unchecked((uint)Read(popup, -20)):X8} parent=0x{(nuint)Read(popup, -8):X} gwOwner=0x{(nuint)GetWindow(popup, 4):X} owner=0x{(nuint)owner:X} popup=0x{(nuint)popup:X} ownerThread={ownerThread} popupThread={popupThread} currentThread={GetCurrentThreadId()} ownerProcess={ownerProcess} popupProcess={popupProcess} currentProcess={Environment.ProcessId} visible={ownerVisible}/{popupVisible} foreground=0x{(nuint)foreground:X} originalForeground=0x{(nuint)originalForeground:X}");
        if (ownerVisible != 0 || popupVisible != 0 || foreground != originalForeground ||
            ownerThread != GetCurrentThreadId() || popupThread != ownerThread ||
            ownerProcess != Environment.ProcessId || popupProcess != ownerProcess)
            throw new InvalidOperationException("Hidden owned-window/foreground invariant changed.");
    }

    private static void DestroyPair(nint owner, nint popup)
    {
        if (popup != 0) Console.WriteLine($"destroy-popup={DestroyWindow(popup)}");
        if (owner != 0) Console.WriteLine($"destroy-owner={DestroyWindow(owner)}");
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extended, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetWindowLongPtrW(nint window, int index, nint value);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint GetWindowLongPtrW(nint window, int index);
    [LibraryImport("user32.dll", SetLastError = true)] private static partial int SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll")] private static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("user32.dll")] private static partial int DestroyWindow(nint window);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] private static partial void SetLastError(uint error);
}
