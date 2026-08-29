using ProGPU.Direct2D;
using Xunit;

namespace ProGPU.Tests;

public sealed class Direct2DInteropContractTests
{
    [Fact]
    public void ManagedProviderUsesTypedAotSafeNativeAbi()
    {
        string project = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGPU.Direct2D.csproj");
        string native = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGpuDirect2DNative.cs");
        string d3dImage = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGpuDirect2DD3DImageSource.cs");

        Assert.Contains(
            "<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<IsAotCompatible>true</IsAotCompatible>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Backend.Native.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProGPU.Wpf.Interop.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "[LibraryImport(",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal const uint AbiVersion = 3U;",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_com_query_interface",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.GetDelegateForFunctionPointer",
            native,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeLibrary.Load",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "IPortableD3DImageSource",
            d3dImage,
            StringComparison.Ordinal);
        Assert.Contains(
            "IPortableInvalidationSource",
            d3dImage,
            StringComparison.Ordinal);
        Assert.Contains(
            "contentVersion == 0U",
            d3dImage,
            StringComparison.Ordinal);
        Assert.Contains(
            "_surface.TextureChanged += handler",
            d3dImage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            d3dImage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDrawScopeOwnsComAndKeyedMutexTransaction()
    {
        string header = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "include",
            "progpu_native_direct2d.h");
        string source = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "src",
            "Direct2D",
            "progpu_native_direct2d.cpp");
        string test = ReadRepoFile(
            "src",
            "ProGPU.Native",
            "tests",
            "progpu_native_direct2d_tests.cpp");

        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_ABI_VERSION = 3U",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_begin_draw",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_surface_end_draw",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_com_release",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "progpu_native_direct2d_com_query_interface",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_INTERFACE_NOT_SUPPORTED",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->BeginDraw();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface->d2d_context->EndDraw(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "release_locked(*surface, release_key, SUCCEEDED(draw_hr))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_ALREADY_ACTIVE",
            test,
            StringComparison.Ordinal);
        Assert.Contains(
            "PROGPU_NATIVE_DIRECT2D_STATUS_DRAW_NOT_ACTIVE",
            test,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DawnBridgeAlternatesGpuOwnershipWithoutCpuCopies()
    {
        string dawn = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnExplicitSharedTextureAccess.cs");
        string surface = ReadRepoFile(
            "src",
            "ProGPU.Direct2D",
            "ProGpuDirect2DSurface.cs");

        Assert.Contains(
            "public bool TryImportDxgiSharedTexture(",
            dawn,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedTextureMemory.ImportDXGISharedHandle(",
            dawn,
            StringComparison.Ordinal);
        Assert.Contains(
            "_access.EndAccess();",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceBeginDraw(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "SurfaceEndDraw(",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_access.BeginAccess(initialized: true);",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "IProGpuContextTextureLeaseSource",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_leaseCount != 0",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "_drawing = true;\n        }\n\n        DawnExplicitSharedTextureAccess?",
            surface,
            StringComparison.Ordinal);
        Assert.Contains(
            "nativeSurface = _nativeSurface;\n        }\n\n        ulong tag1",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CopyPixels",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReadPixels",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ToArray()",
            surface,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicContractKeepsComWindowsOnlyAndTyped()
    {
        var options = new ProGpuDirect2DSurfaceOptions(
            Width: 640U,
            Height: 480U,
            DpiX: 120.0F,
            DpiY: 120.0F,
            Flags: ProGpuDirect2DSurfaceFlags.AllowWarpFallback,
            AdapterLuid: 0x1234L);

        Assert.Equal(640U, options.Width);
        Assert.Equal(480U, options.Height);
        Assert.Equal(ProGpuDirect2DInterfaceKind.D2D1DeviceContext1, (ProGpuDirect2DInterfaceKind)13);
        Assert.Equal(ProGpuDirect2DStatus.DrawFailed, (ProGpuDirect2DStatus)12);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentNullException>(() =>
                ProGpuDirect2DSurface.Create(null!, options));
        }
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. pathParts]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Could not find repository file {Path.Combine(pathParts)}.");
    }
}
