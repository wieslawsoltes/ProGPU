using System.Reflection;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkPathEffectCompatibilityTests
{
    [Fact]
    public void FactoriesExposeNativeSurfaceAndParameterNames()
    {
        Assert.Equal(typeof(SKObject), typeof(SKPathEffect).BaseType);
        Assert.Equal(
            10,
            typeof(SKPathEffect).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Length);
        AssertParameters(nameof(SKPathEffect.Create1DPath), 4, "path", "advance", "phase", "style");
        AssertParameters(nameof(SKPathEffect.Create2DLine), 2, "width", "matrix");
        AssertParameters(nameof(SKPathEffect.Create2DPath), 2, "matrix", "path");
        AssertParameters(nameof(SKPathEffect.CreateCompose), 2, "outer", "inner");
        AssertParameters(nameof(SKPathEffect.CreateCorner), 1, "radius");
        AssertParameters(nameof(SKPathEffect.CreateDash), 2, "intervals", "phase");
        AssertParameters(nameof(SKPathEffect.CreateDiscrete), 3, "segLength", "deviation", "seedAssist");
        AssertParameters(nameof(SKPathEffect.CreateSum), 2, "first", "second");
        AssertParameters(nameof(SKPathEffect.CreateTrim), 2, "start", "stop");
        AssertParameters(nameof(SKPathEffect.CreateTrim), 3, "start", "stop", "mode");
        Assert.Equal([0, 1, 2], Enum.GetValues<SKPath1DPathEffectStyle>().Select(static value => (int)value));
        Assert.Equal([0, 1], Enum.GetValues<SKTrimPathEffectMode>().Select(static value => (int)value));
    }

    [Fact]
    public void FactoriesSnapshotMutableInputsAndComposeIndependentGraphs()
    {
        var intervals = new[] { 4f, 2f };
        using var dash = SKPathEffect.CreateDash(intervals, 1f);
        intervals[0] = 99f;
        Assert.Equal(new[] { 4f, 2f }, dash.Intervals);

        using var stamp = new SKPath();
        stamp.AddRect(new SKRect(0f, 0f, 2f, 1f));
        using var path1D = SKPathEffect.Create1DPath(stamp, 3f, 0f, SKPath1DPathEffectStyle.Rotate);
        using var path2D = SKPathEffect.Create2DPath(SKMatrix.Identity, stamp);
        stamp.Reset();
        Assert.False(Assert.IsType<SKPathEffect.Path1DData>(path1D.Data).Path.IsEmpty);
        Assert.False(Assert.IsType<SKPathEffect.Path2DData>(path2D.Data).Path.IsEmpty);

        using var composed = SKPathEffect.CreateCompose(path1D, dash);
        using var sum = SKPathEffect.CreateSum(path2D, dash);
        path1D.Dispose();
        path2D.Dispose();
        dash.Dispose();
        Assert.Equal(SKPathEffect.EffectKind.Compose, composed.Kind);
        Assert.Equal(SKPathEffect.EffectKind.Sum, sum.Kind);
    }

    [Fact]
    public void PaintRetainsDisposedDashEffectWithoutChangingPenOutput()
    {
        var dash = SKPathEffect.CreateDash([6f, 2f], 3f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            PathEffect = dash,
        };

        dash.Dispose();
        var pen = Assert.IsType<Pen>(paint.ToPen());

        Assert.Equal(new[] { 3.0, 1.0 }, pen.DashArray);
        Assert.Equal(1.5, pen.DashOffset);
    }

    [Fact]
    public void DashTrimComposeAndSumMaterializeMeasuredSegments()
    {
        using var source = new SKPath();
        source.MoveTo(0f, 0f);
        source.LineTo(100f, 0f);

        using var dash = SKPathEffect.CreateDash([10f, 10f], 0f);
        Assert.True(dash.TryApply(source, 1f, out var dashed));
        using (dashed)
        {
            Assert.Equal(5, dashed.Geometry.Figures.Count);
        }

        using var inner = SKPathEffect.CreateTrim(0f, 0.8f);
        using var outer = SKPathEffect.CreateTrim(0.25f, 0.75f);
        using var composed = SKPathEffect.CreateCompose(outer, inner);
        Assert.True(composed.TryApply(source, 1f, out var composedPath));
        using (composedPath)
        {
            Assert.InRange(composedPath.Bounds.Left, 19.99f, 20.01f);
            Assert.InRange(composedPath.Bounds.Right, 59.99f, 60.01f);
        }

        using var first = SKPathEffect.CreateTrim(0f, 0.2f);
        using var second = SKPathEffect.CreateTrim(0.8f, 1f);
        using var sum = SKPathEffect.CreateSum(first, second);
        Assert.True(sum.TryApply(source, 1f, out var summed));
        using (summed)
        {
            Assert.Equal(2, summed.Geometry.Figures.Count);
        }
    }

    [Fact]
    public void CornerDiscreteAndPath1DEffectsProduceDeterministicCpuPaths()
    {
        using var cornerSource = new SKPath();
        cornerSource.MoveTo(0f, 0f);
        cornerSource.LineTo(20f, 0f);
        cornerSource.LineTo(20f, 20f);
        using var corner = SKPathEffect.CreateCorner(4f);
        Assert.True(corner.TryApply(cornerSource, 1f, out var rounded));
        using (rounded)
        {
            Assert.Contains(
                Assert.Single(rounded.Geometry.Figures).Segments,
                static segment => segment is QuadraticBezierSegment);
        }

        using var discrete = SKPathEffect.CreateDiscrete(4f, 2f, 123u);
        Assert.True(discrete.TryApply(cornerSource, 1f, out var firstDiscrete));
        Assert.True(discrete.TryApply(cornerSource, 1f, out var secondDiscrete));
        using (firstDiscrete)
        using (secondDiscrete)
        {
            Assert.Equal(firstDiscrete.Points, secondDiscrete.Points);
            Assert.NotEqual(cornerSource.Points, firstDiscrete.Points);
        }

        using var stamp = new SKPath();
        stamp.MoveTo(0f, -1f);
        stamp.LineTo(2f, 0f);
        stamp.LineTo(0f, 1f);
        stamp.Close();
        using var path1D = SKPathEffect.Create1DPath(stamp, 20f, 0f, SKPath1DPathEffectStyle.Rotate);
        Assert.True(path1D.TryApply(cornerSource, 1f, out var stamped));
        using (stamped)
        {
            Assert.True(stamped.Geometry.Figures.Count >= 3);
        }

        using var morph = SKPathEffect.Create1DPath(stamp, 20f, 0f, SKPath1DPathEffectStyle.Morph);
        Assert.True(morph.TryApply(cornerSource, 1f, out var morphed));
        using (morphed)
        {
            Assert.False(morphed.IsEmpty);
        }
    }

    [Fact]
    public void NonDashEffectIsMaterializedBeforeRetainedCanvasRecording()
    {
        var context = new DrawingContext();
        using var canvas = new SKCanvas(context, 100f, 20f);
        using var source = new SKPath();
        source.MoveTo(0f, 10f);
        source.LineTo(100f, 10f);
        using var trim = SKPathEffect.CreateTrim(0.25f, 0.75f);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            PathEffect = trim,
        };

        canvas.DrawPath(source, paint);

        var command = Assert.Single(context.Commands);
        Assert.NotNull(command.Path);
        Assert.True(command.Path!.TryGetBounds(out var minimum, out var maximum));
        Assert.InRange(minimum.X, 24.99f, 25.01f);
        Assert.InRange(maximum.X, 74.99f, 75.01f);
    }

    private static void AssertParameters(string name, int count, params string[] expected)
    {
        var method = typeof(SKPathEffect).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == count);
        Assert.Equal(expected, method.GetParameters().Select(static parameter => parameter.Name));
    }
}
