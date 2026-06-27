using System.Numerics;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class LineGeometryHitTestingTests
{
    [Fact]
    public void ContainsFillAlwaysRejectsOpenLine()
    {
        Assert.False(LineGeometryHitTesting.ContainsFill(
            new Vector2(5f, 0f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f)));
    }

    [Fact]
    public void ContainsStrokeAcceptsPointOnSolidLineBody()
    {
        Assert.True(LineGeometryHitTesting.ContainsStroke(
            new Vector2(5f, 0.9f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: 0f,
            relativeTolerance: false,
            LineGeometryCap.Flat,
            LineGeometryCap.Flat));
    }

    [Fact]
    public void ContainsStrokeRejectsPointOutsideFlatCap()
    {
        Assert.False(LineGeometryHitTesting.ContainsStroke(
            new Vector2(-0.1f, 0f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: 0f,
            relativeTolerance: false,
            LineGeometryCap.Flat,
            LineGeometryCap.Flat));
    }

    [Fact]
    public void ContainsStrokeHonorsSquareCapExtension()
    {
        Assert.True(LineGeometryHitTesting.ContainsStroke(
            new Vector2(-0.9f, 0f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: 0f,
            relativeTolerance: false,
            LineGeometryCap.Square,
            LineGeometryCap.Flat));
    }

    [Fact]
    public void ContainsStrokeHonorsRoundCap()
    {
        Assert.True(LineGeometryHitTesting.ContainsStroke(
            new Vector2(-0.6f, 0.6f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: 0f,
            relativeTolerance: false,
            LineGeometryCap.Round,
            LineGeometryCap.Flat));
    }

    [Fact]
    public void ContainsStrokeHonorsTriangleCap()
    {
        Assert.True(LineGeometryHitTesting.ContainsStroke(
            new Vector2(10.5f, 0f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: 0f,
            relativeTolerance: false,
            LineGeometryCap.Flat,
            LineGeometryCap.Triangle));
    }

    [Fact]
    public void ContainsStrokeAppliesRelativeTolerance()
    {
        Assert.True(LineGeometryHitTesting.ContainsStroke(
            new Vector2(5f, 1.9f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: 0.1f,
            relativeTolerance: true,
            LineGeometryCap.Flat,
            LineGeometryCap.Flat));
    }

    [Fact]
    public void ContainsStrokeRejectsInvalidTolerance()
    {
        Assert.False(LineGeometryHitTesting.ContainsStroke(
            new Vector2(5f, 0f),
            new Vector2(0f, 0f),
            new Vector2(10f, 0f),
            strokeThickness: 2f,
            tolerance: float.NaN,
            relativeTolerance: false,
            LineGeometryCap.Flat,
            LineGeometryCap.Flat));
    }
}
