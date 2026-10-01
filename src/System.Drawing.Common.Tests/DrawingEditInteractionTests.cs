using ProGPU.Scene;
using ProGPU.SystemDrawing;
using Xunit;

namespace System.Drawing.Tests;

public sealed class DrawingEditInteractionTests
{
    [Theory]
    [InlineData("a\u2603\uFE0Fb\U0001F469\u200D\U0001F4BBc ", 4, 7, 9)]
    [InlineData("x\U0001F469\u200D\U0001F4BBy ", 1, 4, 6)]
    [InlineData("go A\U0001F600 e\u0301 fin ", 7, 8, 9)]
    public void OriginalEditRangesShareOneOwnedGeometryAfterCallerDisposal(string source, int start, int interior, int end)
    {
        DrawingTextLayout layout;
        var recorded = new DrawingContext();
        using (Graphics graphics = Graphics.FromProGpuDrawingContext(recorded))
        using (var font = new Font(FontFamily.GenericSansSerif, 11, GraphicsUnit.Pixel))
        using (var format = new StringFormat(StringFormat.GenericTypographic))
        {
            format.FormatFlags |= StringFormatFlags.NoWrap;
            format.FormatFlags &= ~StringFormatFlags.LineLimit;
            format.Trimming = StringTrimming.None;
            format.SetDigitSubstitution(0, StringDigitSubstitute.None);
            layout = DrawingTextLayout.Create(graphics, source, font, new SizeF(500, 100), format);
            format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            format.Alignment = StringAlignment.Far;
        }
        var ordinaryCaret = layout.GetCaretStop(interior);
        PointF ordinaryPoint = layout.GetSourcePositionPoint(interior);
        var whole = layout.GetEditSelectionRectangles(start, end - start).ToArray();
        Assert.NotEmpty(whole);
        Assert.Equal(whole, layout.GetEditSelectionRectangles(start, interior - start));
        Assert.Equal(whole, layout.GetEditSelectionRectangles(interior, end - interior));
        var caret = layout.GetEditCaretStop(interior);
        Assert.Equal(interior, caret.TextPosition);
        Assert.Equal(layout.GetCaretStop(end, true).Position, caret.Position);
        Assert.Equal(layout.GetCaretStop(end, true).Height, caret.Height);
        Assert.Equal(layout.GetEditSourcePositionPoint(end), layout.GetEditSourcePositionPoint(interior));
        Assert.Equal(layout.GetSourcePositionPoint(start), layout.GetEditSourcePositionPoint(start));
        Assert.Equal(layout.GetSourcePositionPoint(end), layout.GetEditSourcePositionPoint(end));
        Assert.Equal(ordinaryPoint.Y, layout.GetEditSourcePositionPoint(interior).Y);
        Assert.Equal(ordinaryPoint, layout.GetSourcePositionPoint(interior));
        Assert.Equal(ordinaryCaret, layout.GetCaretStop(interior));
        Assert.Empty(recorded.Commands);
        using Graphics replay = Graphics.FromProGpuDrawingContext(recorded);
        using var brush = new SolidBrush(Color.Black);
        layout.Draw(replay, brush, PointF.Empty);
        Assert.NotEmpty(recorded.Commands);
        Assert.Equal(whole, layout.GetEditSelectionRectangles(start, end - start));
        Assert.Equal(caret, layout.GetEditCaretStop(interior));
    }

    [Fact]
    public void EmptyHardRowsCrLfAndSoftWrapAffinityKeepTheirOriginalFrames()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 11, GraphicsUnit.Pixel);
        using var format = new StringFormat();
        format.Trimming = StringTrimming.None;
        format.SetDigitSubstitution(0, StringDigitSubstitute.None);
        DrawingTextLayout hard = DrawingTextLayout.Create(graphics, "x\U0001F469\u200D\U0001F4BB\r\n\r\nb\r\n", font, new SizeF(100, 100), format);
        for (int row = 0; row < hard.RowCount; row++)
        {
            int start = hard.GetRowSourceStart(row);
            foreach (bool affinity in new[] { false, true })
                Assert.Equal(hard.GetCaretStop(start, affinity), hard.GetEditCaretStop(start, affinity));
        }
        Assert.Throws<NotSupportedException>(() => hard.GetEditCaretStop(7));
        for (int position = 6; position <= hard.TextLength; position++)
            Assert.Equal(hard.GetSourcePositionPoint(position), hard.GetEditSourcePositionPoint(position));
        DrawingTextLayout empty = DrawingTextLayout.Create(graphics, "", font, new SizeF(100, 100), format);
        Assert.Equal(empty.GetCaretStop(0), empty.GetEditCaretStop(0));
        Assert.Empty(empty.GetEditSelectionRectangles(0, 0));
        Assert.Equal(empty.GetSourcePositionPoint(0), empty.GetEditSourcePositionPoint(0));
        DrawingTextLayout wrapped = DrawingTextLayout.Create(graphics, "a b c d", font, new SizeF(12, 100), format);
        Assert.True(wrapped.RowCount > 1);
        for (int row = 1; row < wrapped.RowCount; row++)
        {
            int start = wrapped.GetRowSourceStart(row);
            foreach (bool affinity in new[] { false, true })
                Assert.Equal(wrapped.GetCaretStop(start, affinity), wrapped.GetEditCaretStop(start, affinity));
            Assert.Equal(wrapped.GetSourcePositionPoint(start), wrapped.GetEditSourcePositionPoint(start));
        }
    }
}
