using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class StyledTextLayoutTests
{
    [Fact]
    public void OneStyleMatchesAuthoritativeTextLayoutGlyphsAndPositions()
    {
        const string text = "office";
        var font = InterFontFamily.Regular;
        var expected = new TextLayout(text, font, 24.0f, float.PositiveInfinity);
        var actual = new StyledTextLayout(
            text,
            [new StyledTextRange(0, text.Length, new StyledTextStyle(font, 24.0f))]);

        StyledTextGlyph[] glyphs = actual.Glyphs.ToArray();
        Assert.Equal(expected.Glyphs.Count, glyphs.Length);
        for (int index = 0; index < glyphs.Length; index++)
        {
            Assert.Equal(expected.Glyphs[index].GlyphIndex, glyphs[index].GlyphIndex);
            Assert.Equal(expected.Glyphs[index].Cluster, glyphs[index].Cluster);
            Assert.InRange(
                Math.Abs(expected.Glyphs[index].Position.X - glyphs[index].Position.X),
                0.0f,
                0.001f);
            Assert.InRange(
                Math.Abs(expected.Glyphs[index].Position.Y - glyphs[index].Position.Y),
                0.0f,
                0.001f);
        }
    }

    [Fact]
    public void ParagraphBidiResolutionCrossesStyleBoundaries()
    {
        const string text = "abc אבג";
        var font = InterFontFamily.Regular;
        var layout = new StyledTextLayout(
            text,
            [
                new StyledTextRange(0, 4, new StyledTextStyle(font, 18.0f, Tag: 1)),
                new StyledTextRange(4, 3, new StyledTextStyle(font, 18.0f, Tag: 2)),
            ],
            options: new StyledTextLayoutOptions
            {
                BaseDirection = ShapingDirection.LeftToRight,
            });

        int[] visualClusters = layout.Glyphs.Span
            .ToArray()
            .Select(static glyph => glyph.Cluster)
            .Distinct()
            .ToArray();
        Assert.Equal([0, 1, 2, 3, 6, 5, 4], visualClusters);
        Assert.Contains(layout.Glyphs.ToArray(), glyph => glyph.StyleIndex == 0);
        Assert.Contains(layout.Glyphs.ToArray(), glyph => glyph.StyleIndex == 1);
    }

    [Fact]
    public void VariableMetricsWidthAndBaselineShiftStayInTheirRanges()
    {
        const string text = "small LARGE";
        var font = InterFontFamily.Regular;
        var layout = new StyledTextLayout(
            text,
            [
                new StyledTextRange(0, 6, new StyledTextStyle(font, 10.0f)),
                new StyledTextRange(6, 5, new StyledTextStyle(
                    font,
                    20.0f,
                    WidthScale: 1.5f,
                    TrackingFactor: 1.1f,
                    BaselineShift: 3.0f)),
            ]);

        StyledTextGlyph firstLarge = Assert.Single(
            layout.Glyphs.ToArray(),
            static glyph => glyph.Cluster == 6);
        StyledTextLine line = Assert.Single(layout.Lines.ToArray());
        Assert.Equal(1, firstLarge.StyleIndex);
        Assert.True(firstLarge.Position.Y < line.Baseline);
        Assert.True(layout.ContentSize.X > new TextLayout(text, font, 10.0f).ContentSize.X);
        Assert.True(line.Height >= 20.0f);
    }

    [Fact]
    public void InlineBoxParticipatesInWrappingMetricsAndPlacement()
    {
        const string text = "A\uFFFCB";
        var font = InterFontFamily.Regular;
        var layout = new StyledTextLayout(
            text,
            [new StyledTextRange(0, text.Length, new StyledTextStyle(font, 12.0f))],
            [new StyledTextInlineBox(1, 20.0f, 8.0f, 2.0f, 42)]);

        StyledTextPositionedBox box = Assert.Single(layout.Boxes.ToArray());
        StyledTextGlyph b = Assert.Single(
            layout.Glyphs.ToArray(),
            static glyph => glyph.Cluster == 2);
        Assert.Equal(42, box.Tag);
        Assert.Equal(20.0f, box.Width);
        Assert.True(b.Position.X >= box.Position.X + box.Width);
    }

    [Fact]
    public void NonFinalJustifiedLineConsumesTheRequestedWidth()
    {
        const string prefix = "A B ";
        const string text = prefix + "C";
        var font = InterFontFamily.Regular;
        float maxWidth = new TextLayout("A B", font, 16.0f).ContentSize.X + 0.5f;
        var layout = new StyledTextLayout(
            text,
            [new StyledTextRange(
                0,
                text.Length,
                new StyledTextStyle(font, 16.0f, Alignment: TextAlignment.Justify))],
            options: new StyledTextLayoutOptions { MaxWidth = maxWidth });

        StyledTextLine[] lines = layout.Lines.ToArray();
        Assert.True(lines.Length >= 2);
        Assert.False(lines[0].IsParagraphFinal);
        Assert.InRange(Math.Abs(lines[0].Width - maxWidth), 0.0f, 0.01f);
    }

    [Fact]
    public void ExactAndAtLeastSpacingHaveDistinctOversizeBehavior()
    {
        const string text = "A\nB";
        var font = InterFontFamily.Regular;
        var ranges = new[]
        {
            new StyledTextRange(0, text.Length, new StyledTextStyle(font, 20.0f)),
        };
        var exact = new StyledTextLayout(
            text,
            ranges,
            options: new StyledTextLayoutOptions
            {
                MinimumLineSpacing = 10.0f,
                ExactLineSpacing = true,
            });
        var atLeast = new StyledTextLayout(
            text,
            ranges,
            options: new StyledTextLayoutOptions
            {
                MinimumLineSpacing = 10.0f,
                ExactLineSpacing = false,
            });

        Assert.All(exact.Lines.ToArray(), line => Assert.Equal(10.0f, line.Height));
        Assert.All(atLeast.Lines.ToArray(), line => Assert.True(line.Height > 10.0f));
    }

    [Fact]
    public void InvalidPartitionsAndInlineBoxesFailBeforeShaping()
    {
        var font = InterFontFamily.Regular;
        StyledTextStyle style = new(font, 12.0f);

        Assert.Throws<ArgumentException>(() => new StyledTextLayout(
            "abc",
            [new StyledTextRange(0, 2, style)]));
        Assert.Throws<ArgumentException>(() => new StyledTextLayout(
            "abc",
            [new StyledTextRange(0, 3, style)],
            [new StyledTextInlineBox(1, 1.0f, 1.0f, 0.0f)]));
        Assert.Throws<ArgumentException>(() => new StyledTextLayout(
            "\uFFFC",
            [new StyledTextRange(0, 1, style)]));
        Assert.Throws<ArgumentException>(() => new StyledTextLayout(
            "A\U0001F600B",
            [
                new StyledTextRange(0, 2, style),
                new StyledTextRange(2, 2, style),
            ]));
    }
}
