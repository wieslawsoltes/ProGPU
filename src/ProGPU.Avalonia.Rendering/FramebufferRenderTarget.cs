using System;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif

namespace Avalonia.ProGpu
{
    /// <summary>
    /// Render target that renders using the ProGpu drawing context.
    /// </summary>
    internal class FramebufferRenderTarget :
#if AVALONIA11
        IRenderTarget2
#else
        IRenderTarget
#endif
    {
        private readonly bool _useScaledDrawing;
        private IFramebufferRenderTarget? _renderTarget;
#if AVALONIA11
        private IFramebufferRenderTargetWithProperties? _renderTargetWithProperties;
#endif
        private readonly OffscreenTextureCache _textureCache;

        public FramebufferRenderTarget(
            IFramebufferPlatformSurface platformSurface,
            bool useScaledDrawing = false,
            bool requireNativeCompositionScene = false)
        {
            _useScaledDrawing = useScaledDrawing;
            _textureCache = new OffscreenTextureCache(
                requireNativeCompositionScene);
            _renderTarget = platformSurface.CreateFramebufferRenderTarget();
#if AVALONIA11
            _renderTargetWithProperties = _renderTarget as IFramebufferRenderTargetWithProperties;
#endif
        }

        public void Dispose()
        {
            _renderTarget?.Dispose();
            _renderTarget = null;
#if AVALONIA11
            _renderTargetWithProperties = null;
#endif
            _textureCache.Dispose();
        }

        public RenderTargetProperties Properties => new()
        {
            RetainsPreviousFrameContents =
#if AVALONIA11
                _renderTargetWithProperties?.RetainsFrameContents == true,
#else
                true,
#endif
            IsSuitableForDirectRendering = true
        };

        internal bool RequireNativeCompositionScene =>
            _textureCache.RequireNativeCompositionScene;

#if AVALONIA11
        public bool IsCorrupted => false;

        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing) =>
            CreateDrawingContextCore(useScaledDrawing, out _);

        public IDrawingContextImpl CreateDrawingContext(PixelSize expectedPixelSize,
            out RenderTargetDrawingContextProperties properties) =>
            CreateDrawingContextCore(_useScaledDrawing, out properties);

        private IDrawingContextImpl CreateDrawingContextCore(bool useScaledDrawing,
            out RenderTargetDrawingContextProperties properties)
#else
        public PlatformRenderTargetState PlatformRenderTargetState =>
            _renderTarget?.State ?? PlatformRenderTargetState.Disposed;

        public IDrawingContextImpl CreateDrawingContext(IRenderTarget.RenderTargetSceneInfo sceneInfo,
            out RenderTargetDrawingContextProperties properties)
#endif
        {
            if (_renderTarget == null)
                throw new ObjectDisposedException(nameof(FramebufferRenderTarget));

#if AVALONIA11
            FramebufferLockProperties lockProperties = default;
            var framebuffer = _renderTargetWithProperties?.Lock(out lockProperties) ?? _renderTarget.Lock();
#else
            var framebuffer = _renderTarget.Lock(sceneInfo, out _);
#endif

            var createInfo = new DrawingContextImpl.CreateInfo
            {
                Dpi = framebuffer.Dpi,
                ScaleDrawingToDpi =
#if AVALONIA11
                    useScaledDrawing,
#else
                    _useScaledDrawing,
#endif
                CacheHolder = _textureCache
            };

            properties = new()
            {
                PreviousFrameIsRetained =
#if AVALONIA11
                    lockProperties.PreviousFrameIsRetained
#else
                    false
#endif
            };

            return new DrawingContextImpl(createInfo, framebuffer);
        }
    }
}
