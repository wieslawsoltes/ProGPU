using System;
using System.IO;
using ProGPU.Backend.Dawn;
using WebGpuSharp;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class DawnNativeWindowSourceTests
{
    // Pure path guards only: these never configure or load a provider and do
    // not establish the existing file's native ABI or live module identity.
    [Fact]
    public void ProviderLibraryPathRejectsNull()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(
            () => DawnNativeProvider.ValidateLibraryPath(null!));

        Assert.Equal("absolutePath", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("webgpu_dawn.dll")]
    [InlineData(".")]
    [InlineData("../webgpu_dawn.so")]
    public void ProviderLibraryPathRejectsEmptyOrRelativePaths(string path)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => DawnNativeProvider.ValidateLibraryPath(path));

        Assert.Equal("absolutePath", error.ParamName);
    }

    [Fact]
    public void ProviderLibraryPathRejectsMissingAbsoluteFile()
    {
        string directory = Path.GetDirectoryName(typeof(DawnNativeWindowSourceTests).Assembly.Location)!;
        string path = Path.Combine(directory, $"DawnProviderMissingFile.{Guid.NewGuid():N}");
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.False(File.Exists(path));

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => DawnNativeProvider.ValidateLibraryPath(path));

        Assert.Equal(Path.GetFullPath(path), error.FileName);
    }

    [Fact]
    public void ProviderLibraryPathRejectsExistingDirectory()
    {
        string directory = Path.GetDirectoryName(typeof(DawnNativeWindowSourceTests).Assembly.Location)!;
        Assert.True(Directory.Exists(directory));

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => DawnNativeProvider.ValidateLibraryPath(directory));

        Assert.Equal(Path.GetFullPath(directory), error.FileName);
    }

    [Fact]
    public void ProviderLibraryPathNormalizesExistingOwnAssemblyFileWithoutLoadingIt()
    {
        string assemblyPath = typeof(DawnNativeWindowSourceTests).Assembly.Location;
        Assert.True(Path.IsPathFullyQualified(assemblyPath));
        Assert.True(File.Exists(assemblyPath));
        string path = Path.Combine(Path.GetDirectoryName(assemblyPath)!, ".", Path.GetFileName(assemblyPath));

        string normalized = DawnNativeProvider.ValidateLibraryPath(path);

        Assert.Equal(Path.GetFullPath(assemblyPath), normalized);
        Assert.True(File.Exists(normalized));
    }

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
