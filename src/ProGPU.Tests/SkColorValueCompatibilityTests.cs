using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkColorValueCompatibilityTests
{
    [Fact]
    public void PackedColorChannelsParsingAndIdentityMatchNative()
    {
        var color = new SKColor(0x7f123456u);

        Assert.Equal((byte)0x12, color.Red);
        Assert.Equal((byte)0x34, color.Green);
        Assert.Equal((byte)0x56, color.Blue);
        Assert.Equal((byte)0x7f, color.Alpha);
        Assert.Equal("#7f123456", color.ToString());
        Assert.Equal(0x7f123456u, (uint)color);
        Assert.Equal(new SKColor(0xaa, 0xbb, 0xcc, 0xdd), SKColor.Parse("#dabc"));
        Assert.Equal(new SKColor(0x12, 0x34, 0x56), SKColor.Parse("123456"));
        Assert.True(SKColor.TryParse(" #7f123456 ", out var parsed));
        Assert.Equal(color, parsed);
        Assert.False(SKColor.TryParse("not-a-color", out var invalid));
        Assert.Equal(SKColor.Empty, invalid);
        Assert.Equal(SKColor.Empty, SKColors.Empty);
        Assert.Equal(0x00ffffffu, (uint)SKColors.Transparent);
        Assert.Equal(color.WithRed(1).WithGreen(2).WithBlue(3).WithAlpha(4), new SKColor(1, 2, 3, 4));
    }

    [Fact]
    public void HslAndHsvConversionsMatchNativeSkiaSharp()
    {
        var color = new SKColor(0x7f123456u);
        color.ToHsl(out var hslHue, out var saturation, out var lightness);
        color.ToHsv(out var hsvHue, out var hsvSaturation, out var value);

        AssertNear(210.00002f, color.Hue);
        AssertNear(210.00002f, hslHue);
        AssertNear(65.38462f, saturation);
        AssertNear(20.392157f, lightness);
        AssertNear(210.00002f, hsvHue);
        AssertNear(79.06977f, hsvSaturation);
        AssertNear(33.72549f, value);
        Assert.Equal(new SKColor(50, 102, 153, 127), SKColor.FromHsl(210f, 50f, 40f, 127));
        Assert.Equal(new SKColor(51, 76, 102, 127), SKColor.FromHsv(210f, 50f, 40f, 127));
    }

    [Fact]
    public void FloatColorClampAndPackedConversionMatchNativeRounding()
    {
        var color = new SKColorF(0f, 0.5f, 1f, 0.25f);

        Assert.Equal(new SKColor(0, 128, 255, 64), (SKColor)color);
        Assert.Equal("#400080ff", color.ToString());
        Assert.Equal(new SKColorF(1f, 0.5f, 1f, 0.25f), color.WithRed(1f));
        Assert.Equal(new SKColorF(0f, 0.5f, 1f, 0.75f), color.WithAlpha(0.75f));

        var outOfRange = new SKColorF(-1f, 2f, float.NaN, float.PositiveInfinity);
        var clamped = outOfRange.Clamp();
        Assert.Equal(0f, clamped.Red);
        Assert.Equal(1f, clamped.Green);
        Assert.True(float.IsNaN(clamped.Blue));
        Assert.Equal(1f, clamped.Alpha);
        Assert.Equal(new SKColor(0, 255, 0, 255), (SKColor)outOfRange);
    }

    [Fact]
    public void PackedToFloatConversionUsesNativeOneOver255Scale()
    {
        SKColorF color = new SKColor(0x7f123456u);
        const float scale = 1f / 255f;

        Assert.Equal(0x12 * scale, color.Red);
        Assert.Equal(0x34 * scale, color.Green);
        Assert.Equal(0x56 * scale, color.Blue);
        Assert.Equal(0x7f * scale, color.Alpha);
        AssertNear(210.00002f, color.Hue);

        color.ToHsl(out var hue, out var saturation, out var lightness);
        AssertNear(210.00002f, hue);
        AssertNear(65.38462f, saturation);
        AssertNear(20.392157f, lightness);
    }

    private static void AssertNear(float expected, float actual, float tolerance = 0.0001f) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);
}
