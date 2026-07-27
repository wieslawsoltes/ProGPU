using Avalonia;
using Avalonia.Media;
using Avalonia.ProGpu;
using ProGPU.Vector;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class AvaloniaGeometryFactoryContractTests
{
    [Fact]
    public void PrimitiveFactoriesProduceNativeProGpuPaths()
    {
        var rectangle = AvaloniaGeometryFactory.Rectangle(new Rect(4, 5, 20, 30));
        var ellipse = AvaloniaGeometryFactory.Ellipse(new Rect(10, 20, 40, 60));
        var line = AvaloniaGeometryFactory.Line(new Point(2, 3), new Point(8, 13));

        Assert.Equal(new Rect(4, 5, 20, 30), rectangle.Bounds);
        Assert.Equal(new Rect(10, 20, 40, 60), ellipse.Bounds);
        Assert.Single(line.Path.Figures);
        Assert.False(line.Path.Figures[0].IsFilled);
        var segment = Assert.IsType<ProGPU.Vector.LineSegment>(
            Assert.Single(line.Path.Figures[0].Segments));
        Assert.Equal(8, segment.Point.X);
        Assert.Equal(13, segment.Point.Y);
    }

    [Fact]
    public void GroupOwnsAStableSnapshotOfChildFigures()
    {
        var child = AvaloniaGeometryFactory.Rectangle(new Rect(0, 0, 10, 10));
        var group = AvaloniaGeometryFactory.Group(
            Avalonia.Media.FillRule.EvenOdd,
            new[] { child });

        child.Path.Figures.Clear();

        Assert.Equal(ProGPU.Vector.FillRule.EvenOdd, group.Path.FillRule);
        Assert.Single(group.Path.Figures);
        Assert.Equal(new Rect(0, 0, 10, 10), group.Bounds);
    }

    [Fact]
    public void TransformCreatesAnIndependentPathAndKeepsTheContractSource()
    {
        var source = AvaloniaGeometryFactory.Rectangle(new Rect(1, 2, 3, 4));
        var transform = Matrix.CreateScale(2, 3) * Matrix.CreateTranslation(10, 20);

        var transformed = Assert.IsType<AvaloniaTransformedPath>(
            source.WithTransform(transform));

        Assert.Same(source, transformed.SourceGeometry);
        Assert.Equal(transform, transformed.Transform);
        Assert.NotSame(source.Path, transformed.Path);
        Assert.Equal(new Rect(12, 26, 6, 12), transformed.Bounds);
    }

    [Theory]
    [InlineData(GeometryCombineMode.Exclude, 0)]
    [InlineData(GeometryCombineMode.Intersect, 1)]
    [InlineData(GeometryCombineMode.Union, 2)]
    [InlineData(GeometryCombineMode.Xor, 3)]
    public void BooleanGeometryIsDeferredUntilPathCompilation(
        GeometryCombineMode mode,
        int expectedOperation)
    {
        var left = AvaloniaGeometryFactory.Rectangle(new Rect(0, 0, 20, 20));
        var right = AvaloniaGeometryFactory.Rectangle(new Rect(10, 10, 20, 20));

        var combined = AvaloniaGeometryFactory.Combine(mode, left, right);

        Assert.True(combined.Path.IsCombined);
        Assert.Same(left.Path, combined.Path.PathA);
        Assert.Same(right.Path, combined.Path.PathB);
        Assert.Equal(expectedOperation, combined.Path.Op);
        Assert.Empty(combined.Path.Figures);
    }
}
