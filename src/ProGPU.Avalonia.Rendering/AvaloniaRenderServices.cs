using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Avalonia.ProGpu;

/// <summary>
/// Creates ProGPU-owned implementations for Avalonia's rendering primitives.
/// </summary>
internal sealed class PlatformRenderInterface :
    IPlatformRenderInterface
#if PROGPU_AVALONIA_SOURCE_COMPOSITOR
    , IPlatformRenderInterfaceNativeSurfaceFeature
#endif
{
    private readonly bool _requireNativeScene;
    private readonly bool _useDawnMetal;
    private readonly bool _requireDawnMetal;
    private readonly bool _useDawnNative;
    private readonly bool _requireDawnNative;

    public PlatformRenderInterface(
        long? maxResourceBytes = null,
        bool requireNativeCompositionScene = false,
        bool useDawnMetalPresentation = true,
        bool requireDawnMetalPresentation = false,
        bool useDawnNativePresentation = true,
        bool requireDawnNativePresentation = false)
    {
        _ = maxResourceBytes;
        _requireNativeScene = requireNativeCompositionScene;
        _useDawnMetal = useDawnMetalPresentation;
        _requireDawnMetal = requireDawnMetalPresentation;
        _useDawnNative = useDawnNativePresentation;
        _requireDawnNative = requireDawnNativePresentation;
    }

    public bool SupportsIndividualRoundRects => true;

    public bool SupportsRegions => true;

    public AlphaFormat DefaultAlphaFormat => AlphaFormat.Premul;

    public PixelFormat DefaultPixelFormat => PixelFormat.Rgba8888;

    public bool RequiresOpaqueSurface => _useDawnNative;

    public IPlatformRenderInterfaceContext CreateBackendContext(
        IPlatformGraphicsContext? graphicsApiContext) =>
        new ProGpuBackendContext(
            graphicsApiContext,
            _requireNativeScene,
            _useDawnMetal,
            _requireDawnMetal,
            _useDawnNative,
            _requireDawnNative);

    public bool IsSupportedBitmapPixelFormat(PixelFormat format) =>
        format == PixelFormats.Rgb565 ||
        format == PixelFormats.Bgra8888 ||
        format == PixelFormats.Rgba8888;

    public IPlatformRenderInterfaceRegion CreateRegion() =>
        new AvaloniaDirtyRegion();

    public IGeometryImpl CreateEllipseGeometry(Rect rect) =>
        AvaloniaGeometryFactory.Ellipse(rect);

    public IGeometryImpl CreateLineGeometry(Point p1, Point p2) =>
        AvaloniaGeometryFactory.Line(p1, p2);

    public IGeometryImpl CreateRectangleGeometry(Rect rect) =>
        AvaloniaGeometryFactory.Rectangle(rect);

    public IStreamGeometryImpl CreateStreamGeometry() =>
        new AvaloniaStreamPath();

    public IGeometryImpl CreateGeometryGroup(
        FillRule fillRule,
        IReadOnlyList<IGeometryImpl> children) =>
        AvaloniaGeometryFactory.Group(fillRule, children);

    public IGeometryImpl CreateCombinedGeometry(
        GeometryCombineMode combineMode,
        IGeometryImpl first,
        IGeometryImpl second) =>
        AvaloniaGeometryFactory.Combine(combineMode, first, second);

    public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun)
    {
#if AVALONIA11
        ProGpuTypeface typeface = glyphRun.GlyphTypeface as ProGpuTypeface
#else
        ProGpuTypeface typeface =
            glyphRun.GlyphTypeface.PlatformTypeface as ProGpuTypeface
#endif
            ?? throw new InvalidOperationException(
                "The glyph run is not backed by a ProGPU typeface.");

        double unitsPerEm = typeface.Font.UnitsPerEm;
        if (unitsPerEm <= 0)
            return new AvaloniaStreamPath();

        float scale = (float)(glyphRun.FontRenderingEmSize / unitsPerEm);
        double advance = 0;
        var result = new ProGPU.Vector.PathGeometry();
        IReadOnlyList<GlyphInfo> glyphs = glyphRun.GlyphInfos;

        for (int index = 0; index < glyphs.Count; index++)
        {
            GlyphInfo glyph = glyphs[index];
            ProGPU.Vector.PathGeometry? outline =
                typeface.Font.GetGlyphOutline(glyph.GlyphIndex);
            if (outline is not null)
            {
                float x = (float)(
                    glyphRun.BaselineOrigin.X +
                    advance +
                    glyph.GlyphOffset.X);
                float y = (float)(
                    glyphRun.BaselineOrigin.Y +
                    glyph.GlyphOffset.Y);
                Matrix4x4 transform =
                    Matrix4x4.CreateScale(scale, -scale, 1) *
                    Matrix4x4.CreateTranslation(x, y, 0);
                ProGPU.Vector.PathGeometry transformed =
                    outline.CreateTransformed(transform);
                result.Figures.AddRange(transformed.Figures);
            }

            advance += glyph.GlyphAdvance;
        }

        return new AvaloniaStreamPath(result);
    }

    public IBitmapImpl LoadBitmap(string fileName)
    {
        using FileStream input = File.OpenRead(fileName);
        return LoadBitmap(input);
    }

    public IBitmapImpl LoadBitmap(Stream stream) =>
        new ImmutableBitmap(stream);

    public IBitmapImpl LoadBitmap(
        PixelFormat format,
        AlphaFormat alphaFormat,
        IntPtr data,
        PixelSize size,
        Vector dpi,
        int stride) =>
        new ImmutableBitmap(
            size,
            dpi,
            stride,
            format,
            alphaFormat,
            data);

    public IBitmapImpl LoadBitmapToWidth(
        Stream stream,
        int width,
        BitmapInterpolationMode interpolationMode =
            BitmapInterpolationMode.HighQuality) =>
        new ImmutableBitmap(
            stream,
            width,
            horizontal: true,
            interpolationMode);

    public IBitmapImpl LoadBitmapToHeight(
        Stream stream,
        int height,
        BitmapInterpolationMode interpolationMode =
            BitmapInterpolationMode.HighQuality) =>
        new ImmutableBitmap(
            stream,
            height,
            horizontal: false,
            interpolationMode);

    public IBitmapImpl ResizeBitmap(
        IBitmapImpl bitmapImpl,
        PixelSize destinationSize,
        BitmapInterpolationMode interpolationMode =
            BitmapInterpolationMode.HighQuality)
    {
        return bitmapImpl is ImmutableBitmap bitmap
            ? new ImmutableBitmap(
                bitmap,
                destinationSize,
                interpolationMode)
            : throw new ArgumentException(
                "The source bitmap is not owned by ProGPU.",
                nameof(bitmapImpl));
    }

    public IWriteableBitmapImpl LoadWriteableBitmap(string fileName)
    {
        using FileStream input = File.OpenRead(fileName);
        return LoadWriteableBitmap(input);
    }

    public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream) =>
        new WriteableBitmapImpl(stream);

    public IWriteableBitmapImpl LoadWriteableBitmapToWidth(
        Stream stream,
        int width,
        BitmapInterpolationMode interpolationMode =
            BitmapInterpolationMode.HighQuality) =>
        new WriteableBitmapImpl(
            stream,
            width,
            horizontal: true,
            interpolationMode);

    public IWriteableBitmapImpl LoadWriteableBitmapToHeight(
        Stream stream,
        int height,
        BitmapInterpolationMode interpolationMode =
            BitmapInterpolationMode.HighQuality) =>
        new WriteableBitmapImpl(
            stream,
            height,
            horizontal: false,
            interpolationMode);

    public IRenderTargetBitmapImpl CreateRenderTargetBitmap(
        PixelSize size,
        Vector dpi)
    {
        ValidateSize(size);
        return new RenderTargetBitmapImpl(size, dpi);
    }

    public IWriteableBitmapImpl CreateWriteableBitmap(
        PixelSize size,
        Vector dpi,
        PixelFormat format,
        AlphaFormat alphaFormat)
    {
        ValidateSize(size);
        return new WriteableBitmapImpl(
            size,
            dpi,
            format,
            alphaFormat);
    }

    public IGlyphRunImpl CreateGlyphRun(
#if AVALONIA11
        IGlyphTypeface glyphTypeface,
#else
        GlyphTypeface glyphTypeface,
#endif
        double fontRenderingEmSize,
        IReadOnlyList<GlyphInfo> glyphInfos,
        Point baselineOrigin) =>
        new GlyphRunImpl(
            glyphTypeface,
            fontRenderingEmSize,
            glyphInfos,
            baselineOrigin);

    private static void ValidateSize(PixelSize size)
    {
        if (size.Width < 1)
            throw new ArgumentException(
                "Bitmap width must be positive.",
                nameof(size));
        if (size.Height < 1)
            throw new ArgumentException(
                "Bitmap height must be positive.",
                nameof(size));
    }
}
