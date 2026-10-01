using System;
using ProGPU.Backend.Dawn;
using WebGpuSharp;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class DawnNativeWindowSourceTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(0u, true)]
    [InlineData(uint.MaxValue, false)]
    [InlineData(uint.MaxValue, true)]
    public void OffscreenCreationRequiresAnExplicitSupportedBackendBeforeNativeCreation(
        uint backend,
        bool forceFallbackAdapter)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => DawnGpuContext.CreateOffscreen(
                (Silk.NET.WebGPU.BackendType)backend,
                forceFallbackAdapter));

        Assert.Equal("backendType", error.ParamName);
        Assert.Equal((Silk.NET.WebGPU.BackendType)backend, error.ActualValue);
    }

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
