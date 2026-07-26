using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Server;
#endif
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;

namespace Avalonia.ProGpu
{
    internal partial class DrawingContextImpl : IDrawingContextImpl,
        IDrawingContextWithAcrylicLikeSupport,
        IDrawingContextImplWithEffects
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        , ICompositionRenderDataDrawingContextFeature
        , ICompositionVisualTreeDrawingContextFeature
#endif
    {
        private const string ProGpuSurfaceHandleDescriptor = "WGPU_SURFACE";
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        private static readonly bool s_useRetainedAvaloniaScene =
            !string.Equals(
                Environment.GetEnvironmentVariable("PROGPU_AVALONIA_RETAINED_SCENE"),
                "0",
                StringComparison.Ordinal);
#endif
        private static readonly bool s_useDirectPresentationSurface =
            !string.Equals(
                Environment.GetEnvironmentVariable(
                    "PROGPU_AVALONIA_DIRECT_PRESENTATION"),
                "0",
                StringComparison.Ordinal);
        private readonly IDisposable?[]? _disposables;
        private readonly ILockedFramebuffer? _framebuffer;
        private readonly bool _preserveRecordedCommandsOnDispose;
        private readonly bool _disableSubpixelTextRendering;
        private readonly OffscreenTextureCache _offscreenCache;
        private readonly WgpuContext _gpuContext;
        private readonly GpuTexture? _gpuRenderTarget;
        private readonly object? _gpuRenderSynchronizationLock;
        private readonly Action? _gpuRenderStarting;
        private readonly Action<bool>? _gpuRenderCompleted;
        private readonly string _presentationPath;
        private readonly Matrix? _postTransform;
        internal readonly PixelSize _size;
        private Matrix _currentTransform = Matrix.Identity;
        private double _currentOpacity = 1.0;
        private Vector4 _clearColor = new Vector4(1f, 1f, 1f, 1f);
        private DrawingContextState? _state;
        private int _opacityMaskDepth;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        private bool _recordingRetainedCompositionCommands;
#endif
        private bool _leased;
        private bool _disposed;
        private bool _recordingContextReturned;

        private enum ClipKind
        {
            Rectangle,
            Geometry
        }

        private sealed class DrawingContextState
        {
            internal const int MaximumRetainedDepth = 64;

            internal readonly Stack<double> OpacityStack = new();
            internal readonly Stack<ClipKind> ClipStack = new();
            internal readonly Stack<Avalonia.Media.RenderOptions> RenderOptionsStack = new();
#if !AVALONIA11
            internal readonly Stack<Avalonia.Media.TextOptions> TextOptionsStack = new();
#endif
            internal DrawingContextState? Next;

            internal void Clear()
            {
                OpacityStack.Clear();
                ClipStack.Clear();
                RenderOptionsStack.Clear();
#if !AVALONIA11
                TextOptionsStack.Clear();
#endif
            }

            internal bool CanRetain()
            {
                return OpacityStack.EnsureCapacity(0) <= MaximumRetainedDepth &&
                       ClipStack.EnsureCapacity(0) <= MaximumRetainedDepth &&
                       RenderOptionsStack.EnsureCapacity(0) <= MaximumRetainedDepth
#if !AVALONIA11
                       && TextOptionsStack.EnsureCapacity(0) <= MaximumRetainedDepth
#endif
                    ;
            }
        }

        [ThreadStatic]
        private static DrawingContextState? s_drawingContextStatePool;

        [ThreadStatic]
        private static int s_drawingContextStatePoolCount;

        private const int MaximumPooledDrawingContextStates = 4;

        public Avalonia.Media.RenderOptions RenderOptions { get; private set; }
#if !AVALONIA11
        public Avalonia.Media.TextOptions TextOptions { get; private set; }
#endif

        public ProGPU.Scene.DrawingContext DrawingContext { get; private set; }
        public Vector Dpi { get; }

        public struct CreateInfo
        {
            public PixelSize? Size;
            public Vector Dpi;
            public bool ScaleDrawingToDpi;
            public bool DisableSubpixelTextRendering;
            public bool PreserveRecordedCommandsOnDispose;
            public object? GrContext;
            public object? Surface;
            public object? Gpu;
            public object? CurrentSession;
            public object? CacheHolder;
            public GpuTexture? GpuRenderTarget;
            public object? GpuRenderSynchronizationLock;
            public Action? GpuRenderStarting;
            public Action<bool>? GpuRenderCompleted;
            public string? PresentationPath;
        }

        private sealed class ProGpuLeaseFeature : IProGpuApiLeaseFeature
        {
            private readonly DrawingContextImpl _context;

            public ProGpuLeaseFeature(DrawingContextImpl context)
            {
                _context = context;
            }

            public IProGpuApiLease Lease()
            {
                _context.CheckLease();
                return new ApiLease(_context);
            }

            private sealed class ApiLease : IProGpuApiLease
            {
                private DrawingContextImpl? _context;
                private readonly WgpuContext _gpuContext;
                private readonly int _threadId;
                private WgpuContext.CurrentContextScope _currentContextScope;
                private bool _lockTaken;

                public ApiLease(DrawingContextImpl context)
                {
                    _gpuContext = context._gpuContext;
                    _threadId = Environment.CurrentManagedThreadId;
                    if (_gpuContext.IsDisposed)
                        throw new ObjectDisposedException(nameof(WgpuContext));

                    var lockTaken = false;
                    try
                    {
                        Monitor.Enter(_gpuContext.RenderLock, ref lockTaken);
                        if (_gpuContext.IsDisposed)
                            throw new ObjectDisposedException(nameof(WgpuContext));

                        _currentContextScope = WgpuContext.PushCurrent(_gpuContext);
                        _lockTaken = lockTaken;
                        _context = context;
                        context._leased = true;
                    }
                    catch
                    {
                        if (lockTaken)
                            Monitor.Exit(_gpuContext.RenderLock);
                        throw;
                    }
                }

                private DrawingContextImpl Context =>
                    _context ?? throw new ObjectDisposedException(nameof(IProGpuApiLease));

                public ProGPU.Scene.DrawingContext DrawingContext => Context.DrawingContext;
                public WgpuContext WgpuContext
                {
                    get
                    {
                        _ = Context;
                        return _gpuContext;
                    }
                }

                public Matrix4x4 CurrentTransform => ToMatrix4x4(Context.RenderTransform);
                public double CurrentOpacity => Context._currentOpacity;
                public PixelSize PixelSize => Context._size;
                public Vector Dpi => Context.Dpi;

                public void Dispose()
                {
                    var context = _context;
                    if (context == null)
                        return;
                    if (Environment.CurrentManagedThreadId != _threadId)
                    {
                        throw new InvalidOperationException(
                            "The ProGPU API lease must be disposed on the thread that acquired it.");
                    }

                    _context = null;
                    try
                    {
                        _currentContextScope.Dispose();
                    }
                    finally
                    {
                        context._leased = false;
                        if (_lockTaken)
                        {
                            _lockTaken = false;
                            Monitor.Exit(_gpuContext.RenderLock);
                        }
                    }
                }
            }
        }

        public DrawingContextImpl(CreateInfo createInfo, params IDisposable?[]? disposables)
        {
            Dpi = createInfo.Dpi;
            _disposables = disposables;
            _preserveRecordedCommandsOnDispose = createInfo.PreserveRecordedCommandsOnDispose;
            _disableSubpixelTextRendering = createInfo.DisableSubpixelTextRendering;
            _offscreenCache = (createInfo.CacheHolder as OffscreenTextureCache) ?? GetFallbackCache();
            _gpuRenderTarget = createInfo.GpuRenderTarget;
            _gpuRenderSynchronizationLock =
                createInfo.GpuRenderSynchronizationLock;
            _gpuRenderStarting = createInfo.GpuRenderStarting;
            _gpuRenderCompleted = createInfo.GpuRenderCompleted;
            DrawingContext = _preserveRecordedCommandsOnDispose
                ? new ProGPU.Scene.DrawingContext()
                : _offscreenCache.RentRecordingContext();
            if (createInfo.ScaleDrawingToDpi &&
                TryGetDpiScale(createInfo.Dpi, out double scaleX, out double scaleY) &&
                (!NearlyEqual(scaleX, 1.0) || !NearlyEqual(scaleY, 1.0)))
            {
                _postTransform = Matrix.CreateScale(scaleX, scaleY);
            }

            if (disposables != null)
            {
                foreach (var d in disposables)
                {
                    if (d is ILockedFramebuffer fb)
                    {
                        _framebuffer = fb;
                        break;
                    }
                }
            }
            _presentationPath =
                createInfo.PresentationPath ??
                (_framebuffer is IPlatformHandle
                    {
                        HandleDescriptor:
                            ProGpuSurfaceHandleDescriptor
                    }
                    ? "SilkNetWebGpuSurface"
                    : "AvaloniaFramebuffer");

            if (_gpuRenderTarget != null)
            {
                _size = new PixelSize(
                    checked((int)_gpuRenderTarget.Width),
                    checked((int)_gpuRenderTarget.Height));
            }
            else if (createInfo.Size.HasValue)
            {
                _size = createInfo.Size.Value;
            }
            else if (_framebuffer != null)
            {
                _size = _framebuffer.Size;
            }
            else
            {
                _size = default;
            }

            var preferredFormat = _gpuRenderTarget?.Format ?? TextureFormat.Bgra8Unorm;
            if (_gpuRenderTarget == null && _framebuffer != null)
            {
                if (_framebuffer.Format == PixelFormats.Rgba8888)
                {
                    preferredFormat = TextureFormat.Rgba8Unorm;
                }
            }
            else if (_gpuRenderTarget == null)
            {
                var currentContext = WgpuContext.Current;
                if (currentContext != null)
                {
                    preferredFormat = currentContext.SwapChainFormat;
                }
            }
            if (_gpuRenderTarget != null)
            {
                _gpuContext = _gpuRenderTarget.Context;
                if (_gpuContext.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(WgpuContext));
                }

                lock (s_initLock)
                {
                    s_wgpuContext = _gpuContext;
                    WgpuContext.Current = _gpuContext;
                }
            }
            else
            {
                EnsureGpuContext(_framebuffer, preferredFormat);
                _gpuContext = s_wgpuContext ??
                    throw new InvalidOperationException("ProGPU did not initialize a WebGPU context.");
            }
            _state = RentDrawingContextState();
        }

        private void CheckLease()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leased)
                throw new InvalidOperationException("The underlying ProGPU API is currently leased.");
        }

        private DrawingContextState State =>
            _state ?? throw new ObjectDisposedException(nameof(DrawingContextImpl));

        private static DrawingContextState RentDrawingContextState()
        {
            var state = s_drawingContextStatePool;
            if (state == null)
            {
                return new DrawingContextState();
            }

            s_drawingContextStatePool = state.Next;
            state.Next = null;
            s_drawingContextStatePoolCount--;
            return state;
        }

        private void ReturnDrawingContextState()
        {
            var state = _state;
            if (state == null)
            {
                return;
            }

            _state = null;
            state.Clear();
            if (!state.CanRetain() ||
                s_drawingContextStatePoolCount >= MaximumPooledDrawingContextStates)
            {
                return;
            }

            state.Next = s_drawingContextStatePool;
            s_drawingContextStatePool = state;
            s_drawingContextStatePoolCount++;
        }

        private Matrix RenderTransform => _postTransform.HasValue
            ? _currentTransform * _postTransform.Value
            : _currentTransform;

        public void Reset()
        {
            if (_disposed && _preserveRecordedCommandsOnDispose)
            {
                _state = RentDrawingContextState();
                _disposed = false;
            }
            CheckLease();
            DiscardUnbalancedEffectScopes();
            _currentTransform = Matrix.Identity;
            _currentOpacity = 1.0;
            State.Clear();
            _opacityMaskDepth = 0;
            DrawingContext.Clear();
        }

        public void Clear(Avalonia.Media.Color color)
        {
            CheckLease();
            _clearColor = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            var pBrush = new ProGPU.Vector.SolidColorBrush(_clearColor);
            DrawingContext.PushBlendMode(GpuBlendMode.Src);
            DrawingContext.DrawRectangle(pBrush, null, new ProGPU.Scene.Rect(0, 0, _size.Width, _size.Height));
            DrawingContext.PopBlendMode();
        }

        public void DrawBitmap(IBitmapImpl source, double opacity, Avalonia.Rect sourceRect, Avalonia.Rect destRect)
        {
            CheckLease();
            if (source is IDrawingContextLayerImpl layer && layer.CanBlit)
            {
                layer.Blit(this);
                return;
            }

            if (source is IDrawableBitmapImpl drawable)
            {
                var texture = ResolveBitmapTexture(drawable);
                if (texture != null)
                {
                    if (!NearlyEqual(opacity, 1.0))
                    {
                        DrawingContext.PushOpacity((float)opacity);
                    }

                    DrawingContext.DrawTexture(
                        texture,
                        ToLocalProGpuRect(destRect),
                        ToLocalProGpuRect(sourceRect),
                        ToMatrix4x4(RenderTransform));

                    if (!NearlyEqual(opacity, 1.0))
                    {
                        DrawingContext.PopOpacity();
                    }
                }
            }
        }

        private GpuTexture? ResolveBitmapTexture(
            IDrawableBitmapImpl drawable)
        {
            if (drawable is IContextPortableDrawableBitmapImpl portable)
            {
                return portable.GetTexture(_gpuContext);
            }

            if (drawable.Texture == null)
            {
                drawable.UploadToGpu();
            }

            var texture = drawable.Texture;
            if (texture != null &&
                !texture.Context.SharesDeviceWith(_gpuContext))
            {
                throw new InvalidOperationException(
                    "The bitmap texture belongs to a different WebGPU render context/device domain.");
            }

            return texture;
        }

        public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Avalonia.Rect opacityMaskRect, Avalonia.Rect destRect)
        {
            CheckLease();
            DrawBitmap(source, 1.0, new Avalonia.Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height), destRect);
        }

        public void DrawLine(IPen? pen, Avalonia.Point p1, Avalonia.Point p2)
        {
            CheckLease();
            var pPen = ConvertPen(pen);
            if (pPen != null)
            {
                DrawingContext.DrawLine(pPen, TransformPoint(p1), TransformPoint(p2));
            }
        }

        public void DrawGeometry(IBrush? brush, IPen? pen, IGeometryImpl geometry)
        {
            CheckLease();
            if (geometry is GeometryImpl geomImpl)
            {
                var bounds = geomImpl.Bounds;
                var pPen = ConvertPen(pen, bounds);
                if (TryDrawSceneBrush(brush, bounds, geomImpl.Path) ||
                    TryDrawImageBrush(brush, bounds, geomImpl.Path))
                {
                    if (pPen != null)
                    {
                        DrawingContext.DrawPath(
                            null,
                            pPen,
                            geomImpl.Path,
                            ToMatrix4x4(RenderTransform),
                            geomImpl.GetRenderCommandGeometryCache());
                    }
                    return;
                }

                var pBrush = ConvertBrush(brush, bounds);
                if (pBrush == null && pPen == null)
                {
                    return;
                }

                DrawingContext.DrawPath(
                    pBrush,
                    pPen,
                    geomImpl.Path,
                    ToMatrix4x4(RenderTransform),
                    geomImpl.GetRenderCommandGeometryCache());
            }
        }

        public void DrawRectangle(IExperimentalAcrylicMaterial? material, RoundedRect rect)
        {
            CheckLease();
            if (material == null || rect.Rect.Width <= 0 || rect.Rect.Height <= 0)
            {
                return;
            }

            var tintColor = material.TintColor;
            var luminosityColor = material.MaterialColor;
            var fallbackColor = material.FallbackColor;
            var parameters = new BackdropMaterialParams
            {
                Rect = ToLocalProGpuRect(rect.Rect),
                CornerRadiiX = new Vector4(
                    (float)rect.RadiiTopLeft.X,
                    (float)rect.RadiiTopRight.X,
                    (float)rect.RadiiBottomRight.X,
                    (float)rect.RadiiBottomLeft.X),
                CornerRadiiY = new Vector4(
                    (float)rect.RadiiTopLeft.Y,
                    (float)rect.RadiiTopRight.Y,
                    (float)rect.RadiiBottomRight.Y,
                    (float)rect.RadiiBottomLeft.Y),
                Kind = BackdropMaterialKind.Acrylic,
                Source = material.BackgroundSource == AcrylicBackgroundSource.Digger
                    ? BackdropMaterialSource.HostBackdrop
                    : BackdropMaterialSource.None,
                TintColor = new Vector4(
                    tintColor.R / 255f,
                    tintColor.G / 255f,
                    tintColor.B / 255f,
                    tintColor.A / 255f),
                LuminosityColor = new Vector4(
                    luminosityColor.R / 255f,
                    luminosityColor.G / 255f,
                    luminosityColor.B / 255f,
                    luminosityColor.A / 255f),
                FallbackColor = new Vector4(
                    fallbackColor.R / 255f,
                    fallbackColor.G / 255f,
                    fallbackColor.B / 255f,
                    fallbackColor.A / 255f),
                TintOpacity = 1f,
                LuminosityOpacity = 1f,
                MaterialOpacity = 1f,
                NoiseOpacity = 0.0225f,
                BlurRadius = 30f,
                Saturation = 1.25f
            };

            var replaceBackdrop = material.BackgroundSource == AcrylicBackgroundSource.Digger;
            if (replaceBackdrop)
            {
                DrawingContext.PushBlendMode(GpuBlendMode.Src);
            }

            DrawingContext.DrawBackdropMaterial(parameters, ToMatrix4x4(RenderTransform));

            if (replaceBackdrop)
            {
                DrawingContext.PopBlendMode();
            }
        }

        public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect, BoxShadows boxShadows = default)
        {
            CheckLease();
            var pPen = ConvertPen(pen, rect.Rect);
            var localRect = ToLocalProGpuRect(rect.Rect);
            if (RequiresBrushClipPath(brush))
            {
                var clipPath = rect.IsRounded
                    ? PrimitivePathGeometry.CreateRoundedRectangle(
                        localRect.X,
                        localRect.Y,
                        localRect.Width,
                        localRect.Height,
                        (float)rect.RadiiTopLeft.X,
                        (float)rect.RadiiTopLeft.Y)
                    : PrimitivePathGeometry.CreateRectangle(
                        localRect.X,
                        localRect.Y,
                        localRect.Width,
                        localRect.Height);
                if (TryDrawSceneBrush(brush, rect.Rect, clipPath, useGeometryClip: false) ||
                    TryDrawImageBrush(brush, rect.Rect, clipPath))
                {
                    if (pPen != null)
                    {
                        DrawingContext.DrawPath(null, pPen, clipPath, ToMatrix4x4(RenderTransform));
                    }
                    return;
                }
            }

            var pBrush = ConvertBrush(brush, rect.Rect);
            var transform = ToMatrix4x4(RenderTransform);
            if (rect.IsRounded)
            {
                DrawingContext.DrawRoundedRectangle(
                    pBrush,
                    pPen,
                    localRect,
                    (float)rect.RadiiTopLeft.X,
                    (float)rect.RadiiTopLeft.Y,
                    transform);
            }
            else
            {
                DrawingContext.DrawRectangle(pBrush, pPen, localRect, transform);
            }
        }

        public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region)
        {
            CheckLease();
            if (region.IsEmpty)
                return;

            var pBrush = ConvertBrush(brush);
            var pPen = ConvertPen(pen);
            var rects = region.Rects;
            if (rects.Count == 1)
            {
                DrawingContext.DrawRectangle(pBrush, pPen, ToProGpuRect(rects[0]));
            }
            else
            {
                DrawingContext.DrawPath(pBrush, pPen, CreateRegionGeometry(rects), Matrix4x4.Identity);
            }
        }

        public void DrawEllipse(IBrush? brush, IPen? pen, Avalonia.Rect rect)
        {
            CheckLease();
            var center = new Vector2((float)rect.Center.X, (float)rect.Center.Y);
            var radiusX = (float)(rect.Width / 2.0);
            var radiusY = (float)(rect.Height / 2.0);
            var pPen = ConvertPen(pen, rect);
            if (RequiresBrushClipPath(brush))
            {
                var clipPath = PrimitivePathGeometry.CreateEllipse(center, radiusX, radiusY);
                if (TryDrawSceneBrush(brush, rect, clipPath) ||
                    TryDrawImageBrush(brush, rect, clipPath))
                {
                    if (pPen != null)
                    {
                        DrawingContext.DrawPath(null, pPen, clipPath, ToMatrix4x4(RenderTransform));
                    }
                    return;
                }
            }

            var pBrush = ConvertBrush(brush, rect);
            DrawingContext.DrawEllipse(
                pBrush,
                pPen,
                center,
                radiusX,
                radiusY,
                ToMatrix4x4(RenderTransform));
        }

        public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun)
        {
            CheckLease();
            if (glyphRun is GlyphRunImpl run)
            {
                var pBrush = ConvertBrush(foreground, run.Bounds);
                if (pBrush == null) return;

                var simulations = run.Typeface.FontSimulations;
#if !AVALONIA11
                var effectiveTextOptions = GetEffectiveTextOptions();
#endif
                if (foreground is ISolidColorBrush &&
                    pBrush is ProGPU.Vector.SolidColorBrush &&
                    !run.Typeface.Font.HasColorGlyphs)
                {
                    DrawingContext.DrawGlyphRun(
                        run.GlyphIndices,
                        run.ProGpuGlyphPositions,
                        run.Typeface.Font,
                        (float)run.FontRenderingEmSize,
                        pBrush,
                        new Vector2((float)run.BaselineOrigin.X, (float)run.BaselineOrigin.Y),
                        ToMatrix4x4(RenderTransform),
                        isBold: (simulations & FontSimulations.Bold) != 0,
                        isItalic: (simulations & FontSimulations.Oblique) != 0,
#if AVALONIA11
                        textRenderingMode: ToProGpuTextRenderingMode(RenderOptions.TextRenderingMode),
                        textHintingMode: ProGPU.Scene.TextHintingMode.Auto,
#else
                        textRenderingMode: ToProGpuTextRenderingMode(effectiveTextOptions.TextRenderingMode),
                        textHintingMode: ToProGpuTextHintingMode(effectiveTextOptions.TextHintingMode),
#endif
                        preferGlyphAtlas: run.Typeface.Font.HasBitmapGlyphs);
                    return;
                }

                var scale = (float)(run.FontRenderingEmSize / run.Typeface.Font.UnitsPerEm);
                var renderTransform = ToMatrix4x4(RenderTransform);
                var colorGlyphOpacity = foreground?.Opacity ?? 1.0;
                if (foreground is ISolidColorBrush solidColorBrush)
                {
                    colorGlyphOpacity *= solidColorBrush.Color.A / 255.0;
                }

                for (var i = 0; i < run.GlyphIndices.Length; i++)
                {
                    var glyphIndex = run.GlyphIndices[i];
                    var position = run.GlyphPositions[i];
                    var origin = run.BaselineOrigin + new Vector(position.X, position.Y);

                    if (run.Typeface.Font.HasBitmapGlyphs &&
                        run.Typeface.Font.TryGetBitmapGlyph(
                            glyphIndex,
                            (float)run.FontRenderingEmSize,
                            out _))
                    {
                        if (!NearlyEqual(colorGlyphOpacity, 1.0))
                        {
                            DrawingContext.PushOpacity((float)colorGlyphOpacity);
                        }

                        DrawingContext.DrawGlyphRunRange(
                            run.GlyphIndices,
                            run.ProGpuGlyphPositions,
                            i,
                            1,
                            run.Typeface.Font,
                            (float)run.FontRenderingEmSize,
                            _offscreenCache.GetSolidBrush(
                                byte.MaxValue,
                                byte.MaxValue,
                                byte.MaxValue,
                                byte.MaxValue,
                                1f),
                            new Vector2(
                                (float)run.BaselineOrigin.X,
                                (float)run.BaselineOrigin.Y),
                            renderTransform,
                            isBold:
                                (simulations & FontSimulations.Bold) != 0,
                            isItalic:
                                (simulations & FontSimulations.Oblique) != 0,
#if AVALONIA11
                            textRenderingMode:
                                ToProGpuTextRenderingMode(
                                    RenderOptions.TextRenderingMode),
                            textHintingMode:
                                ProGPU.Scene.TextHintingMode.Auto,
#else
                            textRenderingMode:
                                ToProGpuTextRenderingMode(
                                    effectiveTextOptions.TextRenderingMode),
                            textHintingMode:
                                ToProGpuTextHintingMode(
                                    effectiveTextOptions.TextHintingMode),
#endif
                            preferGlyphAtlas: true);

                        if (!NearlyEqual(colorGlyphOpacity, 1.0))
                        {
                            DrawingContext.PopOpacity();
                        }
                        continue;
                    }

                    var glyphTransform = Matrix4x4.CreateScale(scale, scale, 1f) *
                                         Matrix4x4.CreateTranslation((float)origin.X, (float)origin.Y, 0f) *
                                         renderTransform;
                    var colorLayers = run.Typeface.Font.GetColorLayers(glyphIndex);
                    if (colorLayers is { Count: > 0 })
                    {
                        foreach (var layer in colorLayers)
                        {
                            var layerOutline = run.Typeface.Font.GetFlippedGlyphOutline(layer.GlyphId);
                            if (layerOutline == null)
                            {
                                continue;
                            }

                            var layerColor = layer.Color;
                            layerColor.W *= (float)colorGlyphOpacity;
                            DrawingContext.DrawPath(
                                new ProGPU.Vector.SolidColorBrush(layerColor),
                                null,
                                layerOutline,
                                glyphTransform);
                        }
                        continue;
                    }

                    var outline = run.Typeface.Font.GetFlippedGlyphOutline(glyphIndex);
                    if (outline == null)
                    {
                        continue;
                    }

                    DrawingContext.DrawPath(pBrush, null, outline, glyphTransform);
                }
            }
        }

        public IDrawingContextLayerImpl CreateLayer(PixelSize size)
        {
            CheckLease();
            PixelFormat? format = _framebuffer?.Format;
            if (format == null)
            {
                var currentContext = WgpuContext.Current;
                if (currentContext != null)
                {
                    format = currentContext.SwapChainFormat == TextureFormat.Rgba8Unorm
                        ? PixelFormats.Rgba8888
                        : PixelFormats.Bgra8888;
                }
            }
            var createInfo = new SurfaceRenderTarget.CreateInfo
            {
                Width = size.Width,
                Height = size.Height,
                Dpi = Dpi,
                UseScaledDrawing = true,
                Format = format
            };
            return new SurfaceRenderTarget(createInfo);
        }

        public void PushClip(Avalonia.Rect clip)
        {
            CheckLease();
            DrawingContext.PushClip(ToProGpuRect(clip));
            State.ClipStack.Push(ClipKind.Rectangle);
        }
        public void PushClip(RoundedRect clip)
        {
            CheckLease();
            DrawingContext.PushClip(ToProGpuRect(clip.Rect));
            State.ClipStack.Push(ClipKind.Rectangle);
        }
        public void PushClip(IPlatformRenderInterfaceRegion region)
        {
            CheckLease();
            var rects = region.Rects;
            if (rects.Count <= 1)
            {
                var rect = rects.Count == 0 ? default : rects[0];
                DrawingContext.PushClip(ToProGpuRect(rect));
                State.ClipStack.Push(ClipKind.Rectangle);
            }
            else
            {
                DrawingContext.PushGeometryClip(CreateRegionGeometry(rects));
                State.ClipStack.Push(ClipKind.Geometry);
            }
        }
        public void PopClip()
        {
            CheckLease();
            var clipStack = State.ClipStack;
            if (clipStack.Count == 0 || clipStack.Pop() == ClipKind.Rectangle)
                DrawingContext.PopClip();
            else
                DrawingContext.PopGeometryClip();
        }

        public void PushLayer(Avalonia.Rect bounds)
        {
            CheckLease();
            DrawingContext.PushClip(ToProGpuRect(bounds));
        }
        public void PopLayer()
        {
            CheckLease();
            DrawingContext.PopClip();
        }

        public void PushOpacity(double opacity, Avalonia.Rect? bounds)
        {
            CheckLease();
            State.OpacityStack.Push(_currentOpacity);
            _currentOpacity *= opacity;
            DrawingContext.PushOpacity((float)opacity);
        }

        public void PopOpacity()
        {
            CheckLease();
            var opacityStack = State.OpacityStack;
            if (opacityStack.Count > 0)
            {
                _currentOpacity = opacityStack.Pop();
                DrawingContext.PopOpacity();
            }
        }

        public void PushGeometryClip(IGeometryImpl clip)
        {
            CheckLease();
            if (clip is GeometryImpl geomImpl)
            {
                var transform = RenderTransform;
                var path = transform == Matrix.Identity
                    ? geomImpl.Path
                    : geomImpl.Path.CreateTransformed(ToMatrix4x4(transform));
                DrawingContext.PushGeometryClip(path);
            }
        }
        public void PopGeometryClip()
        {
            CheckLease();
            DrawingContext.PopGeometryClip();
        }

        public void PushOpacityMask(IBrush mask, Avalonia.Rect bounds)
        {
            CheckLease();
            var pBrush = ConvertBrush(mask, bounds);
            if (pBrush != null)
            {
                DrawingContext.PushOpacityMask(pBrush, ToProGpuRect(bounds));
            }
            else
            {
                var ownerContext = DrawingContext;
                var picture = RecordOpacityMask(mask, bounds);
                ownerContext.RetainResource(picture);
                ownerContext.PushOpacityMask(picture, ToProGpuRect(bounds));
            }

            _opacityMaskDepth++;
        }

        public void PopOpacityMask()
        {
            CheckLease();
            if (_opacityMaskDepth > 0)
            {
                _opacityMaskDepth--;
                DrawingContext.PopOpacityMask();
            }
        }

        private GpuPicture RecordOpacityMask(IBrush mask, Avalonia.Rect bounds)
        {
            var recorder = new GpuPictureRecorder();
            var recordingContext = recorder.BeginRecording(ToLocalProGpuRect(bounds));
            var ownerContext = DrawingContext;
            GpuPicture? picture = null;

            DrawingContext = recordingContext;
            try
            {
                DrawRectangle(mask, null, new RoundedRect(bounds));
                picture = recorder.EndRecording();
                return picture;
            }
            finally
            {
                DrawingContext = ownerContext;
                if (picture == null)
                {
                    recordingContext.Clear();
                }
            }
        }

        public void PushRenderOptions(Avalonia.Media.RenderOptions renderOptions)
        {
            CheckLease();
            State.RenderOptionsStack.Push(RenderOptions);
            RenderOptions = RenderOptions.MergeWith(renderOptions);
        }

        public void PopRenderOptions()
        {
            CheckLease();
            RenderOptions = State.RenderOptionsStack.Pop();
        }

#if !AVALONIA11
        public void PushTextOptions(Avalonia.Media.TextOptions textOptions)
        {
            CheckLease();
            State.TextOptionsStack.Push(TextOptions);
            TextOptions = TextOptions.MergeWith(textOptions);
        }

        public void PopTextOptions()
        {
            CheckLease();
            TextOptions = State.TextOptionsStack.Pop();
        }
#endif

        public Matrix Transform
        {
            get => _currentTransform;
            set
            {
                CheckLease();
                _currentTransform = value;
            }
        }

        public object? GetFeature(Type featureType)
        {
            if (featureType == typeof(IProGpuApiLeaseFeature))
                return new ProGpuLeaseFeature(this);
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
            if (featureType == typeof(ICompositionRenderDataDrawingContextFeature))
                return this;
            if (s_useRetainedAvaloniaScene &&
                featureType == typeof(ICompositionVisualTreeDrawingContextFeature))
                return this;
#endif
            return null;
        }

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        bool ICompositionVisualTreeDrawingContextFeature.TryRender(
            ServerCompositionTarget target,
            ServerCompositionVisual root,
            LtrbRect clip,
            out int visitedVisuals,
            out int renderedVisuals)
        {
            CheckLease();
            AvaloniaCompositionScene scene =
                _offscreenCache.GetOrCreateCompositionScene(target.Id);
            if (!scene.TrySynchronize(
                    target,
                    root,
                    clip,
                    this,
                    out visitedVisuals,
                    out renderedVisuals) ||
                scene.Root == null)
            {
                _offscreenCache.RemoveCompositionScene(target.Id);
                return false;
            }

            DrawingContext.DrawVisual(
                scene.Root,
                ToMatrix4x4(RenderTransform));
            return true;
        }

        internal void RecordRetainedCompositionVisual(
            ServerCompositionVisual source,
            LtrbRect clip,
            Avalonia.Media.RenderOptions renderOptions,
            Avalonia.Media.TextOptions textOptions,
            ProGPU.Scene.DrawingContext destination)
        {
            var ownerContext = DrawingContext;
            var ownerTransform = _currentTransform;
            var ownerOpacity = _currentOpacity;
            var ownerRenderOptions = RenderOptions;
            var ownerTextOptions = TextOptions;
            int ownerOpacityMaskDepth = _opacityMaskDepth;
            bool ownerRecordingRetainedCompositionCommands =
                _recordingRetainedCompositionCommands;

            destination.Clear();
            DrawingContext = destination;
            _currentTransform = _postTransform?.Invert() ?? Matrix.Identity;
            _currentOpacity = 1;
            RenderOptions = renderOptions;
            TextOptions = textOptions;
            _opacityMaskDepth = 0;
            _recordingRetainedCompositionCommands = true;
            try
            {
                source.RenderRetainedContent(this, clip);
                destination.TrimRetainedCommandCapacity();
            }
            finally
            {
                _recordingRetainedCompositionCommands =
                    ownerRecordingRetainedCompositionCommands;
                _opacityMaskDepth = ownerOpacityMaskDepth;
                TextOptions = ownerTextOptions;
                RenderOptions = ownerRenderOptions;
                _currentOpacity = ownerOpacity;
                _currentTransform = ownerTransform;
                DrawingContext = ownerContext;
            }
        }

        internal (int visited, int rendered) RecordRetainedCompositionSubtree(
            ServerCompositionVisual source,
            ProGPU.Scene.DrawingContext destination)
        {
            var ownerContext = DrawingContext;
            var ownerTransform = _currentTransform;
            var ownerOpacity = _currentOpacity;
            var ownerRenderOptions = RenderOptions;
            var ownerTextOptions = TextOptions;
            int ownerOpacityMaskDepth = _opacityMaskDepth;
            bool ownerRecordingRetainedCompositionCommands =
                _recordingRetainedCompositionCommands;

            destination.Clear();
            DrawingContext = destination;
            _currentTransform = _postTransform?.Invert() ?? Matrix.Identity;
            _currentOpacity = 1;
            RenderOptions = default;
            TextOptions = default;
            _opacityMaskDepth = 0;
            _recordingRetainedCompositionCommands = true;
            try
            {
                var result = source.Render(
                    this,
                    LtrbRect.Infinite,
                    dirtyRects: null,
                    renderChildren: true,
                    skipRootVisualTransform: false,
                    renderingToBitmapCache: false);
                destination.TrimRetainedCommandCapacity();
                return result;
            }
            finally
            {
                _recordingRetainedCompositionCommands =
                    ownerRecordingRetainedCompositionCommands;
                _opacityMaskDepth = ownerOpacityMaskDepth;
                TextOptions = ownerTextOptions;
                RenderOptions = ownerRenderOptions;
                _currentOpacity = ownerOpacity;
                _currentTransform = ownerTransform;
                DrawingContext = ownerContext;
            }
        }

        bool ICompositionRenderDataDrawingContextFeature.TryRender(
            ServerCompositionRenderData renderData)
        {
            CheckLease();

            // These options affect materialized commands and therefore need to
            // become part of the retained key before this fast path can use them.
            if (RenderOptions != default || TextOptions != default)
                return false;

            // The retained Avalonia scene already owns a stable command list for
            // this visual. Expanding the immutable render-data nodes directly
            // into that list avoids a second GpuPicture command array and its
            // per-revision copy. The outer recording scope owns transforms and
            // retained resource leases just as it does for ordinary commands.
            if (_recordingRetainedCompositionCommands)
            {
                renderData.Render(this);
                return true;
            }

            if (!_offscreenCache.TryGetCompositionPicture(
                    renderData.RetainedId,
                    renderData.Revision,
                    out GpuPicture? picture))
            {
                var recorder = new GpuPictureRecorder();
                var recordingContext = recorder.BeginRecording(
                    renderData.Bounds?.ToRect() is { } bounds
                        ? ToLocalProGpuRect(bounds)
                        : default);
                var ownerContext = DrawingContext;
                var ownerTransform = _currentTransform;
                var ownerOpacity = _currentOpacity;
                GpuPicture? recorded = null;

                DrawingContext = recordingContext;
                _currentTransform = _postTransform?.Invert() ?? Matrix.Identity;
                _currentOpacity = 1;
                try
                {
                    renderData.Render(this);
                    recorded = recorder.EndRecording();
                    _offscreenCache.StoreCompositionPicture(
                        renderData.RetainedId,
                        renderData.Revision,
                        recorded);
                    picture = recorded;
                }
                finally
                {
                    _currentTransform = ownerTransform;
                    _currentOpacity = ownerOpacity;
                    DrawingContext = ownerContext;
                    if (recorded == null)
                        recordingContext.Clear();
                }
            }

            DrawingContext.DrawPictureTransformed(
                picture!,
                ToMatrix4x4(RenderTransform));
            return true;
        }
#endif

        [ThreadStatic]
        private static WgpuContext? s_wgpuContext;
        private static readonly object s_initLock = new();
        private static readonly Dictionary<WgpuContext, Dictionary<TextureFormat, Compositor>> s_compositors = new();
        internal static CompositorOptions BackendCompositorOptions { get; } =
            CompositorOptions.Default with
            {
                InitialVertexCount = 1024,
                InitialIndexCount = 1536,
                InitialColorGlyphAtlasSize = 64,
                GlyphUniformStagingBytes = 16 * 1024,
                GlyphCoverageStagingBytes = GlyphAtlas.DefaultCoverageRingBufferSize,
                EnableGpuHitTesting = false,
                PrimarySampleCount = 1,
                EnableIncrementalScenePages = !string.Equals(
                    Environment.GetEnvironmentVariable(
                        "PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES"),
                    "0",
                    StringComparison.Ordinal)
            };

        private static Compositor GetCompositor(WgpuContext context, TextureFormat format)
        {
            lock (s_initLock)
            {
                if (!s_compositors.TryGetValue(context, out var dict))
                {
                    dict = new Dictionary<TextureFormat, Compositor>();
                    s_compositors[context] = dict;
                }

                if (!dict.TryGetValue(format, out var compositor))
                {
                    compositor = new Compositor(context, format, BackendCompositorOptions);
                    dict[format] = compositor;
                }

                return compositor;
            }
        }

        [ThreadStatic]
        private static OffscreenTextureCache? s_fallbackCache;

        private static OffscreenTextureCache GetFallbackCache()
        {
            return s_fallbackCache ??= new OffscreenTextureCache();
        }

        static DrawingContextImpl()
        {
            WgpuContext.Disposing += InvalidateForContext;
        }

        private static unsafe void InvalidateCachedResources()
        {
            s_fallbackCache?.Invalidate(s_wgpuContext);
        }

        public static unsafe void InvalidateForContext(WgpuContext context)
        {
            lock (context.RenderLock)
            {
                Dictionary<TextureFormat, Compositor>? dictToDispose = null;

                lock (s_initLock)
                {
                    if (s_compositors.TryGetValue(context, out var dict))
                    {
                        dictToDispose = dict;
                        s_compositors.Remove(context);
                    }

                    if (s_wgpuContext == context)
                    {
                        s_wgpuContext = null;
                    }
                }

                if (dictToDispose != null)
                {
                    foreach (var compositor in dictToDispose.Values)
                    {
                        try { compositor.Dispose(); } catch {}
                    }
                }

                s_fallbackCache?.Invalidate(context);
            }
        }

        private static unsafe WgpuContext? ResolveContext(ILockedFramebuffer? framebuffer)
        {
            if (TryGetSurfacePointer(framebuffer, out var surfacePtr))
            {
                var current = WgpuContext.Current;
                if (current is { IsDisposed: false } &&
                    (IntPtr)current.Surface == surfacePtr)
                {
                    return current;
                }

                return WgpuContext.TryGetActiveContextForSurface(surfacePtr, out var context)
                    ? context
                    : null;
            }
            return null;
        }

        private static bool TryGetSurfacePointer(
            ILockedFramebuffer? framebuffer,
            out IntPtr surfacePointer)
        {
            if (framebuffer is IPlatformHandle
                {
                    HandleDescriptor: ProGpuSurfaceHandleDescriptor,
                    Handle: var handle
                } && handle != IntPtr.Zero)
            {
                surfacePointer = handle;
                return true;
            }

            surfacePointer = IntPtr.Zero;
            return false;
        }

        private static unsafe void EnsureGpuContext(ILockedFramebuffer? framebuffer, TextureFormat? preferredFormat = null)
        {
            lock (s_initLock)
            {
                var current = ResolveContext(framebuffer);
                if (current == null)
                {
                    current = WgpuContext.Current;
                    if (current == null)
                    {
                        WgpuContext.TryGetFirstActiveContext(out current);
                    }
                }

                if (current == null)
                {
                    if (s_wgpuContext == null)
                    {
                        s_wgpuContext = new WgpuContext();
                        s_wgpuContext.Initialize(null);
                    }
                }
                else
                {
                    s_wgpuContext = current;
                }

                WgpuContext.Current = s_wgpuContext;
            }
        }

        internal static WgpuContext GetOrCreateStandaloneGpuContext(
            TextureFormat preferredFormat)
        {
            EnsureGpuContext(null, preferredFormat);
            return s_wgpuContext ??
                throw new InvalidOperationException(
                    "ProGPU did not initialize a WebGPU context.");
        }

        internal static GpuTexture GetOffscreenTexture(
            OffscreenTextureCache cache, WgpuContext context, uint width, uint height, TextureFormat format)
        {
            if (cache.CachedTexture != null &&
                cache.CachedWidth == width &&
                cache.CachedHeight == height &&
                cache.CachedTexture.Format == format &&
                cache.CachedTexture.Context == context)
            {
                return cache.CachedTexture;
            }

            cache.Invalidate(context);

            cache.CachedWidth = width;
            cache.CachedHeight = height;

            cache.CachedTexture = new GpuTexture(
                context,
                width,
                height,
                format,
                Silk.NET.WebGPU.TextureUsage.RenderAttachment | Silk.NET.WebGPU.TextureUsage.CopySrc | Silk.NET.WebGPU.TextureUsage.TextureBinding,
                "Avalonia offscreen target"
            );

            return cache.CachedTexture;
        }

        internal static GpuTextureReadbackBuffer GetOffscreenReadbackBuffer(
            OffscreenTextureCache cache,
            WgpuContext context)
        {
            return cache.CachedReadbackBuffer ??=
                new GpuTextureReadbackBuffer(context);
        }

        private unsafe void FlushToFramebuffer()
        {
            if (_gpuRenderTarget != null)
            {
                FlushToGpuRenderTarget(_gpuRenderTarget);
                return;
            }

            if (_framebuffer == null) return;
            if (DrawingContext.Commands.Count == 0) return;

            uint width = (uint)_framebuffer.Size.Width;
            uint height = (uint)_framebuffer.Size.Height;
            if (width == 0 || height == 0) return;

            var preferredFormat = TextureFormat.Bgra8Unorm;
            if (_framebuffer.Format == PixelFormats.Rgba8888)
            {
                preferredFormat = TextureFormat.Rgba8Unorm;
            }

            EnsureGpuContext(_framebuffer, preferredFormat);
            var context = s_wgpuContext!;
            lock (context.RenderLock)
            {
                if (context.IsDisposed) return;

                var hostFrame = CreateHostFrame(width, height);
                var drawingVisual = _offscreenCache.GetOrUpdateRecordedVisual(
                    DrawingContext,
                    hostFrame.LogicalSize);

                if (s_useDirectPresentationSurface &&
                    TryGetSurfacePointer(
                        _framebuffer,
                        out var directSurfacePointer))
                {
                    context.ReconfigureIfNeeded(width, height);
                    var surfaceTexture = new SurfaceTexture();
                    context.Wgpu.SurfaceGetCurrentTexture(
                        (Surface*)directSurfacePointer,
                        &surfaceTexture);
                    TextureView* targetView = null;
                    try
                    {
                        if (surfaceTexture.Status ==
                            SurfaceGetCurrentTextureStatus.Success)
                        {
                            var viewDescriptor = new TextureViewDescriptor
                            {
                                Format = context.SwapChainFormat,
                                Dimension = TextureViewDimension.Dimension2D,
                                BaseMipLevel = 0,
                                MipLevelCount = 1,
                                BaseArrayLayer = 0,
                                ArrayLayerCount = 1,
                                Aspect = TextureAspect.All
                            };
                            targetView = context.Wgpu.TextureCreateView(
                                surfaceTexture.Texture,
                                &viewDescriptor);
                            if (targetView != null)
                            {
                                var directCompositor = GetCompositor(
                                    context,
                                    context.SwapChainFormat);
                                Vector4 previousClearColor =
                                    directCompositor.ClearColor;
                                try
                                {
                                    directCompositor.ClearColor = _clearColor;
                                    directCompositor.RenderScene(
                                        drawingVisual,
                                        hostFrame,
                                        targetView);
                                }
                                finally
                                {
                                    directCompositor.ClearColor =
                                        previousClearColor;
                                }

                                ReportCompositorFrame(directCompositor);
                                context.Wgpu.SurfacePresent(
                                    (Surface*)directSurfacePointer);
                            }
                        }
                    }
                    finally
                    {
                        if (targetView != null)
                        {
                            context.Wgpu.TextureViewRelease(targetView);
                        }
                        if (surfaceTexture.Texture != null)
                        {
                            context.Wgpu.TextureRelease(
                                surfaceTexture.Texture);
                        }
                    }
                    return;
                }

                var compositor = GetCompositor(context, preferredFormat);
                var texture = GetOffscreenTexture(
                    _offscreenCache,
                    context,
                    width,
                    height,
                    preferredFormat);
                compositor.RenderOffscreen(
                    drawingVisual,
                    hostFrame,
                    texture,
                    0.0f,
                    _clearColor,
                    loadExistingContents: false
                );
                ReportCompositorFrame(compositor);
                _offscreenCache.IsTextureFresh = false;

                if (TryGetSurfacePointer(_framebuffer, out var surfacePointer))
                {
                    context.ReconfigureIfNeeded(width, height);
                    var surfaceTexture = new SurfaceTexture();
                    context.Wgpu.SurfaceGetCurrentTexture((Surface*)surfacePointer, &surfaceTexture);
                    TextureView* targetView = null;
                    try
                    {
                        if (surfaceTexture.Status == SurfaceGetCurrentTextureStatus.Success)
                        {
                            var viewDesc = new TextureViewDescriptor
                            {
                                Format = context.SwapChainFormat,
                                Dimension = TextureViewDimension.Dimension2D,
                                BaseMipLevel = 0,
                                MipLevelCount = 1,
                                BaseArrayLayer = 0,
                                ArrayLayerCount = 1,
                                Aspect = TextureAspect.All
                            };
                            targetView = context.Wgpu.TextureCreateView(surfaceTexture.Texture, &viewDesc);

                            if (targetView != null)
                            {
                                GpuTextureBlitter.Blit(texture, targetView, context.SwapChainFormat);
                                context.Wgpu.SurfacePresent((Surface*)surfacePointer);
                            }
                        }
                    }
                    finally
                    {
                        if (targetView != null)
                        {
                            context.Wgpu.TextureViewRelease(targetView);
                        }
                        if (surfaceTexture.Texture != null)
                        {
                            context.Wgpu.TextureRelease(surfaceTexture.Texture);
                        }
                    }
                    return;
                }

                var readbackBuffer = GetOffscreenReadbackBuffer(
                    _offscreenCache,
                    context);
                readbackBuffer.TryReadTextureRows(texture, width, height, (void*)_framebuffer.Address, (uint)_framebuffer.RowBytes);
                context.CleanupPendingResources();
            }
        }

        private void FlushToGpuRenderTarget(GpuTexture texture)
        {
            if (_gpuRenderSynchronizationLock != null)
            {
                lock (_gpuRenderSynchronizationLock)
                {
                    FlushToGpuRenderTargetCore(texture);
                }
                return;
            }

            FlushToGpuRenderTargetCore(texture);
        }

        private void FlushToGpuRenderTargetCore(GpuTexture texture)
        {
            var context = texture.Context;
            lock (context.RenderLock)
            {
                if (context.IsDisposed || texture.IsDisposed)
                {
                    return;
                }

                // The optional owner lock is already held before the device
                // lock. Publish CPU/version invalidation only after both are
                // held, so neither a CPU boundary nor another same-device GPU
                // consumer can observe the new version with old pixels.
                _gpuRenderStarting?.Invoke();
                var hostFrame = CreateHostFrame(texture.Width, texture.Height);
                var drawingVisual = _offscreenCache.GetOrUpdateRecordedVisual(
                    DrawingContext,
                    hostFrame.LogicalSize);
                var compositor = GetCompositor(context, texture.Format);
                bool renderSucceeded = false;
                try
                {
                    compositor.RenderOffscreen(
                        drawingVisual,
                        hostFrame,
                        texture,
                        0.0f,
                        _clearColor,
                        loadExistingContents: false);
                    texture.NotifyExternalContentChanged();
                    ReportCompositorFrame(compositor);
                    renderSucceeded = true;
                }
                finally
                {
                    _gpuRenderCompleted?.Invoke(renderSucceeded);
                }
            }
        }

        private void ReportCompositorFrame(Compositor compositor)
        {
            var metrics = compositor.Metrics;
            metrics.PresentationPath = _presentationPath;
            metrics.RecordedCommandCount = DrawingContext.Commands.Count;
            metrics.RecordedCommandCapacity = DrawingContext.Commands.Capacity;
            metrics.RetainedCompositionPictureCount =
                _offscreenCache.CompositionPictureCount;
            metrics.RetainedCompositionPictureHits =
                _offscreenCache.CompositionPictureHits;
            metrics.RetainedCompositionPictureMisses =
                _offscreenCache.CompositionPictureMisses;
            metrics.RetainedCompositionPictureCompilations =
                _offscreenCache.CompositionPictureCompilations;
            metrics.BitmapGlyphMetricCacheCount =
                BitmapGlyphCache.CachedMetricCount;
            metrics.BitmapGlyphDecodedPixelBytes =
                BitmapGlyphCache.CachedDecodedPixelBytes;
            metrics.BitmapGlyphMetricEvictions =
                BitmapGlyphCache.MetricEvictionCount;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
            metrics.RetainedCompositionSceneCount =
                _offscreenCache.CompositionSceneCount;
            metrics.RetainedCompositionSceneNodeCount =
                _offscreenCache.CompositionSceneNodeCount;
            metrics.RetainedCompositionFallbackNodeCount =
                _offscreenCache.CompositionFallbackNodeCount;
            metrics.RetainedCompositionCustomVisualNodeCount =
                _offscreenCache.CompositionCustomVisualNodeCount;
            metrics.RetainedCompositionCustomVisualCompilations =
                _offscreenCache.CompositionCustomVisualCompilations;
            metrics.RetainedCompositionSceneFullSynchronizations =
                _offscreenCache.CompositionSceneFullSynchronizations;
            metrics.RetainedCompositionSceneIncrementalSynchronizations =
                _offscreenCache.CompositionSceneIncrementalSynchronizations;
            metrics.RetainedCompositionSceneUnchangedReuses =
                _offscreenCache.CompositionSceneUnchangedReuses;
#endif
            ProGpuRenderingDiagnostics.ReportFrame(metrics);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            CheckLease();
            DiscardUnbalancedEffectScopes();
            try
            {
                FlushToFramebuffer();
            }
            finally
            {
                try
                {
                    if (!_preserveRecordedCommandsOnDispose)
                    {
                        ReturnRecordingContext();
                    }

                    if (_disposables != null)
                    {
                        foreach (var disposable in _disposables)
                        {
                            disposable?.Dispose();
                        }
                    }
                }
                finally
                {
                    _disposed = true;
                    ReturnDrawingContextState();
                }
            }
        }

        private void ReturnRecordingContext()
        {
            if (_recordingContextReturned)
            {
                return;
            }

            _recordingContextReturned = true;
            _offscreenCache.ReturnRecordingContext(DrawingContext);
        }

        private Vector2 TransformPoint(Point pt)
            => TransformPoint(pt, RenderTransform);

        private static Vector2 TransformPoint(Point pt, Matrix transform)
        {
            var p = pt * transform;
            return new Vector2((float)p.X, (float)p.Y);
        }

        private static ProGPU.Scene.Rect ToProGpuRect(LtrbPixelRect rect)
        {
            return new ProGPU.Scene.Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        private static ProGPU.Vector.PathGeometry CreateRegionGeometry(IList<LtrbPixelRect> rects)
        {
            var geometry = new ProGPU.Vector.PathGeometry { FillRule = ProGPU.Vector.FillRule.Nonzero };
            foreach (var rect in rects)
            {
                if (ProGpuRectUtilities.IsEmpty(rect))
                    continue;

                var figure = new ProGPU.Vector.PathFigure(new Vector2(rect.Left, rect.Top), isClosed: true);
                figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(rect.Right, rect.Top)));
                figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(rect.Right, rect.Bottom)));
                figure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(rect.Left, rect.Bottom)));
                geometry.Figures.Add(figure);
            }

            return geometry;
        }

        internal ProGPU.Scene.Rect ToProGpuRect(Avalonia.Rect r)
        {
            var transformed = r.TransformToAABB(RenderTransform);
            return ToLocalProGpuRect(transformed);
        }

        private bool TryDrawSceneBrush(
            IBrush? brush,
            Avalonia.Rect targetRect,
            ProGPU.Vector.PathGeometry clipPath,
            bool useGeometryClip = true)
        {
            ISceneBrushContent? content = null;
            var ownsContent = false;
            if (brush is ISceneBrush sceneBrush)
            {
                content = sceneBrush.CreateContent();
                ownsContent = true;
            }
            else if (brush is ISceneBrushContent sceneBrushContent)
            {
                content = sceneBrushContent;
            }
            else
            {
                return false;
            }

            try
            {
                if (content == null || content.Rect.Width <= 0 || content.Rect.Height <= 0 ||
                    targetRect.Width <= 0 || targetRect.Height <= 0)
                {
                    return true;
                }

                var tileBrush = content.Brush;
                var calculator = new ProGpuTileBrushCalculator(tileBrush, content.Rect.Size, targetRect.Size);
                var targetOffset = tileBrush.DestinationRect.Unit == RelativeUnit.Relative
                    ? new Vector(targetRect.X, targetRect.Y)
                    : default;

                if (useGeometryClip)
                {
                    DrawingContext.PushGeometryClip(clipPath, ToMatrix4x4(RenderTransform));
                }
                else
                {
                    PushClip(targetRect);
                }
                if (!NearlyEqual(brush.Opacity, 1.0))
                {
                    DrawingContext.PushOpacity((float)brush.Opacity);
                }

                if (tileBrush.TileMode == TileMode.None)
                {
                    var viewport = calculator.IntermediateClip.Translate(targetOffset);
                    PushClip(viewport);
                    content.Render(
                        this,
                        calculator.IntermediateTransform * Matrix.CreateTranslation(targetOffset));
                    PopClip();
                }
                else
                {
                    DrawSceneBrushTiles(content, calculator, targetRect, targetOffset);
                }

                if (!NearlyEqual(brush.Opacity, 1.0))
                {
                    DrawingContext.PopOpacity();
                }
                if (useGeometryClip)
                {
                    DrawingContext.PopGeometryClip();
                }
                else
                {
                    PopClip();
                }
                return true;
            }
            finally
            {
                if (ownsContent)
                {
                    content?.Dispose();
                }
            }
        }

        private void DrawSceneBrushTiles(
            ISceneBrushContent content,
            ProGpuTileBrushCalculator calculator,
            Avalonia.Rect targetRect,
            Vector targetOffset)
        {
            var tileSize = calculator.DestinationRect.Size;
            if (tileSize.Width <= 0 || tileSize.Height <= 0)
            {
                return;
            }

            var anchor = new Point(
                calculator.DestinationRect.X + targetOffset.X,
                calculator.DestinationRect.Y + targetOffset.Y);
            var firstColumn = (int)Math.Floor((targetRect.Left - anchor.X) / tileSize.Width);
            var lastColumn = (int)Math.Ceiling((targetRect.Right - anchor.X) / tileSize.Width);
            var firstRow = (int)Math.Floor((targetRect.Top - anchor.Y) / tileSize.Height);
            var lastRow = (int)Math.Ceiling((targetRect.Bottom - anchor.Y) / tileSize.Height);

            for (var row = firstRow; row < lastRow; row++)
            {
                for (var column = firstColumn; column < lastColumn; column++)
                {
                    var tilePosition = new Point(
                        anchor.X + column * tileSize.Width,
                        anchor.Y + row * tileSize.Height);
                    var viewport = new Avalonia.Rect(tilePosition, tileSize);
                    var transform = calculator.IntermediateTransform *
                                    Matrix.CreateTranslation((Vector)tilePosition);
                    transform *= CreateTileFlipTransform(content.Brush.TileMode, row, column, viewport);

                    PushClip(viewport);
                    content.Render(this, transform);
                    PopClip();
                }
            }
        }

        private static Matrix CreateTileFlipTransform(
            TileMode tileMode,
            int row,
            int column,
            Avalonia.Rect viewport)
        {
            var flipX = (tileMode == TileMode.FlipX || tileMode == TileMode.FlipXY) && (column & 1) != 0;
            var flipY = (tileMode == TileMode.FlipY || tileMode == TileMode.FlipXY) && (row & 1) != 0;
            if (!flipX && !flipY)
            {
                return Matrix.Identity;
            }

            var center = viewport.Center;
            return Matrix.CreateTranslation(-(Vector)center) *
                   Matrix.CreateScale(flipX ? -1 : 1, flipY ? -1 : 1) *
                   Matrix.CreateTranslation((Vector)center);
        }

        private bool TryDrawImageBrush(
            IBrush? brush,
            Avalonia.Rect targetRect,
            ProGPU.Vector.PathGeometry clipPath)
        {
            if (brush is not IImageBrush imageBrush)
            {
                return false;
            }

            if (ProGpuImageBrushSource.GetBitmap(imageBrush.Source) is not IDrawableBitmapImpl bitmap)
            {
                return true;
            }

            var bitmapTexture = ResolveBitmapTexture(bitmap);
            if (bitmapTexture == null)
            {
                return true;
            }

            var imageSize = bitmap.PixelSize.ToSizeWithDpi(bitmap.Dpi);
            if (imageSize.Width <= 0 || imageSize.Height <= 0 || targetRect.Width <= 0 || targetRect.Height <= 0)
            {
                return true;
            }

            var calculator = new ProGpuTileBrushCalculator(imageBrush, imageSize, targetRect.Size);
            var sourceRect = calculator.SourceRect;
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
            {
                return true;
            }

            var targetOffset = imageBrush.DestinationRect.Unit == RelativeUnit.Relative
                ? targetRect.Position
                : default;
            var destinationRect = sourceRect.TransformToAABB(calculator.IntermediateTransform);
            var viewport = calculator.IntermediateClip;
            if (imageBrush.TileMode == TileMode.None)
            {
                destinationRect = destinationRect.Translate(targetOffset);
                viewport = viewport.Translate(targetOffset);
            }
            else
            {
                var tileOffset = targetOffset + calculator.DestinationRect.Position;
                destinationRect = destinationRect.Translate(tileOffset);
                viewport = new Avalonia.Rect(tileOffset, calculator.DestinationRect.Size);
            }

            var sourceScaleX = bitmap.PixelSize.Width / imageSize.Width;
            var sourceScaleY = bitmap.PixelSize.Height / imageSize.Height;
            var textureSourceRect = new Avalonia.Rect(
                sourceRect.X * sourceScaleX,
                sourceRect.Y * sourceScaleY,
                sourceRect.Width * sourceScaleX,
                sourceRect.Height * sourceScaleY);

            var brushTransform = Matrix.Identity;
            if (brush.Transform != null)
            {
                var origin = brush.TransformOrigin.ToPixels(targetRect);
                var offset = Matrix.CreateTranslation(origin);
                brushTransform = (-offset) * brush.Transform.Value * offset;
            }

            var imageTransform = brushTransform * RenderTransform;
            var viewportPath = PrimitivePathGeometry.CreateRectangle(
                (float)viewport.X,
                (float)viewport.Y,
                (float)viewport.Width,
                (float)viewport.Height);

            DrawingContext.PushGeometryClip(clipPath, ToMatrix4x4(RenderTransform));
            DrawingContext.PushGeometryClip(viewportPath, ToMatrix4x4(imageTransform));
            if (!NearlyEqual(brush.Opacity, 1.0))
            {
                DrawingContext.PushOpacity((float)brush.Opacity);
            }

            DrawingContext.DrawTexture(
                bitmapTexture,
                ToLocalProGpuRect(destinationRect),
                ToLocalProGpuRect(textureSourceRect),
                ToMatrix4x4(imageTransform),
                ToTextureSamplingMode(RenderOptions.BitmapInterpolationMode));

            if (!NearlyEqual(brush.Opacity, 1.0))
            {
                DrawingContext.PopOpacity();
            }
            DrawingContext.PopGeometryClip();
            DrawingContext.PopGeometryClip();
            return true;
        }

        private static bool RequiresBrushClipPath(IBrush? brush)
            => brush is ISceneBrush or ISceneBrushContent or IImageBrush;

        internal static bool SupportsRetainedCompositionBrush(IBrush brush) =>
            brush is ISolidColorBrush or
                ILinearGradientBrush or
                IRadialGradientBrush or
                IConicGradientBrush;

        internal static bool SupportsRetainedCompositionOpacityMask(
            IBrush brush) =>
            SupportsRetainedCompositionBrush(brush) ||
            brush is ISceneBrush or ISceneBrushContent or IImageBrush;

        internal ProGPU.Vector.Brush? ConvertRetainedCompositionBrush(
            IBrush? avaloniaBrush,
            Avalonia.Rect targetRect) =>
            ConvertBrush(avaloniaBrush, targetRect, Matrix.Identity);

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        internal GpuPicture RecordRetainedCompositionOpacityMask(
            IBrush mask,
            Avalonia.Rect bounds)
        {
            var ownerTransform = _currentTransform;
            var ownerOpacity = _currentOpacity;
            var ownerRenderOptions = RenderOptions;
            var ownerTextOptions = TextOptions;
            int ownerOpacityMaskDepth = _opacityMaskDepth;

            _currentTransform = _postTransform?.Invert() ?? Matrix.Identity;
            _currentOpacity = 1;
            RenderOptions = default;
            TextOptions = default;
            _opacityMaskDepth = 0;
            try
            {
                return RecordOpacityMask(mask, bounds);
            }
            finally
            {
                _opacityMaskDepth = ownerOpacityMaskDepth;
                TextOptions = ownerTextOptions;
                RenderOptions = ownerRenderOptions;
                _currentOpacity = ownerOpacity;
                _currentTransform = ownerTransform;
            }
        }
#endif

        private ProGPU.Vector.Brush? ConvertBrush(
            IBrush? avaloniaBrush,
            Avalonia.Rect? targetRect = null) =>
            ConvertBrush(avaloniaBrush, targetRect, RenderTransform);

        private ProGPU.Vector.Brush? ConvertBrush(
            IBrush? avaloniaBrush,
            Avalonia.Rect? targetRect,
            Matrix transform)
        {
            if (avaloniaBrush == null) return null;

            float opacity = (float)avaloniaBrush.Opacity;

            if (avaloniaBrush is ISolidColorBrush solid)
            {
                var c = solid.Color;
                return _offscreenCache.GetSolidBrush(c.R, c.G, c.B, c.A, opacity);
            }
            else if (avaloniaBrush is ILinearGradientBrush linear)
            {
                var bounds = targetRect ?? default;
                var start = TransformPoint(
                    linear.StartPoint.ToPixels(bounds),
                    transform);
                var end = TransformPoint(
                    linear.EndPoint.ToPixels(bounds),
                    transform);
                var stops = new ProGPU.Vector.GradientStop[linear.GradientStops.Count];
                for (int i = 0; i < stops.Length; i++)
                {
                    var st = linear.GradientStops[i];
                    var c = st.Color;
                    stops[i] = new ProGPU.Vector.GradientStop(
                        new Vector4(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f),
                        (float)st.Offset
                    );
                }
                return new ProGPU.Vector.LinearGradientBrush(start, end, stops)
                {
                    Opacity = opacity,
                    SpreadMethod = ToGradientSpreadMethod(linear.SpreadMethod)
                };
            }
            else if (avaloniaBrush is IRadialGradientBrush radial)
            {
                var bounds = targetRect ?? default;
                var centerPoint = radial.Center.ToPixels(bounds);
                var originPoint = radial.GradientOrigin.ToPixels(bounds);
                var center = TransformPoint(centerPoint, transform);
                var origin = TransformPoint(originPoint, transform);
                var radiusXPoint = TransformPoint(
                    centerPoint +
                    new Vector(radial.RadiusX.ToValue(bounds.Width), 0),
                    transform);
                var radiusYPoint = TransformPoint(
                    centerPoint +
                    new Vector(0, radial.RadiusY.ToValue(bounds.Height)),
                    transform);
                var radiusX = Vector2.Distance(center, radiusXPoint);
                var radiusY = Vector2.Distance(center, radiusYPoint);
                var stops = new ProGPU.Vector.GradientStop[radial.GradientStops.Count];
                for (int i = 0; i < stops.Length; i++)
                {
                    var st = radial.GradientStops[i];
                    var c = st.Color;
                    stops[i] = new ProGPU.Vector.GradientStop(
                        new Vector4(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f),
                        (float)st.Offset
                    );
                }
                return new ProGPU.Vector.RadialGradientBrush(center, origin, radiusX, radiusY, stops)
                {
                    Opacity = opacity,
                    SpreadMethod = ToGradientSpreadMethod(radial.SpreadMethod)
                };
            }
            else if (avaloniaBrush is IConicGradientBrush conic)
            {
                var bounds = targetRect ?? default;
                Point centerPoint = conic.Center.ToPixels(bounds);
                double startRadians =
                    (conic.Angle - 90d) * Math.PI / 180d;
                Point directionPoint = centerPoint + new Vector(
                    Math.Cos(startRadians),
                    Math.Sin(startRadians));
                Vector2 center = TransformPoint(centerPoint, transform);
                Vector2 direction = TransformPoint(
                    directionPoint,
                    transform) -
                    center;
                float startAngle = MathF.Atan2(
                    direction.Y,
                    direction.X) *
                    (180f / MathF.PI);
                if (startAngle < 0f)
                    startAngle += 360f;

                var stops =
                    new ProGPU.Vector.GradientStop[
                        conic.GradientStops.Count];
                for (int i = 0; i < stops.Length; i++)
                {
                    IGradientStop stop = conic.GradientStops[i];
                    Avalonia.Media.Color color = stop.Color;
                    stops[i] = new ProGPU.Vector.GradientStop(
                        new Vector4(
                            color.R / 255f,
                            color.G / 255f,
                            color.B / 255f,
                            color.A / 255f),
                        (float)stop.Offset);
                }

                return new ProGPU.Vector.SweepGradientBrush(center, stops)
                {
                    Opacity = opacity,
                    StartAngle = 0f,
                    EndAngle = 360f,
                    CoordinateTransform =
                        Matrix4x4.CreateTranslation(
                            -center.X,
                            -center.Y,
                            0f) *
                        Matrix4x4.CreateRotationZ(
                            -startAngle * MathF.PI / 180f) *
                        Matrix4x4.CreateTranslation(
                            center.X,
                            center.Y,
                            0f),
                    SpreadMethod =
                        ToGradientSpreadMethod(conic.SpreadMethod)
                };
            }

            return null;
        }

        private ProGPU.Vector.Pen? ConvertPen(IPen? avaloniaPen, Avalonia.Rect? targetRect = null)
        {
            if (avaloniaPen == null) return null;
            var brush = ConvertBrush(avaloniaPen.Brush, targetRect);
            if (brush == null) return null;

            double[]? dashArray = null;
            if (avaloniaPen.DashStyle?.Dashes is { Count: > 0 } dashes)
            {
                dashArray = new double[dashes.Count];
                for (int index = 0; index < dashArray.Length; index++)
                {
                    dashArray[index] = dashes[index];
                }
            }

            var lineJoin = avaloniaPen.LineJoin switch
            {
                Avalonia.Media.PenLineJoin.Bevel => ProGPU.Vector.PenLineJoin.Bevel,
                Avalonia.Media.PenLineJoin.Round => ProGPU.Vector.PenLineJoin.Round,
                _ => ProGPU.Vector.PenLineJoin.Miter
            };
            var lineCap = avaloniaPen.LineCap switch
            {
                Avalonia.Media.PenLineCap.Round => ProGPU.Vector.PenLineCap.Round,
                Avalonia.Media.PenLineCap.Square => ProGPU.Vector.PenLineCap.Square,
                _ => ProGPU.Vector.PenLineCap.Flat
            };

            if (dashArray == null && avaloniaPen.Brush is ISolidColorBrush solid)
            {
                var color = solid.Color;
                return _offscreenCache.GetSolidPen(
                    color.R,
                    color.G,
                    color.B,
                    color.A,
                    (float)avaloniaPen.Brush.Opacity,
                    (float)avaloniaPen.Thickness,
                    lineJoin,
                    (float)avaloniaPen.MiterLimit,
                    lineCap);
            }

            return new ProGPU.Vector.Pen(
                brush,
                (float)avaloniaPen.Thickness,
                lineJoin,
                (float)avaloniaPen.MiterLimit,
                lineCap,
                lineCap,
                lineCap,
                dashArray,
                avaloniaPen.DashStyle?.Offset ?? 0.0);
        }

        private static ProGPU.Scene.Rect ToLocalProGpuRect(Avalonia.Rect rect)
        {
            return new ProGPU.Scene.Rect((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
        }

        private static ProGPU.Vector.GradientSpreadMethod ToGradientSpreadMethod(
            Avalonia.Media.GradientSpreadMethod spreadMethod)
        {
            return spreadMethod switch
            {
                Avalonia.Media.GradientSpreadMethod.Reflect => ProGPU.Vector.GradientSpreadMethod.Reflect,
                Avalonia.Media.GradientSpreadMethod.Repeat => ProGPU.Vector.GradientSpreadMethod.Repeat,
                _ => ProGPU.Vector.GradientSpreadMethod.Pad
            };
        }

        private static TextureSamplingMode ToTextureSamplingMode(
            Avalonia.Media.Imaging.BitmapInterpolationMode interpolationMode)
        {
            return interpolationMode == Avalonia.Media.Imaging.BitmapInterpolationMode.None
                ? TextureSamplingMode.Nearest
                : TextureSamplingMode.Linear;
        }

#if !AVALONIA11
        private Avalonia.Media.TextOptions GetEffectiveTextOptions()
        {
            var effective = TextOptions;

#pragma warning disable CS0618
            if (effective.TextRenderingMode == Avalonia.Media.TextRenderingMode.Unspecified &&
                RenderOptions.TextRenderingMode != Avalonia.Media.TextRenderingMode.Unspecified)
            {
                effective = effective with { TextRenderingMode = RenderOptions.TextRenderingMode };
            }
#pragma warning restore CS0618

            if (_disableSubpixelTextRendering &&
                effective.TextRenderingMode == Avalonia.Media.TextRenderingMode.SubpixelAntialias)
            {
                effective = effective with { TextRenderingMode = Avalonia.Media.TextRenderingMode.Antialias };
            }

            return effective;
        }
#endif

        private static ProGPU.Scene.TextRenderingMode ToProGpuTextRenderingMode(
            Avalonia.Media.TextRenderingMode mode)
        {
            return mode switch
            {
                Avalonia.Media.TextRenderingMode.SubpixelAntialias => ProGPU.Scene.TextRenderingMode.ClearType,
                Avalonia.Media.TextRenderingMode.Alias => ProGPU.Scene.TextRenderingMode.Aliased,
                _ => ProGPU.Scene.TextRenderingMode.Grayscale
            };
        }

#if !AVALONIA11
        private static ProGPU.Scene.TextHintingMode ToProGpuTextHintingMode(
            Avalonia.Media.TextHintingMode mode)
        {
            return mode switch
            {
                Avalonia.Media.TextHintingMode.None => ProGPU.Scene.TextHintingMode.Animated,
                Avalonia.Media.TextHintingMode.Strong => ProGPU.Scene.TextHintingMode.Fixed,
                _ => ProGPU.Scene.TextHintingMode.Auto
            };
        }
#endif

        private static System.Numerics.Matrix4x4 ToMatrix4x4(Avalonia.Matrix m)
        {
            return new System.Numerics.Matrix4x4(
                (float)m.M11, (float)m.M12, 0f, 0f,
                (float)m.M21, (float)m.M22, 0f, 0f,
                0f,           0f,           1f, 0f,
                (float)m.M31, (float)m.M32, 0f, 1f
            );
        }

        internal static System.Numerics.Matrix4x4 ToProGpuMatrix(Avalonia.Matrix matrix) =>
            ToMatrix4x4(matrix);

        private static CompositorHostFrame CreateHostFrame(
            uint renderTargetWidth,
            uint renderTargetHeight)
        {
            // Avalonia's composition drawing context has already applied the
            // target scaling to command transforms. ProGPU therefore consumes
            // a physical-pixel command space here; applying Dpi again would
            // double-scale culling, glyph raster size, and subpixel phase.
            return CompositorHostFrame.FromRenderTarget(
                renderTargetWidth,
                renderTargetHeight,
                1f);
        }

        private static bool TryGetDpiScale(Vector dpi, out double scaleX, out double scaleY)
        {
            scaleX = dpi.X / 96.0;
            scaleY = dpi.Y / 96.0;
            return double.IsFinite(scaleX) &&
                   double.IsFinite(scaleY) &&
                   scaleX > 0.0 &&
                   scaleY > 0.0;
        }

        private static bool NearlyEqual(double left, double right)
        {
            return Math.Abs(left - right) < 0.0001;
        }

        internal static unsafe void RenderToTexture(ProGPU.Scene.DrawingContext sourceContext, GpuTexture texture, Vector dpi, bool isTextureFresh = false)
        {
            var context = texture.Context;
            lock (context.RenderLock)
            {
                if (context.IsDisposed) return;
                WgpuContext.Current = context;
                s_wgpuContext = context;
                var compositor = GetCompositor(context, texture.Format);
                var hostFrame = CreateHostFrame(
                    texture.Width,
                    texture.Height);

                var drawingVisual = new RecordedDrawingVisual(sourceContext);
                drawingVisual.Size = hostFrame.LogicalSize;

                compositor.RenderOffscreen(
                    drawingVisual,
                    hostFrame,
                    texture,
                    0.0f,
                    new Vector4(0f, 0f, 0f, 0f), // Transparent clear color for layers
                    loadExistingContents: !isTextureFresh
                );
                texture.NotifyExternalContentChanged();
            }
        }

        private sealed class RecordedDrawingVisual : ProGPU.Scene.Visual, IOwnedRenderCommandCache
        {
            private readonly ProGPU.Scene.DrawingContext _context;

            public RecordedDrawingVisual(ProGPU.Scene.DrawingContext context)
            {
                _context = context;
            }

            public ProGPU.Scene.DrawingContext GetOrUpdateRenderCommandCache() => _context;
        }
    }
}
