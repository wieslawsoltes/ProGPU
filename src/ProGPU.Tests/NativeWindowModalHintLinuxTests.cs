using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Windowing;
using Xunit;

namespace ProGPU.Tests;

[Collection("Native window input")]
public unsafe partial class NativeWindowModalHintLinuxTests
{
    private sealed class X11FactAttribute : FactAttribute
    {
        public X11FactAttribute()
        {
            if (!OperatingSystem.IsLinux() ||
                Environment.GetEnvironmentVariable("PROGPU_NATIVE_X11_MODAL_TEST") != "1")
                Skip = "Requires the explicit X11 modal qualification lane with an EWMH window manager.";
        }
    }

    [X11Fact]
    public void HiddenNativeDialogKeepsOwnerAndUnrelatedStateAcrossHintLifetimes()
    {
        var options = WindowOptions.Default;
        options.API = GraphicsAPI.None;
        options.IsVisible = false;
        using var owner = Window.Create(options);
        using var dialog = Window.Create(options);
        owner.Initialize();
        dialog.Initialize();
        using var ownerController = new SilkWindowController(owner);
        using var controller = new SilkWindowController(dialog);
        Assert.True(ownerController.Attach());
        Assert.True(controller.Attach());
        Assert.Equal(NativeWindowKind.X11, controller.Handle.Kind);
        Assert.True(controller.TrySetOwner(ownerController));
        nint display = controller.Handle.Display;
        nuint window = (nuint)controller.Handle.Handle;
        nuint state = XInternAtom(display, "_NET_WM_STATE", 0);
        nuint above = XInternAtom(display, "_NET_WM_STATE_ABOVE", 0);
        nuint modal = XInternAtom(display, "_NET_WM_STATE_MODAL", 0);
        SetStates(display, window, state, [above]);

        Assert.True(controller.TryBeginModalHint(out var hint));
        using (hint)
        {
            Assert.False(controller.TryBeginModalHint(out var duplicate));
            Assert.Null(duplicate);
            Assert.Contains(modal, ReadStates(display, window, state));
            Assert.Contains(above, ReadStates(display, window, state));
            Assert.Equal(ownerController.Handle, controller.Parent);
            // Advisory state cannot promote the native enabled-input capability.
            Assert.False(controller.SetInputAllowed(false));
            Assert.NotEqual(0, XGetTransientForHint(display, window, out nuint nativeOwner));
            Assert.Equal((nuint)ownerController.Handle.Handle, nativeOwner);
        }
        Assert.DoesNotContain(modal, ReadStates(display, window, state));
        Assert.Contains(above, ReadStates(display, window, state));

        SetStates(display, window, state, [above, modal]);
        Assert.True(controller.TryBeginModalHint(out var preexisting));
        preexisting!.Dispose();
        Assert.Contains(modal, ReadStates(display, window, state));
        var fullState = new nuint[64];
        for (int i = 0; i < fullState.Length; i++)
            fullState[i] = XInternAtom(display, $"_PROGPU_MODAL_FIXTURE_STATE_{i}", 0);
        SetStates(display, window, state, fullState);
        Assert.False(controller.TryBeginModalHint(out var overCapacity));
        Assert.Null(overCapacity);
        Assert.Equal(fullState, ReadStates(display, window, state));
        SetStates(display, window, state, [above]);
        Assert.True(controller.TryBeginModalHint(out var final));
        controller.Dispose(); // Release while the real window is still alive.
        Assert.True(final!.IsReleased);
        Assert.DoesNotContain(modal, ReadStates(display, window, state));
        Assert.Contains(above, ReadStates(display, window, state));
        Assert.False(dialog.IsVisible);
    }

    private static void SetStates(nint display, nuint window, nuint property, ReadOnlySpan<nuint> states)
    {
        fixed (nuint* data = states)
            _ = XChangeProperty(display, window, property, 4, 32, 0, (byte*)data, states.Length);
    }

    private static nuint[] ReadStates(nint display, nuint window, nuint property)
    {
        int result = XGetWindowProperty(display, window, property, 0, 64, 0, 4,
            out nuint type, out int format, out nuint count, out nuint remaining, out byte* data);
        try
        {
            Assert.Equal(0, result);
            Assert.Equal((nuint)4, type);
            Assert.Equal(32, format);
            Assert.Equal((nuint)0, remaining);
            Assert.InRange(count, (nuint)0, (nuint)64);
            return new ReadOnlySpan<nuint>(data, (int)count).ToArray();
        }
        finally { if (data != null) XFree(data); }
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(nint display, string name, int onlyExisting);
    [LibraryImport("libX11.so.6")]
    private static partial int XChangeProperty(nint display, nuint window, nuint property,
        nuint type, int format, int mode, byte* data, int count);
    [LibraryImport("libX11.so.6")]
    private static partial int XGetWindowProperty(nint display, nuint window, nuint property,
        nint offset, nint length, int delete, nuint requestedType, out nuint actualType,
        out int format, out nuint items, out nuint remaining, out byte* data);
    [LibraryImport("libX11.so.6")]
    private static partial int XFree(void* data);
    [LibraryImport("libX11.so.6")]
    private static partial int XGetTransientForHint(nint display, nuint window, out nuint owner);
}
