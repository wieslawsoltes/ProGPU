using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkColorFilterBlendModeTests
{
    [Theory]
    [InlineData(SKBlendMode.Overlay, 173, 82, 133)]
    [InlineData(SKBlendMode.Darken, 51, 51, 102)]
    [InlineData(SKBlendMode.Lighten, 204, 204, 153)]
    [InlineData(SKBlendMode.ColorDodge, 255, 255, 255)]
    [InlineData(SKBlendMode.ColorBurn, 0, 0, 0)]
    [InlineData(SKBlendMode.HardLight, 82, 173, 122)]
    [InlineData(SKBlendMode.SoftLight, 180, 89, 141)]
    [InlineData(SKBlendMode.Difference, 153, 153, 51)]
    [InlineData(SKBlendMode.Exclusion, 173, 173, 133)]
    [InlineData(SKBlendMode.Hue, 12, 165, 63)]
    [InlineData(SKBlendMode.Saturation, 204, 51, 153)]
    [InlineData(SKBlendMode.Color, 12, 165, 63)]
    [InlineData(SKBlendMode.Luminosity, 243, 90, 192)]
    public void AdvancedBlendModesMatchNativeOpaqueOutput(
        SKBlendMode mode,
        byte red,
        byte green,
        byte blue)
    {
        var actual = ApplyFilter(
            new SKColor(51, 204, 102, 255),
            new SKColor(204, 51, 153, 255),
            mode);

        AssertColorNear(new SKColor(red, green, blue, 255), actual, tolerance: 0);
    }

    [Theory]
    [InlineData(SKBlendMode.Overlay, 122, 133, 122)]
    [InlineData(SKBlendMode.Darken, 83, 123, 113)]
    [InlineData(SKBlendMode.Lighten, 132, 172, 129)]
    [InlineData(SKBlendMode.ColorDodge, 148, 188, 161)]
    [InlineData(SKBlendMode.ColorBurn, 67, 107, 81)]
    [InlineData(SKBlendMode.HardLight, 93, 162, 119)]
    [InlineData(SKBlendMode.SoftLight, 124, 135, 125)]
    [InlineData(SKBlendMode.Difference, 115, 156, 97)]
    [InlineData(SKBlendMode.Exclusion, 122, 162, 122)]
    [InlineData(SKBlendMode.Hue, 71, 160, 100)]
    [InlineData(SKBlendMode.Saturation, 132, 123, 129)]
    [InlineData(SKBlendMode.Color, 71, 160, 100)]
    [InlineData(SKBlendMode.Luminosity, 144, 136, 141)]
    public void AdvancedBlendModesMatchNativeTranslucentOutput(
        SKBlendMode mode,
        byte red,
        byte green,
        byte blue)
    {
        var actual = ApplyFilter(
            new SKColor(51, 204, 102, 153),
            new SKColor(204, 51, 153, 102),
            mode);

        AssertColorNear(new SKColor(red, green, blue, 194), actual, tolerance: 2);
    }

    private static SKColor ApplyFilter(SKColor source, SKColor destination, SKBlendMode mode)
    {
        using var filter = SKColorFilter.CreateBlendMode(source, mode);
        using var paint = new SKPaint
        {
            Color = destination,
            ColorFilter = filter
        };

        var brush = Assert.IsType<SolidColorBrush>(paint.ToBrush());
        return new SKColor(
            ToByte(brush.Color.X),
            ToByte(brush.Color.Y),
            ToByte(brush.Color.Z),
            ToByte(brush.Color.W));
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

    private static void AssertColorNear(SKColor expected, SKColor actual, int tolerance)
    {
        Assert.InRange(Math.Abs(actual.Red - expected.Red), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Green - expected.Green), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Blue - expected.Blue), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Alpha - expected.Alpha), 0, tolerance);
    }
}
