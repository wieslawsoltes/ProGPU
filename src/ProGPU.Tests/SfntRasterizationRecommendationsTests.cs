using System.Buffers.Binary;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public partial class SfntFontFaceTests
{
    // Original selected-face bytes from independent Microsoft run39096839921.
    // Font SHA256 and all72 complete original images are retained in the evidence.
    private static byte[] GaspMicrosoftSans() => Convert.FromHexString("000000030008000200120001FFFF0003");
    private static byte[] GaspSegoe() => Convert.FromHexString("000100030008000A00130007FFFF000F");

    [Fact]
    public void GaspAllIntegralSizesMatchTheIndependentOriginalRangeInventory()
    {
        foreach (bool segoe in new[] { false, true })
        {
            byte[] table = segoe ? GaspSegoe() : GaspMicrosoftSans();
            for (int size = 1; size <= ushort.MaxValue; size++)
            {
                int middle = segoe ? 19 : 18;
                int maximum = size <= 8 ? 8 : size <= middle ? middle : 65535;
                int flags = size <= 8 ? (segoe ? 10 : 2) : size <= middle ? (segoe ? 7 : 1) : (segoe ? 15 : 3);
                Assert.True(SfntRasterizationRecommendations.TryRead(table, (ushort)size, out var range, out bool available));
                Assert.True(available);
                Assert.Equal(new FontRasterizationRange((ushort)(segoe ? 1 : 0), (ushort)size,
                    (ushort)maximum, (FontRasterizationRecommendations)flags), range);
            }
        }
    }

    [Theory]
    [InlineData(false, 1, 8, 2)]
    [InlineData(false, 8, 8, 2)]
    [InlineData(false, 9, 18, 1)]
    [InlineData(false, 13, 18, 1)]
    [InlineData(false, 18, 18, 1)]
    [InlineData(false, 19, 65535, 3)]
    [InlineData(false, 65535, 65535, 3)]
    [InlineData(true, 8, 8, 10)]
    [InlineData(true, 9, 19, 7)]
    [InlineData(true, 12, 19, 7)]
    [InlineData(true, 19, 19, 7)]
    [InlineData(true, 20, 65535, 15)]
    public void GaspOriginalFontRangesKeepExactInclusiveThresholds(bool segoe, int size, int maximum, int flags)
    {
        Assert.True(SfntRasterizationRecommendations.TryRead(segoe ? GaspSegoe() : GaspMicrosoftSans(),
            (ushort)size, out var range, out bool available));
        Assert.True(available);
        Assert.Equal(new FontRasterizationRange((ushort)(segoe ? 1 : 0), (ushort)size,
            (ushort)maximum, (FontRasterizationRecommendations)flags), range);
    }

    [Fact]
    public void GaspMissingCoverageDiffersFromAnExplicitZeroRecommendation()
    {
        byte[] table = Convert.FromHexString("00010001000C0000");
        Assert.True(SfntRasterizationRecommendations.TryRead(table, 12, out var range, out bool available));
        Assert.True(available);
        Assert.Equal(FontRasterizationRecommendations.None, range.Recommendations);
        Assert.Equal(12, range.MaximumPixelsPerEm);
        Assert.True(SfntRasterizationRecommendations.TryRead(table, 13, out range, out available));
        Assert.False(available); Assert.Equal(default, range);
        Assert.True(SfntRasterizationRecommendations.TryRead(new byte[4], 12, out range, out available));
        Assert.False(available); Assert.Equal(default, range);
    }

    [Fact]
    public void GaspEveryTruncatedPrefixAndExtraBytesRejectAtomically()
    {
        byte[] table = GaspSegoe();
        for (int length = 0; length < table.Length; length++)
        {
            Assert.False(SfntRasterizationRecommendations.TryRead(table.AsSpan(0, length), 12,
                out var range, out bool available));
            Assert.False(available); Assert.Equal(default, range);
        }
        Assert.False(SfntRasterizationRecommendations.TryRead([.. table, 0], 12, out var tail, out bool found));
        Assert.False(found); Assert.Equal(default, tail);
    }

    [Theory]
    [InlineData(0, 2)] // Unknown table version.
    [InlineData(2, 2)] // Inventory length mismatch.
    [InlineData(2, 65535)] // Oversized count, without arithmetic wraparound.
    [InlineData(8, 8)] // Duplicate second maximum.
    [InlineData(12, 18)] // Descending later range, after the selected record.
    [InlineData(12, 19)] // Duplicate later range.
    [InlineData(14, 16)] // Reserved later flag, after an otherwise valid selection.
    public void GaspValidatesTheWholeOriginalInventoryBeforeSelection(int offset, int value)
    {
        byte[] table = GaspSegoe();
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset), (ushort)value);
        Assert.False(SfntRasterizationRecommendations.TryRead(table, 1, out var range, out bool available));
        Assert.False(available); Assert.Equal(default, range);
    }

    [Fact]
    public void GaspVersionsDoNotAliasSymmetricFlagsToGrayscale()
    {
        byte[] table = Convert.FromHexString("00010001FFFF000C");
        Assert.True(SfntRasterizationRecommendations.TryRead(table, 12, out var range, out bool available));
        Assert.True(available);
        Assert.Equal(FontRasterizationRecommendations.SymmetricGridFit |
            FontRasterizationRecommendations.SymmetricSmoothing, range.Recommendations);
        table[1] = 0;
        Assert.False(SfntRasterizationRecommendations.TryRead(table, 12, out range, out available));
        Assert.False(available); Assert.Equal(default, range);
    }

    private static byte[] GaspFont(params (string Tag, byte[] Data)[] extra) => BuildSfntWithTables(
        new[] { ("head", BuildHeadTable()), ("hhea", BuildHheaTable()), ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()), ("cmap", BuildCmapFormat4Table()),
            ("loca", BuildLocaTable()), ("glyf", BuildGlyfTable()) }.Concat(extra).ToArray());

    [Fact]
    public void GaspFontQueryPreservesExistingStorageOwnershipAndOrdinaryMetrics()
    {
        byte[] bytes = GaspFont(("gasp", GaspMicrosoftSans()));
        var font = new TtfFont(bytes);
        // TtfFont owns the supplied normalized array; callers must not mutate it.
        // This query must not introduce a font copy or a different source owner.
        Assert.Same(bytes, font.OwnedFontData);
        Assert.True(font.TryGetStaticRasterizationRecommendations(13, out var range));
        Assert.Equal(FontRasterizationRecommendations.GridFit, range.Recommendations);
        Assert.Equal(18, range.MaximumPixelsPerEm);
        Assert.Equal(800, font.Ascender); Assert.Equal(-200, font.Descender); Assert.Equal(50, font.LineGap);
        Assert.False(new TtfFont(GaspFont()).TryGetStaticRasterizationRecommendations(13, out range));
        Assert.Equal(default, range);
    }

    [Fact]
    public void GaspRawDirectoryRejectsDuplicateMalformedAndLegacyIgnoredRecords()
    {
        var duplicate = new TtfFont(GaspFont(("gasp", GaspSegoe()), ("gasp", GaspSegoe())));
        Assert.Throws<InvalidDataException>(() => duplicate.TryGetStaticRasterizationRecommendations(12, out _));
        var malformed = new TtfFont(GaspFont(("gasp", new byte[3])));
        Assert.Throws<InvalidDataException>(() => malformed.TryGetStaticRasterizationRecommendations(12, out _));
        byte[] bytes = GaspFont(("gasp", GaspSegoe()));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12 + 7 * 16 + 8), uint.MaxValue);
        var ignored = new TtfFont(bytes);
        Assert.Throws<InvalidDataException>(() => ignored.TryGetStaticRasterizationRecommendations(12, out _));
    }

    [Fact]
    public void GaspStaticQueryCannotInventVariableInstanceThresholds()
    {
        var variable = new TtfFont(GaspFont(("gasp", GaspSegoe()), ("fvar", Array.Empty<byte>())));
        Assert.False(variable.TryGetStaticRasterizationRecommendations(12, out var range));
        Assert.Equal(default, range);
        var corruptVariable = new TtfFont(GaspFont(("gasp", new byte[3]), ("fvar", Array.Empty<byte>())));
        Assert.Throws<InvalidDataException>(() => corruptVariable.TryGetStaticRasterizationRecommendations(12, out _));
        byte[] ignored = GaspFont(("gasp", GaspSegoe()), ("fvar", Array.Empty<byte>()));
        BinaryPrimitives.WriteUInt32BigEndian(ignored.AsSpan(12 + 8 * 16 + 8), uint.MaxValue);
        Assert.False(new TtfFont(ignored).TryGetStaticRasterizationRecommendations(12, out range));
        Assert.Equal(default, range);
    }

    [Fact]
    public void GaspStaticQueryDoesNotAdmitFacesWithoutTrueTypeOutlines()
    {
        byte[] bytes = BuildSfntWithTables(("head", BuildHeadTable()), ("maxp", BuildMaxpTable()),
            ("cmap", BuildCmapFormat4Table()), ("gasp", GaspSegoe()));
        var font = new TtfFont(bytes);
        Assert.False(font.HasTrueTypeOutlines);
        Assert.False(font.TryGetStaticRasterizationRecommendations(12, out var range));
        Assert.Equal(default, range);
    }

    [Fact]
    public void GaspQueryUsesTheSelectedCollectionFaceDirectory()
    {
        byte[][] faces = [GaspFont(("gasp", GaspMicrosoftSans())), GaspFont(("gasp", GaspSegoe()))];
        byte[] collection = new byte[20 + faces.Sum(f => f.Length)];
        "ttcf"u8.CopyTo(collection);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(8), 2);
        int start = 20;
        for (int face = 0; face < faces.Length; face++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(12 + 4 * face), (uint)start);
            faces[face].CopyTo(collection, start);
            int count = BinaryPrimitives.ReadUInt16BigEndian(faces[face].AsSpan(4));
            for (int table = 0; table < count; table++)
            {
                int offset = start + 12 + 16 * table + 8;
                uint original = BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(offset));
                BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(offset), original + (uint)start);
            }
            start += faces[face].Length;
        }
        Assert.True(new TtfFont(collection, 0).TryGetStaticRasterizationRecommendations(12, out var first));
        Assert.True(new TtfFont(collection, 1).TryGetStaticRasterizationRecommendations(12, out var second));
        Assert.Equal(FontRasterizationRecommendations.GridFit, first.Recommendations);
        Assert.Equal((FontRasterizationRecommendations)7, second.Recommendations);
        Assert.Equal(0, first.Version); Assert.Equal(1, second.Version);
    }

    [Fact]
    public void GaspZeroQueryRejectsBeforePublishing()
    {
        Assert.False(SfntRasterizationRecommendations.TryRead(GaspSegoe(), 0, out var range, out bool available));
        Assert.False(available); Assert.Equal(default, range);
        var font = new TtfFont(GaspFont(("gasp", GaspSegoe())));
        Assert.Throws<ArgumentOutOfRangeException>(() => font.TryGetStaticRasterizationRecommendations(0, out range));
        Assert.Equal(default, range);
    }
}
