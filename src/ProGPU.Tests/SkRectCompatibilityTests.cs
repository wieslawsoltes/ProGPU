using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkRectCompatibilityTests
{
    [Fact]
    public void LocationSizeMidpointsAndStandardizationMatchNative()
    {
        var rect = new SKRect(2f, 3f, 12f, 23f);
        Assert.Equal(10f, rect.Width);
        Assert.Equal(20f, rect.Height);
        Assert.Equal(7f, rect.MidX);
        Assert.Equal(13f, rect.MidY);
        Assert.Equal(new SKPoint(2f, 3f), rect.Location);
        Assert.Equal(new SKSize(10f, 20f), rect.Size);

        rect.Size = new SKSize(5f, 6f);
        Assert.Equal(new SKRect(2f, 3f, 7f, 9f), rect);
        rect.Location = new SKPoint(10f, 20f);
        Assert.Equal(new SKRect(10f, 20f, 15f, 26f), rect);

        Assert.Equal(new SKRect(1f, 2f, 8f, 9f), new SKRect(8f, 9f, 1f, 2f).Standardized);
        Assert.Equal(new SKRect(1f, 2f, 8f, 9f), new SKRect(1f, 9f, 8f, 2f).Standardized);
        Assert.True(SKRect.Empty.IsEmpty);
        Assert.False(new SKRect(1f, 1f, 1f, 1f).IsEmpty);
    }

    [Fact]
    public void AspectFitAndFillPreserveCenterAndFloatEdges()
    {
        var rect = new SKRect(0f, 0f, 100f, 50f);
        Assert.Equal(new SKRect(25f, 0f, 75f, 50f), rect.AspectFit(new SKSize(1f, 1f)));
        Assert.Equal(new SKRect(0f, -25f, 100f, 75f), rect.AspectFill(new SKSize(1f, 1f)));

        var odd = new SKRect(0f, 0f, 101f, 51f);
        var fit = odd.AspectFit(new SKSize(16f, 9f));
        Assert.Equal(5.166668f, fit.Left, precision: 5);
        Assert.Equal(0f, fit.Top);
        Assert.Equal(95.83333f, fit.Right, precision: 5);
        Assert.Equal(51f, fit.Bottom);
        Assert.Equal(new SKRect(50.5f, 25.5f, 50.5f, 25.5f), odd.AspectFit(SKSize.Empty));
    }

    [Fact]
    public void ContainmentAndIntersectionUseHalfOpenAndInclusiveContracts()
    {
        var rect = new SKRect(0f, 0f, 10f, 10f);
        Assert.True(rect.Contains(0f, 0f));
        Assert.True(rect.Contains(new SKPoint(9.999f, 9.999f)));
        Assert.False(rect.Contains(10f, 9f));
        Assert.False(rect.Contains(9f, 10f));
        Assert.True(rect.Contains(new SKRect(2f, 3f, 8f, 9f)));

        var touching = new SKRect(10f, 5f, 20f, 15f);
        Assert.False(rect.IntersectsWith(touching));
        Assert.True(rect.IntersectsWithInclusive(touching));
        Assert.Equal(new SKRect(10f, 5f, 10f, 10f), SKRect.Intersect(rect, touching));
        Assert.Equal(SKRect.Empty, SKRect.Intersect(rect, new SKRect(11f, 0f, 20f, 10f)));
    }

    [Fact]
    public void InflateOffsetIntersectAndUnionMutateByValue()
    {
        var original = new SKRect(10f, 20f, 30f, 40f);
        Assert.Equal(new SKRect(7f, 16f, 33f, 44f), SKRect.Inflate(original, 3f, 4f));
        Assert.Equal(new SKRect(10f, 20f, 30f, 40f), original);

        original.Inflate(new SKSize(2f, 5f));
        Assert.Equal(new SKRect(8f, 15f, 32f, 45f), original);
        original.Offset(new SKPoint(-3f, 7f));
        Assert.Equal(new SKRect(5f, 22f, 29f, 52f), original);

        original.Intersect(new SKRect(0f, 30f, 20f, 60f));
        Assert.Equal(new SKRect(5f, 30f, 20f, 52f), original);
        original.Union(new SKRect(-4f, 40f, 8f, 70f));
        Assert.Equal(new SKRect(-4f, 30f, 20f, 70f), original);
        Assert.Equal(new SKRect(0f, 0f, 4f, 5f), SKRect.Union(SKRect.Empty, new SKRect(2f, 3f, 4f, 5f)));
    }

    [Fact]
    public void ConversionCreationAndUnionMatchCoordinateSemantics()
    {
        Assert.Equal(new SKRect(2f, 3f, 6f, 8f), SKRect.Create(new SKPoint(2f, 3f), new SKSize(4f, 5f)));
        Assert.Equal(new SKRect(0f, 0f, 4f, 5f), SKRect.Create(new SKSize(4f, 5f)));
        Assert.Equal(new SKRect(0f, 0f, 4f, 5f), SKRect.Create(4f, 5f));
        Assert.Equal(new SKRect(2f, 3f, 6f, 8f), SKRect.Create(2f, 3f, 4f, 5f));

        SKRect converted = new SKRectI(1, 2, 3, 4);
        Assert.Equal(new SKRect(1f, 2f, 3f, 4f), converted);
        Assert.Equal(new SKRect(0f, 0f, 70f, 90f), SKRect.Union(SKRect.Empty, new SKRect(50f, 60f, 70f, 90f)));
    }

    [Fact]
    public void EqualityHashAndFormattingMatchValueSemantics()
    {
        var rect = new SKRect(2f, 3f, 6f, 8f);
        Assert.True(rect == new SKRect(2f, 3f, 6f, 8f));
        Assert.False(rect != new SKRect(2f, 3f, 6f, 8f));
        Assert.Equal(rect.GetHashCode(), new SKRect(2f, 3f, 6f, 8f).GetHashCode());
        Assert.Equal("{Left=2,Top=3,Width=4,Height=5}", rect.ToString());
    }
}
