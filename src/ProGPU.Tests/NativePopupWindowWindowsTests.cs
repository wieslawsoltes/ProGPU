using System.Runtime.CompilerServices;
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
            Assert.True(ConfigureOwnerPreservingActivationContext(
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

    [WindowsFact]
    public unsafe void FrameCallbackCannotReplaceTheConfiguredOwnerAndReportSuccess()
    {
        nint owner = CreateWindowExW(0, "STATIC", "ProGPU reentrant owner", 0,
            0, 0, 32, 32, 0, 0, 0, 0);
        nint otherOwner = 0;
        nint popup = 0;
        Assert.NotEqual(0, owner);
        try
        {
            otherOwner = CreateWindowExW(0, "STATIC", "ProGPU replacement owner", 0,
                0, 0, 32, 32, 0, 0, 0, 0);
            Assert.NotEqual(0, otherOwner);
            popup = CreateWindowExW(0x00040000, "STATIC", "ProGPU reentrant popup", 0x00cf0000,
                0, 0, 16, 16, 0, 0, 0, 0);
            Assert.NotEqual(0, popup);
            uint originalStyle = ReadStyle(popup, -16);
            uint originalExtended = ReadStyle(popup, -20);
            _frameCallbackReowned = false;
            Assert.NotEqual(0, SetWindowSubclass(popup, &ReownerDuringFrameCallback, 1, (nuint)otherOwner));

            Assert.False(ConfigureOwnerPreservingActivationContext(
                new(NativeWindowKind.Win32, owner, 0, "HWND"), new(NativeWindowKind.Win32, popup, 0, "HWND")));
            Assert.True(_frameCallbackReowned);
            Assert.Equal((nint)0, GetWindow(popup, 4));
            Assert.Equal(originalStyle, ReadStyle(popup, -16));
            Assert.Equal(originalExtended, ReadStyle(popup, -20));
            Assert.Equal(0, IsWindowVisible(popup));
        }
        finally
        {
            if (popup != 0) _ = DestroyWindow(popup);
            if (otherOwner != 0) _ = DestroyWindow(otherOwner);
            _ = DestroyWindow(owner);
        }
    }

    [ThreadStatic]
    private static bool _frameCallbackReowned;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint ReownerDuringFrameCallback(nint window, uint message,
        nuint wParam, nint lParam, nuint id, nuint referenceData)
    {
        if (message != 0x0083) return DefSubclassProc(window, message, wParam, lParam); // WM_NCCALCSIZE
        // One-shot, hidden, same-thread mutation after the original procedure.
        // No input synthesis, message pumping or process-global hook is involved.
        _ = RemoveWindowSubclass(window, &ReownerDuringFrameCallback, id);
        nint result = DefSubclassProc(window, message, wParam, lParam);
        if (nint.Size == 8) _ = SetWindowLongPtrW(window, -8, (nint)referenceData);
        else _ = SetWindowLongW(window, -8, (int)referenceData);
        _frameCallbackReowned = GetWindow(window, 4) == (nint)referenceData;
        return result;
    }

    [WindowsFact]
    public void SystemMenuRejectsForeignThreadsAndChildWindowsBeforeTracking()
    {
        nint owner = CreateWindowExW(0, "STATIC", "ProGPU system menu owner", 0x00cf0000,
            0, 0, 32, 32, 0, 0, 0, 0);
        Assert.NotEqual(0, owner);
        nint child = 0;
        try
        {
            child = CreateWindowExW(0, "STATIC", "ProGPU child", 0x40000000,
                0, 0, 16, 16, owner, 0, 0, 0);
            Assert.NotEqual(0, child);
            Assert.False(NativeWindowSystemMenu.TryShow(new(NativeWindowKind.Win32, child, 0, "HWND"), default));
            bool? accepted = null;
            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { accepted = NativeWindowSystemMenu.TryShow(new(NativeWindowKind.Win32, owner, 0, "HWND"), default); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
            Assert.Equal(false, accepted);
        }
        finally
        {
            if (child != 0) _ = DestroyWindow(child);
            _ = DestroyWindow(owner);
        }
    }

    private static uint ReadStyle(nint window, int index) => unchecked((uint)(nint.Size == 8
        ? GetWindowLongPtrW(window, index) : GetWindowLongW(window, index)));

    private static bool ConfigureOwnerPreservingActivationContext(NativeWindowHandle owner, NativeWindowHandle popup)
    {
        Assert.NotEqual(0, GetCurrentActCtx(out nint original));
        nint after = 0;
        try
        {
            bool accepted = NativePopupWindow.TryConfigureOwner(owner, popup);
            Assert.NotEqual(0, GetCurrentActCtx(out after));
            Assert.Equal(original, after);
            return accepted;
        }
        finally
        {
            if (after != 0) ReleaseActCtx(after);
            if (original != 0) ReleaseActCtx(original);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint extended, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);
    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentActCtx(out nint context);
    [LibraryImport("kernel32.dll")]
    private static partial void ReleaseActCtx(nint context);
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
    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);
    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(nint window, int index, int value);
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
}
