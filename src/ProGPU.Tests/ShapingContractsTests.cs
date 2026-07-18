using System.Runtime.InteropServices;
using ProGPU.Fonts.Inter;
using ProGPU.Compute;
using ProGPU.Backend;
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

    [Fact]
    public void CpuExecutorAppliesFeaturesToHalfOpenInputRanges()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        using var ranged = new ShapingBuffer();
        using var enabled = new ShapingBuffer();
        using var disabled = new ShapingBuffer();
        const string text = "33";

        CpuOpenTypeShaper.Instance.Shape(text, face,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                features: new[] { new ShapingFeature(new OpenTypeTag("ss01"), 1, 0, 1) }),
            ranged);
        CpuOpenTypeShaper.Instance.Shape(text, face,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn")),
            enabled);
        CpuOpenTypeShaper.Instance.Shape(text, face,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                features: new[] { new ShapingFeature(new OpenTypeTag("ss01"), 1) }),
            disabled);

        Assert.Equal(2, ranged.Count);
        Assert.NotEqual(enabled[0].GlyphId, ranged[0].GlyphId);
        Assert.Equal(enabled[1].GlyphId, ranged[1].GlyphId);
        Assert.Equal(disabled[0].GlyphId, ranged[0].GlyphId);
        Assert.NotEqual(disabled[1].GlyphId, ranged[1].GlyphId);
    }

    [Fact]
    public void CpuExecutorHonorsDefaultIgnorableBufferPolicy()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        using var normal = new ShapingBuffer();
        using var preserved = new ShapingBuffer();
        using var removed = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape("\u200d", face,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn")), normal);
        CpuOpenTypeShaper.Instance.Shape("\u200d", face,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                flags: ShapingBufferFlags.PreserveDefaultIgnorables), preserved);
        CpuOpenTypeShaper.Instance.Shape("\u200d", face,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                flags: ShapingBufferFlags.RemoveDefaultIgnorables), removed);

        Assert.Equal(1, normal.Count);
        Assert.Equal(1, preserved.Count);
        Assert.Equal(0, removed.Count);
        Assert.NotEqual(normal[0].GlyphId, preserved[0].GlyphId);
    }

    [Fact]
    public void CpuExecutorHonorsCharacterClusterLevels()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        using var graphemes = new ShapingBuffer();
        using var characters = new ShapingBuffer();
        const string text = "x\u030a";
        CpuOpenTypeShaper.Instance.Shape(text, face,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                clusterLevel: ShapingClusterLevel.Graphemes), graphemes);
        CpuOpenTypeShaper.Instance.Shape(text, face,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                clusterLevel: ShapingClusterLevel.Characters), characters);

        Assert.Equal(2, graphemes.Count);
        Assert.Equal(2, characters.Count);
        Assert.Equal(0, graphemes[0].Cluster);
        Assert.Equal(0, graphemes[1].Cluster);
        Assert.Equal(0, characters[0].Cluster);
        Assert.Equal(1, characters[1].Cluster);
    }

    [Fact]
    public void GpuPlanPreservesNominalMappingAndDesignMetrics()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);

        Assert.Equal(16, Marshal.SizeOf<GpuCmapRange>());
        Assert.Equal(16, Marshal.SizeOf<GpuGlyphMetrics>());
        Assert.Equal(20, Marshal.SizeOf<GpuGlyphExtents>());
        Assert.Equal(16, Marshal.SizeOf<GpuShapingScalar>());
        Assert.Equal(32, Marshal.SizeOf<GpuOpenTypeTableDirectory>());
        Assert.Equal(40, Marshal.SizeOf<GpuOpenTypeLookupCommand>());
        foreach (uint codePoint in new uint[] { 'A', 'z', 0x00e9, 0x03a9, 0x20ac, 0x1f642 })
            Assert.Equal(face.GetNominalGlyph(codePoint), plan.GetNominalGlyph(codePoint));
        uint glyph = face.GetNominalGlyph('A');
        GpuGlyphMetrics metric = plan.Metrics.Span[checked((int)glyph)];
        Assert.Equal(face.GetHorizontalAdvance(glyph), metric.AdvanceX);
        Assert.Equal(face.GetVerticalAdvance(glyph), metric.AdvanceY);
        Assert.Equal(face.GetVerticalOrigin(glyph), metric.OriginY);
        Assert.True(face.TryGetGlyphExtents(glyph, out ShapingGlyphExtents expectedExtents));
        GpuGlyphExtents actualExtents = plan.Extents.Span[checked((int)glyph)];
        Assert.Equal(1u, actualExtents.IsValid);
        Assert.Equal(expectedExtents.XBearing, actualExtents.XBearing);
        Assert.Equal(expectedExtents.YBearing, actualExtents.YBearing);
        Assert.Equal(expectedExtents.Width, actualExtents.Width);
        Assert.Equal(expectedExtents.Height, actualExtents.Height);
        Assert.True(face.TryGetTable(new OpenTypeTag("GSUB"), out ReadOnlyMemory<byte> gsub));
        Assert.Equal((uint)gsub.Length, plan.Tables.GsubLength);
        Assert.Equal(gsub.Span, plan.TableData.Span.Slice(
            checked((int)plan.Tables.GsubOffset), checked((int)plan.Tables.GsubLength)));
    }

    [Fact]
    public void GpuLookupPlanSelectsFirstMatchingFeatureVariation()
    {
        GpuOpenTypeShapingPlan regular = GpuOpenTypeShapingPlanCompiler.Compile(
            new FeatureVariationFontFace(0));
        GpuOpenTypeShapingPlan varied = GpuOpenTypeShapingPlanCompiler.Compile(
            new FeatureVariationFontFace(0x2000));
        var request = new ShapingRequest(ShapingDirection.LeftToRight, OpenTypeTag.DefaultScript);

        GpuOpenTypeLookupCommand regularLiga = Assert.Single(
            GpuOpenTypeLookupPlanCompiler.Compile(regular, request),
            command => command.FeatureTag == new OpenTypeTag("liga").Value);
        GpuOpenTypeLookupCommand variedLiga = Assert.Single(
            GpuOpenTypeLookupPlanCompiler.Compile(varied, request),
            command => command.FeatureTag == new OpenTypeTag("liga").Value);

        Assert.NotEqual(regularLiga.LookupOffset, variedLiga.LookupOffset);
        Assert.Equal(0, regular.NormalizedVariationCoordinates.Span[0]);
        Assert.Equal(0x2000, varied.NormalizedVariationCoordinates.Span[0]);
    }

    [Fact]
    public void GpuLookupPlanSelectsNewestSupportedIndicScriptGeneration()
    {
        GpuOpenTypeShapingPlan thirdGeneration = GpuOpenTypeShapingPlanCompiler.Compile(
            new LayoutScriptFontFace("dev3"));
        GpuOpenTypeShapingPlan secondGeneration = GpuOpenTypeShapingPlanCompiler.Compile(
            new LayoutScriptFontFace("dev2"));
        var deva = new OpenTypeTag("deva");

        Assert.Equal(new OpenTypeTag("dev3"),
            GpuOpenTypeLookupPlanCompiler.ResolveLayoutScript(thirdGeneration, deva));
        Assert.Equal(new OpenTypeTag("dev2"),
            GpuOpenTypeLookupPlanCompiler.ResolveLayoutScript(secondGeneration, deva));
        Assert.Equal(deva, GpuOpenTypeLookupPlanCompiler.ResolveLayoutScript(
            GpuOpenTypeShapingPlanCompiler.Compile(new LayoutScriptFontFace("latn")), deva));
    }

    [Fact]
    public void GpuInitializationMatchesFontFace()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.InitializeRun(
            [new GpuShapingScalar('A', 0), new GpuShapingScalar('z', 1)],
            fontData,
            ShapingDirection.LeftToRight,
            output);

        Assert.Equal(2, output.Count);
        Assert.Equal(face.GetNominalGlyph('A'), output[0].GlyphId);
        Assert.Equal(face.GetHorizontalAdvance(output[0].GlyphId), output[0].AdvanceX);
        Assert.Equal(face.GetNominalGlyph('z'), output[1].GlyphId);
        Assert.Equal(face.GetHorizontalAdvance(output[1].GlyphId), output[1].AdvanceX);
    }

    [Fact]
    public void GpuUnicodePlanPreservesAuthoritativeComplexScriptProperties()
    {
        ReadOnlySpan<GpuUnicodePropertyRange> ranges = GpuUnicodeShapingPlan.Ranges.Span;
        ReadOnlySpan<GpuUnicodeDirectionalMapping> directional =
            GpuUnicodeShapingPlan.DirectionalMappings.Span;

        Assert.False(ranges.IsEmpty);
        Assert.Equal(0u, ranges[0].Start);
        Assert.Equal(0x110000u, ranges[^1].End);
        Assert.True(ranges.Length < 20_000, $"Expected compressed Unicode ranges, actual {ranges.Length}.");
        Assert.InRange(directional.Length, 100, 1_000);
        Assert.Equal(3, UnicodeShapingProperties.GetArabicJoiningType(0x0628));
        Assert.Equal(6, UnicodeShapingProperties.GetArabicJoiningType(0x064e));
        Assert.Equal(30, UnicodeShapingProperties.GetCanonicalCombiningClass(0x064e));
        Assert.True(UnicodeShapingProperties.IsMark(0x064e));
        Assert.NotEqual(0, UnicodeShapingProperties.GetIndicProperties(0x0915));
        Assert.NotEqual(0, UnicodeShapingProperties.GetUseCategory(0x0915));
        Assert.Equal((uint)')', UnicodeShapingProperties.GetMirroredCodePoint('('));
        Assert.Equal(0xfe11u, UnicodeShapingProperties.GetVerticalCodePoint(0x3001));

        ReadOnlySpan<uint> packed = GpuUnicodeShapingPlan.PackedData.Span;
        Assert.Equal(4u, packed[11]);
        uint directory = packed[12];
        Assert.InRange(directory, 16u, checked((uint)packed.Length - 28u));
        for (var machine = 0; machine < 4; machine++)
        {
            int descriptor = checked((int)directory + machine * 7);
            Assert.Equal((uint)machine, packed[descriptor]);
            Assert.Equal(
                (uint)UnicodeShapingProperties.GetSyllableMachineStartState(
                    (UnicodeShapingProperties.SyllableMachine)machine),
                packed[descriptor + 1]);
            Assert.Equal(
                (uint)UnicodeShapingProperties.GetSyllableMachineStateCount(
                    (UnicodeShapingProperties.SyllableMachine)machine),
                packed[descriptor + 2]);
        }

        for (var index = 1; index < ranges.Length; index++)
            Assert.Equal(ranges[index - 1].End, ranges[index].Start);
    }

    [Theory]
    [InlineData("deva", 0x0915u, 0x094du, 0x0937u)]
    [InlineData("dev3", 0x0915u, 0x094du, 0x0937u)]
    public void GpuRunsAuthoritativeComplexScriptSyllableMachines(
        string script,
        uint first,
        uint second,
        uint third)
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [
                new GpuShapingScalar(first, 0),
                new GpuShapingScalar(second, 0),
                new GpuShapingScalar(third, 0)
            ],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag(script)),
            [],
            output);

        Assert.Equal(new[] { first, second, third },
            output.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
    }

    [Fact]
    public void GpuMyanmarStageReordersPrebaseVowel()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x1000, 0), new GpuShapingScalar(0x1031, 1)],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("mymr")),
            [],
            output);

        Assert.Equal(new uint[] { 0x1031, 0x1000 },
            output.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.All(output.Glyphs.ToArray(), static glyph => Assert.Equal(0, glyph.Cluster));
    }

    [Fact]
    public void GpuKhmerPreprocessingReordersCoengRaAndBrokenClusters()
    {
        var face = new KhmerShapingFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var consonant = new ShapingBuffer();
        using var broken = new ShapingBuffer();
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("khmr"));

        pipeline.ExecuteRun(
            [
                new GpuShapingScalar(0x1780, 0),
                new GpuShapingScalar(0x17d2, 1),
                new GpuShapingScalar(0x179a, 2)
            ],
            fontData, request, [], consonant);
        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x17b6, 0)],
            fontData, request, [], broken);

        Assert.Equal(new uint[] { 0x17d2, 0x179a, 0x1780 },
            consonant.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.Equal(new uint[] { 0x25cc, 0x17b6 },
            broken.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
    }

    [Fact]
    public void GpuPreprocessingAppliesArabicJoiningForms()
    {
        var face = new ArabicJoiningFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(
            ShapingDirection.RightToLeft,
            new OpenTypeTag("arab"),
            language: "ar");
        uint table = plan.Tables.GsubOffset;
        GpuOpenTypeLookupCommand[] commands =
        [
            new(1, table + 4, 1, 0, new OpenTypeTag("init").Value, 1),
            new(1, table + 26, 1, 0, new OpenTypeTag("medi").Value, 1),
            new(1, table + 48, 1, 0, new OpenTypeTag("fina").Value, 1),
            new(1, table + 70, 1, 0, new OpenTypeTag("isol").Value, 1)
        ];
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var joined = new ShapingBuffer();
        using var transparent = new ShapingBuffer();

        pipeline.ExecuteRun(
            [
                new GpuShapingScalar(0x0628, 0),
                new GpuShapingScalar(0x0628, 1),
                new GpuShapingScalar(0x0628, 2)
            ],
            fontData, request, commands, joined);
        pipeline.ExecuteRun(
            [
                new GpuShapingScalar(0x0628, 0),
                new GpuShapingScalar(0x064e, 0),
                new GpuShapingScalar(0x0628, 1)
            ],
            fontData, request, commands, transparent);

        Assert.Equal(new uint[] { 12, 11, 10 }, joined.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
        Assert.Equal(new uint[] { 12, 2, 10 }, transparent.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
    }

    [Fact]
    public void GpuPreprocessingMatchesDirectionalFallbackAndCombiningOrder()
    {
        const string mirroredText = "(A)";
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(
            ShapingDirection.RightToLeft,
            new OpenTypeTag("latn"),
            language: "en");
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(mirroredText, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var mirrored = new ShapingBuffer();
        using var reordered = new ShapingBuffer();

        pipeline.ExecuteRun(
            mirroredText.Select((character, index) => new GpuShapingScalar(character, index)).ToArray(),
            fontData, request, commands, mirrored);
        pipeline.ExecuteRun(
            [
                new GpuShapingScalar('q', 0),
                new GpuShapingScalar(0x0315, 0),
                new GpuShapingScalar(0x0300, 0)
            ],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn")),
            [],
            reordered);

        Assert.Equal(expected.Glyphs.ToArray(), mirrored.Glyphs.ToArray());
        Assert.Equal(new uint[] { 'q', 0x0300, 0x0315 },
            reordered.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
    }

    [Fact]
    public void GpuNormalizationMatchesCpuCompositionAndMissingGlyphDecomposition()
    {
        var interFace = new TtfShapingFontFace(InterFontFamily.Regular);
        var request = new ShapingRequest(
            ShapingDirection.LeftToRight,
            new OpenTypeTag("latn"),
            language: "en");
        GpuOpenTypeShapingPlan interPlan = GpuOpenTypeShapingPlanCompiler.Compile(interFace);
        GpuOpenTypeLookupCommand[] interCommands = GpuOpenTypeLookupPlanCompiler.Compile(interPlan, request);
        using var expectedComposed = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape("e\u0301", interFace, request, expectedComposed);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var interData = new GpuOpenTypeFontData(context, interPlan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actualComposed = new ShapingBuffer();
        pipeline.ExecuteRun(
            [new GpuShapingScalar('e', 0), new GpuShapingScalar(0x0301, 0)],
            interData, request, interCommands, actualComposed);

        var decompositionFace = new NormalizationFontFace();
        GpuOpenTypeShapingPlan decompositionPlan = GpuOpenTypeShapingPlanCompiler.Compile(decompositionFace);
        using var decompositionData = new GpuOpenTypeFontData(context, decompositionPlan);
        using var actualDecomposed = new ShapingBuffer();
        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x00e9, 0)],
            decompositionData, request, [], actualDecomposed);

        Assert.Equal(expectedComposed.Glyphs.ToArray(), actualComposed.Glyphs.ToArray());
        Assert.Equal(new uint[] { 'e', 0x0301 },
            actualDecomposed.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.Equal(new uint[] { 1, 2 },
            actualDecomposed.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
        Assert.True(UnicodeNormalizationPlan.DecompositionRecords.Length > 1_000);
        Assert.True(UnicodeNormalizationPlan.CompositionRecords.Length > 1_000);
    }

    [Fact]
    public void GpuHangulShapingComposesAndGatesJamoFeatures()
    {
        var composedFace = new HangulShapingFontFace(hasSyllableGlyph: true);
        var jamoFace = new HangulShapingFontFace(hasSyllableGlyph: false);
        GpuOpenTypeShapingPlan composedPlan = GpuOpenTypeShapingPlanCompiler.Compile(composedFace);
        GpuOpenTypeShapingPlan jamoPlan = GpuOpenTypeShapingPlanCompiler.Compile(jamoFace);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("hang"), language: "ko");
        uint table = jamoPlan.Tables.GsubOffset;
        GpuOpenTypeLookupCommand[] commands =
        [
            new(1, table + 4, 1, 0, new OpenTypeTag("ljmo").Value, 1),
            new(1, table + 26, 1, 0, new OpenTypeTag("vjmo").Value, 1)
        ];
        using var context = new WgpuContext();
        context.Initialize(null);
        using var composedData = new GpuOpenTypeFontData(context, composedPlan);
        using var jamoData = new GpuOpenTypeFontData(context, jamoPlan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var composed = new ShapingBuffer();
        using var jamo = new ShapingBuffer();
        using var decomposed = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x1100, 0), new GpuShapingScalar(0x1161, 1)],
            composedData, request, [], composed);
        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x1100, 0), new GpuShapingScalar(0x1161, 1)],
            jamoData, request, commands, jamo);
        pipeline.ExecuteRun(
            [new GpuShapingScalar(0xac00, 0)],
            jamoData, request, commands, decomposed);

        Assert.Single(composed.Glyphs.ToArray());
        Assert.Equal(0xac00u, composed[0].CodePoint);
        Assert.Equal(3u, composed[0].GlyphId);
        Assert.Equal(new uint[] { 10, 11 }, jamo.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
        Assert.Equal(new uint[] { 10, 11 }, decomposed.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
        Assert.All(jamo.Glyphs.ToArray(), static glyph => Assert.Equal(0, glyph.Cluster));
    }

    [Fact]
    public void GpuThaiPreprocessingDecomposesAndReordersSaraAm()
    {
        var face = new ThaiShapingFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("thai"), language: "th");
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [
                new GpuShapingScalar(0x0e01, 0),
                new GpuShapingScalar(0x0e34, 1),
                new GpuShapingScalar(0x0e33, 2)
            ],
            fontData, request, [], output);

        Assert.Equal(new uint[] { 0x0e01, 0x0e4d, 0x0e34, 0x0e32 },
            output.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.All(output.Glyphs.ToArray(), static glyph => Assert.Equal(0, glyph.Cluster));
    }

    [Fact]
    public void GpuPreprocessingInsertsInvalidVowelDottedCircle()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x0905, 0), new GpuShapingScalar(0x093e, 1)],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("deva")),
            [],
            output);

        Assert.Equal(new uint[] { 0x0905, 0x25cc, 0x093e },
            output.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.Equal(output[2].Cluster, output[1].Cluster);
    }

    [Fact]
    public void GpuUsePreprocessingDecomposesMarkLeadingDiacritic()
    {
        var face = new UseDiacriticFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x0f73, 0)],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("tibt")),
            [],
            output);

        Assert.Equal(new uint[] { 0x0f71, 0x0f72 },
            output.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.Equal(new uint[] { 2, 3 },
            output.Glyphs.ToArray().Select(static glyph => glyph.GlyphId));
    }

    [Fact]
    public void GpuUseStageReordersPrebaseVowel()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar(0x0915, 0), new GpuShapingScalar(0x093f, 1)],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("dev3")),
            [],
            output);

        Assert.Equal(new uint[] { 0x093f, 0x0915 },
            output.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        Assert.All(output.Glyphs.ToArray(), static glyph => Assert.Equal(0, glyph.Cluster));
    }

    [Fact]
    public void GpuIndicTwoPassReorderingMatchesManagedRules()
    {
        var face = new IndicShapingFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("deva"));
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);

        foreach ((string Text, uint[] Expected) item in new[]
                 {
                     ("\u0915\u093f", new uint[] { 0x093f, 0x0915 }),
                     ("\u093f", new uint[] { 0x093f, 0x25cc }),
                     ("\u0915\u094d\u0937", new uint[] { 0x0915, 0x094d, 0x0937 })
                 })
        {
            using var actual = new ShapingBuffer();
            pipeline.ExecuteRun(
                item.Text.Select((character, index) => new GpuShapingScalar(character, index)).ToArray(),
                fontData, request, [], actual);
            Assert.Equal(item.Expected,
                actual.Glyphs.ToArray().Select(static glyph => glyph.CodePoint));
        }
    }

    [Fact]
    public void GpuIndicLookupsRespectSyllableAndManualJoinerContracts()
    {
        GpuOpenTypeShapingPlan manualJoinerPlan = GpuOpenTypeShapingPlanCompiler.Compile(
            new IndicShapingFontFace("pref"));
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("deva"));
        GpuOpenTypeLookupCommand manualJoinerCommand = Assert.Single(
            GpuOpenTypeLookupPlanCompiler.Compile(manualJoinerPlan, request),
            static value => value.FeatureTag == new OpenTypeTag("pref").Value);
        Assert.Equal(0x0eu, manualJoinerCommand.CommandFlags & 0x0eu);

        var face = new IndicShapingFontFace("locl");
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        GpuOpenTypeLookupCommand command = Assert.Single(
            GpuOpenTypeLookupPlanCompiler.Compile(plan, request),
            static value => value.FeatureTag == new OpenTypeTag("locl").Value);
        Assert.Equal(2u, command.CommandFlags & 0x0eu);

        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        GpuShapingScalar[] input =
        [
            new GpuShapingScalar(0x0915, 0),
            new GpuShapingScalar(0x0915, 1)
        ];
        using var bounded = new ShapingBuffer();
        pipeline.ExecuteRun(input, fontData, request, [command], bounded);
        Assert.Equal(2, bounded.Count);

        using var unrestricted = new ShapingBuffer();
        pipeline.ExecuteRun(input, fontData, request,
            [command with { CommandFlags = command.CommandFlags & ~2u }], unrestricted);
        Assert.Single(unrestricted.Glyphs.ToArray());
        Assert.Equal(5u, unrestricted[0].GlyphId);

        GpuShapingScalar[] joinedInput =
        [
            new GpuShapingScalar(0x0915, 0),
            new GpuShapingScalar(0x200d, 1),
            new GpuShapingScalar(0x0915, 2)
        ];
        using var automaticJoiner = new ShapingBuffer();
        pipeline.ExecuteRun(joinedInput, fontData, request,
            [command with { CommandFlags = 0 }], automaticJoiner);
        Assert.Single(automaticJoiner.Glyphs.ToArray());

        using var manualJoiner = new ShapingBuffer();
        pipeline.ExecuteRun(joinedInput, fontData, request,
            [command with { CommandFlags = 8 }], manualJoiner);
        Assert.Equal(2, manualJoiner.Count);
    }

    [Fact]
    public void GpuLookupPlanPreservesHalfOpenFeatureRanges()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        uint tag = new OpenTypeTag("ss01").Value;

        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(
            plan,
            new ShapingRequest(
                ShapingDirection.LeftToRight,
                new OpenTypeTag("latn"),
                features: new[] { new ShapingFeature(new OpenTypeTag("ss01"), 1, 0, 1) }));

        GpuOpenTypeLookupCommand[] stylistic = commands.Where(command => command.FeatureTag == tag).ToArray();
        Assert.NotEmpty(stylistic);
        Assert.All(stylistic, command =>
        {
            Assert.Equal(0u, command.RangeStart);
            Assert.Equal(1u, command.RangeEnd);
            Assert.Equal(1u, command.FeatureValue);
        });
    }

    [Fact]
    public void GpuLookupVmAppliesRangedInterFeature()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(
            ShapingDirection.LeftToRight,
            new OpenTypeTag("latn"),
            features: new[] { new ShapingFeature(new OpenTypeTag("ss01"), 1, 0, 1) });
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        commands = commands.Where(command => command.FeatureTag == new OpenTypeTag("ss01").Value).ToArray();
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape("33", face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar('3', 0), new GpuShapingScalar('3', 1)],
            fontData,
            request.Direction,
            commands,
            actual);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.Glyphs.ToArray().Select(glyph => glyph.GlyphId),
            actual.Glyphs.ToArray().Select(glyph => glyph.GlyphId));
    }

    [Fact]
    public void GpuLookupVmExecutesSingleSubstitution()
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        (uint lookupOffset, uint inputGlyph, uint expectedGlyph) = FindSingleSubstitution(plan);
        uint codePoint = FindCodePoint(plan, inputGlyph);
        Assert.NotEqual(uint.MaxValue, codePoint);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var output = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar(codePoint, 0)],
            fontData,
            ShapingDirection.LeftToRight,
            [new GpuOpenTypeLookupCommand(1, lookupOffset, 1, 0, 0, 1)],
            output);

        Assert.Equal(1, output.Count);
        Assert.Equal(expectedGlyph, output[0].GlyphId);
    }

    [Theory]
    [InlineData("office")]
    [InlineData("affine difficult efficient")]
    [InlineData("AVATAR Typography")]
    public void GpuLookupVmMatchesInterLatinSubstitutions(string text)
    {
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn"), language: "en");
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        GpuShapingScalar[] input = text.Select((character, index) =>
            new GpuShapingScalar(character, index)).ToArray();
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(input, fontData, request.Direction, commands, actual);

        Assert.Equal(expected.Glyphs.ToArray().Select(glyph => glyph.GlyphId),
            actual.Glyphs.ToArray().Select(glyph => glyph.GlyphId));
        Assert.Equal(expected.Glyphs.ToArray().Select(glyph => glyph.Cluster),
            actual.Glyphs.ToArray().Select(glyph => glyph.Cluster));
    }

    [Fact]
    public void GpuPositioningMatchesInterKerning()
    {
        const string text = "AVATAR To Wa Yo";
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn"), language: "en");
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        GpuShapingScalar[] input = text.Select((character, index) =>
            new GpuShapingScalar(character, index)).ToArray();
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(input, fontData, request.Direction, commands, actual);

        Assert.Equal(expected.Glyphs.ToArray(), actual.Glyphs.ToArray());
    }

    [Fact]
    public void GpuPositioningMatchesInterMarkAttachment()
    {
        const string text = "x\u030a";
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn"), language: "en");
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar('x', 0), new GpuShapingScalar(0x030a, 0)],
            fontData,
            request.Direction,
            commands,
            actual);

        Assert.Equal(expected.Glyphs.ToArray(), actual.Glyphs.ToArray());
    }

    [Fact]
    public void GpuPositioningAppliesExtentBasedMarkFallbackWithoutGpos()
    {
        var face = new FallbackMarkFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar('x', 0), new GpuShapingScalar(0x0301, 0)],
            fontData,
            new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn")),
            [],
            actual);

        Assert.Equal(2, actual.Count);
        Assert.Equal(500, actual[0].AdvanceX);
        Assert.Equal(0, actual[1].AdvanceX);
        Assert.Equal(-350, actual[1].OffsetX);
        Assert.Equal(-162, actual[1].OffsetY);
    }

    [Fact]
    public void GpuPositioningMatchesVariableInterInstance()
    {
        const string text = "AVATAR";
        TtfFont font = InterFontFamily.GetVariableFont(537, 23);
        var face = new TtfShapingFontFace(font);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        Assert.False(plan.Variations.IsEmpty);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, new OpenTypeTag("latn"), language: "en");
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            text.Select((character, index) => new GpuShapingScalar(character, index)).ToArray(),
            fontData,
            request.Direction,
            commands,
            actual);

        Assert.Equal(expected.Glyphs.ToArray(), actual.Glyphs.ToArray());
    }

    [Fact]
    public void GpuPositioningMatchesVariableInterMarkAnchors()
    {
        const string text = "q\u0308\u0301";
        TtfFont font = InterFontFamily.GetVariableFont(537, 23);
        var face = new TtfShapingFontFace(font);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(
            ShapingDirection.LeftToRight,
            new OpenTypeTag("latn"),
            language: "en",
            features: new[] { new ShapingFeature(new OpenTypeTag("ccmp"), 0) });
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [
                new GpuShapingScalar('q', 0),
                new GpuShapingScalar(0x0308, 0),
                new GpuShapingScalar(0x0301, 0)
            ],
            fontData,
            request.Direction,
            commands,
            actual);

        Assert.Equal(expected.Glyphs.ToArray(), actual.Glyphs.ToArray());
    }

    [Theory]
    [InlineData(ShapingDirection.RightToLeft)]
    [InlineData(ShapingDirection.TopToBottom)]
    [InlineData(ShapingDirection.BottomToTop)]
    public void GpuOutputDirectionMatchesCpuExecutor(ShapingDirection direction)
    {
        const string text = "AV";
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(direction, new OpenTypeTag("latn"), language: "en");
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar('A', 0), new GpuShapingScalar('V', 1)],
            fontData,
            request,
            commands,
            actual);

        ShapingGlyph[] expectedGlyphs = expected.Glyphs.ToArray();
        ShapingGlyph[] actualGlyphs = actual.Glyphs.ToArray();
        Assert.True(expectedGlyphs.SequenceEqual(actualGlyphs),
            $"Expected: {string.Join("; ", expectedGlyphs.Select(Describe))}\n" +
            $"Actual: {string.Join("; ", actualGlyphs.Select(Describe))}");

        static string Describe(ShapingGlyph glyph) =>
            $"gid={glyph.GlyphId},cp={glyph.CodePoint:x},c={glyph.Cluster}," +
            $"a=({glyph.AdvanceX},{glyph.AdvanceY}),o=({glyph.OffsetX},{glyph.OffsetY})";
    }

    [Theory]
    [InlineData(ShapingBufferFlags.None)]
    [InlineData(ShapingBufferFlags.PreserveDefaultIgnorables)]
    [InlineData(ShapingBufferFlags.RemoveDefaultIgnorables)]
    public void GpuDefaultIgnorablePolicyMatchesCpuExecutor(ShapingBufferFlags flags)
    {
        const string text = "\u200d";
        var face = new TtfShapingFontFace(InterFontFamily.Regular);
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(
            ShapingDirection.LeftToRight,
            new OpenTypeTag("latn"),
            flags: flags);
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        using var expected = new ShapingBuffer();
        CpuOpenTypeShaper.Instance.Shape(text, face, request, expected);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun([new GpuShapingScalar(0x200d, 0)], fontData, request, commands, actual);

        Assert.Equal(expected.Glyphs.ToArray(), actual.Glyphs.ToArray());
    }

    [Theory]
    [InlineData(0xfe0e, 1)]
    [InlineData(0xfe0f, 2)]
    public void GpuPreprocessingConsumesSupportedVariationSelectors(uint selector, uint expectedGlyph)
    {
        var face = new VariationSelectorFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        Assert.Equal(2, plan.VariationMappings.Length);
        var request = new ShapingRequest(
            ShapingDirection.LeftToRight,
            OpenTypeTag.DefaultScript,
            flags: ShapingBufferFlags.PreserveDefaultIgnorables);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar('A', 0), new GpuShapingScalar(selector, 0)],
            fontData,
            request,
            [],
            actual);

        Assert.Equal(1, actual.Count);
        Assert.Equal(expectedGlyph, actual[0].GlyphId);
        Assert.Equal((uint)'A', actual[0].CodePoint);
        Assert.Equal(expectedGlyph == 1 ? 500 : 600, actual[0].AdvanceX);
    }

    [Fact]
    public void GpuPositioningAppliesLegacyKernFallback()
    {
        var face = new LegacyKernFontFace();
        GpuOpenTypeShapingPlan plan = GpuOpenTypeShapingPlanCompiler.Compile(face);
        var request = new ShapingRequest(ShapingDirection.LeftToRight, OpenTypeTag.DefaultScript);
        GpuOpenTypeLookupCommand[] commands = GpuOpenTypeLookupPlanCompiler.Compile(plan, request);
        Assert.Contains(commands, command => command.TableKind == 3);
        using var context = new WgpuContext();
        context.Initialize(null);
        using var fontData = new GpuOpenTypeFontData(context, plan);
        using var pipeline = new GpuOpenTypeRunPipeline(context);
        using var actual = new ShapingBuffer();

        pipeline.ExecuteRun(
            [new GpuShapingScalar('A', 0), new GpuShapingScalar('V', 1)],
            fontData, request, commands, actual);

        Assert.Equal(450, actual[0].AdvanceX);
        Assert.Equal(450, actual[1].AdvanceX);
        Assert.Equal(-50, actual[1].OffsetX);
    }

    private sealed class LegacyKernFontFace : IShapingFontFace
    {
        private static readonly byte[] s_kern =
        [
            0, 0, 0, 1,
            0, 0, 0, 20, 0, 1,
            0, 1, 0, 0, 0, 0, 0, 0,
            0, 1, 0, 2, 0xff, 0x9c
        ];
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 3;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("kern") ? s_kern : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint == 'A' ? 1u : codePoint == 'V' ? 2u : 0u;
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 0 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;
    }

    private sealed class ArabicJoiningFontFace : IShapingFontFace
    {
        private static readonly byte[] s_gsub = CreateGsub();

        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 14;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("GSUB") ? s_gsub : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint == 0x0628 ? 1u : codePoint == 0x064e ? 2u : 0u;
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 2 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;

        private static byte[] CreateGsub()
        {
            var data = new byte[92];
            WriteLookup(4, 10);
            WriteLookup(26, 11);
            WriteLookup(48, 12);
            WriteLookup(70, 13);
            return data;

            void WriteLookup(int lookup, ushort substitute)
            {
                U16(lookup, 1); U16(lookup + 2, 0); U16(lookup + 4, 1); U16(lookup + 6, 8);
                int subtable = lookup + 8;
                U16(subtable, 2); U16(subtable + 2, 8); U16(subtable + 4, 1); U16(subtable + 6, substitute);
                U16(subtable + 8, 1); U16(subtable + 10, 1); U16(subtable + 12, 1);
            }

            void U16(int offset, ushort value)
            {
                data[offset] = (byte)(value >> 8);
                data[offset + 1] = (byte)value;
            }
        }
    }

    private sealed class NormalizationFontFace : IShapingFontFace
    {
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 3;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = ReadOnlyMemory<byte>.Empty;
            return false;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint == 'e' ? 1u : codePoint == 0x0301 ? 2u : 0u;
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 2 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;
    }

    private sealed class HangulShapingFontFace(bool hasSyllableGlyph) : IShapingFontFace
    {
        private static readonly byte[] s_gsub = CreateGsub();
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 12;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("GSUB") ? s_gsub : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint switch
        {
            0x1100 => 1,
            0x1161 => 2,
            0xac00 when hasSyllableGlyph => 3,
            _ => 0
        };
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;

        private static byte[] CreateGsub()
        {
            var data = new byte[48];
            WriteLookup(4, 1, 10);
            WriteLookup(26, 2, 11);
            return data;

            void WriteLookup(int lookup, ushort coveredGlyph, ushort substitute)
            {
                U16(lookup, 1); U16(lookup + 2, 0); U16(lookup + 4, 1); U16(lookup + 6, 8);
                int subtable = lookup + 8;
                U16(subtable, 2); U16(subtable + 2, 8); U16(subtable + 4, 1); U16(subtable + 6, substitute);
                U16(subtable + 8, 1); U16(subtable + 10, 1); U16(subtable + 12, coveredGlyph);
            }

            void U16(int offset, ushort value)
            {
                data[offset] = (byte)(value >> 8);
                data[offset + 1] = (byte)value;
            }
        }
    }

    private sealed class ThaiShapingFontFace : IShapingFontFace
    {
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 6;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = ReadOnlyMemory<byte>.Empty;
            return false;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint switch
        {
            0x0e01 => 1,
            0x0e34 => 2,
            0x0e33 => 3,
            0x0e4d => 4,
            0x0e32 => 5,
            _ => 0
        };
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId is 2 or 4 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;
    }

    private sealed class UseDiacriticFontFace : IShapingFontFace
    {
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 4;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = ReadOnlyMemory<byte>.Empty;
            return false;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint switch
        {
            0x0f73 => 1,
            0x0f71 => 2,
            0x0f72 => 3,
            _ => 0
        };
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 0 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;
    }

    private sealed class KhmerShapingFontFace : IShapingFontFace
    {
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 6;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = ReadOnlyMemory<byte>.Empty;
            return false;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint switch
        {
            0x1780 => 1,
            0x17d2 => 2,
            0x179a => 3,
            0x17b6 => 4,
            0x25cc => 5,
            _ => 0
        };
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 0 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;
    }

    private sealed class IndicShapingFontFace(string? featureTag = null) : IShapingFontFace
    {
        private readonly byte[]? _gsub = featureTag is null ? null : CreateGsub(featureTag);

        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 6;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("GSUB") && _gsub is not null
                ? _gsub
                : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint switch
        {
            0x0915 => 1,
            0x0937 => 2,
            0x093f => 3,
            0x094d => 4,
            0x25cc => 5,
            _ => 0
        };
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId is 3 or 4 ? 0 : glyphId == 0 ? 0 : 500;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => 250;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;

        private static byte[] CreateGsub(string featureTag)
        {
            var data = new byte[80];
            U16(0, 1); U16(2, 0); U16(4, 10); U16(6, 30); U16(8, 44);
            U16(10, 1); Tag(12, "dev2"); U16(16, 8);
            U16(18, 4); U16(20, 0);
            U16(22, 0); U16(24, ushort.MaxValue); U16(26, 1); U16(28, 0);
            U16(30, 1); Tag(32, featureTag); U16(36, 8);
            U16(38, 0); U16(40, 1); U16(42, 0);
            U16(44, 1); U16(46, 4);
            U16(48, 4); U16(50, 0); U16(52, 1); U16(54, 8);
            U16(56, 1); U16(58, 18); U16(60, 1); U16(62, 8);
            U16(64, 1); U16(66, 4);
            U16(68, 5); U16(70, 2); U16(72, 1);
            U16(74, 1); U16(76, 1); U16(78, 1);
            return data;

            void U16(int offset, ushort value)
            {
                data[offset] = (byte)(value >> 8);
                data[offset + 1] = (byte)value;
            }
            void Tag(int offset, string value)
            {
                for (var index = 0; index < 4; index++) data[offset + index] = (byte)value[index];
            }
        }
    }

    private sealed class FallbackMarkFontFace : IShapingFontFace
    {
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 3;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = ReadOnlyMemory<byte>.Empty;
            return false;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint switch
        {
            'x' => 1,
            0x0301 => 2,
            _ => 0
        };
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 1 ? 500 : glyphId == 2 ? 200 : 0;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => GetHorizontalAdvance(glyphId) / 2;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetGlyphExtents(uint glyphId, out ShapingGlyphExtents extents)
        {
            extents = glyphId switch
            {
                1 => new ShapingGlyphExtents(0, 700, 500, -700),
                2 => new ShapingGlyphExtents(0, 700, 200, -100),
                _ => default
            };
            return glyphId is 1 or 2;
        }
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;
    }

    private sealed class VariationSelectorFontFace : IShapingFontFace
    {
        private static readonly byte[] s_cmap = CreateCmap();
        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 3;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("cmap") ? s_cmap : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => codePoint == 'A' ? 1u : 0u;
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = variationSelector == 0xfe0f ? 2u : 1u;
            return codePoint == 'A' && variationSelector is 0xfe0e or 0xfe0f;
        }
        public int GetHorizontalAdvance(uint glyphId) => glyphId == 1 ? 500 : glyphId == 2 ? 600 : 0;
        public int GetVerticalAdvance(uint glyphId) => 1000;
        public int GetHorizontalOrigin(uint glyphId) => GetHorizontalAdvance(glyphId) / 2;
        public int GetVerticalOrigin(uint glyphId) => 800;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short coordinate)
        {
            coordinate = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;

        private static byte[] CreateCmap()
        {
            var data = new byte[61];
            U16(0, 0); U16(2, 1); U16(4, 0); U16(6, 5); U32(8, 12);
            U16(12, 14); U32(14, 49); U32(18, 2);
            U24(22, 0xfe0e); U32(25, 32); U32(29, 0);
            U24(33, 0xfe0f); U32(36, 0); U32(40, 40);
            U32(44, 1); U24(48, 'A'); data[51] = 0;
            U32(52, 1); U24(56, 'A'); U16(59, 2);
            return data;

            void U16(int offset, uint value)
            {
                data[offset] = (byte)(value >> 8);
                data[offset + 1] = (byte)value;
            }
            void U24(int offset, uint value)
            {
                data[offset] = (byte)(value >> 16);
                data[offset + 1] = (byte)(value >> 8);
                data[offset + 2] = (byte)value;
            }
            void U32(int offset, uint value)
            {
                data[offset] = (byte)(value >> 24);
                data[offset + 1] = (byte)(value >> 16);
                data[offset + 2] = (byte)(value >> 8);
                data[offset + 3] = (byte)value;
            }
        }
    }

    private sealed class FeatureVariationFontFace(short coordinate) : IShapingFontFace
    {
        private static readonly byte[] s_gsub = CreateGsub();

        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 1;
        public uint VariationAxisCount => 1;
        public bool HasActiveVariations => coordinate != 0;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("GSUB") ? s_gsub : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => 0;
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => 0;
        public int GetVerticalAdvance(uint glyphId) => 0;
        public int GetHorizontalOrigin(uint glyphId) => 0;
        public int GetVerticalOrigin(uint glyphId) => 0;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short value)
        {
            value = coordinate;
            return axisIndex == 0;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;

        private static byte[] CreateGsub()
        {
            var data = new byte[116];
            U16(0, 1); U16(2, 1); U16(4, 14); U16(6, 34); U16(8, 50); U32(10, 68);
            U16(14, 1); Tag(16, "DFLT"); U16(20, 8);
            U16(22, 4); U16(24, 0);
            U16(26, 0); U16(28, ushort.MaxValue); U16(30, 1); U16(32, 0);
            U16(34, 1); Tag(36, "liga"); U16(40, 10);
            U16(44, 0); U16(46, 1); U16(48, 0);
            U16(50, 2); U16(52, 6); U16(54, 12);
            U16(56, 1); U16(58, 0); U16(60, 0);
            U16(62, 1); U16(64, 0); U16(66, 0);
            U16(68, 1); U16(70, 0); U32(72, 1);
            U32(76, 16); U32(80, 30);
            U16(84, 1); U32(86, 6);
            U16(90, 1); U16(92, 0); U16(94, 0x2000); U16(96, 0x4000);
            U16(98, 1); U16(100, 0); U16(102, 1);
            U16(104, 0); U32(106, 12);
            U16(110, 0); U16(112, 1); U16(114, 1);
            return data;

            void U16(int offset, ushort value)
            {
                data[offset] = (byte)(value >> 8);
                data[offset + 1] = (byte)value;
            }
            void U32(int offset, uint value)
            {
                data[offset] = (byte)(value >> 24);
                data[offset + 1] = (byte)(value >> 16);
                data[offset + 2] = (byte)(value >> 8);
                data[offset + 3] = (byte)value;
            }
            void Tag(int offset, string value)
            {
                for (var index = 0; index < 4; index++) data[offset + index] = (byte)value[index];
            }
        }
    }

    private sealed class LayoutScriptFontFace(string scriptTag) : IShapingFontFace
    {
        private readonly byte[] _gsub = CreateGsub(scriptTag);

        public int FaceIndex => 0;
        public ushort UnitsPerEm => 1000;
        public uint GlyphCount => 1;
        public uint VariationAxisCount => 0;
        public bool HasActiveVariations => false;
        public bool TryGetTable(OpenTypeTag tag, out ReadOnlyMemory<byte> table)
        {
            table = tag == new OpenTypeTag("GSUB") ? _gsub : ReadOnlyMemory<byte>.Empty;
            return !table.IsEmpty;
        }
        public uint GetNominalGlyph(uint codePoint) => 0;
        public bool TryGetVariationGlyph(uint codePoint, uint variationSelector, out uint glyphId)
        {
            glyphId = 0;
            return false;
        }
        public int GetHorizontalAdvance(uint glyphId) => 0;
        public int GetVerticalAdvance(uint glyphId) => 0;
        public int GetHorizontalOrigin(uint glyphId) => 0;
        public int GetVerticalOrigin(uint glyphId) => 0;
        public bool TryGetNormalizedVariationCoordinate(uint axisIndex, out short value)
        {
            value = 0;
            return false;
        }
        public float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) => 0;

        private static byte[] CreateGsub(string scriptTag)
        {
            var data = new byte[32];
            U16(0, 1); U16(2, 0); U16(4, 10); U16(6, 28); U16(8, 30);
            U16(10, 1); Tag(12, scriptTag); U16(16, 8);
            U16(18, 4); U16(20, 0);
            U16(22, 0); U16(24, ushort.MaxValue); U16(26, 0);
            U16(28, 0); U16(30, 0);
            return data;

            void U16(int offset, ushort value)
            {
                data[offset] = (byte)(value >> 8);
                data[offset + 1] = (byte)value;
            }
            void Tag(int offset, string value)
            {
                data[offset] = (byte)value[0];
                data[offset + 1] = (byte)value[1];
                data[offset + 2] = (byte)value[2];
                data[offset + 3] = (byte)value[3];
            }
        }
    }

    private static (uint LookupOffset, uint InputGlyph, uint ExpectedGlyph) FindSingleSubstitution(
        GpuOpenTypeShapingPlan plan)
    {
        ReadOnlySpan<byte> data = plan.TableData.Span;
        int table = checked((int)plan.Tables.GsubOffset);
        int lookupList = table + ReadU16(data, table + 8);
        int lookupCount = ReadU16(data, lookupList);
        for (var lookupIndex = 0; lookupIndex < lookupCount; lookupIndex++)
        {
            int lookup = lookupList + ReadU16(data, lookupList + 2 + lookupIndex * 2);
            if (ReadU16(data, lookup) != 1 || ReadU16(data, lookup + 4) == 0) continue;
            int subtable = lookup + ReadU16(data, lookup + 6);
            int format = ReadU16(data, subtable);
            int coverage = subtable + ReadU16(data, subtable + 2);
            uint input = FirstCoverageGlyph(data, coverage);
            uint expected = format switch
            {
                1 => (uint)((input + unchecked((short)ReadU16(data, subtable + 4))) & 0xffff),
                2 when ReadU16(data, subtable + 4) != 0 => ReadU16(data, subtable + 6),
                _ => input
            };
            if (expected != input && FindCodePoint(plan, input) != uint.MaxValue)
                return (checked((uint)lookup), input, expected);
        }
        throw new InvalidOperationException("Inter contains no testable direct single-substitution lookup.");
    }

    private static uint FirstCoverageGlyph(ReadOnlySpan<byte> data, int coverage) =>
        ReadU16(data, coverage) switch
        {
            1 => ReadU16(data, coverage + 4),
            2 => ReadU16(data, coverage + 4),
            _ => uint.MaxValue
        };

    private static uint FindCodePoint(GpuOpenTypeShapingPlan plan, uint glyph)
    {
        foreach (GpuCmapRange range in plan.Cmap.Span)
        {
            if (range.Kind == GpuCmapRange.Constant)
            {
                if (range.Glyph == glyph) return range.Start;
                continue;
            }
            if (glyph < range.Glyph) continue;
            uint codePoint = range.Start + glyph - range.Glyph;
            if (codePoint <= range.End) return codePoint;
        }
        return uint.MaxValue;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] << 8 | data[offset + 1]);

    private static ShapingGlyph Glyph(uint glyphId) => new() { GlyphId = glyphId };
}
