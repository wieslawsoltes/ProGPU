using System.Text.Json;
using System.Text.Json.Nodes;
using HintedDisplayPolicyProbe;
using SourceDisplayPolicyProbe;
using Xunit;

public sealed class SourceGeometryComparisonTests
{
    private static JsonElement Source() => JsonSerializer.SerializeToElement(new
    {
        Text = "ab", Dpi = 1.5, Lines = new[] { new
        {
            SourceStart = 0, Length = 3, Width = 28.0 / 3, WidthIncludingTrailingWhitespace = 28.0 / 3,
            Height = 12.0, Baseline = 10.0, Start = 0.0,
            Runs = new[] { new { TextSourceCharacterIndex = 0, TextSourceLength = 2, BidiLevel = 0,
                GlyphIds = new[] { 10, 11 }, Clusters = new[] { 0, 1 }, Advances = new[] { 14.0 / 3, 14.0 / 3 },
                Offsets = new[] { new[] { -2.0 / 3, 1.0 / 3 }, new[] { 0.0, 0.0 } } } }
        } }
    });
    private static NativeSourceParagraph Native() => new(ReferenceInput.FontHash, 0, [new('a', 0, 1), new('b', 1, 1)],
        [new(0, 0, 10, 0, 0, 1, 0, 448, 0, 0, 14F / 3, 0, 10), new(1, 1, 11, 0, 1, 2, 0, 448, 0, 0, 14F / 3, 14F / 3, 10)],
        [new(0, 2, 0, 2, 28F / 3, 12, 10)]);
    private static SourceRunMetric[] Metrics() => [new(0, 0, 0, 14.0 / 3, -2.0 / 3, 1.0 / 3), new(0, 0, 1, 14.0 / 3, 0, 0)];
    private static SourceLineMetric[] Lines() => [new(28.0 / 3, 0, 12, 10, 10, 0)];

    [Fact]
    public void DoubleMetricsAreComparedWithoutRasterPromotionOrOffsetRepair()
    {
        var source = Source(); var alignment = SourceComparison.Compare(source, Native());
        Assert.False(alignment.Runs[0].Occurrences[0].ExactPositionedAdvance);
        var result = SourceGeometryComparison.Compare(source, alignment, Lines(), Metrics());
        Assert.Equal("ExactComparedSourceGeometry", result.Status); Assert.Equal(2, result.ComparedOccurrences);
        Assert.All(result.Runs[0].Occurrences, value => { Assert.True(value.ExactAdvance); Assert.True(value.ExactOffset); });
    }

    [Theory]
    [InlineData("advance")] [InlineData("offsetX")] [InlineData("offsetY")]
    [InlineData("height")] [InlineData("baseline")] [InlineData("width")] [InlineData("origin")]
    public void OneDoubleStepDifferenceCannotBecomeExact(string field)
    {
        var metrics = Metrics(); var lines = Lines();
        if (field == "advance") metrics[0] = metrics[0] with { Advance = Math.BitIncrement(metrics[0].Advance) };
        if (field == "offsetX") metrics[0] = metrics[0] with { OffsetX = Math.BitIncrement(metrics[0].OffsetX) };
        if (field == "offsetY") metrics[0] = metrics[0] with { OffsetY = Math.BitIncrement(metrics[0].OffsetY) };
        if (field == "height") lines[0] = lines[0] with { Height = Math.BitIncrement(lines[0].Height) };
        if (field == "baseline") lines[0] = lines[0] with { BaselineOffset = Math.BitIncrement(lines[0].BaselineOffset) };
        if (field == "width") lines[0] = lines[0] with { Width = Math.BitIncrement(lines[0].Width) };
        if (field == "origin") lines[0] = lines[0] with { OriginX = 0.5 };
        var source = Source();
        Assert.Equal("SourceGeometryDifferences", SourceGeometryComparison.Compare(source, SourceComparison.Compare(source, Native()), lines, metrics).Status);
    }

    [Theory]
    [InlineData("glyph")] [InlineData("cluster")] [InlineData("fullLevel")] [InlineData("font")]
    public void DifferentOccurrenceIdentityNeverUsesMatchingMetrics(string field)
    {
        var native = Native();
        native.Glyphs[0] = field switch
        {
            "glyph" => native.Glyphs[0] with { GlyphId = 11 }, "cluster" => native.Glyphs[0] with { End = 2 },
            "fullLevel" => native.Glyphs[0] with { BidiLevel = 2 }, _ => native.Glyphs[0] with { FontIndex = 1 }
        };
        var source = Source(); var alignment = SourceComparison.Compare(source, native);
        var result = SourceGeometryComparison.Compare(source, alignment, Lines(), []);
        Assert.Equal("ExplicitUnmatchedDomains", result.Status); Assert.Equal(2, result.OriginalOccurrences); Assert.Equal(0, result.ComparedOccurrences);
        Assert.Throws<InvalidDataException>(() => SourceGeometryComparison.Compare(source, alignment, Lines(), Metrics()));
    }

    [Fact]
    public void DuplicateOrUnownedTailCannotHideInsideMetricBatch()
    {
        var source = Source(); var alignment = SourceComparison.Compare(source, Native());
        Assert.Throws<InvalidDataException>(() => SourceGeometryComparison.Compare(source, alignment, Lines(), [.. Metrics(), Metrics()[0]]));
        Assert.Throws<InvalidDataException>(() => SourceGeometryComparison.Compare(source, alignment, Lines(), [.. Metrics(), new(0, 0, 2, 1, 0, 0)]));
    }

    [Fact]
    public void IndependentlyPinnedHebrewFaceDoesNotRelaxOldInterProbe()
    {
        var source = Source(); var native = Native() with { FontSha256 = SourceReferenceInput.RtlFontHash };
        Assert.Equal("ExplicitUnmatchedDomains", SourceComparison.Compare(source, native).Status);
        Assert.Equal(2, SourceComparison.Compare(source, native, SourceReferenceInput.RtlFontHash).ComparedOccurrenceCount);
    }
}
