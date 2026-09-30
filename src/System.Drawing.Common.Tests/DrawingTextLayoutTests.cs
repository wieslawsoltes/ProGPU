using System.Drawing.Text;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using Xunit;

namespace System.Drawing.Tests;

public sealed class DrawingTextLayoutTests
{
    [Theory]
    [InlineData(StringAlignment.Near, false)]
    [InlineData(StringAlignment.Center, false)]
    [InlineData(StringAlignment.Far, true)]
    public void ClusterHitRetainsUtf16RangeThroughDrawingAlignment(StringAlignment alignment, bool rtl)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 20);
        using StringFormat format = MakeFormat();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Far;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        if (rtl) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, "a x\u0301 b", font, new SizeF(300, 150), format);
        var bounds = Assert.Single(layout.GetSelectionRectangles(2, 2));
        Assert.True(bounds.Width > 0);
        foreach (float fraction in new[] { .25f, .75f })
        {
            var point = new PointF(bounds.X + bounds.Width * fraction, bounds.Y + bounds.Height / 2);
            var result = layout.HitTestCluster(point);
            Assert.Equal((2, 2), (result.ClusterStart, result.ClusterLength));
            Assert.Equal(bounds, result.Hit.Bounds);
            Assert.True(result.Hit.IsInside);
            Assert.Equal(layout.HitTestPoint(point), result.Hit);
        }
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("a\r\n\r\nb", 3)]
    public void ClusterHitKeepsAlignedEmptyRowInsertionAndFontHeight(string text, int position)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 20);
        using StringFormat format = MakeFormat();
        format.Alignment = StringAlignment.Far;
        format.LineAlignment = StringAlignment.Far;
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, text, font, new SizeF(300, 150), format);
        var caret = layout.GetCaretStop(position);
        var point = new PointF(caret.Position.X + 1000, caret.Position.Y + caret.Height / 2);
        var result = layout.HitTestCluster(point);
        Assert.Equal((position, 0), (result.ClusterStart, result.ClusterLength));
        Assert.False(result.Hit.IsInside);
        Assert.Equal(0, result.Hit.Bounds.Width);
        Assert.Equal(caret.Position.X, result.Hit.Bounds.X);
        Assert.Equal(caret.Position.Y, result.Hit.Bounds.Y);
        Assert.Equal(caret.Height, result.Hit.Bounds.Height);
        Assert.Equal(layout.HitTestPoint(point), result.Hit);
    }

    [Theory]
    [InlineData(StringAlignment.Near)]
    [InlineData(StringAlignment.Center)]
    [InlineData(StringAlignment.Far)]
    public void RowNavigationUsesAlignedLayoutCoordinates(StringAlignment alignment)
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 20);
        using StringFormat format = MakeFormat();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Far;
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, "aaaa\n\naaaa", font, new SizeF(300, 500), format);
        var origin = layout.GetCaretStop(3);
        var middle = layout.MoveCaretVertically(3, false, 1, origin.Position.X);
        Assert.Equal(5, middle.TextPosition);
        var last = layout.MoveCaretVertically(5, middle.IsTrailing, 1, origin.Position.X);
        Assert.Equal(9, last.TextPosition);
        Assert.Equal(origin.Position.X, last.Position.X);
        Assert.True(last.Position.Y > origin.Position.Y);
        Assert.Equal(6, layout.GetRowBoundary(9, false, false).TextPosition);
        Assert.Equal(10, layout.GetRowBoundary(9, false, true).TextPosition);
    }

    [Theory]
    [InlineData("\n\n", new[] { 0, 1, 2 }, 0)]
    [InlineData("a\r\n\r\nb\r\n", new[] { 0, 3, 5, 8 }, 2)]
    public void RetainedDrawingKeepsEmptyRowsAndOriginalCrLfIndices(string text, int[] starts, int glyphCount)
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        using var font = new Font(FontFamily.GenericSansSerif, 20);
        using var brush = new SolidBrush(Color.Black);
        using StringFormat format = MakeFormat();
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, text, font, new SizeF(300, 500), format);
        float height = font.GetHeight(graphics);
        Assert.Equal(text.Length, layout.TextLength);
        for (int row = 0; row < starts.Length; row++)
        {
            var caret = layout.GetCaretStop(starts[row]);
            Assert.Equal(starts[row], caret.TextPosition);
            Assert.Equal(row * height, caret.Position.Y, 4);
            Assert.Equal(height, caret.Height, 4);
            if (starts[row] == text.Length || text[starts[row]] is '\r' or '\n')
                Assert.Equal(starts[row], layout.HitTestPoint(new PointF(10000, (row + .5f) * height)).TextPosition);
        }
        layout.Draw(graphics, brush, PointF.Empty);
        Assert.Equal(glyphCount, context.Commands.Where(c => c.Type == RenderCommandType.DrawGlyphRun)
            .Sum(c => c.GlyphIndices!.Length));
    }

    [Theory]
    [InlineData(StringAlignment.Near, false, false)]
    [InlineData(StringAlignment.Center, false, false)]
    [InlineData(StringAlignment.Far, false, false)]
    [InlineData(StringAlignment.Near, true, false)]
    [InlineData(StringAlignment.Center, true, false)]
    [InlineData(StringAlignment.Far, true, false)]
    [InlineData(StringAlignment.Near, true, true)]
    [InlineData(StringAlignment.Far, true, true)]
    public void RepeatedPaintReusesExactGlyphsAndMatchesFormattedDrawing(
        StringAlignment alignment, bool noWrap, bool rtl)
    {
        const string text = "office e\u0301 abc אבג  ";
        var direct = new DrawingContext();
        var retained = new DrawingContext();
        using Graphics first = Graphics.FromProGpuDrawingContext(direct);
        using Graphics second = Graphics.FromProGpuDrawingContext(retained);
        using var font = new Font(FontFamily.GenericSansSerif, 16,
            FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout);
        using var brush = new SolidBrush(Color.Navy);
        using StringFormat format = MakeFormat();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        if (noWrap) format.FormatFlags |= StringFormatFlags.NoWrap;
        if (rtl) format.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
        var bounds = new RectangleF(7, 9, 130, 200);
        DrawingTextLayout layout = DrawingTextLayout.Create(first, text, font, bounds.Size, format);
        Assert.Empty(direct.Commands);
        first.DrawString(text, font, brush, bounds, format);
        layout.Draw(second, brush, bounds.Location);
        Assert.Equal(direct.Commands.Count, retained.Commands.Count);
        for (int i = 0; i < direct.Commands.Count; i++)
        {
            RenderCommand expected = direct.Commands[i];
            RenderCommand actual = retained.Commands[i];
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal((expected.Rect.X, expected.Rect.Y, expected.Rect.Width, expected.Rect.Height),
                (actual.Rect.X, actual.Rect.Y, actual.Rect.Width, actual.Rect.Height));
            Assert.Equal(expected.Transform, actual.Transform);
            Assert.Same(expected.Font, actual.Font);
            Assert.Equal(expected.FontSize, actual.FontSize);
            Assert.Equal(expected.Position, actual.Position);
            Assert.Equal(expected.IsBold, actual.IsBold);
            Assert.Equal(expected.IsItalic, actual.IsItalic);
            Assert.Equal(expected.GlyphIndices, actual.GlyphIndices);
            Assert.Equal(expected.GlyphPositions, actual.GlyphPositions);
        }
        var before = retained.Commands.Where(c => c.Type == RenderCommandType.DrawGlyphRun).ToArray();
        int count = retained.Commands.Count;
        layout.Draw(second, brush, new PointF(21, 33));
        var after = retained.Commands.Skip(count).Where(c => c.Type == RenderCommandType.DrawGlyphRun).ToArray();
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Same(before[i].GlyphIndices, after[i].GlyphIndices);
            Assert.Same(before[i].GlyphPositions, after[i].GlyphPositions);
        }
        Assert.Equal(text.Length, layout.TextLength);
        var selection = layout.GetSelectionRectangles(0, text.Length);
        Assert.NotEmpty(selection);
        foreach (var box in selection)
        {
            var hit = layout.HitTestPoint(new PointF(box.X + box.Width / 2, box.Y + box.Height / 2));
            Assert.True(hit.IsInside);
            Assert.InRange(hit.TextPosition, 0, text.Length);
        }
    }

    [Fact]
    public void CompleteParagraphSurvivesClippingAndCallerDisposal()
    {
        const string text = "first line\nsecond line";
        var context = new DrawingContext();
        DrawingTextLayout layout;
        using (Graphics graphics = Graphics.FromProGpuDrawingContext(context))
        using (var font = new Font(FontFamily.GenericSansSerif, 18))
        using (StringFormat format = MakeFormat())
        {
            format.FormatFlags &= ~StringFormatFlags.NoClip;
            layout = DrawingTextLayout.Create(graphics, text, font, new SizeF(500, 1), format);
            format.Alignment = StringAlignment.Far;
            format.SetTabStops(10, [20]);
        }
        using Graphics target = Graphics.FromProGpuDrawingContext(context);
        using var brush = new SolidBrush(Color.Black);
        layout.Draw(target, brush, PointF.Empty);
        Assert.Equal(text.Length, layout.GetCaretStop(text.Length, true).TextPosition);
        Assert.True(layout.ContentSize.Height > 1);
        Assert.Contains(context.Commands, c => c.Type == RenderCommandType.PushClip && c.Rect.Height == 1);
        Assert.Equal(RenderCommandType.PopClip, context.Commands[^1].Type);
        Assert.Contains(context.Commands, c => c.GlyphPositions?.Any(p => p.Y > 1) == true);
    }

    [Fact]
    public void AlignmentMovesPaintingAndInteractionTogether()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using StringFormat format = MakeFormat();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        DrawingTextLayout near = DrawingTextLayout.Create(graphics, "abc", font, new SizeF(300, 80), format);
        format.Alignment = StringAlignment.Far;
        format.LineAlignment = StringAlignment.Far;
        DrawingTextLayout far = DrawingTextLayout.Create(graphics, "abc", font, new SizeF(300, 80), format);
        var shift = new Vector2(300 - near.ContentSize.Width, 80 - near.ContentSize.Height);
        Assert.Equal(near.GetCaretStop(0).Position + shift, far.GetCaretStop(0).Position);
        var a = Assert.Single(near.GetSelectionRectangles(0, 3));
        var b = Assert.Single(far.GetSelectionRectangles(0, 3));
        Assert.Equal(a with { X = a.X + shift.X, Y = a.Y + shift.Y }, b);
        var hit = far.HitTestPoint(new PointF(b.X + .1f, b.Y + b.Height / 2));
        Assert.True(hit.IsInside);
        Assert.Equal(0, hit.TextPosition);
    }

    [Fact]
    public void EmptyParagraphCaretUsesActualFontLineHeight()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, "", font, new SizeF(300, 80));
        Assert.Equal(font.GetHeight(graphics), layout.GetCaretStop(0).Height, 4);
        Assert.Equal(layout.GetCaretStop(0).Height, layout.HitTestPoint(PointF.Empty).Bounds.Height);
        Assert.Empty(layout.GetSelectionRectangles(0, 1));
    }

    [Fact]
    public void DpiMismatchRejectsBeforeRecordingAnything()
    {
        var context = new DrawingContext();
        using Graphics first = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using Graphics target = Graphics.FromProGpuDrawingContext(context, new RectangleF(0, 0, 500, 100),
            Matrix4x4.Identity, 192, 192);
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using var brush = new SolidBrush(Color.Black);
        DrawingTextLayout layout = DrawingTextLayout.Create(first, "abc", font, new SizeF(300, 80));
        Assert.Throws<InvalidOperationException>(() => layout.Draw(target, brush, PointF.Empty));
        Assert.Empty(context.Commands);
    }

    [Theory]
    [InlineData("mnemonic")]
    [InlineData("digits")]
    [InlineData("trim")]
    [InlineData("vertical")]
    [InlineData("line-limit")]
    [InlineData("unknown")]
    public void UnsupportedIndexChangingOrUnknownFormatsFailExplicitly(string kind)
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using StringFormat format = MakeFormat();
        switch (kind)
        {
            case "mnemonic": format.HotkeyPrefix = HotkeyPrefix.Show; break;
            case "digits": format.SetDigitSubstitution(0x401, StringDigitSubstitute.National); break;
            case "trim": format.Trimming = StringTrimming.EllipsisCharacter; break;
            case "vertical": format.FormatFlags |= StringFormatFlags.DirectionVertical; break;
            case "line-limit": format.FormatFlags |= StringFormatFlags.LineLimit; break;
            case "unknown": format.FormatFlags |= (StringFormatFlags)0x08000000; break;
        }
        Assert.Throws<NotSupportedException>(() => DrawingTextLayout.Create(graphics, "abc", font, new SizeF(300, 80), format));
        Assert.Empty(context.Commands);
    }

    [Fact]
    public void SourceRowsIncludeBlankRowsAndBothCrLfUnitsWithoutInk()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using StringFormat format = MakeFormat();
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, "a\r\n\r\nb", font, new SizeF(300, 100), format);
        Assert.Equal(3, layout.RowCount);
        Assert.Equal(new[] { 0, 3, 5 }, Enumerable.Range(0, layout.RowCount).Select(layout.GetRowSourceStart));
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 2, 2 }, Enumerable.Range(0, 7).Select(layout.GetRowIndexFromTextPosition));
        Assert.Equal(layout.GetSourcePositionPoint(1), layout.GetSourcePositionPoint(2));
        Assert.Equal(layout.GetSourcePositionPoint(3), layout.GetSourcePositionPoint(4));
        Assert.Empty(layout.GetSelectionRectangles(1, 4));
    }

    [Fact]
    public void SourcePositionsFollowTheSameDrawingAlignmentOffset()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using StringFormat format = MakeFormat();
        format.FormatFlags |= StringFormatFlags.NoWrap;
        const string text = "abc\r\nxy";
        DrawingTextLayout near = DrawingTextLayout.Create(graphics, text, font, new SizeF(300, 100), format);
        format.Alignment = StringAlignment.Far;
        format.LineAlignment = StringAlignment.Far;
        DrawingTextLayout far = DrawingTextLayout.Create(graphics, text, font, new SizeF(300, 100), format);
        var offset = new SizeF(300 - near.ContentSize.Width, 100 - near.ContentSize.Height);
        for (int position = 0; position <= text.Length; position++)
        {
            Assert.Equal(PointF.Add(near.GetSourcePositionPoint(position), offset), far.GetSourcePositionPoint(position));
            Assert.Equal(near.GetRowIndexFromTextPosition(position), far.GetRowIndexFromTextPosition(position));
        }
    }

    [Fact]
    public void SourceRowQueriesPreserveWrappedCaretAffinity()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        using StringFormat format = MakeFormat();
        DrawingTextLayout prefix = DrawingTextLayout.Create(graphics, "aa ", font, new SizeF(300, 100), format);
        float width = prefix.GetCaretStop(3).Position.X + 1;
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, "aa aa aa", font, new SizeF(width, 100), format);
        Assert.Equal(new[] { 0, 3, 6 }, Enumerable.Range(0, layout.RowCount).Select(layout.GetRowSourceStart));
        Assert.Equal(1, layout.GetRowIndexFromTextPosition(3));
        Assert.Equal(0, layout.GetCaretRowIndex(3, true));
        Assert.Equal(1, layout.GetCaretRowIndex(3, false));
    }

    [Fact]
    public void EmptyDrawingLayoutKeepsOneOwnedSourceRow()
    {
        using Graphics graphics = Graphics.FromProGpuDrawingContext(new DrawingContext());
        using var font = new Font(FontFamily.GenericSansSerif, 16);
        DrawingTextLayout layout = DrawingTextLayout.Create(graphics, "", font, new SizeF(300, 100));
        Assert.Equal(1, layout.RowCount);
        Assert.Equal(0, layout.GetRowSourceStart(0));
        Assert.Equal(0, layout.GetRowIndexFromTextPosition(0));
        Assert.Equal(0, layout.GetCaretRowIndex(0));
        var caret = layout.GetCaretStop(0);
        Assert.Equal(new PointF(caret.Position.X, caret.Position.Y), layout.GetSourcePositionPoint(0));
    }

    private static StringFormat MakeFormat()
    {
        var format = new StringFormat(StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces)
            { Trimming = StringTrimming.None };
        format.SetDigitSubstitution(0, StringDigitSubstitute.None);
        return format;
    }
}
