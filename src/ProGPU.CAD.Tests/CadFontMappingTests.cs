using System.Text;
using ProGPU.CAD;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadFontMappingTests
{
    [Fact]
    public void ParserRetainsDocumentedAsciiPairsAcrossLineEndings()
    {
        CadFontMappingTable table = Parse(
            "  romanc ; times.ttf  \r\n\ncomplex.shx; replacement.shx\rthird;third.otf");

        Assert.Equal(
            new[]
            {
                new CadFontMapping("romanc", "times.ttf"),
                new CadFontMapping("complex.shx", "replacement.shx"),
                new CadFontMapping("third", "third.otf"),
            },
            table.Mappings.ToArray());
    }

    [Fact]
    public void CatalogAppliesExtensionlessShxMappingsBeforeExactFonts()
    {
        CadShxGlyphCache original = CreateCache();
        CadShxGlyphCache replacement = CreateCache();
        var catalog = new CadShxFontCatalog();
        catalog.Register("original.shx", original);
        catalog.Register("replacement.shx", replacement);
        catalog.ApplyShxMappings(Parse("original;replacement.shx"));

        CadShxFontResolution resolution = catalog.Resolve(new CadShxFontRequest(
            "STYLE",
            @"C:\drawing\ORIGINAL.SHX",
            string.Empty));

        Assert.Same(replacement, resolution.GlyphCache);
        Assert.True(resolution.IsSubstitution);
        Assert.Equal(1, catalog.MappingCount);
        Assert.True(catalog.RemoveMapping("original"));
    }

    [Fact]
    public void CatalogRejectsCrossKindAndCanonicalDuplicatesAtomically()
    {
        var catalog = new CadShxFontCatalog();
        catalog.SetMapping("existing", "existing-replacement.shx");

        Assert.Throws<ArgumentException>(() => catalog.ApplyShxMappings(Parse(
            "first;first-replacement.shx\nsecond;second.ttf")));
        Assert.Throws<InvalidDataException>(() => catalog.ApplyShxMappings(Parse(
            "same;s1.shx\nsame.shx;s2.shx")));

        Assert.Equal(1, catalog.MappingCount);
        Assert.True(catalog.RemoveMapping("existing.shx"));
        Assert.False(catalog.RemoveMapping("first"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    [InlineData("missing-separator")]
    [InlineData(";replacement.shx")]
    [InlineData("original;")]
    [InlineData("original;replacement.shx;extra")]
    [InlineData("folder/original;replacement.shx")]
    [InlineData("original;folder/replacement.shx")]
    [InlineData("original;replacement")]
    [InlineData("original;r.shx\nORIGINAL;r2.shx")]
    [InlineData("# undocumented comments are rejected")]
    public void ParserRejectsAmbiguousOrEmptyInput(string source)
    {
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Fact]
    public void ParserRejectsNonAsciiAndConfiguredLimitOverruns()
    {
        byte[] nonAscii = Encoding.UTF8.GetBytes("original;réplacement.shx");

        Assert.Throws<InvalidDataException>(() => CadFontMappingTable.Parse(nonAscii));
        Assert.Throws<InvalidDataException>(() => CadFontMappingTable.Parse(
            "one;one.shx\ntwo;two.shx"u8,
            new CadFontMappingParseOptions { MaxMappings = 1 }));
        Assert.Throws<InvalidDataException>(() => CadFontMappingTable.Parse(
            "long;replacement.shx"u8,
            new CadFontMappingParseOptions { MaxFileBytes = 64, MaxLineBytes = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CadFontMappingTable.Parse(
            "one;one.shx"u8,
            new CadFontMappingParseOptions { MaxFileBytes = 4, MaxLineBytes = 8 }));
    }

    private static CadFontMappingTable Parse(string source) =>
        CadFontMappingTable.Parse(Encoding.ASCII.GetBytes(source));

    private static CadShxGlyphCache CreateCache()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write((ushort)0);
        writer.Write((ushort)65);
        writer.Write((ushort)2);
        writer.Write((ushort)0);
        writer.Write((ushort)9);
        writer.Write((ushort)65);
        writer.Write((ushort)7);
        writer.Write("TEST"u8);
        writer.Write((byte)0);
        writer.Write(new byte[] { 10, 2, 0, 0 });
        writer.Write("A"u8);
        writer.Write((byte)0);
        writer.Write(new byte[] { 2, 8, 3, 0, 0 });
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }
}
