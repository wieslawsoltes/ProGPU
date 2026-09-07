using System.Buffers.Binary;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeMilTileBrushTests
{
    [Fact]
    public void WritesCacheBrushPacketWithoutTileBrushFields()
    {
        var batch = new NativeMilBatchBuilder();
        var brush = new NativeMilBitmapCacheBrush(7, 6, 0.75, 3, 4, 5);
        batch.SetBitmapCacheBrush(2, brush);
        byte[] bytes = batch.ToArray();
        Assert.Equal(40, bytes.Length);
        Assert.Equal(40U, UInt32(bytes, 0));
        Assert.Equal(0x84U, UInt32(bytes, 4));
        Assert.Equal(2U, UInt32(bytes, 8));
        Assert.Equal(0.75, Double(bytes, 12));
        for (uint i = 0; i < 5; ++i) Assert.Equal(3U + i, UInt32(bytes, 20 + (int)i * 4));
        foreach (double opacity in new[] { double.NaN, double.PositiveInfinity, -0.01, 1.01 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.SetBitmapCacheBrush(2, brush with { Opacity = opacity }));
            Assert.Equal(bytes, batch.ToArray());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => batch.SetBitmapCacheBrush(0, brush));
        Assert.Equal(bytes, batch.ToArray());
        Assert.Equal(83U, (uint)NativeMilResourceType.BitmapCacheBrush);
    }

    [Theory]
    [InlineData(0x81U)]
    [InlineData(0x82U)]
    [InlineData(0x83U)]
    public void WritesEveryCanonicalTileBrushField(uint command)
    {
        var brush = new NativeMilTileBrush(new(1, 2, 3, 4), new(5, 6, 7, 8),
            Opacity: 0.75, ViewportUnits: NativeMilBrushMappingMode.Absolute,
            Stretch: NativeMilStretch.UniformToFill, TileMode: NativeMilTileMode.Tile,
            AlignmentX: NativeMilAlignment.End, AlignmentY: NativeMilAlignment.Start,
            Cache: true, CacheInvalidationThresholdMinimum: 0.25,
            CacheInvalidationThresholdMaximum: 2.5, OpacityAnimationHandle: 10,
            TransformHandle: 11, RelativeTransformHandle: 12,
            ViewportAnimationHandle: 13, ViewboxAnimationHandle: 14);
        var batch = new NativeMilBatchBuilder();
        switch (command)
        {
            case 0x81: batch.SetImageBrush(1, brush, 15); break;
            case 0x82: batch.SetDrawingBrush(1, brush, 15); break;
            case 0x83: batch.SetVisualBrush(1, brush, 15); break;
        }
        byte[] bytes = batch.ToArray();
        Assert.Equal(152, bytes.Length);
        Assert.Equal(152U, UInt32(bytes, 0));
        Assert.Equal(command, UInt32(bytes, 4));
        Assert.Equal(1U, UInt32(bytes, 8));
        Assert.Equal(0.75, Double(bytes, 12));
        for (int i = 0; i < 8; ++i) Assert.Equal(i + 1.0, Double(bytes, 20 + i * 8));
        Assert.Equal(0.25, Double(bytes, 84));
        Assert.Equal(2.5, Double(bytes, 92));
        uint[] expected = [10, 11, 12, 0, 1, 13, 14, 3, 4, 2, 0, 1, 15];
        for (int i = 0; i < expected.Length; ++i)
            Assert.Equal(expected[i], UInt32(bytes, 100 + i * 4));
    }

    [Fact]
    public void RejectsInvalidStateBeforeAppendingAnyPacket()
    {
        var brush = new NativeMilTileBrush(new(0, 0, 1, 1), new(0, 0, 1, 1));
        var batch = new NativeMilBatchBuilder();
        batch.SetImageBrush(1, brush);
        byte[] original = batch.ToArray();
        NativeMilTileBrush[] invalid =
        [
            brush with { Opacity = double.NaN }, brush with { Opacity = 1.01 },
            brush with { Viewport = new(0, 0, -1, 1) },
            brush with { Viewbox = new(double.PositiveInfinity, 0, 1, 1) },
            brush with { ViewportUnits = (NativeMilBrushMappingMode)2 },
            brush with { ViewboxUnits = (NativeMilBrushMappingMode)2 },
            brush with { Stretch = (NativeMilStretch)4 },
            brush with { TileMode = (NativeMilTileMode)5 },
            brush with { AlignmentX = (NativeMilAlignment)3 },
            brush with { AlignmentY = (NativeMilAlignment)3 }
        ];
        foreach (NativeMilTileBrush value in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.SetImageBrush(1, value));
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.SetDrawingBrush(1, value));
            Assert.Throws<ArgumentOutOfRangeException>(() => batch.SetVisualBrush(1, value));
            Assert.Equal(original, batch.ToArray());
        }
        Assert.Equal(1U, (uint)NativeMilTileMode.FlipX);
        Assert.Equal(2U, (uint)NativeMilTileMode.FlipY);
        Assert.Equal(3U, (uint)NativeMilTileMode.FlipXY);
        Assert.Equal(4U, (uint)NativeMilTileMode.Tile);
    }

    [Fact]
    public void PreservesCanonicalEmptyRectAndUnvalidatedCacheHints()
    {
        var empty = new NativeMilRect(double.PositiveInfinity, double.PositiveInfinity,
            double.NegativeInfinity, double.NegativeInfinity);
        var brush = new NativeMilTileBrush(empty, empty,
            CacheInvalidationThresholdMinimum: double.NaN,
            CacheInvalidationThresholdMaximum: double.PositiveInfinity);
        var batch = new NativeMilBatchBuilder();
        batch.SetImageBrush(1, brush);
        byte[] bytes = batch.ToArray();
        Assert.True(double.IsPositiveInfinity(Double(bytes, 20)));
        Assert.True(double.IsNegativeInfinity(Double(bytes, 36)));
        Assert.True(double.IsPositiveInfinity(Double(bytes, 52)));
        Assert.True(double.IsNegativeInfinity(Double(bytes, 68)));
        Assert.True(double.IsNaN(Double(bytes, 84)));
        Assert.True(double.IsPositiveInfinity(Double(bytes, 92)));
    }

    private static uint UInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static double Double(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset, 8));
}
