using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkRoundRectCompatibilityTests
{
    private static readonly SKPoint[] s_complexRadii =
    [
        new(10f, 2f),
        new(4f, 6f),
        new(7f, 8f),
        new(9f, 5f),
    ];

    [Fact]
    public void EnumValuesMatchNative()
    {
        Assert.Equal(0, (int)SKRoundRectCorner.UpperLeft);
        Assert.Equal(1, (int)SKRoundRectCorner.UpperRight);
        Assert.Equal(2, (int)SKRoundRectCorner.LowerRight);
        Assert.Equal(3, (int)SKRoundRectCorner.LowerLeft);
        Assert.Equal(0, (int)SKRoundRectType.Empty);
        Assert.Equal(5, (int)SKRoundRectType.Complex);
    }

    [Fact]
    public void EmptyAndRectConstructionMatchesNative()
    {
        using var empty = new SKRoundRect();
        Assert.Equal(SKRect.Empty, empty.Rect);
        Assert.Equal(SKRoundRectType.Empty, empty.Type);
        Assert.Equal(0f, empty.Width);
        Assert.Equal(0f, empty.Height);
        Assert.True(empty.IsValid);
        Assert.True(empty.AllCornersCircular);

        using var rect = new SKRoundRect(new SKRect(30f, 20f, 10f, 5f));
        Assert.Equal(new SKRect(10f, 5f, 30f, 20f), rect.Rect);
        Assert.Equal(SKRoundRectType.Rect, rect.Type);
        Assert.All(rect.Radii, radius => Assert.Equal(default, radius));

        using var invalid = new SKRoundRect(new SKRect(float.NaN, 0f, 10f, 10f));
        Assert.Equal(SKRoundRectType.Empty, invalid.Type);
        Assert.Equal(SKRect.Empty, invalid.Rect);
    }

    [Fact]
    public void RoundRectUsesOfficialSkObjectOwnership()
    {
        var value = new SKRoundRect(new SKRect(0f, 0f, 20f, 10f), 2f, 3f);
        Assert.IsAssignableFrom<SKObject>(value);
        Assert.NotEqual(IntPtr.Zero, value.Handle);

        value.Dispose();
        Assert.Equal(IntPtr.Zero, value.Handle);
        value.Dispose();
    }

    [Fact]
    public void ConstructionKeepsCornerRadiiInSingleManagedAllocation()
    {
        const int iterations = 10_000;
        var rect = new SKRect(0f, 0f, 64f, 48f);

        using (var warmup = new SKRoundRect(rect, 8f, 4f))
        {
            Assert.Equal(new SKPoint(8f, 4f), warmup.GetRadii(SKRoundRectCorner.UpperLeft));
        }

        var checksum = 0f;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            using var value = new SKRoundRect(rect, (index & 15) + 1f, 4f);
            checksum += value.GetRadii(SKRoundRectCorner.UpperLeft).X;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(85_000f, checksum);
        Assert.True(
            allocated <= 96L * iterations,
            $"Expected one bounded SKRoundRect allocation per construction, but measured {allocated / (double)iterations:F3} B/op.");
    }

    [Fact]
    public void UniformAndOvalConstructionMatchesNative()
    {
        using var uniform = new SKRoundRect(new SKRect(0f, 0f, 20f, 10f), 100f, 30f);
        Assert.Equal(SKRoundRectType.Simple, uniform.Type);
        Assert.All(uniform.Radii, radius => Assert.Equal(new SKPoint(10f, 3f), radius));
        Assert.False(uniform.AllCornersCircular);

        using var circular = new SKRoundRect(new SKRect(0f, 0f, 20f, 20f), 4f);
        Assert.Equal(SKRoundRectType.Simple, circular.Type);
        Assert.True(circular.AllCornersCircular);

        uniform.SetOval(new SKRect(0f, 0f, 20f, 10f));
        Assert.Equal(SKRoundRectType.Oval, uniform.Type);
        Assert.All(uniform.Radii, radius => Assert.Equal(new SKPoint(10f, 5f), radius));

        uniform.SetRect(new SKRect(0f, 0f, 20f, 10f), -2f, 3f);
        Assert.Equal(SKRoundRectType.Rect, uniform.Type);
        Assert.All(uniform.Radii, radius => Assert.Equal(default, radius));
    }

    [Fact]
    public void NinePatchAndComplexRadiiMatchNative()
    {
        using var value = new SKRoundRect();
        value.SetNinePatch(new SKRect(0f, 0f, 20f, 10f), 8f, 3f, 4f, 2f);
        Assert.Equal(SKRoundRectType.NinePatch, value.Type);
        Assert.Equal(
            new[] { new SKPoint(8f, 3f), new SKPoint(4f, 3f), new SKPoint(4f, 2f), new SKPoint(8f, 2f) },
            value.Radii);

        value.SetRectRadii(new SKRect(0f, 0f, 20f, 10f), s_complexRadii);
        Assert.Equal(SKRoundRectType.Complex, value.Type);
        Assert.Equal(
            new[]
            {
                new SKPoint(7.142857f, 1.4285715f),
                new SKPoint(2.857143f, 4.285714f),
                new SKPoint(5f, 5.714286f),
                new SKPoint(6.428571f, 3.5714285f),
            },
            value.Radii);
        Assert.True(value.IsValid);
    }

    [Fact]
    public void RadiiAndCopyConstructionOwnIndependentSnapshots()
    {
        using var source = CreateComplex();
        var radii = source.Radii;
        radii[0] = new SKPoint(99f, 99f);
        Assert.Equal(new SKPoint(7.142857f, 1.4285715f), source.GetRadii(SKRoundRectCorner.UpperLeft));

        using var copy = new SKRoundRect(source);
        source.SetRect(SKRect.Empty);
        Assert.Equal(SKRoundRectType.Complex, copy.Type);
        Assert.Equal(new SKRect(0f, 0f, 20f, 10f), copy.Rect);
        Assert.Throws<NullReferenceException>(() => new SKRoundRect(null!));
    }

    [Fact]
    public void SetRectRadiiValidationMatchesNative()
    {
        using var value = new SKRoundRect();
        Assert.Equal(
            "radii",
            Assert.Throws<ArgumentNullException>(() => value.SetRectRadii(SKRect.Empty, (SKPoint[])null!)).ParamName);
        Assert.Equal(
            "radii",
            Assert.Throws<ArgumentException>(() => value.SetRectRadii(SKRect.Empty, new SKPoint[3])).ParamName);
        Assert.Equal(
            "radii",
            Assert.Throws<ArgumentException>(() => value.SetRectRadii(SKRect.Empty, new SKPoint[5].AsSpan())).ParamName);

        value.SetRectRadii(
            new SKRect(0f, 0f, 20f, 10f),
            [new SKPoint(float.NaN, 2f), new(2f, 2f), new(2f, 2f), new(2f, 2f)]);
        Assert.Equal(SKRoundRectType.Rect, value.Type);
    }

    [Fact]
    public void CircularToleranceMatchesNative()
    {
        using var value = new SKRoundRect(
            new SKRect(0f, 0f, 20f, 20f),
            4f,
            4f + 1f / 8192f);
        Assert.True(value.AllCornersCircular);
        Assert.False(value.CheckAllCornersCircular(1f / 16384f));
        Assert.False(value.CheckAllCornersCircular(-1f));
    }

    [Fact]
    public void ContainsChecksRoundedCorners()
    {
        using var value = CreateComplex();
        Assert.True(value.Contains(new SKRect(8f, 4f, 12f, 6f)));
        Assert.False(value.Contains(new SKRect(0f, 0f, 1f, 1f)));
        Assert.False(value.Contains(new SKRect(-1f, 4f, 1f, 6f)));

        value.SetRect(new SKRect(0f, 0f, 20f, 10f));
        Assert.True(value.Contains(new SKRect(0f, 0f, 20f, 10f)));
    }

    [Fact]
    public void DeflateInflateAndSizeOverloadsMatchNative()
    {
        using var value = CreateComplex();
        value.Deflate(new SKSize(3f, 2f));
        Assert.Equal(new SKRect(3f, 2f, 17f, 8f), value.Rect);
        Assert.Equal(
            new[] { default, default, new SKPoint(2f, 3.7142859f), new SKPoint(3.4285712f, 1.5714285f) },
            value.Radii);

        value.Inflate(new SKSize(5f, 4f));
        Assert.Equal(new SKRect(-2f, -2f, 22f, 12f), value.Rect);
        Assert.Equal(
            new[] { default, default, new SKPoint(7f, 7.714286f), new SKPoint(8.428572f, 5.5714283f) },
            value.Radii);
    }

    [Fact]
    public void ExcessiveDeflatePreservesCenteredEmptyBounds()
    {
        using var value = new SKRoundRect(new SKRect(0f, 0f, 20f, 10f), 4f);
        value.Deflate(20f, 20f);
        Assert.Equal(SKRoundRectType.Empty, value.Type);
        Assert.Equal(new SKRect(10f, 5f, 10f, 5f), value.Rect);
        Assert.True(value.IsValid);
    }

    [Fact]
    public void OffsetOverloadsPreserveRadii()
    {
        using var value = CreateComplex();
        var radii = value.Radii;
        value.Offset(new SKPoint(3f, -2f));
        value.Offset(4f, 5f);
        Assert.Equal(new SKRect(7f, 3f, 27f, 13f), value.Rect);
        Assert.Equal(radii, value.Radii);
    }

    [Fact]
    public void AxisAlignedTransformsMatchNative()
    {
        using var value = CreateComplex();
        using var scaled = value.Transform(new SKMatrix(2f, 0f, 0f, 0f, 3f, 0f, 0f, 0f, 1f));
        Assert.NotNull(scaled);
        Assert.Equal(new SKRect(0f, 0f, 40f, 30f), scaled.Rect);
        Assert.Equal(
            new[]
            {
                new SKPoint(14.285714f, 4.285714f),
                new SKPoint(5.714287f, 12.857142f),
                new SKPoint(10f, 17.142857f),
                new SKPoint(12.857142f, 10.714285f),
            },
            scaled.Radii);

        Assert.True(value.TryTransform(
            new SKMatrix(0f, -2f, 20f, 3f, 0f, 0f, 0f, 0f, 1f),
            out var rotated));
        Assert.NotNull(rotated);
        using (rotated)
        {
            Assert.Equal(new SKRect(0f, 0f, 20f, 60f), rotated.Rect);
            Assert.Equal(new SKPoint(7.1428566f, 19.285713f), rotated.Radii[0]);
        }
    }

    [Fact]
    public void NonAxisAlignedTransformsReturnNull()
    {
        using var value = CreateComplex();
        var rotation = SKMatrix.CreateRotationDegrees(30f);
        Assert.False(value.TryTransform(rotation, out var transformed));
        Assert.Null(transformed);
        Assert.Null(value.Transform(rotation));

        var perspective = SKMatrix.Identity;
        perspective.Persp0 = 0.01f;
        Assert.False(value.TryTransform(perspective, out transformed));
        Assert.Null(transformed);
    }

    private static SKRoundRect CreateComplex()
    {
        var value = new SKRoundRect();
        value.SetRectRadii(new SKRect(0f, 0f, 20f, 10f), s_complexRadii);
        return value;
    }
}
