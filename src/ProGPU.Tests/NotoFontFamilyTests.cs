using System.Security.Cryptography;
using ProGPU.Fonts.Noto;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class NotoFontFamilyTests
{
    [Fact]
    public void FacesAreUnmodifiedOfficialReleaseAssets()
    {
        Assert.True(NotoFontFamily.Japanese.UsesExternalStorage);
        Assert.True(NotoFontFamily.Symbols.UsesExternalStorage);
        string japaneseHash = Convert.ToHexString(SHA256.HashData(NotoFontFamily.Japanese.FontData.Span)).ToLowerInvariant();
        string symbolsHash = Convert.ToHexString(SHA256.HashData(NotoFontFamily.Symbols.FontData.Span)).ToLowerInvariant();

        Assert.Equal("2.004", NotoFontFamily.CjkVersion);
        Assert.Equal("2.006", NotoFontFamily.SymbolsVersion);
        Assert.Equal("68a3fc98800b2a27b371f2fb79991daf3633bd89309d4ffaa6946fd587f375b5", japaneseHash);
        Assert.Equal("2af28573fcdb6c72ec195908c2f95a5d82fb65c8e289a11aa7088663f4cab99b", symbolsHash);
    }

    [Theory]
    [InlineData('プ')]
    [InlineData('描')]
    [InlineData('画')]
    [InlineData('速')]
    public void JapaneseFaceCoversSampleCjk(char character)
    {
        Assert.NotEqual((ushort)0, NotoFontFamily.Japanese.GetGlyphIndex(character));
    }

    [Theory]
    [InlineData('日')]
    [InlineData('本')]
    [InlineData('語')]
    [InlineData('描')]
    public void JapaneseCffOutlinesAreDecodedOnDemandWithoutWholeFontFallback(char character)
    {
        TtfFont font = NotoFontFamily.Japanese;
        ushort glyph = font.GetGlyphIndex(character);

        PathGeometry outline = Assert.IsType<PathGeometry>(font.GetGlyphOutline(glyph));

        Assert.NotEmpty(outline.Figures);
        Assert.Contains(outline.Figures.SelectMany(static figure => figure.Segments),
            static segment => segment is LineSegment or CubicBezierSegment);
        Assert.False(font.IsCffFallbackLoaded);
    }

    [Theory]
    [InlineData('A')]
    [InlineData('日')]
    [InlineData('か')]
    [InlineData('ナ')]
    public void JapaneseCffOnDemandOutlinesMatchEstablishedFallback(char character)
    {
        byte[] fontData = NotoFontFamily.Japanese.FontData.ToArray();
        var compactFont = new TtfFont(fontData);
        var fallbackFont = new TtfFont(fontData);
        ushort glyph = compactFont.GetGlyphIndex(character);

        PathGeometry compact = Assert.IsType<PathGeometry>(compactFont.GetGlyphOutline(glyph));
        PathGeometry fallback = Assert.IsType<PathGeometry>(
            fallbackFont.GetCffFallbackGlyphOutlineForTesting(glyph));

        AssertGeometryEqual(fallback, compact);
    }

    [Theory]
    [InlineData('A', 34, 14, 1714381338565491643UL)]
    [InlineData('日', 20220, 16, 5620540281806238275UL)]
    public void JapaneseCffOutlinesMatchNativeCanonicalSegmentCheckpoints(
        char character,
        ushort expectedGlyph,
        int expectedSegments,
        ulong expectedHash)
    {
        TtfFont font = NotoFontFamily.Japanese;
        ushort glyph = font.GetGlyphIndex(character);
        PathGeometry outline = Assert.IsType<PathGeometry>(font.GetGlyphOutline(glyph));

        Assert.Equal(expectedGlyph, glyph);
        Assert.Equal(expectedSegments, CountCanonicalSegments(outline));
        Assert.Equal(expectedHash, HashCanonicalSegments(outline));
    }

    [Theory]
    [InlineData('♠')]
    [InlineData('♦')]
    [InlineData('♣')]
    [InlineData('★')]
    public void SymbolsFaceCoversSampleSymbols(char character)
    {
        Assert.NotEqual((ushort)0, NotoFontFamily.Symbols.GetGlyphIndex(character));
    }

    [Fact]
    public void RegisteredFallbacksMatchByScriptAndCharacter()
    {
        var manager = new FontManager();
        NotoFontFamily.RegisterFallbacks(manager);

        Assert.True(manager.TryMatchCharacter(
            null,
            FontStyleRequest.Normal,
            ["ja-JP"],
            '描',
            out TtfFont? japanese,
            out ushort japaneseGlyph));
        Assert.NotNull(japanese);
        Assert.NotEqual((ushort)0, japanese!.GetGlyphIndex('描'));
        Assert.NotEqual((ushort)0, japaneseGlyph);

        Assert.True(manager.TryMatchCharacter(
            null,
            FontStyleRequest.Normal,
            null,
            '♠',
            out TtfFont? symbols,
            out ushort symbolGlyph));
        Assert.Same(NotoFontFamily.Symbols, symbols);
        Assert.NotEqual((ushort)0, symbolGlyph);
    }

    private static void AssertGeometryEqual(PathGeometry expected, PathGeometry actual)
    {
        Assert.Equal(expected.FillRule, actual.FillRule);
        Assert.Equal(expected.Figures.Count, actual.Figures.Count);
        for (int figureIndex = 0; figureIndex < expected.Figures.Count; figureIndex++)
        {
            PathFigure expectedFigure = expected.Figures[figureIndex];
            PathFigure actualFigure = actual.Figures[figureIndex];
            AssertPointEqual(expectedFigure.StartPoint, actualFigure.StartPoint);
            Assert.Equal(expectedFigure.IsClosed, actualFigure.IsClosed);
            Assert.Equal(expectedFigure.Segments.Count, actualFigure.Segments.Count);
            for (int segmentIndex = 0; segmentIndex < expectedFigure.Segments.Count; segmentIndex++)
            {
                PathSegment expectedSegment = expectedFigure.Segments[segmentIndex];
                PathSegment actualSegment = actualFigure.Segments[segmentIndex];
                Assert.Equal(expectedSegment.GetType(), actualSegment.GetType());
                switch (expectedSegment, actualSegment)
                {
                    case (LineSegment expectedLine, LineSegment actualLine):
                        AssertPointEqual(expectedLine.Point, actualLine.Point);
                        break;
                    case (QuadraticBezierSegment expectedQuadratic, QuadraticBezierSegment actualQuadratic):
                        AssertPointEqual(expectedQuadratic.ControlPoint, actualQuadratic.ControlPoint);
                        AssertPointEqual(expectedQuadratic.Point, actualQuadratic.Point);
                        break;
                    case (CubicBezierSegment expectedCubic, CubicBezierSegment actualCubic):
                        AssertPointEqual(expectedCubic.ControlPoint1, actualCubic.ControlPoint1);
                        AssertPointEqual(expectedCubic.ControlPoint2, actualCubic.ControlPoint2);
                        AssertPointEqual(expectedCubic.Point, actualCubic.Point);
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException(
                            $"Unexpected CFF segment type {expectedSegment.GetType().Name}.");
                }
            }
        }
    }

    private static void AssertPointEqual(System.Numerics.Vector2 expected, System.Numerics.Vector2 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 0.001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 0.001f);
    }

    private static int CountCanonicalSegments(PathGeometry geometry)
    {
        int count = 0;
        foreach (PathFigure figure in geometry.Figures)
        {
            count += figure.Segments.Count;
            System.Numerics.Vector2 current = figure.StartPoint;
            foreach (PathSegment segment in figure.Segments)
            {
                current = segment switch
                {
                    LineSegment line => line.Point,
                    CubicBezierSegment cubic => cubic.Point,
                    _ => current
                };
            }
            if (figure.IsClosed && current != figure.StartPoint)
            {
                count++;
            }
        }
        return count;
    }

    private static ulong HashCanonicalSegments(PathGeometry geometry)
    {
        const ulong offset = 1469598103934665603UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
        void Append(uint value) => hash = (hash ^ value) * prime;
        void AppendPoint(System.Numerics.Vector2 point)
        {
            Append(Bits(point.X));
            Append(Bits(point.Y));
        }

        foreach (PathFigure figure in geometry.Figures)
        {
            System.Numerics.Vector2 current = figure.StartPoint;
            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        Append(0U);
                        AppendPoint(current);
                        AppendPoint(line.Point);
                        AppendPoint(default);
                        AppendPoint(default);
                        current = line.Point;
                        break;
                    case CubicBezierSegment cubic:
                        Append(2U);
                        AppendPoint(current);
                        AppendPoint(cubic.ControlPoint1);
                        AppendPoint(cubic.ControlPoint2);
                        AppendPoint(cubic.Point);
                        current = cubic.Point;
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException(
                            $"Unexpected canonical CFF segment {segment.GetType().Name}.");
                }
            }
            if (figure.IsClosed && current != figure.StartPoint)
            {
                Append(0U);
                AppendPoint(current);
                AppendPoint(figure.StartPoint);
                AppendPoint(default);
                AppendPoint(default);
            }
        }
        return hash;
    }
}
