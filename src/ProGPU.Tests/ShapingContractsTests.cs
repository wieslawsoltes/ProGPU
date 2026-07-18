using System.Runtime.InteropServices;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class ShapingContractsTests
{
    [Fact]
    public void OpenTypeTagRoundTripsFontByteOrder()
    {
        var tag = new OpenTypeTag("kern");

        Assert.Equal(0x6b65726eu, tag.Value);
        Assert.Equal("kern", tag.ToString());
        Assert.True(OpenTypeTag.TryParse("mark", out OpenTypeTag parsed));
        Assert.Equal(new OpenTypeTag("mark"), parsed);
        Assert.False(OpenTypeTag.TryParse("bad", out _));
    }

    [Fact]
    public void FeatureRangesAreHalfOpen()
    {
        var feature = new ShapingFeature(new OpenTypeTag("liga"), value: 1, start: 3, end: 5);

        Assert.False(feature.AppliesTo(2));
        Assert.True(feature.AppliesTo(3));
        Assert.True(feature.AppliesTo(4));
        Assert.False(feature.AppliesTo(5));
    }

    [Fact]
    public void GlyphRecordHasStableShaderInterchangeLayout()
    {
        Assert.Equal(32, Marshal.SizeOf<ShapingGlyph>());
        Assert.Equal(0, Marshal.OffsetOf<ShapingGlyph>(nameof(ShapingGlyph.GlyphId)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<ShapingGlyph>(nameof(ShapingGlyph.AdvanceX)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<ShapingGlyph>(nameof(ShapingGlyph.OffsetY)).ToInt32());
    }

    [Fact]
    public void BufferReplaceSupportsExpansionAndContraction()
    {
        using var buffer = new ShapingBuffer(initialCapacity: 2, maximumGlyphCount: 16);
        buffer.Append(Glyph(1));
        buffer.Append(Glyph(2));
        buffer.Append(Glyph(3));

        buffer.Replace(1, 1, [Glyph(20), Glyph(21), Glyph(22)]);
        Assert.Equal(new uint[] { 1, 20, 21, 22, 3 }, buffer.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));

        buffer.Replace(1, 3, [Glyph(9)]);
        Assert.Equal(new uint[] { 1, 9, 3 }, buffer.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
    }

    [Fact]
    public void BufferEnforcesConfiguredExpansionLimit()
    {
        using var buffer = new ShapingBuffer(initialCapacity: 1, maximumGlyphCount: 2);
        buffer.Append(Glyph(1));
        buffer.Append(Glyph(2));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => buffer.Append(Glyph(3)));
        Assert.Contains("glyph limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutorRequestsRequireResolvedDirection()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShapingRequest(ShapingDirection.Unspecified, OpenTypeTag.DefaultScript));

        var request = new ShapingRequest(
            ShapingDirection.RightToLeft,
            new OpenTypeTag("arab"),
            language: "ar",
            flags: ShapingBufferFlags.BeginningOfText | ShapingBufferFlags.EndOfText);

        Assert.Equal(ShapingDirection.RightToLeft, request.Direction);
        Assert.Equal("arab", request.Script.ToString());
        Assert.Equal("ar", request.Language);
    }

    [Fact]
    public void ExistingFontAdapterExposesTablesMetricsAndNominalGlyphs()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);

        uint glyph = face.GetNominalGlyph('A');
        Assert.NotEqual(0u, glyph);
        Assert.Equal(InterFontFamily.Regular.UnitsPerEm, face.UnitsPerEm);
        Assert.Equal(InterFontFamily.Regular.NumGlyphs, face.GlyphCount);
        Assert.True(face.GetHorizontalAdvance(glyph) > 0);
        Assert.True(face.GetVerticalAdvance(glyph) > 0);
        Assert.True(face.GetHorizontalOrigin(glyph) > 0);
        Assert.True(face.TryGetTable(new OpenTypeTag("GSUB"), out ReadOnlyMemory<byte> table));
        Assert.False(table.IsEmpty);
        Assert.False(face.TryGetVariationGlyph('A', 0xfe0f, out _));
    }

    [Fact]
    public void CpuExecutorProducesTheRendererResultInDesignUnits()
    {
        TtfFont font = InterFontFamily.Regular;
        const string text = "office";
        var request = new ShapingRequest(
            ShapingDirection.LeftToRight,
            new OpenTypeTag("latn"),
            language: "en");
        using var buffer = new ShapingBuffer();

        CpuOpenTypeShaper.Instance.Shape(text, new TtfShapingFontFace(font), request, buffer);
        IReadOnlyList<ShapedGlyph> renderer = OpenTypeTextShaper.Shape(text, font, font.UnitsPerEm,
            new TextShapingOptions
            {
                Script = "latn",
                Language = "en",
                Direction = ShapingDirection.LeftToRight
            });

        Assert.Equal(renderer.Count, buffer.Count);
        for (var index = 0; index < renderer.Count; index++)
        {
            ShapedGlyph expected = renderer[index];
            ShapingGlyph actual = buffer[index];
            Assert.Equal(expected.GlyphIndex, actual.GlyphId);
            Assert.Equal(expected.CodePoint, actual.CodePoint);
            Assert.Equal(expected.Cluster, actual.Cluster);
            Assert.Equal(expected.AdvanceX, actual.AdvanceX);
            Assert.Equal(expected.AdvanceY, actual.AdvanceY);
            Assert.Equal(expected.OffsetX, actual.OffsetX);
            Assert.Equal(expected.OffsetY, actual.OffsetY);
        }
    }

    private static ShapingGlyph Glyph(uint glyphId) => new() { GlyphId = glyphId };
}
