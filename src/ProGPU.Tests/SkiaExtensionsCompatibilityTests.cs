using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkiaExtensionsCompatibilityTests
{
    private static readonly int[] s_bytesPerPixel =
    [
        0, 1, 2, 2, 4, 4, 4, 4, 4, 1,
        8, 8, 16, 2, 2, 4, 2, 4, 8, 4,
        4, 4, 4, 1, 8, 8, 8, 2, 2,
    ];

    private static readonly uint[] s_glFormats =
    [
        0, 0x803c, 0x8d62, 0x8056, 0x8058, 0x8051, 0x93a1, 0x8059,
        0, 0x8040, 0x881a, 0x881a, 0, 0x822b, 0x822d, 0x822f,
        0x822a, 0x822c, 0x805b, 0, 0, 0, 0x8c43, 0x8229,
        0, 0, 0, 0x822a, 0x822d,
    ];

    [Fact]
    public void PixelGeometryClassificationMatchesOfficialValues()
    {
        AssertGeometry(SKPixelGeometry.Unknown, false, false, false, false);
        AssertGeometry(SKPixelGeometry.RgbHorizontal, true, false, true, false);
        AssertGeometry(SKPixelGeometry.BgrHorizontal, true, false, false, true);
        AssertGeometry(SKPixelGeometry.RgbVertical, false, true, true, false);
        AssertGeometry(SKPixelGeometry.BgrVertical, false, true, false, true);
        AssertGeometry((SKPixelGeometry)999, false, false, false, false);
    }

    [Fact]
    public void PixelFormatSizeAndGlMappingsMatchEveryOfficialColorType()
    {
        for (var value = 0; value < s_bytesPerPixel.Length; value++)
        {
            var colorType = (SKColorType)value;
            var bytes = s_bytesPerPixel[value];
            Assert.Equal(bytes, colorType.GetBytesPerPixel());
            Assert.Equal(bytes switch { 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => 0 }, colorType.GetBitShiftPerPixel());
            Assert.Equal(s_glFormats[value], colorType.ToGlSizedFormat());
        }
    }

    [Fact]
    public void AlphaTypeValidationMatchesFormatCategories()
    {
        var opaque = new HashSet<SKColorType>
        {
            SKColorType.Rgb565,
            SKColorType.Rgb888x,
            SKColorType.Rgb101010x,
            SKColorType.Gray8,
            SKColorType.Rg88,
            SKColorType.RgF16,
            SKColorType.Rg1616,
            SKColorType.Bgr101010x,
            SKColorType.Bgr101010xXR,
            SKColorType.R8Unorm,
            SKColorType.RgbF16F16F16x,
            SKColorType.R16Unorm,
            SKColorType.RF16,
        };
        var alphaOnly = new HashSet<SKColorType>
        {
            SKColorType.Alpha8,
            SKColorType.AlphaF16,
            SKColorType.Alpha16,
        };

        foreach (var colorType in Enum.GetValues<SKColorType>())
        {
            foreach (var alphaType in Enum.GetValues<SKAlphaType>())
            {
                var expected = colorType == SKColorType.Unknown
                    ? SKAlphaType.Unknown
                    : opaque.Contains(colorType)
                        ? SKAlphaType.Opaque
                        : alphaOnly.Contains(colorType) && alphaType == SKAlphaType.Unpremul
                            ? SKAlphaType.Premul
                            : alphaType;
                Assert.Equal(expected, colorType.GetAlphaType(alphaType));
            }
        }

        Assert.Equal((SKAlphaType)999, SKColorType.Rgba8888.GetAlphaType((SKAlphaType)999));
        Assert.Equal(SKAlphaType.Opaque, SKColorType.Rgb565.GetAlphaType((SKAlphaType)999));
    }

    [Fact]
    public void InvalidColorTypesFailWithTheOfficialParameterBoundary()
    {
        foreach (var colorType in new[] { (SKColorType)(-1), (SKColorType)29, (SKColorType)999 })
        {
            Assert.Equal("colorType", Assert.Throws<ArgumentOutOfRangeException>(() => colorType.GetBytesPerPixel()).ParamName);
            Assert.Equal("colorType", Assert.Throws<ArgumentOutOfRangeException>(() => colorType.GetBitShiftPerPixel()).ParamName);
            Assert.Equal("colorType", Assert.Throws<ArgumentOutOfRangeException>(() => colorType.GetAlphaType()).ParamName);
            Assert.Equal("colorType", Assert.Throws<ArgumentOutOfRangeException>(() => colorType.ToGlSizedFormat()).ParamName);
        }
    }

    [Fact]
    public void StablePixelMetadataQueriesAllocateNothing()
    {
        _ = SKColorType.Rgba8888.GetBytesPerPixel();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000_000; index++)
        {
            _ = SKColorType.Rgba8888.GetBytesPerPixel();
            _ = SKColorType.Rgba8888.GetBitShiftPerPixel();
            _ = SKColorType.Rgba8888.GetAlphaType();
            _ = SKColorType.Rgba8888.ToGlSizedFormat();
            _ = SKPixelGeometry.RgbHorizontal.IsHorizontal();
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    private static void AssertGeometry(
        SKPixelGeometry geometry,
        bool horizontal,
        bool vertical,
        bool rgb,
        bool bgr)
    {
        Assert.Equal(horizontal, geometry.IsHorizontal());
        Assert.Equal(vertical, geometry.IsVertical());
        Assert.Equal(rgb, geometry.IsRgb());
        Assert.Equal(bgr, geometry.IsBgr());
    }
}
