using System.Runtime.CompilerServices;

namespace SkiaSharp;

/// <summary>
/// Provides allocation-free metadata queries for pixel formats and LCD pixel
/// geometry.
/// </summary>
public static class SkiaExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHorizontal(this SKPixelGeometry pg) =>
        pg is SKPixelGeometry.RgbHorizontal or SKPixelGeometry.BgrHorizontal;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsVertical(this SKPixelGeometry pg) =>
        pg is SKPixelGeometry.RgbVertical or SKPixelGeometry.BgrVertical;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRgb(this SKPixelGeometry pg) =>
        pg is SKPixelGeometry.RgbHorizontal or SKPixelGeometry.RgbVertical;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsBgr(this SKPixelGeometry pg) =>
        pg is SKPixelGeometry.BgrHorizontal or SKPixelGeometry.BgrVertical;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBytesPerPixel(this SKColorType colorType) => colorType switch
    {
        SKColorType.Unknown => 0,
        SKColorType.Alpha8 or
            SKColorType.Gray8 or
            SKColorType.R8Unorm => 1,
        SKColorType.Rgb565 or
            SKColorType.Argb4444 or
            SKColorType.Rg88 or
            SKColorType.AlphaF16 or
            SKColorType.Alpha16 or
            SKColorType.R16Unorm or
            SKColorType.RF16 => 2,
        SKColorType.Rgba8888 or
            SKColorType.Rgb888x or
            SKColorType.Bgra8888 or
            SKColorType.Rgba1010102 or
            SKColorType.Rgb101010x or
            SKColorType.RgF16 or
            SKColorType.Rg1616 or
            SKColorType.Bgra1010102 or
            SKColorType.Bgr101010x or
            SKColorType.Bgr101010xXR or
            SKColorType.Srgba8888 => 4,
        SKColorType.RgbaF16 or
            SKColorType.RgbaF16Clamped or
            SKColorType.Rgba16161616 or
            SKColorType.Rgba10x6 or
            SKColorType.Bgra10101010XR or
            SKColorType.RgbF16F16F16x => 8,
        SKColorType.RgbaF32 => 16,
        _ => throw UnknownColorType(colorType),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBitShiftPerPixel(this SKColorType colorType) => colorType switch
    {
        SKColorType.Unknown or
            SKColorType.Alpha8 or
            SKColorType.Gray8 or
            SKColorType.R8Unorm => 0,
        SKColorType.Rgb565 or
            SKColorType.Argb4444 or
            SKColorType.Rg88 or
            SKColorType.AlphaF16 or
            SKColorType.Alpha16 or
            SKColorType.R16Unorm or
            SKColorType.RF16 => 1,
        SKColorType.Rgba8888 or
            SKColorType.Rgb888x or
            SKColorType.Bgra8888 or
            SKColorType.Rgba1010102 or
            SKColorType.Rgb101010x or
            SKColorType.RgF16 or
            SKColorType.Rg1616 or
            SKColorType.Bgra1010102 or
            SKColorType.Bgr101010x or
            SKColorType.Bgr101010xXR or
            SKColorType.Srgba8888 => 2,
        SKColorType.RgbaF16 or
            SKColorType.RgbaF16Clamped or
            SKColorType.Rgba16161616 or
            SKColorType.Rgba10x6 or
            SKColorType.Bgra10101010XR or
            SKColorType.RgbF16F16F16x => 3,
        SKColorType.RgbaF32 => 4,
        _ => throw UnknownColorType(colorType),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SKAlphaType GetAlphaType(
        this SKColorType colorType,
        SKAlphaType alphaType = SKAlphaType.Premul) => colorType switch
    {
        SKColorType.Unknown => SKAlphaType.Unknown,
        SKColorType.Rgb565 or
            SKColorType.Rgb888x or
            SKColorType.Rgb101010x or
            SKColorType.Gray8 or
            SKColorType.Rg88 or
            SKColorType.RgF16 or
            SKColorType.Rg1616 or
            SKColorType.Bgr101010x or
            SKColorType.Bgr101010xXR or
            SKColorType.R8Unorm or
            SKColorType.RgbF16F16F16x or
            SKColorType.R16Unorm or
            SKColorType.RF16 => SKAlphaType.Opaque,
        SKColorType.Alpha8 or
            SKColorType.AlphaF16 or
            SKColorType.Alpha16 => alphaType == SKAlphaType.Unpremul
                ? SKAlphaType.Premul
                : alphaType,
        SKColorType.Argb4444 or
            SKColorType.Rgba8888 or
            SKColorType.Bgra8888 or
            SKColorType.Rgba1010102 or
            SKColorType.RgbaF16 or
            SKColorType.RgbaF16Clamped or
            SKColorType.RgbaF32 or
            SKColorType.Rgba16161616 or
            SKColorType.Bgra1010102 or
            SKColorType.Srgba8888 or
            SKColorType.Rgba10x6 or
            SKColorType.Bgra10101010XR => alphaType,
        _ => throw UnknownColorType(colorType),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ToGlSizedFormat(this SKColorType colorType) => colorType switch
    {
        SKColorType.Unknown => 0,
        SKColorType.Alpha8 => 0x803c,
        SKColorType.Rgb565 => 0x8d62,
        SKColorType.Argb4444 => 0x8056,
        SKColorType.Rgba8888 => 0x8058,
        SKColorType.Rgb888x => 0x8051,
        SKColorType.Bgra8888 => 0x93a1,
        SKColorType.Rgba1010102 => 0x8059,
        SKColorType.Rgb101010x => 0,
        SKColorType.Gray8 => 0x8040,
        SKColorType.RgbaF16 or SKColorType.RgbaF16Clamped => 0x881a,
        SKColorType.RgbaF32 => 0,
        SKColorType.Rg88 => 0x822b,
        SKColorType.AlphaF16 => 0x822d,
        SKColorType.RgF16 => 0x822f,
        SKColorType.Alpha16 => 0x822a,
        SKColorType.Rg1616 => 0x822c,
        SKColorType.Rgba16161616 => 0x805b,
        SKColorType.Bgra1010102 or
            SKColorType.Bgr101010x or
            SKColorType.Bgr101010xXR => 0,
        SKColorType.Srgba8888 => 0x8c43,
        SKColorType.R8Unorm => 0x8229,
        SKColorType.Rgba10x6 or
            SKColorType.Bgra10101010XR or
            SKColorType.RgbF16F16F16x => 0,
        SKColorType.R16Unorm => 0x822a,
        SKColorType.RF16 => 0x822d,
        _ => throw UnknownColorType(colorType),
    };

    private static ArgumentOutOfRangeException UnknownColorType(SKColorType colorType) =>
        new(nameof(colorType), $"Unknown color type: '{colorType}'");
}
