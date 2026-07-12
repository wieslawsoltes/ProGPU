using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkFontTransformTests
{
    [Fact]
    public void GetTextPathAppliesHorizontalFontScale()
    {
        using var normal = new SKFont(SKTypeface.Default, 40f);
        using var stretched = new SKFont(SKTypeface.Default, 40f, scaleX: 1.5f);
        using var normalPath = normal.GetTextPath("A");
        using var stretchedPath = stretched.GetTextPath("A");

        AssertNear(normalPath.Bounds.Left * 1.5f, stretchedPath.Bounds.Left);
        AssertNear(normalPath.Bounds.Right * 1.5f, stretchedPath.Bounds.Right);
        AssertNear(normalPath.Bounds.Top, stretchedPath.Bounds.Top);
        AssertNear(normalPath.Bounds.Bottom, stretchedPath.Bounds.Bottom);
    }

    [Fact]
    public void FontSkewChangesMeasuredBoundsButNotTextPath()
    {
        using var normal = new SKFont(SKTypeface.Default, 40f);
        using var skewed = new SKFont(SKTypeface.Default, 40f, scaleX: 1f, skewX: 0.25f);
        using var normalPath = normal.GetTextPath("A");
        using var skewedPath = skewed.GetTextPath("A");

        normal.MeasureText("A", out var normalBounds);
        skewed.MeasureText("A", out var skewedBounds);

        Assert.Equal(normalPath.Bounds, skewedPath.Bounds);
        Assert.True(skewedBounds.Left < normalBounds.Left);
        AssertNear(normalBounds.Right, skewedBounds.Right);
        Assert.Equal(MathF.Floor(normalPath.Bounds.Left) - 1f, normalBounds.Left);
        Assert.Equal(MathF.Floor(normalPath.Bounds.Top) - 1f, normalBounds.Top);
        Assert.Equal(MathF.Ceiling(normalPath.Bounds.Right) + 1f, normalBounds.Right);
        Assert.Equal(MathF.Ceiling(normalPath.Bounds.Bottom) + 1f, normalBounds.Bottom);
    }

    [Fact]
    public void GetGlyphWidthsAppliesHorizontalFontScale()
    {
        using var normal = new SKFont(SKTypeface.Default, 40f);
        using var stretched = new SKFont(SKTypeface.Default, 40f, scaleX: 1.5f);
        var glyph = SKTypeface.Default.Font.GetGlyphIndex('A');
        Span<ushort> glyphs = [glyph];
        Span<float> normalWidths = stackalloc float[1];
        Span<float> stretchedWidths = stackalloc float[1];
        Span<SKRect> normalBounds = stackalloc SKRect[1];
        Span<SKRect> stretchedBounds = stackalloc SKRect[1];

        normal.GetGlyphWidths(glyphs, normalWidths, normalBounds);
        stretched.GetGlyphWidths(glyphs, stretchedWidths, stretchedBounds);

        AssertNear(normalWidths[0] * 1.5f, stretchedWidths[0]);
        Assert.True(stretchedBounds[0].Width > normalBounds[0].Width);
        AssertNear(normalBounds[0].Top, stretchedBounds[0].Top);
        AssertNear(normalBounds[0].Bottom, stretchedBounds[0].Bottom);
    }

    private static void AssertNear(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
}
