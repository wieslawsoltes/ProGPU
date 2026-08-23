using ProGPU.Backend;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class WindowCustomizationTests
{
    [Fact]
    public void CapabilityMatrixDoesNotAdvertiseUnsupportedWaylandOperations()
    {
        var windows = NativeWindowCapabilities.ForKind(NativeWindowKind.Win32);
        var macOs = NativeWindowCapabilities.ForKind(NativeWindowKind.Cocoa);
        var x11 = NativeWindowCapabilities.ForKind(NativeWindowKind.X11);
        var wayland = NativeWindowCapabilities.ForKind(NativeWindowKind.Wayland);

        Assert.True(windows.Supports(NativeWindowFeatures.Mica | NativeWindowFeatures.MoveDrag));
        Assert.True(windows.Supports(NativeWindowFeatures.CloseButton));
        Assert.True(macOs.Supports(NativeWindowFeatures.Mica | NativeWindowFeatures.ResizeDrag));
        Assert.True(macOs.Supports(NativeWindowFeatures.CloseButton));
        Assert.True(x11.Supports(NativeWindowFeatures.Acrylic | NativeWindowFeatures.Taskbar));
        Assert.True(x11.Supports(NativeWindowFeatures.CloseButton));
        Assert.False(x11.Supports(NativeWindowFeatures.Mica));
        Assert.True(wayland.Supports(NativeWindowFeatures.ClientAreaExtension));
        Assert.False(wayland.Supports(NativeWindowFeatures.MoveDrag));
        Assert.False(wayland.Supports(NativeWindowFeatures.Taskbar));
        Assert.False(wayland.Supports(NativeWindowFeatures.Mica));
        Assert.False(wayland.Supports(NativeWindowFeatures.CloseButton));
    }

    [Fact]
    public void TransparentSurfacePrefersPremultipliedAlpha()
    {
        var modes = new[]
        {
            CompositeAlphaMode.Opaque,
            CompositeAlphaMode.Unpremultiplied,
            CompositeAlphaMode.Premultiplied
        };

        var selected = WgpuContext.ChooseCompositeAlphaMode(true, modes);

        Assert.Equal(CompositeAlphaMode.Premultiplied, selected);
    }

    [Fact]
    public void OpaqueSurfacePrefersOpaqueAlpha()
    {
        var modes = new[]
        {
            CompositeAlphaMode.Premultiplied,
            CompositeAlphaMode.Opaque
        };

        var selected = WgpuContext.ChooseCompositeAlphaMode(false, modes);

        Assert.Equal(CompositeAlphaMode.Opaque, selected);
    }

}
