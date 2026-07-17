using ProGPU.Fonts.Inter;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public sealed class OpenTypeTextShaperTests
{
    [Fact]
    public void InterExposesDocumentedOpenTypeFeatures()
    {
        IReadOnlyList<string> tags = InterFontFamily.Regular.GetOpenTypeFeatureTags();

        foreach (string tag in new[]
                 {
                     "aalt", "calt", "case", "ccmp", "cpsp", "dlig", "dnom", "frac", "kern", "locl",
                     "mark", "mkmk", "numr", "ordn", "pnum", "salt", "sinf", "subs", "sups", "tnum", "zero",
                     "ss01", "ss02", "ss03", "ss04", "ss05", "ss06", "ss07", "ss08",
                     "cv01", "cv02", "cv03", "cv04", "cv05", "cv06", "cv07", "cv08",
                     "cv09", "cv10", "cv11", "cv12", "cv13", "cv14"
                 })
        {
            Assert.Contains(tag, tags);
        }
    }

    [Theory]
    [InlineData("calt", "-> --> ---> => ==> <->")]
    [InlineData("dlig", "ff ffi fft ft fi tt tf df dt")]
    [InlineData("case", "abc (def) [ghi] {ui}")]
    [InlineData("frac", "1/2 12/25")]
    [InlineData("numr", "0123456789")]
    [InlineData("dnom", "0123456789")]
    [InlineData("sups", "X0123456789")]
    [InlineData("subs", "H0123456789")]
    [InlineData("tnum", "0123456789")]
    [InlineData("zero", "O0")]
    [InlineData("kern", "AVATAR To Wa Yo")]
    [InlineData("cpsp", "INTER")]
    [InlineData("ordn", "1a 2o 3a 4o")]
    [InlineData("salt", "1 3 4 6 9 I l a G t")]
    [InlineData("aalt", "1 3 4 6 9 I l a G t")]
    [InlineData("pnum", "0123456789")]
    [InlineData("sinf", "H2O SF6 H2SO4")]
    [InlineData("ss01", "1234567890")]
    [InlineData("ss02", "WP0ACO9XSI1lO0")]
    [InlineData("ss03", "“Inter,” ‘interface,’")]
    [InlineData("ss04", "I1l O0 S5 G6 B8")]
    [InlineData("ss05", "0123456789 ABCDEFG")]
    [InlineData("ss06", "0123456789 ABCDEFG")]
    [InlineData("ss07", ".,:;!? ¿¡")]
    [InlineData("ss08", "“Inter” ‘UI’")]
    [InlineData("cv01", "1")]
    [InlineData("cv02", "4")]
    [InlineData("cv03", "6")]
    [InlineData("cv04", "9")]
    [InlineData("cv05", "l ł ƚ ɫ")]
    [InlineData("cv06", "u")]
    [InlineData("cv07", "ß")]
    [InlineData("cv08", "I")]
    [InlineData("cv09", "3")]
    [InlineData("cv10", "G")]
    [InlineData("cv11", "a")]
    [InlineData("cv12", "f")]
    [InlineData("cv13", "t")]
    [InlineData("cv14", "ẞ")]
    public void DocumentedInterFeatureChangesRetainedGlyphsOrPositioning(string tag, string sample)
    {
        IReadOnlyList<ShapedGlyph> disabled = OpenTypeTextShaper.Shape(
            sample,
            InterFontFamily.Regular,
            32f,
            FeatureOptions(tag, enabled: false));
        IReadOnlyList<ShapedGlyph> enabled = OpenTypeTextShaper.Shape(
            sample,
            InterFontFamily.Regular,
            32f,
            FeatureOptions(tag, enabled: true));

        Assert.NotEqual(Signature(disabled), Signature(enabled));
    }

    [Fact]
    public void InterCompositionAndMarkAttachmentAreAppliedDuringLayoutShaping()
    {
        AssertFeatureChanges("ccmp", "A\u030A", new OpenTypeFeatureSetting("calt", 0));
        AssertFeatureChanges("mark", "a\u0308", new OpenTypeFeatureSetting("ccmp", 0));
        AssertFeatureChanges("mkmk", "a\u0308\u0301", new OpenTypeFeatureSetting("ccmp", 0));
    }

    [Fact]
    public void InterLocalizedRomanianFormsHonorLanguageSelection()
    {
        const string sample = "ş ţ Ş Ţ";
        IReadOnlyList<ShapedGlyph> disabled = OpenTypeTextShaper.Shape(sample, InterFontFamily.Regular, 32f, new TextShapingOptions
        {
            Script = "latn",
            Language = "ro",
            Features = [new OpenTypeFeatureSetting("locl", 0)]
        });
        IReadOnlyList<ShapedGlyph> enabled = OpenTypeTextShaper.Shape(sample, InterFontFamily.Regular, 32f, new TextShapingOptions
        {
            Script = "latn",
            Language = "ro",
            Features = [new OpenTypeFeatureSetting("locl")]
        });

        Assert.NotEqual(Signature(disabled), Signature(enabled));
    }

    [Fact]
    public void InterOptionalFeaturesProduceRetainedGlyphSubstitutionsAndPositioning()
    {
        TtfFont font = InterFontFamily.Regular;
        IReadOnlyList<ShapedGlyph> normalZero = OpenTypeTextShaper.Shape("0", font, 32f, Features());
        IReadOnlyList<ShapedGlyph> slashedZero = OpenTypeTextShaper.Shape("0", font, 32f, Features("zero"));
        Assert.NotEqual(normalZero[0].GlyphIndex, slashedZero[0].GlyphIndex);

        IReadOnlyList<ShapedGlyph> proportional = OpenTypeTextShaper.Shape("0123456789", font, 32f, Features());
        IReadOnlyList<ShapedGlyph> tabular = OpenTypeTextShaper.Shape("0123456789", font, 32f, Features("tnum"));
        Assert.True(proportional.Select(glyph => glyph.AdvanceX).Distinct().Count() > 1);
        Assert.Single(tabular.Select(glyph => glyph.AdvanceX).Distinct());

        IReadOnlyList<ShapedGlyph> ordinaryFraction = OpenTypeTextShaper.Shape("1/2", font, 32f, Features());
        IReadOnlyList<ShapedGlyph> fraction = OpenTypeTextShaper.Shape("1/2", font, 32f, Features("frac"));
        Assert.NotEqual(
            ordinaryFraction.Select(glyph => glyph.GlyphIndex),
            fraction.Select(glyph => glyph.GlyphIndex));
    }

    [Fact]
    public void AlternateFeatureIntegerSelectsDifferentAlternateGlyphs()
    {
        TtfFont font = InterFontFamily.Regular;
        IReadOnlyList<ShapedGlyph> first = OpenTypeTextShaper.Shape(
            "1",
            font,
            32f,
            TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting("aalt", 1)));
        IReadOnlyList<ShapedGlyph> second = OpenTypeTextShaper.Shape(
            "1",
            font,
            32f,
            TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting("aalt", 2)));

        Assert.NotEqual(first[0].GlyphIndex, second[0].GlyphIndex);
    }

    [Fact]
    public void VariableKerningMatchesHarfBuzzReferenceAtMidAxisInstance()
    {
        TtfFont font = InterFontFamily.GetVariableFont(537, 23);
        IReadOnlyList<ShapedGlyph> glyphs = OpenTypeTextShaper.Shape("AVATAR", font, font.UnitsPerEm);

        Assert.Equal(new ushort[] { 2, 456, 2, 411, 2, 384 }, glyphs.Select(static glyph => glyph.GlyphIndex));
        Assert.Equal(new[] { 1250f, 1246f, 1241f, 1119f, 1430f, 1321f }, glyphs.Select(static glyph => glyph.AdvanceX));
    }

    [Fact]
    public void VariableMarkAnchorsMatchHarfBuzzReferenceAtMidAxisInstance()
    {
        TtfFont font = InterFontFamily.GetVariableFont(537, 23);
        IReadOnlyList<ShapedGlyph> glyphs = OpenTypeTextShaper.Shape(
            "q\u0308\u0301",
            font,
            font.UnitsPerEm,
            TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting("ccmp", 0)));

        Assert.Equal(new ushort[] { 849, 2704, 1770 }, glyphs.Select(static glyph => glyph.GlyphIndex));
        Assert.Equal(new[] { 1229f, 0f, 0f }, glyphs.Select(static glyph => glyph.AdvanceX));
        Assert.Equal(0f, glyphs[0].OffsetX);
        Assert.InRange(glyphs[1].OffsetX, -1215f, -1213f);
        Assert.InRange(glyphs[2].OffsetX, -835f, -833f);
        Assert.InRange(glyphs[2].OffsetY, -434f, -432f);
    }

    [Theory]
    [InlineData("a\u0308\u0301")]
    [InlineData("\U0001F636\u200D\U0001F32B\uFE0F")]
    [InlineData("\U0001F1FA\U0001F1FC")]
    public void DefaultClustersFollowExtendedGraphemeBoundaries(string text)
    {
        IReadOnlyList<ShapedGlyph> glyphs = OpenTypeTextShaper.Shape(
            text,
            InterFontFamily.Regular,
            InterFontFamily.Regular.UnitsPerEm,
            TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting("ccmp", 0)));

        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, static glyph => Assert.Equal(0, glyph.Cluster));
    }

    private static TextShapingOptions Features(params string[] optional)
    {
        var features = new List<OpenTypeFeatureSetting>
        {
            new("ccmp"), new("locl"), new("rlig"), new("liga"), new("clig"),
            new("calt"), new("kern"), new("mark"), new("mkmk")
        };
        features.AddRange(optional.Select(tag => new OpenTypeFeatureSetting(tag)));
        return new TextShapingOptions { Features = features };
    }

    private static string Signature(IReadOnlyList<ShapedGlyph> glyphs) =>
        string.Join('|', glyphs.Select(glyph =>
            $"{glyph.GlyphIndex}:{glyph.AdvanceX:R}:{glyph.OffsetX:R}:{glyph.OffsetY:R}"));

    private static void AssertFeatureChanges(
        string tag,
        string sample,
        params OpenTypeFeatureSetting[] common)
    {
        IReadOnlyList<OpenTypeFeatureSetting> disabledFeatures = [.. common, new OpenTypeFeatureSetting(tag, 0)];
        IReadOnlyList<OpenTypeFeatureSetting> enabledFeatures = [.. common, new OpenTypeFeatureSetting(tag)];
        IReadOnlyList<ShapedGlyph> disabled = OpenTypeTextShaper.Shape(
            sample,
            InterFontFamily.Regular,
            32f,
            new TextShapingOptions { Features = disabledFeatures });
        IReadOnlyList<ShapedGlyph> enabled = OpenTypeTextShaper.Shape(
            sample,
            InterFontFamily.Regular,
            32f,
            new TextShapingOptions { Features = enabledFeatures });

        Assert.NotEqual(Signature(disabled), Signature(enabled));
    }

    private static TextShapingOptions FeatureOptions(string tag, bool enabled) =>
        tag switch
        {
            "case" => TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting("calt", 0),
                new OpenTypeFeatureSetting(tag, enabled ? 1 : 0)),
            "pnum" => TextShapingOptions.WithFeatures(
                new OpenTypeFeatureSetting("tnum", enabled ? 0 : 1),
                new OpenTypeFeatureSetting(tag, enabled ? 1 : 0)),
            _ => TextShapingOptions.WithFeatures(new OpenTypeFeatureSetting(tag, enabled ? 1 : 0))
        };
}
