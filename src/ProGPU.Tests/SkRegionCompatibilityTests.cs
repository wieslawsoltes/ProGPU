using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkRegionCompatibilityTests
{
    [Fact]
    public void ConstructorsCopyPropertiesAndTranslationMatchNative()
    {
        using var source = new SKRegion(new SKRectI(1, 2, 11, 12));
        using var copy = new SKRegion(source);

        Assert.True(source.IsRect);
        Assert.False(source.IsComplex);
        Assert.Equal(new SKRectI(1, 2, 11, 12), copy.Bounds);
        copy.Translate(5, -2);
        Assert.Equal(new SKRectI(6, 0, 16, 10), copy.Bounds);
        Assert.Equal(new SKRectI(1, 2, 11, 12), source.Bounds);

        using var assigned = new SKRegion();
        Assert.True(assigned.SetRegion(copy));
        Assert.Equal(copy.Bounds, assigned.Bounds);
        Assert.True(assigned.SetRegion(assigned));
    }

    [Fact]
    public void UnionNormalizesIntoNativeHorizontalBands()
    {
        using var region = new SKRegion(new SKRectI(0, 0, 10, 10));
        Assert.True(region.Op(new SKRectI(5, 5, 15, 15), SKRegionOperation.Union));

        Assert.True(region.IsComplex);
        Assert.False(region.IsRect);
        Assert.Equal(new SKRectI(0, 0, 15, 15), region.Bounds);
        Assert.Equal(
            new[]
            {
                new SKRectI(0, 0, 10, 5),
                new SKRectI(0, 5, 15, 10),
                new SKRectI(5, 10, 15, 15),
            },
            ReadRects(region.CreateRectIterator()));
    }

    [Fact]
    public void ClipAndSpanIteratorsMatchNativeCullingAndExclusiveRuns()
    {
        using var region = new SKRegion(new SKRectI(0, 0, 10, 10));
        region.Op(new SKRectI(5, 5, 15, 15), SKRegionOperation.Union);

        using var clipIterator = region.CreateClipIterator(new SKRectI(3, 4, 12, 8));
        Assert.Equal(
            new[]
            {
                new SKRectI(0, 0, 10, 5),
                new SKRectI(0, 5, 15, 10),
            },
            ReadRects(clipIterator));

        using var spanIterator = region.CreateSpanIterator(6, 3, 12);
        Assert.True(spanIterator.Next(out var left, out var right));
        Assert.Equal(3, left);
        Assert.Equal(12, right);
        Assert.False(spanIterator.Next(out left, out right));
        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void IteratorsSnapshotRegionStateAndDeriveFromSkObject()
    {
        using var region = new SKRegion(new SKRectI(0, 0, 10, 10));
        using var iterator = region.CreateRectIterator();
        SKObject ownedIterator = iterator;
        Assert.NotEqual(IntPtr.Zero, ownedIterator.Handle);

        region.Translate(100, 100);

        Assert.True(iterator.Next(out var original));
        Assert.Equal(new SKRectI(0, 0, 10, 10), original);
    }

    [Fact]
    public void SetRectsUnionsAndCanonicalizesInput()
    {
        using var region = new SKRegion();
        Assert.True(region.SetRects(
            [
                new SKRectI(0, 0, 10, 10),
                new SKRectI(10, 0, 20, 10),
                new SKRectI(5, 5, 15, 15),
                SKRectI.Empty,
            ]));

        Assert.Equal(new SKRectI(0, 0, 20, 15), region.Bounds);
        Assert.True(region.Contains(new SKPointI(19, 9)));
        Assert.True(region.Contains(new SKRectI(1, 1, 19, 9)));
        Assert.False(region.Contains(new SKRectI(0, 0, 20, 15)));
    }

    [Fact]
    public void SetPathRasterizesArbitraryFillInsideClipAtPixelCenters()
    {
        using var circle = new SKPath();
        circle.AddCircle(5f, 5f, 5f);
        using var clip = new SKRegion(new SKRectI(0, 0, 5, 10));
        using var region = new SKRegion();

        Assert.True(region.SetPath(circle, clip));

        Assert.True(region.Contains(4, 5));
        Assert.False(region.Contains(5, 5));
        Assert.False(region.Contains(0, 0));
        Assert.Equal(new SKRectI(0, 0, 5, 10), region.Bounds);
        Assert.False(region.SetPath(circle));
    }

    [Fact]
    public void PathConstructorAndPathQueriesUseRasterizedRegion()
    {
        using var triangle = new SKPath();
        triangle.MoveTo(0f, 0f);
        triangle.LineTo(10f, 0f);
        triangle.LineTo(5f, 10f);
        triangle.Close();
        using var region = new SKRegion(triangle);

        Assert.True(region.Contains(5, 2));
        Assert.False(region.Contains(0, 9));
        Assert.True(region.Intersects(triangle));
        Assert.True(region.Contains(triangle));
        Assert.False(region.QuickReject(triangle));

        using var distant = new SKPath();
        distant.AddRect(new SKRect(100f, 100f, 110f, 110f));
        Assert.True(region.QuickReject(distant));
        Assert.False(region.Intersects(distant));
    }

    [Fact]
    public void RegionOperationsHandleRegionAndPathOperands()
    {
        using var left = new SKRegion(new SKRectI(0, 0, 10, 10));
        using var right = new SKRegion(new SKRectI(5, 0, 15, 10));
        using var intersection = new SKRegion(left);
        Assert.True(intersection.Op(right, SKRegionOperation.Intersect));
        Assert.Equal(new SKRectI(5, 0, 10, 10), intersection.Bounds);

        using var difference = new SKRegion(left);
        Assert.True(difference.Op(right, SKRegionOperation.Difference));
        Assert.Equal(new SKRectI(0, 0, 5, 10), difference.Bounds);

        using var reverseDifference = new SKRegion(left);
        Assert.True(reverseDifference.Op(right, SKRegionOperation.ReverseDifference));
        Assert.Equal(new SKRectI(10, 0, 15, 10), reverseDifference.Bounds);

        using var xor = new SKRegion(left);
        Assert.True(xor.Op(right, SKRegionOperation.XOR));
        Assert.True(xor.Contains(2, 5));
        Assert.False(xor.Contains(7, 5));
        Assert.True(xor.Contains(12, 5));

        using var path = new SKPath();
        path.AddRect(new SKRect(20f, 0f, 25f, 5f));
        Assert.True(xor.Op(path, SKRegionOperation.Union));
        Assert.True(xor.Contains(22, 2));
    }

    [Fact]
    public void QuickQueriesAndBoundaryPathUseCanonicalCoverage()
    {
        using var region = new SKRegion(new SKRectI(0, 0, 10, 10));
        region.Op(new SKRectI(10, 0, 20, 10), SKRegionOperation.Union);

        Assert.True(region.IsRect);
        Assert.True(region.QuickContains(new SKRectI(1, 1, 19, 9)));
        Assert.False(region.QuickReject(new SKRectI(19, 9, 30, 20)));
        Assert.True(region.QuickReject(new SKRectI(20, 10, 30, 20)));

        using var boundary = region.GetBoundaryPath();
        Assert.True(boundary.Contains(5f, 5f));
        Assert.True(boundary.Contains(15f, 5f));
        Assert.False(boundary.Contains(25f, 5f));
    }

    private static SKRectI[] ReadRects(SKRegion.RectIterator iterator)
    {
        var rects = new List<SKRectI>();
        while (iterator.Next(out var rect))
        {
            rects.Add(rect);
        }
        return rects.ToArray();
    }

    private static SKRectI[] ReadRects(SKRegion.ClipIterator iterator)
    {
        var rects = new List<SKRectI>();
        while (iterator.Next(out var rect))
        {
            rects.Add(rect);
        }
        return rects.ToArray();
    }
}
