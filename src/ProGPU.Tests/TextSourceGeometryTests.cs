using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextSourceGeometryTests
{
    [Theory]
    [InlineData("", new[] { 0 })]
    [InlineData("a\r\nb", new[] { 0, 3 })]
    [InlineData("a\nb", new[] { 0, 2 })]
    [InlineData("a\rb", new[] { 0, 2 })]
    [InlineData("\r\n\r\n", new[] { 0, 2, 4 })]
    [InlineData("a\r\n\r\nb\n", new[] { 0, 3, 5, 7 })]
    public void EverySourceCodeUnitBelongsToItsActualPhysicalRow(string text, int[] starts)
    {
        var snapshot = Make(text).CreateInteractionSnapshot();
        Assert.Equal(starts.Length, snapshot.RowCount);
        Assert.Equal(starts, Enumerable.Range(0, snapshot.RowCount).Select(snapshot.GetRowSourceStart));
        for (int position = 0; position <= text.Length; position++)
            Assert.Equal(Array.FindLastIndex(starts, start => start <= position), snapshot.GetRowIndexFromTextPosition(position));
    }

    [Fact]
    public void SoftWrapKeepsSourceRowAndCaretAffinityDistinct()
    {
        float width = Make("aa ").Glyphs.Sum(g => g.Glyph.Advance) + 1;
        var snapshot = Make("aa aa aa", width).CreateInteractionSnapshot();
        Assert.Equal(3, snapshot.RowCount);
        Assert.Equal(new[] { 0, 3, 6 }, Enumerable.Range(0, 3).Select(snapshot.GetRowSourceStart));
        Assert.Equal(1, snapshot.GetRowIndexFromTextPosition(3));
        Assert.Equal(0, snapshot.GetCaretRowIndex(3, true));
        Assert.Equal(1, snapshot.GetCaretRowIndex(3, false));
        Assert.Equal(snapshot.GetCaretStop(3, false).Position, snapshot.GetSourcePositionPoint(3));
    }

    [Theory]
    [InlineData("a\rb", 1)]
    [InlineData("a\nb", 1)]
    [InlineData("a\r\nb", 1)]
    [InlineData("a\r\nb", 2)]
    public void HardDelimiterPositionUsesThePrecedingRowsRealEnd(string text, int position)
    {
        var snapshot = Make(text).CreateInteractionSnapshot();
        Assert.Equal(snapshot.GetRowBoundary(0, false, true).Position, snapshot.GetSourcePositionPoint(position));
        Assert.Equal(0, snapshot.GetRowIndexFromTextPosition(position));
    }

    [Theory]
    [InlineData("a\u0301b")]
    [InlineData("\U0001F600b")]
    public void InteriorClusterCodeUnitsDoNotInventPositionStops(string text)
    {
        var snapshot = Make(text).CreateInteractionSnapshot();
        Assert.Equal(snapshot.GetSourcePositionPoint(0), snapshot.GetSourcePositionPoint(1));
        Assert.DoesNotContain(snapshot.CaretStops.ToArray(), c => c.TextPosition == 1);
    }

    [Theory]
    [InlineData(ShapingDirection.LeftToRight)]
    [InlineData(ShapingDirection.RightToLeft)]
    public void MixedBidiSourcePointsUseTheOwnedClustersLogicalLeadingEdges(ShapingDirection direction)
    {
        var snapshot = Make("a אב b\r\nc גד d", direction: direction).CreateInteractionSnapshot();
        foreach (TextCaretStop caret in snapshot.CaretStops)
        {
            if (caret.IsTrailing) continue;
            Assert.Equal(caret.Position, snapshot.GetSourcePositionPoint(caret.TextPosition));
        }
        Assert.Equal(8, snapshot.GetRowSourceStart(1));
    }

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Center)]
    [InlineData(TextAlignment.Right)]
    public void EmptyRowsKeepSourceIdentityAtZeroHeight(TextAlignment alignment)
    {
        var snapshot = new TextLayout("\r\n\r\n", InterFontFamily.Regular, 0, 300, alignment).CreateInteractionSnapshot();
        Assert.Equal(3, snapshot.RowCount);
        Assert.Equal(new[] { 0, 2, 4 }, Enumerable.Range(0, 3).Select(snapshot.GetRowSourceStart));
        for (int index = 0; index <= 4; index++)
            Assert.Equal(snapshot.GetCaretStop((index / 2) * 2).Position, snapshot.GetSourcePositionPoint(index));
    }

    [Fact]
    public void SnapshotOwnsSourceRowsAfterOriginalGlyphMutationAndRegeneration()
    {
        var layout = Make("first\r\n\r\nlast");
        var snapshot = layout.CreateInteractionSnapshot();
        int[] rows = Enumerable.Range(0, snapshot.RowCount).Select(snapshot.GetRowSourceStart).ToArray();
        Vector2[] positions = Enumerable.Range(0, snapshot.TextLength + 1).Select(snapshot.GetSourcePositionPoint).ToArray();
        layout.Glyphs.Clear();
        Assert.Equal(rows, Enumerable.Range(0, snapshot.RowCount).Select(snapshot.GetRowSourceStart));
        Assert.Equal(positions, Enumerable.Range(0, snapshot.TextLength + 1).Select(snapshot.GetSourcePositionPoint));
        layout.GenerateLayout(null);
        Assert.Equal(rows, Enumerable.Range(0, snapshot.RowCount).Select(snapshot.GetRowSourceStart));
    }

    [Theory]
    [InlineData("\u200Babc\r\nxyz", 6)]
    [InlineData("\u202Eabc\u202C\r\n\u200Bxyz", 7)]
    [InlineData("\u2067אב\u2069\nabc", 5)]
    [InlineData("\u200B\r\n\u200B", 3)]
    public void HiddenFormattingCharactersRetainOriginalSourceRowCoverage(string text, int secondRow)
    {
        var snapshot = Make(text).CreateInteractionSnapshot();
        Assert.Equal(0, snapshot.GetRowSourceStart(0));
        Assert.Equal(secondRow, snapshot.GetRowSourceStart(1));
        Vector2[] points = snapshot.CaretStops.ToArray().Select(c => c.Position).ToArray();
        for (int position = 0; position <= text.Length; position++)
        {
            Assert.Equal(position < secondRow ? 0 : 1, snapshot.GetRowIndexFromTextPosition(position));
            Assert.Contains(snapshot.GetSourcePositionPoint(position), points);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void InvalidSourcePositionsAreRejected(int position)
    {
        var snapshot = Make("abc").CreateInteractionSnapshot();
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetRowIndexFromTextPosition(position));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetSourcePositionPoint(position));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void InvalidRowsAreRejected(int row)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Make("a\nb").CreateInteractionSnapshot().GetRowSourceStart(row));

    [Theory]
    [InlineData(ShapingDirection.TopToBottom)]
    [InlineData(ShapingDirection.BottomToTop)]
    public void VerticalWritingDoesNotAcquireHorizontalSourceRows(ShapingDirection direction)
    {
        var snapshot = Make("abc", direction: direction).CreateInteractionSnapshot();
        Assert.Throws<NotSupportedException>(() => snapshot.RowCount);
        Assert.Throws<NotSupportedException>(() => snapshot.GetRowSourceStart(0));
        Assert.Throws<NotSupportedException>(() => snapshot.GetSourcePositionPoint(0));
        Assert.Throws<NotSupportedException>(() => snapshot.GetCaretRowIndex(0));
    }

    private static TextLayout Make(string text, float width = 300, ShapingDirection direction = ShapingDirection.LeftToRight)
        => new(text, InterFontFamily.Regular, 20, width,
            shapingOptions: new TextShapingOptions { Direction = direction });
}
