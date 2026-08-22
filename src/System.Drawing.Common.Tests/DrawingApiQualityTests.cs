using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
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
        Assert.Equal(1, Image.GetPixelFormatSize(PixelFormat.Format1bppIndexed));
        Assert.Equal(4, Image.GetPixelFormatSize(PixelFormat.Format4bppIndexed));
        Assert.Equal(16, Image.GetPixelFormatSize(PixelFormat.Format16bppGrayScale));
        Assert.Equal(48, Image.GetPixelFormatSize(PixelFormat.Format48bppRgb));
        Assert.Equal(64, Image.GetPixelFormatSize(PixelFormat.Format64bppArgb));
        Assert.Equal(198659, (int)PixelFormat.Format8bppIndexed);
        Assert.Equal(135174, (int)PixelFormat.Format16bppRgb565);
    }

    [Fact]
    public void BitmapScan0ConstructorDecodesTypedPixelRows()
    {
        byte[] source = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0, 0];
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                width: 2,
                height: 1,
                stride: 8,
                format: PixelFormat.Format24bppRgb,
                scan0: handle.AddrOfPinnedObject());

            Assert.Equal(Color.FromArgb(0x30, 0x20, 0x10).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
            Assert.Equal(Color.FromArgb(0x60, 0x50, 0x40).ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
            Assert.Equal(PixelFormat.Format24bppRgb, bitmap.PixelFormat);
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void BitmapScan0ConstructorHonorsNegativeStrideFromFirstLogicalRow()
    {
        byte[] source = [255, 0, 0, 255, 0, 0, 255, 255];
        GCHandle handle = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                width: 1,
                height: 2,
                stride: -4,
                format: PixelFormat.Format32bppArgb,
                scan0: IntPtr.Add(handle.AddrOfPinnedObject(), 4));

            Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(0, 1).ToArgb());
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void CallerOwnedLockBitsBufferRoundTripsWithoutOwnershipTransfer()
    {
        using var bitmap = new Bitmap(2, 1);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Blue);

        byte[] buffer = new byte[8];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var data = new BitmapData
            {
                Scan0 = handle.AddrOfPinnedObject(),
                Stride = 8
            };
            BitmapData returned = bitmap.LockBits(
                new Rectangle(0, 0, 2, 1),
                ImageLockMode.ReadWrite | ImageLockMode.UserInputBuffer,
                PixelFormat.Format32bppArgb,
                data);

            Assert.Same(data, returned);
            Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, buffer);
            buffer[0] = 0;
            buffer[1] = 255;
            buffer[2] = 0;
            buffer[3] = 255;
            bitmap.UnlockBits(data);

            Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(1, 0).ToArgb());
            Assert.Throws<ArgumentException>(() => bitmap.UnlockBits(data));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void PackedAndHighDepthLockBitsFormatsRoundTripThroughManagedRgba()
    {
        PixelFormat[] formats =
        [
            PixelFormat.Format16bppArgb1555,
            PixelFormat.Format16bppGrayScale,
            PixelFormat.Format16bppRgb555,
            PixelFormat.Format16bppRgb565,
            PixelFormat.Format24bppRgb,
            PixelFormat.Format32bppRgb,
            PixelFormat.Format32bppArgb,
            PixelFormat.Format32bppPArgb,
            PixelFormat.Format48bppRgb,
            PixelFormat.Format64bppArgb,
            PixelFormat.Format64bppPArgb
        ];

        foreach (PixelFormat format in formats)
        {
            using var bitmap = new Bitmap(1, 1);
            bitmap.SetPixel(0, 0, Color.FromArgb(255, 123, 65, 31));
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, 1, 1), ImageLockMode.ReadWrite, format);
            bitmap.UnlockBits(data);
            Color roundTrip = bitmap.GetPixel(0, 0);

            if (format == PixelFormat.Format16bppGrayScale)
            {
                Assert.Equal(roundTrip.R, roundTrip.G);
                Assert.Equal(roundTrip.G, roundTrip.B);
            }
            else
            {
                Assert.InRange(Math.Abs(roundTrip.R - 123), 0, 8);
                Assert.InRange(Math.Abs(roundTrip.G - 65), 0, 8);
                Assert.InRange(Math.Abs(roundTrip.B - 31), 0, 8);
            }
        }
    }

    [Fact]
    public void BitmapCropCloneMaterializesTheRequestedPixelFormat()
    {
        using var bitmap = new Bitmap(3, 2);
        bitmap.SetPixel(1, 0, Color.FromArgb(255, 123, 65, 31));
        bitmap.SetPixel(1, 1, Color.Blue);

        using Bitmap clone = bitmap.Clone(new Rectangle(1, 0, 1, 2), PixelFormat.Format16bppRgb565);

        Assert.Equal(new Size(1, 2), clone.Size);
        Assert.Equal(PixelFormat.Format16bppRgb565, clone.PixelFormat);
        Color quantized = clone.GetPixel(0, 0);
        Assert.InRange(Math.Abs(quantized.R - 123), 0, 8);
        Assert.InRange(Math.Abs(quantized.G - 65), 0, 4);
        Assert.InRange(Math.Abs(quantized.B - 31), 0, 8);
        Assert.Equal(Color.Blue.ToArgb(), clone.GetPixel(0, 1).ToArgb());
    }

    [Fact]
    public void CallerOwnedReadOnlyLockBitsHasBoundedWarmedAllocation()
    {
        using var bitmap = new Bitmap(64, 64);
        byte[] buffer = new byte[64 * 64 * 4];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var data = new BitmapData { Scan0 = handle.AddrOfPinnedObject(), Stride = 64 * 4 };
            Rectangle rectangle = new(0, 0, 64, 64);
            bitmap.LockBits(
                rectangle,
                ImageLockMode.ReadOnly | ImageLockMode.UserInputBuffer,
                PixelFormat.Format32bppArgb,
                data);
            bitmap.UnlockBits(data);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 32; iteration++)
            {
                bitmap.LockBits(
                    rectangle,
                    ImageLockMode.ReadOnly | ImageLockMode.UserInputBuffer,
                    PixelFormat.Format32bppArgb,
                    data);
                bitmap.UnlockBits(data);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(allocated, 0, 512);
        }
        finally
        {
            handle.Free();
        }
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

    [Fact]
    public void GraphicsPathOutlineHitTestingHonorsWidthCapsAndDashIntervals()
    {
        using var path = new GraphicsPath();
        path.AddLine(0f, 0f, 20f, 0f);
        using var pen = new Pen(Color.Black, 2f);

        Assert.True(path.IsOutlineVisible(2f, 0.99f, pen));
        Assert.False(path.IsOutlineVisible(2f, 1.01f, pen));
        Assert.False(path.IsOutlineVisible(-0.01f, 0f, pen));

        pen.StartCap = LineCap.Square;
        Assert.True(path.IsOutlineVisible(-0.99f, 0f, pen));
        Assert.False(path.IsOutlineVisible(-1.01f, 0f, pen));

        pen.StartCap = LineCap.Flat;
        pen.DashStyle = DashStyle.Custom;
        pen.DashPattern = [2f, 2f];
        Assert.True(path.IsOutlineVisible(new PointF(2f, 0f), pen));
        Assert.False(path.IsOutlineVisible(new Point(5, 0), pen));
        Assert.True(path.IsOutlineVisible(9, 0, pen));

        using var widened = (GraphicsPath)path.Clone();
        widened.Widen(pen);
        Assert.True(widened.IsVisible(2f, 0f));
        Assert.False(widened.IsVisible(5f, 0f));
        Assert.True(widened.IsVisible(9f, 0f));
    }

    [Fact]
    public void GraphicsPathOutlineHitTestingHonorsJoinsAndCurves()
    {
        using var corner = new GraphicsPath();
        corner.AddLines([new PointF(0f, 10f), new PointF(0f, 0f), new PointF(10f, 0f)]);
        using var pen = new Pen(Color.Black, 4f) { LineJoin = LineJoin.Round };

        Assert.True(corner.IsOutlineVisible(-1.3f, -1.3f, pen));
        Assert.False(corner.IsOutlineVisible(-1.9f, -1.9f, pen));

        using var ellipse = new GraphicsPath();
        ellipse.AddEllipse(0f, 0f, 20f, 10f);
        Assert.True(ellipse.IsOutlineVisible(10f, 0.5f, pen));
        Assert.False(ellipse.IsOutlineVisible(10f, 5f, pen));
    }

    [Fact]
    public void GraphicsPathWidenProducesFilledGeometryWithTransformAndHairlineFloor()
    {
        using var path = new GraphicsPath();
        path.AddLine(0f, 0f, 10f, 0f);
        using var pen = new Pen(Color.Black, 0.25f);
        using var matrix = new Matrix();
        matrix.Translate(5f, 7f);

        path.Widen(pen, matrix, 0.1f);

        Assert.Equal(FillMode.Winding, path.FillMode);
        Assert.Equal(new RectangleF(5f, 6.5f, 10f, 1f), path.GetBounds());
        Assert.True(path.IsVisible(10f, 7f));
        Assert.False(path.IsVisible(10f, 7.51f));
        Assert.All(path.PathTypes, type => Assert.NotEqual((byte)PathPointType.Bezier3, (byte)(type & (byte)PathPointType.PathTypeMask)));
    }

    [Fact]
    public void GraphicsPathWidenHandlesEmptyPathsAndRejectsNullPens()
    {
        using var empty = new GraphicsPath(FillMode.Winding);
        using var pen = new Pen(Color.Black, 3f);

        empty.Widen(pen);

        Assert.Equal(0, empty.PointCount);
        Assert.Equal(FillMode.Winding, empty.FillMode);
        Assert.Throws<ArgumentNullException>(() => empty.Widen(null!));
        Assert.Throws<ArgumentNullException>(() => empty.IsOutlineVisible(0f, 0f, null!));
    }

    [Fact]
    public void GraphicsPathPerspectiveWarpMapsRectangleCornersAndAppliesMatrixFirst()
    {
        using var path = new GraphicsPath(FillMode.Winding);
        path.AddRectangle(new RectangleF(0f, 0f, 10f, 10f));
        using var matrix = new Matrix();
        matrix.Translate(10f, 20f);
        PointF[] destination =
        [
            new PointF(2f, 3f),
            new PointF(22f, 5f),
            new PointF(4f, 23f),
            new PointF(18f, 19f),
        ];

        path.Warp(destination, new RectangleF(10f, 20f, 10f, 10f), matrix);

        Assert.Equal(FillMode.Winding, path.FillMode);
        Assert.Equal(destination[0], path.PathPoints[0]);
        Assert.Equal(destination[1], path.PathPoints[1]);
        Assert.Equal(destination[3], path.PathPoints[2]);
        Assert.Equal(destination[2], path.PathPoints[3]);
        Assert.Equal((byte)PathPointType.CloseSubpath, (byte)(path.PathTypes[^1] & (byte)PathPointType.CloseSubpath));
    }

    [Fact]
    public void GraphicsPathBilinearWarpAdaptivelySubdividesCurvedDiagonal()
    {
        PointF[] destination =
        [
            new PointF(0f, 0f),
            new PointF(10f, 0f),
            new PointF(0f, 10f),
            new PointF(20f, 20f),
        ];
        using var bilinear = new GraphicsPath();
        bilinear.AddLine(0f, 0f, 10f, 10f);
        using var perspective = Assert.IsType<GraphicsPath>(bilinear.Clone());

        bilinear.Warp(destination, new RectangleF(0f, 0f, 10f, 10f), null, WarpMode.Bilinear, 0.1f);
        perspective.Warp(destination, new RectangleF(0f, 0f, 10f, 10f), null, WarpMode.Perspective, 0.1f);

        Assert.True(bilinear.PointCount > 2);
        Assert.Contains(bilinear.PathPoints, point => MathF.Abs(point.X - 7.5f) < 0.001f && MathF.Abs(point.Y - 7.5f) < 0.001f);
        Assert.Equal(2, perspective.PointCount);
        Assert.Equal(new PointF(20f, 20f), bilinear.GetLastPoint());
        Assert.All(bilinear.PathTypes[1..], type =>
            Assert.Equal((byte)PathPointType.Line, (byte)(type & (byte)PathPointType.PathTypeMask)));
    }

    [Fact]
    public void GraphicsPathWarpSupportsImpliedParallelogramAndValidatesInputs()
    {
        PointF[] destination = [new PointF(5f, 7f), new PointF(25f, 7f), new PointF(5f, 27f)];
        using var path = new GraphicsPath();
        path.AddRectangle(new RectangleF(0f, 0f, 10f, 10f));

        path.Warp(destination, new RectangleF(0f, 0f, 10f, 10f), null, WarpMode.Bilinear);

        Assert.Equal(new PointF(25f, 27f), path.PathPoints[2]);
        Assert.Throws<ArgumentNullException>(() => path.Warp(null!, new RectangleF(0f, 0f, 1f, 1f)));
        Assert.Throws<ArgumentException>(() => path.Warp([], new RectangleF(0f, 0f, 1f, 1f)));
        Assert.Throws<ArgumentException>(() => path.Warp(destination, RectangleF.Empty));
        Assert.Throws<ArgumentException>(() => path.Warp(destination, new RectangleF(0f, 0f, 1f, 1f), null, (WarpMode)42));

        using var empty = new GraphicsPath();
        empty.Warp(destination, new RectangleF(0f, 0f, 10f, 10f));
        Assert.Equal(0, empty.PointCount);
    }

    [Fact]
    public void GraphicsPathBilinearWarpHasBoundedAllocation()
    {
        using var source = new GraphicsPath();
        for (int index = 0; index < 16; index++)
        {
            source.AddEllipse(index * 8f, index * 4f, 64f, 32f);
        }
        PointF[] destination =
        [
            new PointF(0f, 0f),
            new PointF(256f, 8f),
            new PointF(12f, 160f),
            new PointF(220f, 192f),
        ];
        RectangleF sourceRectangle = source.GetBounds();
        using (var warmup = Assert.IsType<GraphicsPath>(source.Clone()))
        {
            warmup.Warp(destination, sourceRectangle, null, WarpMode.Bilinear, 0.25f);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int pointCount;
        using (var warped = Assert.IsType<GraphicsPath>(source.Clone()))
        {
            warped.Warp(destination, sourceRectangle, null, WarpMode.Bilinear, 0.25f);
            pointCount = warped.PointCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pointCount > source.PointCount);
        Assert.InRange(allocated, 0, 72_000);
    }

    [Fact]
    public void GraphicsPathAddStringMaterializesAllOfficialLayoutOverloads()
    {
        using var point = new GraphicsPath();
        using var pointF = new GraphicsPath();
        using var rectangle = new GraphicsPath();
        using var rectangleF = new GraphicsPath();
        using var format = StringFormat.GenericDefault;

        point.AddString("mono", FontFamily.GenericMonospace, (int)FontStyle.Regular, 18f, new Point(10, 12), format);
        pointF.AddString("mono", FontFamily.GenericMonospace, (int)FontStyle.Regular, 18f, new PointF(10f, 12f), format);
        rectangle.AddString("mono", FontFamily.GenericMonospace, (int)FontStyle.Regular, 18f, new Rectangle(10, 12, 120, 40), format);
        rectangleF.AddString("mono", FontFamily.GenericMonospace, (int)FontStyle.Regular, 18f, new RectangleF(10f, 12f, 120f, 40f), format);

        Assert.True(point.PointCount > 0);
        Assert.Equal(point.PointCount, pointF.PointCount);
        Assert.True(rectangle.PointCount > 0);
        Assert.Equal(rectangle.PointCount, rectangleF.PointCount);
        Assert.True(point.GetBounds().Left >= 10f);
        Assert.True(point.GetBounds().Top >= 12f);
    }

    [Fact]
    public void GraphicsPathAddStringUsesShapedAlignmentWrappingAndDecorations()
    {
        using var near = new GraphicsPath();
        using var far = new GraphicsPath();
        using var decorated = new GraphicsPath();
        using var nearFormat = StringFormat.GenericTypographic;
        using var farFormat = StringFormat.GenericTypographic;
        farFormat.Alignment = StringAlignment.Far;
        var layout = new RectangleF(5f, 7f, 180f, 80f);

        near.AddString("office", FontFamily.GenericSansSerif, (int)FontStyle.Regular, 24f, layout, nearFormat);
        far.AddString("office", FontFamily.GenericSansSerif, (int)FontStyle.Regular, 24f, layout, farFormat);
        decorated.AddString(
            "office",
            FontFamily.GenericSansSerif,
            (int)(FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout),
            24f,
            layout,
            nearFormat);

        Assert.True(far.GetBounds().Left > near.GetBounds().Left);
        Assert.True(decorated.PointCount > near.PointCount);
        Assert.Contains(near.PathTypes, type =>
            (type & (byte)PathPointType.PathTypeMask) == (byte)PathPointType.Bezier3);

        using var wrapped = new GraphicsPath();
        wrapped.AddString("word word word", FontFamily.GenericSansSerif, 0, 20f, new RectangleF(0f, 0f, 45f, 200f), nearFormat);
        Assert.True(wrapped.GetBounds().Height > near.GetBounds().Height);
    }

    [Fact]
    public void GraphicsPathAddStringHandlesEmptyNegativeAndInvalidArguments()
    {
        using var path = new GraphicsPath();
        path.AddString(string.Empty, FontFamily.GenericSansSerif, 0, 16f, PointF.Empty, null);
        Assert.Equal(0, path.PointCount);

        path.AddString("A", FontFamily.GenericSansSerif, 0, -16f, PointF.Empty, null);
        Assert.True(path.PointCount > 0);
        Assert.Throws<ArgumentNullException>(() => path.AddString(null!, FontFamily.GenericSansSerif, 0, 16f, PointF.Empty, null));
        Assert.Throws<ArgumentNullException>(() => path.AddString("A", null!, 0, 16f, PointF.Empty, null));
        Assert.Throws<ArgumentException>(() => path.AddString("A", FontFamily.GenericSansSerif, 0, 0f, PointF.Empty, null));
    }

    [Fact]
    public void GraphicsPathAddStringHasBoundedWarmAllocation()
    {
        using var format = StringFormat.GenericTypographic;
        using (var warmup = new GraphicsPath())
        {
            warmup.AddString("LibreWinForms", FontFamily.GenericSansSerif, 0, 24f, PointF.Empty, format);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int pointCount;
        using (var path = new GraphicsPath())
        {
            path.AddString("LibreWinForms", FontFamily.GenericSansSerif, 0, 24f, PointF.Empty, format);
            pointCount = path.PointCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pointCount > 0);
        Assert.InRange(allocated, 0, 24_000);
    }

    [Fact]
    public void GraphicsPathLineOutlineQueryHasBoundedAllocation()
    {
        using var path = new GraphicsPath();
        path.AddLines([new PointF(0f, 0f), new PointF(20f, 0f), new PointF(20f, 20f)]);
        using var pen = new Pen(Color.Black, 3f) { LineJoin = LineJoin.Round };
        _ = path.IsOutlineVisible(10f, 1f, pen);

        const int iterations = 256;
        int hits = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            if (path.IsOutlineVisible(10f, 1f, pen))
            {
                hits++;
            }
        }
        long bytesPerQuery = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;

        Assert.Equal(iterations, hits);
        Assert.InRange(bytesPerQuery, 0, 256);
    }

    [Fact]
    public void GraphicsPathCurveWideningHasBoundedAllocation()
    {
        using var path = new GraphicsPath();
        for (int index = 0; index < 16; index++)
        {
            path.AddEllipse(index * 8f, index * 4f, 64f, 32f);
        }
        using var pen = new Pen(Color.Black, 3f) { LineJoin = LineJoin.Round };
        using (var warmup = (GraphicsPath)path.Clone())
        {
            warmup.Widen(pen);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int pointCount;
        using (var widened = (GraphicsPath)path.Clone())
        {
            widened.Widen(pen);
            pointCount = widened.PointCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(pointCount > path.PointCount);
        Assert.InRange(allocated, 0, 280_000);
    }
}
