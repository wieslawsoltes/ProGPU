using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace Avalonia.ProGpu.UnitTests;

public sealed class NativeTextHintingContractTests
{
    [Fact]
    public void GeneratedRecordsMatchIndependentFixedWidthLayout()
    {
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.HintedFontRequest>());
        Assert.Equal(16, Marshal.SizeOf<NativeHintedPoint>());
        Assert.Equal(136, Marshal.SizeOf<NativeHintedGlyph>());
        Assert.Equal(12, Marshal.SizeOf<NativeHintedBatchCounts>());
        string[] prefix = ["GlyphIndex", "PointOffset", "PointCount", "ContourOffset", "ContourCount", "OutlineFlags"];
        string[] metrics = ["AdvanceX266", "AdvanceY266", "HorizontalBearingX266", "HorizontalBearingY266",
            "Width266", "Height266", "HorizontalAdvance266", "VerticalBearingX266", "VerticalBearingY266",
            "VerticalAdvance266", "LinearHorizontalAdvance1616", "LinearVerticalAdvance1616",
            "LeftSideBearingDelta266", "RightSideBearingDelta266"];
        for (int index = 0; index < prefix.Length; index++)
        {
            Assert.Equal(typeof(uint), typeof(NativeHintedGlyph).GetField(prefix[index])!.FieldType);
            Assert.Equal(index * 4, (int)Marshal.OffsetOf<NativeHintedGlyph>(prefix[index]));
        }
        for (int index = 0; index < metrics.Length; index++)
        {
            Assert.Equal(typeof(long), typeof(NativeHintedGlyph).GetField(metrics[index])!.FieldType);
            Assert.Equal(24 + index * 8, (int)Marshal.OffsetOf<NativeHintedGlyph>(metrics[index]));
        }
    }

    [Fact]
    public void SignedFixedPointRecordsDoNotNarrowThroughFloatOrNativeLong()
    {
        NativeHintedPoint[] points = [new() { X266 = long.MinValue, Y266 = long.MaxValue }];
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(points.AsSpan());
        Assert.Equal(long.MinValue, BinaryPrimitives.ReadInt64LittleEndian(bytes));
        Assert.Equal(long.MaxValue, BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
        NativeHintedGlyph[] glyphs = [new()
        {
            GlyphIndex = uint.MaxValue, OutlineFlags = uint.MaxValue,
            AdvanceX266 = -9_007_199_254_740_993, RightSideBearingDelta266 = 9_007_199_254_740_993,
        }];
        bytes = MemoryMarshal.AsBytes(glyphs.AsSpan());
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
        Assert.Equal(-9_007_199_254_740_993, BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]));
        Assert.Equal(9_007_199_254_740_993, BinaryPrimitives.ReadInt64LittleEndian(bytes[128..]));
    }

    [Fact]
    public void BatchLeasesHaveTheirOwnDisposedIdentityAndReleaseOnce()
    {
        int releases = 0;
        var owner = new NativeTextContextOwner(42, _ => releases++, nameof(NativeHintedFontBatch));
        using (var lease = owner.Acquire())
        {
            owner.Dispose();
            Assert.Equal(0, releases);
            Assert.Equal(nameof(NativeHintedFontBatch), Assert.Throws<ObjectDisposedException>(() =>
            {
                using var rejected = owner.Acquire();
            }).ObjectName);
        }
        Assert.Equal(1, releases);
        owner.Dispose();
        Assert.Equal(1, releases);
    }
}
