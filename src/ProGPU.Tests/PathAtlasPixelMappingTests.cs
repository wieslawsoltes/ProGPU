using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class PathAtlasPixelMappingTests
{
    private static Vector2[] Positions() => [new(4, 36), new(52, 36), new(52, 68), new(4, 68)];
    private static Vector2[] Atlas() => [new(2, 2), new(50, 2), new(50, 34), new(2, 34)];

    [Fact]
    public void ExactTranslationAdmitsAllFourCornersIncludingNegativeCoordinates()
    {
        var positions = Positions();
        Assert.True(PathAtlasPixelMapping.IsExact(positions, Atlas()));
        for (int i = 0; i < 4; i++) positions[i] -= new Vector2(100, 100);
        Assert.True(PathAtlasPixelMapping.IsExact(positions, Atlas()));
    }

    [Fact]
    public void EveryCornerAndBothAxesRequireFiniteExactIntegerCoordinates()
    {
        foreach (bool changeAtlas in new[] { false, true })
        for (int corner = 0; corner < 4; corner++)
        for (int axis = 0; axis < 2; axis++)
        foreach (float value in new[] { 0.25f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 8388609f, -8388609f })
        {
            var positions = Positions();
            var atlas = Atlas();
            var changed = changeAtlas ? atlas : positions;
            changed[corner] = axis == 0 ? new(value, changed[corner].Y) : new(changed[corner].X, value);
            Assert.False(PathAtlasPixelMapping.IsExact(positions, atlas));
        }
    }

    [Fact]
    public void IntegerBoundsDoNotAdmitScaleMirrorRotationOrOneChangedCorner()
    {
        foreach (var transform in new[] {
            Matrix3x2.CreateScale(2), Matrix3x2.CreateScale(-1, 1),
            new Matrix3x2(0, 1, -1, 0, 0, 0), Matrix3x2.CreateTranslation(0.5f, 0.5f) })
        {
            var positions = Positions();
            for (int i = 0; i < 4; i++) positions[i] = Vector2.Transform(positions[i], transform);
            Assert.False(PathAtlasPixelMapping.IsExact(positions, Atlas()));
        }
        for (int i = 0; i < 4; i++)
        {
            var atlas = Atlas();
            atlas[i].X += 1;
            Assert.False(PathAtlasPixelMapping.IsExact(Positions(), atlas));
        }
    }

    [Fact]
    public void BothSpansMustContainExactlyFourCorners()
    {
        Assert.False(PathAtlasPixelMapping.IsExact([], Atlas()));
        Assert.False(PathAtlasPixelMapping.IsExact(Positions(), []));
        Assert.False(PathAtlasPixelMapping.IsExact(Positions().AsSpan(0, 3), Atlas()));
        Assert.False(PathAtlasPixelMapping.IsExact(Positions(), new Vector2[5]));
    }
}
