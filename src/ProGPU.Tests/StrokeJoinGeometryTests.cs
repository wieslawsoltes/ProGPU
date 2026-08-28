using System;
using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class StrokeJoinGeometryTests
{
    [Fact]
    public void OverLimitMiterUsesWpfClippedMiterTriangles()
    {
        Span<StrokeJoinTriangle> triangles =
            stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];

        var count = StrokeJoinGeometry.WriteLineJoin(
            triangles,
            PenLineJoin.Miter,
            thickness: 2f,
            miterLimit: 1f,
            previousPoint: Vector2.Zero,
            joinPoint: Vector2.UnitX,
            nextPoint: Vector2.One);

        Assert.Equal(3, count);
        AssertPoint(triangles[0].P0, 1f, 0f);
        AssertPoint(triangles[0].P1, 1f, -1f);
        AssertPoint(triangles[0].P2, 1.4142135f, -1f);
        AssertPoint(triangles[1].P1, 1.4142135f, -1f);
        AssertPoint(triangles[1].P2, 2f, -0.41421357f);
        AssertPoint(triangles[2].P2, 2f, 0f);

        var allocated = StrokeJoinGeometry.CreateLineJoin(
            PenLineJoin.Miter,
            thickness: 2f,
            miterLimit: 1f,
            previousPoint: Vector2.Zero,
            joinPoint: Vector2.UnitX,
            nextPoint: Vector2.One);

        Assert.Equal(count, allocated.Length);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(triangles[index].P0, allocated[index].P0);
            Assert.Equal(triangles[index].P1, allocated[index].P1);
            Assert.Equal(triangles[index].P2, allocated[index].P2);
        }
    }

    [Fact]
    public void WithinLimitMiterRetainsFullMiterTriangles()
    {
        Span<StrokeJoinTriangle> triangles =
            stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];

        var count = StrokeJoinGeometry.WriteLineJoin(
            triangles,
            PenLineJoin.Miter,
            thickness: 2f,
            miterLimit: 2f,
            previousPoint: Vector2.Zero,
            joinPoint: Vector2.UnitX,
            nextPoint: Vector2.One);

        Assert.Equal(2, count);
        AssertPoint(triangles[1].P1, 2f, -1f);
    }

    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    public void ReversalMiterAndBevelUseWpfHalfWidthSquare(PenLineJoin lineJoin)
    {
        Span<StrokeJoinTriangle> triangles =
            stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];

        var count = StrokeJoinGeometry.WriteLineJoin(
            triangles,
            lineJoin,
            thickness: 2f,
            miterLimit: 1f,
            previousPoint: new Vector2(0f, 1f),
            joinPoint: Vector2.Zero,
            nextPoint: new Vector2(0f, 1f));

        Assert.Equal(3, count);
        AssertPoint(triangles[0].P1, 1f, 0f);
        AssertPoint(triangles[0].P2, 1f, -1f);
        AssertPoint(triangles[1].P2, -1f, -1f);
        AssertPoint(triangles[2].P2, -1f, 0f);
    }

    [Fact]
    public void ReversalRoundUsesWpfIncomingSemicircle()
    {
        Span<StrokeJoinTriangle> triangles =
            stackalloc StrokeJoinTriangle[StrokeJoinGeometry.MaxTrianglesPerJoin];

        var count = StrokeJoinGeometry.WriteLineJoin(
            triangles,
            PenLineJoin.Round,
            thickness: 2f,
            miterLimit: 1f,
            previousPoint: new Vector2(0f, 1f),
            joinPoint: Vector2.Zero,
            nextPoint: new Vector2(0f, 1f));

        Assert.Equal(StrokeJoinGeometry.MaxTrianglesPerJoin, count);
        AssertPoint(triangles[0].P1, 1f, 0f);
        AssertPoint(triangles[3].P2, 0f, -1f);
        AssertPoint(triangles[^1].P2, -1f, 0f);
    }

    private static void AssertPoint(Vector2 actual, float x, float y)
    {
        Assert.InRange(MathF.Abs(actual.X - x), 0f, 0.000001f);
        Assert.InRange(MathF.Abs(actual.Y - y), 0f, 0.000001f);
    }
}
