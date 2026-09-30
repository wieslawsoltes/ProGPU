using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextInteractionSnapshotTests
{
    [Theory]
    [InlineData("ax\u0301b", 1, 2)]
    [InlineData("a\U0001F642b", 1, 2)]
    [InlineData("affib", 1, 3)]
    public void ClusterHitRetainsTheWholeShapedUtf16Range(string text, int start, int length)
    {
        var layout = new TextLayout(text, InterFontFamily.Regular, 24, 300,
            shapingOptions: new TextShapingOptions
            {
                Direction = ShapingDirection.LeftToRight,
                Features = [new OpenTypeFeatureSetting("dlig")]
            },
            formattingOptions: new TextLayoutFormattingOptions { EnableFontFallback = false });
        Assert.Contains(layout.Glyphs, glyph => glyph.Cluster == start);
        Assert.DoesNotContain(layout.Glyphs, glyph => glyph.Cluster > start && glyph.Cluster < start + length);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        TextBounds bounds = Assert.Single(snapshot.GetSelectionRectangles(start, length));
        Assert.True(bounds.Width > 0);
        foreach (float fraction in new[] { .25f, .5f, .75f })
        {
            float x = fraction == .5f ? (bounds.X + bounds.Right) * .5f : bounds.X + bounds.Width * fraction;
            var point = new Vector2(x, bounds.Y + bounds.Height / 2);
            TextClusterHitTestResult result = snapshot.HitTestCluster(point);
            Assert.Equal((start, length), (result.ClusterStart, result.ClusterLength));
            Assert.Equal(bounds, result.Hit.Bounds);
            Assert.True(result.Hit.IsInside);
            Assert.Equal(fraction >= .5f, result.Hit.IsTrailingHit);
            Assert.Equal(fraction >= .5f ? start + length : start, result.Hit.TextPosition);
            Assert.Equal(layout.HitTestPoint(point), result.Hit);
            Assert.Equal(snapshot.HitTestPoint(point), result.Hit);
        }
    }

    [Fact]
    public void ClusterHitPreservesRtlHalfAndMidpointAffinity()
    {
        var layout = new TextLayout("אב", InterFontFamily.Regular, 24, 300,
            shapingOptions: new TextShapingOptions { Direction = ShapingDirection.RightToLeft });
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        TextBounds bounds = Assert.Single(snapshot.GetSelectionRectangles(0, 1));
        Assert.True(bounds.Width > 0);
        foreach (float fraction in new[] { .25f, .5f, .75f })
        {
            float x = fraction == .5f ? (bounds.X + bounds.Right) * .5f : bounds.X + bounds.Width * fraction;
            var point = new Vector2(x, bounds.Y + bounds.Height / 2);
            TextClusterHitTestResult result = snapshot.HitTestCluster(point);
            Assert.Equal((0, 1), (result.ClusterStart, result.ClusterLength));
            Assert.Equal(1, result.Hit.BidiLevel & 1);
            Assert.Equal(fraction < .5f, result.Hit.IsTrailingHit);
            Assert.Equal(fraction < .5f ? 1 : 0, result.Hit.TextPosition);
            Assert.Equal(snapshot.HitTestPoint(point), result.Hit);
        }
    }

    [Fact]
    public void ClusterHitPreservesSharedEdgeTieAndNearestOutsideSelection()
    {
        var layout = new TextLayout("ab", InterFontFamily.Regular, 24, 300);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        TextBounds first = Assert.Single(snapshot.GetSelectionRectangles(0, 1));
        TextBounds second = Assert.Single(snapshot.GetSelectionRectangles(1, 1));
        Assert.Equal(first.Right, second.X);
        var edge = new Vector2(first.Right, first.Y + first.Height / 2);
        TextClusterHitTestResult tie = snapshot.HitTestCluster(edge);
        Assert.Equal((0, 1), (tie.ClusterStart, tie.ClusterLength));
        Assert.Equal(1, tie.Hit.TextPosition);
        Assert.True(tie.Hit.IsTrailingHit);
        Assert.True(tie.Hit.IsInside);
        Assert.Equal(snapshot.HitTestPoint(edge), tie.Hit);
        var outside = new Vector2(second.Right + 100, second.Y + second.Height / 2);
        TextClusterHitTestResult nearest = snapshot.HitTestCluster(outside);
        Assert.Equal((1, 1), (nearest.ClusterStart, nearest.ClusterLength));
        Assert.Equal(2, nearest.Hit.TextPosition);
        Assert.False(nearest.Hit.IsInside);
        Assert.Equal(snapshot.HitTestPoint(outside), nearest.Hit);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("\r\n", 0)]
    [InlineData("\r\n", 2)]
    [InlineData("a\r\n\r\nb", 3)]
    public void EmptyRowClusterHitUsesItsOwnedInsertionRange(string text, int position)
    {
        var layout = new TextLayout(text, InterFontFamily.Regular, 24, 300);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        TextCaretStop caret = snapshot.GetCaretStop(position);
        var point = new Vector2(1000, caret.Position.Y + caret.Height / 2);
        TextClusterHitTestResult result = snapshot.HitTestCluster(point);
        Assert.Equal((position, 0), (result.ClusterStart, result.ClusterLength));
        Assert.Equal(position, result.Hit.TextPosition);
        Assert.False(result.Hit.IsTrailingHit);
        Assert.False(result.Hit.IsInside);
        Assert.Equal(0, result.Hit.Bounds.Width);
        Assert.Equal(caret.Height, result.Hit.Bounds.Height);
        Assert.Equal(snapshot.HitTestPoint(point), result.Hit);
    }

    [Fact]
    public void OriginalPointHitConstructorAndDeconstructionRemainUnchanged()
    {
        var expectedBounds = new TextBounds(1, 2, 3, 4);
        var hit = new TextHitTestResult(7, true, false, expectedBounds, 1);
        (int position, bool trailing, bool inside, TextBounds bounds, sbyte level) = hit;
        Assert.Equal((7, true, false, expectedBounds, (sbyte)1), (position, trailing, inside, bounds, level));
    }

    [Theory]
    [InlineData("abc", new[] { 0, 1, 2, 3 })]
    [InlineData("a\u0301b", new[] { 0, 2, 3 })]
    public void VisualMovementAdvancesPastCoincidentAffinities(string text, int[] boundaries)
    {
        var source = new TextLayout(text, InterFontFamily.Regular, 20, 500);
        TextInteractionSnapshot snapshot = source.CreateInteractionSnapshot();
        TextCaretStop caret = snapshot.GetCaretStop(0);
        for (int i = 1; i < boundaries.Length; i++)
        {
            caret = snapshot.MoveCaretVisually(caret.TextPosition, caret.IsTrailing, 1);
            Assert.Equal(boundaries[i], caret.TextPosition);
        }
        for (int i = boundaries.Length - 2; i >= 0; i--)
        {
            caret = snapshot.MoveCaretVisually(caret.TextPosition, caret.IsTrailing, -1);
            Assert.Equal(boundaries[i], caret.TextPosition);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("x\u0301 abc אבג")]
    [InlineData("abc def\n\nghi jkl")]
    public void SnapshotUsesTheSameInteractionContract(string text)
    {
        var layout = new TextLayout(text, InterFontFamily.Regular, 20, 65);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        Assert.Equal(text.Length, snapshot.TextLength);
        Assert.Equal(layout.GetVisualCaretStops(), snapshot.CaretStops.ToArray());
        for (int position = 0; position <= text.Length; position++)
        {
            foreach (bool trailing in new[] { false, true })
            {
                Assert.Equal(layout.GetCaretStop(position, trailing), snapshot.GetCaretStop(position, trailing));
                foreach (int direction in new[] { -1, 0, 1 })
                    Assert.Equal(layout.MoveCaretVisually(position, trailing, direction),
                        snapshot.MoveCaretVisually(position, trailing, direction));
            }
            Assert.Equal(layout.GetSelectionRectangles(position, text.Length - position),
                snapshot.GetSelectionRectangles(position, text.Length - position));
            Assert.Equal(layout.GetSelectionRectangles(position, -position),
                snapshot.GetSelectionRectangles(position, -position));
        }
        foreach (Vector2 point in new[] { new Vector2(-1), Vector2.Zero, new Vector2(18, 9), new Vector2(200) })
            Assert.Equal(layout.HitTestPoint(point), snapshot.HitTestPoint(point));
    }

    [Fact]
    public void SnapshotOwnsItsGenerationAfterSourceGlyphsAreClearedOrRegenerated()
    {
        var layout = new TextLayout("x\u0301 abc", InterFontFamily.Regular, 20, 65);
        TextInteractionSnapshot snapshot = layout.CreateInteractionSnapshot();
        TextCaretStop[] carets = snapshot.CaretStops.ToArray();
        TextBounds[] selection = snapshot.GetSelectionRectangles(0, layout.Text.Length).ToArray();
        TextHitTestResult hit = snapshot.HitTestPoint(new Vector2(8, 8));
        TextClusterHitTestResult cluster = snapshot.HitTestCluster(new Vector2(8, 8));
        layout.Glyphs.Clear();
        Assert.Equal(carets, snapshot.CaretStops.ToArray());
        Assert.Equal(selection, snapshot.GetSelectionRectangles(0, snapshot.TextLength));
        Assert.Equal(hit, snapshot.HitTestPoint(new Vector2(8, 8)));
        Assert.Equal(cluster, snapshot.HitTestCluster(new Vector2(8, 8)));
        layout.GenerateLayout(null);
        Assert.Equal(cluster, snapshot.HitTestCluster(new Vector2(8, 8)));
        Assert.Equal(carets, snapshot.CaretStops.ToArray());
        Assert.Equal(carets, layout.CreateInteractionSnapshot().CaretStops.ToArray());
    }
}
