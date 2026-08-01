using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkGrContextCompatibilityTests
{
    [Fact]
    public void ContextOptionsAndBackendDescriptorsRoundTripWithoutOwningHandles()
    {
        var options = new GRContextOptions
        {
            AllowPathMaskCaching = true,
            AvoidStencilBuffers = true,
            BufferMapThreshold = 4_096,
            DoManualMipmapping = true,
            GlyphCacheTextureMaximumBytes = 8_192,
            RuntimeProgramCacheSize = 64,
        };
        Assert.True(options.AllowPathMaskCaching);
        Assert.True(options.AvoidStencilBuffers);
        Assert.Equal(4_096, options.BufferMapThreshold);
        Assert.True(options.DoManualMipmapping);
        Assert.Equal(8_192, options.GlyphCacheTextureMaximumBytes);
        Assert.Equal(64, options.RuntimeProgramCacheSize);

        using var direct3D = new GRD3DBackendContext
        {
            Adapter = (IntPtr)1,
            Device = (IntPtr)2,
            Queue = (IntPtr)3,
            ProtectedContext = true,
        };
        using var metal = new GRMtlBackendContext
        {
            DeviceHandle = (IntPtr)4,
            QueueHandle = (IntPtr)5,
        };
        using var vulkan = new GRVkBackendContext
        {
            VkInstance = (IntPtr)6,
            VkPhysicalDevice = (IntPtr)7,
            VkDevice = (IntPtr)8,
            VkQueue = (IntPtr)9,
            GraphicsQueueIndex = 2,
            MaxAPIVersion = 3,
            ProtectedContext = true,
        };

        Assert.Equal((IntPtr)1, direct3D.Adapter);
        Assert.Equal((IntPtr)5, metal.QueueHandle);
        Assert.Equal((IntPtr)8, vulkan.VkDevice);
        Assert.Equal(2u, vulkan.GraphicsQueueIndex);
    }

    [Fact]
    public void GlAndVulkanHelpersOwnOnlyTheirManagedCompatibilityState()
    {
        using var gl = GRGlInterface.Create(_ => (IntPtr)1);
        Assert.True(gl.Validate());
        Assert.False(gl.HasExtension("GL_EXT_framebuffer_object"));

        using var extensions = GRVkExtensions.Create(
            (_, _, _) => (IntPtr)1,
            (IntPtr)2,
            (IntPtr)3,
            ["VK_KHR_surface"],
            ["VK_KHR_swapchain"]);
        extensions.HasExtension("VK_KHR_surface", 1);
        Assert.NotEqual(IntPtr.Zero, extensions.Handle);
    }

    [Fact]
    public void AbandonmentAndCacheBudgetRemainLocalToTheWrapper()
    {
        using var first = GRContext.CreateGl();
        using var second = new GRContext(first.Context);
        using var dump = new RecordingDump();

        first.SetResourceCacheLimit(32L * 1024 * 1024);
        Assert.Equal(32L * 1024 * 1024, first.GetResourceCacheLimit());
        Assert.Throws<ArgumentOutOfRangeException>(() => first.SetResourceCacheLimit(-1));
        first.GetResourceCacheUsage(out var resourceCount, out var resourceBytes);
        Assert.True(resourceCount >= 0);
        Assert.Equal(0, resourceBytes);
        first.DumpMemoryStatistics(dump);
        Assert.Equal(3, dump.NumericCount);
        Assert.Equal("ProGPU WebGPU", dump.Backend);

        first.ResetContext();
        first.ResetContext(GRGlBackendState.TextureBinding);
        first.ResetContext(17u);
        first.PurgeUnlockedResources(scratchResourcesOnly: true);
        first.PurgeUnlockedResources(0, preferScratchResources: false);
        first.PurgeUnusedResources(0);
        first.AbandonContext();

        Assert.True(first.IsAbandoned);
        Assert.False(second.IsAbandoned);
        Assert.False(first.Context.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => first.Flush());
    }

    private sealed class RecordingDump : SKTraceMemoryDump
    {
        public RecordingDump()
            : base(detailedDump: true, dumpWrappedObjects: false)
        {
        }

        public int NumericCount { get; private set; }

        public string? Backend { get; private set; }

        protected internal override void OnDumpNumericValue(
            string dumpName,
            string valueName,
            string units,
            ulong value) => NumericCount++;

        protected internal override void OnDumpStringValue(
            string dumpName,
            string valueName,
            string value) => Backend = value;
    }
}
