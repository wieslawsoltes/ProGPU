#if !AVALONIA11
using System;
using System.Threading;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;

namespace Avalonia.ProGpu;

/// <summary>
/// Keeps package-mode Avalonia Native usable when its drawable IOSurface
/// cannot be imported by the pinned Dawn build.
/// </summary>
internal sealed class DawnFallbackRenderTarget : IRenderTarget
{
    private IRenderTarget? _dawn;
    private readonly IFramebufferPlatformSurface _framebufferSurface;
    private readonly bool _requireNativeCompositionScene;
    private Action? _disableDawnPresentation;
    private FramebufferRenderTarget? _framebuffer;

    internal DawnFallbackRenderTarget(
        IRenderTarget dawn,
        IFramebufferPlatformSurface framebufferSurface,
        bool requireNativeCompositionScene,
        Action disableDawnPresentation)
    {
        _dawn = dawn ??
            throw new ArgumentNullException(nameof(dawn));
        _framebufferSurface = framebufferSurface ??
            throw new ArgumentNullException(nameof(framebufferSurface));
        _requireNativeCompositionScene =
            requireNativeCompositionScene;
        _disableDawnPresentation = disableDawnPresentation ??
            throw new ArgumentNullException(
                nameof(disableDawnPresentation));
    }

    public RenderTargetProperties Properties =>
        _framebuffer?.Properties ??
        _dawn?.Properties ??
        default;

    public PlatformRenderTargetState PlatformRenderTargetState =>
        _framebuffer?.PlatformRenderTargetState ??
        _dawn?.PlatformRenderTargetState ??
        PlatformRenderTargetState.Disposed;

    public IDrawingContextImpl CreateDrawingContext(
        IRenderTarget.RenderTargetSceneInfo sceneInfo,
        out RenderTargetDrawingContextProperties properties)
    {
        if (_framebuffer is not null)
        {
            return _framebuffer.CreateDrawingContext(
                sceneInfo,
                out properties);
        }

        IRenderTarget dawn = _dawn ??
            throw new ObjectDisposedException(
                nameof(DawnFallbackRenderTarget));
        try
        {
            return dawn.CreateDrawingContext(
                sceneInfo,
                out properties);
        }
        catch (NotSupportedException)
        {
            dawn.Dispose();
            _dawn = null;
            Interlocked.Exchange(
                ref _disableDawnPresentation,
                null)?.Invoke();
            _framebuffer = new FramebufferRenderTarget(
                _framebufferSurface,
                requireNativeCompositionScene:
                    _requireNativeCompositionScene);
            return _framebuffer.CreateDrawingContext(
                sceneInfo,
                out properties);
        }
    }

    public void Dispose()
    {
        _dawn?.Dispose();
        _dawn = null;
        _disableDawnPresentation = null;
        _framebuffer?.Dispose();
        _framebuffer = null;
    }
}
#endif
