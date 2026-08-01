using System;
using System.IO;

namespace SkiaSharp;

#nullable disable
public struct SKWebpEncoderFrame
{
    private SKPixmap _pixmap;
    private TimeSpan _duration;

    public SKWebpEncoderFrame(SKBitmap bitmap, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        _pixmap = bitmap.PeekPixels();
        _duration = duration;
    }

    public SKWebpEncoderFrame(SKImage image, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(image);
        var bitmap = SKBitmap.FromImage(image);
        _pixmap = bitmap.PeekPixels();
        _duration = duration;
    }

    public SKWebpEncoderFrame(SKPixmap pixmap, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(pixmap);
        _pixmap = pixmap;
        _duration = duration;
    }

    public SKPixmap Pixmap
    {
        readonly get => _pixmap;
        set => _pixmap = value;
    }

    public TimeSpan Duration
    {
        readonly get => _duration;
        set => _duration = value;
    }
}
#nullable enable

public static class SKWebpEncoder
{
    public static SKData? Encode(SKPixmap src, SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(src);
        return null;
    }

    public static bool Encode(Stream dst, SKPixmap src, SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        return false;
    }

    public static bool Encode(SKWStream dst, SKPixmap src, SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(src);
        return false;
    }

    public static SKData? EncodeAnimated(
        ReadOnlySpan<SKWebpEncoderFrame> frames,
        SKWebpEncoderOptions options) => null;

    public static bool EncodeAnimated(
        Stream dst,
        ReadOnlySpan<SKWebpEncoderFrame> frames,
        SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        return false;
    }

    public static bool EncodeAnimated(
        SKWStream dst,
        ReadOnlySpan<SKWebpEncoderFrame> frames,
        SKWebpEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(dst);
        return false;
    }
}
