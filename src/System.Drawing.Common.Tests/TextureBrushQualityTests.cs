using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ProGPU.Scene;
using Xunit;

namespace System.Drawing.Tests;

public sealed class TextureBrushQualityTests
{
    [Theory]
    [InlineData(WrapMode.Tile)]
    [InlineData(WrapMode.TileFlipX)]
    [InlineData(WrapMode.TileFlipY)]
    [InlineData(WrapMode.TileFlipXY)]
    [InlineData(WrapMode.Clamp)]
    public void ConstructorsSnapshotImageAndPreserveWrapMode(WrapMode wrapMode)
    {
        using var source = CreateQuadBitmap();
        using var brush = new TextureBrush(source, wrapMode);
        source.SetPixel(0, 0, Color.Purple);

        using var first = Assert.IsType<Bitmap>(brush.Image);
        using var second = Assert.IsType<Bitmap>(brush.Image);

        Assert.NotSame(source, first);
        Assert.NotSame(first, second);
        Assert.Equal(Color.Red.ToArgb(), first.GetPixel(0, 0).ToArgb());
        Assert.Equal(wrapMode, brush.WrapMode);
        Assert.True(brush.Transform.IsIdentity);
    }

    [Fact]
    public void RectangleAndImageAttributesCreateOwnedAdjustedCrop()
    {
        using var source = CreateQuadBitmap();
        using var attributes = new ImageAttributes();
        attributes.SetRemapTable(new ColorMap
        {
            OldColor = Color.Red,
            NewColor = Color.Magenta
        });
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        using var brush = new TextureBrush(
            source,
            new Rectangle(0, 0, 2, 1),
            attributes);
        attributes.ClearRemapTable();
        attributes.SetWrapMode(WrapMode.Clamp);

        using var image = Assert.IsType<Bitmap>(brush.Image);
        Assert.Equal(new Size(2, 1), image.Size);
        Assert.Equal(Color.Magenta.ToArgb(), image.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Green.ToArgb(), image.GetPixel(1, 0).ToArgb());
        Assert.Equal(WrapMode.TileFlipXY, brush.WrapMode);
    }

    [Fact]
    public void CloneOwnsImageTransformAndLifetimeIndependently()
    {
        var source = CreateQuadBitmap();
        var original = new TextureBrush(source, WrapMode.TileFlipX);
        source.Dispose();
        original.TranslateTransform(3f, 4f);
        var clone = Assert.IsType<TextureBrush>(original.Clone());
        original.Dispose();

        Assert.Equal(WrapMode.TileFlipX, clone.WrapMode);
        using Matrix transform = clone.Transform;
        Assert.Equal(3f, transform.OffsetX);
        Assert.Equal(4f, transform.OffsetY);
        using var image = Assert.IsType<Bitmap>(clone.Image);
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(0, 0).ToArgb());
        clone.Dispose();
    }

    [Fact]
    public void TransformOperationsHonorMatrixOrderAndReset()
    {
        using var source = CreateQuadBitmap();
        using var brush = new TextureBrush(source);
        using var initial = new Matrix(1f, 0f, 0f, 1f, 2f, 3f);
        using var scale = new Matrix(2f, 0f, 0f, 4f, 0f, 0f);
        brush.Transform = initial;
        brush.MultiplyTransform(scale, MatrixOrder.Append);
        brush.RotateTransform(90f, MatrixOrder.Prepend);
        brush.TranslateTransform(5f, 7f, MatrixOrder.Append);

        using Matrix expected = initial.Clone();
        expected.Multiply(scale, MatrixOrder.Append);
        expected.Rotate(90f, MatrixOrder.Prepend);
        expected.Translate(5f, 7f, MatrixOrder.Append);
        Assert.Equal(expected, brush.Transform);

        brush.ResetTransform();
        Assert.True(brush.Transform.IsIdentity);
    }

    [Fact]
    public void TileFlipXRecordsAlternatingMirroredTypedTextureTransforms()
    {
        using var source = CreateQuadBitmap();
        using var target = new Bitmap(4, 2);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source, WrapMode.TileFlipX);

        graphics.FillRectangle(brush, 0, 0, 4, 2);

        Assert.Equal(RenderCommandType.PushClip, graphics.DrawingContext.Commands[0].Type);
        RenderCommand first = graphics.DrawingContext.Commands[1];
        RenderCommand second = graphics.DrawingContext.Commands[2];
        Assert.Equal(RenderCommandType.DrawTexture, first.Type);
        Assert.Equal(RenderCommandType.DrawTexture, second.Type);
        Assert.Equal(1f, first.Transform.M11);
        Assert.Equal(0f, first.Transform.M41);
        Assert.Equal(-1f, second.Transform.M11);
        Assert.Equal(4f, second.Transform.M41);
        Assert.Equal(RenderCommandType.PopClip, graphics.DrawingContext.Commands[3].Type);
        Assert.Equal(1, graphics.DrawingContext.RetainedResourceCount);
    }

    [Fact]
    public void ClampRecordsOneTileAndLeavesOutsideTransparent()
    {
        using var source = CreateQuadBitmap();
        using var target = new Bitmap(4, 4);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source, WrapMode.Clamp);

        graphics.FillRectangle(brush, 0, 0, 4, 4);

        Assert.Collection(
            graphics.DrawingContext.Commands,
            command => Assert.Equal(RenderCommandType.PushClip, command.Type),
            command => Assert.Equal(RenderCommandType.DrawTexture, command.Type),
            command => Assert.Equal(RenderCommandType.PopClip, command.Type));
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.FromArgb(0, 0, 0, 0).ToArgb(), target.GetPixel(3, 3).ToArgb());
    }

    [Fact]
    public void FillPathUsesTypedGeometryClipAndTextureCommands()
    {
        using var source = CreateQuadBitmap();
        using var target = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source);
        using var path = new GraphicsPath();
        path.AddEllipse(1, 1, 4, 4);

        graphics.FillPath(brush, path);

        Assert.Equal(RenderCommandType.PushGeometryClip, graphics.DrawingContext.Commands[0].Type);
        Assert.Contains(
            graphics.DrawingContext.Commands,
            static command => command.Type == RenderCommandType.DrawTexture);
        Assert.Equal(
            RenderCommandType.PopGeometryClip,
            graphics.DrawingContext.Commands[^1].Type);
    }

    [Fact]
    public void TexturePenWidensStrokeIntoTypedGeometryClipAndTextureCommands()
    {
        using var source = CreateQuadBitmap();
        using var target = new Bitmap(8, 8);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source);
        using var pen = new Pen(brush, 2f);
        using var path = new GraphicsPath();
        path.AddLine(1, 4, 7, 4);

        graphics.DrawPath(pen, path);

        Assert.Equal(RenderCommandType.PushGeometryClip, graphics.DrawingContext.Commands[0].Type);
        Assert.Contains(
            graphics.DrawingContext.Commands,
            static command => command.Type == RenderCommandType.DrawTexture);
        Assert.Equal(
            RenderCommandType.PopGeometryClip,
            graphics.DrawingContext.Commands[^1].Type);
        Assert.NotEqual(0, target.GetPixel(4, 4).A);
        Assert.Equal(0, target.GetPixel(4, 0).A);
    }

    [Theory]
    [InlineData(WrapMode.Tile)]
    [InlineData(WrapMode.TileFlipX)]
    [InlineData(WrapMode.TileFlipY)]
    [InlineData(WrapMode.TileFlipXY)]
    public void WrapModesProduceOfficialMirrorPatternPixels(WrapMode wrapMode)
    {
        using var source = CreateQuadBitmap();
        using var target = new Bitmap(4, 4);
        using (Graphics graphics = Graphics.FromImage(target))
        using (var brush = new TextureBrush(source, wrapMode))
        {
            graphics.FillRectangle(brush, 0, 0, 4, 4);
        }

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int sourceX = wrapMode is WrapMode.TileFlipX or WrapMode.TileFlipXY
                    && x >= 2
                        ? 3 - x
                        : x % 2;
                int sourceY = wrapMode is WrapMode.TileFlipY or WrapMode.TileFlipXY
                    && y >= 2
                        ? 3 - y
                        : y % 2;
                Assert.Equal(
                    source.GetPixel(sourceX, sourceY).ToArgb(),
                    target.GetPixel(x, y).ToArgb());
            }
        }
    }

    [Fact]
    public void ConstructorsAndWrapSetterRejectInvalidArguments()
    {
        using var source = CreateQuadBitmap();
        Assert.Throws<ArgumentNullException>(() => new TextureBrush(null!));
        Assert.ThrowsAny<ArgumentException>(() =>
            new TextureBrush(source, (WrapMode)(-1)));
        Assert.ThrowsAny<ArgumentException>(() =>
            new TextureBrush(source, new Rectangle(-1, 0, 1, 1)));
        Assert.ThrowsAny<ArgumentException>(() =>
            new TextureBrush(source, Rectangle.Empty));

        using var brush = new TextureBrush(source);
        Assert.ThrowsAny<ArgumentException>(() => brush.WrapMode = (WrapMode)5);
        Assert.Throws<ArgumentNullException>(() => brush.Transform = null!);
        Assert.Throws<ArgumentNullException>(() => brush.MultiplyTransform(null!));
    }

    [Fact]
    public void DisposedBrushRejectsPublicStateAndRendering()
    {
        using var source = CreateQuadBitmap();
        var brush = new TextureBrush(source);
        brush.Dispose();

        Assert.Throws<ObjectDisposedException>(() => brush.Image);
        Assert.Throws<ObjectDisposedException>(() => brush.Transform);
        Assert.Throws<ObjectDisposedException>(() => brush.WrapMode);
        Assert.Throws<ObjectDisposedException>(() => brush.Clone());
        Assert.Throws<ObjectDisposedException>(() => brush.ToProGpuBrush());
    }

    [Fact]
    public void WarmedTransformMutationAllocatesNothing()
    {
        using var source = CreateQuadBitmap();
        using var brush = new TextureBrush(source);
        brush.TranslateTransform(1f, -1f);
        brush.TranslateTransform(-1f, 1f);

        const int Iterations = 1024;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            brush.TranslateTransform(1f, -1f);
            brush.TranslateTransform(-1f, 1f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void WarmedFourTileRecordingHasBoundedPerFillAllocation()
    {
        using var source = CreateQuadBitmap();
        using var target = new Bitmap(4, 4);
        using var graphics = Graphics.FromImage(target);
        using var brush = new TextureBrush(source, WrapMode.TileFlipXY);
        graphics.FillRectangle(brush, 0, 0, 4, 4);
        graphics.DrawingContext.Clear();

        const int Iterations = 64;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            graphics.FillRectangle(brush, 0, 0, 4, 4);
            graphics.DrawingContext.Clear();
        }

        long bytesPerFill =
            (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerFill, 0, 512);
    }

    private static Bitmap CreateQuadBitmap()
    {
        var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Green);
        bitmap.SetPixel(0, 1, Color.Blue);
        bitmap.SetPixel(1, 1, Color.Yellow);
        return bitmap;
    }
}
