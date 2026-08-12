using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.WebGPU;

namespace ProGPU.Backend.Native;

/// <summary>
/// Owns one ProGPU C++ renderer over an existing Silk/wgpu-native device.
/// </summary>
/// <remarks>
/// Rendering crosses the C ABI once per frame and submits one native WebGPU
/// command buffer. The compositor is owner-thread affine and must be disposed
/// before its <see cref="WgpuContext"/> unless context disposal does so first.
/// </remarks>
public sealed unsafe class NativeCompositor : IDisposable
{
    private const uint ExternalImageSourceViewFlag = 1U;

    private readonly WgpuContext _context;
    private readonly TextureFormat _targetFormat;
    private nint _engine;
    private int _disposeState;

    public NativeCompositor(
        WgpuContext context,
        TextureFormat targetFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsDisposed || !context.IsInitialized)
        {
            throw new ObjectDisposedException(nameof(context));
        }
        if (context.BackendKind != WgpuBackendKind.SilkNative)
        {
            throw new NotSupportedException(
                "This native binary accepts only the exact Silk.NET 2.23 wgpu-native ABI. Dawn and browser devices require their own adapters.");
        }

        var nativeFormat = ToNativeFormat(targetFormat);
        var options = new NativeMethods.EngineOptions
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.EngineOptions>(),
            AbiVersion = NativeMethods.AbiVersion,
            BackendAbi = NativeMethods.WgpuNativeMay2024BackendAbi,
            TargetFormat = nativeFormat,
            Device = (nuint)context.Device,
            Queue = (nuint)context.Queue
        };

        lock (context.RenderLock)
        {
            var engine = nint.Zero;
            var status = NativeMethods.Create(&options, &engine);
            if (status != NativeRendererStatus.Success || engine == 0)
            {
                throw new NativeRendererException(
                    status,
                    "The ProGPU native renderer could not be created.");
            }
            _engine = engine;
        }

        _context = context;
        _targetFormat = targetFormat;
        WgpuContext.Disposing += OnContextDisposing;
    }

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    /// <summary>
    /// Returns the queue token for the most recently submitted native frame.
    /// </summary>
    public NativeSubmissionToken GetLastSubmissionToken()
    {
        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            ulong value = 0;
            ThrowForStatus(NativeMethods.GetLastSubmission(_engine, &value));
            return new NativeSubmissionToken(value, _engine);
        }
    }

    /// <summary>
    /// Tests whether the GPU has completed a native submission without waiting.
    /// </summary>
    public bool IsSubmissionComplete(NativeSubmissionToken token) =>
        PollSubmission(token, wait: false);

    /// <summary>
    /// Waits until the GPU completes a native submission.
    /// </summary>
    public void WaitForSubmission(NativeSubmissionToken token)
    {
        if (!PollSubmission(token, wait: true))
        {
            throw new NativeRendererException(
                NativeRendererStatus.DeviceLost,
                "The WebGPU queue did not complete the requested native submission.");
        }
    }

    public static NativeRendererInfo GetInfo()
    {
        if (NativeMethods.GetAbiVersion() != NativeMethods.AbiVersion)
        {
            throw new NotSupportedException(
                "The loaded ProGPU native renderer has an incompatible ABI.");
        }

        var info = new NativeMethods.EngineInfo
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.EngineInfo>()
        };
        if (NativeMethods.GetInfo(&info) == 0)
        {
            throw new InvalidOperationException(
                "The ProGPU native renderer did not return its capability record.");
        }

        byte* namePointer = info.Name;
        var name = Marshal.PtrToStringUTF8((nint)namePointer) ?? string.Empty;
        return new NativeRendererInfo(
            info.AbiVersion,
            info.BackendAbi,
            (NativeRendererCapabilities)info.Capabilities,
            name);
    }

    public NativeFrameMetrics Render(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeSolidRectangle> rectangles,
        Vector4 clearColor)
    {
        ValidateTarget(target);

        fixed (NativeSolidRectangle* rectanglePointer = rectangles)
        {
            var frame = new NativeMethods.Frame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.Frame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                Rectangles = rectanglePointer,
                RectangleCount = (nuint)rectangles.Length
            };
            var metrics = new NativeMethods.FrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.FrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.Render(_engine, &frame, &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativeFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.VertexUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount);
        }
    }

    public NativeAnalyticFrameMetrics RenderAnalytic(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeAnalyticPrimitive> primitives,
        Vector4 clearColor)
    {
        ValidateTarget(target);

        fixed (NativeAnalyticPrimitive* primitivePointer = primitives)
        {
            var frame = new NativeMethods.AnalyticFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.AnalyticFrame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                Primitives = primitivePointer,
                PrimitiveCount = (nuint)primitives.Length
            };
            var metrics = new NativeMethods.AnalyticFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.AnalyticFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.RenderAnalytic(
                    _engine,
                    &frame,
                    &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativeAnalyticFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount);
        }
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0)
    {
        return RenderGeometry(
            target,
            dpiScale,
            primitives,
            ReadOnlySpan<Vector2>.Empty,
            ReadOnlySpan<NativePolyline>.Empty,
            ReadOnlySpan<double>.Empty,
            ReadOnlySpan<NativeDashStyle>.Empty,
            ReadOnlySpan<NativeSpline>.Empty,
            clearColor,
            capturePayloadHash,
            contentRevision);
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<NativePolyline> polylines,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0)
    {
        return RenderGeometry(
            target,
            dpiScale,
            primitives,
            points,
            polylines,
            ReadOnlySpan<double>.Empty,
            ReadOnlySpan<NativeDashStyle>.Empty,
            ReadOnlySpan<NativeSpline>.Empty,
            clearColor,
            capturePayloadHash,
            contentRevision);
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<NativePolyline> polylines,
        ReadOnlySpan<double> doubles,
        ReadOnlySpan<NativeSpline> splines,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0)
    {
        return RenderGeometry(
            target,
            dpiScale,
            primitives,
            points,
            polylines,
            doubles,
            ReadOnlySpan<NativeDashStyle>.Empty,
            splines,
            clearColor,
            capturePayloadHash,
            contentRevision);
    }

    public NativeGeometryFrameMetrics RenderGeometry(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGeometryPrimitive> primitives,
        ReadOnlySpan<Vector2> points,
        ReadOnlySpan<NativePolyline> polylines,
        ReadOnlySpan<double> doubles,
        ReadOnlySpan<NativeDashStyle> dashStyles,
        ReadOnlySpan<NativeSpline> splines,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0)
    {
        ValidateTarget(target);

        fixed (NativeGeometryPrimitive* primitivePointer = primitives)
        fixed (Vector2* pointPointer = points)
        fixed (NativePolyline* polylinePointer = polylines)
        fixed (double* doublePointer = doubles)
        fixed (NativeDashStyle* dashStylePointer = dashStyles)
        fixed (NativeSpline* splinePointer = splines)
        {
            var frame = new NativeMethods.GeometryFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GeometryFrame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                Primitives = primitivePointer,
                PrimitiveCount = (nuint)primitives.Length,
                Flags = (capturePayloadHash
                        ? NativeMethods.GeometryFrameCapturePayloadHash
                        : 0U) |
                    (contentRevision != 0U
                        ? NativeMethods.GeometryFrameRetainCompiledPayload
                        : 0U),
                Reserved = contentRevision,
                Points = pointPointer,
                PointCount = (nuint)points.Length,
                Polylines = polylinePointer,
                PolylineCount = (nuint)polylines.Length,
                Doubles = doublePointer,
                DoubleCount = (nuint)doubles.Length,
                DashStyles = dashStylePointer,
                DashStyleCount = (nuint)dashStyles.Length,
                Splines = splinePointer,
                SplineCount = (nuint)splines.Length
            };
            var metrics = new NativeMethods.GeometryFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GeometryFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.RenderGeometry(
                    _engine,
                    &frame,
                    &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativeGeometryFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.BrushUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    public NativePathFrameMetrics RenderPaths(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativePathFill> paths,
        ReadOnlySpan<NativePathSegment> segments,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0)
    {
        ValidateTarget(target);

        fixed (NativePathFill* pathPointer = paths)
        fixed (NativePathSegment* segmentPointer = segments)
        {
            var frame = new NativeMethods.PathFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.PathFrame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                Paths = pathPointer,
                PathCount = (nuint)paths.Length,
                Segments = segmentPointer,
                SegmentCount = (nuint)segments.Length,
                Flags = (capturePayloadHash
                        ? NativeMethods.GeometryFrameCapturePayloadHash
                        : 0U) |
                    (contentRevision != 0U
                        ? NativeMethods.GeometryFrameRetainCompiledPayload
                        : 0U),
                ContentRevision = contentRevision
            };
            var metrics = new NativeMethods.PathFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.PathFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.RenderPaths(_engine, &frame, &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativePathFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.RasterizedPathCount,
                metrics.AtlasWidth,
                metrics.AtlasHeight,
                metrics.AtlasGeneration,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.BrushUploadBytes,
                metrics.PathUploadBytes,
                metrics.CoverageStagingBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    public NativeGlyphFrameMetrics RenderGlyphs(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<NativeGlyphOutline> outlines,
        ReadOnlySpan<NativePathSegment> segments,
        ReadOnlySpan<NativePositionedGlyph> glyphs,
        Vector4 clearColor,
        bool capturePayloadHash = false,
        uint contentRevision = 0)
    {
        ValidateTarget(target);

        fixed (NativeGlyphOutline* outlinePointer = outlines)
        fixed (NativePathSegment* segmentPointer = segments)
        fixed (NativePositionedGlyph* glyphPointer = glyphs)
        {
            var frame = new NativeMethods.GlyphFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GlyphFrame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                Outlines = outlinePointer,
                OutlineCount = (nuint)outlines.Length,
                Segments = segmentPointer,
                SegmentCount = (nuint)segments.Length,
                Glyphs = glyphPointer,
                GlyphCount = (nuint)glyphs.Length,
                Flags = (capturePayloadHash
                        ? NativeMethods.GeometryFrameCapturePayloadHash
                        : 0U) |
                    (contentRevision != 0U
                        ? NativeMethods.GeometryFrameRetainCompiledPayload
                        : 0U),
                ContentRevision = contentRevision
            };
            var metrics = new NativeMethods.GlyphFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.GlyphFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.RenderGlyphs(
                    _engine,
                    &frame,
                    &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativeGlyphFrameMetrics(
                metrics.DrawCallCount,
                metrics.GlyphCount,
                metrics.RasterizedGlyphCount,
                metrics.AtlasWidth,
                metrics.AtlasHeight,
                metrics.AtlasGeneration,
                metrics.AtlasGrowthCount,
                metrics.InstanceUploadBytes,
                metrics.OutlineUploadBytes,
                metrics.CoverageStagingBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    public NativeImageFrameMetrics RenderImage(
        GpuTexture target,
        float dpiScale,
        ReadOnlySpan<byte> rgbaPixels,
        uint imageWidth,
        uint imageHeight,
        uint rowBytes,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        Vector4 clearColor,
        uint imageRevision,
        uint contentRevision)
    {
        ValidateTarget(target);

        fixed (byte* pixelPointer = rgbaPixels)
        {
            var frame = new NativeMethods.ImageFrame
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrame>(),
                Width = target.Width,
                Height = target.Height,
                DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr,
                ClearColor = new NativeMethods.NativeColor
                {
                    R = clearColor.X,
                    G = clearColor.Y,
                    B = clearColor.Z,
                    A = clearColor.W
                },
                RgbaPixels = pixelPointer,
                PixelBytes = (nuint)rgbaPixels.Length,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                RowBytes = rowBytes,
                Sampling = sampling,
                ImageRevision = imageRevision,
                ContentRevision = contentRevision,
                SourceRect = sourceRect,
                DestinationRect = destinationRect,
                Transform = transform,
                Opacity = opacity,
                Reserved = 0U,
                ExternalSourceView = 0U,
                SourceFlags = 0U,
                Reserved2 = 0U
            };
            var metrics = new NativeMethods.ImageFrameMetrics
            {
                StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>()
            };

            lock (_context.RenderLock)
            {
                ThrowIfDisposed();
                var status = NativeMethods.RenderImage(
                    _engine,
                    &frame,
                    &metrics);
                if (status != NativeRendererStatus.Success)
                {
                    throw new NativeRendererException(status, ReadLastError());
                }
                target.NotifyExternalContentChanged();
                _context.PollDevice(wait: false);
            }

            return new NativeImageFrameMetrics(
                metrics.DrawCallCount,
                metrics.VertexCount,
                metrics.IndexCount,
                metrics.TextureGeneration,
                metrics.VertexUploadBytes,
                metrics.IndexUploadBytes,
                metrics.TextureUploadBytes,
                metrics.UniformUploadBytes,
                metrics.SubmissionCount,
                metrics.PayloadHash);
        }
    }

    /// <summary>
    /// Samples an existing texture from the compositor's WebGPU device
    /// without transferring its pixels through the native ABI.
    /// </summary>
    /// <remarks>
    /// The native renderer retains the source view until another image source
    /// replaces it or this compositor is disposed. Keep <paramref name="source"/>
    /// alive and undisposed for that interval. Increment
    /// <paramref name="sourceRevision"/> after producer work changes the source
    /// contents and increment <paramref name="contentRevision"/> when the draw
    /// rectangle, transform, opacity, or sampling state changes.
    /// </remarks>
    public NativeImageFrameMetrics RenderExternalImage(
        GpuTexture target,
        GpuTexture source,
        float dpiScale,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        Vector4 clearColor,
        uint sourceRevision,
        uint contentRevision)
    {
        ValidateTarget(target);
        ValidateImageSource(source, target);
        var frame = new NativeMethods.ImageFrame
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrame>(),
            Width = target.Width,
            Height = target.Height,
            DpiScale = dpiScale,
            TargetView = (nuint)target.ViewPtr,
            ClearColor = new NativeMethods.NativeColor
            {
                R = clearColor.X,
                G = clearColor.Y,
                B = clearColor.Z,
                A = clearColor.W
            },
            RgbaPixels = null,
            PixelBytes = 0U,
            ImageWidth = source.Width,
            ImageHeight = source.Height,
            RowBytes = 0U,
            Sampling = sampling,
            ImageRevision = sourceRevision,
            ContentRevision = contentRevision,
            SourceRect = sourceRect,
            DestinationRect = destinationRect,
            Transform = transform,
            Opacity = opacity,
            Reserved = 0U,
            ExternalSourceView = (nuint)source.ViewPtr,
            SourceFlags = ExternalImageSourceViewFlag,
            Reserved2 = 0U,
            ExternalMaskView = 0U,
            MaskWidth = 0U,
            MaskHeight = 0U,
            MaskDestinationRect = default,
            MaskRevision = 0U,
            MaskSampling = NativeImageSampling.Nearest
        };
        var metrics = new NativeMethods.ImageFrameMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>()
        };

        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            var status = NativeMethods.RenderImage(_engine, &frame, &metrics);
            if (status != NativeRendererStatus.Success)
            {
                throw new NativeRendererException(status, ReadLastError());
            }
            target.NotifyExternalContentChanged();
            _context.PollDevice(wait: false);
        }

        return new NativeImageFrameMetrics(
            metrics.DrawCallCount,
            metrics.VertexCount,
            metrics.IndexCount,
            metrics.TextureGeneration,
            metrics.VertexUploadBytes,
            metrics.IndexUploadBytes,
            metrics.TextureUploadBytes,
            metrics.UniformUploadBytes,
            metrics.SubmissionCount,
            metrics.PayloadHash);
    }

    /// <summary>
    /// Samples a same-device image through a same-device texture opacity mask
    /// without transferring either payload through the native ABI.
    /// </summary>
    /// <remarks>
    /// The mask red channel is mapped over <paramref name="maskDestinationRect"/>
    /// in logical target coordinates and multiplies source alpha. Both source
    /// views remain retained until replacement or compositor disposal, so both
    /// textures must remain alive for that interval.
    /// </remarks>
    public NativeImageFrameMetrics RenderMaskedExternalImage(
        GpuTexture target,
        GpuTexture source,
        GpuTexture mask,
        float dpiScale,
        NativeImageRect sourceRect,
        NativeImageRect destinationRect,
        NativeImageRect maskDestinationRect,
        Matrix3x2 transform,
        float opacity,
        NativeImageSampling sampling,
        NativeImageSampling maskSampling,
        Vector4 clearColor,
        uint sourceRevision,
        uint maskRevision,
        uint contentRevision)
    {
        ValidateTarget(target);
        ValidateImageSource(source, target);
        ValidateImageMask(mask, target);
        var frame = new NativeMethods.ImageFrame
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrame>(),
            Width = target.Width,
            Height = target.Height,
            DpiScale = dpiScale,
            TargetView = (nuint)target.ViewPtr,
            ClearColor = new NativeMethods.NativeColor
            {
                R = clearColor.X,
                G = clearColor.Y,
                B = clearColor.Z,
                A = clearColor.W
            },
            ImageWidth = source.Width,
            ImageHeight = source.Height,
            Sampling = sampling,
            ImageRevision = sourceRevision,
            ContentRevision = contentRevision,
            SourceRect = sourceRect,
            DestinationRect = destinationRect,
            Transform = transform,
            Opacity = opacity,
            ExternalSourceView = (nuint)source.ViewPtr,
            SourceFlags = ExternalImageSourceViewFlag,
            ExternalMaskView = (nuint)mask.ViewPtr,
            MaskWidth = mask.Width,
            MaskHeight = mask.Height,
            MaskDestinationRect = maskDestinationRect,
            MaskRevision = maskRevision,
            MaskSampling = maskSampling
        };
        var metrics = new NativeMethods.ImageFrameMetrics
        {
            StructSize = (uint)Unsafe.SizeOf<NativeMethods.ImageFrameMetrics>()
        };

        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            var status = NativeMethods.RenderImage(_engine, &frame, &metrics);
            if (status != NativeRendererStatus.Success)
            {
                throw new NativeRendererException(status, ReadLastError());
            }
            target.NotifyExternalContentChanged();
            _context.PollDevice(wait: false);
        }

        return new NativeImageFrameMetrics(
            metrics.DrawCallCount,
            metrics.VertexCount,
            metrics.IndexCount,
            metrics.TextureGeneration,
            metrics.VertexUploadBytes,
            metrics.IndexUploadBytes,
            metrics.TextureUploadBytes,
            metrics.UniformUploadBytes,
            metrics.SubmissionCount,
            metrics.PayloadHash);
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void OnContextDisposing(WgpuContext context)
    {
        if (ReferenceEquals(context, _context))
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        WgpuContext.Disposing -= OnContextDisposing;
        var engine = Interlocked.Exchange(ref _engine, 0);
        if (engine == 0)
        {
            return;
        }

        lock (_context.RenderLock)
        {
            NativeMethods.Destroy(engine);
        }
    }

    private bool PollSubmission(NativeSubmissionToken token, bool wait)
    {
        if (!token.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(token),
                "A native submission token must be non-zero.");
        }

        lock (_context.RenderLock)
        {
            ThrowIfDisposed();
            if (token.Owner != _engine)
            {
                throw new ArgumentException(
                    "A native submission token belongs to a different compositor.",
                    nameof(token));
            }
            byte complete = 0;
            ThrowForStatus(NativeMethods.PollSubmission(
                _engine,
                token.Value,
                wait ? (byte)1 : (byte)0,
                &complete));
            return complete != 0;
        }
    }

    private void ThrowForStatus(NativeRendererStatus status)
    {
        if (status != NativeRendererStatus.Success)
        {
            throw new NativeRendererException(status, ReadLastError());
        }
    }

    private string ReadLastError()
    {
        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* pointer = buffer)
        {
            var required = NativeMethods.GetLastError(
                _engine,
                pointer,
                (nuint)buffer.Length);
            if (required == 0)
            {
                return "The ProGPU native renderer returned an unspecified error.";
            }
            var terminator = buffer.IndexOf((byte)0);
            var length = terminator >= 0 ? terminator : buffer.Length;
            return Encoding.UTF8.GetString(buffer[..length]);
        }
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed || _context.IsDisposed || _engine == 0)
        {
            throw new ObjectDisposedException(nameof(NativeCompositor));
        }
    }

    private void ValidateTarget(GpuTexture target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        if (!ReferenceEquals(target.Context, _context))
        {
            throw new ArgumentException(
                "The target must belong to the native compositor's WebGPU device domain.",
                nameof(target));
        }
        if (target.IsDisposed || target.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(target));
        }
        if (target.Format != _targetFormat || target.SampleCount != 1)
        {
            throw new ArgumentException(
                "The target format and sample count must match the native pipeline.",
                nameof(target));
        }
        if ((target.Usage & TextureUsage.RenderAttachment) == 0)
        {
            throw new ArgumentException(
                "The target must allow WebGPU render-attachment usage.",
                nameof(target));
        }
    }

    private void ValidateImageSource(GpuTexture source, GpuTexture target)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, target))
        {
            throw new ArgumentException(
                "An external image source cannot also be the active render target.",
                nameof(source));
        }
        if (!ReferenceEquals(source.Context, _context))
        {
            throw new ArgumentException(
                "The external image source must belong to the native compositor's WebGPU device domain.",
                nameof(source));
        }
        if (source.IsDisposed || source.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(source));
        }
        if ((source.Usage & TextureUsage.TextureBinding) == 0 ||
            source.SampleCount != 1 ||
            source.AlphaMode != GpuTextureAlphaMode.Straight ||
            source.Format is not (
                TextureFormat.Rgba8Unorm or
                TextureFormat.Bgra8Unorm or
                TextureFormat.Rgba8UnormSrgb or
                TextureFormat.Bgra8UnormSrgb))
        {
            throw new ArgumentException(
                "The first external image lane requires a single-sample bindable straight-alpha RGBA/BGRA 8-bit texture.",
                nameof(source));
        }
    }

    private void ValidateImageMask(GpuTexture mask, GpuTexture target)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (ReferenceEquals(mask, target))
        {
            throw new ArgumentException(
                "An image mask cannot also be the active render target.",
                nameof(mask));
        }
        if (!ReferenceEquals(mask.Context, _context))
        {
            throw new ArgumentException(
                "The image mask must belong to the native compositor's WebGPU device domain.",
                nameof(mask));
        }
        if (mask.IsDisposed || mask.ViewPtr == null)
        {
            throw new ObjectDisposedException(nameof(mask));
        }
        if ((mask.Usage & TextureUsage.TextureBinding) == 0 ||
            mask.SampleCount != 1 ||
            mask.Format is not (
                TextureFormat.R8Unorm or
                TextureFormat.Rgba8Unorm or
                TextureFormat.Bgra8Unorm))
        {
            throw new ArgumentException(
                "The initial native image-mask lane requires a single-sample bindable R8/RGBA/BGRA unorm texture.",
                nameof(mask));
        }
    }

    private static NativeRendererTextureFormat ToNativeFormat(
        TextureFormat format) => format switch
        {
            TextureFormat.Rgba8Unorm => NativeRendererTextureFormat.Rgba8Unorm,
            TextureFormat.Bgra8Unorm => NativeRendererTextureFormat.Bgra8Unorm,
            TextureFormat.Rgba8UnormSrgb => NativeRendererTextureFormat.Rgba8UnormSrgb,
            TextureFormat.Bgra8UnormSrgb => NativeRendererTextureFormat.Bgra8UnormSrgb,
            _ => throw new NotSupportedException(
                $"The initial native renderer does not support {format} targets.")
        };
}

public sealed class NativeRendererException : Exception
{
    public NativeRendererException(
        NativeRendererStatus status,
        string message)
        : base(message)
    {
        Status = status;
    }

    public NativeRendererStatus Status { get; }
}
