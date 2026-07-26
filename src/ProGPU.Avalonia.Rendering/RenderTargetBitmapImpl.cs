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

internal class RenderTargetBitmapImpl : WriteableBitmapImpl,
    IRenderTargetBitmapImpl,
    IFramebufferPlatformSurface
{
    private readonly OffscreenTextureCache _textureCache = new();
    internal bool HasIntermediateTexture =>
        _textureCache.CachedTexture != null;

    public RenderTargetBitmapImpl(PixelSize size, Vector dpi) : base(size, dpi,
        PixelFormats.Rgba8888,
        Platform.AlphaFormat.Premul,
        TextureUsage.TextureBinding |
        TextureUsage.RenderAttachment |
        TextureUsage.CopySrc |
        TextureUsage.CopyDst)
    {
        InitializeGpuTexture("Avalonia render-target bitmap");
    }

#if AVALONIA11
    IDrawingContextImpl IRenderTarget.CreateDrawingContext(bool useScaledDrawing) =>
        CreateDrawingContextCore(useScaledDrawing);
#else
    public IDrawingContextImpl CreateDrawingContext()
    {
        return CreateDrawingContextCore(useScaledDrawing: true);
    }
#endif

    private IDrawingContextImpl CreateDrawingContextCore(bool useScaledDrawing)
    {
        var texture = Texture ??
            throw new ObjectDisposedException(nameof(RenderTargetBitmapImpl));
        return new DrawingContextImpl(new DrawingContextImpl.CreateInfo
        {
            Size = PixelSize,
            Dpi = Dpi,
            ScaleDrawingToDpi = useScaledDrawing,
            CacheHolder = _textureCache,
            GpuRenderTarget = texture,
            GpuRenderSynchronizationLock =
                this.GpuRenderSynchronizationLock,
            GpuRenderStarting = MarkGpuContentChanged
        });
    }

    public bool IsCorrupted => false;

    public override void Dispose()
    {
        _textureCache.Dispose();
        base.Dispose();
    }

    public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new FuncFramebufferRenderTarget(Lock);
}
