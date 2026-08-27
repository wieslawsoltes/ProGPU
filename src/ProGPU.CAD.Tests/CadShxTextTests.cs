using System.Numerics;
using System.Text;
using ProGPU.CAD;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadShxTextTests
{
    [Fact]
    public void StandardLayoutCachesGlyphsAndMapsDocumentedControls()
    {
        CadShxGlyphCache cache = CreateCache();

        var layout = new CadShxTextLayout(
            "%%uA A%%u%%d%%p%%c%%065",
            cache);

        Assert.Equal(7, layout.Glyphs.Length);
        Assert.Equal(new ushort[] { 65, 32, 65, 256, 257, 258, 65 },
            layout.Glyphs.Span.ToArray().Select(item => item.Glyph.ShapeNumber));
        Assert.Equal(new Vector2(17.0f, 0.0f), layout.Advance);
        Assert.Equal(new Vector2(0.0f, 0.0f), layout.BoundsMin);
        Assert.Equal(new Vector2(14.0f, 1.0f), layout.BoundsMax);
        Assert.All(
            layout.Glyphs.Span[..3].ToArray(),
            item => Assert.Equal(CadShxTextDecoration.Underline, item.Decorations));
        Assert.All(
            layout.Glyphs.Span[3..].ToArray(),
            item => Assert.Equal(CadShxTextDecoration.None, item.Decorations));
        Assert.Equal(5, cache.Count);

        _ = new CadShxTextLayout("A%%d", cache);
        Assert.Equal(5, cache.Count);
    }

    [Fact]
    public void StandardSymbolScalarsUseReservedShapeNumbers()
    {
        CadShxGlyphCache cache = CreateCache();

        var layout = new CadShxTextLayout("\u00B0\u00B1\\U+2205", cache);

        Assert.Equal(new ushort[] { 256, 257, 258 },
            layout.Glyphs.Span.ToArray().Select(item => item.Glyph.ShapeNumber));
    }

    [Fact]
    public void DualOrientationCacheKeepsIndependentGlyphPrograms()
    {
        CadShxGlyphCache cache = CreateCache();

        var horizontal = new CadShxTextLayout("B", cache);
        var vertical = new CadShxTextLayout("B", cache, CadShxOrientation.Vertical);

        Assert.Equal(new Vector2(2.0f, 0.0f), horizontal.Advance);
        Assert.Equal(new Vector2(0.0f, -1.0f), vertical.Advance);
        Assert.Equal(2, cache.Count);
        Assert.NotSame(
            horizontal.Glyphs.Span[0].Glyph,
            vertical.Glyphs.Span[0].Glyph);
    }

    [Fact]
    public void StandardLayoutRejectsMissingUnicodeMalformedAndOversizedInput()
    {
        CadShxGlyphCache cache = CreateCache();

        Assert.Throws<InvalidDataException>(() => new CadShxTextLayout("C", cache));
        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout("\u0100", cache));
        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout("\ud83d\ude00", cache));
        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout("%%12", cache));
        Assert.Throws<NotSupportedException>(() => new CadShxTextLayout("\\U+12XZ", cache));
        Assert.Throws<InvalidDataException>(() => new CadShxTextLayout("%%u", cache));
        Assert.Throws<InvalidDataException>(() => new CadShxTextLayout(
            "AA",
            cache,
            options: new CadShxTextLayoutOptions { MaxGlyphs = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadShxTextLayout(
            "A",
            cache,
            options: new CadShxTextLayoutOptions { MaxCodeUnits = 0 }));
    }

    private static CadShxGlyphCache CreateCache()
    {
        CadShxFont font = CadShxFont.Parse(BuildStandardShx(
            (0, "TESTFONT", new byte[] { 10, 2, 2, 0 }),
            (32, "SPACE", new byte[] { 2, 8, 2, 0, 0 }),
            (65, "UCA", new byte[] { 0x14, 0x10, 2, 8, 3, 0xFF, 0 }),
            (66, "UCB", new byte[]
            {
                2, 14, 8, 0xFF, 2,
                1, 0x14,
                2, 8, 2, 0xFF,
                14, 8, 0xFF, 0xFD,
                0,
            }),
            (256, "DEGREE", new byte[] { 2, 8, 1, 0, 0 }),
            (257, "PLUSMINUS", new byte[] { 2, 8, 1, 0, 0 }),
            (258, "DIAMETER", new byte[] { 2, 8, 1, 0, 0 })));
        return new CadShxGlyphCache(font);
    }

    private static byte[] BuildStandardShx(
        params (ushort Number, string Name, byte[] Program)[] shapes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(shape => shape.Number));
        writer.Write(shapes.Max(shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return stream.ToArray();
    }
}
