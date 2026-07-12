using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkColorCompatibilityTests
{
    [Fact]
    public void PackedConstructorsChannelsAndConversionsMatchNative()
    {
        var color = new SKColor(1, 128, 255, 64);
        Assert.Equal((byte)1, color.Red);
        Assert.Equal((byte)128, color.Green);
        Assert.Equal((byte)255, color.Blue);
        Assert.Equal((byte)64, color.Alpha);
        Assert.Equal(0x400180ffu, (uint)color);
        Assert.Equal(color, new SKColor(0x400180ffu));
        Assert.Equal(new SKColor(1, 2, 3, 255), new SKColor(1, 2, 3));
    }

    [Fact]
    public void ComponentCopiesEqualityHashAndFormattingMatchNative()
    {
        var color = new SKColor(1, 2, 3, 4);
        Assert.Equal(new SKColor(9, 2, 3, 4), color.WithRed(9));
        Assert.Equal(new SKColor(1, 9, 3, 4), color.WithGreen(9));
        Assert.Equal(new SKColor(1, 2, 9, 4), color.WithBlue(9));
        Assert.Equal(new SKColor(1, 2, 3, 9), color.WithAlpha(9));
        Assert.True(color == new SKColor(1, 2, 3, 4));
        Assert.False(color != new SKColor(1, 2, 3, 4));
        Assert.Equal(color.GetHashCode(), new SKColor(1, 2, 3, 4).GetHashCode());
        Assert.Equal("#04010203", color.ToString());
    }

    [Fact]
    public void HexParsingSupportsNativeShortAndPackedForms()
    {
        Assert.True(SKColor.TryParse(" #abc ", out var rgb));
        Assert.Equal(new SKColor(0xaa, 0xbb, 0xcc), rgb);
        Assert.True(SKColor.TryParse("#8abc", out var argb));
        Assert.Equal(new SKColor(0xaa, 0xbb, 0xcc, 0x88), argb);
        Assert.True(SKColor.TryParse("80112233", out var packed));
        Assert.Equal(0x80112233u, (uint)packed);
        Assert.True(SKColor.TryParse("###fff", out var repeatedMarker));
        Assert.Equal(SKColors.White, repeatedMarker);
        Assert.False(SKColor.TryParse(null!, out var invalid));
        Assert.Equal(SKColor.Empty, invalid);
        Assert.Throws<ArgumentException>(() => SKColor.Parse("#12"));
    }

    [Fact]
    public void FloatColorPropertiesCopiesAndClampMatchNative()
    {
        var color = new SKColorF(0.25f, 0.5f, 0.75f);
        Assert.Equal(0.25f, color.Red);
        Assert.Equal(0.5f, color.Green);
        Assert.Equal(0.75f, color.Blue);
        Assert.Equal(1f, color.Alpha);
        Assert.Equal(new SKColorF(1f, 0.5f, 0.75f, 1f), color.WithRed(1f));
        Assert.Equal(new SKColorF(0.25f, 1f, 0.75f, 1f), color.WithGreen(1f));
        Assert.Equal(new SKColorF(0.25f, 0.5f, 1f, 1f), color.WithBlue(1f));
        Assert.Equal(new SKColorF(0.25f, 0.5f, 0.75f, 0.25f), color.WithAlpha(0.25f));

        var clamped = new SKColorF(-1f, 0.5f, 2f, float.NaN).Clamp();
        Assert.Equal(0f, clamped.Red);
        Assert.Equal(0.5f, clamped.Green);
        Assert.Equal(1f, clamped.Blue);
        Assert.True(float.IsNaN(clamped.Alpha));
    }

    [Fact]
    public void PackedAndFloatConversionsClampAndRoundLikeNative()
    {
        SKColorF converted = new SKColor(1, 128, 255, 64);
        Assert.Equal(1f / 255f, converted.Red);
        Assert.Equal(128f / 255f, converted.Green);
        Assert.Equal(1f, converted.Blue);
        Assert.Equal(64f / 255f, converted.Alpha);

        Assert.Equal(0x1a1a1a1au, (uint)(SKColor)new SKColorF(0.1f, 0.1f, 0.1f, 0.1f));
        Assert.Equal(0x80808080u, (uint)(SKColor)new SKColorF(0.5f, 0.5f, 0.5f, 0.5f));
        Assert.Equal(0xff00ff00u, (uint)(SKColor)new SKColorF(float.NaN, 2f, -1f, 1f));
    }

    [Fact]
    public void HslFactoriesMatchNativeFloatAndByteResults()
    {
        Assert.Equal(new SKColorF(1f, 0f, 3.5762787E-07f, 1f), SKColorF.FromHsl(0f, 100f, 50f));
        Assert.Equal(new SKColorF(0f, 1f, 0f, 1f), SKColorF.FromHsl(120f, 100f, 50f));
        Assert.Equal(new SKColorF(0f, 0f, 1f, 1f), SKColorF.FromHsl(240f, 100f, 50f));
        Assert.Equal(0xffff0000u, (uint)SKColor.FromHsl(0f, 100f, 50f));
        Assert.Equal(0xff3f3f3fu, (uint)SKColor.FromHsl(42f, 0f, 25f));
    }

    [Fact]
    public void HsvFactoriesMatchNativeFloatAndByteResults()
    {
        Assert.Equal(new SKColorF(0.5f, 0f, 0f, 1f), SKColorF.FromHsv(0f, 100f, 50f));
        Assert.Equal(new SKColorF(0f, 0.5f, 0f, 1f), SKColorF.FromHsv(120f, 100f, 50f));
        Assert.Equal(new SKColorF(0f, 0f, 0.5f, 1f), SKColorF.FromHsv(240f, 100f, 50f));
        Assert.Equal(0xff7f0000u, (uint)SKColor.FromHsv(0f, 100f, 50f));
        Assert.Equal(0xff3f3f3fu, (uint)SKColor.FromHsv(42f, 0f, 25f));
    }

    [Fact]
    public void HslHsvAndHueExtractionMatchNativePrecision()
    {
        var color = new SKColorF(0.25f, 0.5f, 0.75f);
        color.ToHsl(out var hslHue, out var saturation, out var lightness);
        Assert.Equal(210f, hslHue);
        Assert.Equal(50f, saturation);
        Assert.Equal(50f, lightness);
        color.ToHsv(out var hsvHue, out saturation, out var value);
        Assert.Equal(210f, hsvHue);
        Assert.Equal(66.66667f, saturation, precision: 4);
        Assert.Equal(75f, value);
        Assert.Equal(210f, color.Hue);
        Assert.Equal(210f, new SKColor(64, 128, 192).Hue);
    }

    [Fact]
    public void FloatEqualityHashAndFormattingUseAllComponents()
    {
        var color = new SKColorF(0.25f, 0.5f, 0.75f, 1f);
        Assert.True(color == new SKColorF(0.25f, 0.5f, 0.75f, 1f));
        Assert.False(color != new SKColorF(0.25f, 0.5f, 0.75f, 1f));
        Assert.NotEqual(color, color.WithAlpha(0.5f));
        Assert.Equal(color.GetHashCode(), new SKColorF(0.25f, 0.5f, 0.75f, 1f).GetHashCode());
        Assert.Equal("#ff4080bf", color.ToString());
    }
}
