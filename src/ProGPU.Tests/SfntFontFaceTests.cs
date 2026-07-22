using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using ProGPU.Tests.Headless;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public class SfntFontFaceTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z3GQAAAAASUVORK5CYII=";

    private const string CffWoffFontBase64 = """
        d09GRk9UVE8AAAL0AAkAAAAAA9AAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAABDRkYgAAACbAAAAH4AAACSuCV3zE9TLzIAAAFA
        AAAALgAAAGBFIUPRY21hcAAAAjQAAAApAAAANAAMAJRoZWFkAAAA4AAAADYAAAA2LfovUmhoZWEAAAEYAAAAIAAAACQEsgGS
        aG10eAAAAuwAAAAGAAAABgImAABtYXhwAAABOAAAAAYAAAAGAAJQAG5hbWUAAAFwAAAAwwAAAX3OQCP/cG9zdAAAAmAAAAAM
        AAAAIAADAAAAAQAAAAEAAOYHGIpfDzz1AAMD6AAAAADmd/XbAAAAAOZ39dsAMgAAAcICvAAAAAMAAgAAAAAAAHicY2BkYGBW
        +G/BwMD4hYGBIYVxAgNQBAUwAgBTuQNIAABQAAACAAB4nGNgZvzCOIGBlYGFgTBgRObYAwGQcmRwZFb4b8HAwKzAcAJNvQID
        AwD5rQV3AAB4nIWPwQqCQBCGP9OKKOpU3WLPgaK37oKdAons3kEiEoVVD71Ir9BT9G6NuYF1aQZ2vpn/n4EFJtyxaMJi9n6b
        6DGUrmWbJQvDTof7jHEND5izEafljGSyYme4x5SrYRuPm2Gnw325+DA8YM0z1sU2TlQYReqQltU+PdfZSf9MlRkfU11eilwFnt
        86xNDortGJ0RRspSYoQiJJxYGUkoq91DM1GSf+edWP+yidFuUiO7mogXzK/7rRXvjsu9/7L3i8OwsAeJxjYGBgYmBgYAZiESD
        JCKZZGBSANAsQgviO//9DyP8HwHwGAFKXBp0AAAB4nGNgZsALAAB9AAR4nGNkYGFkYGRkFAsoyncPCHV2cwtJLS7RDUpNL81J
        LALJSP2QZvohw/xDgmVv94+wnwGsX/m7v6sJfWcUZGBiZJRQh2hUAOpUAGlVgGpFE2YAAiWQBl6FHx17xX4G/FD5G8DO96Pj
        vdiPDvbvfd8PdP/uk/rTwc4HAFZkMPwAAAH0AAAAMgAA
        """;

    [Fact]
    public void ReadsNamesFromSfntNameTable()
    {
        byte[] fontData = BuildSfnt("ProGPU Sans", "ProGPU Sans Regular");

        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.True(face.TryGetName(SfntNameIds.FamilyName, out string familyName));
        Assert.True(face.TryGetName(SfntNameIds.FullName, out string fullName));
        Assert.Equal("ProGPU Sans", familyName);
        Assert.Equal("ProGPU Sans Regular", fullName);
        Assert.True(face.TryGetTable("name", out ReadOnlyMemory<byte> nameTable));
        Assert.NotEqual(0, nameTable.Length);
    }

    [Fact]
    public void PrefersLatinCanonicalNamesOverLocalizedWindowsNames()
    {
        byte[] fontData = BuildSfnt(BuildLocalizedNameTable());
        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.True(face.TryGetName(SfntNameIds.FamilyName, out string familyName));
        Assert.True(face.TryGetName(SfntNameIds.FullName, out string fullName));
        Assert.Equal("Geeza Pro", familyName);
        Assert.Equal("Geeza Pro Regular", fullName);

        string file = Path.Combine(Path.GetTempPath(), $"progpu-localized-font-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, fontData);
        try
        {
            FontInfo? info = FontApi.ParseFontInfo(file);

            Assert.NotNull(info);
            Assert.Equal("Geeza Pro", info.FamilyName);
            Assert.Equal("Geeza Pro Regular", info.Name);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FontApiParsesFontInfoWithFaceIndex()
    {
        string file = Path.Combine(Path.GetTempPath(), $"progpu-font-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, BuildSfnt("ProGPU Serif", "ProGPU Serif Bold"));

        try
        {
            FontInfo? info = FontApi.ParseFontInfo(file);

            Assert.NotNull(info);
            Assert.Equal("ProGPU Serif", info.FamilyName);
            Assert.Equal("ProGPU Serif Bold", info.Name);
            Assert.Equal(0, info.FaceIndex);
            Assert.Equal(file, info.FilePath);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FontApiStreamsMetadataWithoutReadingTheWholeFontFile()
    {
        string file = Path.Combine(Path.GetTempPath(), $"progpu-font-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, BuildSfnt("ProGPU Sparse", "ProGPU Sparse Regular"));
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        try
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            FontInfo? info = FontApi.ParseFontInfo(file);

            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.NotNull(info);
            Assert.Equal("ProGPU Sparse", info.FamilyName);
            Assert.Equal("ProGPU Sparse Regular", info.Name);
            Assert.True(allocatedBytes < 1024 * 1024, $"Allocated {allocatedBytes:N0} bytes.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FontApiStreamsCharacterMapWithoutReadingTheWholeFontFile()
    {
        string file = Path.Combine(Path.GetTempPath(), $"progpu-font-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, BuildMetricsSfnt());
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        try
        {
            var info = new FontInfo { FilePath = file, FaceIndex = 0 };
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            bool containsA = FontApi.ContainsGlyph(info, 'A');
            bool containsB = FontApi.ContainsGlyph(info, 'B');

            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(containsA);
            Assert.False(containsB);
            Assert.True(allocatedBytes < 1024 * 1024, $"Allocated {allocatedBytes:N0} bytes.");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FontApiParsesEveryTrueTypeCollectionFace()
    {
        string file = Path.Combine(Path.GetTempPath(), $"progpu-font-{Guid.NewGuid():N}.ttc");
        File.WriteAllBytes(file, BuildTtc(
            ("ProGPU Mono", "ProGPU Mono Regular"),
            ("ProGPU Mono", "ProGPU Mono Bold")));

        try
        {
            List<FontInfo> infos = FontApi.ParseFontInfos(file);

            Assert.Equal(2, infos.Count);
            Assert.Equal("ProGPU Mono Regular", infos[0].Name);
            Assert.Equal(0, infos[0].FaceIndex);
            Assert.Equal("ProGPU Mono Bold", infos[1].Name);
            Assert.Equal(1, infos[1].FaceIndex);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void LoadsTrueTypeCollectionFaces()
    {
        byte[] fontData = BuildTtc(
            ("ProGPU Mono", "ProGPU Mono Regular"),
            ("ProGPU Mono", "ProGPU Mono Bold"));

        IReadOnlyList<SfntFontFace> faces = SfntFontFace.LoadFaces(fontData);

        Assert.Equal(2, faces.Count);
        Assert.Equal(0, faces[0].FaceIndex);
        Assert.Equal(1, faces[1].FaceIndex);
        Assert.True(faces[0].TryGetName(SfntNameIds.FullName, out string firstName));
        Assert.True(faces[1].TryGetName(SfntNameIds.FullName, out string secondName));
        Assert.Equal("ProGPU Mono Regular", firstName);
        Assert.Equal("ProGPU Mono Bold", secondName);
    }

    [Fact]
    public void TtfFontLoadsRequestedCollectionFace()
    {
        byte[] fontData = BuildMetricsTtc(1000, 2048);

        var first = new TtfFont(fontData, 0);
        var second = new TtfFont(fontData, 1);

        Assert.Equal(0, first.FaceIndex);
        Assert.Equal(1000, first.UnitsPerEm);
        Assert.Equal(1, second.FaceIndex);
        Assert.Equal(2048, second.UnitsPerEm);
        Assert.Equal(2, TtfFont.GetFaceCount(fontData));
        Assert.True(second.TryGetTable("head", out ReadOnlyMemory<byte> head));
        Assert.Equal(2048, (head.Span[18] << 8) | head.Span[19]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TtfFont(fontData, 2));
    }

    [Fact]
    public void TtfFontReadsClosestSbixStrikeAndDuplicateGlyph()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("sbix", BuildSbixTable()));

        var font = new TtfFont(fontData);

        Assert.True(font.HasBitmapGlyphs);
        Assert.False(font.HasTrueTypeOutlines);
        Assert.True(font.TryGetBitmapGlyph(1, 35, out BitmapGlyphData direct));
        Assert.Equal(40, direct.PixelsPerEm);
        Assert.Equal(72, direct.PixelsPerInch);
        Assert.Equal(-4, direct.OriginOffsetX);
        Assert.Equal(12, direct.OriginOffsetY);
        Assert.Equal(TtfFont.PngBitmapGraphicType, direct.GraphicType);
        Assert.Equal(new byte[] { 40, 41, 42 }, direct.Data.ToArray());

        Assert.True(font.TryGetBitmapGlyph(2, 19, out BitmapGlyphData duplicate));
        Assert.Equal(20, duplicate.PixelsPerEm);
        Assert.Equal(7, duplicate.OriginOffsetX);
        Assert.Equal(8, duplicate.OriginOffsetY);
        Assert.Equal(new byte[] { 20, 21, 22 }, duplicate.Data.ToArray());
    }

    [Fact]
    public void FallbackFontLoadsOnlyRequestedSbixGlyphData()
    {
        byte[] sourceSbix = BuildSbixTable();
        var oversizedSbix = new byte[32 * 1024 * 1024];
        sourceSbix.CopyTo(oversizedSbix, 0);
        byte[] sourceFont = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("sbix", oversizedSbix));
        string file = Path.Combine(Path.GetTempPath(), $"progpu-sbix-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, sourceFont);

        try
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            TtfFont font = TtfFont.LoadGlyphResidentFile(file, 0, 1);

            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(font.FontData.Length < 1024 * 1024, $"Resident font is {font.FontData.Length:N0} bytes.");
            Assert.True(allocatedBytes < 2 * 1024 * 1024, $"Allocated {allocatedBytes:N0} bytes.");
            Assert.True(font.TryGetBitmapGlyph(1, 35, out BitmapGlyphData glyph));
            Assert.Equal(new byte[] { 40, 41, 42 }, glyph.Data.ToArray());
            Assert.False(font.TryGetBitmapGlyph(2, 35, out _));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void GlyphAtlasUploadsSbixAsIntrinsicColorBitmap()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("sbix", BuildSingleSbixTable(Convert.FromBase64String(OnePixelPngBase64))));
        var font = new TtfFont(fontData);
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, 64);

        GlyphInfo info = atlas.GetOrCreateGlyphByIndex(font, 1, 40f);

        Assert.True(info.IsColorBitmap);
        Assert.Equal(1u, info.Width);
        Assert.Equal(1u, info.Height);
        Assert.Equal(2f, info.BearX);
        Assert.Equal(5f, info.BearY);
        Assert.Equal(2f, info.RasterScale);
        Assert.True(info.TexCoordMax.X > info.TexCoordMin.X);
        Assert.True(info.TexCoordMax.Y > info.TexCoordMin.Y);
        Assert.Contains(atlas.ColorAtlasTexture.ReadPixels(), static value => value != 0);
        Assert.All(atlas.AtlasTexture.ReadPixels(), static value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TtfFontReadsEveryCblcIndexFormat(ushort indexFormat)
    {
        byte[] imageData = Convert.FromBase64String(OnePixelPngBase64);
        (byte[] cblc, byte[] cbdt) = BuildSingleCbdtTables(imageData, indexFormat);
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("CBLC", cblc),
            ("CBDT", cbdt));

        var font = new TtfFont(fontData);

        Assert.True(font.HasBitmapGlyphs);
        Assert.True(font.TryGetBitmapGlyph(1, 30f, out BitmapGlyphData glyph));
        Assert.Equal(20, glyph.PixelsPerEm);
        Assert.True(glyph.UsesHorizontalMetrics);
        Assert.Equal(3, glyph.BearingX);
        Assert.Equal(4, glyph.BearingY);
        Assert.Equal(TtfFont.PngBitmapGraphicType, glyph.GraphicType);
        Assert.Equal(imageData, glyph.Data.ToArray());
    }

    [Fact]
    public void GlyphAtlasUploadsCbdtUsingBitmapBearings()
    {
        (byte[] cblc, byte[] cbdt) = BuildSingleCbdtTables(
            Convert.FromBase64String(OnePixelPngBase64));
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("CBLC", cblc),
            ("CBDT", cbdt));
        var font = new TtfFont(fontData);
        using var atlas = new GlyphAtlas(HeadlessWindow.Shared.Context, 64);

        GlyphInfo info = atlas.GetOrCreateGlyphByIndex(font, 1, 40f);

        Assert.True(info.IsColorBitmap);
        Assert.Equal(1u, info.Width);
        Assert.Equal(1u, info.Height);
        Assert.Equal(3f, info.BearX);
        Assert.Equal(-4f, info.BearY);
        Assert.Equal(2f, info.RasterScale);
        Assert.Contains(atlas.ColorAtlasTexture.ReadPixels(), static value => value != 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TtfFontReadsPlainAndGzipOpenTypeSvgGlyphs(bool compress)
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" id="glyph1">
              <defs>
                <linearGradient id="gradient" x1="0" y1="0" x2="10" y2="0">
                  <stop offset="0" stop-color="#ff0000" />
                  <stop offset="1" stop-color="#0000ff" />
                </linearGradient>
                <path id="shape" d="M0 0 L10 0 L10 10 L0 10 Z" />
              </defs>
              <g transform="translate(10 20)">
                <use href="#shape" fill="url(#gradient)" />
              </g>
            </svg>
            """;
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("SVG ", BuildSingleSvgTable(svg, compress)));
        var font = new TtfFont(fontData);

        List<FontColorLayer>? layers = font.GetColorLayers(1);

        FontColorLayer layer = Assert.Single(Assert.IsType<List<FontColorLayer>>(layers));
        Assert.True(font.HasColorLayers(1));
        Assert.True(layer.UsesSvgCoordinates);
        Assert.IsType<LinearGradientBrush>(layer.Brush);
        Assert.NotNull(layer.Geometry);
        Assert.True(layer.Geometry!.TryGetBounds(out var minimum, out var maximum));
        Assert.Equal(new Vector2(10, 20), minimum);
        Assert.Equal(new Vector2(20, 30), maximum);
        var gradient = Assert.IsType<LinearGradientBrush>(layer.Brush);
        Assert.Equal(new Vector2(10, 20), gradient.StartPoint);
        Assert.Equal(new Vector2(20, 20), gradient.EndPoint);
    }

    [Fact]
    public void TtfFontAcceptsBitmapOnlyBhedFonts()
    {
        byte[] fontData = BuildSfntWithTables(
            ("bhed", BuildHeadTable(2048)),
            ("maxp", BuildMaxpTable(3)),
            ("cmap", BuildCmapFormat4Table()),
            ("bloc", Array.Empty<byte>()),
            ("bdat", Array.Empty<byte>()));

        var font = new TtfFont(fontData);

        Assert.Equal(2048, font.UnitsPerEm);
        Assert.True(font.HasBitmapGlyphs);
        Assert.False(font.HasTrueTypeOutlines);
    }

    [Fact]
    public void TtfFontBuildsScaledAndTranslatedCompositeOutline()
    {
        (byte[] loca, byte[] glyf) = BuildCompositeGlyphTables();
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable(3)),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4Table()),
            ("loca", loca),
            ("glyf", glyf));

        var font = new TtfFont(fontData);
        var outline = font.GetGlyphOutline(2);

        Assert.NotNull(outline);
        Assert.Single(outline.Figures);
        Assert.Equal(new Vector2(50, 20), outline.Figures[0].StartPoint);
        Assert.Contains(
            outline.Figures[0].Segments,
            segment => segment is LineSegment { Point: var point } && point == new Vector2(200, 170));
    }

    [Fact]
    public void TtfFontPublishesOneImmutableOutlineAcrossConcurrentCacheHits()
    {
        (byte[] loca, byte[] glyf) = BuildCompositeGlyphTables();
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable(3)),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4Table()),
            ("loca", loca),
            ("glyf", glyf));
        var font = new TtfFont(fontData);
        var outlines = new PathGeometry?[256];

        Parallel.For(0, outlines.Length, index => outlines[index] = font.GetGlyphOutline(2));

        PathGeometry first = Assert.IsType<PathGeometry>(outlines[0]);
        Assert.All(outlines, outline => Assert.Same(first, outline));
    }

    [Fact]
    public void TtfFontPublishesAsciiCmapHitsAndMissesAcrossConcurrentLookups()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable(3)),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4Table()),
            ("loca", BuildLocaTable(3)),
            ("glyf", BuildGlyfTable()));
        var font = new TtfFont(fontData);
        var mappings = new ushort[512];

        Parallel.For(
            0,
            mappings.Length,
            index => mappings[index] = font.GetGlyphIndex(index % 2 == 0 ? 'A' : 'Z'));

        for (var index = 0; index < mappings.Length; index++)
        {
            Assert.Equal(index % 2 == 0 ? (ushort)1 : (ushort)0, mappings[index]);
        }
    }

    [Fact]
    public void TtfFontLoadsCffOutlinesFromWoffContainer()
    {
        byte[] fontData = Convert.FromBase64String(CffWoffFontBase64);

        var font = new TtfFont(fontData);
        ushort glyphIndex = font.GetGlyphIndex('A');
        PathGeometry? outline = font.GetGlyphOutline(glyphIndex);

        Assert.Equal("ProGPU CFF Test", font.FamilyName);
        Assert.False(font.HasTrueTypeOutlines);
        Assert.True(font.HasCffOutlines);
        Assert.Equal((ushort)1, glyphIndex);
        Assert.NotNull(outline);
        Assert.Contains(
            outline!.Figures.SelectMany(static figure => figure.Segments),
            static segment => segment is CubicBezierSegment);
        var (_, gpuSegments) = font.CompileGpuOutlineData();
        Assert.Contains(gpuSegments, static segment => segment.SegmentType == 2);
        Assert.Equal(48, System.Runtime.InteropServices.Marshal.SizeOf<GpuSegment>());
        Assert.True(font.TryGetGlyphBounds(glyphIndex, out var xMin, out var yMin, out var xMax, out var yMax));
        Assert.Equal((short)100, xMin);
        Assert.Equal((short)0, yMin);
        Assert.Equal((short)400, xMax);
        Assert.Equal((short)750, yMax);
        Assert.False(font.IsCffFallbackLoaded);
        Assert.Equal("OTTO", Encoding.ASCII.GetString(font.FontData.Span[..4]));
    }

    [Fact]
    public void TtfFontLoadsCffOutlinesFromTrueTypeCollectionFace()
    {
        byte[] woffData = Convert.FromBase64String(CffWoffFontBase64);
        byte[] standaloneData = new TtfFont(woffData).FontData.ToArray();
        byte[] collectionData = BuildSingleFaceTtc(standaloneData);

        var font = new TtfFont(collectionData, 0);
        ushort glyphIndex = font.GetGlyphIndex('A');
        PathGeometry? outline = font.GetGlyphOutline(glyphIndex);

        Assert.Equal("ProGPU CFF Test", font.FamilyName);
        Assert.Equal(0, font.FaceIndex);
        Assert.True(font.HasCffOutlines);
        Assert.Equal((ushort)1, glyphIndex);
        Assert.NotNull(outline);
        Assert.Contains(
            outline!.Figures.SelectMany(static figure => figure.Segments),
            static segment => segment is CubicBezierSegment);
    }

    [Fact]
    public void TtfFontRejectsWoffTableOutsideDeclaredContainer()
    {
        byte[] fontData = Convert.FromBase64String(CffWoffFontBase64);
        fontData[48] = 0xFF;
        fontData[49] = 0xFF;
        fontData[50] = 0xFF;
        fontData[51] = 0xFF;

        Assert.Throws<FormatException>(() => new TtfFont(fontData));
    }

    [Fact]
    public void ReadsCmapMetricsGlyphBoundsAndEmbeddingRights()
    {
        byte[] fontData = BuildMetricsSfnt();

        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.False(face.UsesSymbolCharacterMap);
        Assert.True(face.TryGetGlyphCount(out ushort glyphCount));
        Assert.Equal(2, glyphCount);
        Assert.True(face.TryGetGlyphIndex('A', out ushort glyphIndex));
        Assert.Equal(1, glyphIndex);
        Assert.True(face.TryGetHorizontalGlyphMetrics(glyphIndex, out SfntHorizontalGlyphMetrics metrics));
        Assert.Equal(600, metrics.AdvanceWidth);
        Assert.Equal(-20, metrics.LeftSideBearing);
        Assert.True(face.TryGetGlyphBounds(glyphIndex, out SfntGlyphBounds bounds));
        Assert.Equal(-10, bounds.XMin);
        Assert.Equal(-20, bounds.YMin);
        Assert.Equal(300, bounds.XMax);
        Assert.Equal(700, bounds.YMax);
        Assert.True(face.TryGetEmbeddingRights(out ushort fsType));
        Assert.Equal(0x0008, fsType);
    }

    [Fact]
    public void FallsBackToFormat4WhenFormat12MissesBmpGlyph()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4And12Table()),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));

        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.True(face.TryGetGlyphIndex('A', out ushort glyphIndex));
        Assert.Equal(1, glyphIndex);
    }

    [Fact]
    public void ReadsManyToOneFormat13CharacterMap()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat13Table()),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));

        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.True(face.TryGetGlyphIndex(0x263a, out ushort first));
        Assert.True(face.TryGetGlyphIndex(0x1f636, out ushort second));
        Assert.True(face.TryGetGlyphIndex(0x10ffff, out ushort missing));
        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(0, missing);
    }

    [Theory]
    [InlineData(0xB200, 0xF14A)]
    [InlineData(0xB300, 0xF24C)]
    public void MapsLegacyArabicSymbolCmapsFromOs2FontPage(ushort fontPage, ushort privateCodePoint)
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapSymbolFormat4Table(privateCodePoint)),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table(fontPage)));

        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.True(face.UsesSymbolCharacterMap);
        Assert.True(face.TryGetGlyphIndex(0x0628, out ushort glyphIndex));
        Assert.Equal(1, glyphIndex);
    }

    [Fact]
    public void MapsOrdinarySymbolCmapLowBytesIntoF000PrivateUseArea()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapSymbolFormat4Table(0xF041)),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));

        SfntFontFace face = SfntFontFace.Load(fontData);

        Assert.True(face.TryGetGlyphIndex(0x41, out ushort glyphIndex));
        Assert.Equal(1, glyphIndex);
    }

    [Fact]
    public void ReadsNonDefaultFormat14VariationGlyph()
    {
        byte[] fontData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4And14Table()),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));

        var font = new TtfFont(fontData);

        Assert.True(font.TryGetVariationGlyph('A', 0xFE0F, out ushort glyph));
        Assert.Equal(1, glyph);
        Assert.False(font.TryGetVariationGlyph('B', 0xFE0F, out _));
    }

    [Fact]
    public void TextLayoutKeepsVariationSelectorInPrecedingFallbackRun()
    {
        const ushort baseCodePoint = 0xE123;
        byte[] primaryData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4Table()),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));
        byte[] fallbackData = BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable(3)),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4And14Table(baseCodePoint, variationGlyph: 2)),
            ("loca", BuildLocaTable(3)),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));
        var primary = new TtfFont(primaryData);
        var fallback = new TtfFont(fallbackData);
        FontManager.Default.RegisterFont(
            "ProGPU Test Variation Fallback",
            new Lazy<TtfFont>(() => fallback),
            FontStyleRequest.Normal,
            isFallback: true);

        var layout = new TextLayout("\uE123\uFE0F", primary, 16f);

        TextRunGlyph glyph = Assert.Single(layout.Glyphs);
        Assert.Same(fallback, glyph.Font);
        Assert.Equal((ushort)2, glyph.GlyphIndex);
    }

    private static byte[] BuildSfnt(string familyName, string fullName)
    {
        return BuildSfnt(BuildNameTable(familyName, fullName));
    }

    private static byte[] BuildSfnt(byte[] nameTable)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteSfntFace(writer, 0, nameTable);
        return stream.ToArray();
    }

    private static byte[] BuildLocalizedNameTable()
    {
        var values = new[]
        {
            (Platform: (ushort)3, Encoding: (ushort)1, Language: (ushort)0x0420, NameId: SfntNameIds.FamilyName, Value: "\u06AF\u06CC\u0632\u0627 \u067E\u0631\u0648"),
            (Platform: (ushort)3, Encoding: (ushort)1, Language: (ushort)0x0420, NameId: SfntNameIds.FullName, Value: "\u06AF\u06CC\u0632\u0627 \u067E\u0631\u0648 \u0631\u06CC\u06AF\u0648\u0644\u0631"),
            (Platform: (ushort)0, Encoding: (ushort)4, Language: (ushort)0, NameId: SfntNameIds.FamilyName, Value: "Geeza Pro"),
            (Platform: (ushort)0, Encoding: (ushort)4, Language: (ushort)0, NameId: SfntNameIds.FullName, Value: "Geeza Pro Regular")
        };
        byte[][] encodedValues = values
            .Select(static value => Encoding.BigEndianUnicode.GetBytes(value.Value))
            .ToArray();
        int stringOffset = 6 + values.Length * 12;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteUShort(writer, 0);
        WriteUShort(writer, (ushort)values.Length);
        WriteUShort(writer, (ushort)stringOffset);

        int valueOffset = 0;
        for (int i = 0; i < values.Length; i++)
        {
            WriteUShort(writer, values[i].Platform);
            WriteUShort(writer, values[i].Encoding);
            WriteUShort(writer, values[i].Language);
            WriteUShort(writer, values[i].NameId);
            WriteUShort(writer, (ushort)encodedValues[i].Length);
            WriteUShort(writer, (ushort)valueOffset);
            valueOffset += encodedValues[i].Length;
        }

        for (int i = 0; i < encodedValues.Length; i++)
        {
            writer.Write(encodedValues[i]);
        }

        return stream.ToArray();
    }

    private static byte[] BuildTtc(params (string FamilyName, string FullName)[] faces)
    {
        byte[][] nameTables = faces
            .Select(face => BuildNameTable(face.FamilyName, face.FullName))
            .ToArray();

        uint[] faceOffsets = new uint[faces.Length];
        uint offset = (uint)(12 + faces.Length * 4);
        for (int i = 0; i < faces.Length; i++)
        {
            faceOffsets[i] = offset;
            offset += (uint)(28 + nameTables[i].Length);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteTag(writer, "ttcf");
        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, (uint)faces.Length);
        foreach (uint faceOffset in faceOffsets)
        {
            WriteUInt(writer, faceOffset);
        }

        for (int i = 0; i < faces.Length; i++)
        {
            stream.Position = faceOffsets[i];
            WriteSfntFace(writer, faceOffsets[i], nameTables[i]);
        }

        return stream.ToArray();
    }

    private static void WriteSfntFace(BinaryWriter writer, uint faceOffset, byte[] nameTable)
    {
        uint nameTableOffset = faceOffset + 28;
        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, 1);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteTag(writer, "name");
        WriteUInt(writer, 0);
        WriteUInt(writer, nameTableOffset);
        WriteUInt(writer, (uint)nameTable.Length);
        writer.Write(nameTable);
    }

    private static byte[] BuildNameTable(string familyName, string fullName)
    {
        byte[] familyBytes = Encoding.BigEndianUnicode.GetBytes(familyName);
        byte[] fullBytes = Encoding.BigEndianUnicode.GetBytes(fullName);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 30);
        WriteNameRecord(writer, SfntNameIds.FamilyName, familyBytes.Length, 0);
        WriteNameRecord(writer, SfntNameIds.FullName, fullBytes.Length, familyBytes.Length);
        writer.Write(familyBytes);
        writer.Write(fullBytes);

        return stream.ToArray();
    }

    private static byte[] BuildMetricsSfnt()
    {
        return BuildSfntWithTables(
            ("head", BuildHeadTable()),
            ("hhea", BuildHheaTable()),
            ("maxp", BuildMaxpTable()),
            ("hmtx", BuildHmtxTable()),
            ("cmap", BuildCmapFormat4Table()),
            ("loca", BuildLocaTable()),
            ("glyf", BuildGlyfTable()),
            ("OS/2", BuildOs2Table()));
    }

    private static byte[] BuildMetricsTtc(params ushort[] unitsPerEmValues)
    {
        var faceTables = unitsPerEmValues
            .Select(unitsPerEm => new (string Tag, byte[] Data)[]
            {
                ("head", BuildHeadTable(unitsPerEm)),
                ("hhea", BuildHheaTable()),
                ("maxp", BuildMaxpTable()),
                ("hmtx", BuildHmtxTable()),
                ("cmap", BuildCmapFormat4Table()),
                ("loca", BuildLocaTable()),
                ("glyf", BuildGlyfTable()),
                ("OS/2", BuildOs2Table())
            })
            .ToArray();

        uint[] faceOffsets = new uint[faceTables.Length];
        uint offset = (uint)(12 + faceTables.Length * 4);
        for (int i = 0; i < faceTables.Length; i++)
        {
            faceOffsets[i] = offset;
            offset += GetSfntLength(faceTables[i]);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteTag(writer, "ttcf");
        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, (uint)faceTables.Length);
        foreach (uint faceOffset in faceOffsets)
        {
            WriteUInt(writer, faceOffset);
        }

        for (int i = 0; i < faceTables.Length; i++)
        {
            stream.Position = faceOffsets[i];
            WriteSfntWithTables(writer, faceOffsets[i], faceTables[i]);
        }

        return stream.ToArray();
    }

    private static byte[] BuildSingleFaceTtc(byte[] sfnt)
    {
        const uint faceOffset = 16;
        if (sfnt.Length < 12)
        {
            throw new ArgumentException("SFNT data is truncated.", nameof(sfnt));
        }

        ushort tableCount = ReadUShort(sfnt, 4);
        int sourceDirectoryEnd = checked(12 + tableCount * 16);
        if (sourceDirectoryEnd > sfnt.Length)
        {
            throw new ArgumentException("SFNT table directory is truncated.", nameof(sfnt));
        }

        int targetTableOffset = Align4(checked((int)faceOffset + sourceDirectoryEnd));
        var targetOffsets = new uint[tableCount];
        for (int i = 0; i < tableCount; i++)
        {
            int sourceRecordOffset = 12 + i * 16;
            uint tableLength = ReadUInt(sfnt, sourceRecordOffset + 12);
            targetOffsets[i] = checked((uint)targetTableOffset);
            targetTableOffset = Align4(checked(targetTableOffset + checked((int)tableLength)));
        }

        using var stream = new MemoryStream(new byte[targetTableOffset], writable: true);
        using var writer = new BinaryWriter(stream);
        WriteTag(writer, "ttcf");
        WriteUInt(writer, 0x00010000);
        WriteUInt(writer, 1);
        WriteUInt(writer, faceOffset);

        stream.Position = faceOffset;
        writer.Write(sfnt, 0, 12);
        for (int i = 0; i < tableCount; i++)
        {
            int sourceRecordOffset = 12 + i * 16;
            writer.Write(sfnt, sourceRecordOffset, 8);
            WriteUInt(writer, targetOffsets[i]);
            uint tableLength = ReadUInt(sfnt, sourceRecordOffset + 12);
            WriteUInt(writer, tableLength);

            uint sourceTableOffset = ReadUInt(sfnt, sourceRecordOffset + 8);
            if (sourceTableOffset > sfnt.Length || tableLength > sfnt.Length - sourceTableOffset)
            {
                throw new ArgumentException("SFNT table is outside the source data.", nameof(sfnt));
            }

            long returnPosition = stream.Position;
            stream.Position = targetOffsets[i];
            writer.Write(sfnt, checked((int)sourceTableOffset), checked((int)tableLength));
            stream.Position = returnPosition;
        }

        return stream.ToArray();
    }

    private static byte[] BuildSfntWithTables(params (string Tag, byte[] Data)[] tables)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, (ushort)tables.Length);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);

        uint tableOffset = (uint)(12 + tables.Length * 16);
        foreach ((string tag, byte[] data) in tables)
        {
            WriteTag(writer, tag);
            WriteUInt(writer, 0);
            WriteUInt(writer, tableOffset);
            WriteUInt(writer, (uint)data.Length);
            tableOffset += (uint)data.Length;
        }

        foreach ((_, byte[] data) in tables)
        {
            writer.Write(data);
        }

        return stream.ToArray();
    }

    private static uint GetSfntLength((string Tag, byte[] Data)[] tables)
    {
        return (uint)(12 + tables.Length * 16 + tables.Sum(table => table.Data.Length));
    }

    private static void WriteSfntWithTables(BinaryWriter writer, uint faceOffset, (string Tag, byte[] Data)[] tables)
    {
        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, (ushort)tables.Length);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);

        uint tableOffset = faceOffset + (uint)(12 + tables.Length * 16);
        foreach ((string tag, byte[] data) in tables)
        {
            WriteTag(writer, tag);
            WriteUInt(writer, 0);
            WriteUInt(writer, tableOffset);
            WriteUInt(writer, (uint)data.Length);
            tableOffset += (uint)data.Length;
        }

        foreach ((_, byte[] data) in tables)
        {
            writer.Write(data);
        }
    }

    private static byte[] BuildHeadTable(ushort unitsPerEm = 1000)
    {
        byte[] table = new byte[54];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        stream.Position = 4;
        WriteUInt(writer, 0x00010000);
        stream.Position = 18;
        WriteUShort(writer, unitsPerEm);
        stream.Position = 50;
        WriteShort(writer, 0);
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
        WriteShort(writer, 50);
        stream.Position = 34;
        WriteUShort(writer, 2);
        return table;
    }

    private static byte[] BuildMaxpTable(ushort glyphCount = 2)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUInt(writer, 0x00010000);
        WriteUShort(writer, glyphCount);
        return stream.ToArray();
    }

    private static byte[] BuildSbixTable()
    {
        byte[] strike20 = BuildSbixStrike(20, -2, 6, new byte[] { 20, 21, 22 });
        byte[] strike40 = BuildSbixStrike(40, -4, 12, new byte[] { 40, 41, 42 });
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 1);
        WriteUShort(writer, 1);
        WriteUInt(writer, 2);
        WriteUInt(writer, 16);
        WriteUInt(writer, (uint)(16 + strike20.Length));
        writer.Write(strike20);
        writer.Write(strike40);
        return stream.ToArray();
    }

    private static byte[] BuildSingleSbixTable(byte[] imageData)
    {
        byte[] strike = BuildSbixStrike(20, -2, 6, imageData);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 1);
        WriteUShort(writer, 1);
        WriteUInt(writer, 1);
        WriteUInt(writer, 12);
        writer.Write(strike);
        return stream.ToArray();
    }

    private static (byte[] Cblc, byte[] Cbdt) BuildSingleCbdtTables(
        byte[] imageData,
        ushort indexFormat = 1)
    {
        bool metricsInImage = indexFormat is 1 or 3 or 4;
        using var cbdtStream = new MemoryStream();
        using var cbdtWriter = new BinaryWriter(cbdtStream);
        WriteUShort(cbdtWriter, 3);
        WriteUShort(cbdtWriter, 0);
        if (metricsInImage)
        {
            WriteSmallCbdtMetrics(cbdtWriter);
        }
        WriteUInt(cbdtWriter, (uint)imageData.Length);
        cbdtWriter.Write(imageData);
        byte[] cbdt = cbdtStream.ToArray();

        using var subtableStream = new MemoryStream();
        using var subtableWriter = new BinaryWriter(subtableStream);
        WriteUShort(subtableWriter, indexFormat);
        WriteUShort(subtableWriter, metricsInImage ? (ushort)17 : (ushort)19);
        WriteUInt(subtableWriter, 4); // image data starts after CBDT header
        uint bitmapDataLength = (uint)cbdt.Length - 4;
        switch (indexFormat)
        {
            case 1:
                WriteUInt(subtableWriter, 0);
                WriteUInt(subtableWriter, bitmapDataLength);
                break;
            case 2:
                WriteUInt(subtableWriter, bitmapDataLength);
                WriteBigCbdtMetrics(subtableWriter);
                break;
            case 3:
                WriteUShort(subtableWriter, 0);
                WriteUShort(subtableWriter, checked((ushort)bitmapDataLength));
                break;
            case 4:
                WriteUInt(subtableWriter, 1);
                WriteUShort(subtableWriter, 1);
                WriteUShort(subtableWriter, 0);
                WriteUShort(subtableWriter, ushort.MaxValue);
                WriteUShort(subtableWriter, checked((ushort)bitmapDataLength));
                break;
            case 5:
                WriteUInt(subtableWriter, bitmapDataLength);
                WriteBigCbdtMetrics(subtableWriter);
                WriteUInt(subtableWriter, 1);
                WriteUShort(subtableWriter, 1);
                WriteUShort(subtableWriter, 0); // 32-bit subtable alignment
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(indexFormat));
        }
        byte[] subtable = subtableStream.ToArray();

        using var cblcStream = new MemoryStream();
        using var cblcWriter = new BinaryWriter(cblcStream);
        WriteUShort(cblcWriter, 3);
        WriteUShort(cblcWriter, 0);
        WriteUInt(cblcWriter, 1);
        WriteUInt(cblcWriter, 56); // indexSubtableListOffset
        WriteUInt(cblcWriter, checked((uint)(8 + subtable.Length)));
        WriteUInt(cblcWriter, 1);
        WriteUInt(cblcWriter, 0);
        cblcWriter.Write(new byte[24]); // horizontal and vertical line metrics
        WriteUShort(cblcWriter, 1);
        WriteUShort(cblcWriter, 1);
        cblcWriter.Write((byte)20);
        cblcWriter.Write((byte)20);
        cblcWriter.Write((byte)32);
        cblcWriter.Write((byte)1); // horizontal metrics
        WriteUShort(cblcWriter, 1);
        WriteUShort(cblcWriter, 1);
        WriteUInt(cblcWriter, 8);
        cblcWriter.Write(subtable);
        return (cblcStream.ToArray(), cbdt);
    }

    private static void WriteSmallCbdtMetrics(BinaryWriter writer)
    {
        writer.Write((byte)1); // height
        writer.Write((byte)1); // width
        writer.Write(unchecked((byte)(sbyte)3)); // horizontal bearing X
        writer.Write(unchecked((byte)(sbyte)4)); // horizontal bearing Y
        writer.Write((byte)5); // advance
    }

    private static void WriteBigCbdtMetrics(BinaryWriter writer)
    {
        WriteSmallCbdtMetrics(writer);
        writer.Write((byte)0); // vertical bearing X
        writer.Write((byte)0); // vertical bearing Y
        writer.Write((byte)5); // vertical advance
    }

    private static byte[] BuildSingleSvgTable(string xml, bool compress)
    {
        byte[] document = Encoding.UTF8.GetBytes(xml);
        if (compress)
        {
            using var compressedStream = new MemoryStream();
            using (var gzip = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(document);
            }
            document = compressedStream.ToArray();
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteUShort(writer, 0);
        WriteUInt(writer, 10);
        WriteUInt(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 1);
        WriteUShort(writer, 1);
        WriteUInt(writer, 14);
        WriteUInt(writer, (uint)document.Length);
        writer.Write(document);
        return stream.ToArray();
    }

    private static byte[] BuildSbixStrike(
        ushort pixelsPerEm,
        short originOffsetX,
        short originOffsetY,
        byte[] imageData)
    {
        const uint dataStart = 20;
        uint duplicateStart = dataStart + 8u + (uint)imageData.Length;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, pixelsPerEm);
        WriteUShort(writer, 72);
        WriteUInt(writer, dataStart);
        WriteUInt(writer, dataStart);
        WriteUInt(writer, duplicateStart);
        WriteUInt(writer, duplicateStart + 10);

        WriteShort(writer, originOffsetX);
        WriteShort(writer, originOffsetY);
        WriteTag(writer, "png ");
        writer.Write(imageData);

        WriteShort(writer, 7);
        WriteShort(writer, 8);
        WriteTag(writer, "dupe");
        WriteUShort(writer, 1);
        return stream.ToArray();
    }

    private static (byte[] Loca, byte[] Glyf) BuildCompositeGlyphTables()
    {
        byte[] simple = BuildSimpleSquareGlyph();
        byte[] composite = BuildCompositeGlyph();
        using var locaStream = new MemoryStream();
        using var locaWriter = new BinaryWriter(locaStream);
        WriteUShort(locaWriter, 0);
        WriteUShort(locaWriter, 0);
        WriteUShort(locaWriter, (ushort)(simple.Length / 2));
        WriteUShort(locaWriter, (ushort)((simple.Length + composite.Length) / 2));

        return (locaStream.ToArray(), simple.Concat(composite).ToArray());
    }

    private static byte[] BuildSimpleSquareGlyph()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, 1);
        WriteShort(writer, 0);
        WriteShort(writer, 0);
        WriteShort(writer, 100);
        WriteShort(writer, 100);
        WriteUShort(writer, 3);
        WriteUShort(writer, 0);
        writer.Write(new byte[] { 1, 1, 1, 1 });
        foreach (short value in new short[] { 0, 100, 0, -100 })
        {
            WriteShort(writer, value);
        }
        foreach (short value in new short[] { 0, 0, 100, 0 })
        {
            WriteShort(writer, value);
        }
        return stream.ToArray();
    }

    private static byte[] BuildCompositeGlyph()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, -1);
        WriteShort(writer, 50);
        WriteShort(writer, 20);
        WriteShort(writer, 200);
        WriteShort(writer, 170);
        WriteUShort(writer, 0x000B); // word XY arguments and a uniform scale
        WriteUShort(writer, 1);
        WriteShort(writer, 50);
        WriteShort(writer, 20);
        WriteShort(writer, 0x6000); // 1.5 in F2Dot14
        return stream.ToArray();
    }

    private static byte[] BuildHmtxTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 500);
        WriteShort(writer, 0);
        WriteUShort(writer, 600);
        WriteShort(writer, -20);
        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat4Table()
    {
        byte[] format4 = BuildFormat4Subtable();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        WriteUInt(writer, 12);
        writer.Write(format4);
        return stream.ToArray();
    }

    private static byte[] BuildCmapSymbolFormat4Table(ushort codePoint)
    {
        byte[] format4 = BuildFormat4Subtable(codePoint);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 0);
        WriteUInt(writer, 12);
        writer.Write(format4);
        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat4And12Table()
    {
        byte[] format4 = BuildFormat4Subtable();
        byte[] format12 = BuildFormat12Subtable();
        uint format4Offset = 20;
        uint format12Offset = format4Offset + (uint)format4.Length;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        WriteUInt(writer, format4Offset);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, format12Offset);
        writer.Write(format4);
        writer.Write(format12);
        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat13Table()
    {
        byte[] format13 = BuildFormat13Subtable();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 1);
        WriteUShort(writer, 3);
        WriteUShort(writer, 10);
        WriteUInt(writer, 12);
        writer.Write(format13);
        return stream.ToArray();
    }

    private static byte[] BuildCmapFormat4And14Table(ushort codePoint = 0x0041, ushort variationGlyph = 1)
    {
        byte[] format4 = BuildFormat4Subtable(codePoint);
        using var format14Stream = new MemoryStream();
        using (var format14Writer = new BinaryWriter(format14Stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            WriteUShort(format14Writer, 14);
            WriteUInt(format14Writer, 30);
            WriteUInt(format14Writer, 1);
            format14Writer.Write(new byte[] { 0x00, 0xFE, 0x0F });
            WriteUInt(format14Writer, 0);
            WriteUInt(format14Writer, 21);
            WriteUInt(format14Writer, 1);
            format14Writer.Write(new byte[] { 0x00, (byte)(codePoint >> 8), (byte)codePoint });
            WriteUShort(format14Writer, variationGlyph);
        }
        byte[] format14 = format14Stream.ToArray();
        uint format4Offset = 20;
        uint format14Offset = format4Offset + (uint)format4.Length;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteUShort(writer, 0);
        WriteUShort(writer, 2);
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        WriteUInt(writer, format4Offset);
        WriteUShort(writer, 0);
        WriteUShort(writer, 5);
        WriteUInt(writer, format14Offset);
        writer.Write(format4);
        writer.Write(format14);
        return stream.ToArray();
    }

    private static byte[] BuildFormat4Subtable(ushort codePoint = 0x0041)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 4);
        WriteUShort(writer, 32);
        WriteUShort(writer, 0);
        WriteUShort(writer, 4);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        WriteUShort(writer, codePoint);
        WriteUShort(writer, 0xFFFF);
        WriteUShort(writer, 0);
        WriteUShort(writer, codePoint);
        WriteUShort(writer, 0xFFFF);
        WriteShort(writer, unchecked((short)(1 - codePoint)));
        WriteShort(writer, 1);
        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        return stream.ToArray();
    }

    private static byte[] BuildFormat12Subtable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 12);
        WriteUShort(writer, 0);
        WriteUInt(writer, 28);
        WriteUInt(writer, 0);
        WriteUInt(writer, 1);
        WriteUInt(writer, 0x1F600);
        WriteUInt(writer, 0x1F600);
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildFormat13Subtable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 13);
        WriteUShort(writer, 0);
        WriteUInt(writer, 40);
        WriteUInt(writer, 0);
        WriteUInt(writer, 2);
        WriteUInt(writer, 0x263a);
        WriteUInt(writer, 0x263a);
        WriteUInt(writer, 1);
        WriteUInt(writer, 0x1f000);
        WriteUInt(writer, 0x1ffff);
        WriteUInt(writer, 1);
        return stream.ToArray();
    }

    private static byte[] BuildLocaTable(ushort glyphCount = 2)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteUShort(writer, 0);
        WriteUShort(writer, 0);
        for (var glyph = 2; glyph <= glyphCount; glyph++)
        {
            WriteUShort(writer, 5);
        }
        return stream.ToArray();
    }

    private static byte[] BuildGlyfTable()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteShort(writer, 1);
        WriteShort(writer, -10);
        WriteShort(writer, -20);
        WriteShort(writer, 300);
        WriteShort(writer, 700);
        return stream.ToArray();
    }

    private static byte[] BuildOs2Table(ushort fontPage = 0)
    {
        byte[] table = new byte[64];
        using var stream = new MemoryStream(table);
        using var writer = new BinaryWriter(stream);

        stream.Position = 4;
        WriteUShort(writer, 400);
        WriteUShort(writer, 5);
        WriteUShort(writer, 0x0008);
        stream.Position = 62;
        WriteUShort(writer, fontPage);
        return table;
    }

    private static void WriteNameRecord(BinaryWriter writer, ushort nameId, int length, int offset)
    {
        WriteUShort(writer, 3);
        WriteUShort(writer, 1);
        WriteUShort(writer, 0x0409);
        WriteUShort(writer, nameId);
        WriteUShort(writer, (ushort)length);
        WriteUShort(writer, (ushort)offset);
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        writer.Write(Encoding.ASCII.GetBytes(tag));
    }

    private static void WriteUShort(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteShort(BinaryWriter writer, short value)
    {
        WriteUShort(writer, unchecked((ushort)value));
    }

    private static void WriteUInt(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static ushort ReadUShort(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) |
                      (data[offset + 1] << 16) |
                      (data[offset + 2] << 8) |
                       data[offset + 3]);
    }
}
