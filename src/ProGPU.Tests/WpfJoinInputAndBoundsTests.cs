using System;
using System.Linq;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class WpfJoinInputAndBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReversalMaterialBoundsRetainSourceOverhangThroughDashes(bool dashed)
    {
        var source = Path(reversal: true);
        var pen = CreatePen();
        if (dashed) pen.DashArray = [3.75, 1.25];
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen,
            out var prepared, out var coverage, out var bounds));
        Assert.Equal(new Rect(12.25f, 36.25f, 24, 8), bounds);
        Assert.True(coverage.UseWpfJoinSemantics);
        Assert.True(StrokeCoverageGeometry.TryMeasurePreparedLinearStrokeOutline(prepared, coverage, out var outlineBounds));
        Assert.Equal(bounds, outlineBounds);
        pen.UseWpfJoinSemantics = false;
        Assert.True(StrokeCoverageGeometry.TryPrepareLinearPath(source, pen, out _, out _, out var legacy));
        Assert.Equal(32.25f, legacy.Right); // Historical false-policy bounds stay unchanged.
    }

    [Theory]
    [InlineData(PenLineJoin.Miter, false, 0)]
    [InlineData(PenLineJoin.Bevel, false, 0)]
    [InlineData(PenLineJoin.Round, false, 0)]
    [InlineData(PenLineJoin.Miter, true, 0)]
    [InlineData(PenLineJoin.Miter, false, 1)]
    [InlineData(PenLineJoin.Miter, false, 2)]
    public void ActualInputExcludesRoundedCornerLeakAndRetainsExactJoin(PenLineJoin join, bool smooth, int frame)
    {
        var pen = CreatePen(join);
        var transform = Frame(frame);
        var command = Command(Path(curved: true, smooth: smooth), pen);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(command, transform, 73);
        var index = builder.BuildIndex();
        var bodies = index.Primitives.Where(p => p.Kind == GpuHitTestPrimitiveKind.PathStroke).ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.All(bodies, p => { Assert.Equal(1f, p.Data1.Y); Assert.Equal(Vector4.Zero, p.Data2); });
        Assert.Contains(index.Primitives, p => p.Kind == GpuHitTestPrimitiveKind.PathFill);
        using var context = new WgpuContext();
        context.Initialize(null);
        bool expectedOuter = join != PenLineJoin.Bevel || smooth;
        var outer = Vector2.Transform(new Vector2(34.65f, 42.65f), transform);
        Assert.Equal(expectedOuter, GpuHitTestEngine.TryHitTestPoint(context, index, outer, out var hit));
        if (expectedOuter) Assert.Equal(73, hit.Id);
        Assert.False(GpuHitTestEngine.TryHitTestPoint(context, index,
            Vector2.Transform(new Vector2(35.45f, 43.45f), transform), out _));
        Assert.True(GpuHitTestEngine.TryHitTestPoint(context, index,
            Vector2.Transform(new Vector2(20.25f, 40.25f), transform), out _));
        Assert.False(GpuHitTestEngine.TryHitTestPoint(context, index,
            Vector2.Transform(new Vector2(10, 40.25f), transform), out _));
        Assert.Equal(expectedOuter, GpuHitTestEngine.TryHitTestPoint(context, index, outer, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedGapRetainsOnlyPaintedBodiesAndActualImplicitClose(bool trailingGap)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new(10, 10), isClosed: true) { IsFilled = false };
        figure.Segments.Add(new LineSegment(new(30, 10)));
        figure.Segments.Add(new LineSegment(new(30, 30), isStroked: false));
        if (!trailingGap) figure.Segments.Add(new LineSegment(new(10, 30)));
        path.Figures.Add(figure);
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(Command(path, CreatePen()), Matrix4x4.Identity, 74);
        var index = builder.BuildIndex();
        var bodies = index.Primitives.Where(p => p.Kind == GpuHitTestPrimitiveKind.PathStroke).ToArray();
        Assert.Equal(trailingGap ? 2 : 3, bodies.Length);
        Assert.All(bodies, p => Assert.Equal(Vector4.Zero, p.Data2));
        var closing = index.PathSegments[(int)bodies[^1].Data1.X];
        Assert.Equal(trailingGap ? new Vector2(30, 30) : new Vector2(10, 30), closing.P0);
        Assert.Equal(new Vector2(10, 10), closing.P1);
        Assert.DoesNotContain(bodies, p =>
            index.PathSegments[(int)p.Data1.X].P0 == new Vector2(30, 10) &&
            index.PathSegments[(int)p.Data1.X].P1 == new Vector2(30, 30));
        if (trailingGap) Assert.DoesNotContain(index.Primitives, p => p.Kind == GpuHitTestPrimitiveKind.PathFill);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsupportedSourcePolicyRejectsBeforeInputOrBoundsPublication(int invalid)
    {
        var pen = CreatePen();
        if (invalid == 0) pen.LineJoin = PenLineJoin.MiterOrBevel;
        if (invalid == 1) pen.StrokeTransformMode = PenStrokeTransformMode.Fixed;
        if (invalid == 2) pen.Thickness = Pen.HairlineThickness;
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(Command(Path(), CreatePen()), Matrix4x4.Identity, 1);
        var before = builder.BuildIndex();
        Assert.Throws<NotSupportedException>(() => builder.AddCommand(Command(new PathGeometry(), pen), Matrix4x4.Identity));
        var after = builder.BuildIndex();
        Assert.Equal(before.Primitives, after.Primitives);
        Assert.Equal(before.PathSegments, after.PathSegments);
        Assert.Throws<NotSupportedException>(() => StrokeCoverageGeometry.TryPrepareLinearPath(
            new PathGeometry(), pen, out _, out _, out _));
    }

    [Fact]
    public void FailedLateSourceSegmentRollsBackNewHitBodiesAndKeepsCapturedGeneration()
    {
        using var builder = new GpuRenderCommandHitTestCacheBuilder();
        builder.AddCommand(Command(Path(), CreatePen()), Matrix4x4.Identity, 1);
        var before = builder.BuildIndex();
        var invalid = Path();
        invalid.Figures.Add(new PathFigure(new(1, 2))
            { Segments = { new LineSegment(new(float.NaN, 4)) } });
        Assert.Throws<NotSupportedException>(() => builder.AddCommand(Command(invalid, CreatePen()), Matrix4x4.Identity, 2));
        var after = builder.BuildIndex();
        Assert.Equal(before.Primitives, after.Primitives);
        Assert.Equal(before.PathSegments, after.PathSegments);
        invalid.Figures.Clear();
        Assert.Equal(before.Primitives, builder.BuildIndex().Primitives);
    }

    internal static Pen CreatePen(PenLineJoin join = PenLineJoin.Miter) =>
        new(new SolidColorBrush(Vector4.One), 8, join, 1) { UseWpfJoinSemantics = true };

    internal static PathGeometry Path(bool reversal = false, bool curved = false, bool smooth = false)
    {
        var path = new PathGeometry();
        var figure = new PathFigure(new(12.25f, 40.25f)) { IsFilled = false };
        figure.Segments.Add(curved
            ? new CubicBezierSegment(new(16.25f, 40.25f), new(28.25f, 40.25f), new(32.25f, 40.25f))
            : new LineSegment(new(32.25f, 40.25f)));
        figure.Segments.Add(new LineSegment(reversal ? new(12.25f, 40.25f) : new(32.25f, 20.25f), smooth));
        path.Figures.Add(figure);
        return path;
    }

    internal static RenderCommand Command(PathGeometry path, Pen pen) => new()
    {
        Type = RenderCommandType.DrawPath, Path = path, Pen = pen,
        IsPenThicknessLocal = true, IsEdgeAliased = true, Transform = Matrix4x4.Identity
    };

    private static Matrix4x4 Frame(int frame) => frame switch
    {
        1 => Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(7.25f, -3.5f, 0),
        2 => new Matrix4x4(2, .25f, 0, 0, .5f, 3, 0, 0, 0, 0, 1, 0, 4, 7, 0, 1),
        _ => Matrix4x4.Identity
    };
}
