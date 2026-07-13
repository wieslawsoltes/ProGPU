using System;
using System.Reflection;
using Xunit;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsUnit = System.Drawing.GraphicsUnit;
using DrawingFont = System.Drawing.Font;
using DrawingFontFamily = System.Drawing.FontFamily;
using DrawingFontStyle = System.Drawing.FontStyle;

namespace ProGPU.Tests;

public sealed class GdiTextUnitTests
{
    [Theory]
    [InlineData(DrawingGraphicsUnit.Point, 12f, 96f, 16f)]
    [InlineData(DrawingGraphicsUnit.Inch, 0.25f, 96f, 24f)]
    [InlineData(DrawingGraphicsUnit.Document, 75f, 96f, 24f)]
    [InlineData(DrawingGraphicsUnit.Millimeter, 25.4f, 96f, 96f)]
    [InlineData(DrawingGraphicsUnit.Pixel, 18f, 96f, 18f)]
    [InlineData(DrawingGraphicsUnit.Display, 19f, 96f, 19f)]
    [InlineData(DrawingGraphicsUnit.World, 20f, 96f, 20f)]
    public void FontUnitConversionMatchesGdiPixelSemantics(
        DrawingGraphicsUnit unit,
        float size,
        float dpi,
        float expectedPixels)
    {
        AssertNear(expectedPixels, ConvertFontSizeToPixels(size, unit, dpi));
    }

    [Fact]
    public void FontsAndFamiliesUseSystemDrawingValueEquality()
    {
        using var first = new DrawingFont("Microsoft Sans Serif", 10f, DrawingFontStyle.Regular, DrawingGraphicsUnit.Point);
        using var equivalent = new DrawingFont("Microsoft Sans Serif", 10f, DrawingFontStyle.Regular, DrawingGraphicsUnit.Point);
        using var bold = new DrawingFont("Microsoft Sans Serif", 10f, DrawingFontStyle.Bold, DrawingGraphicsUnit.Point);
        using var firstFamily = new DrawingFontFamily("Microsoft Sans Serif");
        using var equivalentFamily = new DrawingFontFamily("Microsoft Sans Serif");

        Assert.Equal(firstFamily, equivalentFamily);
        Assert.Equal(firstFamily.GetHashCode(), equivalentFamily.GetHashCode());
        Assert.Equal(first, equivalent);
        Assert.Equal(first.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(first, bold);
    }

    private static float ConvertFontSizeToPixels(float size, DrawingGraphicsUnit unit, float dpi)
    {
        var method = typeof(DrawingGraphics).GetMethod(
            "ConvertFontSizeToPixels",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (float)method.Invoke(null, [size, unit, dpi])!;
    }

    private static void AssertNear(float expected, float actual)
    {
        Assert.InRange(MathF.Abs(expected - actual), 0f, 0.0001f);
    }
}
