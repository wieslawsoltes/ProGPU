using System.Drawing.Text;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.SystemDrawing;
using Xunit;

namespace System.Drawing.Tests;

public sealed class DrawingTextLayoutTests
{
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

    private static StringFormat MakeFormat()
    {
        var format = new StringFormat(StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces)
            { Trimming = StringTrimming.None };
        format.SetDigitSubstitution(0, StringDigitSubstitute.None);
        return format;
    }
}
