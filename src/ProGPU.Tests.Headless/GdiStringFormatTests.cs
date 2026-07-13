using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Text;
using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public class GdiStringFormatTests
{
    [Fact]
    public void DefaultsAndGenericFormatsMatchSystemDrawingContract()
    {
        using var format = new StringFormat(StringFormatFlags.DirectionRightToLeft, 2057);

        Assert.Equal(StringAlignment.Near, format.Alignment);
        Assert.Equal(0, format.DigitSubstitutionLanguage);
        Assert.Equal(StringDigitSubstitute.User, format.DigitSubstitutionMethod);
        Assert.Equal(StringFormatFlags.DirectionRightToLeft, format.FormatFlags);
        Assert.Equal(HotkeyPrefix.None, format.HotkeyPrefix);
        Assert.Equal(StringAlignment.Near, format.LineAlignment);
        Assert.Equal(StringTrimming.Character, format.Trimming);

        using var genericDefault = StringFormat.GenericDefault;
        using var genericDefaultAgain = StringFormat.GenericDefault;
        Assert.NotSame(genericDefault, genericDefaultAgain);
        Assert.Equal((StringFormatFlags)0, genericDefault.FormatFlags);
        Assert.Equal(StringTrimming.Character, genericDefault.Trimming);

        using var typographic = StringFormat.GenericTypographic;
        Assert.Equal(
            StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip,
            typographic.FormatFlags);
        Assert.Equal(StringTrimming.None, typographic.Trimming);
    }

    [Fact]
    public void CloneCopiesStateAndOwnedArraysIndependently()
    {
        using var original = new StringFormat(StringFormatFlags.NoClip, -1)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Far,
            HotkeyPrefix = HotkeyPrefix.Hide,
            Trimming = StringTrimming.EllipsisWord
        };
        original.SetDigitSubstitution(2057, StringDigitSubstitute.National);
        original.SetTabStops(3f, [4f, 8f]);
        original.SetMeasurableCharacterRanges([new CharacterRange(1, 2)]);

        using var clone = Assert.IsType<StringFormat>(original.Clone());
        float[] cloneStops = clone.GetTabStops(out float cloneFirstTab);
        cloneStops[0] = 100f;
        original.FormatFlags = StringFormatFlags.NoFontFallback;

        Assert.Equal(StringAlignment.Center, clone.Alignment);
        Assert.Equal(StringAlignment.Far, clone.LineAlignment);
        Assert.Equal(HotkeyPrefix.Hide, clone.HotkeyPrefix);
        Assert.Equal(StringTrimming.EllipsisWord, clone.Trimming);
        Assert.Equal(StringFormatFlags.NoClip, clone.FormatFlags);
        Assert.Equal(2057, clone.DigitSubstitutionLanguage);
        Assert.Equal(StringDigitSubstitute.National, clone.DigitSubstitutionMethod);
        Assert.Equal(3f, cloneFirstTab);
        Assert.Equal([4f, 8f], clone.GetTabStops(out _));
    }

    [Fact]
    public void TabStopsRangesValidationAndDisposeMatchPublicSurface()
    {
        using var format = new StringFormat();
        format.SetTabStops(10f, [1f, 2.5f, float.PositiveInfinity, float.NaN]);
        float[] stops = format.GetTabStops(out float firstTab);

        Assert.Equal(10f, firstTab);
        Assert.Equal(4, stops.Length);
        Assert.True(float.IsPositiveInfinity(stops[2]));
        Assert.True(float.IsNaN(stops[3]));
        Assert.Throws<ArgumentException>(() => format.SetTabStops(-1f, []));
        Assert.Throws<NotImplementedException>(() => format.SetTabStops(0f, [float.NegativeInfinity]));
        Assert.Throws<OverflowException>(() => format.SetMeasurableCharacterRanges(new CharacterRange[33]));
        Assert.Equal(new CharacterRange(1, 2), new CharacterRange(1, 2));

        var disposed = new StringFormat();
        disposed.Dispose();
        disposed.Dispose();
        Assert.Throws<ArgumentException>(() => _ = disposed.Alignment);
        Assert.Throws<ArgumentException>(() => disposed.Clone());
    }

    [Fact]
    public void InvalidAlignmentTrimmingAndHotkeyValuesAreRejected()
    {
        using var format = new StringFormat();

        Assert.Throws<InvalidEnumArgumentException>(() => format.Alignment = (StringAlignment)(-1));
        Assert.Throws<InvalidEnumArgumentException>(() => format.LineAlignment = (StringAlignment)3);
        Assert.Throws<InvalidEnumArgumentException>(() => format.Trimming = (StringTrimming)6);
        Assert.Throws<InvalidEnumArgumentException>(() => format.HotkeyPrefix = (HotkeyPrefix)3);

        format.FormatFlags = (StringFormatFlags)(-1);
        Assert.Equal((StringFormatFlags)(-1), format.FormatFlags);
    }

    [Fact]
    public void ClassDiagramEllipsisCharacterDrawsSingleClippedGlyphRun()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var brush = new SolidBrush(Color.Black);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
        Font font = SystemFonts.DefaultFont;
        float lineHeight = graphics.MeasureString("M", font).Height;
        float width = graphics.MeasureString("Class", font).Width;
        var bounds = new RectangleF(4f, 6f, width, lineHeight + 0.1f);

        graphics.DrawString("Class diagram node with a long name", font, brush, bounds, format);

        Assert.Equal(RenderCommandType.PushClip, context.Commands[0].Type);
        RenderCommand run = Assert.Single(context.Commands, command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(RenderCommandType.PopClip, context.Commands[^1].Type);
        Assert.NotNull(run.Font);
        Assert.NotNull(run.GlyphIndices);
        Assert.Equal(run.Font!.GetGlyphIndex('\u2026'), run.GlyphIndices![^1]);
        Assert.Equal(new Vector2(bounds.X, bounds.Y), run.Position);
    }

    [Fact]
    public void ReportingAlignmentUsesPositionedGlyphsAndVerticalBlockOffset()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var brush = new SolidBrush(Color.Black);
        using var format = StringFormat.GenericTypographic;
        format.FormatFlags = StringFormatFlags.LineLimit;
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Far;
        Font font = SystemFonts.DefaultFont;
        var bounds = new RectangleF(10f, 20f, 160f, 70f);

        graphics.DrawString("Report title", font, brush, bounds, format);

        Assert.Equal(RenderCommandType.PushClip, context.Commands[0].Type);
        RenderCommand run = Assert.Single(context.Commands, command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(RenderCommandType.PopClip, context.Commands[^1].Type);
        Assert.True(run.GlyphPositions![0].X > 0f);
        Assert.True(run.Position.Y > bounds.Y);
    }

    [Fact]
    public void NoClipSuppressesClipCommandsAndMeasureReportsFittedContent()
    {
        var context = new DrawingContext();
        using var graphics = Graphics.FromProGpuDrawingContext(context);
        using var brush = new SolidBrush(Color.Black);
        using var format = new StringFormat(StringFormatFlags.NoClip | StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter
        };
        Font font = SystemFonts.DefaultFont;
        float lineHeight = graphics.MeasureString("M", font).Height;
        var area = new SizeF(graphics.MeasureString("Short", font).Width, lineHeight + 0.1f);

        SizeF measured = graphics.MeasureString(
            "A much longer report heading",
            font,
            area,
            format,
            out int charactersFitted,
            out int linesFilled);
        graphics.DrawString("A much longer report heading", font, brush, new RectangleF(0f, 0f, area.Width, area.Height), format);

        Assert.DoesNotContain(context.Commands, command => command.Type is RenderCommandType.PushClip or RenderCommandType.PopClip);
        Assert.InRange(measured.Width, 0f, area.Width);
        Assert.InRange(charactersFitted, 1, "A much longer report heading".Length - 1);
        Assert.Equal(1, linesFilled);
    }
}
