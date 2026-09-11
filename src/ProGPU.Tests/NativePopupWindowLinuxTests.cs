using System.Runtime.InteropServices;
using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

[Collection("Native window input")]
public unsafe partial class NativePopupWindowLinuxTests
{
    private sealed class X11FactAttribute : FactAttribute
    {
        public X11FactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PROGPU_NATIVE_X11_POPUP_TEST") != "1")
                Skip = "Requires the explicit X11 popup server-state lane (Xvfb or a desktop X server).";
        }
    }

    [X11Fact]
    public void HiddenPopupConfirmsOwnerFlagsTypesAndPreservesGeometryAndEvents()
    {
        nint display = XOpenDisplay(null);
        Assert.NotEqual(0, display); // Requested qualification must not silently skip missing DISPLAY.
        nuint owner = 0, popup = 0;
        try
        {
            nuint root = XDefaultRootWindow(display);
            owner = XCreateSimpleWindow(display, root, 11, 13, 80, 60, 0, 0, 0);
            popup = XCreateSimpleWindow(display, root, 31, 37, 24, 18, 0, 0, 0);
            Assert.NotEqual((nuint)0, owner);
            Assert.NotEqual((nuint)0, popup);
            _ = XSelectInput(display, popup, (nint)1 << 17);
            var ownerHandle = new NativeWindowHandle(NativeWindowKind.X11, (nint)owner, display, "XID");
            var popupHandle = new NativeWindowHandle(NativeWindowKind.X11, (nint)popup, display, "XID");
            Assert.True(NativePopupWindow.TryConfigureOwner(ownerHandle, popupHandle));
            Assert.True(NativePopupWindow.TryConfigureOwner(ownerHandle, popupHandle)); // Idempotent, still hidden.
            Assert.NotEqual(0, XGetTransientForHint(display, popup, out nuint actualOwner));
            Assert.Equal(owner, actualOwner);
            Assert.Equal(136, sizeof(X11NativeWindowPlatform.XWindowAttributes));
            X11NativeWindowPlatform.XWindowAttributes state = default;
            Assert.NotEqual(0, XGetWindowAttributes(display, popup, &state));
            Assert.Equal((31, 37, 24, 18), (state.X, state.Y, state.Width, state.Height));
            Assert.Equal(0, state.MapState);
            Assert.Equal(1, state.OverrideRedirect);
            Assert.Equal((nint)1 << 17, state.YourEventMask);
            nuint property = XInternAtom(display, "_NET_WM_WINDOW_TYPE", 1);
            int result = XGetWindowProperty(display, popup, property, 0, 2, 0, 4,
                out nuint type, out int format, out nuint count, out nuint remaining, out byte* data);
            try
            {
                Assert.Equal(0, result);
                Assert.Equal((nuint)4, type);
                Assert.Equal(32, format);
                Assert.Equal((nuint)2, count);
                Assert.Equal((nuint)0, remaining);
                Assert.True(data != null);
                Assert.Equal(XInternAtom(display, "_NET_WM_WINDOW_TYPE_DROPDOWN_MENU", 1), ((nuint*)data)[0]);
                Assert.Equal(XInternAtom(display, "_NET_WM_WINDOW_TYPE_POPUP_MENU", 1), ((nuint*)data)[1]);
            }
            finally { if (data != null) XFree(data); }
            X11NativeWindowPlatform.XWindowAttributes ownerState = default;
            Assert.NotEqual(0, XGetWindowAttributes(display, owner, &ownerState));
            Assert.Equal(0, ownerState.OverrideRedirect);
            Assert.Equal((11, 13, 80, 60), (ownerState.X, ownerState.Y, ownerState.Width, ownerState.Height));
        }
        finally
        {
            if (popup != 0) XDestroyWindow(display, popup);
            if (owner != 0) XDestroyWindow(display, owner);
            XCloseDisplay(display);
        }
    }

    [X11Fact]
    public void MappedAndChildWindowsAreRejectedBeforeChangingOwner()
    {
        nint display = XOpenDisplay(null);
        Assert.NotEqual(0, display);
        nuint owner = 0, popup = 0, child = 0;
        try
        {
            nuint root = XDefaultRootWindow(display);
            owner = XCreateSimpleWindow(display, root, 0, 0, 80, 60, 0, 0, 0);
            popup = XCreateSimpleWindow(display, root, 0, 0, 24, 18, 0, 0, 0);
            child = XCreateSimpleWindow(display, owner, 0, 0, 10, 10, 0, 0, 0);
            Assert.True(owner != 0 && popup != 0 && child != 0);
            var ownerHandle = new NativeWindowHandle(NativeWindowKind.X11, (nint)owner, display, "XID");
            Assert.True(NativePopupWindow.TryConfigureOwner(ownerHandle,
                new(NativeWindowKind.X11, (nint)popup, display, "XID")));
            // Override-redirect maps directly even with a WM present, so this
            // fixture does not race a pending WM-owned MapRequest.
            _ = XMapWindow(display, popup);
            Assert.False(NativePopupWindow.TryConfigureOwner(ownerHandle,
                new(NativeWindowKind.X11, (nint)popup, display, "XID")));
            Assert.False(NativePopupWindow.TryConfigureOwner(ownerHandle,
                new(NativeWindowKind.X11, (nint)child, display, "XID")));
            Assert.NotEqual(0, XGetTransientForHint(display, popup, out nuint retainedOwner));
            Assert.Equal(owner, retainedOwner);
            Assert.Equal(0, XGetTransientForHint(display, child, out _));
        }
        finally
        {
            if (child != 0) XDestroyWindow(display, child);
            if (popup != 0) XDestroyWindow(display, popup);
            if (owner != 0) XDestroyWindow(display, owner);
            XCloseDisplay(display);
        }
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(byte* name);
    [LibraryImport("libX11.so.6")]
    private static partial nuint XDefaultRootWindow(nint display);
    [LibraryImport("libX11.so.6")]
    private static partial nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y,
        uint width, uint height, uint borderWidth, nuint border, nuint background);
    [LibraryImport("libX11.so.6")]
    private static partial int XMapWindow(nint display, nuint window);
    [LibraryImport("libX11.so.6")]
    private static partial int XSelectInput(nint display, nuint window, nint events);
    [LibraryImport("libX11.so.6")]
    private static partial int XGetWindowAttributes(nint display, nuint window,
        X11NativeWindowPlatform.XWindowAttributes* attributes);
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(nint display, string name, int onlyExisting);
    [LibraryImport("libX11.so.6")]
    private static partial int XGetWindowProperty(nint display, nuint window, nuint property,
        nint offset, nint length, int delete, nuint requestedType, out nuint actualType,
        out int format, out nuint items, out nuint remaining, out byte* data);
    [LibraryImport("libX11.so.6")]
    private static partial int XGetTransientForHint(nint display, nuint window, out nuint owner);
    [LibraryImport("libX11.so.6")]
    private static partial int XFree(void* data);
    [LibraryImport("libX11.so.6")]
    private static partial int XDestroyWindow(nint display, nuint window);
    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);
}
