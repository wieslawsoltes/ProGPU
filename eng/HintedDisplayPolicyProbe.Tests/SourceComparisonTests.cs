using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HintedDisplayPolicyProbe;
using Xunit;

public sealed class SourceComparisonTests
{
    private static JsonDocument Source(double advance = 1, double dpi = 1, string text = "ab", uint[]? ids = null, int[]? clusters = null, int? lineLength = null)
        => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            Text = text, Dpi = dpi, Direction = "RightToLeft", Lines = new[] { new
            {
                SourceStart = 0, Length = lineLength ?? text.Length + 1, Width = advance * 2, WidthIncludingTrailingWhitespace = advance * 2,
                Runs = new[] { new { TextSourceCharacterIndex = 0, TextSourceLength = text.Length, BidiLevel = 0,
                    GlyphIds = ids ?? [10U, 11U], Clusters = clusters ?? [0, 1], Advances = new[] { advance, advance },
                    Offsets = new[] { new[] { 0.0, 0.0 }, new[] { 0.0, 0.0 } } } }
            } }
        }));

    private static NativeSourceParagraph Native(long advance = 64, float projection = 1)
        => new(ReferenceInput.FontHash, 0, [new('a', 0, 1), new('b', 1, 1)],
            [new(0, 0, 10, 0, 0, 1, 0, advance, 0, 0, projection, 0, 10),
             new(1, 1, 11, 0, 1, 2, 0, advance, 0, 0, projection, projection, 10)],
            [new(0, 2, 0, 2, projection * 2, 12, 10)]);

    [Fact]
    public void VirtualEndOfParagraphIsRecordedWithoutInventingSourceOrGlyphs()
    {
        using var source = Source();
        var result = SourceComparison.Compare(source.RootElement, Native());
        Assert.Equal("AlignedExactAdvances", result.Status);
        Assert.True(result.ExactFittingSourceRanges);
        var line = Assert.Single(result.Lines);
        Assert.Equal(3, line.OriginalLength); Assert.Equal(2, line.SourceEnd);
        Assert.Equal(1, line.VirtualEndOfParagraphLength);
        Assert.Equal(2, result.ComparedOccurrenceCount); Assert.Empty(result.UnmatchedNativePositionedIndices);
        Assert.Contains("frames are not equated", result.OffsetFrameQualification);
    }

    [Fact]
    public void ExactFixedPointAndFloatProjectionDifferencesAreBothRetained()
    {
        using var source = Source(9.6, 1.25);
        var result = SourceComparison.Compare(source.RootElement, Native(768, 9.6F));
        Assert.Equal("AlignedAdvanceDifferences", result.Status);
        var occurrence = result.Runs[0].Occurrences[0];
        Assert.True(occurrence.ExactLogicalAdvance);
        Assert.False(occurrence.ExactPositionedAdvance);
        Assert.Equal(768, occurrence.NativeAdvance26_6);
        Assert.Equal(9.6, occurrence.NativeAdvanceDip);
        Assert.Equal((double)9.6F - 9.6, occurrence.PositionedAdvanceDeltaDip);
    }

    [Fact]
    public void CompleteSourceWithoutVirtualEndUnitRemainsUnchanged()
    {
        using var source = Source(lineLength: 2);
        var line = Assert.Single(SourceComparison.Compare(source.RootElement, Native()).Lines);
        Assert.Equal(2, line.OriginalLength); Assert.Equal(2, line.SourceEnd);
        Assert.Equal(0, line.VirtualEndOfParagraphLength);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OtherOvershootOrNonfinalVirtualEndCannotBeRelabeledAsOriginalSource(bool nonfinal)
    {
        using var source = Source(lineLength: nonfinal ? 3 : 4);
        var node = JsonNode.Parse(source.RootElement.GetRawText())!;
        if (nonfinal) node["Lines"]!.AsArray().Add(node["Lines"]![0]!.DeepClone());
        using var changed = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SourceComparison.Compare(changed.RootElement, Native()));
    }

    [Fact]
    public void SignedNativeAdvanceAndLogicalOffsetAreNeverClampedOrRepaired()
    {
        using var source = Source(-1);
        var native = Native(-64, -1);
        native.Glyphs[0] = native.Glyphs[0] with { OffsetX26_6 = -17, OffsetY26_6 = 29 };
        var occurrence = SourceComparison.Compare(source.RootElement, native).Runs[0].Occurrences[0];
        Assert.Equal(-1, occurrence.NativeAdvanceDip);
        Assert.Equal(new long[] { -17, 29 }, occurrence.NativeLogicalOffset26_6);
        Assert.Equal(new[] { -17 / 64.0, 29 / 64.0 }, occurrence.NativeLogicalOffsetDip);
        Assert.Equal(new[] { 0.0, 0.0 }, occurrence.OriginalSourceOffset);
    }

    [Theory]
    [InlineData("glyph", "different-original-glyph-sequence")]
    [InlineData("cluster", "different-original-UTF16-cluster-coverage")]
    [InlineData("bidi", "different-original-run-bidi-level")]
    [InlineData("font", "different-original-physical-font-owner")]
    public void ExactOccurrencePreconditionsCannotBeMatchedByGlyphIdSearch(string mutation, string reason)
    {
        using var source = Source();
        var native = Native();
        native.Glyphs[0] = mutation switch
        {
            "glyph" => native.Glyphs[0] with { GlyphId = 11 },
            "cluster" => native.Glyphs[0] with { End = 2 },
            "bidi" => native.Glyphs[0] with { BidiLevel = 1 },
            _ => native.Glyphs[0] with { FontIndex = 1 }
        };
        var result = SourceComparison.Compare(source.RootElement, native);
        Assert.Equal("ExplicitUnmatchedDomains", result.Status);
        var run = Assert.Single(result.Runs);
        Assert.Equal("Unmatched", run.Status); Assert.Contains(reason, run.Reasons);
        Assert.Equal(2, result.SourceOccurrenceCount); Assert.Equal(0, result.ComparedOccurrenceCount);
        Assert.Equal(new[] { 0, 1 }, result.UnmatchedNativePositionedIndices);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalFontBytesAndFaceRemainExact(bool face)
    {
        using var source = Source();
        var native = face ? Native() with { FaceIndex = 1 } : Native() with { FontSha256 = new string('0', 64) };
        var result = SourceComparison.Compare(source.RootElement, native);
        Assert.Contains("native-original-font-bytes-or-face-mismatch", result.Reasons);
        Assert.Equal(0, result.AlignedRunCount);
    }

    [Fact]
    public void DifferentFittingKeepsAllSourceRunsExplicitlyUnmatched()
    {
        using var source = Source();
        var native = Native() with { Lines = [new(0, 1, 0, 1, 1, 12, 10), new(1, 2, 1, 1, 1, 12, 22)] };
        var result = SourceComparison.Compare(source.RootElement, native);
        Assert.False(result.ExactFittingSourceRanges);
        Assert.Equal("DifferentFittingSourceRange", result.Lines[0].Status);
        Assert.Contains("different-or-ambiguous-fitting-source-range", result.Runs[0].Reasons);
        Assert.Equal(1, result.SourceRunCount); Assert.Equal(0, result.AlignedRunCount);
    }

    [Fact]
    public void OriginalLogicalIdentitySurvivesDifferentPositionedOrderWithoutIdMatching()
    {
        using var source = Source(ids: [10, 10]);
        var native = Native();
        var first = native.Glyphs[0]; var second = native.Glyphs[1];
        native.Glyphs[0] = second with { PositionedIndex = 0, GlyphId = 10 };
        native.Glyphs[1] = first with { PositionedIndex = 1 };
        var occurrences = SourceComparison.Compare(source.RootElement, native).Runs[0].Occurrences;
        Assert.Equal(new[] { 1, 0 }, occurrences.Select(value => value.PositionedIndex));
        Assert.Equal(new[] { 0, 1 }, occurrences.Select(value => value.LogicalIndex));
        Assert.Equal(new[] { 0, 1 }, occurrences.Select(value => value.SourceStart));
    }

    [Theory]
    [InlineData("a\u0301b", 0x0061U, 0x0301U, 1)]
    [InlineData("\U0001F600b", 0x1F600U, 0U, 2)]
    public void ClusterCoverageUsesOriginalUtf16NotGlyphOrScalarCounts(string text, uint first, uint mark, int length)
    {
        using var source = Source(text: text, clusters: [0, 0, 1]);
        NativeSourceScalar[] scalars = mark == 0 ? [new(first, 0, length), new('b', 2, 1)] :
            [new(first, 0, 1), new(mark, 1, 1), new('b', 2, 1)];
        var native = Native() with { Scalars = scalars, Lines = [new(0, 3, 0, 2, 2, 12, 10)] };
        native.Glyphs[0] = native.Glyphs[0] with { End = 2 };
        native.Glyphs[1] = native.Glyphs[1] with { Start = 2, End = 3 };
        var result = SourceComparison.Compare(source.RootElement, native);
        Assert.Equal("AlignedExactAdvances", result.Status);
        Assert.Equal(2, result.Runs[0].Occurrences[0].SourceEnd);
    }

    [Fact]
    public void NativeSourceMutationNeverBecomesAnAlignedComparison()
    {
        using var source = Source();
        var native = Native(); native.Scalars[0] = native.Scalars[0] with { CodePoint = 'z' };
        var result = SourceComparison.Compare(source.RootElement, native);
        Assert.Contains("native-original-UTF16-source-mismatch", result.Reasons);
        Assert.Equal(0, result.ComparedOccurrenceCount);
    }

    [Fact]
    public void InvalidNativeGenerationCannotPublishPartialComparison()
    {
        using var source = Source();
        var native = Native(); native.Glyphs[1] = native.Glyphs[1] with { LogicalIndex = 0 };
        Assert.Throws<InvalidDataException>(() => SourceComparison.Compare(source.RootElement, native));
    }

    [Fact]
    public void EveryOriginalDisplayCaseAndRunReceivesAnExplicitResultWithoutNativeClaims()
    {
        string path = Environment.GetEnvironmentVariable("PROGPU_WPF_DISPLAY_REFERENCE")
            ?? throw new InvalidOperationException("Original reference path required.");
        using var reference = ReferenceInput.ParseVerified(File.ReadAllBytes(path), path);
        int cases = 0, runs = 0, virtualEnds = 0;
        foreach (var item in reference.RootElement.GetProperty("Cases").EnumerateArray())
        {
            if (item.GetProperty("Mode").GetString() != "Display") continue;
            string text = item.GetProperty("Text").GetString()!;
            int offset = 0;
            var scalars = new List<NativeSourceScalar>();
            foreach (var rune in text.EnumerateRunes()) { scalars.Add(new((uint)rune.Value, offset, rune.Utf16SequenceLength)); offset += rune.Utf16SequenceLength; }
            // Missing native data is an explicit unmatched control, not a
            // fabricated native generation or a Microsoft parity observation.
            var result = SourceComparison.Compare(item, new(ReferenceInput.FontHash, 0, scalars.ToArray(), [], []));
            Assert.Equal("ExplicitUnmatchedDomains", result.Status);
            Assert.Equal(0, result.ComparedOccurrenceCount);
            Assert.All(result.Runs, run => Assert.Equal("Unmatched", run.Status));
            Assert.Equal(item.GetProperty("Lines").EnumerateArray().Sum(line => line.GetProperty("Runs").GetArrayLength()), result.SourceRunCount);
            Assert.All(result.Lines, line => Assert.InRange(line.VirtualEndOfParagraphLength, 0, 1));
            cases++; runs += result.SourceRunCount; virtualEnds += result.Lines.Count(line => line.VirtualEndOfParagraphLength == 1);
        }
        Assert.Equal(96, cases); Assert.True(runs > 96); Assert.Equal(96, virtualEnds);
    }
}
