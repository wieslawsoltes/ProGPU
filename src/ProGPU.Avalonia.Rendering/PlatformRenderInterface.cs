using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;

namespace Avalonia.ProGpu
{
    internal class PlatformRenderInterface : IPlatformRenderInterface
    {
        private readonly bool _requireNativeCompositionScene;
        private readonly bool _useDawnMetalPresentation;
        private readonly bool _requireDawnMetalPresentation;
        private readonly bool _useDawnNativePresentation;
        private readonly bool _requireDawnNativePresentation;

        public PlatformRenderInterface(
            long? maxResourceBytes = null,
            bool requireNativeCompositionScene = false,
            bool useDawnMetalPresentation = true,
            bool requireDawnMetalPresentation = false,
            bool useDawnNativePresentation = true,
            bool requireDawnNativePresentation = false)
        {
            _requireNativeCompositionScene = requireNativeCompositionScene;
            _useDawnMetalPresentation = useDawnMetalPresentation;
            _requireDawnMetalPresentation = requireDawnMetalPresentation;
            _useDawnNativePresentation = useDawnNativePresentation;
            _requireDawnNativePresentation =
                requireDawnNativePresentation;
            DefaultPixelFormat = PixelFormat.Rgba8888;
        }

        public IPlatformRenderInterfaceContext CreateBackendContext(IPlatformGraphicsContext? graphicsContext)
        {
            return new SkiaContext(
                graphicsContext,
                _requireNativeCompositionScene,
                _useDawnMetalPresentation,
                _requireDawnMetalPresentation,
                _useDawnNativePresentation,
                _requireDawnNativePresentation);
        }

        public bool SupportsIndividualRoundRects => true;

        public AlphaFormat DefaultAlphaFormat => AlphaFormat.Premul;

        public PixelFormat DefaultPixelFormat { get; }

        public bool IsSupportedBitmapPixelFormat(PixelFormat format) =>
            format == PixelFormats.Rgb565
            || format == PixelFormats.Bgra8888
            || format == PixelFormats.Rgba8888;

        public bool SupportsRegions => true;
        public IPlatformRenderInterfaceRegion CreateRegion() => new SkiaRegionImpl();

        public IGeometryImpl CreateEllipseGeometry(Rect rect) => new EllipseGeometryImpl(rect);

        public IGeometryImpl CreateLineGeometry(Point p1, Point p2) => new LineGeometryImpl(p1, p2);

        public IGeometryImpl CreateRectangleGeometry(Rect rect) => new RectangleGeometryImpl(rect);

        public IStreamGeometryImpl CreateStreamGeometry()
        {
            return new StreamGeometryImpl();
        }

        public IGeometryImpl CreateGeometryGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children)
        {
            return new GeometryGroupImpl(fillRule, children);
        }

        public IGeometryImpl CreateCombinedGeometry(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2)
        {
            return CombinedGeometryImpl.ForceCreate(combineMode, g1, g2);
        }

        public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun)
        {
#if AVALONIA11
            if (glyphRun.GlyphTypeface is not ProGpuTypeface glyphTypeface)
#else
            if (glyphRun.GlyphTypeface.PlatformTypeface is not ProGpuTypeface glyphTypeface)
#endif
            {
                throw new InvalidOperationException("PlatformTypeface is not ProGpuTypeface.");
            }

            var fontRenderingEmSize = (float)glyphRun.FontRenderingEmSize;
            double scale = fontRenderingEmSize / glyphTypeface.Font.UnitsPerEm;

            var (currentX, currentY) = glyphRun.BaselineOrigin;
            var combinedPath = new ProGPU.Vector.PathGeometry();

            for (var i = 0; i < glyphRun.GlyphInfos.Count; i++)
            {
                var glyphInfo = glyphRun.GlyphInfos[i];
                var glyph = glyphInfo.GlyphIndex;
                var offset = glyphInfo.GlyphOffset;
                var originX = currentX + offset.X;
                var originY = currentY + offset.Y;

                var outline = glyphTypeface.Font.GetGlyphOutline(glyph);
                if (outline != null)
                {
                    foreach (var figure in outline.Figures)
                    {
                        var startPoint = new Vector2(
                            (float)(originX + figure.StartPoint.X * scale),
                            (float)(originY - figure.StartPoint.Y * scale)
                        );

                        var newFigure = new ProGPU.Vector.PathFigure(startPoint, figure.IsClosed)
                        {
                            IsFilled = figure.IsFilled
                        };

                        foreach (var segment in figure.Segments)
                        {
                            if (segment is ProGPU.Vector.LineSegment line)
                            {
                                newFigure.Segments.Add(new ProGPU.Vector.LineSegment(new Vector2(
                                    (float)(originX + line.Point.X * scale),
                                    (float)(originY - line.Point.Y * scale)
                                )));
                            }
                            else if (segment is ProGPU.Vector.QuadraticBezierSegment quad)
                            {
                                newFigure.Segments.Add(new ProGPU.Vector.QuadraticBezierSegment(
                                    new Vector2(
                                        (float)(originX + quad.ControlPoint.X * scale),
                                        (float)(originY - quad.ControlPoint.Y * scale)
                                    ),
                                    new Vector2(
                                        (float)(originX + quad.Point.X * scale),
                                        (float)(originY - quad.Point.Y * scale)
                                    )
                                ));
                            }
                            else if (segment is ProGPU.Vector.CubicBezierSegment cubic)
                            {
                                newFigure.Segments.Add(new ProGPU.Vector.CubicBezierSegment(
                                    new Vector2(
                                        (float)(originX + cubic.ControlPoint1.X * scale),
                                        (float)(originY - cubic.ControlPoint1.Y * scale)
                                    ),
                                    new Vector2(
                                        (float)(originX + cubic.ControlPoint2.X * scale),
                                        (float)(originY - cubic.ControlPoint2.Y * scale)
                                    ),
                                    new Vector2(
                                        (float)(originX + cubic.Point.X * scale),
                                        (float)(originY - cubic.Point.Y * scale)
                                    )
                                ));
                            }
                        }
                        combinedPath.Figures.Add(newFigure);
                    }
                }

                currentX += glyphInfo.GlyphAdvance;
            }

            return new StreamGeometryImpl(combinedPath);
        }

        public IBitmapImpl LoadBitmap(string fileName)
        {
            using (var stream = File.OpenRead(fileName))
            {
                return LoadBitmap(stream);
            }
        }

        public IBitmapImpl LoadBitmap(Stream stream)
        {
            return new ImmutableBitmap(stream);
        }

        public IWriteableBitmapImpl LoadWriteableBitmapToWidth(Stream stream, int width,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WriteableBitmapImpl(stream, width, true, interpolationMode);
        }

        public IWriteableBitmapImpl LoadWriteableBitmapToHeight(Stream stream, int height,
            BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new WriteableBitmapImpl(stream, height, false, interpolationMode);
        }

        public IWriteableBitmapImpl LoadWriteableBitmap(string fileName)
        {
            using (var stream = File.OpenRead(fileName))
            {
                return LoadWriteableBitmap(stream);
            }
        }

        public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream)
        {
            return new WriteableBitmapImpl(stream);
        }

        public IBitmapImpl LoadBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride)
        {
            return new ImmutableBitmap(size, dpi, stride, format, alphaFormat, data);
        }

        public IBitmapImpl LoadBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new ImmutableBitmap(stream, width, true, interpolationMode);
        }

        public IBitmapImpl LoadBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            return new ImmutableBitmap(stream, height, false, interpolationMode);
        }

        public IBitmapImpl ResizeBitmap(IBitmapImpl bitmapImpl, PixelSize destinationSize, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality)
        {
            if (bitmapImpl is ImmutableBitmap ibmp)
            {
                return new ImmutableBitmap(ibmp, destinationSize, interpolationMode);
            }
            else
            {
                throw new Exception("Invalid source bitmap type.");
            }
        }

        public IRenderTargetBitmapImpl CreateRenderTargetBitmap(PixelSize size, Vector dpi)
        {
            if (size.Width < 1)
            {
                throw new ArgumentException("Width can't be less than 1", nameof(size));
            }

            if (size.Height < 1)
            {
                throw new ArgumentException("Height can't be less than 1", nameof(size));
            }

            return new RenderTargetBitmapImpl(size, dpi);
        }

        public IWriteableBitmapImpl CreateWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat)
        {
            return new WriteableBitmapImpl(size, dpi, format, alphaFormat);
        }

        public IGlyphRunImpl CreateGlyphRun(
#if AVALONIA11
            IGlyphTypeface
#else
            GlyphTypeface
#endif
            glyphTypeface, double fontRenderingEmSize,
            IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin)
        {
            return new GlyphRunImpl(glyphTypeface, fontRenderingEmSize, glyphInfos, baselineOrigin);
        }
    }
}
