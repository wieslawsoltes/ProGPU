using ProGPU.CAD;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMTextTests
{
    [Fact]
    public void PlainUnicodeTextIsRetainedAsOneTypedInline()
    {
        CadMTextContent content = CadMTextParser.Parse("Office مرحبا 🏖️");

        CadMTextInline inline = Assert.Single(content.Inlines.ToArray());
        Assert.Equal(CadMTextInlineKind.Text, inline.Kind);
        Assert.Equal("Office مرحبا 🏖️", inline.Text);
        Assert.Equal(CadMTextRunStyle.Default, inline.Style);
        Assert.Equal(inline.Text.Length, content.DecodedCodeUnitCount);
    }

    [Fact]
    public void GroupsRestoreCompleteFormattingState()
    {
        CadMTextContent content = CadMTextParser.Parse(
            @"A{\L\O\K\H1.5x;\W0.8;\T1.2;\Q12;\A2;B}C");

        CadMTextInline[] runs = content.Inlines.ToArray();
        Assert.Equal(3, runs.Length);
        Assert.Equal("A", runs[0].Text);
        Assert.Equal("B", runs[1].Text);
        Assert.Equal("C", runs[2].Text);
        Assert.Equal(
            CadMTextDecoration.Underline |
            CadMTextDecoration.Overline |
            CadMTextDecoration.StrikeThrough,
            runs[1].Style.Decorations);
        Assert.Equal(new CadMTextHeight(1.5, true), runs[1].Style.Height);
        Assert.True(runs[1].Style.HasWidthFactorOverride);
        Assert.True(runs[1].Style.HasTrackingFactorOverride);
        Assert.True(runs[1].Style.HasObliqueOverride);
        Assert.Equal(0.8, runs[1].Style.WidthFactor);
        Assert.Equal(1.2, runs[1].Style.TrackingFactor);
        Assert.Equal(12.0, runs[1].Style.ObliqueDegrees);
        Assert.Equal(CadMTextVerticalAlignment.Top, runs[1].Style.VerticalAlignment);
        Assert.Equal(CadMTextRunStyle.Default, runs[2].Style);
    }

    [Fact]
    public void EscapesSymbolsAndSemanticBreaksAreNotFlattened()
    {
        CadMTextContent content = CadMTextParser.Parse(
            @"A\~B\\\{\}\U+00B0%%p%%c\Pnext\Ncolumn^Jlast");

        CadMTextInline[] inlines = content.Inlines.ToArray();
        Assert.Collection(
            inlines,
            item =>
            {
                Assert.Equal(CadMTextInlineKind.Text, item.Kind);
                Assert.Equal("A\u00A0B\\{}°±∅", item.Text);
            },
            item => Assert.Equal(CadMTextInlineKind.ParagraphBreak, item.Kind),
            item => Assert.Equal("next", item.Text),
            item => Assert.Equal(CadMTextInlineKind.ColumnBreak, item.Kind),
            item => Assert.Equal("column", item.Text),
            item => Assert.Equal(CadMTextInlineKind.ParagraphBreak, item.Kind),
            item => Assert.Equal("last", item.Text));
    }

    [Theory]
    [InlineData(@"\S1/2;", CadMTextStackKind.Horizontal)]
    [InlineData(@"\S1#2;", CadMTextStackKind.Diagonal)]
    [InlineData(@"\S1^2;", CadMTextStackKind.Tolerance)]
    public void StackedTextRetainsBothValuesAndSeparatorKind(
        string source,
        CadMTextStackKind expectedKind)
    {
        CadMTextInline inline = Assert.Single(
            CadMTextParser.Parse(source).Inlines.ToArray());

        Assert.Equal(CadMTextInlineKind.Stack, inline.Kind);
        Assert.Equal("1", inline.Text);
        Assert.Equal("2", inline.SecondaryText);
        Assert.Equal(expectedKind, inline.StackKind);
    }

    [Fact]
    public void FontColorAndParagraphOverridesAreTyped()
    {
        CadMTextContent content = CadMTextParser.Parse(
            @"\fArial|b1|i1|c0|p34;\C5;blue\c1122867;rgb\pxqc;center");

        CadMTextInline[] runs = content.Inlines.ToArray();
        Assert.Equal(3, runs.Length);
        Assert.Equal(
            new CadMTextFontOverride("Arial", true, true, 0, 34),
            runs[0].Style.Font);
        Assert.Equal(new CadMTextColor(CadMTextColorKind.Indexed, 5), runs[0].Style.Color);
        Assert.Equal(new CadMTextColor(CadMTextColorKind.TrueColor, 1122867), runs[1].Style.Color);
        Assert.Equal(CadMTextParagraphAlignment.Center, runs[2].Style.Paragraph.Alignment);
        Assert.Equal("xqc", runs[2].Style.Paragraph.RawPayload);
    }

    [Fact]
    public void ParagraphGeometryTabsSpacingAndCaretControlsAreTyped()
    {
        CadMTextContent content = CadMTextParser.Parse(
            @"\pxi-1,l2,r3,b0.5,a0.25,se1.2,qj,t4,c8,r12,d16;A^IB^JC^MD");

        CadMTextInline[] inlines = content.Inlines.ToArray();
        Assert.Collection(
            inlines,
            item => Assert.Equal("A\tB", item.Text),
            item => Assert.Equal(CadMTextInlineKind.ParagraphBreak, item.Kind),
            item => Assert.Equal("CD", item.Text));
        CadMTextParagraphFormat paragraph = inlines[0].Style.Paragraph;
        Assert.Equal(CadMTextParagraphAlignment.Justify, paragraph.Alignment);
        Assert.Equal(-1.0, paragraph.FirstLineIndentFactor);
        Assert.Equal(2.0, paragraph.LeftIndentFactor);
        Assert.Equal(3.0, paragraph.RightIndentFactor);
        Assert.Equal(0.5, paragraph.SpaceBeforeFactor);
        Assert.Equal(0.25, paragraph.SpaceAfterFactor);
        Assert.Equal(
            new CadMTextParagraphLineSpacing(
                CadMTextParagraphLineSpacingKind.Exact,
                1.2),
            paragraph.LineSpacing);
        Assert.Equal(
            [
                new CadMTextTabStop(4.0, CadMTextTabAlignment.Left),
                new CadMTextTabStop(8.0, CadMTextTabAlignment.Center),
                new CadMTextTabStop(12.0, CadMTextTabAlignment.Right),
                new CadMTextTabStop(16.0, CadMTextTabAlignment.Decimal),
            ],
            paragraph.TabStops.ToArray());
    }

    [Fact]
    public void ParagraphResetsAndTabBudgetsFailTransactionally()
    {
        CadMTextInline reset = Assert.Single(CadMTextParser.Parse(
            @"\pi4,l2,r3,b1,a1,sm1.5,t4,c8;A\pi*,l*,r*,b*,a*,s*,q*,t;B")
            .Inlines.ToArray(),
            static item => item.Text == "B");

        CadMTextParagraphFormat paragraph = reset.Style.Paragraph;
        Assert.Equal(CadMTextParagraphAlignment.Left, paragraph.Alignment);
        Assert.Equal(0.0, paragraph.FirstLineIndentFactor);
        Assert.Equal(0.0, paragraph.LeftIndentFactor);
        Assert.Equal(0.0, paragraph.RightIndentFactor);
        Assert.Equal(0.0, paragraph.SpaceBeforeFactor);
        Assert.Equal(0.0, paragraph.SpaceAfterFactor);
        Assert.Equal(CadMTextParagraphLineSpacing.Entity, paragraph.LineSpacing);
        Assert.Empty(paragraph.TabStops.ToArray());
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\pl-1;A"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\pr-1;A"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\pb-1;A"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\psx1;A"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\pt2,r2;A"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(
            @"\pt2,c4;A",
            new CadMTextParseOptions { MaxTabStopsPerParagraph = 1 }));
    }

    [Fact]
    public void AbsoluteAndRelativeHeightsRemainDistinct()
    {
        CadMTextInline[] runs = CadMTextParser.Parse(
            @"\H2.5;absolute\H0.5x;relative").Inlines.ToArray();

        Assert.Equal(new CadMTextHeight(2.5, false), runs[0].Style.Height);
        Assert.Equal(new CadMTextHeight(0.5, true), runs[1].Style.Height);
    }

    [Fact]
    public void MalformedAndUnsupportedContentFailsExplicitly()
    {
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse("}"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse("{"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\H1.5x"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\U+D800"));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(@"\S1/;"));
        Assert.Throws<NotSupportedException>(() => CadMTextParser.Parse(@"\Xunsupported"));
        Assert.Throws<NotSupportedException>(() => CadMTextParser.Parse("%<\\AcVar Date>%"));
    }

    [Fact]
    public void ParserBudgetsAreCheckedBeforeUnboundedOutputGrowth()
    {
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(
            "12345",
            new CadMTextParseOptions { MaxDecodedCodeUnits = 4 }));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(
            @"a\Pb\Pc",
            new CadMTextParseOptions { MaxInlineCount = 4 }));
        Assert.Throws<CadMTextParseException>(() => CadMTextParser.Parse(
            "{{a}}",
            new CadMTextParseOptions { MaxNestingDepth = 1 }));
    }
}
