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

    [Fact]
    public void CatalogResolvesPortableFilenamesAndExplicitAliases()
    {
        CadShxGlyphCache cache = CreateCache();
        var catalog = new CadShxFontCatalog();
        catalog.Register(@"C:\bundled\Simplex.shx", cache, "SIMPLEX");
        catalog.Register("simplex-alias.shx", cache);

        CadShxFontResolution exact = catalog.Resolve(new CadShxFontRequest(
            "IGNORED_STYLE",
            "/drawing/fonts/SIMPLEX.SHX",
            string.Empty));
        CadShxFontResolution style = catalog.Resolve(new CadShxFontRequest(
            "simplex",
            string.Empty,
            string.Empty));

        Assert.Same(cache, exact.GlyphCache);
        Assert.False(exact.IsSubstitution);
        Assert.Equal("Simplex.shx", exact.ResolvedFontName);
        Assert.Same(cache, style.GlyphCache);
        Assert.False(style.IsSubstitution);
        Assert.Equal(1, catalog.RegisteredFontCount);
        Assert.Equal(3, catalog.RegisteredNameCount);
    }

    [Fact]
    public void CatalogMappingOverridesOriginalAndMissingTargetFallsBack()
    {
        CadShxGlyphCache original = CreateCache();
        CadShxGlyphCache replacement = CreateCache();
        var catalog = new CadShxFontCatalog();
        catalog.Register("original.shx", original);
        catalog.Register("replacement.shx", replacement);
        ICadShxFontResolver originalGeneration = catalog.CreateResolverSnapshot();
        Assert.Same(originalGeneration, catalog.CreateResolverSnapshot());
        catalog.SetMapping("original.shx", "replacement.shx");

        CadShxFontResolution mapped = catalog.Resolve(new CadShxFontRequest(
            "STYLE",
            "original.shx",
            string.Empty));
        CadShxFontResolution retainedOriginal = originalGeneration.Resolve(
            new CadShxFontRequest("STYLE", "original.shx", string.Empty));
        Assert.NotSame(originalGeneration, catalog.CreateResolverSnapshot());
        catalog.SetMapping("original.shx", "missing.shx");
        CadShxFontResolution fallbackToOriginal = catalog.Resolve(new CadShxFontRequest(
            "STYLE",
            "original.shx",
            string.Empty));

        Assert.Same(replacement, mapped.GlyphCache);
        Assert.True(mapped.IsSubstitution);
        Assert.Equal("replacement.shx", mapped.ResolvedFontName);
        Assert.Same(original, retainedOriginal.GlyphCache);
        Assert.False(retainedOriginal.IsSubstitution);
        Assert.Same(original, fallbackToOriginal.GlyphCache);
        Assert.False(fallbackToOriginal.IsSubstitution);
        Assert.Equal(1, catalog.MappingCount);
        Assert.True(catalog.RemoveMapping(@"C:\fonts\ORIGINAL.SHX"));
        Assert.Equal(0, catalog.MappingCount);
    }

    [Fact]
    public void CatalogAlternateIsExplicitAndBigFontRequestsStayUnresolved()
    {
        CadShxGlyphCache alternate = CreateCache();
        var catalog = new CadShxFontCatalog();
        catalog.Register("simplex.shx", alternate, "DEFAULT");
        catalog.SetAlternate("default");

        CadShxFontResolution substituted = catalog.Resolve(new CadShxFontRequest(
            "MISSING",
            "missing.shx",
            string.Empty));
        CadShxFontResolution bigFont = catalog.Resolve(new CadShxFontRequest(
            "BIG",
            "simplex.shx",
            "asian.shx"));

        Assert.Same(alternate, substituted.GlyphCache);
        Assert.True(substituted.IsSubstitution);
        Assert.Equal("simplex.shx", catalog.AlternateFontName);
        Assert.Null(bigFont.GlyphCache);
        catalog.ClearAlternate();
        Assert.Null(catalog.Resolve(new CadShxFontRequest(
            "MISSING",
            "missing.shx",
            string.Empty)).GlyphCache);
        Assert.Throws<KeyNotFoundException>(() => catalog.SetAlternate("unknown.shx"));
    }

    [Fact]
    public void CatalogRegistrationIsAtomicAndParsedSourcesAreOwned()
    {
        CadShxGlyphCache first = CreateCache();
        CadShxGlyphCache second = CreateCache();
        var catalog = new CadShxFontCatalog();
        catalog.Register("first.shx", first, "SHARED");

        Assert.Throws<InvalidOperationException>(() =>
            catalog.Register("second.shx", second, "shared"));
        Assert.Equal(1, catalog.RegisteredFontCount);
        Assert.Equal(2, catalog.RegisteredNameCount);
        Assert.Throws<ArgumentException>(() => catalog.Register("not-a-font.ttf", second));

        byte[] source = BuildStandardShx(
            (0, "OWNED", new byte[] { 10, 2, 0, 0 }),
            (65, "UCA", new byte[] { 2, 8, 3, 0, 0 }));
        CadShxGlyphCache parsed = catalog.ParseAndRegister(
            "owned.shx",
            source,
            aliases: ["OWNED"]);
        Array.Fill(source, (byte)0);

        Parallel.For(0, 1_000, _ =>
        {
            CadShxFontResolution resolution = catalog.Resolve(new CadShxFontRequest(
                "OWNED",
                "owned.shx",
                string.Empty));
            Assert.Same(parsed, resolution.GlyphCache);
        });
        Assert.Equal(new Vector2(3, 0), parsed.GetGlyph(65).Advance);
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
