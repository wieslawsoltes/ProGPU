using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextLayoutHardBreakInteractionTests
{
    private static float LineHeight => (InterFontFamily.Regular.Ascender - InterFontFamily.Regular.Descender +
        InterFontFamily.Regular.LineGap) * (20f / InterFontFamily.Regular.UnitsPerEm);

    [Theory]
    [InlineData("\n", new[] { 0, 1 })]
    [InlineData("a\n", new[] { 0, 2 })]
    [InlineData("\n\n", new[] { 0, 1, 2 })]
    [InlineData("a\n\nb", new[] { 0, 2, 3 })]
    [InlineData("\r", new[] { 0, 1 })]
    [InlineData("\r\n", new[] { 0, 2 })]
    [InlineData("a\r\n\r\nb", new[] { 0, 3, 5 })]
    [InlineData("\r\na", new[] { 0, 2 })]
    public void EachActualRowRetainsItsOriginalSourceCaretWithoutBreakGlyphs(string text, int[] starts)
    {
        var layout = new TextLayout(text, InterFontFamily.Regular, 20, 300);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        Assert.Same(text, layout.Text);
        Assert.Equal(text.Count(c => c is not ('\r' or '\n')), layout.Glyphs.Count);
        Assert.DoesNotContain(layout.Glyphs, glyph => glyph.CodePoint is '\r' or '\n');
        for (int row = 0; row < starts.Length; row++)
        {
            TextCaretStop caret = layout.GetCaretStop(starts[row]);
            Assert.Equal(starts[row], caret.TextPosition);
            Assert.Equal(new Vector2(0, row * LineHeight), caret.Position);
            Assert.Equal(LineHeight, caret.Height);
            Assert.Equal(caret, snapshot.GetCaretStop(starts[row]));
            Vector2 rowStart = new(0, row * LineHeight);
            Assert.Equal(starts[row], layout.HitTestPoint(rowStart).TextPosition);
            Assert.Equal(starts[row], snapshot.HitTestPoint(rowStart).TextPosition);
        }
        Assert.Equal(starts.Length * LineHeight, layout.ContentSize.Y);
    }

    [Theory]
    [InlineData(TextAlignment.Left, false, 0f)]
    [InlineData(TextAlignment.Center, false, 150f)]
    [InlineData(TextAlignment.Right, false, 300f)]
    [InlineData(TextAlignment.Left, true, 0f)]
    [InlineData(TextAlignment.Center, true, 150f)]
    [InlineData(TextAlignment.Right, true, 300f)]
    public void EmptyRowsKeepTheirOwnAlignmentAndResolvedParagraphLevel(TextAlignment alignment, bool rtl, float x)
    {
        var layout = new TextLayout("a\n\n", InterFontFamily.Regular, 20, 300, alignment,
            shapingOptions: new TextShapingOptions
            { Direction = rtl ? ShapingDirection.RightToLeft : ShapingDirection.LeftToRight });
        for (int row = 1; row <= 2; row++)
        {
            TextCaretStop caret = layout.GetCaretStop(row + 1);
            Assert.Equal(new Vector2(x, row * LineHeight), caret.Position);
            Assert.Equal(rtl ? 1 : 0, caret.BidiLevel);
            Assert.Equal(row + 1, caret.TextPosition);
        }
    }

    [Fact]
    public void EmptyRowHitUsesItsActualVerticalBandEvenFarFromItsZeroWidthCaret()
    {
        var layout = new TextLayout("a\n\nb", InterFontFamily.Regular, 20, 300);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        foreach (float x in new[] { -10000f, 0, 10000f })
        {
            Vector2 point = new(x, LineHeight * 1.5f);
            TextHitTestResult hit = layout.HitTestPoint(point);
            Assert.Equal(2, hit.TextPosition);
            Assert.Equal(new TextBounds(0, LineHeight, 0, LineHeight), hit.Bounds);
            Assert.False(hit.IsInside);
            Assert.Equal(hit, snapshot.HitTestPoint(point));
        }
    }

    [Theory]
    [InlineData("a\nb", 1)]
    [InlineData("a\r\nb", 2)]
    public void VisibleClusterCannotAbsorbAHardBreak(string text, int breakLength)
    {
        var layout = new TextLayout(text, InterFontFamily.Regular, 20, 300);
        float advance = layout.Glyphs[0].Glyph.Advance;
        Assert.Equal(1, layout.HitTestPoint(new(advance, LineHeight / 2)).TextPosition);
        Assert.Empty(layout.GetSelectionRectangles(1, breakLength));
    }

    [Fact]
    public void OwnedEmptyRowsSurviveSourceReuseAndParticipateInVisualNavigation()
    {
        var layout = new TextLayout("a\n\nb\n", InterFontFamily.Regular, 20, 300);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        TextCaretStop[] original = snapshot.CaretStops.ToArray();
        layout.Glyphs.Clear();
        Assert.Equal(original, snapshot.CaretStops.ToArray());
        Assert.Throws<InvalidOperationException>(() => layout.GetVisualCaretStops());
        TextCaretStop caret = snapshot.GetCaretStop(0);
        foreach (int position in new[] { 1, 2, 3, 4, 5 })
        {
            caret = snapshot.MoveCaretVisually(caret.TextPosition, caret.IsTrailing, 1);
            Assert.Equal(position, caret.TextPosition);
        }
        layout.GenerateLayout(null);
        Assert.Equal(original, layout.GetVisualCaretStops());
    }
}
