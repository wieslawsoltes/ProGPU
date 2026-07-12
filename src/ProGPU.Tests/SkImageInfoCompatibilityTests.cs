using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkImageInfoCompatibilityTests
{
    [Fact]
    public void DefaultsMatchNativePlatformPixelOrder()
    {
        var info = new SKImageInfo(3, 2);

        Assert.Equal(SKColorType.Rgba8888, SKImageInfo.PlatformColorType);
        Assert.Equal(24, SKImageInfo.PlatformColorAlphaShift);
        Assert.Equal(0, SKImageInfo.PlatformColorRedShift);
        Assert.Equal(8, SKImageInfo.PlatformColorGreenShift);
        Assert.Equal(16, SKImageInfo.PlatformColorBlueShift);
        Assert.Equal(SKColorType.Rgba8888, info.ColorType);
        Assert.Equal(SKAlphaType.Premul, info.AlphaType);
        Assert.Null(info.ColorSpace);
        Assert.True(SKImageInfo.Empty.IsEmpty);
    }

    [Fact]
    public void DerivedSizesPreserveCheckedAndWideContracts()
    {
        var rgba = new SKImageInfo(3, 2, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var unknown = new SKImageInfo(3, 2, SKColorType.Unknown, SKAlphaType.Unknown);

        Assert.Equal(4, rgba.BytesPerPixel);
        Assert.Equal(2, rgba.BitShiftPerPixel);
        Assert.Equal(32, rgba.BitsPerPixel);
        Assert.Equal(12, rgba.RowBytes);
        Assert.Equal(12L, rgba.RowBytes64);
        Assert.Equal(24, rgba.BytesSize);
        Assert.Equal(24L, rgba.BytesSize64);
        Assert.Equal(new SKSizeI(3, 2), rgba.Size);
        Assert.Equal(SKRectI.Create(3, 2), rgba.Rect);
        Assert.True(rgba.IsOpaque);
        Assert.False(rgba.IsEmpty);
        Assert.Equal(0, unknown.BytesPerPixel);
        Assert.Throws<OverflowException>(() =>
            new SKImageInfo(int.MaxValue, 2, SKColorType.Rgba8888).BytesSize);
        Assert.Equal((long)int.MaxValue * 8, new SKImageInfo(
            int.MaxValue,
            2,
            SKColorType.Rgba8888).BytesSize64);
    }

    [Fact]
    public void WithMethodsAndEqualityCopyOnlyRequestedMetadata()
    {
        using var colorSpace = SKColorSpace.CreateSrgbLinear();
        var original = new SKImageInfo(
            10,
            20,
            SKColorType.Bgra8888,
            SKAlphaType.Unpremul,
            colorSpace);

        Assert.Equal(original, original.WithSize(10, 20));
        Assert.NotEqual(original, original.WithSize(new SKSizeI(11, 20)));
        Assert.Equal(SKColorType.RgbaF16, original.WithColorType(SKColorType.RgbaF16).ColorType);
        Assert.Equal(SKAlphaType.Opaque, original.WithAlphaType(SKAlphaType.Opaque).AlphaType);
        Assert.Null(original.WithColorSpace(null).ColorSpace);
        Assert.Equal(original.GetHashCode(), original.WithSize(10, 20).GetHashCode());
        Assert.Equal(10, original.Width);
        Assert.Equal(20, original.Height);
    }
}
