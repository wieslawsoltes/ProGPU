using System.Numerics;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests;

public sealed class TextInteractionSnapshotTests
{
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
        layout.Glyphs.Clear();
        Assert.Equal(carets, snapshot.CaretStops.ToArray());
        Assert.Equal(selection, snapshot.GetSelectionRectangles(0, snapshot.TextLength));
        Assert.Equal(hit, snapshot.HitTestPoint(new Vector2(8, 8)));
        layout.GenerateLayout(null);
        Assert.Equal(carets, snapshot.CaretStops.ToArray());
        Assert.Equal(carets, layout.CreateInteractionSnapshot().CaretStops.ToArray());
    }
}
