using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.ComponentModel;
using System.Numerics;
using Xunit;

namespace ProGPU.SystemDrawing.Tests;

public sealed class DrawingApiQualityTests
{
    [Fact]
    public void RotateFlipMovesPixelsAndDimensionsExactly()
    {
        using var bitmap = new Bitmap(2, 3);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Green);
        bitmap.SetPixel(0, 2, Color.Blue);

        bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);

        Assert.Equal(new Size(3, 2), bitmap.Size);
        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(2, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), bitmap.GetPixel(2, 1).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
    }

    [Fact]
    public void CustomDashPatternUsesDefensiveCopies()
    {
        using var pen = new Pen(Color.Black);
        float[] input = [2f, 3f];
        pen.DashPattern = input;
        input[0] = 99f;

        float[] firstRead = pen.DashPattern;
        firstRead[1] = 88f;

        Assert.Equal(DashStyle.Custom, pen.DashStyle);
        Assert.Equal(new[] { 2f, 3f }, pen.DashPattern);
    }

    [Fact]
    public void CurvedRegionEmptinessDoesNotRequireRectangleScans()
    {
        using var path = new GraphicsPath();
        path.AddEllipse(0, 0, 10, 10);
        using var region = new Region(path);
        using var bitmap = new Bitmap(20, 20);
        using Graphics graphics = Graphics.FromImage(bitmap);

        Assert.False(region.IsEmpty(graphics));
    }

    [Fact]
    public void PreviewControllerProducesManagedPageWithoutPrinterDriver()
    {
        using var document = new PrintDocument();
        var controller = new PreviewPrintController { UseAntiAlias = true };
        document.PrintController = controller;
        document.PrintPage += (_, args) =>
        {
            args.Graphics.FillRectangle(Brushes.Red, new Rectangle(0, 0, 10, 10));
            args.HasMorePages = false;
        };

        document.Print();

        PreviewPageInfo page = Assert.Single(controller.GetPreviewPageInfo());
        Assert.Equal(document.DefaultPageSettings.Bounds.Size, page.PhysicalSize);
        page.Image.Dispose();
    }

    [Fact]
    public void ImageAttributesSnapshotCloneAndApplyColorRemapping()
    {
        var mutableMap = new ColorMap { OldColor = Color.Red, NewColor = Color.Blue };
        using var attributes = new ImageAttributes();
        attributes.SetRemapTable(mutableMap);
        mutableMap.NewColor = Color.Green;

        using var clone = Assert.IsType<ImageAttributes>(attributes.Clone());
        attributes.ClearRemapTable();

        using var source = new Bitmap(2, 1);
        source.SetPixel(0, 0, Color.Red);
        source.SetPixel(1, 0, Color.Green);
        using Bitmap remapped = source.CreateColorRemapped(clone.RemapTable);

        Assert.Equal(Color.Blue.ToArgb(), remapped.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), remapped.GetPixel(1, 0).ToArgb());
    }

    [Fact]
    public void ImageAttributesSnapshotColorMatrixAndRejectUseAfterDispose()
    {
        var matrix = new ColorMatrix { Matrix00 = 0.25f };
        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        matrix.Matrix00 = 0.75f;

        using var clone = Assert.IsType<ImageAttributes>(attributes.Clone());
        Assert.Equal(0.25f, clone.ColorMatrix!.Matrix00);

        attributes.Dispose();
        Assert.Throws<ObjectDisposedException>(() => attributes.ClearColorMatrix());
    }

    [Fact]
    public void CpuBackedIconRemapHasBoundedAllocation()
    {
        using var source = new Bitmap(64, 64);
        var map = new[] { (Color.Red, Color.Blue) };
        using (Bitmap warmup = source.CreateColorRemapped(map))
        {
        }

        const int iterations = 16;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            using Bitmap remapped = source.CreateColorRemapped(map);
        }
        long bytesPerRemap = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        Assert.InRange(bytesPerRemap, 16_384, 20_000);
    }

    [Fact]
    public void ImageMetadataFramesAndCloneHaveManagedValueSemantics()
    {
        using var bitmap = new Bitmap(8, 6);
        var tag = new object();
        bitmap.Tag = tag;
        bitmap.SetResolution(144f, 120f);

        GraphicsUnit boundsUnit = GraphicsUnit.Inch;
        RectangleF bounds = bitmap.GetBounds(ref boundsUnit);
        Guid[] dimensions = bitmap.FrameDimensionsList;
        dimensions[0] = Guid.Empty;

        using var clone = Assert.IsType<Bitmap>(bitmap.Clone());
        Assert.IsAssignableFrom<MarshalByRefObject>(bitmap);
        Assert.Equal(new SizeF(8f, 6f), bitmap.PhysicalDimension);
        Assert.Equal(new RectangleF(0f, 0f, 8f, 6f), bounds);
        Assert.Equal(GraphicsUnit.Pixel, boundsUnit);
        Assert.Equal(144f, clone.HorizontalResolution);
        Assert.Equal(120f, clone.VerticalResolution);
        Assert.Same(tag, clone.Tag);
        Assert.Equal(FrameDimension.Page.Guid, bitmap.FrameDimensionsList[0]);
        Assert.Equal("Page", FrameDimension.Page.ToString());
        Assert.Equal("[FrameDimension: 00000000-0000-0000-0000-000000000000]", new FrameDimension(Guid.Empty).ToString());
        Assert.Equal(1, bitmap.GetFrameCount(FrameDimension.Page));
        Assert.Equal(0, bitmap.SelectActiveFrame(FrameDimension.Page, 0));
        Assert.Throws<ArgumentException>(() => bitmap.GetFrameCount(FrameDimension.Time));
        Assert.Throws<ArgumentException>(() => bitmap.SelectActiveFrame(FrameDimension.Page, 1));
        Assert.Throws<ArgumentException>(() => bitmap.SetResolution(0f, 96f));
    }

    [Fact]
    public void PixelFormatClassifiersUseTheDeclaredFormatFlags()
    {
        Assert.True(Image.IsAlphaPixelFormat(PixelFormat.Format32bppArgb));
        Assert.True(Image.IsAlphaPixelFormat(PixelFormat.Format32bppPArgb));
        Assert.False(Image.IsAlphaPixelFormat(PixelFormat.Format24bppRgb));
        Assert.True(Image.IsCanonicalPixelFormat(PixelFormat.Format32bppArgb));
        Assert.False(Image.IsCanonicalPixelFormat(PixelFormat.Format32bppPArgb));
        Assert.False(Image.IsExtendedPixelFormat(PixelFormat.Format32bppArgb));
    }

    [Fact]
    public void PaletteAndPropertyMetadataUseTheDocumentedOwnershipBoundaries()
    {
        Color[] customColors = [Color.Red, Color.Blue];
        var palette = new ColorPalette(customColors);
        customColors[0] = Color.Green;
        Assert.Equal(Color.Green, palette.Entries[0]);

        using var bitmap = new Bitmap(2, 1);
        bitmap.Palette = palette;
        palette.Entries[0] = Color.Magenta;
        Assert.Equal(Color.Green, bitmap.Palette.Entries[0]);

        ColorPalette returnedPalette = bitmap.Palette;
        returnedPalette.Entries[0] = Color.Yellow;
        Assert.Equal(Color.Green, bitmap.Palette.Entries[0]);

        var property = new PropertyItem(0x010E, type: 2, value: [1, 2, 3]);
        bitmap.SetPropertyItem(property);
        property.Value[0] = 99;
        PropertyItem firstRead = bitmap.GetPropertyItem(0x010E);
        Assert.Equal(new byte[] { 1, 2, 3 }, firstRead.Value);
        firstRead.Value[1] = 88;
        Assert.Equal(new byte[] { 1, 2, 3 }, bitmap.PropertyItems[0].Value);
        Assert.Equal(new[] { 0x010E }, bitmap.PropertyIdList);

        using var clone = Assert.IsType<Bitmap>(bitmap.Clone());
        bitmap.RemovePropertyItem(0x010E);
        Assert.Empty(bitmap.PropertyItems);
        Assert.Equal(new byte[] { 1, 2, 3 }, clone.GetPropertyItem(0x010E).Value);
        Assert.Throws<ArgumentException>(() => bitmap.GetPropertyItem(0x010E));
        Assert.Throws<ArgumentException>(() => bitmap.RemovePropertyItem(0x010E));
    }

    [Fact]
    public void FixedAndOptimalPalettesHaveDeterministicCardinality()
    {
        Assert.Equal(2, new ColorPalette(PaletteType.FixedBlackAndWhite).Entries.Length);
        Assert.Equal(16, new ColorPalette(PaletteType.FixedHalftone8).Entries.Length);
        Assert.Equal(35, new ColorPalette(PaletteType.FixedHalftone27).Entries.Length);
        Assert.Equal(72, new ColorPalette(PaletteType.FixedHalftone64).Entries.Length);
        Assert.Equal(133, new ColorPalette(PaletteType.FixedHalftone125).Entries.Length);
        Assert.Equal(224, new ColorPalette(PaletteType.FixedHalftone216).Entries.Length);
        Assert.Equal(252, new ColorPalette(PaletteType.FixedHalftone252).Entries.Length);
        Assert.Equal(256, new ColorPalette(PaletteType.FixedHalftone256).Entries.Length);

        using var bitmap = new Bitmap(4, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Red);
        bitmap.SetPixel(2, 0, Color.Blue);
        bitmap.SetPixel(3, 0, Color.Transparent);

        ColorPalette optimal = ColorPalette.CreateOptimalPalette(3, useTransparentColor: true, bitmap);
        Assert.Equal(3, optimal.Entries.Length);
        Assert.Equal(Color.Transparent, optimal.Entries[0]);
        Assert.Contains(optimal.Entries, color => color.ToArgb() == Color.Red.ToArgb());
        Assert.Contains(optimal.Entries, color => color.ToArgb() == Color.Blue.ToArgb());
        Assert.Equal((int)PaletteFlags.HasAlpha, optimal.Flags);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ColorPalette.CreateOptimalPalette(0, useTransparentColor: false, bitmap));
    }

    [Fact]
    public void OptimalPaletteQuantizationIsDeterministicAndAllocationBounded()
    {
        using var bitmap = new Bitmap(64, 64);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int red = (x * 255) / (bitmap.Width - 1);
                int green = (y * 255) / (bitmap.Height - 1);
                int blue = ((x ^ y) * 255) / (bitmap.Width - 1);
                bitmap.SetPixel(x, y, Color.FromArgb(255, red, green, blue));
            }
        }

        _ = ColorPalette.CreateOptimalPalette(16, useTransparentColor: false, bitmap);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ColorPalette first = ColorPalette.CreateOptimalPalette(16, useTransparentColor: false, bitmap);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        ColorPalette second = ColorPalette.CreateOptimalPalette(16, useTransparentColor: false, bitmap);

        Assert.Equal(16, first.Entries.Length);
        Assert.Equal(first.Entries.Select(color => color.ToArgb()), second.Entries.Select(color => color.ToArgb()));
        Assert.InRange(allocated, 400_000, 600_000);
    }

    [Fact]
    public void ImageAttributesAdjustPaletteUsingSnapshottedRemapAndMatrixState()
    {
        var remapPalette = new ColorPalette(Color.Red, Color.Green);
        using var remapAttributes = new ImageAttributes();
        remapAttributes.SetRemapTable(new ColorMap { OldColor = Color.Red, NewColor = Color.Blue });
        remapAttributes.GetAdjustedPalette(remapPalette, ColorAdjustType.Bitmap);
        Assert.Equal(Color.Blue.ToArgb(), remapPalette.Entries[0].ToArgb());
        Assert.Equal(Color.Green.ToArgb(), remapPalette.Entries[1].ToArgb());

        var matrixPalette = new ColorPalette(Color.Red);
        using var matrixAttributes = new ImageAttributes();
        matrixAttributes.SetColorMatrix(new ColorMatrix { Matrix00 = 0.5f });
        matrixAttributes.GetAdjustedPalette(matrixPalette, ColorAdjustType.Bitmap);
        Assert.Equal(Color.FromArgb(255, 128, 0, 0).ToArgb(), matrixPalette.Entries[0].ToArgb());
    }

    [Fact]
    public void MatrixParallelogramAndCompositionMapPointsExactly()
    {
        using var parallelogram = new Matrix(
            new RectangleF(10f, 20f, 4f, 2f),
            new PointF(1f, 2f),
            new PointF(9f, 2f),
            new PointF(3f, 8f));
        PointF[] corners =
        [
            new(10f, 20f),
            new(14f, 20f),
            new(10f, 22f),
            new(14f, 22f)
        ];
        parallelogram.TransformPoints(corners);

        Assert.Equal(new PointF(1f, 2f), corners[0]);
        Assert.Equal(new PointF(9f, 2f), corners[1]);
        Assert.Equal(new PointF(3f, 8f), corners[2]);
        Assert.Equal(new PointF(11f, 8f), corners[3]);

        using var appended = new Matrix();
        appended.Translate(10f, 0f);
        appended.Scale(2f, 3f, MatrixOrder.Append);
        PointF[] appendedPoint = [new(1f, 1f)];
        appended.TransformPoints(appendedPoint);
        Assert.Equal(new PointF(22f, 3f), appendedPoint[0]);

        using var prepended = new Matrix();
        prepended.Translate(10f, 0f);
        prepended.Scale(2f, 3f, MatrixOrder.Prepend);
        PointF[] prependedPoint = [new(1f, 1f)];
        prepended.TransformPoints(prependedPoint);
        Assert.Equal(new PointF(12f, 3f), prependedPoint[0]);
    }

    [Fact]
    public void MatrixSpanVectorInverseAndOwnershipContractsAreFunctional()
    {
        using var matrix = new Matrix();
        matrix.RotateAt(90f, new PointF(5f, 7f), MatrixOrder.Append);

        PointF[] pivot = [new(5f, 7f)];
        matrix.TransformPoints((ReadOnlySpan<PointF>)pivot);
        Assert.InRange(pivot[0].X, 4.9999f, 5.0001f);
        Assert.InRange(pivot[0].Y, 6.9999f, 7.0001f);

        PointF[] vector = [new(1f, 0f)];
        matrix.TransformVectors((ReadOnlySpan<PointF>)vector);
        Assert.InRange(vector[0].X, -0.0001f, 0.0001f);
        Assert.InRange(vector[0].Y, 0.9999f, 1.0001f);

        PointF[] original = [new(3f, 4f)];
        PointF[] roundTrip = [original[0]];
        matrix.TransformPoints(roundTrip);
        using Matrix inverse = matrix.Clone();
        inverse.Invert();
        inverse.TransformPoints(roundTrip);
        Assert.InRange(roundTrip[0].X, original[0].X - 0.0001f, original[0].X + 0.0001f);
        Assert.InRange(roundTrip[0].Y, original[0].Y - 0.0001f, original[0].Y + 0.0001f);

        Matrix3x2 replacement = Matrix3x2.CreateTranslation(11f, 13f);
        matrix.MatrixElements = replacement;
        float[] elements = matrix.Elements;
        elements[4] = 999f;
        Assert.Equal(replacement, matrix.MatrixElements);
        Assert.Equal(11f, matrix.OffsetX);
        Assert.Equal(13f, matrix.OffsetY);

        using var singular = new Matrix(0f, 0f, 0f, 0f, 0f, 0f);
        Assert.False(singular.IsInvertible);
        Assert.Throws<ArgumentException>(() => singular.Invert());
        Assert.Throws<ArgumentException>(() => matrix.Scale(1f, 1f, (MatrixOrder)42));

        matrix.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = matrix.Elements);
    }

    [Fact]
    public void MatrixPointBatchTransformAllocatesNothingAfterWarmup()
    {
        using var matrix = new Matrix();
        matrix.Rotate(0.125f);
        var points = new PointF[1024];
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = new PointF(index, -index);
        }

        matrix.TransformPoints((ReadOnlySpan<PointF>)points);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 64; iteration++)
        {
            matrix.TransformPoints((ReadOnlySpan<PointF>)points);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void BlendContainersUseTheDocumentedMutableArrayOwnership()
    {
        var blend = new Blend(3);
        var factors = new[] { 0f, 0.75f, 1f };
        var positions = new[] { 0f, 0.25f, 1f };
        blend.Factors = factors;
        blend.Positions = positions;
        Assert.Same(factors, blend.Factors);
        Assert.Same(positions, blend.Positions);

        var colorBlend = new ColorBlend(3);
        Color[] colors = [Color.Red, Color.Green, Color.Blue];
        colorBlend.Colors = colors;
        colorBlend.Positions = positions;
        Assert.Same(colors, colorBlend.Colors);
        Assert.Same(positions, colorBlend.Positions);
        Assert.Throws<OverflowException>(() => new Blend(-1));
        Assert.Throws<OverflowException>(() => new ColorBlend(-1));
    }

    [Fact]
    public void LinearGradientStateClonesAndLowersToTypedNativeStops()
    {
        using var brush = new LinearGradientBrush(
            new RectangleF(10f, 20f, 100f, 50f),
            Color.Red,
            Color.Blue,
            angle: 0f,
            isAngleScaleable: true)
        {
            GammaCorrection = true,
            WrapMode = WrapMode.TileFlipX
        };

        Assert.Equal(new RectangleF(10f, 20f, 100f, 50f), brush.Rectangle);
        Assert.Equal(new Vector2(10f, 45f), brush.StartPoint);
        Assert.Equal(new Vector2(110f, 45f), brush.EndPoint);

        var interpolation = new ColorBlend(3)
        {
            Colors = [Color.Red, Color.Green, Color.Blue],
            Positions = [0f, 0.25f, 1f]
        };
        brush.InterpolationColors = interpolation;
        interpolation.Colors[0] = Color.Black;
        interpolation.Positions[1] = 0.75f;

        using var transform = new Matrix();
        transform.Translate(5f, 7f);
        brush.Transform = transform;
        transform.Reset();

        ColorBlend firstRead = brush.InterpolationColors;
        firstRead.Colors[0] = Color.Magenta;
        firstRead.Positions[1] = 0.5f;
        ColorBlend secondRead = brush.InterpolationColors;
        Assert.Equal(Color.Red.ToArgb(), secondRead.Colors[0].ToArgb());
        Assert.Equal(0.25f, secondRead.Positions[1]);
        using (Matrix returnedTransform = brush.Transform)
        {
            Assert.Equal(Matrix3x2.CreateTranslation(5f, 7f), returnedTransform.MatrixElements);
        }

        var native = Assert.IsType<ProGPU.Vector.LinearGradientBrush>(brush.ToProGpuBrush());
        Assert.Equal(ProGPU.Vector.GradientSpreadMethod.Reflect, native.SpreadMethod);
        Assert.Equal(ProGPU.Vector.GradientColorInterpolationMode.ScRgbLinearInterpolation, native.ColorInterpolationMode);
        Assert.Equal(new[] { 0f, 0.25f, 1f }, native.Stops.Select(stop => stop.Offset));
        Assert.Equal(1f, native.Stops[0].Color.X);
        Assert.Equal(0f, native.Stops[0].Color.Z);
        Assert.Equal(-5f, native.CoordinateTransform.M41);
        Assert.Equal(-7f, native.CoordinateTransform.M42);

        using var clone = Assert.IsType<LinearGradientBrush>(brush.Clone());
        brush.InterpolationColors = new ColorBlend(2)
        {
            Colors = [Color.Black, Color.White],
            Positions = [0f, 1f]
        };
        Assert.Equal(Color.Red.ToArgb(), clone.InterpolationColors.Colors[0].ToArgb());
        Assert.Equal(0.25f, clone.InterpolationColors.Positions[1]);
    }

    [Fact]
    public void ScalableLinearGradientAngleAccountsForRectangleAspectRatio()
    {
        var rectangle = new RectangleF(0f, 0f, 200f, 100f);
        using var fixedAngle = new LinearGradientBrush(
            rectangle,
            Color.Black,
            Color.White,
            angle: 45f,
            isAngleScaleable: false);
        using var scalableAngle = new LinearGradientBrush(
            rectangle,
            Color.Black,
            Color.White,
            angle: 45f,
            isAngleScaleable: true);

        Vector2 fixedDirection = Vector2.Normalize(fixedAngle.EndPoint - fixedAngle.StartPoint);
        Vector2 scalableDirection = Vector2.Normalize(scalableAngle.EndPoint - scalableAngle.StartPoint);

        Assert.InRange(fixedDirection.Y / fixedDirection.X, 0.9999f, 1.0001f);
        Assert.InRange(scalableDirection.Y / scalableDirection.X, 1.9999f, 2.0001f);
    }

    [Fact]
    public void LinearGradientFalloffFunctionsAreRenderedAndValidated()
    {
        using var brush = new LinearGradientBrush(
            new PointF(0f, 0f),
            new PointF(100f, 0f),
            Color.Black,
            Color.White);

        brush.SetBlendTriangularShape(0.25f, 0.8f);
        Blend triangular = Assert.IsType<Blend>(brush.Blend);
        Assert.Equal(new[] { 0f, 0.25f, 1f }, triangular.Positions);
        Assert.Equal(new[] { 0f, 0.8f, 0f }, triangular.Factors);
        var triangularNative = Assert.IsType<ProGPU.Vector.LinearGradientBrush>(brush.ToProGpuBrush());
        Assert.Equal(3, triangularNative.Stops.Length);
        Assert.InRange(triangularNative.Stops[1].Color.X, 0.7999f, 0.8001f);

        brush.SetSigmaBellShape(0.25f, 0.6f);
        Blend bell = Assert.IsType<Blend>(brush.Blend);
        int focusIndex = Array.IndexOf(bell.Positions, 0.25f);
        Assert.True(focusIndex >= 0);
        Assert.Equal(0f, bell.Factors[0]);
        Assert.InRange(bell.Factors[focusIndex], 0.5999f, 0.6001f);
        Assert.Equal(0f, bell.Factors[^1]);
        Assert.Equal(bell.Positions.Length, Assert.IsType<ProGPU.Vector.LinearGradientBrush>(brush.ToProGpuBrush()).Stops.Length);

        Assert.Throws<ArgumentException>(() => brush.SetBlendTriangularShape(-0.1f));
        Assert.Throws<ArgumentException>(() => brush.SetSigmaBellShape(0.5f, 1.1f));
        Assert.Throws<ArgumentException>(() => brush.LinearColors = [Color.Red]);
        Assert.Throws<ArgumentException>(() => brush.Blend = new Blend(2)
        {
            Factors = [0f, 1f],
            Positions = [0.2f, 1f]
        });
        Assert.Throws<InvalidEnumArgumentException>(() => brush.WrapMode = (WrapMode)99);

        brush.Dispose();
        Assert.Throws<ObjectDisposedException>(() => brush.ToProGpuBrush());
    }

    [Fact]
    public void EightStopLinearGradientLoweringHasBoundedAllocation()
    {
        using var brush = new LinearGradientBrush(
            new RectangleF(0f, 0f, 128f, 64f),
            Color.Black,
            Color.White,
            LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend(8)
            {
                Colors =
                [
                    Color.Black,
                    Color.Navy,
                    Color.Blue,
                    Color.Cyan,
                    Color.Lime,
                    Color.Yellow,
                    Color.Red,
                    Color.White
                ],
                Positions = [0f, 0.12f, 0.28f, 0.42f, 0.58f, 0.72f, 0.88f, 1f]
            }
        };

        _ = brush.ToProGpuBrush();
        const int iterations = 64;
        int stopCount = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            stopCount += Assert.IsType<ProGPU.Vector.LinearGradientBrush>(brush.ToProGpuBrush()).Stops.Length;
        }
        long bytesPerLowering = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        Assert.Equal(512, stopCount);
        Assert.InRange(bytesPerLowering, 288, 352);
    }

    [Fact]
    public void GraphicsPathEllipseExportsCanonicalCubicPathData()
    {
        using var path = new GraphicsPath();
        path.AddEllipse(10f, 20f, 80f, 40f);

        Assert.Equal(13, path.PointCount);
        Assert.Equal(new PointF(10f, 40f), path.PathPoints[0]);
        Assert.Equal((byte)PathPointType.Start, path.PathTypes[0]);
        Assert.All(path.PathTypes[1..^1], type => Assert.Equal((byte)PathPointType.Bezier3, type));
        Assert.Equal(
            (byte)((byte)PathPointType.Bezier3 | (byte)PathPointType.CloseSubpath),
            path.PathTypes[^1]);
        Assert.Equal(new RectangleF(10f, 20f, 80f, 40f), path.GetBounds());
        Assert.True(path.IsVisible(50f, 40f));
        Assert.False(path.IsVisible(0f, 0f));
    }

    [Fact]
    public void GraphicsPathDataRoundTripsMarkersAndCloneStateIndependently()
    {
        PointF[] points =
        [
            new(0f, 0f),
            new(10f, 0f),
            new(10f, 10f),
            new(0f, 10f)
        ];
        byte[] types =
        [
            (byte)PathPointType.Start,
            (byte)PathPointType.Line,
            (byte)((byte)PathPointType.Line | (byte)PathPointType.PathMarker),
            (byte)((byte)PathPointType.Line | (byte)PathPointType.CloseSubpath)
        ];

        using var path = new GraphicsPath(points, types, FillMode.Winding);
        using var clone = Assert.IsType<GraphicsPath>(path.Clone());
        path.Reset();

        Assert.Equal(FillMode.Winding, clone.FillMode);
        Assert.Equal(points, clone.PathPoints);
        Assert.Equal(types, clone.PathTypes);
        Assert.Equal(FillMode.Alternate, path.FillMode);
        Assert.Equal(0, path.PointCount);

        path.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = path.PointCount);
    }

    [Fact]
    public void GraphicsPathTransformAndBoundsDoNotMutateTheBoundsQuery()
    {
        using var path = new GraphicsPath();
        path.AddRectangle(new RectangleF(0f, 0f, 20f, 10f));
        using var matrix = new Matrix();
        matrix.Translate(5f, 7f);
        using var pen = new Pen(Color.Black, 4f);

        Assert.Equal(new RectangleF(3f, 5f, 24f, 14f), path.GetBounds(matrix, pen));
        Assert.Equal(new RectangleF(0f, 0f, 20f, 10f), path.GetBounds());

        path.Transform(matrix);
        Assert.Equal(new RectangleF(5f, 7f, 20f, 10f), path.GetBounds());
        Assert.Equal(new PointF(5f, 17f), path.GetLastPoint());
    }

    [Fact]
    public void GraphicsPathSpanExportAllocatesNothingAfterWarmup()
    {
        using var path = new GraphicsPath();
        path.AddClosedCurve(
            [new PointF(0f, 0f), new PointF(20f, 0f), new PointF(20f, 20f), new PointF(0f, 20f)],
            0.5f);
        var points = new PointF[path.PointCount];
        var types = new byte[path.PointCount];
        path.GetPathPoints(points);
        path.GetPathTypes(types);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 64; iteration++)
        {
            path.GetPathPoints(points);
            path.GetPathTypes(types);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(13, path.PointCount);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GraphicsPathReverseAndFlattenPreserveGeometryWithoutCurveTypes()
    {
        using var path = new GraphicsPath();
        path.AddBezier(
            new PointF(0f, 0f),
            new PointF(0f, 20f),
            new PointF(20f, 20f),
            new PointF(20f, 0f));
        RectangleF originalBounds = path.GetBounds();

        path.Reverse();
        Assert.Equal(new PointF(20f, 0f), path.PathPoints[0]);
        Assert.Equal(new PointF(0f, 0f), path.GetLastPoint());
        Assert.Equal(originalBounds, path.GetBounds());

        path.Flatten(null, 0.1f);
        Assert.True(path.PointCount > 4);
        Assert.All(path.PathTypes[1..], type =>
            Assert.Equal((byte)PathPointType.Line, (byte)(type & (byte)PathPointType.PathTypeMask)));
        RectangleF flattenedBounds = path.GetBounds();
        Assert.InRange(flattenedBounds.Left, originalBounds.Left - 0.001f, originalBounds.Left + 0.001f);
        Assert.InRange(flattenedBounds.Right, originalBounds.Right - 0.001f, originalBounds.Right + 0.001f);
    }

    [Fact]
    public void GraphicsPathIteratorEnumeratesMarkersSubpathsAndPathTypes()
    {
        using var source = new GraphicsPath(FillMode.Winding);
        source.AddLine(0f, 0f, 10f, 0f);
        source.SetMarkers();
        source.AddBezier(10f, 0f, 12f, 4f, 18f, 4f, 20f, 0f);
        source.CloseFigure();
        source.AddRectangle(new RectangleF(30f, 10f, 5f, 7f));
        using var iterator = new GraphicsPathIterator(source);

        Assert.Equal(9, iterator.Count);
        Assert.Equal(2, iterator.SubpathCount);
        Assert.True(iterator.HasCurve());

        PointF[] points = null!;
        byte[] types = null!;
        Assert.Equal(9, iterator.Enumerate(ref points, ref types));
        Assert.Equal(source.PathPoints, points);
        Assert.Equal(source.PathTypes, types);

        Assert.Equal(2, iterator.NextMarker(out int markerStart, out int markerEnd));
        Assert.Equal((0, 1), (markerStart, markerEnd));
        using var markerPath = new GraphicsPath();
        Assert.Equal(7, iterator.NextMarker(markerPath));
        Assert.Equal(FillMode.Winding, markerPath.FillMode);
        Assert.Equal(new PointF(10f, 0f), markerPath.PathPoints[0]);
        Assert.Equal(new PointF(30f, 10f), markerPath.PathPoints[4]);

        iterator.Rewind();
        using var subpath = new GraphicsPath();
        Assert.Equal(5, iterator.NextSubpath(subpath, out bool isClosed));
        Assert.True(isClosed);
        Assert.Equal(5, subpath.PointCount);
        Assert.Equal(4, iterator.NextSubpath(out int subpathStart, out int subpathEnd, out isClosed));
        Assert.Equal((5, 8), (subpathStart, subpathEnd));
        Assert.True(isClosed);

        iterator.Rewind();
        Assert.Equal(1, iterator.NextPathType(out byte pathType, out int typeStart, out int typeEnd));
        Assert.Equal((byte)PathPointType.Start, pathType);
        Assert.Equal((0, 0), (typeStart, typeEnd));
        Assert.Equal(1, iterator.NextPathType(out pathType, out typeStart, out typeEnd));
        Assert.Equal((byte)PathPointType.Line, pathType);
        Assert.Equal(3, iterator.NextPathType(out pathType, out typeStart, out typeEnd));
        Assert.Equal((byte)PathPointType.Bezier3, pathType);
    }

    [Fact]
    public void GraphicsPathIteratorSpanEnumerationAllocatesNothingAfterWarmup()
    {
        using var source = new GraphicsPath();
        source.AddEllipse(0f, 0f, 40f, 20f);
        using var iterator = new GraphicsPathIterator(source);
        var points = new PointF[iterator.Count];
        var types = new byte[iterator.Count];
        iterator.Enumerate(points, types);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 64; iteration++)
        {
            iterator.Enumerate(points, types);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        iterator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = iterator.Count);
    }
}
