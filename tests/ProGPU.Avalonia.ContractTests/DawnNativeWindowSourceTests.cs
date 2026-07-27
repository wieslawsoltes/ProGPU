using System;
using ProGPU.Backend.Dawn;
using WebGpuSharp;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class DawnNativeWindowSourceTests
{
    [Theory]
    [InlineData("HWND", true, false, DawnNativeWindowKind.Win32)]
    [InlineData("XID", false, true, DawnNativeWindowKind.Xlib)]
    public void TypedAvaloniaHandleMapsToNativeDawnBackend(
        string descriptor,
        bool isWindows,
        bool isLinux,
        DawnNativeWindowKind expected)
    {
        Assert.True(
            DawnNativeWindowSource.TryGetKind(
                descriptor,
                isWindows,
                isLinux,
                out DawnNativeWindowKind actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("HWND", false, true)]
    [InlineData("XID", true, false)]
    [InlineData("NSWindow", false, false)]
    [InlineData("", true, false)]
    public void MismatchedOrUnknownHandleIsRejected(
        string descriptor,
        bool isWindows,
        bool isLinux)
    {
        Assert.False(
            DawnNativeWindowSource.TryGetKind(
                descriptor,
                isWindows,
                isLinux,
                out _));
    }

    [Fact]
    public void VulkanPresentationRequiresOpaqueAlpha()
    {
        CompositeAlphaMode selected = DawnGpuContext.SelectAlphaMode(
            new[]
            {
                CompositeAlphaMode.Premultiplied,
                CompositeAlphaMode.Opaque
            },
            BackendType.Vulkan);

        Assert.Equal(CompositeAlphaMode.Opaque, selected);
        Assert.Throws<NotSupportedException>(
            () => DawnGpuContext.SelectAlphaMode(
                new[] { CompositeAlphaMode.Premultiplied },
                BackendType.Vulkan));
    }

    [Fact]
    public void NonVulkanPresentationKeepsPremultipliedPreference()
    {
        CompositeAlphaMode selected = DawnGpuContext.SelectAlphaMode(
            new[]
            {
                CompositeAlphaMode.Opaque,
                CompositeAlphaMode.Premultiplied
            },
            BackendType.D3D12);

        Assert.Equal(CompositeAlphaMode.Premultiplied, selected);
    }
}
