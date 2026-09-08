using System.Runtime.InteropServices;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public partial class NativePopupWindowWindowsTests
{
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires real Win32 HWNDs; run in the Windows qualification lane.";
        }
    }

    [WindowsFact]
    public void HiddenWindowGetsOwnerAndKeepsMouseClicksWithoutActivation()
    {
        nint owner = CreateWindowExW(0, "STATIC", "ProGPU owner fixture", 0, 0, 0, 32, 32, 0, 0, 0, 0);
        Assert.NotEqual(0, owner);
        nint popup = 0;
        try
        {
            popup = CreateWindowExW(0x00040000, "STATIC", "ProGPU popup fixture", 0x00cf0000,
                0, 0, 16, 16, 0, 0, 0, 0);
            Assert.NotEqual(0, popup);
            nint foreground = GetForegroundWindow();
            Assert.True(NativePopupWindow.TryConfigureOwner(
                new(NativeWindowKind.Win32, owner, 0, "HWND"), new(NativeWindowKind.Win32, popup, 0, "HWND")));
            Assert.Equal(owner, GetWindow(popup, 4)); // GW_OWNER
            uint style = ReadStyle(popup, -16);
            uint extended = ReadStyle(popup, -20);
            Assert.Equal(0x80000000u, style & 0xc0000000u); // popup, not child
            Assert.Equal(0u, style & 0x00cf0000u);
            Assert.Equal(0x08000080u, extended & 0x08040080u);
            Assert.Equal(0, IsWindowVisible(popup));
            Assert.Equal((nint)3, SendMessageW(popup, 0x0021, (nuint)owner, 0x02010001));
            Assert.Equal(foreground, GetForegroundWindow());
            // Destruction traverses the real subclass chain and removes its hook.
        }
        finally
        {
            if (popup != 0) _ = DestroyWindow(popup);
            _ = DestroyWindow(owner);
        }
    }

    private static uint ReadStyle(nint window, int index) => unchecked((uint)(nint.Size == 8
        ? GetWindowLongPtrW(window, index) : GetWindowLongW(window, index)));

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extended, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint window);
    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial nint SendMessageW(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);
    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(nint window, int index);
}
