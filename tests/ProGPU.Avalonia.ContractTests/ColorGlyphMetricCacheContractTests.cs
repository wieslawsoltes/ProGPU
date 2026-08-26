using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia;
using ProGPU.Text;
using Xunit;

namespace Avalonia.ProGpu.ContractTests;

public sealed class ColorGlyphMetricCacheContractTests
{
    [Fact]
    public void AvaloniaColorAtlasRetainsLargeBitmapEmojiWorkingSets()
    {
        Assert.Equal(64u,
            AvaloniaGpuDevicePool.Options.InitialColorGlyphAtlasSize);
        Assert.True(
            AvaloniaGpuDevicePool.Options.ColorGlyphAtlasSize >= 1024u);
    }

    [Fact]
    public void SbixCoordinatesArePlacedRelativeToTheBaseline()
    {
        var metrics = new ColorGlyphMetrics(
            pixelsPerEm: 20,
            pixelsPerInch: 72,
            originOffsetX: 2,
            originOffsetY: 5,
            pixelWidth: 20,
            pixelHeight: 18);

        Assert.Equal(
            new Rect(96, 24, 40, 36),
            metrics.GetBounds(new Point(100, 50), emSize: 40));
    }

    [Fact]
    public void PngDimensionsAreReadWithoutDecodingPixels()
    {
        Span<byte> encoded = stackalloc byte[24];
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        signature.CopyTo(encoded);
        encoded[12] = (byte)'I';
        encoded[13] = (byte)'H';
        encoded[14] = (byte)'D';
        encoded[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(encoded[16..], 320);
        BinaryPrimitives.WriteUInt32BigEndian(encoded[20..], 180);

        Assert.True(
            EncodedImageDimensions.TryRead(
                encoded,
                out int width,
                out int height));
        Assert.Equal(320, width);
        Assert.Equal(180, height);
        Assert.Equal(0, BoundedColorGlyphMetrics.CachedDecodedPixelBytes);
    }

    [Fact]
    public void TruncatedPayloadHasNoDimensions()
    {
        Assert.False(
            EncodedImageDimensions.TryRead(
                [137, 80, 78, 71],
                out int width,
                out int height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void SystemColorFontCachesOnlyEncodedDimensions()
    {
        const string fontPath =
            "/System/Library/Fonts/Apple Color Emoji.ttc";
        if (!OperatingSystem.IsMacOS() || !File.Exists(fontPath))
            return;

        var font = new TtfFont(fontPath, faceIndex: 0);
        ushort glyph = font.GetGlyphIndex(0x1f600);

        Assert.NotEqual(0, glyph);
        Assert.True(
            BoundedColorGlyphMetrics.TryGetMetrics(
                font,
                glyph,
                emSize: 64,
                out ColorGlyphMetrics metrics));
        Assert.True(metrics.PixelWidth > 0);
        Assert.True(metrics.PixelHeight > 0);
        Assert.Equal(0, BoundedColorGlyphMetrics.CachedDecodedPixelBytes);
        Assert.InRange(
            BoundedColorGlyphMetrics.CachedMetricCount,
            1,
            BoundedColorGlyphMetrics.MaximumCachedMetricCount);
    }
}
