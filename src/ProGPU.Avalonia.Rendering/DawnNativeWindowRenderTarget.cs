#if !AVALONIA11
using System;
using Avalonia.Platform;
using ProGPU.Backend.Dawn;

namespace Avalonia.ProGpu;

/// <summary>
/// Presents ProGPU directly to an Avalonia-owned HWND or XID through Dawn,
/// while retaining Avalonia's windowing, input, lifetime, and platform
/// services.
/// </summary>
/// <remarks>
/// Each frame acquires the Dawn swapchain texture and renders into it directly.
/// Work is O(C + G) for the compositor's commands and glyphs, with O(1)
/// presentation overhead, no CPU framebuffer readback, and no full-frame copy.
/// </remarks>
internal sealed class DawnNativeWindowRenderTarget : IRenderTarget
{
    private readonly INativePlatformHandleSurface _platformSurface;
    private readonly DawnNativeWindowSource _windowSource;
    private readonly DawnNativePresentationSurface _presentationSurface;
    private readonly OffscreenTextureCache _textureCache;
    private bool _corrupted;
    private bool _disposed;

    internal DawnNativeWindowRenderTarget(
        INativePlatformHandleSurface platformSurface,
        DawnNativeWindowSource windowSource,
        DawnGpuContext dawnContext,
        bool requireNativeCompositionScene)
    {
        _platformSurface = platformSurface ??
            throw new ArgumentNullException(nameof(platformSurface));
        _windowSource = windowSource ??
            throw new ArgumentNullException(nameof(windowSource));
        ArgumentNullException.ThrowIfNull(dawnContext);

        try
        {
            _presentationSurface =
                dawnContext.CreatePresentationSurface(windowSource);
        }
        catch
        {
            windowSource.Dispose();
            throw;
        }
        _textureCache = new OffscreenTextureCache(
            requireNativeCompositionScene);
    }

    public RenderTargetProperties Properties => new()
    {
        RetainsPreviousFrameContents = false,
        IsSuitableForDirectRendering = true
    };

    public PlatformRenderTargetState PlatformRenderTargetState =>
        _disposed
            ? PlatformRenderTargetState.Disposed
            : _corrupted || _presentationSurface.IsDeviceLost
                ? PlatformRenderTargetState.Corrupted
            : _platformSurface.IsReady
                ? PlatformRenderTargetState.Ready
                : PlatformRenderTargetState.NotReadyTryLater;

    public IDrawingContextImpl CreateDrawingContext(
        IRenderTarget.RenderTargetSceneInfo sceneInfo,
        out RenderTargetDrawingContextProperties properties)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_presentationSurface.IsDeviceLost)
        {
            throw new RenderTargetCorruptedException(
                "The Dawn presentation device is lost.");
        }
        PixelSize size = _platformSurface.Size;
        if (!_platformSurface.IsReady ||
            size.Width <= 0 ||
            size.Height <= 0)
        {
            throw new RenderTargetNotReadyException();
        }

        DawnNativePresentationFrame? frame = null;
        try
        {
            frame = _presentationSurface.Acquire(
                checked((uint)size.Width),
                checked((uint)size.Height));
            properties = new RenderTargetDrawingContextProperties
            {
                PreviousFrameIsRetained = false
            };
            return new DrawingContextImpl(
                new DrawingContextImpl.CreateInfo
                {
                    Size = size,
                    Dpi = new Vector(
                        _platformSurface.Scaling * 96.0,
                        _platformSurface.Scaling * 96.0),
                    ScaleDrawingToDpi = false,
                    CacheHolder = _textureCache,
                    GpuRenderTarget = frame.Texture,
                    PresentationPath =
                        _windowSource.Kind ==
                        DawnNativeWindowKind.Win32
                            ? "DawnD3D12HWND"
                            : "DawnVulkanXlib",
                    GpuRenderCompleted = frame.Complete
                },
                frame);
        }
        catch (TimeoutException exception)
        {
            frame?.Dispose();
            throw new RenderTargetNotReadyException(exception);
        }
        catch (Exception exception)
        {
            frame?.Dispose();
            _corrupted = true;
            throw new RenderTargetCorruptedException(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _textureCache.Dispose();
        _presentationSurface.Dispose();
        _windowSource.Dispose();
        _disposed = true;
    }
}
#endif
