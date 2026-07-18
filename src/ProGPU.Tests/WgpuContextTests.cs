using ProGPU.Backend;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using System.Reflection;
using Xunit;

namespace ProGPU.Tests;

public sealed class WgpuContextTests
{
    [Fact]
    public unsafe void SharedSurfaceRejectsUninitializedDeviceOwnerWithoutMutatingContext()
    {
        using var owner = new WgpuContext();
        using var surface = new WgpuContext();
        var window = DispatchProxy.Create<IWindow, DefaultDispatchProxy>();

        Assert.Throws<InvalidOperationException>(() => surface.InitializeSharedDevice(window, owner));
        Assert.True(surface.Instance == null);
        Assert.True(surface.Adapter == null);
        Assert.True(surface.Device == null);
        Assert.True(surface.Queue == null);
        Assert.True(surface.Surface == null);
    }
    [Fact]
    public void VsyncOffUsesImmediateWhenSurfaceAdvertisesIt()
    {
        var selected = WgpuContext.ChoosePresentMode(
            vsync: false,
            [PresentMode.Fifo, PresentMode.Immediate]);

        Assert.Equal(PresentMode.Immediate, selected);
    }

    [Fact]
    public void VsyncOffFallsBackToAdvertisedPresentModeWhenImmediateIsAbsent()
    {
        var selected = WgpuContext.ChoosePresentMode(
            vsync: false,
            [PresentMode.Fifo]);

        Assert.Equal(PresentMode.Fifo, selected);
    }

    [Fact]
    public void VsyncOnPrefersFifoWhenSurfaceAdvertisesIt()
    {
        var selected = WgpuContext.ChoosePresentMode(
            vsync: true,
            [PresentMode.Immediate, PresentMode.Fifo]);

        Assert.Equal(PresentMode.Fifo, selected);
    }

    [Fact]
    public void SurfaceConfigurationRequiresEveryCapabilityInventory()
    {
        Assert.True(WgpuContext.CanConfigureSurface(
            [TextureFormat.Bgra8Unorm],
            [CompositeAlphaMode.Opaque],
            [PresentMode.Fifo]));
        Assert.False(WgpuContext.CanConfigureSurface(
            [],
            [CompositeAlphaMode.Opaque],
            [PresentMode.Fifo]));
        Assert.False(WgpuContext.CanConfigureSurface(
            [TextureFormat.Bgra8Unorm],
            [],
            [PresentMode.Fifo]));
        Assert.False(WgpuContext.CanConfigureSurface(
            [TextureFormat.Bgra8Unorm],
            [CompositeAlphaMode.Opaque],
            []));
    }

    [Theory]
    [InlineData(15, 16u, 16u, 4u, true)]
    [InlineData(16, 16u, 16u, 4u, false)]
    [InlineData(16, 17u, 17u, 4u, true)]
    [InlineData(16, 17u, 16u, 4u, false)]
    [InlineData(16, 17u, 17u, 3u, false)]
    public void WpfShaderEffectMaskBindingFollowsDeviceLimits(
        int activeSamplerRegisterCount,
        uint maxSampledTexturesPerShaderStage,
        uint maxSamplersPerShaderStage,
        uint maxBindGroups,
        bool expected)
    {
        var canBind = WgpuContext.CanBindWpfShaderEffectMask(
            activeSamplerRegisterCount,
            maxSampledTexturesPerShaderStage,
            maxSamplersPerShaderStage,
            maxBindGroups);

        Assert.Equal(expected, canBind);
    }

    [Fact]
    public void PendingResourceSnapshotDropsDuplicateAndZeroPointers()
    {
        var method = typeof(WgpuContext).GetMethod(
            "SnapshotPendingResourcePointers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var pending = new List<IntPtr>
        {
            new(1),
            new(2),
            IntPtr.Zero,
            new(1),
            new(3),
            new(2)
        };

        var context = new WgpuContext();
        var snapshot = method.Invoke(context, [pending]);
        Assert.NotNull(snapshot);

        var length = Assert.IsType<int>(snapshot.GetType().GetProperty("Length")!.GetValue(snapshot));
        Assert.Equal(3, length);
        Assert.IsAssignableFrom<IDisposable>(snapshot).Dispose();
    }

    [Fact]
    public unsafe void GpuTextureFinalizerDoesNotQueueResourcesAgainWhenOwnerDisposesLater()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var texture = new GpuTexture(
            context,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "Finalizer idempotence test");
        var finalizeResources = typeof(GpuTexture).GetMethod(
            "FinalizeResources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(finalizeResources);

        finalizeResources.Invoke(texture, null);

        Assert.True(texture.IsDisposed);
        Assert.True(texture.TexturePtr == null);
        Assert.True(texture.ViewPtr == null);
        Assert.Single(context.PendingTextures);
        Assert.Single(context.PendingTextureViews);

        texture.Dispose();

        Assert.Single(context.PendingTextures);
        Assert.Single(context.PendingTextureViews);
        context.CleanupPendingResources();
        GC.SuppressFinalize(texture);
    }

    [Fact]
    public unsafe void PendingResourceCleanupDoesNotWaitForTheWholeDevice()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        var buffer = new GpuBuffer(
            context,
            256,
            BufferUsage.CopyDst,
            "Non-blocking retirement test");
        buffer.Dispose();
        long waitsBeforeCleanup = context.BlockingDeviceWaitCount;

        context.CleanupPendingResources();

        Assert.Equal(waitsBeforeCleanup, context.BlockingDeviceWaitCount);
        Assert.Empty(context.PendingBuffers);
    }

    [Fact]
    public void GpuTextureFinalizerToleratesPartiallyConstructedInstance()
    {
        var texture = (GpuTexture)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(GpuTexture));
        var finalizeResources = typeof(GpuTexture).GetMethod(
            "FinalizeResources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(finalizeResources);

        finalizeResources.Invoke(texture, null);

        Assert.True(texture.IsDisposed);
        GC.SuppressFinalize(texture);
    }

    private class DefaultDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
            {
                return null;
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    [Fact]
    public unsafe void VerifyShaderModuleFailsClosedWhenNativeCompilationInfoIsUnavailable()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        var codePtr = SilkMarshal.StringToPtr(
            """
            @vertex
            fn vs_main() -> @builtin(position) vec4<f32> {
                return vec4<f32>(0.0, 0.0, 0.0, 1.0);
            }

            @fragment
            fn fs_main() -> @location(0) vec4<f32> {
                return vec4<f32>(missing_symbol, 0.0, 0.0, 1.0);
            }
            """);
        var labelPtr = SilkMarshal.StringToPtr("InvalidWgslVerificationTest");
        ShaderModule* module = null;

        try
        {
            var wgslDesc = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct
                {
                    Next = null,
                    SType = SType.ShaderModuleWgslDescriptor
                },
                Code = (byte*)codePtr
            };

            var desc = new ShaderModuleDescriptor
            {
                NextInChain = (ChainedStruct*)&wgslDesc,
                Label = (byte*)labelPtr
            };

            module = context.Wgpu.DeviceCreateShaderModule(context.Device, &desc);
            Assert.True(module != null, "Expected WebGPU to create an invalid shader module so verification can exercise the unsupported-diagnostics path.");

            Assert.Equal(
                ShaderModuleVerificationStatus.Unavailable,
                context.GetShaderModuleVerificationStatus(module, out string errors));
            Assert.Contains("verification is unavailable", errors, StringComparison.Ordinal);
            Assert.False(context.VerifyShaderModule(module, out errors));
            Assert.Contains("verification is unavailable", errors, StringComparison.Ordinal);
        }
        finally
        {
            if (module != null)
            {
                context.Wgpu.ShaderModuleRelease(module);
            }

            SilkMarshal.Free(codePtr);
            SilkMarshal.Free(labelPtr);
        }
    }
}
