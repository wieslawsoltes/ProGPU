using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Backend.Native;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Samples;
using ProGPU.Text;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace ProGPU.Samples.Desktop;

internal static class NativeRendererSamplePage
{
    public static FrameworkElement Create()
    {
        if (AppState._wgpuContext is not { } context)
        {
            return CreateMessage(
                "Native C++ renderer unavailable",
                "The gallery WebGPU context has not been initialized.");
        }

        DawnGpuContext? dawnContext = null;
        if (context.BackendKind == WgpuBackendKind.DawnNative)
        {
            dawnContext = Program.CurrentDawnContext;
            if (dawnContext is null ||
                !ReferenceEquals(dawnContext.Context, context))
            {
                return CreateMessage(
                    "Native C++ Dawn renderer unavailable",
                    "The gallery does not own the typed Dawn context required " +
                    "to resolve provider procedures safely.");
            }
        }
        else if (context.BackendKind != WgpuBackendKind.SilkNative)
        {
            return CreateMessage(
                "Native C++ renderer unavailable",
                $"The {context.BackendKind} backend has no native adapter.");
        }

        try
        {
            var session = new NativeRendererSampleSession(
                context,
                dawnContext);
            FrameworkElement page = session.CreatePage();
            page.Unloaded += (_, _) => session.Dispose();
            return page;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            NativeRendererException)
        {
            return CreateMessage(
                "Native C++ renderer could not be loaded",
                exception.Message +
                " Run eng/build-progpu-native.sh once; use " +
                "--native-renderer only for the direct wgpu-native comparison.");
        }
    }

    private static FrameworkElement CreateMessage(
        string title,
        string detail)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20)
        };
        content.AddChild(CreateText(title, 22f, bold: true));
        content.AddChild(CreateText(detail, 13f));
        return content;
    }

    private static RichTextBlock CreateText(
        string text,
        float fontSize,
        bool bold = false)
    {
        var block = new RichTextBlock
        {
            Font = AppState._font,
            FontSize = fontSize,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Inline inline = new Run(text);
        block.Inlines.Add(bold ? new Bold(inline) : inline);
        return block;
    }

    private sealed class NativeRendererSampleSession : IDisposable
    {
        private const uint TargetWidth = 960;
        private const uint TargetHeight = 540;
        private const uint MaximumTargetDimension = 8192;
        private const int MaximumRectangles = 4096;

        private readonly NativeCompositor _compositor;
        private readonly GpuTexture _target;
        private readonly GpuTexture _externalImageSource;
        private readonly GpuTexture _externalImageMask;
        private readonly NativeSolidRectangle[] _rectangles =
            new NativeSolidRectangle[MaximumRectangles];
        private readonly NativeAnalyticPrimitive[] _analyticPrimitives =
            new NativeAnalyticPrimitive[MaximumRectangles];
        private readonly NativeGeometryPrimitive[] _geometryPrimitives =
            new NativeGeometryPrimitive[MaximumRectangles];
        private readonly Vector2[] _polylinePoints =
            new Vector2[MaximumRectangles * 4];
        private readonly NativePolyline[] _polylines =
            new NativePolyline[MaximumRectangles];
        private readonly double[] _dashIntervals =
            new double[MaximumRectangles * 3];
        private readonly NativeDashStyle[] _dashStyles =
            new NativeDashStyle[MaximumRectangles];
        private readonly Vector2[] _splineControlPoints =
            new Vector2[MaximumRectangles * 6];
        private readonly double[] _splineDoubles =
            new double[MaximumRectangles * 16];
        private readonly NativeSpline[] _splines =
            new NativeSpline[MaximumRectangles];
        private readonly NativePathFill[] _pathFills =
            new NativePathFill[MaximumRectangles];
        private readonly NativePathSegment[] _pathSegments =
            new NativePathSegment[4];
        private NativeGlyphOutline[] _glyphOutlines = [];
        private NativePathSegment[] _glyphSegments = [];
        private readonly NativePositionedGlyph[] _positionedGlyphs =
            new NativePositionedGlyph[MaximumRectangles];
        private readonly byte[] _imagePixels = new byte[192 * 128 * 4];
        private readonly byte[] _imageMaskPixels = new byte[192 * 128 * 4];
        private readonly byte[] _semanticScene =
            new byte[NativeRepresentativeSceneSample.RequiredBufferSize];
        private readonly NativeRendererInfo _info;
        private NativeTexturePreview? _preview;
        private Run? _countRun;
        private Run? _metricsRun;
        private int _rectangleCount = 384;
        private int _palette;
        private NativeBatchMode _mode = NativeBatchMode.RepresentativeScene;
        private uint _contentRevision;
        private uint _imageRevision;
        private ulong _semanticGeneration;
        private int _semanticSceneLength;
        private NativeSceneUpdateMetrics _semanticUpdateMetrics;
        private GpuPicture? _managedPicture;
        private NativeCompiledPicture? _compiledPicture;
        private double _managedPictureCompileMilliseconds;
        private bool _imageNeedsUpload;
        private bool _sceneDirty = true;
        private float _renderDpiScale = 1f;
        private int _disposeState;

        public NativeRendererSampleSession(
            WgpuContext context,
            DawnGpuContext? dawnContext)
        {
            _info = dawnContext is null
                ? NativeCompositor.GetInfo()
                : NativeDawnAdapter.GetInfo();
            _target = new GpuTexture(
                context,
                TargetWidth,
                TargetHeight,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment |
                TextureUsage.TextureBinding |
                TextureUsage.CopySrc,
                "Native C++ gallery render target",
                alphaMode: GpuTextureAlphaMode.Premultiplied);
            _externalImageSource = new GpuTexture(
                context,
                192,
                128,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.CopyDst,
                "Native C++ gallery same-device image source",
                alphaMode: GpuTextureAlphaMode.Straight);
            _externalImageMask = new GpuTexture(
                context,
                192,
                128,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.CopyDst,
                "Native C++ gallery same-device image mask",
                alphaMode: GpuTextureAlphaMode.Straight);
            _compositor = dawnContext is null
                ? new NativeCompositor(
                    context,
                    TextureFormat.Rgba8Unorm)
                : NativeDawnAdapter.CreateCompositor(
                    dawnContext,
                    TextureFormat.Rgba8Unorm);
            RenderFrame();
        }

        public FrameworkElement CreatePage()
        {
            var root = new Grid
            {
                Margin = new Thickness(14)
            };
            root.RowDefinitions.Add(GridLength.Auto);
            root.RowDefinitions.Add(GridLength.Auto);
            root.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

            var heading = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };
            heading.AddChild(CreateText(
                "Pure C++ WebGPU renderer",
                22f,
                bold: true));
            heading.AddChild(CreateText(
                $"{_info.Name}. One stable C ABI call records one GPU " +
                "submission. Cycle indexed analytic primitives, lines and " +
                "polygon geometry, capped GPU Bezier curves, connected " +
                "polylines with joins, dashed strokes, adaptive rational " +
                "splines, retained compute-rasterized paths, or the rectangle " +
                "fast path, plus positioned glyph runs rasterized into the " +
                "native-owned GPU atlas, retained RGBA image upload, or " +
                "same-device zero-copy image and opacity-mask sampling. " +
                "The default representative scene submits analytic, path, " +
                "positioned-text, cubic color-processed image, transform, " +
                "clip, rounded mask, and effect-chain work through one " +
                "immutable semantic stream. The managed-picture mode records " +
                "ordinary GpuPicture commands once, compiles them through the " +
                "standalone ProGPU.Scene.Native package, and replays the " +
                "retained C++ scene without per-frame managed allocation. " +
                "Stable geometry, " +
                "path, glyph, and image modes " +
                "reuse retained native CPU/GPU payloads. Every mode " +
                "reuses the production Vector.wgsl, GlyphRasterizer.wgsl, or " +
                "Text.wgsl modules.",
                12f));
            root.AddChild(heading);
            Grid.SetRow(heading, 0);

            var controls = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalSpacing = 10f,
                VerticalSpacing = 8f,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _countRun = new Run();
            var countLabel = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 12f,
                Width = 132f,
                Margin = new Thickness(0, 8, 0, 0)
            };
            countLabel.Inlines.Add(_countRun);
            UpdateCountText();
            controls.AddChild(countLabel);

            var countSlider = new Slider
            {
                Minimum = 1,
                Maximum = MaximumRectangles,
                Value = _rectangleCount,
                Width = 280f,
                SmallChange = 1,
                LargeChange = 128
            };
            countSlider.ValueChanged += (_, _) =>
            {
                _rectangleCount = Math.Clamp(
                    (int)Math.Round(countSlider.Value),
                    1,
                    MaximumRectangles);
                _sceneDirty = true;
                UpdateCountText();
            };
            controls.AddChild(countSlider);

            var renderButton = CreateButton("Render native batch", 170f);
            renderButton.Click += (_, _) => RenderFrame();
            controls.AddChild(renderButton);

            var modeButton = CreateButton("Toggle batch mode", 156f);
            modeButton.Click += (_, _) =>
            {
                _mode = (NativeBatchMode)(((int)_mode + 1) %
                    Enum.GetValues<NativeBatchMode>().Length);
                _sceneDirty = true;
                UpdateCountText();
                RenderFrame();
            };
            controls.AddChild(modeButton);

            var paletteButton = CreateButton("Cycle palette", 132f);
            paletteButton.Click += (_, _) =>
            {
                _palette = (_palette + 1) % 3;
                _sceneDirty = true;
                RenderFrame();
            };
            controls.AddChild(paletteButton);

            _metricsRun = new Run();
            var metrics = new RichTextBlock
            {
                Font = AppState._font,
                FontSize = 11f,
                Margin = new Thickness(0, 7, 0, 0)
            };
            metrics.Inlines.Add(_metricsRun);
            controls.AddChild(metrics);
            root.AddChild(controls);
            Grid.SetRow(controls, 1);

            _preview = new NativeTexturePreview(
                _target,
                UpdatePreviewSurface);
            var previewBorder = new Border
            {
                Background = new ThemeResourceBrush("ControlBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = 8f,
                Padding = new Thickness(8),
                Child = _preview
            };
            root.AddChild(previewBorder);
            Grid.SetRow(previewBorder, 2);

            // Publish the constructor's initial frame now that text runs exist.
            RenderFrame();
            return root;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }
            _managedPicture?.Dispose();
            _compositor.Dispose();
            _externalImageMask.Dispose();
            _externalImageSource.Dispose();
            _target.Dispose();
        }

        private void RenderFrame()
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            if (_sceneDirty)
            {
                switch (_mode)
                {
                    case NativeBatchMode.RepresentativeScene:
                        FillImage(_palette, writeExternalSource: false);
                        _semanticGeneration++;
                        if (_semanticGeneration == 0U)
                        {
                            _semanticGeneration = 1U;
                        }
                        _semanticSceneLength =
                            NativeRepresentativeSceneSample.Build(
                                _semanticScene,
                                _imagePixels,
                                _semanticGeneration,
                                _palette);
                        _semanticUpdateMetrics = _compositor.UpdateScene(
                            _semanticScene.AsSpan(0, _semanticSceneLength));
                        break;
                    case NativeBatchMode.ManagedPicture:
                        BuildAndCompileManagedPicture();
                        break;
                    case NativeBatchMode.Analytic:
                        FillAnalyticPrimitives(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Geometry:
                        FillGeometryPrimitives(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Curves:
                        FillCurvePrimitives(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Polylines:
                        FillPolylines(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Dashes:
                        FillDashedPolylines(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Splines:
                        FillSplines(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Paths:
                        FillPaths(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Glyphs:
                        FillGlyphs(_rectangleCount, _palette);
                        break;
                    case NativeBatchMode.Images:
                        FillImage(_palette, writeExternalSource: false);
                        break;
                    case NativeBatchMode.ExternalImages:
                        FillImage(_palette, writeExternalSource: true);
                        break;
                    case NativeBatchMode.MaskedImages:
                        FillImage(
                            _palette,
                            writeExternalSource: true,
                            writeExternalMask: true);
                        break;
                    default:
                        FillRectangles(_rectangleCount, _palette);
                        break;
                }
                _contentRevision++;
                if (_contentRevision == 0U)
                {
                    _contentRevision = 1U;
                }
                _sceneDirty = false;
            }
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long timestamp = Stopwatch.GetTimestamp();
            uint drawCallCount;
            uint vertexCount;
            ulong uploadBytes;
            if (_mode is NativeBatchMode.RepresentativeScene or
                NativeBatchMode.ManagedPicture)
            {
                ulong sceneId = _mode == NativeBatchMode.RepresentativeScene
                    ? NativeRepresentativeSceneSample.SceneId
                    : NativeRepresentativeSceneSample.SceneId + 1U;
                NativeSceneFrameMetrics metrics = _compositor.RenderScene(
                    _target,
                    dpiScale: _renderDpiScale,
                    sceneId,
                    _semanticGeneration,
                    new Vector4(0.015f, 0.02f, 0.035f, 1f));
                drawCallCount = metrics.DrawCallCount;
                vertexCount = 0;
                uploadBytes = metrics.VertexUploadBytes +
                    metrics.IndexUploadBytes +
                    metrics.TextureUploadBytes +
                    metrics.UniformUploadBytes +
                    metrics.CoverageStagingBytes +
                    metrics.BrushUploadBytes +
                    metrics.GradientStopUploadBytes +
                    metrics.TextStyleUploadBytes +
                    metrics.ColorGlyphUploadBytes;
            }
            else if (_mode == NativeBatchMode.Analytic)
            {
                NativeAnalyticFrameMetrics metrics = _compositor.RenderAnalytic(
                    _target,
                    dpiScale: _renderDpiScale,
                    _analyticPrimitives.AsSpan(0, _rectangleCount),
                    new Vector4(0.015f, 0.02f, 0.035f, 1f));
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.VertexUploadBytes + metrics.IndexUploadBytes;
            }
            else if (_mode is NativeBatchMode.Geometry or NativeBatchMode.Curves)
            {
                NativeGeometryFrameMetrics metrics = _compositor.RenderGeometry(
                    _target,
                    dpiScale: _renderDpiScale,
                    _geometryPrimitives.AsSpan(0, _rectangleCount),
                    new Vector4(0.015f, 0.02f, 0.035f, 1f),
                    contentRevision: _contentRevision);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.VertexUploadBytes +
                    metrics.IndexUploadBytes + metrics.BrushUploadBytes;
            }
            else if (_mode is NativeBatchMode.Polylines or NativeBatchMode.Dashes)
            {
                NativeGeometryFrameMetrics metrics = _mode == NativeBatchMode.Dashes
                    ? _compositor.RenderGeometry(
                        _target,
                        dpiScale: _renderDpiScale,
                        ReadOnlySpan<NativeGeometryPrimitive>.Empty,
                        _polylinePoints.AsSpan(0, _rectangleCount * 4),
                        _polylines.AsSpan(0, _rectangleCount),
                        _dashIntervals.AsSpan(0, _rectangleCount * 3),
                        _dashStyles.AsSpan(0, _rectangleCount),
                        ReadOnlySpan<NativeSpline>.Empty,
                        new Vector4(0.015f, 0.02f, 0.035f, 1f),
                        contentRevision: _contentRevision)
                    : _compositor.RenderGeometry(
                        _target,
                        dpiScale: _renderDpiScale,
                        ReadOnlySpan<NativeGeometryPrimitive>.Empty,
                        _polylinePoints.AsSpan(0, _rectangleCount * 4),
                        _polylines.AsSpan(0, _rectangleCount),
                        new Vector4(0.015f, 0.02f, 0.035f, 1f),
                        contentRevision: _contentRevision);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.VertexUploadBytes +
                    metrics.IndexUploadBytes + metrics.BrushUploadBytes;
            }
            else if (_mode == NativeBatchMode.Splines)
            {
                NativeGeometryFrameMetrics metrics = _compositor.RenderGeometry(
                    _target,
                    dpiScale: _renderDpiScale,
                    ReadOnlySpan<NativeGeometryPrimitive>.Empty,
                    _splineControlPoints.AsSpan(0, _rectangleCount * 6),
                    ReadOnlySpan<NativePolyline>.Empty,
                    _splineDoubles.AsSpan(0, _rectangleCount * 16),
                    _splines.AsSpan(0, _rectangleCount),
                    new Vector4(0.015f, 0.02f, 0.035f, 1f),
                    contentRevision: _contentRevision);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.VertexUploadBytes +
                    metrics.IndexUploadBytes + metrics.BrushUploadBytes;
            }
            else if (_mode == NativeBatchMode.Paths)
            {
                NativePathFrameMetrics metrics = _compositor.RenderPaths(
                    _target,
                    dpiScale: _renderDpiScale,
                    _pathFills.AsSpan(0, _rectangleCount),
                    _pathSegments,
                    new Vector4(0.015f, 0.02f, 0.035f, 1f),
                    contentRevision: _contentRevision);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.VertexUploadBytes +
                    metrics.IndexUploadBytes + metrics.BrushUploadBytes +
                    metrics.PathUploadBytes;
            }
            else if (_mode == NativeBatchMode.Glyphs)
            {
                NativeGlyphFrameMetrics metrics = _compositor.RenderGlyphs(
                    _target,
                    dpiScale: _renderDpiScale,
                    _glyphOutlines,
                    _glyphSegments,
                    _positionedGlyphs.AsSpan(0, _rectangleCount),
                    new Vector4(0.015f, 0.02f, 0.035f, 1f),
                    contentRevision: _contentRevision);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.GlyphCount * 6U;
                uploadBytes = metrics.InstanceUploadBytes +
                    metrics.OutlineUploadBytes;
            }
            else if (_mode == NativeBatchMode.Images)
            {
                NativeImageFrameMetrics metrics = _compositor.RenderImage(
                    _target,
                    dpiScale: _renderDpiScale,
                    _imageNeedsUpload
                        ? _imagePixels
                        : ReadOnlySpan<byte>.Empty,
                    imageWidth: 192,
                    imageHeight: 128,
                    rowBytes: 192 * 4,
                    new NativeImageRect(0f, 0f, 192f, 128f),
                    new NativeImageRect(80f, 60f, 800f, 420f),
                    Matrix3x2.Identity,
                    opacity: 1f,
                    NativeImageSampling.Nearest,
                    new Vector4(0.015f, 0.02f, 0.035f, 1f),
                    _imageRevision,
                    _contentRevision,
                    drawState: default,
                    maxAnisotropy: 1);
                _imageNeedsUpload = false;
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.TextureUploadBytes +
                    metrics.VertexUploadBytes + metrics.IndexUploadBytes;
            }
            else if (_mode == NativeBatchMode.ExternalImages)
            {
                NativeImageFrameMetrics metrics = _compositor.RenderExternalImage(
                    _target,
                    _externalImageSource,
                    dpiScale: _renderDpiScale,
                    new NativeImageRect(0f, 0f, 192f, 128f),
                    new NativeImageRect(80f, 60f, 800f, 420f),
                    Matrix3x2.Identity,
                    opacity: 1f,
                    NativeImageSampling.Nearest,
                    new Vector4(0.015f, 0.02f, 0.035f, 1f),
                    _imageRevision,
                    _contentRevision,
                    drawState: default,
                    maxAnisotropy: 1);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.TextureUploadBytes +
                    metrics.VertexUploadBytes + metrics.IndexUploadBytes;
            }
            else if (_mode == NativeBatchMode.MaskedImages)
            {
                NativeImageFrameMetrics metrics =
                    _compositor.RenderMaskedExternalImage(
                        _target,
                        _externalImageSource,
                        _externalImageMask,
                        dpiScale: _renderDpiScale,
                        new NativeImageRect(0f, 0f, 192f, 128f),
                        new NativeImageRect(80f, 60f, 800f, 420f),
                        new NativeImageRect(80f, 60f, 800f, 420f),
                        Matrix3x2.Identity,
                        opacity: 1f,
                        NativeImageSampling.Nearest,
                        NativeImageSampling.Linear,
                        new Vector4(0.015f, 0.02f, 0.035f, 1f),
                        _imageRevision,
                        _imageRevision,
                        _contentRevision,
                        drawState: default,
                        maxAnisotropy: 1);
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.TextureUploadBytes +
                    metrics.VertexUploadBytes + metrics.IndexUploadBytes +
                    metrics.UniformUploadBytes;
            }
            else
            {
                NativeFrameMetrics metrics = _compositor.Render(
                    _target,
                    dpiScale: _renderDpiScale,
                    _rectangles.AsSpan(0, _rectangleCount),
                    new Vector4(0.015f, 0.02f, 0.035f, 1f));
                drawCallCount = metrics.DrawCallCount;
                vertexCount = metrics.VertexCount;
                uploadBytes = metrics.VertexUploadBytes;
            }
            double elapsedMilliseconds =
                Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            long managedBytes =
                GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            if (_metricsRun is not null)
            {
                _metricsRun.Text =
                    $"C ABI + submit {elapsedMilliseconds:F3} ms · " +
                    $"managed alloc {managedBytes} B · " +
                    $"draws {drawCallCount} · " +
                    $"vertices {vertexCount:N0} · " +
                    $"upload {uploadBytes:N0} B · " +
                    $"target {_target.Width}×{_target.Height} " +
                    $"@ {_renderDpiScale:F2}x" +
                    (_mode is NativeBatchMode.RepresentativeScene or
                        NativeBatchMode.ManagedPicture
                        ? $" · scene {_semanticUpdateMetrics.CommandCount}/" +
                          $"{_semanticUpdateMetrics.ResourceCount}" +
                          (_mode == NativeBatchMode.ManagedPicture
                              ? $" · compile {_managedPictureCompileMilliseconds:F3} ms"
                              : string.Empty)
                        : string.Empty);
            }
            _preview?.Invalidate();
        }

        private void UpdatePreviewSurface(Vector2 availableSize)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            float displayScale = ResolveDisplayScale();
            NativePreviewSurfaceLayout layout = CalculatePreviewSurfaceLayout(
                availableSize,
                displayScale);
            bool targetChanged =
                _target.Width != layout.PixelWidth ||
                _target.Height != layout.PixelHeight;
            bool scaleChanged = _renderDpiScale != layout.RenderDpiScale;

            _preview?.SetDestination(layout.Destination);
            if (!targetChanged && !scaleChanged)
            {
                return;
            }

            _target.Resize(layout.PixelWidth, layout.PixelHeight);
            _renderDpiScale = layout.RenderDpiScale;
            RenderFrame();
        }

        private static NativePreviewSurfaceLayout CalculatePreviewSurfaceLayout(
            Vector2 availableSize,
            float displayScale)
        {
            float safeDisplayScale = float.IsFinite(displayScale) && displayScale > 0f
                ? displayScale
                : 1f;
            float availableWidth = float.IsFinite(availableSize.X)
                ? Math.Max(1f, availableSize.X)
                : TargetWidth;
            float availableHeight = float.IsFinite(availableSize.Y)
                ? Math.Max(1f, availableSize.Y)
                : TargetHeight;
            float logicalFitScale = Math.Min(
                availableWidth / TargetWidth,
                availableHeight / TargetHeight);
            float requestedRenderScale = Math.Max(
                1f / Math.Max(TargetWidth, TargetHeight),
                logicalFitScale * safeDisplayScale);
            requestedRenderScale = Math.Min(
                requestedRenderScale,
                MaximumTargetDimension /
                    (float)Math.Max(TargetWidth, TargetHeight));
            uint pixelWidth = Math.Clamp(
                (uint)Math.Max(1d, Math.Round(
                    TargetWidth * requestedRenderScale,
                    MidpointRounding.ToEven)),
                1U,
                MaximumTargetDimension);
            uint pixelHeight = Math.Clamp(
                (uint)Math.Max(1d, Math.Round(
                    TargetHeight * requestedRenderScale,
                    MidpointRounding.ToEven)),
                1U,
                MaximumTargetDimension);
            float renderDpiScale = Math.Min(
                pixelWidth / (float)TargetWidth,
                pixelHeight / (float)TargetHeight);
            float destinationWidth = pixelWidth / safeDisplayScale;
            float destinationHeight = pixelHeight / safeDisplayScale;
            var destination = new Rect(
                Math.Max(0f, (availableWidth - destinationWidth) * 0.5f),
                Math.Max(0f, (availableHeight - destinationHeight) * 0.5f),
                destinationWidth,
                destinationHeight);
            return new NativePreviewSurfaceLayout(
                pixelWidth,
                pixelHeight,
                renderDpiScale,
                destination);
        }

        private static float ResolveDisplayScale()
        {
            double scale = DisplayScaleResolver.ResolveWindowDisplayScale(
                AppState._window);
            return double.IsFinite(scale) && scale > 0d
                ? (float)scale
                : 1f;
        }

        private readonly record struct NativePreviewSurfaceLayout(
            uint PixelWidth,
            uint PixelHeight,
            float RenderDpiScale,
            Rect Destination);

        private void BuildAndCompileManagedPicture()
        {
            _managedPicture?.Dispose();
            _managedPicture = RecordManagedPicture(_rectangleCount, _palette);
            _semanticGeneration++;
            if (_semanticGeneration == 0U)
            {
                _semanticGeneration = 1U;
            }

            long timestamp = Stopwatch.GetTimestamp();
            if (!GpuPictureNativeSceneCompiler.TryCompile(
                    _managedPicture,
                    NativeRepresentativeSceneSample.SceneId + 1U,
                    _semanticGeneration,
                    out _compiledPicture,
                    out NativePictureCompileFailure failure) ||
                _compiledPicture is null)
            {
                throw new InvalidOperationException(
                    $"Managed picture could not be lowered to the native scene: {failure}.");
            }
            _managedPictureCompileMilliseconds =
                Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            _semanticUpdateMetrics = _compositor.UpdateScene(_compiledPicture.Stream);
            _semanticSceneLength = _compiledPicture.Length;
        }

        private static GpuPicture RecordManagedPicture(int count, int palette)
        {
            var recorder = new GpuPictureRecorder();
            DrawingContext drawing = recorder.BeginRecording(
                new Rect(0f, 0f, TargetWidth, TargetHeight));
            var brushes = new SolidColorBrush[3]
            {
                new(Palette(0.08f, palette)),
                new(Palette(0.42f, palette)),
                new(Palette(0.76f, palette))
            };
            var pens = new Pen[3]
            {
                new(brushes[0], 2f),
                new(brushes[1], 2.5f),
                new(brushes[2], 3f)
            };

            const float inset = 18f;
            const float gap = 3f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;
            int analyticCount = (count + 1) / 2;

            // Consecutive families let the compiler lower this managed display
            // list to two retained native draw batches.
            for (int index = 0; index < analyticCount; index++)
            {
                int column = index % columns;
                int row = index / columns;
                var rect = new Rect(
                    inset + column * cellWidth + gap,
                    inset + row * cellHeight + gap,
                    MathF.Max(1f, cellWidth - gap * 2f),
                    MathF.Max(1f, cellHeight - gap * 2f));
                if ((index & 1) == 0)
                {
                    drawing.DrawRoundedRectangle(
                        brushes[index % brushes.Length],
                        null,
                        rect,
                        MathF.Min(5f, MathF.Min(rect.Width, rect.Height) * 0.25f));
                }
                else
                {
                    drawing.DrawEllipse(
                        brushes[index % brushes.Length],
                        null,
                        new Vector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f),
                        rect.Width * 0.5f,
                        rect.Height * 0.5f);
                }
            }

            for (int index = analyticCount; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float left = inset + column * cellWidth + gap;
                float top = inset + row * cellHeight + gap;
                float right = left + MathF.Max(1f, cellWidth - gap * 2f);
                float bottom = top + MathF.Max(1f, cellHeight - gap * 2f);
                if ((index & 1) == 0)
                {
                    drawing.DrawLine(
                        pens[index % pens.Length],
                        new Vector2(left, bottom),
                        new Vector2(right, top));
                }
                else
                {
                    drawing.FillTriangle(
                        brushes[index % brushes.Length],
                        new Vector2((left + right) * 0.5f, top),
                        new Vector2(right, bottom),
                        new Vector2(left, bottom));
                }
            }
            return recorder.EndRecording();
        }

        private void FillRectangles(int count, int palette)
        {
            const float inset = 18f;
            const float gap = 3f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;

            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                Vector4 color = Palette(phase, palette);
                _rectangles[index] = new NativeSolidRectangle(
                    inset + column * cellWidth + gap * 0.5f,
                    inset + row * cellHeight + gap * 0.5f,
                    Math.Max(1f, cellWidth - gap),
                    Math.Max(1f, cellHeight - gap),
                    color);
            }
        }

        private void FillAnalyticPrimitives(int count, int palette)
        {
            const float inset = 24f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;

            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                float itemWidth = Math.Max(2f, cellWidth * 0.64f);
                float itemHeight = Math.Max(2f, cellHeight * 0.58f);
                float centerX = inset + (column + 0.5f) * cellWidth;
                float centerY = inset + (row + 0.5f) * cellHeight;
                Vector4 color = Palette(phase, palette);
                color.W = 0.68f + 0.3f * Wave(phase + 0.17f);
                Matrix3x2 transform =
                    Matrix3x2.CreateScale(
                        0.82f + 0.32f * Wave(phase + 0.21f),
                        0.78f + 0.38f * Wave(phase + 0.49f)) *
                    Matrix3x2.CreateSkew(
                        (Wave(phase + 0.77f) - 0.5f) * 0.18f,
                        0f) *
                    Matrix3x2.CreateRotation(
                        (Wave(phase + 0.91f) - 0.5f) * 0.28f) *
                    Matrix3x2.CreateTranslation(centerX, centerY);
                _analyticPrimitives[index] = new NativeAnalyticPrimitive(
                    (NativeAnalyticPrimitiveKind)(index % 3),
                    -itemWidth * 0.5f,
                    -itemHeight * 0.5f,
                    itemWidth,
                    itemHeight,
                    color,
                    transform,
                    cornerRadius: Math.Min(itemWidth, itemHeight) * 0.22f,
                    strokeThickness: (index & 1) == 0 ? 0f : 1f + index % 4);
            }
        }

        private void FillGeometryPrimitives(int count, int palette)
        {
            const float inset = 24f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;

            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                float itemWidth = Math.Max(2f, cellWidth * 0.58f);
                float itemHeight = Math.Max(2f, cellHeight * 0.52f);
                Vector4 color = Palette(phase, palette);
                color.W = 0.68f + 0.3f * Wave(phase + 0.17f);
                Matrix3x2 transform =
                    Matrix3x2.CreateScale(
                        0.82f + 0.36f * Wave(phase + 0.21f),
                        0.76f + 0.48f * Wave(phase + 0.49f)) *
                    Matrix3x2.CreateSkew(
                        (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                        (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
                    Matrix3x2.CreateRotation(
                        (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
                    Matrix3x2.CreateTranslation(
                        inset + (column + 0.5f) * cellWidth,
                        inset + (row + 0.5f) * cellHeight);
                switch (index % 3)
                {
                    case 0:
                        NativeGeometryPrimitiveFlags flags = (index % 9) switch
                        {
                            0 => NativeGeometryPrimitiveFlags.Hairline,
                            3 => NativeGeometryPrimitiveFlags.FixedDeviceStroke,
                            _ => NativeGeometryPrimitiveFlags.None
                        };
                        _geometryPrimitives[index] = new NativeGeometryPrimitive(
                            NativeGeometryPrimitiveKind.Line,
                            new Vector2(-itemWidth * 0.5f, -itemHeight * 0.22f),
                            new Vector2(itemWidth * 0.5f, itemHeight * 0.22f),
                            color,
                            transform,
                            strokeThickness: flags == NativeGeometryPrimitiveFlags.Hairline
                                ? 0f
                                : 1f + index % 4,
                            flags: flags,
                            startCap: (NativeStrokeCap)(index % 4),
                            endCap: (NativeStrokeCap)((index + 2) % 4));
                        break;
                    case 1:
                        _geometryPrimitives[index] = new NativeGeometryPrimitive(
                            NativeGeometryPrimitiveKind.Triangle,
                            new Vector2(-itemWidth * 0.5f, itemHeight * 0.45f),
                            new Vector2(0f, -itemHeight * 0.5f),
                            color,
                            transform,
                            p2: new Vector2(itemWidth * 0.5f, itemHeight * 0.45f));
                        break;
                    default:
                        _geometryPrimitives[index] = new NativeGeometryPrimitive(
                            NativeGeometryPrimitiveKind.Quadrilateral,
                            new Vector2(-itemWidth * 0.5f, -itemHeight * 0.35f),
                            new Vector2(itemWidth * 0.35f, -itemHeight * 0.5f),
                            color,
                            transform,
                            p2: new Vector2(itemWidth * 0.5f, itemHeight * 0.35f),
                            p3: new Vector2(-itemWidth * 0.35f, itemHeight * 0.5f));
                        break;
                }
            }
        }

        private void FillCurvePrimitives(int count, int palette)
        {
            const float inset = 24f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;
            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                float itemWidth = Math.Max(2f, cellWidth * 0.72f);
                float itemHeight = Math.Max(2f, cellHeight * 0.68f);
                Vector4 color = Palette(phase, palette);
                color.W = 0.72f + 0.26f * Wave(phase + 0.17f);
                Matrix3x2 transform =
                    Matrix3x2.CreateScale(
                        0.82f + 0.36f * Wave(phase + 0.21f),
                        0.76f + 0.48f * Wave(phase + 0.49f)) *
                    Matrix3x2.CreateSkew(
                        (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                        (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
                    Matrix3x2.CreateRotation(
                        (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
                    Matrix3x2.CreateTranslation(
                        inset + (column + 0.5f) * cellWidth,
                        inset + (row + 0.5f) * cellHeight);
                NativeGeometryPrimitiveFlags flags = (index % 3) switch
                {
                    0 => NativeGeometryPrimitiveFlags.Hairline,
                    1 => NativeGeometryPrimitiveFlags.FixedDeviceStroke,
                    _ => NativeGeometryPrimitiveFlags.None
                };
                _geometryPrimitives[index] = new NativeGeometryPrimitive(
                    (index & 1) == 0
                        ? NativeGeometryPrimitiveKind.QuadraticBezier
                        : NativeGeometryPrimitiveKind.CubicBezier,
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.22f),
                    new Vector2(-itemWidth * 0.18f, -itemHeight * 0.62f),
                    color,
                    transform,
                    p2: new Vector2(itemWidth * 0.18f, itemHeight * 0.58f),
                    p3: new Vector2(itemWidth * 0.5f, -itemHeight * 0.18f),
                    strokeThickness: flags == NativeGeometryPrimitiveFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    flags: flags,
                    startCap: (NativeStrokeCap)(index % 4),
                    endCap: (NativeStrokeCap)((index + 2) % 4));
            }
        }

        private void FillPolylines(int count, int palette)
        {
            const float inset = 24f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;
            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                float itemWidth = Math.Max(3f, cellWidth * 0.62f);
                float itemHeight = Math.Max(3f, cellHeight * 0.58f);
                int pointOffset = index * 4;
                _polylinePoints[pointOffset] =
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.22f);
                _polylinePoints[pointOffset + 1] =
                    new Vector2(-itemWidth * 0.18f, -itemHeight * 0.46f);
                _polylinePoints[pointOffset + 2] =
                    new Vector2(itemWidth * 0.16f, itemHeight * 0.44f);
                _polylinePoints[pointOffset + 3] =
                    new Vector2(itemWidth * 0.5f, -itemHeight * 0.18f);
                Matrix3x2 transform =
                    Matrix3x2.CreateScale(
                        0.8f + 0.4f * Wave(phase + 0.21f),
                        0.72f + 0.56f * Wave(phase + 0.49f)) *
                    Matrix3x2.CreateSkew(
                        (Wave(phase + 0.77f) - 0.5f) * 0.28f,
                        (Wave(phase + 0.43f) - 0.5f) * 0.14f) *
                    Matrix3x2.CreateRotation(
                        (Wave(phase + 0.91f) - 0.5f) * 0.36f) *
                    Matrix3x2.CreateTranslation(
                        inset + (column + 0.5f) * cellWidth,
                        inset + (row + 0.5f) * cellHeight);
                NativePolylineFlags flags = (index % 3) switch
                {
                    0 => NativePolylineFlags.Hairline,
                    1 => NativePolylineFlags.FixedDeviceStroke,
                    _ => NativePolylineFlags.None
                };
                Vector4 color = Palette(phase, palette);
                color.W = 0.72f + 0.26f * Wave(phase + 0.17f);
                _polylines[index] = new NativePolyline(
                    (nuint)pointOffset,
                    4,
                    color,
                    transform,
                    flags == NativePolylineFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    miterLimit: 2f + index % 5,
                    flags: flags,
                    startCap: (NativeStrokeCap)(index % 4),
                    endCap: (NativeStrokeCap)((index + 2) % 4),
                    lineJoin: (NativeStrokeJoin)(index % 3),
                    isClosed: index % 4 == 3);
            }
        }

        private void FillDashedPolylines(int count, int palette)
        {
            FillPolylines(count, palette);
            for (int index = 0; index < count; index++)
            {
                int intervalOffset = index * 3;
                _dashIntervals[intervalOffset] = 1.75;
                _dashIntervals[intervalOffset + 1] = 0.9;
                _dashIntervals[intervalOffset + 2] = 0.45;
                _dashStyles[index] = new NativeDashStyle(
                    (nuint)intervalOffset,
                    intervalCount: 3,
                    offset: -0.35,
                    cap: NativeStrokeCap.Round);

                NativePolyline source = _polylines[index];
                NativePolylineFlags strokeMode = source.Flags &
                    (NativePolylineFlags.EdgeAliased |
                     NativePolylineFlags.Hairline |
                     NativePolylineFlags.FixedDeviceStroke);
                _polylines[index] = new NativePolyline(
                    source.PointOffset,
                    source.PointCount,
                    source.Color,
                    source.Transform,
                    source.StrokeThickness,
                    source.MiterLimit,
                    strokeMode,
                    source.StartCap,
                    source.EndCap,
                    source.LineJoin,
                    source.IsClosed,
                    dashStyle: (uint)index + 1U);
            }
        }

        private void FillSplines(int count, int palette)
        {
            const int controlPointsPerSpline = 6;
            const int knotsPerSpline = 10;
            const int doublesPerSpline = 16;
            ReadOnlySpan<double> knots = [0, 0, 0, 0, 1, 2, 3, 3, 3, 3];
            const float inset = 24f;
            float usableWidth = TargetWidth - inset * 2f;
            float usableHeight = TargetHeight - inset * 2f;
            int columns = Math.Max(
                1,
                (int)MathF.Ceiling(MathF.Sqrt(
                    count * usableWidth / usableHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = usableWidth / columns;
            float cellHeight = usableHeight / rows;
            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                float phase = (index * 0.61803398875f + palette * 0.23f) % 1f;
                float itemWidth = Math.Max(4f, cellWidth * 0.72f);
                float itemHeight = Math.Max(4f, cellHeight * 0.68f);
                int pointOffset = index * controlPointsPerSpline;
                _splineControlPoints[pointOffset] =
                    new Vector2(-itemWidth * 0.5f, itemHeight * 0.12f);
                _splineControlPoints[pointOffset + 1] =
                    new Vector2(-itemWidth * 0.34f, -itemHeight * 0.5f);
                _splineControlPoints[pointOffset + 2] =
                    new Vector2(-itemWidth * 0.1f, itemHeight * 0.48f);
                _splineControlPoints[pointOffset + 3] =
                    new Vector2(itemWidth * 0.12f, -itemHeight * 0.46f);
                _splineControlPoints[pointOffset + 4] =
                    new Vector2(itemWidth * 0.34f, itemHeight * 0.5f);
                _splineControlPoints[pointOffset + 5] =
                    new Vector2(itemWidth * 0.5f, -itemHeight * 0.1f);
                int doubleOffset = index * doublesPerSpline;
                knots.CopyTo(_splineDoubles.AsSpan(
                    doubleOffset,
                    knotsPerSpline));
                for (int weight = 0; weight < controlPointsPerSpline; weight++)
                {
                    _splineDoubles[doubleOffset + knotsPerSpline + weight] =
                        0.78 + 0.44 * Wave(phase + weight * 0.137f);
                }
                Matrix3x2 transform =
                    Matrix3x2.CreateScale(
                        0.82f + 0.38f * Wave(phase + 0.21f),
                        0.74f + 0.52f * Wave(phase + 0.49f)) *
                    Matrix3x2.CreateSkew(
                        (Wave(phase + 0.77f) - 0.5f) * 0.24f,
                        (Wave(phase + 0.43f) - 0.5f) * 0.12f) *
                    Matrix3x2.CreateRotation(
                        (Wave(phase + 0.91f) - 0.5f) * 0.32f) *
                    Matrix3x2.CreateTranslation(
                        inset + (column + 0.5f) * cellWidth,
                        inset + (row + 0.5f) * cellHeight);
                NativePolylineFlags flags = (index % 3) switch
                {
                    0 => NativePolylineFlags.Hairline,
                    1 => NativePolylineFlags.FixedDeviceStroke,
                    _ => NativePolylineFlags.None
                };
                Vector4 color = Palette(phase, palette);
                color.W = 0.72f + 0.26f * Wave(phase + 0.17f);
                var stroke = new NativePolyline(
                    (nuint)pointOffset,
                    controlPointsPerSpline,
                    color,
                    transform,
                    flags == NativePolylineFlags.Hairline
                        ? 0f
                        : 1f + index % 4,
                    miterLimit: 2f + index % 5,
                    flags: flags,
                    startCap: (NativeStrokeCap)(index % 4),
                    endCap: (NativeStrokeCap)((index + 2) % 4),
                    lineJoin: (NativeStrokeJoin)(index % 3),
                    isClosed: index % 4 == 3);
                _splines[index] = new NativeSpline(
                    stroke,
                    (nuint)doubleOffset,
                    knotsPerSpline,
                    degree: 3,
                    weightOffset: (nuint)(doubleOffset + knotsPerSpline),
                    weightCount: controlPointsPerSpline);
            }
        }

        private static float Wave(float phase) =>
            0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);

        private static Vector4 Palette(float phase, int palette)
        {
            float wave0 = 0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
            float wave1 = 0.5f + 0.5f * MathF.Sin((phase + 0.333f) * MathF.Tau);
            float wave2 = 0.5f + 0.5f * MathF.Sin((phase + 0.666f) * MathF.Tau);
            return palette switch
            {
                1 => new Vector4(0.25f + 0.7f * wave2, 0.12f + 0.45f * wave0, 0.35f + 0.6f * wave1, 1f),
                2 => new Vector4(0.08f + 0.38f * wave1, 0.38f + 0.58f * wave2, 0.42f + 0.5f * wave0, 1f),
                _ => new Vector4(0.12f + 0.45f * wave0, 0.3f + 0.62f * wave1, 0.45f + 0.5f * wave2, 1f)
            };
        }

        private void FillPaths(int count, int palette)
        {
            const float radius = 12f;
            const float kappa = 0.55228475f;
            _pathSegments[0] = new NativePathSegment(
                NativePathSegmentKind.Cubic,
                new Vector2(0f, -radius),
                new Vector2(radius * kappa, -radius),
                new Vector2(radius, -radius * kappa),
                new Vector2(radius, 0f));
            _pathSegments[1] = new NativePathSegment(
                NativePathSegmentKind.Cubic,
                new Vector2(radius, 0f),
                new Vector2(radius, radius * kappa),
                new Vector2(radius * kappa, radius),
                new Vector2(0f, radius));
            _pathSegments[2] = new NativePathSegment(
                NativePathSegmentKind.Cubic,
                new Vector2(0f, radius),
                new Vector2(-radius * kappa, radius),
                new Vector2(-radius, radius * kappa),
                new Vector2(-radius, 0f));
            _pathSegments[3] = new NativePathSegment(
                NativePathSegmentKind.Cubic,
                new Vector2(-radius, 0f),
                new Vector2(-radius, -radius * kappa),
                new Vector2(-radius * kappa, -radius),
                new Vector2(0f, -radius));

            int columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(
                count * TargetWidth / (float)TargetHeight)));
            int rows = (count + columns - 1) / columns;
            float cellWidth = TargetWidth / (float)columns;
            float cellHeight = TargetHeight / (float)rows;
            float scale = MathF.Min(cellWidth, cellHeight) / (radius * 2.8f);
            for (int index = 0; index < count; index++)
            {
                int column = index % columns;
                int row = index / columns;
                Matrix3x2 transform = Matrix3x2.CreateScale(scale) *
                    Matrix3x2.CreateTranslation(
                        (column + 0.5f) * cellWidth,
                        (row + 0.5f) * cellHeight);
                _pathFills[index] = new NativePathFill(
                    0,
                    (nuint)_pathSegments.Length,
                    new Vector2(-radius),
                    new Vector2(radius),
                    Palette(index * 0.61803398875f % 1f, palette),
                    transform);
            }
        }

        private void FillGlyphs(int count, int palette)
        {
            const float fontSize = 28f;
            const string alphabet =
                "ProGPUWebNative0123456789ABCDEFGHJKLMNQRSTUVXYZ";
            TtfFont font = AppState._font ?? throw new InvalidOperationException(
                "The native glyph sample requires the gallery font.");
            float rasterScale = fontSize / font.UnitsPerEm;
            int columns = Math.Max(1, (int)(TargetWidth / 44f));
            int rows = Math.Max(1, (count + columns - 1) / columns);
            float cellHeight = Math.Min(42f, TargetHeight / (float)rows);
            var outlines = new List<NativeGlyphOutline>();
            var segments = new List<NativePathSegment>();
            var outlineIndices = new Dictionary<ushort, uint>();

            for (int index = 0; index < count; index++)
            {
                ushort glyphIndex = font.GetGlyphIndex(
                    alphabet[index % alphabet.Length]);
                if (!outlineIndices.TryGetValue(
                        glyphIndex,
                        out uint outlineIndex))
                {
                    PathGeometry? outline = font.GetGlyphOutline(glyphIndex);
                    if (outline == null ||
                        !outline.TryGetBounds(
                            out Vector2 minimum,
                            out Vector2 maximum))
                    {
                        throw new InvalidOperationException(
                            $"Glyph {glyphIndex} has no renderable outline.");
                    }
                    nuint segmentOffset = (nuint)segments.Count;
                    foreach (PathFigure figure in outline.Figures)
                    {
                        Vector2 current = figure.StartPoint;
                        foreach (PathSegment segment in figure.Segments)
                        {
                            switch (segment)
                            {
                                case LineSegment line:
                                    segments.Add(new NativePathSegment(
                                        NativePathSegmentKind.Line,
                                        current,
                                        line.Point));
                                    current = line.Point;
                                    break;
                                case QuadraticBezierSegment quadratic:
                                    segments.Add(new NativePathSegment(
                                        NativePathSegmentKind.Quadratic,
                                        current,
                                        quadratic.ControlPoint,
                                        quadratic.Point));
                                    current = quadratic.Point;
                                    break;
                                case CubicBezierSegment cubic:
                                    segments.Add(new NativePathSegment(
                                        NativePathSegmentKind.Cubic,
                                        current,
                                        cubic.ControlPoint1,
                                        cubic.ControlPoint2,
                                        cubic.Point));
                                    current = cubic.Point;
                                    break;
                            }
                        }
                        if (figure.IsClosed && current != figure.StartPoint)
                        {
                            segments.Add(new NativePathSegment(
                                NativePathSegmentKind.Line,
                                current,
                                figure.StartPoint));
                        }
                    }
                    outlineIndex = checked((uint)outlines.Count);
                    outlineIndices.Add(glyphIndex, outlineIndex);
                    outlines.Add(new NativeGlyphOutline(
                        segmentOffset,
                        (nuint)segments.Count - segmentOffset,
                        minimum,
                        maximum,
                        rasterScale));
                }

                int column = index % columns;
                int row = index / columns;
                _positionedGlyphs[index] = new NativePositionedGlyph(
                    outlineIndex,
                    new Vector2(
                        18f + column * 44f,
                        Math.Min(TargetHeight - 10f, 34f + row * cellHeight)),
                    Vector2.UnitX,
                    Vector2.UnitY,
                    Palette(index * 0.61803398875f % 1f, palette));
            }
            _glyphOutlines = outlines.ToArray();
            _glyphSegments = segments.ToArray();
        }

        private void FillImage(
            int palette,
            bool writeExternalSource,
            bool writeExternalMask = false)
        {
            const int imageWidth = 192;
            const int imageHeight = 128;
            for (int y = 0; y < imageHeight; y++)
            {
                for (int x = 0; x < imageWidth; x++)
                {
                    int offset = (y * imageWidth + x) * 4;
                    bool checker = ((x / 12) + (y / 12)) % 2 == 0;
                    Vector4 color = Palette(
                        (x / (float)imageWidth + y / (float)imageHeight) * 0.5f,
                        palette);
                    _imagePixels[offset] = (byte)Math.Clamp(
                        (checker ? color.X : color.Z) * 255f,
                        0f,
                        255f);
                    _imagePixels[offset + 1] = (byte)Math.Clamp(
                        color.Y * 255f,
                        0f,
                        255f);
                    _imagePixels[offset + 2] = (byte)Math.Clamp(
                        (checker ? color.Z : color.X) * 255f,
                        0f,
                        255f);
                    _imagePixels[offset + 3] = byte.MaxValue;
                    float normalizedX = (x + 0.5f) / imageWidth * 2f - 1f;
                    float normalizedY = (y + 0.5f) / imageHeight * 2f - 1f;
                    byte mask = (byte)Math.Clamp(
                        (1f - MathF.Sqrt(
                            normalizedX * normalizedX +
                            normalizedY * normalizedY)) * 384f,
                        0f,
                        255f);
                    _imageMaskPixels[offset] = mask;
                    _imageMaskPixels[offset + 1] = mask;
                    _imageMaskPixels[offset + 2] = mask;
                    _imageMaskPixels[offset + 3] = mask;
                }
            }
            _imageRevision++;
            if (_imageRevision == 0U)
            {
                _imageRevision = 1U;
            }
            if (writeExternalSource)
            {
                _externalImageSource.WritePixels<byte>(_imagePixels);
                if (writeExternalMask)
                {
                    _externalImageMask.WritePixels<byte>(_imageMaskPixels);
                }
                _imageNeedsUpload = false;
            }
            else
            {
                _imageNeedsUpload = true;
            }
        }

        private void UpdateCountText()
        {
            if (_countRun is not null)
            {
                _countRun.Text = _mode switch
                {
                    NativeBatchMode.RepresentativeScene =>
                        "Representative semantic scene",
                    NativeBatchMode.ManagedPicture =>
                        $"Managed → C++: {_rectangleCount:N0}",
                    NativeBatchMode.Analytic => $"Analytic: {_rectangleCount:N0}",
                    NativeBatchMode.Geometry => $"Geometry: {_rectangleCount:N0}",
                    NativeBatchMode.Curves => $"Curves: {_rectangleCount:N0}",
                    NativeBatchMode.Polylines => $"Polylines: {_rectangleCount:N0}",
                    NativeBatchMode.Dashes => $"Dashes: {_rectangleCount:N0}",
                    NativeBatchMode.Splines => $"Splines: {_rectangleCount:N0}",
                    NativeBatchMode.Paths => $"Paths: {_rectangleCount:N0}",
                    NativeBatchMode.Glyphs => $"Glyphs: {_rectangleCount:N0}",
                    NativeBatchMode.Images => "Retained RGBA image",
                    NativeBatchMode.ExternalImages => "Zero-copy GPU image view",
                    NativeBatchMode.MaskedImages => "Zero-copy GPU image + mask",
                    _ => $"Rectangles: {_rectangleCount:N0}"
                };
            }
        }

        private enum NativeBatchMode
        {
            RepresentativeScene,
            ManagedPicture,
            Analytic,
            Geometry,
            Curves,
            Polylines,
            Dashes,
            Splines,
            Paths,
            Glyphs,
            Images,
            ExternalImages,
            MaskedImages,
            Rectangles
        }

        private static Button CreateButton(string text, float width)
        {
            var label = CreateText(text, 12f);
            label.Margin = new Thickness(0);
            return new Button
            {
                Width = width,
                Height = 36f,
                CornerRadius = 6f,
                Content = label
            };
        }
    }

    private sealed class NativeTexturePreview : FrameworkElement
    {
        private readonly GpuTexture _texture;
        private readonly Action<Vector2> _arranged;
        private Rect _destination;

        public NativeTexturePreview(
            GpuTexture texture,
            Action<Vector2> arranged)
        {
            _texture = texture;
            _arranged = arranged;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
        }

        public void SetDestination(Rect destination)
        {
            if (_destination == destination)
            {
                return;
            }

            _destination = destination;
            Invalidate();
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            base.ArrangeOverride(arrangeRect);
            _arranged(new Vector2(arrangeRect.Width, arrangeRect.Height));
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawTexture(
                _texture,
                _destination.Width > 0f && _destination.Height > 0f
                    ? _destination
                    : new Rect(Vector2.Zero, Size));
        }
    }
}
