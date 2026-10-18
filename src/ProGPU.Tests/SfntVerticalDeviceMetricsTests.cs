using System.Buffers.Binary;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public partial class SfntFontFaceTests
{
    private static byte[] VdmxTable()
    {
        // Two independent ratio groups, each with exact records at 12 and 16.
        byte[] table = new byte[50];
        VdmxU16(table, 0, 1); VdmxU16(table, 2, 2); VdmxU16(table, 4, 2);
        new byte[] { 1, 2, 1, 1, 1, 1, 1, 1 }.CopyTo(table, 6);
        VdmxU16(table, 14, 18); VdmxU16(table, 16, 34);
        foreach (int group in new[] { 18, 34 })
        {
            VdmxU16(table, group, 2); table[group + 2] = 12; table[group + 3] = 16;
            VdmxU16(table, group + 4, 12); VdmxU16(table, group + 6, group == 18 ? 30 : 12);
            VdmxU16(table, group + 8, -3);
            VdmxU16(table, group + 10, 16); VdmxU16(table, group + 12, group == 18 ? 40 : 17);
            VdmxU16(table, group + 14, -4);
        }
        return table;
    }
    private static void VdmxU16(byte[] table, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset), unchecked((ushort)value));

    [Theory]
    [InlineData(12, 1, 1, 15)]
    [InlineData(16, 1, 1, 21)]
    [InlineData(12, 2, 2, 15)]
    [InlineData(12, 2, 1, 33)]
    public void VdmxRetainsOriginalExactSizeAndAspectRatio(int size, int x, int y, int height)
    {
        Assert.True(SfntVerticalDeviceMetrics.TryRead(VdmxTable(), (ushort)size, (ushort)x, (ushort)y, out var value, out bool found));
        Assert.True(found);
        Assert.Equal(size, value.PixelsPerEm);
        Assert.Equal(height, value.Height);
    }

    [Theory]
    [InlineData(13, 1, 1)]
    [InlineData(12, 1, 2)]
    public void VdmxMissingRecordOrRatioNeverScalesOrSelectsNearest(int size, int x, int y)
    {
        Assert.True(SfntVerticalDeviceMetrics.TryRead(VdmxTable(), (ushort)size, (ushort)x, (ushort)y, out var value, out bool found));
        Assert.False(found);
        Assert.Equal(default, value);
    }

    [Fact]
    public void VdmxEveryTruncatedPrefixFailsAtomically()
    {
        byte[] table = VdmxTable();
        for (int length = 0; length < table.Length; length++)
        {
            Assert.False(SfntVerticalDeviceMetrics.TryRead(table.AsSpan(0, length), 12, 1, 1, out var value, out bool found));
            Assert.False(found);
            Assert.Equal(default, value);
        }
    }

    [Theory]
    [InlineData(0, 2)] // Unknown table version.
    [InlineData(2, 0)] // Missing group inventory.
    [InlineData(14, 19)] // Offset inside another group.
    [InlineData(18, 100)] // Corrupt unselected group count.
    [InlineData(28, 12)] // Duplicate height in the unselected group.
    [InlineData(24, -4)] // Inverted unselected metric.
    [InlineData(44, 12)] // Duplicate selected group's later height.
    public void VdmxValidatesWholeInventoryBeforePublishing(int offset, int value)
    {
        byte[] table = VdmxTable(); VdmxU16(table, offset, value);
        Assert.False(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out var metrics, out bool found));
        Assert.False(found);
        Assert.Equal(default, metrics);
    }

    [Fact]
    public void VdmxOriginalVersionCharsetAndLastFallbackRemainDistinct()
    {
        byte[] table = VdmxTable(); VdmxU16(table, 0, 0);
        table[10] = 0;
        Assert.True(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out _, out bool found));
        Assert.False(found); // Version-zero non-ANSI record is not selected.
        VdmxU16(table, 0, 1);
        Array.Clear(table, 11, 3);
        Assert.True(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 2, out var metrics, out found));
        Assert.True(found); Assert.Equal(15, metrics.Height);
        Array.Clear(table, 7, 3);
        Assert.False(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out _, out _));
    }

    private static byte[] VdmxFont(params (string Tag, byte[] Data)[] extra) => BuildSfntWithTables(
        new[] { ("head", BuildHeadTable()), ("hhea", BuildHheaTable()), ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()), ("cmap", BuildCmapFormat4Table()),
            ("loca", BuildLocaTable()), ("glyf", BuildGlyfTable()) }.Concat(extra).ToArray());

    [Fact]
    public void VdmxLegacyDefaultsTerminateTheirOwnCharsetInventoryOnly()
    {
        byte[] table = VdmxTable(); VdmxU16(table, 0, 0);
        Array.Clear(table, 7, 3); // First inventory's ANSI default.
        table[10] = 0; // Distinct non-ANSI inventory is ignored for selection.
        Assert.True(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out var metric, out bool found));
        Assert.True(found); Assert.Equal(33, metric.Height);
        Array.Clear(table, 11, 3); // Two original legacy defaults remain distinct.
        Assert.True(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out metric, out found));
        Assert.True(found); Assert.Equal(33, metric.Height);
        table[10] = 1;
        Assert.False(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out metric, out found));
        Assert.False(found); Assert.Equal(default, metric);
        table[10] = 0; VdmxU16(table, 0, 1);
        Assert.False(SfntVerticalDeviceMetrics.TryRead(table, 12, 1, 1, out metric, out found));
        Assert.False(found); Assert.Equal(default, metric);
    }

    [Fact]
    public void VdmxFontQueryPreservesOrdinaryLineMetricsAndMissingTable()
    {
        var font = new TtfFont(VdmxFont(("VDMX", VdmxTable())));
        Assert.True(font.TryGetVerticalDeviceMetrics(12, out var metrics));
        Assert.Equal(15, metrics.Height);
        Assert.Equal(800, font.Ascender); Assert.Equal(-200, font.Descender); Assert.Equal(50, font.LineGap);
        Assert.False(font.TryGetVerticalDeviceMetrics(13, out metrics)); Assert.Equal(default, metrics);
        Assert.False(new TtfFont(VdmxFont()).TryGetVerticalDeviceMetrics(12, out metrics));
        Assert.Equal(default, metrics);
    }

    [Fact]
    public void VdmxFontQueryRejectsDuplicateMalformedAndIgnoredRawRecords()
    {
        var duplicate = new TtfFont(VdmxFont(("VDMX", VdmxTable()), ("VDMX", VdmxTable())));
        Assert.Throws<InvalidDataException>(() => duplicate.TryGetVerticalDeviceMetrics(12, out _));
        var malformed = new TtfFont(VdmxFont(("VDMX", new byte[4])));
        Assert.Throws<InvalidDataException>(() => malformed.TryGetVerticalDeviceMetrics(12, out _));
        byte[] bytes = VdmxFont(("VDMX", VdmxTable()));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12 + 7 * 16 + 8), uint.MaxValue);
        var ignored = new TtfFont(bytes);
        Assert.Throws<InvalidDataException>(() => ignored.TryGetVerticalDeviceMetrics(12, out _));
        var variable = new TtfFont(VdmxFont(("VDMX", VdmxTable()), ("fvar", Array.Empty<byte>())));
        Assert.False(variable.TryGetVerticalDeviceMetrics(12, out var metric)); Assert.Equal(default, metric);
    }
}
