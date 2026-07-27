using System;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif

namespace Avalonia.ProGpu;

/// <summary>
/// Adapts an Avalonia CPU framebuffer surface to the ProGPU recording and
/// readback path.
/// </summary>
internal sealed class FramebufferRenderTarget :
#if AVALONIA11
    IRenderTarget2
#else
    IRenderTarget
#endif
{
    private readonly bool _scaleToDpi;
    private readonly OffscreenTextureCache _resources;
    private IFramebufferRenderTarget? _platformTarget;
#if AVALONIA11
    private IFramebufferRenderTargetWithProperties? _targetProperties;
#endif

    public FramebufferRenderTarget(
        IFramebufferPlatformSurface surface,
        bool useScaledDrawing = false,
        bool requireNativeCompositionScene = false)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _scaleToDpi = useScaledDrawing;
        _resources =
            new OffscreenTextureCache(requireNativeCompositionScene);
        _platformTarget = surface.CreateFramebufferRenderTarget();
#if AVALONIA11
        _targetProperties =
            _platformTarget as IFramebufferRenderTargetWithProperties;
#endif
    }

    public RenderTargetProperties Properties => new()
    {
        RetainsPreviousFrameContents =
#if AVALONIA11
            _targetProperties?.RetainsFrameContents == true,
#else
            true,
#endif
        IsSuitableForDirectRendering = true
    };

    internal bool RequireNativeCompositionScene =>
        _resources.RequireNativeCompositionScene;

#if AVALONIA11
    public bool IsCorrupted => false;

    public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing) =>
        CreateContext(useScaledDrawing, out _);

    public IDrawingContextImpl CreateDrawingContext(
        PixelSize expectedPixelSize,
        out RenderTargetDrawingContextProperties properties) =>
        CreateContext(_scaleToDpi, out properties);

    private IDrawingContextImpl CreateContext(
        bool scaleToDpi,
        out RenderTargetDrawingContextProperties properties)
    {
        IFramebufferRenderTarget target = _platformTarget ??
            throw new ObjectDisposedException(nameof(FramebufferRenderTarget));
        FramebufferLockProperties lockProperties = default;
        ILockedFramebuffer framebuffer =
            _targetProperties?.Lock(out lockProperties) ?? target.Lock();
        properties = new RenderTargetDrawingContextProperties
        {
            PreviousFrameIsRetained =
                lockProperties.PreviousFrameIsRetained
        };
        return CreateContext(framebuffer, scaleToDpi);
    }
#else
    public PlatformRenderTargetState PlatformRenderTargetState =>
        _platformTarget?.State ?? PlatformRenderTargetState.Disposed;

    public IDrawingContextImpl CreateDrawingContext(
        IRenderTarget.RenderTargetSceneInfo sceneInfo,
        out RenderTargetDrawingContextProperties properties)
    {
        IFramebufferRenderTarget target = _platformTarget ??
            throw new ObjectDisposedException(nameof(FramebufferRenderTarget));
        ILockedFramebuffer framebuffer = target.Lock(sceneInfo, out _);
        properties = new RenderTargetDrawingContextProperties
        {
            PreviousFrameIsRetained = false
        };
        return CreateContext(framebuffer, _scaleToDpi);
    }
#endif

    public void Dispose()
    {
        IFramebufferRenderTarget? target = _platformTarget;
        _platformTarget = null;
#if AVALONIA11
        _targetProperties = null;
#endif
        target?.Dispose();
        _resources.Dispose();
    }

    private DrawingContextImpl CreateContext(
        ILockedFramebuffer framebuffer,
        bool scaleToDpi)
    {
        return new DrawingContextImpl(
            new DrawingContextImpl.CreateInfo
            {
                Dpi = framebuffer.Dpi,
                ScaleDrawingToDpi = scaleToDpi,
                CacheHolder = _resources
            },
            framebuffer);
    }
}
