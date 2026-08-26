#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif
using System;
using Avalonia.Platform;
using ProGPU.Backend;
using Silk.NET.WebGPU;

namespace Avalonia.ProGpu;

/// <summary>
/// Render-target bitmap whose render attachment is also its sample source.
/// CPU storage is created only by Lock or Save.
/// </summary>
internal sealed class RenderTargetBitmapImpl :
    WriteableBitmapImpl,
    IRenderTargetBitmapImpl,
    IFramebufferPlatformSurface
{
    private readonly OffscreenTextureCache _commandCache = new();

    public RenderTargetBitmapImpl(PixelSize size, Vector dpi)
        : base(
            size,
            dpi,
            PixelFormats.Rgba8888,
            Avalonia.Platform.AlphaFormat.Premul,
            TextureUsage.TextureBinding |
            TextureUsage.RenderAttachment |
            TextureUsage.CopySrc |
            TextureUsage.CopyDst,
            "Avalonia render-target bitmap")
    {
    }

    internal bool HasIntermediateTexture =>
        _commandCache.CachedTexture is not null;

#if AVALONIA11
    IDrawingContextImpl IRenderTarget.CreateDrawingContext(
        bool useScaledDrawing) =>
        CreateContext(useScaledDrawing);
#else
    public IDrawingContextImpl CreateDrawingContext() =>
        CreateContext(useScaledDrawing: true);
#endif

    public bool IsCorrupted =>
        Texture?.Context is
        {
            IsDisposed: true
        } or
        {
            IsDeviceLost: true
        };

    public IFramebufferRenderTarget CreateFramebufferRenderTarget() =>
        new FuncFramebufferRenderTarget(Lock);

    public override void Dispose()
    {
        _commandCache.Dispose();
        base.Dispose();
    }

    private DrawingContextImpl CreateContext(bool useScaledDrawing)
    {
        WgpuContext context =
            WgpuContext.Current is
            {
                IsDisposed: false,
                IsDeviceLost: false
            } current
                ? current
                : AvaloniaGpuDevicePool.GetOrCreateStandalone(
                    TextureFormat.Rgba8Unorm);
        GpuTexture texture =
            GetTexture(context) ??
            throw new ObjectDisposedException(
                nameof(RenderTargetBitmapImpl));
        return new DrawingContextImpl(new DrawingContextImpl.CreateInfo
        {
            Size = PixelSize,
            Dpi = Dpi,
            ScaleDrawingToDpi = useScaledDrawing,
            CacheHolder = _commandCache,
            GpuRenderTarget = texture,
            GpuRenderSynchronizationLock = GpuRenderSynchronizationLock,
            GpuRenderStarting = MarkGpuContentChanged
        });
    }
}
