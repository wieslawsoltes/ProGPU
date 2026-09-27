using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextRowNavigationTests
{
    [Theory]
    [InlineData(1, 0, 2)]
    [InlineData(4, 4, 4)]
    [InlineData(7, 6, 8)]
    public void BoundariesUseOriginalSourceWithinOneActualRow(int caret, int start, int end)
    {
        var snapshot = Make("ab\r\n\r\ncd").CreateInteractionSnapshot();
        Assert.Equal(start, snapshot.GetRowBoundary(caret, false, false).TextPosition);
        Assert.Equal(end, snapshot.GetRowBoundary(caret, false, true).TextPosition);
    }

    [Fact]
    public void WrappedBoundaryKeepsItsDistinctRowAffinities()
    {
        float width = Make("aa ").Glyphs.Sum(glyph => glyph.Glyph.Advance) + 1;
        var snapshot = Make("aa aa aa", width).CreateInteractionSnapshot();
        Assert.Equal(3, snapshot.GetRowBoundary(4, false, false).TextPosition);
        Assert.Equal(6, snapshot.GetRowBoundary(4, false, true).TextPosition);
        Assert.Equal(0, snapshot.GetRowBoundary(3, true, false).TextPosition);
        Assert.Equal(6, snapshot.GetRowBoundary(3, false, true).TextPosition);
    }

    [Theory]
    [InlineData("aaaa\nx\naaaa", 6, 10)]
    [InlineData("aaaa\n\naaaa", 5, 9)]
    public void VerticalMovementUsesPreferredXAcrossShortAndEmptyRows(string text, int middle, int last)
    {
        var layout = Make(text);
        var snapshot = layout.CreateInteractionSnapshot();
        var origin = snapshot.GetCaretStop(3);
        var next = snapshot.MoveCaretVertically(3, false, 1, origin.Position.X);
        Assert.Equal(middle, next.TextPosition);
        layout.Glyphs.Clear();
        next = snapshot.MoveCaretVertically(next.TextPosition, next.IsTrailing, 1, origin.Position.X);
        Assert.Equal(last, next.TextPosition);
        next = snapshot.MoveCaretVertically(next.TextPosition, next.IsTrailing, -1, origin.Position.X);
        Assert.Equal(middle, next.TextPosition);
        next = snapshot.MoveCaretVertically(next.TextPosition, next.IsTrailing, -1, origin.Position.X);
        Assert.Equal(3, next.TextPosition);
        Assert.Equal(next, snapshot.MoveCaretVertically(next.TextPosition, next.IsTrailing, -1, origin.Position.X));
    }

    [Theory]
    [InlineData(-1000f, 4)]
    [InlineData(1000f, 7)]
    public void VerticalTargetsAreRealClusterStops(float x, int expected)
    {
        var snapshot = Make("a\u0301b\nc\u0301d").CreateInteractionSnapshot();
        var result = snapshot.MoveCaretVertically(2, false, 1, x);
        Assert.Equal(expected, result.TextPosition);
        Assert.Contains(result, snapshot.CaretStops.ToArray());
    }

    [Fact]
    public void RowIdentityDoesNotDependOnDistinctRoundedVerticalPositions()
    {
        var snapshot = new TextLayout("\n\n", InterFontFamily.Regular, 0, 300).CreateInteractionSnapshot();
        var first = snapshot.GetCaretStop(0);
        var second = snapshot.MoveCaretVertically(0, false, 1, 0);
        Assert.Equal(first.Position, second.Position);
        Assert.Equal(1, second.TextPosition);
        Assert.Equal(2, snapshot.MoveCaretVertically(1, false, 1, 0).TextPosition);
    }

    [Fact]
    public void MixedBidiRowsRetainLogicalEndpointsAndPhysicalTargets()
    {
        var snapshot = Make("a אב b\nc גד d").CreateInteractionSnapshot();
        Assert.Equal(0, snapshot.GetRowBoundary(3, false, false).TextPosition);
        Assert.Equal(6, snapshot.GetRowBoundary(3, false, true).TextPosition);
        var current = snapshot.GetCaretStop(3);
        var candidates = snapshot.CaretStops.ToArray().Where(c => c.TextPosition >= 7).ToArray();
        var actual = snapshot.MoveCaretVertically(3, current.IsTrailing, 1, current.Position.X);
        float distance = candidates.Min(c => Math.Abs(c.Position.X - current.Position.X));
        Assert.Equal(distance, Math.Abs(actual.Position.X - current.Position.X));
        Assert.Contains(actual, candidates);
    }

    [Fact]
    public void ZeroDirectionAndEmptyTextRetainTheExistingCaret()
    {
        foreach (string text in new[] { "", "abc\ndef" })
        {
            var snapshot = Make(text).CreateInteractionSnapshot();
            Assert.Equal(snapshot.GetCaretStop(0), snapshot.MoveCaretVertically(0, false, 0, 1000));
        }
        var empty = Make("").CreateInteractionSnapshot();
        Assert.Equal(empty.GetCaretStop(0), empty.GetRowBoundary(0, false, true));
        Assert.Equal(empty.GetCaretStop(0), empty.MoveCaretVertically(0, false, 1, 1000));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonfinitePreferredCoordinateIsRejected(float x)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Make("abc").CreateInteractionSnapshot()
            .MoveCaretVertically(0, false, 1, x));

    [Fact]
    public void VerticalWritingIsNotAdmittedAsHorizontalRows()
    {
        var snapshot = new TextLayout("abc", InterFontFamily.Regular, 20, 300,
            shapingOptions: new TextShapingOptions { Direction = ShapingDirection.TopToBottom }).CreateInteractionSnapshot();
        Assert.Throws<NotSupportedException>(() => snapshot.GetRowBoundary(0, false, true));
        Assert.Throws<NotSupportedException>(() => snapshot.MoveCaretVertically(0, false, 1, 0));
    }

    private static TextLayout Make(string text, float width = 300)
        => new(text, InterFontFamily.Regular, 20, width);
}
