using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
using Avalonia.Rendering.Composition.Drawing;
using Avalonia.Rendering.Composition.Server;
#endif
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using SkiaSharp;
using AVector = Avalonia.Vector;
using SceneBrush = ProGPU.Vector.Brush;
using ScenePen = ProGPU.Vector.Pen;
using SceneRect = ProGPU.Scene.Rect;
using AColor = Avalonia.Media.Color;

namespace Avalonia.ProGpu;

internal readonly record struct AvaloniaSkiaClipState(
    SKRectI DeviceBounds,
    bool IsRect);

/// <summary>
/// Records Avalonia drawing contracts as typed ProGPU retained commands.
/// Device submission is deferred until disposal so nested Avalonia state
/// scopes never cross a GPU command-encoder lifetime.
/// </summary>
internal partial class DrawingContextImpl :
    IDrawingContextImpl,
    IDrawingContextWithAcrylicLikeSupport,
    IDrawingContextImplWithEffects
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    , ICompositionRenderDataDrawingContextFeature
    , ICompositionVisualTreeDrawingContextFeature
#endif
{
    private const string SurfaceHandleKind = "WGPU_SURFACE";
    private readonly IDisposable?[]? _ownedFrameObjects;
    private readonly ILockedFramebuffer? _framebuffer;
    private readonly bool _reusableRecording;
    private readonly bool _disableSubpixelTextRendering;
    private readonly OffscreenTextureCache _resources;
    private readonly GpuTexture? _gpuTarget;
    private readonly object? _submissionGate;
    private readonly Action? _beforeSubmit;
    private readonly Action<bool>? _afterSubmit;
    private readonly Matrix? _physicalScale;
    private readonly string _presentationPath;
    private readonly AvaloniaDrawingState _drawingState;
    private readonly Stack<double> _opacityFrames;
    private readonly Stack<bool> _clipFrames;
    private readonly Stack<AvaloniaSkiaClipState> _skiaClipFrames;
    private readonly Stack<RenderOptions> _renderOptionFrames;
    private readonly Stack<RenderCommandPresentationDependencies>
        _renderOptionDependencyFrames;
#if !AVALONIA11
    private readonly Stack<TextOptions> _textOptionFrames;
    private readonly Stack<RenderCommandPresentationDependencies>
        _textOptionDependencyFrames;
#endif
    private Matrix _transform = Matrix.Identity;
    private double _opacity = 1d;
    private Vector4 _clearColor = Vector4.Zero;
    private bool _leased;
    private bool _disposed;
    private bool _recordingReturned;
    private AvaloniaSkiaClipState _skiaClipState;
    private RenderCommandPresentationDependencies
        _presentationDependencies;
    private bool _insideRetainedVisual;
    internal readonly PixelSize _size;

#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    private ProGpuCompositionServerBackend? _compositionBackend;
    private static readonly bool s_retainedSceneEnabled =
        !string.Equals(
            Environment.GetEnvironmentVariable("PROGPU_AVALONIA_RETAINED_SCENE"),
            "0",
            StringComparison.Ordinal);
    internal static bool UseRetainedAvaloniaScene => s_retainedSceneEnabled;
#endif

    public struct CreateInfo
    {
        public PixelSize? Size;
        public AVector Dpi;
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

    public DrawingContextImpl(
        CreateInfo createInfo,
        params IDisposable?[]? disposables)
    {
        Dpi = createInfo.Dpi;
        _ownedFrameObjects = disposables;
        _reusableRecording = createInfo.PreserveRecordedCommandsOnDispose;
        _disableSubpixelTextRendering =
            createInfo.DisableSubpixelTextRendering;
        _resources =
            createInfo.CacheHolder as OffscreenTextureCache ??
            AvaloniaGpuDevicePool.ThreadCache;
        _drawingState = _reusableRecording
            ? new AvaloniaDrawingState()
            : _resources.RentDrawingState();
        _opacityFrames = _drawingState.OpacityFrames;
        _clipFrames = _drawingState.GeometryClipFrames;
        _skiaClipFrames = _drawingState.SkiaClipFrames;
        _renderOptionFrames = _drawingState.RenderOptionFrames;
        _renderOptionDependencyFrames =
            _drawingState.RenderOptionDependencyFrames;
#if !AVALONIA11
        _textOptionFrames = _drawingState.TextOptionFrames;
        _textOptionDependencyFrames =
            _drawingState.TextOptionDependencyFrames;
#endif
        _gpuTarget = createInfo.GpuRenderTarget;
        _submissionGate = createInfo.GpuRenderSynchronizationLock;
        _beforeSubmit = createInfo.GpuRenderStarting;
        _afterSubmit = createInfo.GpuRenderCompleted;

        if (disposables is not null)
        {
            foreach (IDisposable? item in disposables)
            {
                if (item is ILockedFramebuffer framebuffer)
                {
                    _framebuffer = framebuffer;
                    break;
                }
            }
        }

        _size = _gpuTarget is not null
            ? new PixelSize(
                checked((int)_gpuTarget.Width),
                checked((int)_gpuTarget.Height))
            : createInfo.Size ??
              _framebuffer?.Size ??
              default;
        _skiaClipState = CreateFullSkiaClipState();

        if (createInfo.ScaleDrawingToDpi &&
            TryGetScale(Dpi, out double scaleX, out double scaleY) &&
            (Math.Abs(scaleX - 1d) > 0.0001 ||
             Math.Abs(scaleY - 1d) > 0.0001))
        {
            _physicalScale = Matrix.CreateScale(scaleX, scaleY);
        }

        _presentationPath =
            createInfo.PresentationPath ??
            (_framebuffer is IPlatformHandle
             {
                 HandleDescriptor: SurfaceHandleKind
             }
                ? "SilkNetWebGpuSurface"
                : "AvaloniaFramebuffer");

        DrawingContext = _reusableRecording
            ? new ProGPU.Scene.DrawingContext()
            : _resources.RentRecordingContext();

        TextureFormat desiredFormat =
            _gpuTarget?.Format ??
            GetFramebufferFormat(_framebuffer);
        GpuContext = _gpuTarget?.Context ??
            AvaloniaGpuDevicePool.ResolveOrCreate(
                GetSurfaceHandle(_framebuffer),
                desiredFormat);
    }

    public ProGPU.Scene.DrawingContext DrawingContext { get; private set; }

    public AVector Dpi { get; }

    public RenderOptions RenderOptions { get; private set; }

#if !AVALONIA11
    public TextOptions TextOptions { get; private set; }
#endif

    internal WgpuContext GpuContext { get; }

    public Matrix Transform
    {
        get => _transform;
        set
        {
            EnsureAvailable();
            _transform = value;
        }
    }

    private Matrix CommandTransform =>
        _physicalScale is { } scale
            ? _transform * scale
            : _transform;

    public void Reset()
    {
        if (_disposed && _reusableRecording)
            _disposed = false;
        EnsureAvailable();
        DiscardUnbalancedEffectScopes();
        _transform = Matrix.Identity;
        _opacity = 1d;
        _opacityFrames.Clear();
        _clipFrames.Clear();
        _skiaClipFrames.Clear();
        _skiaClipState = CreateFullSkiaClipState();
        _renderOptionFrames.Clear();
        _renderOptionDependencyFrames.Clear();
#if !AVALONIA11
        _textOptionFrames.Clear();
        _textOptionDependencyFrames.Clear();
        TextOptions = default;
#endif
        RenderOptions = default;
        _presentationDependencies =
            RenderCommandPresentationDependencies.None;
        DrawingContext.Clear();
    }

    public void Clear(AColor color)
    {
        EnsureAvailable();
        _clearColor = ToColor(color);
        DrawingContext.PushBlendMode(GpuBlendMode.Src);
        DrawingContext.DrawRectangle(
            _resources.GetSolidBrush(
                color.R,
                color.G,
                color.B,
                color.A,
                1f),
            null,
            new SceneRect(0, 0, _size.Width, _size.Height));
        DrawingContext.PopBlendMode();
    }

    public void DrawBitmap(
        IBitmapImpl source,
        double opacity,
        Rect sourceRect,
        Rect destRect)
    {
        EnsureAvailable();
        if (!TryResolveTexture(source, out GpuTexture? texture))
            return;

        float alpha = ClampUnit(opacity);
        if (alpha < 1f)
            DrawingContext.PushOpacity(alpha);
        DrawingContext.DrawTexture(
            texture!,
            ToLocalRect(destRect),
            ToLocalRect(sourceRect),
            ToProGpuMatrix(CommandTransform),
            GetTextureSampling());
        MarkLastCommandPresentationDependencies(
            _presentationDependencies &
            RenderCommandPresentationDependencies.TextureSampling);
        if (alpha < 1f)
            DrawingContext.PopOpacity();
    }

    public void DrawBitmap(
        IBitmapImpl source,
        IBrush opacityMask,
        Rect opacityMaskRect,
        Rect destRect)
    {
        EnsureAvailable();
        PushOpacityMask(opacityMask, opacityMaskRect);
        DrawBitmap(
            source,
            1d,
            new Rect(source.PixelSize.ToSize(96)),
            destRect);
        PopOpacityMask();
    }

    public void DrawLine(IPen? pen, Point p1, Point p2)
    {
        EnsureAvailable();
        ScenePen? translated = ConvertPen(pen);
        if (translated is null)
            return;
        DrawingContext.DrawLine(
            translated,
            ToVector(p1),
            ToVector(p2),
            ToProGpuMatrix(CommandTransform));
    }

    public void DrawGeometry(
        IBrush? brush,
        IPen? pen,
        IGeometryImpl geometry)
    {
        EnsureAvailable();
        if (geometry is not AvaloniaPathAdapter path)
            return;

        Rect target = geometry.Bounds;
        if (brush is not null &&
            TryDrawBrushContent(brush, target, path.Path))
        {
            brush = null;
        }

        SceneBrush? fill = ConvertBrush(brush, target);
        ScenePen? stroke = ConvertPen(pen, target);
        if (fill is null && stroke is null)
            return;
        DrawingContext.DrawPath(
            fill,
            stroke,
            path.Path,
            ToProGpuMatrix(CommandTransform),
            path.GetRenderCommandGeometryCache());
    }

    public void DrawRectangle(
        IExperimentalAcrylicMaterial material,
        RoundedRect rect)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(material);
        var materialBrush = new BackdropMaterialBrush
        {
            TintColor = ToColor(material.TintColor),
            TintOpacity = ClampUnit(material.TintOpacity),
            FallbackColor = ToColor(material.FallbackColor),
            UseFallback =
                material.BackgroundSource ==
                AcrylicBackgroundSource.None
        };
        DrawingContext.DrawBackdropMaterial(
            materialBrush,
            ToLocalRect(rect.Rect),
            ToCornerVector(rect, horizontal: true),
            ToCornerVector(rect, horizontal: false),
            ToProGpuMatrix(CommandTransform));
    }

    public void DrawRectangle(
        IBrush? brush,
        IPen? pen,
        RoundedRect rect,
        BoxShadows boxShadows = default)
    {
        EnsureAvailable();
        if (boxShadows.Count > 0)
            DrawBoxShadows(rect, boxShadows);

        ProGPU.Vector.PathGeometry? clipPath =
            rect.IsRounded && !rect.IsUniform
                ? CreateRoundedRectPath(rect)
                : null;
        if (brush is not null &&
            TryDrawBrushContent(brush, rect.Rect, clipPath))
        {
            brush = null;
        }

        SceneBrush? fill = ConvertBrush(brush, rect.Rect);
        ScenePen? stroke = ConvertPen(pen, rect.Rect);
        if (fill is null && stroke is null)
            return;

        if (clipPath is not null)
        {
            DrawingContext.DrawPath(
                fill,
                stroke,
                clipPath,
                ToProGpuMatrix(CommandTransform));
            return;
        }

        SceneRect target = ToLocalRect(rect.Rect);
        Matrix4x4 transform = ToProGpuMatrix(CommandTransform);
        if (rect.IsRounded)
        {
            DrawingContext.DrawRoundedRectangle(
                fill,
                stroke,
                target,
                (float)rect.RadiiTopLeft.X,
                (float)rect.RadiiTopLeft.Y,
                transform);
        }
        else
        {
            DrawingContext.DrawRectangle(
                fill,
                stroke,
                target,
                transform);
        }
    }

    public void DrawRegion(
        IBrush? brush,
        IPen? pen,
        IPlatformRenderInterfaceRegion region)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(region);
        foreach (LtrbPixelRect item in region.Rects)
        {
            DrawRectangle(
                brush,
                pen,
                new RoundedRect(
                    new Rect(
                        item.Left,
                        item.Top,
                        item.Right - item.Left,
                        item.Bottom - item.Top)));
        }
    }

    public void DrawEllipse(
        IBrush? brush,
        IPen? pen,
        Rect rect)
    {
        EnsureAvailable();
        if (brush is not null &&
            RequiresBrushContentClip(brush))
        {
            var ellipsePath = AvaloniaGeometryFactory.Ellipse(rect);
            if (TryDrawBrushContent(brush, rect, ellipsePath.Path))
                brush = null;
        }

        SceneBrush? fill = ConvertBrush(brush, rect);
        ScenePen? stroke = ConvertPen(pen, rect);
        if (fill is null && stroke is null)
            return;
        DrawingContext.DrawEllipse(
            fill,
            stroke,
            new Vector2(
                (float)(rect.X + rect.Width * 0.5),
                (float)(rect.Y + rect.Height * 0.5)),
            (float)(Math.Abs(rect.Width) * 0.5),
            (float)(Math.Abs(rect.Height) * 0.5),
            ToProGpuMatrix(CommandTransform));
    }

    public void DrawGlyphRun(
        IBrush? foreground,
        IGlyphRunImpl glyphRun)
    {
        EnsureAvailable();
        if (foreground is null || glyphRun is not GlyphRunImpl run)
            return;
        SceneBrush? fill = ConvertBrush(foreground, run.Bounds);
        if (fill is null)
            return;

        ProGPU.Scene.TextRenderingMode rendering =
            ResolveTextRenderingMode();
        ProGPU.Scene.TextHintingMode hinting =
            ResolveTextHintingMode();
        DrawingContext.DrawGlyphRun(
            run.GlyphIndices,
            run.ProGpuGlyphPositions,
            run.Typeface.Font,
            (float)run.FontRenderingEmSize,
            fill,
            ToVector(run.BaselineOrigin),
            ToProGpuMatrix(CommandTransform),
            textRenderingMode: rendering,
            textHintingMode: hinting,
            preferGlyphAtlas: true,
            useLogicalGlyphAtlasResolution: false);
        MarkLastCommandPresentationDependencies(
            _presentationDependencies &
            (RenderCommandPresentationDependencies.TextRendering |
             RenderCommandPresentationDependencies.TextHinting));
    }

    public IDrawingContextLayerImpl CreateLayer(PixelSize size)
    {
        EnsureAvailable();
        return new SurfaceRenderTarget(
            new SurfaceRenderTarget.CreateInfo
            {
                Width = size.Width,
                Height = size.Height,
                Dpi = Dpi,
                UseScaledDrawing = false,
                DisableTextLcdRendering =
                    _disableSubpixelTextRendering,
                Context = GpuContext
            });
    }

    public void PushClip(Rect clip)
    {
        EnsureAvailable();
        DrawingContext.PushClip(
            ToLocalRect(clip),
            ToProGpuMatrix(CommandTransform));
        _clipFrames.Push(false);
        PushSkiaClipState(clip, isGeometryClip: false);
    }

    public void PushClip(RoundedRect clip)
    {
        EnsureAvailable();
        if (!clip.IsRounded)
        {
            PushClip(clip.Rect);
            return;
        }
        DrawingContext.PushGeometryClip(
            CreateRoundedRectPath(clip),
            ToProGpuMatrix(CommandTransform));
        _clipFrames.Push(true);
        PushSkiaClipState(clip.Rect, isGeometryClip: true);
    }

    public void PushClip(IPlatformRenderInterfaceRegion region)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(region);
        LtrbPixelRect bounds = region.Bounds;
        PushClip(
            new Rect(
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top));
    }

    public void PopClip()
    {
        EnsureAvailable();
        if (_clipFrames.Count == 0)
            return;
        if (_clipFrames.Pop())
            DrawingContext.PopGeometryClip();
        else
            DrawingContext.PopClip();
        PopSkiaClipState();
    }

    public void PushLayer(Rect bounds)
    {
        EnsureAvailable();
        DrawingContext.PushOpacity(1f);
    }

    public void PopLayer()
    {
        EnsureAvailable();
        DrawingContext.PopOpacity();
    }

    public void PushOpacity(double opacity, Rect? bounds)
    {
        EnsureAvailable();
        _opacityFrames.Push(_opacity);
        _opacity *= ClampUnit(opacity);
        DrawingContext.PushOpacity(ClampUnit(opacity));
    }

    public void PopOpacity()
    {
        EnsureAvailable();
        if (_opacityFrames.Count == 0)
            return;
        _opacity = _opacityFrames.Pop();
        DrawingContext.PopOpacity();
    }

    public void PushGeometryClip(IGeometryImpl clip)
    {
        EnsureAvailable();
        if (clip is AvaloniaPathAdapter path)
        {
            DrawingContext.PushGeometryClip(
                path.Path,
                ToProGpuMatrix(CommandTransform));
            PushSkiaClipState(clip.Bounds, isGeometryClip: true);
        }
        else
        {
            DrawingContext.PushClip(
                ToLocalRect(clip.Bounds),
                ToProGpuMatrix(CommandTransform));
            PushSkiaClipState(clip.Bounds, isGeometryClip: false);
        }
    }

    public void PopGeometryClip()
    {
        EnsureAvailable();
        DrawingContext.PopGeometryClip();
        PopSkiaClipState();
    }

    public void PushOpacityMask(IBrush mask, Rect bounds)
    {
        EnsureAvailable();
        ArgumentNullException.ThrowIfNull(mask);
        SceneBrush? translated = ConvertBrush(mask, bounds);
        if (translated is not null)
        {
            DrawingContext.PushOpacityMask(
                translated,
                ToProGpuRect(bounds));
            return;
        }

        GpuPicture picture = RecordBrushPicture(mask, bounds);
        DrawingContext.RetainResource(picture);
        DrawingContext.PushOpacityMask(picture, ToProGpuRect(bounds));
    }

    public void PopOpacityMask()
    {
        EnsureAvailable();
        DrawingContext.PopOpacityMask();
    }

    public void PushRenderOptions(RenderOptions renderOptions)
    {
        EnsureAvailable();
        _renderOptionFrames.Push(RenderOptions);
        _renderOptionDependencyFrames.Push(_presentationDependencies);
        RenderOptions = renderOptions.MergeWith(RenderOptions);
        if (renderOptions.BitmapInterpolationMode !=
            BitmapInterpolationMode.Unspecified)
        {
            _presentationDependencies &=
                ~RenderCommandPresentationDependencies.TextureSampling;
        }
    }

    public void PopRenderOptions()
    {
        EnsureAvailable();
        if (_renderOptionFrames.Count > 0)
        {
            RenderOptions = _renderOptionFrames.Pop();
            _presentationDependencies =
                _renderOptionDependencyFrames.Pop();
        }
    }

#if !AVALONIA11
    public void PushTextOptions(TextOptions textOptions)
    {
        EnsureAvailable();
        _textOptionFrames.Push(TextOptions);
        _textOptionDependencyFrames.Push(_presentationDependencies);
        TextOptions = textOptions.MergeWith(TextOptions);
        if (textOptions.TextRenderingMode !=
            Avalonia.Media.TextRenderingMode.Unspecified)
        {
            _presentationDependencies &=
                ~RenderCommandPresentationDependencies.TextRendering;
        }
        if (textOptions.TextHintingMode !=
            Avalonia.Media.TextHintingMode.Unspecified)
        {
            _presentationDependencies &=
                ~RenderCommandPresentationDependencies.TextHinting;
        }
    }

    public void PopTextOptions()
    {
        EnsureAvailable();
        if (_textOptionFrames.Count > 0)
        {
            TextOptions = _textOptionFrames.Pop();
            _presentationDependencies =
                _textOptionDependencyFrames.Pop();
        }
    }
#endif

    public object? GetFeature(Type featureType)
    {
        ArgumentNullException.ThrowIfNull(featureType);
        if (featureType == typeof(IProGpuApiLeaseFeature) ||
            featureType ==
                typeof(Avalonia.Skia.ISkiaSharpApiLeaseFeature))
        {
            return new LeaseFeature(this);
        }
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
        if (featureType == typeof(ICompositionRenderDataDrawingContextFeature))
            return this;
        if (s_retainedSceneEnabled &&
            featureType == typeof(ICompositionVisualTreeDrawingContextFeature))
        {
            return this;
        }
#endif
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        EnsureAvailable();
        _disposed = true;
        DiscardUnbalancedEffectScopes();

        bool submitted = false;
        try
        {
            if (DrawingContext.Commands.Count > 0 &&
                _size.Width > 0 &&
                _size.Height > 0)
            {
                submitted = Submit();
            }
        }
        finally
        {
            _afterSubmit?.Invoke(submitted);
            if (!_reusableRecording)
            {
                ReturnRecordingContext();
                _resources.ReturnDrawingState(_drawingState);
            }
            foreach (IDisposable? owned in _ownedFrameObjects ?? [])
                owned?.Dispose();
        }
    }

    private bool Submit()
    {
        object gate = _submissionGate ?? GpuContext.RenderLock;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(
                GpuContext.IsDisposed,
                GpuContext);
            _beforeSubmit?.Invoke();
            using WgpuContext.CurrentContextScope scope =
                WgpuContext.PushCurrent(GpuContext);
            if (_gpuTarget is not null)
            {
                Compositor compositor =
                    AvaloniaGpuDevicePool.RenderToTexture(
                    GpuContext,
                    _resources,
                    DrawingContext,
                    _gpuTarget,
                    _size,
                    _clearColor);
                ReportFrame(compositor);
                return true;
            }

            if (_framebuffer is null)
                return false;
            IntPtr surface = GetSurfaceHandle(_framebuffer);
            if (surface != IntPtr.Zero)
            {
                Compositor compositor =
                    AvaloniaGpuDevicePool.RenderToSurface(
                        GpuContext,
                    _resources,
                    DrawingContext,
                    surface,
                        _size,
                        _clearColor);
                (_framebuffer as IGpuDirectPresentationFrame)?
                    .MarkGpuPresentationComplete();
                ReportFrame(compositor);
                return true;
            }

            Compositor framebufferCompositor =
                AvaloniaGpuDevicePool.RenderToFramebuffer(
                GpuContext,
                _resources,
                DrawingContext,
                _framebuffer,
                _clearColor);
            ReportFrame(framebufferCompositor);
            return true;
        }
    }

    private void ReturnRecordingContext()
    {
        if (_recordingReturned)
            return;
        _recordingReturned = true;
        _resources.ReturnRecordingContext(DrawingContext);
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_leased)
        {
            throw new InvalidOperationException(
                "The drawing context is unavailable while its ProGPU API lease is active.");
        }
    }

    private void CheckLease() => EnsureAvailable();

    private bool TryResolveTexture(
        IBitmapImpl source,
        out GpuTexture? texture)
    {
        texture = source switch
        {
            IPortableProGpuBitmapSource portable =>
                portable.GetTexture(GpuContext),
            IProGpuBitmapSource native =>
                ResolveNativeTexture(native),
            _ => null
        };
        return texture is { IsDisposed: false } &&
               texture.Context.SharesDeviceWith(GpuContext);
    }

    private GpuTexture? ResolveNativeTexture(IProGpuBitmapSource source)
    {
        source.EnsureGpuTexture();
        return source.Texture;
    }

    private static TextureFormat GetFramebufferFormat(
        ILockedFramebuffer? framebuffer) =>
        framebuffer?.Format == PixelFormats.Rgba8888
            ? TextureFormat.Rgba8Unorm
            : TextureFormat.Bgra8Unorm;

    private static IntPtr GetSurfaceHandle(
        ILockedFramebuffer? framebuffer) =>
        framebuffer is IPlatformHandle
        {
            HandleDescriptor: SurfaceHandleKind,
            Handle: var handle
        }
            ? handle
            : IntPtr.Zero;

    private static bool TryGetScale(
        AVector dpi,
        out double scaleX,
        out double scaleY)
    {
        scaleX = dpi.X / 96d;
        scaleY = dpi.Y / 96d;
        return double.IsFinite(scaleX) &&
               double.IsFinite(scaleY) &&
               scaleX > 0d &&
               scaleY > 0d;
    }

    internal SceneRect ToProGpuRect(Rect rect) =>
        ToLocalRect(rect.TransformToAABB(CommandTransform));

    private static SceneRect ToLocalRect(Rect rect) =>
        new(
            (float)rect.X,
            (float)rect.Y,
            (float)rect.Width,
            (float)rect.Height);

    private static Vector2 ToVector(Point point) =>
        new((float)point.X, (float)point.Y);

    internal static Matrix4x4 ToProGpuMatrix(Matrix matrix) =>
        new(
            (float)matrix.M11,
            (float)matrix.M12,
            0f,
            0f,
            (float)matrix.M21,
            (float)matrix.M22,
            0f,
            0f,
            0f,
            0f,
            1f,
            0f,
            (float)matrix.M31,
            (float)matrix.M32,
            0f,
            1f);

    private static Vector4 ToColor(AColor color) =>
        new(
            color.R / 255f,
            color.G / 255f,
            color.B / 255f,
            color.A / 255f);

    private static float ClampUnit(double value) =>
        double.IsFinite(value)
            ? (float)Math.Clamp(value, 0d, 1d)
            : 0f;

    private AvaloniaSkiaClipState CreateFullSkiaClipState()
    {
        SKRectI bounds = _size.Width > 0 && _size.Height > 0
            ? new SKRectI(0, 0, _size.Width, _size.Height)
            : SKRectI.Empty;
        return new AvaloniaSkiaClipState(
            bounds,
            IsRect: bounds.Right > bounds.Left &&
                bounds.Bottom > bounds.Top);
    }

    private void PushSkiaClipState(
        Rect localBounds,
        bool isGeometryClip)
    {
        Rect deviceBounds = localBounds.TransformToAABB(CommandTransform);
        bool isDeviceRect = !isGeometryClip &&
            IsAxisAlignedSkiaClipTransform(CommandTransform);
        PushSkiaDeviceClipState(
            ToLocalRect(deviceBounds),
            isDeviceRect);
    }

    private void PushSkiaDeviceClipState(
        SceneRect deviceBounds,
        bool isDeviceRect)
    {
        _skiaClipFrames.Push(_skiaClipState);
        if (_skiaClipState.DeviceBounds.Right <=
                _skiaClipState.DeviceBounds.Left ||
            _skiaClipState.DeviceBounds.Bottom <=
                _skiaClipState.DeviceBounds.Top)
        {
            return;
        }

        SKRectI incoming = SKCanvas.ToDeviceBounds(
            new SKRect(
                deviceBounds.X,
                deviceBounds.Y,
                deviceBounds.Right,
                deviceBounds.Bottom),
            roundToNearest: isDeviceRect);
        SKRectI intersection = SKRectI.Intersect(
            _skiaClipState.DeviceBounds,
            incoming);
        _skiaClipState = intersection.Right > intersection.Left &&
            intersection.Bottom > intersection.Top
                ? new AvaloniaSkiaClipState(
                    intersection,
                    _skiaClipState.IsRect && isDeviceRect)
                : new AvaloniaSkiaClipState(
                    SKRectI.Empty,
                    IsRect: false);
    }

    private void PopSkiaClipState()
    {
        if (_skiaClipFrames.Count > 0)
            _skiaClipState = _skiaClipFrames.Pop();
    }

    private static bool IsAxisAlignedSkiaClipTransform(Matrix matrix)
    {
        const double epsilon = 0.0001;
        return Math.Abs(matrix.M12) <= epsilon &&
            Math.Abs(matrix.M21) <= epsilon;
    }

    private TextureSamplingMode GetTextureSampling() =>
        RenderOptions.BitmapInterpolationMode switch
        {
            BitmapInterpolationMode.None =>
                TextureSamplingMode.Nearest,
            _ => TextureSamplingMode.Linear
        };

    private void MarkLastCommandPresentationDependencies(
        RenderCommandPresentationDependencies dependencies)
    {
        if (!_insideRetainedVisual ||
            dependencies ==
                RenderCommandPresentationDependencies.None ||
            DrawingContext.Commands.Count == 0)
        {
            return;
        }

        ref RenderCommand command = ref DrawingContext.Commands.AsSpan()[^1];
        command.PresentationDependencies |= dependencies;
    }

    private ProGPU.Scene.TextRenderingMode ResolveTextRenderingMode()
    {
#if !AVALONIA11
        if (_disableSubpixelTextRendering)
            return ProGPU.Scene.TextRenderingMode.Grayscale;
        return TextOptions.TextRenderingMode switch
        {
            Avalonia.Media.TextRenderingMode.Alias =>
                ProGPU.Scene.TextRenderingMode.Aliased,
            Avalonia.Media.TextRenderingMode.SubpixelAntialias =>
                ProGPU.Scene.TextRenderingMode.ClearType,
            _ => ProGPU.Scene.TextRenderingMode.Grayscale
        };
#else
        return _disableSubpixelTextRendering
            ? ProGPU.Scene.TextRenderingMode.Grayscale
            : ProGPU.Scene.TextRenderingMode.ClearType;
#endif
    }

    private ProGPU.Scene.TextHintingMode ResolveTextHintingMode()
    {
#if !AVALONIA11
        return TextOptions.TextHintingMode switch
        {
            Avalonia.Media.TextHintingMode.None =>
                ProGPU.Scene.TextHintingMode.Animated,
            Avalonia.Media.TextHintingMode.Strong =>
                ProGPU.Scene.TextHintingMode.Fixed,
            _ => ProGPU.Scene.TextHintingMode.Auto
        };
#else
        return ProGPU.Scene.TextHintingMode.Auto;
#endif
    }

    private sealed class LeaseFeature :
        IProGpuApiLeaseFeature,
        Avalonia.Skia.ISkiaSharpApiLeaseFeature
    {
        private readonly DrawingContextImpl _owner;

        internal LeaseFeature(DrawingContextImpl owner)
        {
            _owner = owner;
        }

        public IProGpuApiLease Lease() => new Lease(_owner);

        Avalonia.Skia.ISkiaSharpApiLease
            Avalonia.Skia.ISkiaSharpApiLeaseFeature.Lease() =>
                new ProGpuSkiaSharpApiLease(
                    new Lease(_owner),
                    _owner._skiaClipState);
    }

    private sealed class Lease : IProGpuApiLease
    {
        private DrawingContextImpl? _owner;
        private readonly int _threadId;
        private readonly WgpuContext.CurrentContextScope _contextScope;
        private bool _lockHeld;

        internal Lease(DrawingContextImpl owner)
        {
            owner.EnsureAvailable();
            _threadId = Environment.CurrentManagedThreadId;
            Monitor.Enter(owner.GpuContext.RenderLock);
            _lockHeld = true;
            try
            {
                _contextScope =
                    WgpuContext.PushCurrent(owner.GpuContext);
                owner._leased = true;
                _owner = owner;
            }
            catch
            {
                Monitor.Exit(owner.GpuContext.RenderLock);
                _lockHeld = false;
                throw;
            }
        }

        private DrawingContextImpl Owner =>
            _owner ??
            throw new ObjectDisposedException(nameof(IProGpuApiLease));

        public ProGPU.Scene.DrawingContext DrawingContext =>
            Owner.DrawingContext;

        public WgpuContext WgpuContext => Owner.GpuContext;

        public Matrix4x4 CurrentTransform =>
            ToProGpuMatrix(Owner.CommandTransform);

        public double CurrentOpacity => Owner._opacity;

        public PixelSize PixelSize => Owner._size;

        public AVector Dpi => Owner.Dpi;

        public void Dispose()
        {
            DrawingContextImpl? owner = _owner;
            if (owner is null)
                return;
            if (_threadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException(
                    "A ProGPU API lease must be returned on its acquisition thread.");
            }

            _owner = null;
            try
            {
                _contextScope.Dispose();
                owner._leased = false;
            }
            finally
            {
                if (_lockHeld)
                {
                    _lockHeld = false;
                    Monitor.Exit(owner.GpuContext.RenderLock);
                }
            }
        }
    }
}
