using Xunit;

namespace ProGPU.Tests;

public sealed class DawnSharedTextureMemoryContractTests
{
    [Fact]
    public void DawnBackendUsesStaticAotCompatibleInterop()
    {
        string project = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "ProGPU.Backend.Dawn.csproj");
        string source = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnSharedTextureMemory.cs");

        Assert.Contains(
            "<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "[LibraryImport(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeLibrary.",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetProcAddress",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTextureAccessUsesExactTypedDawnContracts()
    {
        string source = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnSharedTextureMemory.cs");

        Assert.Contains(
            "DawnNativeEnumBase + 36",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DawnNativeEnumBase + 42",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SType.SharedTextureMemoryIOSurfaceDescriptor",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SType.SharedTextureMemoryMetalEndAccessState",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SType.SharedFenceMTLSharedEventExportInfo",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public TextureHandle CreateTexture(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSharedObjectsAndReturnedArraysAreReleasedDeterministically()
    {
        string source = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnSharedTextureMemory.cs");
        string probe = ReadRepoFile(
            "tools",
            "ProGPU.DawnSharedMemoryProbe",
            "Program.cs");

        Assert.Contains(
            "SharedTextureMemoryEndAccessStateFreeMembers(state);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedTextureMemoryRelease(handle);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedFenceRelease(handle);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DawnMetalEndAccessResult destination",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (previous == sharedEvent)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "using TextureHandle texture",
            probe,
            StringComparison.Ordinal);
        Assert.Contains(
            "api.CommandBufferRelease(commandBuffer);",
            probe,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DawnCoreBackendTranslatesAbiAndOwnsDeviceDeterministically()
    {
        string api = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnWebGpuApi.cs");
        string context = ReadRepoFile(
            "src",
            "ProGPU.Backend.Dawn",
            "DawnGpuContext.cs");
        string probe = ReadRepoFile(
            "tools",
            "ProGPU.DawnSharedMemoryProbe",
            "Program.cs");

        Assert.Contains(
            "class DawnWebGpuApi : IWebGpuApi",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "SW.LoadOp.Clear => W.LoadOp.Clear",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "SW.TextureDimension.Dimension2D =>",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "W.BufferBindingType.BindingNotUsed",
            api,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "NativeLibrary.",
            api,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "System.Reflection",
            api,
            StringComparison.Ordinal);

        Assert.Contains(
            "WebGPU_FFI.CreateInstance",
            context,
            StringComparison.Ordinal);
        Assert.Contains(
            "W.InstanceFeatureName.TimedWaitAny",
            context,
            StringComparison.Ordinal);
        Assert.True(
            context.IndexOf("queue.Release();", StringComparison.Ordinal) <
            context.IndexOf("device.Destroy();", StringComparison.Ordinal));
        Assert.True(
            context.IndexOf("device.Release();", StringComparison.Ordinal) <
            context.IndexOf("adapter.Release();", StringComparison.Ordinal));
        Assert.True(
            context.IndexOf("adapter.Release();", StringComparison.Ordinal) <
            context.IndexOf("instance.Release();", StringComparison.Ordinal));

        Assert.Contains(
            "DawnGpuContext.CreateMetalPresentation()",
            probe,
            StringComparison.Ordinal);
        Assert.Contains(
            "compositor.RenderScene(root, width, height, view);",
            probe,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidatePixel(",
            probe,
            StringComparison.Ordinal);
        Assert.Contains(
            "ForceDeviceLossForDiagnostics()",
            context,
            StringComparison.Ordinal);
        Assert.Contains(
            "EntryPoint = \"wgpuDeviceForceLoss\"",
            context,
            StringComparison.Ordinal);
        Assert.Contains(
            "--force-device-loss",
            probe,
            StringComparison.Ordinal);
        Assert.Contains(
            "replacement.Context.IsDeviceLost",
            probe,
            StringComparison.Ordinal);
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
