using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsContainerQualityTests
{
    [Fact]
    public void GraphicsContainerHasOfficialPublicShape()
    {
        Type type = typeof(GraphicsContainer);

        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.Equal(typeof(MarshalByRefObject), type.BaseType);
        Assert.Empty(type.GetConstructors());
    }

    [Fact]
    public void BeginAndEndContainerResetAndRestorePublicState()
    {
        using var target = new Bitmap(12, 12);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.SetClip(new Rectangle(2, 3, 4, 5));
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PageScale = 0.5f;
        graphics.PageUnit = GraphicsUnit.Inch;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.RenderingOrigin = new Point(-1, -2);
        graphics.RotateTransform(45f);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextContrast = 0;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

        GraphicsContainer container = graphics.BeginContainer();

        Assert.True(graphics.Clip.IsInfinite(graphics));
        Assert.Equal(CompositingMode.SourceOver, graphics.CompositingMode);
        Assert.Equal(CompositingQuality.Default, graphics.CompositingQuality);
        Assert.Equal(InterpolationMode.Bilinear, graphics.InterpolationMode);
        Assert.Equal(1f, graphics.PageScale);
        Assert.Equal(GraphicsUnit.Display, graphics.PageUnit);
        Assert.Equal(PixelOffsetMode.Default, graphics.PixelOffsetMode);
        Assert.Equal(new Point(-1, -2), graphics.RenderingOrigin);
        Assert.Equal(SmoothingMode.None, graphics.SmoothingMode);
        Assert.Equal(4, graphics.TextContrast);
        Assert.Equal(TextRenderingHint.SystemDefault, graphics.TextRenderingHint);
        Assert.True(graphics.Transform.IsIdentity);

        graphics.EndContainer(container);

        Assert.Equal(new RectangleF(2f, 3f, 4f, 5f), graphics.Clip.GetBounds(graphics));
        Assert.Equal(CompositingMode.SourceCopy, graphics.CompositingMode);
        Assert.Equal(CompositingQuality.HighQuality, graphics.CompositingQuality);
        Assert.Equal(InterpolationMode.HighQualityBicubic, graphics.InterpolationMode);
        Assert.Equal(0.5f, graphics.PageScale);
        Assert.Equal(GraphicsUnit.Inch, graphics.PageUnit);
        Assert.Equal(PixelOffsetMode.Half, graphics.PixelOffsetMode);
        Assert.Equal(new Point(-1, -2), graphics.RenderingOrigin);
        Assert.Equal(SmoothingMode.AntiAlias, graphics.SmoothingMode);
        Assert.Equal(0, graphics.TextContrast);
        Assert.Equal(TextRenderingHint.AntiAlias, graphics.TextRenderingHint);
        Assert.False(graphics.Transform.IsIdentity);
    }

    [Fact]
    public void NestedContainersComposeParentAndLocalTransforms()
    {
        using var target = new Bitmap(100, 100);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.TranslateTransform(10f, 0f);

        GraphicsContainer outer = graphics.BeginContainer();
        AssertDevicePoint(graphics, PointF.Empty, new PointF(10f, 0f));
        graphics.TranslateTransform(0f, 20f);
        AssertDevicePoint(graphics, PointF.Empty, new PointF(10f, 20f));

        GraphicsContainer inner = graphics.BeginContainer();
        graphics.ScaleTransform(2f, 3f);
        AssertDevicePoint(graphics, new PointF(1f, 1f), new PointF(12f, 23f));
        graphics.EndContainer(inner);

        AssertDevicePoint(graphics, PointF.Empty, new PointF(10f, 20f));
        graphics.EndContainer(outer);
        AssertDevicePoint(graphics, PointF.Empty, new PointF(10f, 0f));
    }

    [Fact]
    public void RectangleContainerMapsSourceCoordinatesIntoDestination()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 200f, 200f),
            Matrix4x4.CreateTranslation(7f, 11f, 0f));
        graphics.TranslateTransform(5f, 3f);

        GraphicsContainer container = graphics.BeginContainer(
            new RectangleF(10f, 20f, 30f, 40f),
            new RectangleF(0f, 0f, 60f, 80f),
            GraphicsUnit.Pixel);

        AssertDevicePoint(graphics, PointF.Empty, new PointF(22f, 34f));
        AssertDevicePoint(graphics, new PointF(60f, 80f), new PointF(52f, 74f));
        graphics.EndContainer(container);
        AssertDevicePoint(graphics, PointF.Empty, new PointF(12f, 14f));
    }

    [Fact]
    public void ContainerKeepsParentClipEffectiveWhilePublicClipIsInfinite()
    {
        using var target = new Bitmap(6, 6);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.Transparent);
            graphics.SetClip(new Rectangle(2, 2, 2, 2));
            GraphicsContainer container = graphics.BeginContainer();
            Assert.True(graphics.Clip.IsInfinite(graphics));
            graphics.FillRectangle(Brushes.Red, 0, 0, 6, 6);
            graphics.EndContainer(container);
        }

        Assert.Equal(0, target.GetPixel(1, 1).A);
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(2, 2).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), target.GetPixel(3, 3).ToArgb());
        Assert.Equal(0, target.GetPixel(4, 4).A);
    }

    [Fact]
    public void ContainerTokensAreOwnedSingleUseAndInvalidateNestedScopes()
    {
        using var firstTarget = new Bitmap(4, 4);
        using var secondTarget = new Bitmap(4, 4);
        using Graphics first = Graphics.FromImage(firstTarget);
        using Graphics second = Graphics.FromImage(secondTarget);
        GraphicsContainer outer = first.BeginContainer();
        GraphicsContainer inner = first.BeginContainer();

        Assert.Throws<ArgumentException>(() => second.EndContainer(outer));
        Assert.Throws<ArgumentNullException>(() => first.EndContainer(null!));
        first.EndContainer(outer);
        Assert.Throws<ArgumentException>(() => first.EndContainer(outer));
        Assert.Throws<ArgumentException>(() => first.EndContainer(inner));
    }

    [Fact]
    public void RestoringOuterSaveInvalidatesContainerAndBalancesClipScopes()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 20f, 20f),
            Matrix4x4.Identity);
        graphics.SetClip(new Rectangle(1, 1, 18, 18));
        GraphicsState saved = graphics.Save();
        GraphicsContainer container = graphics.BeginContainer();
        graphics.SetClip(new Rectangle(2, 2, 4, 4));

        graphics.Restore(saved);

        Assert.Throws<ArgumentException>(() => graphics.EndContainer(container));
        Assert.Equal(
            context.Commands.Count(command => command.Type == RenderCommandType.PushGeometryClip),
            context.Commands.Count(command => command.Type == RenderCommandType.PopGeometryClip) + 1);
    }

    [Fact]
    public void DisposingActiveNestedContainersBalancesRecorderClipScopes()
    {
        var context = new DrawingContext();
        Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 20f, 20f),
            Matrix4x4.Identity);
        graphics.SetClip(new Rectangle(1, 1, 18, 18));
        graphics.BeginContainer();
        graphics.SetClip(new Rectangle(2, 2, 10, 10));
        graphics.BeginContainer();
        graphics.SetClip(new Rectangle(3, 3, 4, 4));

        graphics.Dispose();

        Assert.Equal(
            context.Commands.Count(command => command.Type == RenderCommandType.PushGeometryClip),
            context.Commands.Count(command => command.Type == RenderCommandType.PopGeometryClip));
    }

    [Fact]
    public void WarmedContainerRoundTripHasBoundedAllocation()
    {
        using var target = new Bitmap(4, 4);
        using Graphics graphics = Graphics.FromImage(target);
        GraphicsContainer warmup = graphics.BeginContainer();
        graphics.EndContainer(warmup);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            GraphicsContainer container = graphics.BeginContainer();
            graphics.EndContainer(container);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 256 * 1_024);
    }

    [Theory]
    [InlineData(GraphicsUnit.World)]
    [InlineData(GraphicsUnit.Display)]
    [InlineData((GraphicsUnit)(-1))]
    public void RectangleContainerRejectsUnsupportedUnits(GraphicsUnit unit)
    {
        using var target = new Bitmap(4, 4);
        using Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentException>(() => graphics.BeginContainer(
            new RectangleF(0f, 0f, 1f, 1f),
            new RectangleF(0f, 0f, 1f, 1f),
            unit));
    }

    private static void AssertDevicePoint(Graphics graphics, PointF input, PointF expected)
    {
        PointF[] points = [input];
        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, points);
        Assert.Equal(expected, points[0]);
    }
}
