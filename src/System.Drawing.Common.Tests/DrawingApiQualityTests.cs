using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
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
}
