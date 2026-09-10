using System.Drawing.Drawing2D;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsCoordinateSpaceQualityTests
{
    [Fact]
    public void CoordinateSpaceHasOfficialValues()
    {
        Assert.Equal(["World", "Page", "Device"], Enum.GetNames<CoordinateSpace>());
        Assert.Equal(0, (int)CoordinateSpace.World);
        Assert.Equal(1, (int)CoordinateSpace.Page);
        Assert.Equal(2, (int)CoordinateSpace.Device);
    }

    [Fact]
    public void TransformPointsConvertsWorldPageAndDeviceSpaces()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 100f, 100f),
            Matrix4x4.CreateTranslation(10f, 20f, 0f));
        graphics.PageUnit = GraphicsUnit.Pixel;
        graphics.PageScale = 2f;
        graphics.TransformElements = Matrix3x2.CreateTranslation(3f, 4f);

        AssertTransform(
            graphics,
            CoordinateSpace.Device,
            CoordinateSpace.World,
            new PointF(1f, 2f),
            new PointF(18f, 32f));
        AssertTransform(
            graphics,
            CoordinateSpace.Page,
            CoordinateSpace.World,
            new PointF(1f, 2f),
            new PointF(4f, 6f));
        AssertTransform(
            graphics,
            CoordinateSpace.Device,
            CoordinateSpace.Page,
            new PointF(4f, 6f),
            new PointF(18f, 32f));
        AssertTransform(
            graphics,
            CoordinateSpace.World,
            CoordinateSpace.Device,
            new PointF(18f, 32f),
            new PointF(1f, 2f));
        AssertTransform(
            graphics,
            CoordinateSpace.Page,
            CoordinateSpace.Device,
            new PointF(18f, 32f),
            new PointF(4f, 6f));
        AssertTransform(
            graphics,
            CoordinateSpace.World,
            CoordinateSpace.Page,
            new PointF(4f, 6f),
            new PointF(1f, 2f));
    }

    [Fact]
    public void ArrayAndSpanOverloadsMutateCallerStorage()
    {
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.TransformElements = Matrix3x2.CreateScale(2f, 3f)
            * Matrix3x2.CreateTranslation(4f, 5f);
        Point[] integerPoints = [new Point(1, 1), new Point(2, 2)];
        PointF[] floatPoints = [new PointF(1f, 1f), new PointF(2f, 2f)];

        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, integerPoints);
        ReadOnlySpan<PointF> writableView = floatPoints;
        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, writableView);

        Assert.Equal([new Point(6, 8), new Point(8, 11)], integerPoints);
        Assert.Equal([new PointF(6f, 8f), new PointF(8f, 11f)], floatPoints);
    }

    [Fact]
    public void TransformPointsValidatesInputsAndDisposedState()
    {
        using var target = new Bitmap(8, 8);
        Graphics graphics = Graphics.FromImage(target);

        Assert.Throws<ArgumentNullException>(() =>
            graphics.TransformPoints(CoordinateSpace.Page, CoordinateSpace.Page, (Point[])null!));
        Assert.Throws<ArgumentNullException>(() =>
            graphics.TransformPoints(CoordinateSpace.Page, CoordinateSpace.Page, (PointF[])null!));
        Assert.Throws<ArgumentException>(() =>
            graphics.TransformPoints(CoordinateSpace.Page, CoordinateSpace.Page, Array.Empty<Point>()));
        Assert.Throws<ArgumentException>(() =>
            graphics.TransformPoints((CoordinateSpace)(-1), CoordinateSpace.World, [Point.Empty]));
        Assert.Throws<ArgumentException>(() =>
            graphics.TransformPoints(CoordinateSpace.World, (CoordinateSpace)3, [PointF.Empty]));

        graphics.Dispose();
        Assert.Throws<ArgumentException>(() =>
            graphics.TransformPoints(CoordinateSpace.Page, CoordinateSpace.Page, [Point.Empty]));
    }

    [Fact]
    public void WarmedSpanTransformAllocatesNothing()
    {
        using var target = new Bitmap(8, 8);
        using Graphics graphics = Graphics.FromImage(target);
        graphics.TransformElements = Matrix3x2.CreateTranslation(1f, 2f);
        PointF[] points = [new PointF(1f, 1f)];
        ReadOnlySpan<PointF> writableView = points;
        graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, writableView);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_024; index++)
        {
            points[0] = PointF.Empty;
            graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, writableView);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(new PointF(1f, 2f), points[0]);
    }

    private static void AssertTransform(
        Graphics graphics,
        CoordinateSpace destination,
        CoordinateSpace source,
        PointF input,
        PointF expected)
    {
        PointF[] points = [input];
        graphics.TransformPoints(destination, source, points);
        Assert.Equal(expected, points[0]);
    }
}
