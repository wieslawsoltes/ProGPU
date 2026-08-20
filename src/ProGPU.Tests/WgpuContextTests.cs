using ProGPU.Backend;
using ProGPU.Browser;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;
using System.Reflection;
using Xunit;

namespace ProGPU.Tests;

public sealed class WgpuContextTests
{
    [Fact]
    public void LinuxBackendPreferenceChoosesAcceleratedGlOverCpuVulkan()
    {
        LinuxWebGpuAdapterCandidate[] candidates =
        [
            new(AdapterType.Cpu, BackendType.Vulkan),
            new(AdapterType.IntegratedGpu, BackendType.OpenGL)
        ];

        Assert.Equal(
            BackendType.OpenGL,
            LinuxWebGpuBackendPreference.Choose(candidates));
    }

    [Fact]
    public void LinuxBackendPreferenceKeepsAcceleratedVulkan()
    {
        LinuxWebGpuAdapterCandidate[] candidates =
        [
            new(AdapterType.IntegratedGpu, BackendType.OpenGL),
            new(AdapterType.IntegratedGpu, BackendType.Vulkan)
        ];

        Assert.Equal(
            BackendType.Vulkan,
            LinuxWebGpuBackendPreference.Choose(candidates));
    }

    [Fact]
    public void LinuxBackendPreferenceUsesCpuVulkanAsLastResort()
    {
        LinuxWebGpuAdapterCandidate[] candidates =
        [new(AdapterType.Cpu, BackendType.Vulkan)];

        Assert.Equal(
            BackendType.Vulkan,
            LinuxWebGpuBackendPreference.Choose(candidates));
    }

    [Fact]
    public void DeviceLossInvalidatesExistingContextsButNotReplacements()
    {
        var existing = new WgpuContext();

        Assert.False(existing.IsDeviceLost);

        WgpuContext.RaiseWebGpuDeviceLost(
            DeviceLostReason.Destroyed,
            "normal disposal");

        Assert.False(existing.IsDeviceLost);

        WgpuContext.RaiseWebGpuDeviceLost(
            DeviceLostReason.Unknown,
            "synthetic test loss");

        Assert.True(existing.IsDeviceLost);
        Assert.False(existing.IsInitialized);
        Assert.False(new WgpuContext().IsDeviceLost);
    }

    [Fact]
    public unsafe void ExternalNativeDeviceUsesTypedPollingAndOwnership()
    {
        using var api = new BrowserWebGpuApi(_ => { });
        var lifetime = new RecordingExternalDeviceLifetime();
        var context = new WgpuContext();

        context.InitializeExternalNativeDevice(
            api,
            lifetime,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            TextureFormat.Bgra8Unorm,
            supportsTextureFormatsTier1: true,
            adapterBackendType: BackendType.Metal,
            adapterName: "Test Dawn Metal",
            adapterType: AdapterType.IntegratedGpu,
            adapterDriverDescription: "Test Metal Driver",
            adapterVendorId: 0x106B,
            adapterDeviceId: 0x1234);

        Assert.True(context.IsInitialized);
        Assert.Equal(WgpuBackendKind.DawnNative, context.BackendKind);
        Assert.Equal(BackendType.Metal, context.AdapterBackendType);
        Assert.Equal("Test Dawn Metal", context.AdapterName);
        Assert.Equal(
            new WgpuAdapterSelectionDiagnostics(
                "Test Dawn Metal",
                BackendType.Metal,
                AdapterType.IntegratedGpu,
                "Test Metal Driver",
                0x106B,
                0x1234,
                false,
                WgpuAdapterSelectionReason.ExternalNativeHost),
            context.AdapterSelectionDiagnostics);
        Assert.True(context.SupportsTextureFormatsTier1);

        context.PollDevice(wait: false);
        context.WaitIdle();

        Assert.Equal(1, lifetime.NonBlockingPollCount);
        Assert.Equal(1, lifetime.WaitingPollCount);

        context.Dispose();

        Assert.True(lifetime.IsDisposed);
        Assert.Equal(2, lifetime.WaitingPollCount);
        Assert.False(context.IsInitialized);
    }

    [Fact]
    public unsafe void PendingWebGpuReferenceCleanupDoesNotWaitForTheQueue()
    {
        using var api = new BrowserWebGpuApi(_ => { });
        var lifetime = new RecordingExternalDeviceLifetime();
        using var context = new WgpuContext();
        context.InitializeExternalNativeDevice(
            api,
            lifetime,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            TextureFormat.Bgra8Unorm);
        using (var texture = new GpuTexture(
                   context,
                   4,
                   4,
                   TextureFormat.Rgba8Unorm,
                   TextureUsage.TextureBinding | TextureUsage.CopyDst))
        {
        }

        context.CleanupPendingResources();

        Assert.Equal(0, lifetime.WaitingPollCount);
        Assert.Empty(context.PendingTextureViews);
        Assert.Empty(context.PendingTextures);
    }

    [Fact]
    public unsafe void ExternalTextureOwnerCleanupWaitsForTheQueue()
    {
        using var api = new BrowserWebGpuApi(_ => { });
        var lifetime = new RecordingExternalDeviceLifetime();
        using var context = new WgpuContext();
        context.InitializeExternalNativeDevice(
            api,
            lifetime,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            TextureFormat.Bgra8Unorm);
        var owner = new RecordingDisposable();
        context.QueueExternalTextureOwnerDisposal(owner);

        context.CleanupPendingResources();

        Assert.Equal(1, lifetime.WaitingPollCount);
        Assert.True(owner.IsDisposed);
    }

    [Fact]
    public unsafe void DeferredWebGpuReferenceCleanupDrainsAtABoundedInterval()
    {
        using var api = new BrowserWebGpuApi(_ => { });
        var lifetime = new RecordingExternalDeviceLifetime();
        using var context = new WgpuContext();
        context.InitializeExternalNativeDevice(
            api,
            lifetime,
            BrowserWebGpuApi.DeviceHandle,
            BrowserWebGpuApi.QueueHandle,
            TextureFormat.Bgra8Unorm);

        for (var index = 0; index < 8; index++)
        {
            using (var texture = new GpuTexture(
                       context,
                       4,
                       4,
                       TextureFormat.Rgba8Unorm,
                       TextureUsage.TextureBinding | TextureUsage.CopyDst))
            {
            }

            var commandBuffer = (CommandBuffer*)1;
            context.Submit(1, &commandBuffer);
            context.CleanupPendingResources();
            if (index < 7)
            {
                Assert.Equal(0, lifetime.WaitingPollCount);
            }
        }

        Assert.Equal(1, lifetime.WaitingPollCount);
        Assert.Equal(3, lifetime.NonBlockingPollCount);
    }

    [Fact]
    public void Tier1TextureFormatsFailBeforeBackendAllocationWhenUnsupported()
    {
        using var context = new WgpuContext();
        context.Initialize(null);

        Assert.False(
            context.SupportsTextureFormatsTier1);
        NotSupportedException error =
            Assert.Throws<NotSupportedException>(
                () => new GpuTexture(
                    context,
                    4,
                    4,
                    ProGpuTextureFormats.R16Unorm,
                    TextureUsage.TextureBinding));

        Assert.Contains(
            "texture-formats-tier1",
            error.Message,
            StringComparison.Ordinal);
        Assert.NotEqual(
            ProGpuTextureFormats.R16Unorm,
            ProGpuTextureFormats.RG16Unorm);
    }

    [Fact]
    public unsafe void ExternalTextureOwnerIsReleasedOnlyAfterGpuQueueCleanup()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        var descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D
            {
                Width = 4,
                Height = 4,
                DepthOrArrayLayers = 1
            },
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1
        };
        Texture* nativeTexture =
            context.Api.DeviceCreateTexture(
                context.Device,
                &descriptor);
        Assert.True(nativeTexture != null);
        var owner = new RecordingDisposable();
        var texture = GpuTexture.WrapOwnedExternal(
            context,
            nativeTexture,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding,
            externalOwner: owner);

        texture.Dispose();

        Assert.False(owner.IsDisposed);
        Assert.Single(context.PendingExternalTextureOwners);
        Assert.Single(context.PendingTextures);
        Assert.Single(context.PendingTextureViews);

        context.CleanupPendingResources();

        Assert.True(owner.IsDisposed);
        Assert.Empty(context.PendingExternalTextureOwners);
    }

    [Fact]
    public unsafe void ContextDisposalReleasesLiveExternalTextureOwner()
    {
        var context = new WgpuContext();
        context.Initialize(null);
        var descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D
            {
                Width = 4,
                Height = 4,
                DepthOrArrayLayers = 1
            },
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1
        };
        Texture* nativeTexture =
            context.Api.DeviceCreateTexture(
                context.Device,
                &descriptor);
        Assert.True(nativeTexture != null);
        var owner = new RecordingDisposable();
        var texture = GpuTexture.WrapOwnedExternal(
            context,
            nativeTexture,
            4,
            4,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding,
            externalOwner: owner);

        context.Dispose();

        Assert.True(texture.IsDisposed);
        Assert.True(owner.IsDisposed);
        texture.Dispose();
    }

    [Fact]
    public void FailedExternalTextureImportLeavesNativeOwnerWithCaller()
    {
        using var context = new WgpuContext();
        var importer = new RejectingExternalTextureImporter();
        context.SetExternalTextureImporter(importer);
        var owner = new RecordingDisposable();
        var descriptor = new ProGpuExternalTextureDescriptor(
            ProGpuExternalTextureHandleKind.IOSurface,
            1,
            4,
            4,
            TextureFormat.Bgra8Unorm,
            TextureUsage.TextureBinding,
            GpuTextureAlphaMode.Premultiplied,
            IsInitialized: true);

        bool imported = context.TryImportExternalTexture(
            in descriptor,
            owner,
            out GpuTexture texture);

        Assert.False(imported);
        Assert.Null(texture);
        Assert.False(owner.IsDisposed);
        Assert.Same(context, importer.LastContext);
    }

    [Fact]
    public void ChooseSurfaceFormat_PrefersNonSrgbRgbaOverSrgbFirstEntry()
    {
        TextureFormat selected = WgpuContext.ChooseSurfaceFormat(
            [TextureFormat.Rgba8UnormSrgb, TextureFormat.Rgba8Unorm]);

        Assert.Equal(TextureFormat.Rgba8Unorm, selected);
    }

    [Fact]
    public void ChooseSurfaceFormat_PrefersBgraNonSrgbWhenBothEncodedFormatsExist()
    {
        TextureFormat selected = WgpuContext.ChooseSurfaceFormat(
            [TextureFormat.Rgba8Unorm, TextureFormat.Bgra8Unorm]);

        Assert.Equal(TextureFormat.Bgra8Unorm, selected);
    }

    [Fact]
    public void ChooseSurfaceFormat_FallsBackToFirstAdvertisedFormat()
    {
        TextureFormat selected = WgpuContext.ChooseSurfaceFormat(
            [TextureFormat.Rgba16float, TextureFormat.Rgba8UnormSrgb]);

        Assert.Equal(TextureFormat.Rgba16float, selected);
    }

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
        Assert.False(owner.SharesDeviceWith(surface));
    }

    [Fact]
    public void InitializedContextHasStableTypedDeviceIdentity()
    {
        using var first = new WgpuContext();
        using var second = new WgpuContext();
        first.Initialize(null);
        second.Initialize(null);

        Assert.True(first.SharesDeviceWith(first));
        Assert.False(first.SharesDeviceWith(second));
        Assert.False(second.SharesDeviceWith(first));
    }

    [Fact]
    public unsafe void ImmutableLayoutsAreSharedAndReferenceCountedPerDevice()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        var bindGroupKey = new WgpuDeviceResourceKey(
            "ProGPU.Tests",
            "UniformLayout");
        var pipelineKey = new WgpuDeviceResourceKey(
            "ProGPU.Tests",
            "UniformPipelineLayout");
        var entry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform,
                HasDynamicOffset = false,
                MinBindingSize = 16
            }
        };
        var bindGroupDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 1,
            Entries = &entry
        };

        using var firstBindGroupLayout =
            context.AcquireSharedBindGroupLayout(
                bindGroupKey,
                &bindGroupDescriptor);
        using var secondBindGroupLayout =
            context.AcquireSharedBindGroupLayout(
                bindGroupKey,
                &bindGroupDescriptor);

        Assert.True(
            firstBindGroupLayout.Handle ==
            secondBindGroupLayout.Handle);
        Assert.Equal(1, context.CachedDeviceBindGroupLayoutCount);

        BindGroupLayout* bindGroupLayout =
            firstBindGroupLayout.Handle;
        var layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = bindGroupLayout;
        var pipelineDescriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = layouts
        };
        using var firstPipelineLayout =
            context.AcquireSharedPipelineLayout(
                pipelineKey,
                &pipelineDescriptor);
        using var secondPipelineLayout =
            context.AcquireSharedPipelineLayout(
                pipelineKey,
                &pipelineDescriptor);

        Assert.True(
            firstPipelineLayout.Handle ==
            secondPipelineLayout.Handle);
        Assert.Equal(1, context.CachedDevicePipelineLayoutCount);

        firstPipelineLayout.Dispose();
        firstBindGroupLayout.Dispose();

        Assert.Equal(1, context.CachedDevicePipelineLayoutCount);
        Assert.Equal(1, context.CachedDeviceBindGroupLayoutCount);

        secondPipelineLayout.Dispose();
        secondBindGroupLayout.Dispose();

        Assert.Equal(0, context.CachedDevicePipelineLayoutCount);
        Assert.Equal(0, context.CachedDeviceBindGroupLayoutCount);
        context.CleanupPendingResources();
    }

    [Fact]
    public unsafe void SharedLayoutKeyRejectsAbiCollision()
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        var key = new WgpuDeviceResourceKey(
            "ProGPU.Tests",
            "Collision");
        var firstEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout
            {
                Type = BufferBindingType.Uniform
            }
        };
        var secondEntry = firstEntry;
        secondEntry.Buffer.Type =
            BufferBindingType.ReadOnlyStorage;
        var firstDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 1,
            Entries = &firstEntry
        };
        var secondDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 1,
            Entries = &secondEntry
        };
        using var lease = context.AcquireSharedBindGroupLayout(
            key,
            &firstDescriptor);

        InvalidOperationException? error = null;
        try
        {
            using var unexpected =
                context.AcquireSharedBindGroupLayout(
                    key,
                    &secondDescriptor);
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Contains(
            "different ABI",
            error.Message,
            StringComparison.Ordinal);
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

    private sealed class RecordingExternalDeviceLifetime
        : IWebGpuExternalDeviceLifetime
    {
        public int NonBlockingPollCount { get; private set; }
        public int WaitingPollCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Poll(bool wait)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (wait)
            {
                WaitingPollCount++;
            }
            else
            {
                NonBlockingPollCount++;
            }
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
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

    private sealed class RecordingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            Assert.False(IsDisposed);
            IsDisposed = true;
        }
    }

    private sealed class RejectingExternalTextureImporter :
        IProGpuExternalTextureImporter
    {
        public WgpuContext? LastContext { get; private set; }

        public bool TryImportExternalTexture(
            WgpuContext targetContext,
            in ProGpuExternalTextureDescriptor descriptor,
            IDisposable nativeOwner,
            out GpuTexture texture)
        {
            LastContext = targetContext;
            texture = null!;
            return false;
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
