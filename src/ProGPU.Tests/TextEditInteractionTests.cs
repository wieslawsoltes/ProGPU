using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextEditInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalOwnerAcrossFallbackBoxesRetainsSourceIndexAndActualTrailingFrame(bool rtl)
    {
        sbyte level = rtl ? (sbyte)1 : (sbyte)0;
        TextLayout.ClusterBox[] boxes = rtl
            ? [new(4, 6, level, 3, 2, 11, 13, 0), new(1, 4, level, 14, 2, 11, 13, 0)]
            : [new(1, 4, level, 3, 2, 11, 13, 0), new(4, 6, level, 14, 2, 11, 13, 0)];
        var original = new TextInteractionSnapshot(7, 13, boxes, [], true, [0], [0, 1, 6, 7]);
        TextCaretStop[] originalCarets = original.CaretStops.ToArray();
        TextEditInteractionSnapshot edit = original.CreateEditInteractionSnapshot();
        Assert.Equal(edit.GetSelectionRectangles(1, 5), edit.GetSelectionRectangles(1, 3));
        Assert.Equal(edit.GetSelectionRectangles(1, 5), edit.GetSelectionRectangles(4, 2));
        foreach (bool affinity in new[] { false, true })
        {
            TextCaretStop caret = edit.GetCaretStop(4, affinity);
            Assert.Equal(4, caret.TextPosition);
            Assert.Equal(affinity, caret.IsTrailing);
            Assert.Equal(original.GetCaretStop(6, true).Position, caret.Position);
            Assert.Equal(13, caret.Height);
            Assert.Equal(level, caret.BidiLevel);
        }
        Assert.Equal(original.GetCaretStop(6, true).Position, edit.GetSourcePositionPoint(4));
        Assert.Equal(original.GetSourcePositionPoint(1), edit.GetSourcePositionPoint(1));
        Assert.Equal(original.GetSourcePositionPoint(6), edit.GetSourcePositionPoint(6));
        Assert.Equal(originalCarets, original.CaretStops.ToArray());
        Assert.NotEqual(original.GetCaretStop(4).Position, edit.GetCaretStop(4).Position);
        for (float x = 3; x < 25; x += .5f)
            Assert.Contains(edit.HitTestPoint(new Vector2(x, 8)).TextPosition, new[] { 1, 6 });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnsupportedOwnerTopologyRejectsWithoutChangingOriginalSnapshot(int kind)
    {
        TextLayout.ClusterBox[] boxes = kind switch
        {
            0 => [new(1, 4, 0, 3, 0, 11, 13, 0), new(4, 6, 0, 0, 13, 11, 13, 1)],
            1 => [new(1, 4, 0, 3, 0, 11, 13, 0), new(4, 6, 1, 14, 0, 11, 13, 0)],
            _ => [new(1, 2, 0, 3, 0, 4, 13, 0), new(6, 7, 0, 7, 0, 4, 13, 0), new(2, 6, 0, 11, 0, 4, 13, 0)]
        };
        var original = new TextInteractionSnapshot(7, 13, boxes, [], true,
            kind == 0 ? [0, 4] : [0], [0, 1, 6, 7]);
        TextCaretStop[] carets = original.CaretStops.ToArray();
        Assert.Throws<NotSupportedException>(() => original.CreateEditInteractionSnapshot());
        Assert.Equal(carets, original.CaretStops.ToArray());
    }

    [Fact]
    public void MissingOriginalOwnershipAndMissingTrailingEdgeAreExplicit()
    {
        TextLayout.ClusterBox[] boxes = [new(1, 4, 0, 3, 0, 11, 13, 0)];
        var missing = new TextInteractionSnapshot(7, 13, boxes, [], true, [0]);
        Assert.Throws<NotSupportedException>(() => missing.CreateEditInteractionSnapshot());
        var incomplete = new TextInteractionSnapshot(7, 13, boxes, [], true, [0], [0, 1, 6, 7]);
        Assert.Throws<NotSupportedException>(() => incomplete.CreateEditInteractionSnapshot());
        var vertical = new TextInteractionSnapshot(7, 13, boxes, [], false, [], [0, 1, 6, 7]);
        Assert.Throws<NotSupportedException>(() => vertical.CreateEditInteractionSnapshot());
    }

    [Fact]
    public void AnOwnerGapCannotBecomeABoundingRectangle()
    {
        TextLayout.ClusterBox[] boxes = [new(1, 4, 0, 3, 0, 11, 13, 0), new(4, 6, 0, 15, 0, 11, 13, 0)];
        var original = new TextInteractionSnapshot(7, 13, boxes, [], true, [0], [0, 1, 6, 7]);
        Assert.Throws<NotSupportedException>(() => original.CreateEditInteractionSnapshot());
        Assert.Equal(15, original.GetCaretStop(4).Position.X);
    }

    [Fact]
    public void AClusterAcrossOriginalGraphemesDoesNotInventAnInteriorCaretOrReassignOwners()
    {
        (TextLayout.ClusterBox[] Boxes, int[] Boundaries)[] cases =
        [([new(0, 2, 0, 3, 0, 11, 13, 0)], [0, 1, 2]),
            ([new(0, 2, 0, 3, 0, 11, 13, 0), new(2, 3, 0, 14, 0, 11, 13, 0)], [0, 1, 3])];
        foreach (var item in cases)
        {
            var original = new TextInteractionSnapshot(item.Boundaries[^1], 13, item.Boxes, [], true, [0], item.Boundaries);
            TextCaretStop[] carets = original.CaretStops.ToArray();
            Assert.Throws<NotSupportedException>(() => original.CreateEditInteractionSnapshot());
            Assert.Equal(carets, original.CaretStops.ToArray());
            Assert.Throws<NotSupportedException>(() => original.CreateEditInteractionSnapshot());
        }
    }

    [Fact]
    public void EmptyRowsRetainTheirInsertionOrderAfterPrecedingFallbackBoxesCoalesce()
    {
        TextLayout.ClusterBox[] boxes =
        [new(1, 4, 0, 3, 0, 11, 13, 0), new(4, 6, 0, 14, 0, 11, 13, 0), new(8, 9, 0, 0, 26, 11, 13, 2)];
        TextLayout.EmptyLineCaret[] empty =
        [new(2, new(7, false, new(0, 13), 13, 0), 1), new(3, new(9, false, new(0, 39), 13, 0), 3)];
        var original = new TextInteractionSnapshot(9, 13, boxes, empty, true, [0, 7, 8, 9], [0, 1, 6, 7, 8, 9]);
        TextEditInteractionSnapshot edit = original.CreateEditInteractionSnapshot();
        Assert.Equal(original.GetCaretStop(7), edit.GetCaretStop(7));
        Assert.Equal(original.GetCaretStop(9), edit.GetCaretStop(9));
        Assert.Equal(7, edit.HitTestPoint(new(0, 19)).TextPosition);
        Assert.Equal(9, edit.HitTestPoint(new(0, 45)).TextPosition);
        TextLayout.EmptyLineCaret[] remapped = TextInteractionSnapshot.RemapEditEmptyLines(empty, [0, 0, 1, 2]);
        Assert.Equal(new[] { 1, 2 }, remapped.Select(static row => row.BeforeBoxIndex));
        TextLayout.ClusterBox[] grouped = [new(1, 6, 0, 3, 0, 22, 13, 0), boxes[2]];
        var rows = new List<int>();
        IReadOnlyList<TextCaretStop> carets = TextInteractionSnapshot.BuildCaretStops(grouped, 13, remapped, rows);
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 3 }, rows);
        Assert.Equal(empty[0].Caret, carets[2]);
        Assert.Equal(empty[1].Caret, carets[^1]);
        Assert.Equal(new[] { 2, 3 }, empty.Select(static row => row.BeforeBoxIndex));
    }

    [Fact]
    public void OriginalWriterPolicyCapturesBeforeFallbackAndRejectsMalformedOwnership()
    {
        Assert.Equal(new[] { 0, 1, 6, 7, 8 }, OriginalGraphemePolicy.Capture("x\U0001F469\u200D\U0001F4BBy "));
        Assert.Equal(new[] { 0, 2, 3 }, OriginalGraphemePolicy.Capture("e\u0301x"));
        Assert.Null(OriginalGraphemePolicy.Capture("a\uD800b"));
        Assert.Null(OriginalGraphemePolicy.Capture("a\uDC00b"));
        var empty = new TextInteractionSnapshot(0, 13, [], [], true, [0], [0]);
        TextEditInteractionSnapshot edit = empty.CreateEditInteractionSnapshot();
        Assert.Empty(edit.GetSelectionRectangles(0, 0));
        Assert.Equal(0, edit.GetCaretStop(0).TextPosition);
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.GetCaretStop(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.GetCaretStop(1));
        Assert.Equal(empty.GetSourcePositionPoint(0), edit.GetSourcePositionPoint(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.GetSourcePositionPoint(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.GetSourcePositionPoint(1));
    }

    [Fact]
    public void OriginalOwnershipSurvivesWriterRetirementAndIsNotReusedByAnotherGeneration()
    {
        var options = new TextLayoutFormattingOptions { RetainOriginalGraphemeOwnership = true };
        var writer = new TextLayout("e\u0301 x", InterFontFamily.Regular, 20, 100, formattingOptions: options);
        TextInteractionSnapshot original = writer.CreateInteractionSnapshot();
        TextEditInteractionSnapshot edit = original.CreateEditInteractionSnapshot();
        TextBounds[] selection = edit.GetSelectionRectangles(1, 1).ToArray();
        TextCaretStop caret = edit.GetCaretStop(1);
        writer.Glyphs.Clear();
        writer.GenerateLayout(null);
        Assert.Equal(selection, edit.GetSelectionRectangles(1, 1));
        Assert.Equal(caret, edit.GetCaretStop(1));
        writer.ClearForReuse();
        writer.Reset("ab", InterFontFamily.Regular, 20, 100, TextAlignment.Left, null, null);
        Assert.Throws<NotSupportedException>(() => writer.CreateInteractionSnapshot().CreateEditInteractionSnapshot());
        writer.Reset("xy", InterFontFamily.Regular, 11, 100, TextAlignment.Left, null, null, options);
        TextEditInteractionSnapshot next = writer.CreateInteractionSnapshot().CreateEditInteractionSnapshot();
        Assert.Equal(2, next.TextLength);
        Assert.NotEqual(caret.Position, next.GetCaretStop(1).Position);
        Assert.Equal(selection, edit.GetSelectionRectangles(1, 1));
        Assert.Equal(caret, edit.GetCaretStop(1));
        var malformed = new TextLayout("a\uD800b", InterFontFamily.Regular, 20, 100, formattingOptions: options);
        Assert.Throws<NotSupportedException>(() => malformed.CreateInteractionSnapshot().CreateEditInteractionSnapshot());
    }
}
