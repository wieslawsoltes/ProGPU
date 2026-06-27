using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class RectangleGeometryHitTestingTests
{
    [Fact]
    public void ContainsFillAcceptsAxisAlignedRectanglePoint()
    {
        Assert.True(RectangleGeometryHitTesting.ContainsFill(
            new Vector2(50f, 25f),
            new Vector2(0f, 0f),
            new Vector2(100f, 50f),
            Vector2.Zero));
    }

    [Fact]
    public void ContainsFillRejectsPointOutsideRectangle()
    {
        Assert.False(RectangleGeometryHitTesting.ContainsFill(
            new Vector2(-1f, 25f),
            new Vector2(0f, 0f),
            new Vector2(100f, 50f),
            Vector2.Zero));
    }

    [Fact]
    public void ContainsFillUsesRoundedCornerEllipse()
    {
        var min = new Vector2(0f, 0f);
        var max = new Vector2(100f, 50f);
        var radius = new Vector2(20f, 10f);

        Assert.True(RectangleGeometryHitTesting.ContainsFill(new Vector2(15f, 5f), min, max, radius));
        Assert.False(RectangleGeometryHitTesting.ContainsFill(new Vector2(1f, 1f), min, max, radius));
    }

    [Fact]
    public void ContainsStrokeAcceptsPointOnRectangleStroke()
    {
        Assert.True(RectangleGeometryHitTesting.ContainsStroke(
            new Vector2(0f, 25f),
            new Vector2(0f, 0f),
            new Vector2(100f, 50f),
            Vector2.Zero,
            strokeThickness: 10f,
            tolerance: 0f,
            relativeTolerance: false));
    }

    [Fact]
    public void ContainsStrokeRejectsPointInsideStrokeHole()
    {
        Assert.False(RectangleGeometryHitTesting.ContainsStroke(
            new Vector2(50f, 25f),
            new Vector2(0f, 0f),
            new Vector2(100f, 50f),
            Vector2.Zero,
            strokeThickness: 10f,
            tolerance: 0f,
            relativeTolerance: false));
    }

    [Fact]
    public void ContainsStrokeAppliesRelativeTolerance()
    {
        Assert.True(RectangleGeometryHitTesting.ContainsStroke(
            new Vector2(-5f, 25f),
            new Vector2(0f, 0f),
            new Vector2(100f, 50f),
            Vector2.Zero,
            strokeThickness: 2f,
            tolerance: 0.04f,
            relativeTolerance: true));
    }

    [Fact]
    public void ContainsStrokeRejectsInvalidTolerance()
    {
        Assert.False(RectangleGeometryHitTesting.ContainsStroke(
            new Vector2(0f, 25f),
            new Vector2(0f, 0f),
            new Vector2(100f, 50f),
            Vector2.Zero,
            strokeThickness: 10f,
            tolerance: float.NaN,
            relativeTolerance: false));
    }

    [Fact]
    public void ContainsStrokeUsesRoundedCornerRing()
    {
        Assert.True(RectangleGeometryHitTesting.ContainsStroke(
            new Vector2(2f, 2f),
            new Vector2(0f, 0f),
            new Vector2(100f, 40f),
            new Vector2(10f, 10f),
            strokeThickness: 4f,
            tolerance: 0f,
            relativeTolerance: false));
    }
}
