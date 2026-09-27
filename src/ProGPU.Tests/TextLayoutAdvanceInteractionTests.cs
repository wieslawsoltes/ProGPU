using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextLayoutAdvanceInteractionTests
{
    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void CombiningMarkPlacementDoesNotSplitTheLogicalCluster(TextAlignment alignment)
    {
        const float size = 32;
        const float width = 300;
        var font = InterFontFamily.Regular;
        var layout = new TextLayout("x\u0301", font, size, width, alignment,
            shapingOptions: new TextShapingOptions { Direction = ShapingDirection.LeftToRight },
            formattingOptions: new TextLayoutFormattingOptions { EnableFontFallback = false });
        Assert.True(layout.Glyphs.Count >= 2);
        Assert.All(layout.Glyphs, glyph => Assert.Equal(0, glyph.Cluster));
        TextRunGlyph[] original = layout.Glyphs.ToArray();
        float advance = layout.Glyphs.Sum(glyph => glyph.Glyph.Advance);
        float left = alignment switch
        {
            TextAlignment.Center => (width - advance) / 2,
            TextAlignment.Right => width - advance,
            _ => 0
        };
        float height = (font.Ascender - font.Descender + font.LineGap) * size / font.UnitsPerEm;
        float baseline = font.Ascender * size / font.UnitsPerEm;
        float pen = left;
        bool hasPlacement = false;
        foreach (TextRunGlyph glyph in original)
        {
            hasPlacement |= MathF.Abs(glyph.Position.X - pen) > .01f
                || MathF.Abs(glyph.Position.Y - baseline) > .01f;
            pen += glyph.Glyph.Advance;
        }
        Assert.True(hasPlacement, "The fixture must exercise actual shaped placement independently of its pen.");

        TextBounds selection = Assert.Single(layout.GetSelectionRectangles(0, 2));
        Near(left, selection.X);
        Near(0, selection.Y);
        Near(advance, selection.Width);
        Near(height, selection.Height);
        var stops = layout.GetVisualCaretStops();
        Assert.Equal(2, stops.Count);
        Assert.Equal(new[] { 0, 2 }, stops.Select(stop => stop.TextPosition));
        Assert.All(stops, stop => { Near(0, stop.Position.Y); Near(height, stop.Height); });
        Near(left, stops[0].Position.X);
        Near(left + advance, stops[1].Position.X);
        Assert.Equal(0, layout.HitTestPoint(new Vector2(left + advance * .25f, height / 2)).TextPosition);
        Assert.Equal(2, layout.HitTestPoint(new Vector2(left + advance * .75f, height / 2)).TextPosition);
        Assert.Equal(original, layout.Glyphs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractionUsesLayoutPensWhileDrawingPositionsRemainIndependent(bool wrapped)
    {
        var font = InterFontFamily.Regular;
        var layout = new TextLayout(wrapped ? "abc def ghi" : "abc", font, 20,
            wrapped ? 45 : 300);
        TextCaretStop[] before = layout.GetVisualCaretStops().ToArray();
        TextBounds[] selection = layout.GetSelectionRectangles(0, layout.Text.Length).ToArray();
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            TextRunGlyph glyph = layout.Glyphs[i];
            glyph.Position += new Vector2(i % 2 == 0 ? 7 : -3, i % 3);
            layout.Glyphs[i] = glyph;
        }
        TextRunGlyph[] positioned = layout.Glyphs.ToArray();

        Assert.Equal(before, layout.GetVisualCaretStops());
        Assert.Equal(selection, layout.GetSelectionRectangles(0, layout.Text.Length));
        Assert.Equal(positioned, layout.Glyphs);
    }

    [Fact]
    public void OffsetZeroAdvanceMarkKeepsOneClusterAndTwoCaretEndpoints()
    {
        var layout = new TextLayout("ab", InterFontFamily.Regular, 20);
        Assert.Equal(2, layout.Glyphs.Count);
        TextRunGlyph first = layout.Glyphs[0];
        first.Glyph.Advance = 6;
        first.Position.X = -.5f;
        layout.Glyphs[0] = first;
        TextRunGlyph mark = layout.Glyphs[1];
        mark.Cluster = 0;
        mark.Glyph.Advance = 0;
        mark.Position = new Vector2(3, -7);
        layout.Glyphs[1] = mark;
        TextRunGlyph[] positioned = layout.Glyphs.ToArray();

        TextBounds box = Assert.Single(layout.GetSelectionRectangles(0, 2));
        Near(0, box.X);
        Near(0, box.Y);
        Near(6, box.Width);
        var stops = layout.GetVisualCaretStops();
        Assert.Equal(2, stops.Count);
        Assert.Equal(new[] { 0, 2 }, stops.Select(stop => stop.TextPosition));
        Assert.Equal(new[] { 0f, 6f }, stops.Select(stop => stop.Position.X));
        Assert.All(stops, stop => Near(0, stop.Position.Y));
        Assert.Equal(positioned, layout.Glyphs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegenerationReplacesLineFramesAndRejectsStaleGlyphMembership(bool clearAll)
    {
        var layout = new TextLayout("abc def ghi", InterFontFamily.Regular, 20, 45);
        TextCaretStop[] expected = layout.GetVisualCaretStops().ToArray();
        for (int i = 0; i < 3; i++)
        {
            layout.GenerateLayout(null);
            Assert.Equal(expected, layout.GetVisualCaretStops());
        }
        if (clearAll)
            layout.Glyphs.Clear();
        else
            layout.Glyphs.RemoveAt(0);
        Assert.Throws<InvalidOperationException>(() => layout.GetVisualCaretStops());
        layout.GenerateLayout(null);
        Assert.Equal(expected, layout.GetVisualCaretStops());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedPensPreserveVisualOrderAndBidiAffinity(bool rightToLeft)
    {
        var layout = new TextLayout(rightToLeft ? "אבג" : "abc", InterFontFamily.Regular,
            24, 300, TextAlignment.Right,
            shapingOptions: new TextShapingOptions
            {
                Direction = rightToLeft ? ShapingDirection.RightToLeft : ShapingDirection.LeftToRight
            });
        Assert.Equal(3, layout.Glyphs.Count);
        float pen = 300 - layout.ContentSize.X;
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            TextRunGlyph glyph = layout.Glyphs[i];
            Assert.Equal(rightToLeft, (glyph.BidiLevel & 1) != 0);
            glyph.Position += new Vector2(-.5f, i + 2);
            layout.Glyphs[i] = glyph;
        }
        var stops = layout.GetVisualCaretStops();
        Assert.Equal(6, stops.Count);
        for (int i = 0; i < layout.Glyphs.Count; i++)
        {
            TextRunGlyph glyph = layout.Glyphs[i];
            TextBounds box = Assert.Single(layout.GetSelectionRectangles(glyph.Cluster, 1));
            Near(pen, box.X);
            Near(0, box.Y);
            Near(glyph.Glyph.Advance, box.Width);
            Near(layout.ContentSize.Y, box.Height);
            Assert.Equal(rightToLeft ? glyph.Cluster + 1 : glyph.Cluster, stops[i * 2].TextPosition);
            Assert.Equal(rightToLeft, stops[i * 2].IsTrailing);
            Near(pen, stops[i * 2].Position.X);
            pen += glyph.Glyph.Advance;
            Near(pen, stops[i * 2 + 1].Position.X);
        }
    }

    [Fact]
    public void HardBreakAndEmptyRowKeepTheWritersOriginalLineTop()
    {
        var layout = new TextLayout("ab\n\ncd", InterFontFamily.Regular, 20);
        TextBounds first = Assert.Single(layout.GetSelectionRectangles(0, 2));
        TextBounds last = Assert.Single(layout.GetSelectionRectangles(4, 2));
        Near(0, first.Y);
        Near(first.Height * 2, last.Y);
        Near(first.Height, last.Height);
        Near(last.Y + last.Height, layout.ContentSize.Y);
    }

    private static void Near(float expected, float actual)
        => Assert.InRange(MathF.Abs(actual - expected), 0, .0001f);
}
