using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public class SfntFontSubsetterTests
{
    [Fact]
    public void CreatesGlyphIdPreservingTrueTypeSubsetWithCompositeDependencies()
    {
        byte[] fontData = BuildTrueTypeSubsetFixtureFont();
        SfntFontFace originalFace = SfntFontFace.Load(fontData);
        Assert.True(originalFace.TryGetTable("glyf", out ReadOnlyMemory<byte> originalGlyf));

        byte[] subset = SfntFontSubsetter.CreateGlyphIdPreservingSubset(fontData, 0, new ushort[] { 2 });

        Assert.True(subset.Length < fontData.Length);
        Assert.Equal(272, subset.Length);
        Assert.Equal(10017802304682166674UL, HashBytes(subset));
        SfntFontFace subsetFace = SfntFontFace.Load(subset);
        Assert.True(subsetFace.TryGetGlyphCount(out ushort glyphCount));
        Assert.Equal(4, glyphCount);
        Assert.True(subsetFace.TryGetTable("glyf", out ReadOnlyMemory<byte> subsetGlyf));
        Assert.True(subsetGlyf.Length < originalGlyf.Length);
        Assert.False(subsetFace.TryGetTable("DSIG", out _));
        Assert.True(subsetFace.TryGetTable("head", out ReadOnlyMemory<byte> head));
        Assert.Equal(1, ReadShort(head.Span, 50));

        Assert.True(subsetFace.TryGetGlyphBounds(1, out SfntGlyphBounds componentBounds));
        Assert.Equal(20, componentBounds.XMax);
        Assert.True(subsetFace.TryGetGlyphBounds(2, out SfntGlyphBounds compositeBounds));
        Assert.Equal(40, compositeBounds.XMax);
        Assert.True(subsetFace.TryGetGlyphBounds(3, out SfntGlyphBounds omittedBounds));
        Assert.Equal(0, omittedBounds.XMax);
    }

    [Fact]
    public void TryCreateGlyphIdPreservingSubsetFailsClosedForInvalidFonts()
    {
        Assert.False(SfntFontSubsetter.TryCreateGlyphIdPreservingSubset(
            new byte[] { 1, 2, 3 },
            0,
            new ushort[] { 1 },
            out byte[] subset));
        Assert.Empty(subset);
    }

    [Fact]
    public void CompactSubsetMatchesNativeByteOracle()
    {
        byte[] fontData = BuildTrueTypeSubsetFixtureFont();

        byte[] subset = SfntFontSubsetter.CreateCompactSubset(
            fontData,
            0,
            new ushort[] { 2 },
            out SfntGlyphRemap[] glyphMap);

        Assert.Equal(new[]
        {
            new SfntGlyphRemap(0, 0),
            new SfntGlyphRemap(1, 1),
            new SfntGlyphRemap(2, 2)
        }, glyphMap);
        Assert.Equal(264, subset.Length);
        Assert.Equal(5117190155084041207UL, HashBytes(subset));
    }

    [Fact]
    public void CreatesCompactTrueTypeSubsetWithGlyphRemappingAndCompositeRewrite()
    {
        byte[] fontData = BuildCompactTrueTypeSubsetFixtureFont();

        byte[] subset = SfntFontSubsetter.CreateCompactSubset(fontData, 0, new ushort[] { 3 }, out var glyphMap);

        Assert.Equal(new[]
        {
            new SfntGlyphRemap(0, 0),
            new SfntGlyphRemap(2, 1),
            new SfntGlyphRemap(3, 2)
        }, glyphMap);

        SfntFontFace subsetFace = SfntFontFace.Load(subset);
        Assert.True(subsetFace.TryGetGlyphCount(out ushort glyphCount));
        Assert.Equal(3, glyphCount);
        Assert.True(subsetFace.TryGetGlyphBounds(1, out SfntGlyphBounds componentBounds));
        Assert.Equal(20, componentBounds.XMax);
        Assert.True(subsetFace.TryGetGlyphBounds(2, out SfntGlyphBounds compositeBounds));
        Assert.Equal(40, compositeBounds.XMax);
        Assert.True(subsetFace.TryGetHorizontalGlyphMetrics(1, out SfntHorizontalGlyphMetrics componentMetrics));
        Assert.Equal(502, componentMetrics.AdvanceWidth);
        Assert.True(subsetFace.TryGetHorizontalGlyphMetrics(2, out SfntHorizontalGlyphMetrics compositeMetrics));
        Assert.Equal(503, compositeMetrics.AdvanceWidth);
        Assert.False(subsetFace.TryGetTable("DSIG", out _));
        Assert.False(subsetFace.TryGetTable("cmap", out _));
        Assert.False(subsetFace.TryGetTable("GSUB", out _));

        Assert.True(subsetFace.TryGetTable("loca", out ReadOnlyMemory<byte> loca));
        Assert.True(subsetFace.TryGetTable("glyf", out ReadOnlyMemory<byte> glyf));
        uint[] offsets = ReadLongLoca(loca.Span, glyphCount);
        int compositeOffset = checked((int)offsets[2]);
        Assert.Equal(-1, ReadShort(glyf.Span, compositeOffset));
        Assert.Equal(1, ReadUShort(glyf.Span, compositeOffset + 12));
    }

    [Fact]
    public void TryCreateCompactSubsetFailsClosedForInvalidFonts()
    {
        Assert.False(SfntFontSubsetter.TryCreateCompactSubset(
            new byte[] { 1, 2, 3 },
            0,
            new ushort[] { 1 },
            out byte[] subset,
            out var glyphMap));
        Assert.Empty(subset);
        Assert.Empty(glyphMap);
    }

    private static byte[] BuildTrueTypeSubsetFixtureFont()
    {
        byte[] glyf = BuildGlyfTable(out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable(4)),
            ("hmtx", BuildHmtxTable(4)),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf),
            ("DSIG", Enumerable.Repeat((byte)0xA5, 128).ToArray()));
    }

    private static byte[] BuildCompactTrueTypeSubsetFixtureFont()
    {
        byte[][] glyphs =
        {
            Array.Empty<byte>(),
            BuildSimpleGlyph(0, 0, 300, 300, 768),
            BuildSimpleGlyph(0, 0, 20, 20),
            BuildCompositeGlyph(0, 0, 40, 40, 2),
        };

        byte[] glyf = BuildGlyfTable(glyphs, out uint[] glyphOffsets);
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable(4)),
            ("hmtx", BuildHmtxTable(4)),
            ("loca", BuildLongLoca(glyphOffsets)),
            ("glyf", glyf),
            ("cmap", Enumerable.Repeat((byte)0xC0, 32).ToArray()),
            ("GSUB", Enumerable.Repeat((byte)0xAB, 32).ToArray()),
            ("DSIG", Enumerable.Repeat((byte)0xA5, 128).ToArray()));
    }

    private static byte[] BuildGlyfTable(out uint[] glyphOffsets)
    {
        byte[][] glyphs =
        {
            Array.Empty<byte>(),
            BuildSimpleGlyph(0, 0, 20, 20),
            BuildCompositeGlyph(0, 0, 40, 40, 1),
            BuildSimpleGlyph(0, 0, 300, 300, 768),
        };

        return BuildGlyfTable(glyphs, out glyphOffsets);
    }

    private static byte[] BuildGlyfTable(byte[][] glyphs, out uint[] glyphOffsets)
    {
        glyphOffsets = new uint[glyphs.Length + 1];
        using var stream = new MemoryStream();
        for (int i = 0; i < glyphs.Length; i++)
        {
            glyphOffsets[i] = checked((uint)stream.Position);
            stream.Write(glyphs[i]);
            WritePadding(stream);
        }

        glyphOffsets[^1] = checked((uint)stream.Position);
        return stream.ToArray();
    }

    private static byte[] BuildSimpleGlyph(short xMin, short yMin, short xMax, short yMax, int extraBytes = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, 1);
        WriteShort(writer, xMin);
        WriteShort(writer, yMin);
        WriteShort(writer, xMax);
        WriteShort(writer, yMax);
        writer.Write(new byte[extraBytes]);
        return stream.ToArray();
    }

    private static byte[] BuildCompositeGlyph(short xMin, short yMin, short xMax, short yMax, ushort componentGlyph)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, -1);
        WriteShort(writer, xMin);
        WriteShort(writer, yMin);
        WriteShort(writer, xMax);
        WriteShort(writer, yMax);
        WriteUShort(writer, 0x0002);
        WriteUShort(writer, componentGlyph);
        writer.Write((byte)0);
        writer.Write((byte)0);
        return stream.ToArray();
    }

    private static byte[] BuildHeadTable()
    {
        byte[] table = new byte[54];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, 0x00010000);
        stream.Position = 18;
        WriteUShort(writer, 1000);
        stream.Position = 50;
        WriteShort(writer, 1);
        return table;
    }

    private static byte[] BuildHheaTable()
    {
        byte[] table = new byte[36];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        stream.Position = 4;
        WriteShort(writer, 800);
        WriteShort(writer, -200);
        WriteShort(writer, 0);
        stream.Position = 34;
        WriteUShort(writer, 4);
        return table;
    }

    private static byte[] BuildMaxpTable(ushort glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, glyphCount);
        return stream.ToArray();
    }

    private static byte[] BuildHmtxTable(int glyphCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        for (int i = 0; i < glyphCount; i++)
        {
            WriteUShort(writer, checked((ushort)(500 + i)));
            WriteShort(writer, 0);
        }

        return stream.ToArray();
    }

    private static byte[] BuildLongLoca(uint[] glyphOffsets)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        foreach (uint offset in glyphOffsets)
        {
            WriteUInt(writer, offset);
        }

        return stream.ToArray();
    }

    private static byte[] BuildSfntWithTables(params (string Tag, byte[] Data)[] tables)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, checked((ushort)tables.Length));
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);

        uint tableOffset = checked((uint)(12 + tables.Length * 16));
        foreach ((string tag, byte[] data) in tables)
        {
            WriteTag(writer, tag);
            WriteUInt(writer, 0);
            WriteUInt(writer, tableOffset);
            WriteUInt(writer, checked((uint)data.Length));
            tableOffset += checked((uint)Align4(data.Length));
        }

        foreach ((_, byte[] data) in tables)
        {
            writer.Write(data);
            WritePadding(stream);
        }

        return stream.ToArray();
    }

    private static void WritePadding(Stream stream)
    {
        while ((stream.Position & 3) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static int Align4(int value)
    {
        return checked((value + 3) & ~3);
    }

    private static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static short ReadShort(ReadOnlySpan<byte> data, int offset)
    {
        return unchecked((short)((data[offset] << 8) | data[offset + 1]));
    }

    private static ushort ReadUShort(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset)
    {
        return (uint)((data[offset] << 24) |
                      (data[offset + 1] << 16) |
                      (data[offset + 2] << 8) |
                       data[offset + 3]);
    }

    private static uint[] ReadLongLoca(ReadOnlySpan<byte> loca, ushort glyphCount)
    {
        var offsets = new uint[glyphCount + 1];
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = ReadUInt(loca, i * 4);
        }

        return offsets;
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        Assert.Equal(4, tag.Length);
        foreach (char ch in tag)
        {
            writer.Write(checked((byte)ch));
        }
    }

    private static void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)((value >> 8) & 0xFF));
        writer.Write((byte)(value & 0xFF));
    }

    private static void WriteShort(BinaryWriter writer, short value)
    {
        WriteUShort(writer, unchecked((ushort)value));
    }

    private static void WriteUInt(BinaryWriter writer, uint value)
    {
        writer.Write((byte)((value >> 24) & 0xFF));
        writer.Write((byte)((value >> 16) & 0xFF));
        writer.Write((byte)((value >> 8) & 0xFF));
        writer.Write((byte)(value & 0xFF));
    }
}
