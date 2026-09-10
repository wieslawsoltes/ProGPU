using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsStateQualityTests
{
    [Fact]
    public void StatePropertiesHaveOfficialDefaultsAndValidation()
    {
        using var target = new Bitmap(8, 8);
        Graphics graphics = Graphics.FromImage(target);

        Assert.Equal(CompositingMode.SourceOver, graphics.CompositingMode);
        Assert.Equal(Point.Empty, graphics.RenderingOrigin);
        Assert.Equal(4, graphics.TextContrast);
        Assert.True(graphics.TransformElements.IsIdentity);
        Assert.Throws<InvalidEnumArgumentException>(() =>
            graphics.CompositingMode = (CompositingMode)2);
        Assert.Throws<ArgumentException>(() => graphics.TextContrast = -1);
        Assert.Throws<ArgumentException>(() => graphics.TextContrast = 13);
        Assert.Throws<ArgumentException>(() => graphics.TransformElements = default);

        graphics.Dispose();
        Assert.Throws<ArgumentException>(() => graphics.CompositingMode);
        Assert.Throws<ArgumentException>(() => graphics.RenderingOrigin);
        Assert.Throws<ArgumentException>(() => graphics.TextContrast);
        Assert.Throws<ArgumentException>(() => graphics.TransformElements);
    }

    [Fact]
    public void SaveAndRestoreRoundTripsPortableGraphicsState()
    {
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        GraphicsState saved = graphics.Save();

        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.RenderingOrigin = new Point(3, 5);
        graphics.TextContrast = 12;
        graphics.TransformElements = Matrix3x2.CreateTranslation(7f, 9f);

        graphics.Restore(saved);

        Assert.Equal(CompositingMode.SourceOver, graphics.CompositingMode);
        Assert.Equal(Point.Empty, graphics.RenderingOrigin);
        Assert.Equal(4, graphics.TextContrast);
        Assert.True(graphics.TransformElements.IsIdentity);
    }

    [Fact]
    public void OrderedTransformOverloadsMatchMatrixComposition()
    {
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        Matrix3x2 translation = Matrix3x2.CreateTranslation(2f, 3f);
        graphics.TransformElements = translation;

        graphics.ScaleTransform(4f, 5f, MatrixOrder.Append);
        Assert.Equal(translation * Matrix3x2.CreateScale(4f, 5f), graphics.TransformElements);

        graphics.ResetTransform();
        using var rotation = new Matrix(Matrix3x2.CreateRotation(MathF.PI / 2f));
        graphics.MultiplyTransform(rotation);
        graphics.TranslateTransform(6f, 7f, MatrixOrder.Prepend);
        graphics.RotateTransform(90f, MatrixOrder.Append);

        Matrix3x2 expected = Matrix3x2.CreateTranslation(6f, 7f)
            * rotation.MatrixElements
            * Matrix3x2.CreateRotation(MathF.PI / 2f);
        Assert.Equal(expected, graphics.TransformElements);
    }

    [Fact]
    public void WarmedTransformElementsRoundTripAllocatesNothing()
    {
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        Matrix3x2 value = Matrix3x2.CreateScale(2f, 3f)
            * Matrix3x2.CreateTranslation(4f, 5f);
        graphics.TransformElements = value;
        _ = graphics.TransformElements;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            graphics.TransformElements = value;
            _ = graphics.TransformElements;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RectangleVisibilityOverloadsUseTheEffectiveClip()
    {
        using var target = new Bitmap(20, 20);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.SetClip(new Rectangle(2, 3, 5, 6));

        Assert.True(graphics.IsVisible(4, 5, 2, 2));
        Assert.True(graphics.IsVisible(6.5f, 8.5f, 2f, 2f));
        Assert.False(graphics.IsVisible(8, 10, 2, 2));
        Assert.False(graphics.IsVisible(-4f, -5f, 2f, 2f));
    }

    [Fact]
    public void SourceCopyReplacesDestinationAlpha()
    {
        using var target = new Bitmap(1, 1);
        using (Graphics graphics = Graphics.FromImage(target))
        using (var source = new SolidBrush(Color.FromArgb(128, 255, 0, 0)))
        {
            graphics.Clear(Color.Blue);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.FillRectangle(source, 0, 0, 1, 1);
        }

        Color pixel = target.GetPixel(0, 0);
        Assert.InRange(pixel.A, 127, 129);
        Assert.Equal(255, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
    }

    [Fact]
    public void SourceCopyScopeBalancesAcrossHostedFlushes()
    {
        var context = new DrawingContext();
        using var targetContext = new WgpuContext();
        var batches = new List<RenderCommandType[]>();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 8f, 8f),
            Matrix4x4.Identity,
            targetContext,
            _ =>
            {
                batches.Add(context.Commands.Select(command => command.Type).ToArray());
                context.Clear();
            },
            static () => { });
        graphics.CompositingMode = CompositingMode.SourceCopy;

        graphics.FillRectangle(Brushes.Red, 0, 0, 2, 2);
        graphics.Flush();
        graphics.FillRectangle(Brushes.Blue, 2, 0, 2, 2);
        graphics.Flush();

        Assert.Equal(2, batches.Count);
        Assert.All(batches, batch =>
        {
            Assert.Equal(RenderCommandType.PushBlendMode, batch[0]);
            Assert.Equal(RenderCommandType.PopBlendMode, batch[^1]);
            Assert.Contains(RenderCommandType.DrawRect, batch);
        });
        Assert.Equal(
            [RenderCommandType.PushBlendMode],
            context.Commands.Select(command => command.Type));
    }

    [Fact]
    public void RenderingOriginOffsetsHatchTileCoordinates()
    {
        using var target = new Bitmap(3, 3);
        using (Graphics graphics = Graphics.FromImage(target))
        using (var hatch = new HatchBrush(HatchStyle.Cross, Color.Red, Color.Black))
        {
            graphics.RenderingOrigin = new Point(1, 1);
            graphics.FillRectangle(hatch, 0, 0, 3, 3);
        }

        Assert.Equal(Color.Black.ToArgb(), target.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(0, 1).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(1, 1).ToArgb());
    }
}
