using System;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using ProGPU.Backend;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackendContextCollection
{
    public const string Name = "ProGPU backend context";
}

[Collection(BackendContextCollection.Name)]
public sealed class AvaloniaBackendContextContractTests
{
    [Fact]
    public void ConstructionAndFramebufferSelectionAreGpuLazy()
    {
        int activeBefore = WgpuContext.ActiveContexts.Count;
        var surface = new StubFramebufferSurface(isReady: true);

        using var context = CreateContext();

        Assert.Equal(activeBefore, WgpuContext.ActiveContexts.Count);
        Assert.True(context.IsReadyToCreateRenderTarget([surface]));
        using IRenderTarget target =
            context.CreateRenderTarget([surface]);

        Assert.IsType<FramebufferRenderTarget>(target);
        Assert.Equal(1, surface.TargetCreationCount);
        Assert.Equal(activeBefore, WgpuContext.ActiveContexts.Count);
    }

    [Fact]
    public void OffscreenLayerReusesTheExistingWebGpuDevice()
    {
        using var bootstrap = new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Dpi = new Vector(96, 96)
            });
        WgpuContext selected =
            Assert.IsType<WgpuContext>(WgpuContext.Current);
        int activeBefore = WgpuContext.ActiveContexts.Count;
        using var context = CreateContext();

        using IDrawingContextLayerImpl layer =
            context.CreateOffscreenRenderTarget(
                new PixelSize(32, 24),
                new Vector(1, 1),
                enableTextAntialiasing: true);

        var surface = Assert.IsType<SurfaceRenderTarget>(layer);
        Assert.Same(selected, surface.Texture?.Context);
        Assert.Equal(activeBefore, WgpuContext.ActiveContexts.Count);
    }

    [Fact]
    public void ReadinessFollowsTheSelectedFramebufferSurface()
    {
        using var context = CreateContext();
        var unavailable = new StubFramebufferSurface(isReady: false);

        Assert.False(
            context.IsReadyToCreateRenderTarget([unavailable]));
    }

    private static ProGpuBackendContext CreateContext() =>
        new(
            platformGraphics: null,
            requireNativeCompositionScene: false,
            useDawnMetalPresentation: false,
            requireDawnMetalPresentation: false,
            useDawnNativePresentation: false,
            requireDawnNativePresentation: false);

    private sealed class StubFramebufferSurface :
        IFramebufferPlatformSurface
    {
        private readonly bool _isReady;

        internal StubFramebufferSurface(bool isReady)
        {
            _isReady = isReady;
        }

        public int TargetCreationCount { get; private set; }

        public bool IsReady => _isReady;

        public IFramebufferRenderTarget CreateFramebufferRenderTarget()
        {
            TargetCreationCount++;
            return new StubFramebufferTarget();
        }
    }

    private sealed class StubFramebufferTarget :
        IFramebufferRenderTarget
    {
        public PlatformRenderTargetState State =>
            PlatformRenderTargetState.Ready;

        public ILockedFramebuffer Lock(
            IRenderTarget.RenderTargetSceneInfo sceneInfo,
            out FramebufferLockProperties properties)
        {
            properties = default;
            throw new InvalidOperationException(
                "The lazy-selection contract must not lock the framebuffer.");
        }

        public void Dispose()
        {
        }
    }
}
