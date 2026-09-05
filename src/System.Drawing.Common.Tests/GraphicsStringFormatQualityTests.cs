using ProGPU.Scene;
using ProGPU.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsStringFormatQualityTests
{
    [Fact]
    public void UnderlineAndStrikeoutAreRecordedAcrossStringDrawingPaths()
    {
        using var font = new Font(
            FontFamily.GenericSansSerif,
            16f,
            FontStyle.Underline | FontStyle.Strikeout);
        using var brush = new SolidBrush(Color.Navy);

        var pointContext = new DrawingContext();
        using (Graphics graphics = Graphics.FromProGpuDrawingContext(pointContext))
        {
            graphics.DrawString("decorated", font, brush, 4f, 6f);
        }
        RenderCommand[] pointDecorations = pointContext.Commands
            .Where(static command => command.Type == RenderCommandType.DrawRect)
            .ToArray();
        Assert.Equal(2, pointDecorations.Length);
        Assert.Equal(pointDecorations[0].Rect.X, pointDecorations[1].Rect.X);
        Assert.Equal(pointDecorations[0].Rect.Width, pointDecorations[1].Rect.Width);
        Assert.True(pointDecorations[0].Rect.Y > pointDecorations[1].Rect.Y);

        var rectangleContext = new DrawingContext();
        using (Graphics graphics = Graphics.FromProGpuDrawingContext(rectangleContext))
        {
            graphics.DrawString("decorated", font, brush, new RectangleF(4f, 6f, 120f, 40f));
        }
        Assert.Equal(
            2,
            rectangleContext.Commands.Count(static command => command.Type == RenderCommandType.DrawRect));

        var formattedContext = new DrawingContext();
        using (Graphics graphics = Graphics.FromProGpuDrawingContext(formattedContext))
        using (var format = StringFormat.GenericTypographic)
        {
            graphics.DrawString(
                "decorated",
                font,
                brush,
                new RectangleF(4f, 6f, 120f, 40f),
                format);
        }
        Assert.Equal(
            2,
            formattedContext.Commands.Count(static command => command.Type == RenderCommandType.DrawRect));
    }

    [Fact]
    public void SpanDrawingAndMeasurementUseCanonicalTypedTextPath()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(
            context,
            new RectangleF(0f, 0f, 240f, 120f));
        using var font = new Font(FontFamily.GenericSansSerif, 16f);
        using var brush = new SolidBrush(Color.Navy);
        using var format = StringFormat.GenericTypographic;
        ReadOnlySpan<char> text = "LibreWinForms";

        SizeF spanSize = graphics.MeasureString(text, font, new SizeF(180f, 80f), format, out int fitted, out int lines);
        SizeF stringSize = graphics.MeasureString(text.ToString(), font, new SizeF(180f, 80f), format);
        graphics.DrawString(text, font, brush, new RectangleF(8f, 10f, 180f, 80f), format);

        Assert.Equal(text.Length, fitted);
        Assert.Equal(1, lines);
        Assert.Equal(stringSize, spanSize);
        Assert.Contains(context.Commands, static command => command.Type == RenderCommandType.DrawGlyphRun);
    }

    [Fact]
    public void MeasurableCharacterRangesUseShapedClustersAcrossWrappedLines()
    {
        using var target = new Bitmap(160, 120);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var format = StringFormat.GenericTypographic;
        const string text = "alpha beta gamma delta";
        format.SetMeasurableCharacterRanges([new CharacterRange(0, text.Length)]);

        Region[] regions = graphics.MeasureCharacterRanges(
            text.AsSpan(),
            font,
            new RectangleF(10f, 12f, 62f, 100f),
            format);

        using Region region = Assert.Single(regions);
        using var identity = new Matrix();
        RectangleF[] scans = region.GetRegionScans(identity);
        RectangleF bounds = region.GetBounds(graphics);
        Assert.True(scans.Length >= 2);
        Assert.True(bounds.Left >= 10f);
        Assert.True(bounds.Top >= 12f);
        Assert.True(bounds.Height > scans[0].Height);
    }

    [Fact]
    public void MeasurableCharacterRangesHonorNoClip()
    {
        using var target = new Bitmap(120, 80);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var clipped = StringFormat.GenericTypographic;
        using var unclipped = StringFormat.GenericTypographic;
        const string text = "unclipped text";
        clipped.FormatFlags = StringFormatFlags.NoWrap;
        unclipped.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        clipped.SetMeasurableCharacterRanges([new CharacterRange(0, text.Length)]);
        unclipped.SetMeasurableCharacterRanges([new CharacterRange(0, text.Length)]);
        var layout = new RectangleF(4f, 6f, 20f, 40f);

        using Region clippedRegion = Assert.Single(graphics.MeasureCharacterRanges(text, font, layout, clipped));
        using Region unclippedRegion = Assert.Single(graphics.MeasureCharacterRanges(text, font, layout, unclipped));

        Assert.True(clippedRegion.GetBounds(graphics).Right <= layout.Right + 0.01f);
        Assert.True(unclippedRegion.GetBounds(graphics).Right > layout.Right);
    }

    [Fact]
    public void EmptySpanMatchesOfficialValidationOrder()
    {
        using var target = new Bitmap(32, 32);
        using Graphics graphics = Graphics.FromImage(target);
        using var brush = new SolidBrush(Color.Black);

        Assert.Equal(SizeF.Empty, graphics.MeasureString(ReadOnlySpan<char>.Empty, null!));
        Assert.Empty(graphics.MeasureCharacterRanges(
            ReadOnlySpan<char>.Empty,
            null!,
            RectangleF.Empty,
            null));
        graphics.DrawString(ReadOnlySpan<char>.Empty, null!, brush, PointF.Empty);
        Assert.Throws<ArgumentNullException>(() =>
            graphics.DrawString(ReadOnlySpan<char>.Empty, null!, null!, PointF.Empty));
    }

    [Fact]
    public void WarmedSpanMeasurementHasBoundedManagedAllocation()
    {
        using var target = new Bitmap(160, 80);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 16f);
        using var format = StringFormat.GenericTypographic;
        char[] text = "LibreWinForms text".ToCharArray();
        _ = graphics.MeasureString(text.AsSpan(), font, new SizeF(140f, 60f), format);

        const int Iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            _ = graphics.MeasureString(text.AsSpan(), font, new SizeF(140f, 60f), format);
        }

        long bytesPerMeasure = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerMeasure, 1_024, 16_384);
    }

    [Fact]
    public void ExplicitTabStopsAdvanceTheFollowingCharacterFromTheLineOrigin()
    {
        using var target = new Bitmap(180, 80);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 16f);
        using var format = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.NoClip);
        format.SetTabStops(10f, [40f]);
        format.SetMeasurableCharacterRanges([new CharacterRange(2, 1)]);

        using Region region = Assert.Single(graphics.MeasureCharacterRanges(
            "A\tB",
            font,
            new RectangleF(3f, 4f, 160f, 50f),
            format));

        Assert.True(region.GetBounds(graphics).Left >= 52.9f);
    }

    [Fact]
    public void MeasureTrailingSpacesControlsMeasuredWidth()
    {
        using var target = new Bitmap(240, 80);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 16f);
        using var compact = new StringFormat(StringFormatFlags.NoWrap);
        using var expanded = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces);

        SizeF compactSize = graphics.MeasureString("A   ", font, new SizeF(220f, 60f), compact);
        SizeF expandedSize = graphics.MeasureString("A   ", font, new SizeF(220f, 60f), expanded);

        Assert.True(expandedSize.Width > compactSize.Width);
        Assert.Equal(compactSize.Height, expandedSize.Height);
    }

    [Fact]
    public void DirectionVerticalUsesTheTypedVerticalShapingPath()
    {
        using var target = new Bitmap(240, 240);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 20f);
        using var horizontal = new StringFormat(StringFormatFlags.NoWrap);
        using var vertical = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.DirectionVertical);

        SizeF horizontalSize = graphics.MeasureString("ABCD", font, new SizeF(220f, 220f), horizontal);
        SizeF verticalSize = graphics.MeasureString("ABCD", font, new SizeF(220f, 220f), vertical);

        Assert.True(horizontalSize.Width > horizontalSize.Height);
        Assert.True(verticalSize.Height > verticalSize.Width);
    }

    [Fact]
    public void DigitSubstitutionShapesCultureNativeDigits()
    {
        var latinContext = new DrawingContext();
        var arabicContext = new DrawingContext();
        using Graphics latinGraphics = Graphics.FromProGpuDrawingContext(latinContext);
        using Graphics arabicGraphics = Graphics.FromProGpuDrawingContext(arabicContext);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var brush = new SolidBrush(Color.Black);
        using var latin = new StringFormat(StringFormatFlags.NoWrap);
        using var arabic = new StringFormat(StringFormatFlags.NoWrap);
        latin.SetDigitSubstitution(0x0C01, StringDigitSubstitute.None);
        arabic.SetDigitSubstitution(0x0C01, StringDigitSubstitute.National);

        latinGraphics.DrawString("123", font, brush, PointF.Empty, latin);
        arabicGraphics.DrawString("123", font, brush, PointF.Empty, arabic);

        ushort[] latinGlyphs = Assert.Single(
            latinContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).GlyphIndices!;
        ushort[] arabicGlyphs = Assert.Single(
            arabicContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).GlyphIndices!;
        Assert.NotEqual(latinGlyphs, arabicGlyphs);
    }

    [Fact]
    public void NoFontFallbackKeepsMissingGlyphsOnTheRequestedFace()
    {
        string fonts = Path.Combine(AppContext.BaseDirectory, "Fonts");
        var requested = new TtfFont(Path.Combine(fonts, "Inter-Regular.ttf"));
        var fallback = new TtfFont(Path.Combine(fonts, "NotoSansCJKjp-Regular.otf"));
        FontApi.RegisterPlatformFallbackFont(fallback);
        using var font = new Font(requested, 18f);
        using var brush = new SolidBrush(Color.Black);
        var fallbackContext = new DrawingContext();
        var requestedContext = new DrawingContext();
        using Graphics fallbackGraphics = Graphics.FromProGpuDrawingContext(fallbackContext);
        using Graphics requestedGraphics = Graphics.FromProGpuDrawingContext(requestedContext);
        using var enabled = new StringFormat(StringFormatFlags.NoWrap);
        using var disabled = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.NoFontFallback);

        fallbackGraphics.DrawString("漢", font, brush, PointF.Empty, enabled);
        requestedGraphics.DrawString("漢", font, brush, PointF.Empty, disabled);

        TtfFont enabledFont = Assert.Single(
            fallbackContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).Font!;
        TtfFont disabledFont = Assert.Single(
            requestedContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).Font!;
        Assert.NotSame(disabledFont, enabledFont);
        Assert.Same(requested, disabledFont);
    }

    [Fact]
    public void DisplayFormatControlRecordsAVisibleRepresentativeGlyph()
    {
        var hiddenContext = new DrawingContext();
        var visibleContext = new DrawingContext();
        using Graphics hiddenGraphics = Graphics.FromProGpuDrawingContext(hiddenContext);
        using Graphics visibleGraphics = Graphics.FromProGpuDrawingContext(visibleContext);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var brush = new SolidBrush(Color.Black);
        using var hidden = new StringFormat(StringFormatFlags.NoWrap);
        using var visible = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.DisplayFormatControl);

        hiddenGraphics.DrawString("A\u200eB", font, brush, PointF.Empty, hidden);
        visibleGraphics.DrawString("A\u200eB", font, brush, PointF.Empty, visible);

        RenderCommand hiddenRun = Assert.Single(
            hiddenContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        RenderCommand visibleRun = Assert.Single(
            visibleContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        Assert.Equal(3, hiddenRun.GlyphIndices!.Length);
        Assert.Equal(3, visibleRun.GlyphIndices!.Length);
        Assert.NotEqual(hiddenRun.GlyphIndices[1], visibleRun.GlyphIndices[1]);
        Assert.NotNull(visibleRun.Font!.GetFlippedGlyphOutline(visibleRun.GlyphIndices[1]));
    }

    [Fact]
    public void ShowHotkeyPrefixRecordsOnlyTheMnemonicUnderline()
    {
        var showContext = new DrawingContext();
        var hideContext = new DrawingContext();
        using Graphics showGraphics = Graphics.FromProGpuDrawingContext(showContext);
        using Graphics hideGraphics = Graphics.FromProGpuDrawingContext(hideContext);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var brush = new SolidBrush(Color.Black);
        using var show = new StringFormat(StringFormatFlags.NoWrap)
        {
            HotkeyPrefix = HotkeyPrefix.Show
        };
        using var hide = new StringFormat(StringFormatFlags.NoWrap)
        {
            HotkeyPrefix = HotkeyPrefix.Hide
        };

        showGraphics.DrawString("Sa&ve && Close", font, brush, PointF.Empty, show);
        hideGraphics.DrawString("Sa&ve && Close", font, brush, PointF.Empty, hide);

        RenderCommand showRun = Assert.Single(
            showContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        RenderCommand hideRun = Assert.Single(
            hideContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun);
        RenderCommand underline = Assert.Single(
            showContext.Commands,
            static command => command.Type == RenderCommandType.DrawRect);
        Assert.Equal(showRun.GlyphIndices, hideRun.GlyphIndices);
        Assert.DoesNotContain(
            hideContext.Commands,
            static command => command.Type == RenderCommandType.DrawRect);
        Assert.True(underline.Rect.Width > 0f);
        Assert.True(underline.Rect.Height >= 1f);
        Assert.True(underline.Rect.X > showRun.GlyphPositions![0].X);
    }

    [Fact]
    public void WarmedMnemonicRecordingHasBoundedManagedAllocation()
    {
        var context = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var brush = new SolidBrush(Color.Black);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            HotkeyPrefix = HotkeyPrefix.Show
        };
        const string Text = "Sa&ve";
        graphics.DrawString(Text, font, brush, PointF.Empty, format);

        const int Iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            context.Commands.Clear();
            graphics.DrawString(Text, font, brush, PointF.Empty, format);
        }

        long bytesPerRecord = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerRecord, 1_024, 24_576);
    }

    [Fact]
    public void LineLimitExcludesAPartiallyVisibleFinalLine()
    {
        using var target = new Bitmap(240, 160);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var singleLine = new StringFormat(StringFormatFlags.NoWrap);
        using var partial = new StringFormat();
        using var whole = new StringFormat(StringFormatFlags.LineLimit);
        float lineHeight = graphics.MeasureString("M", font, new SizeF(200f, 120f), singleLine).Height;
        var layoutArea = new SizeF(58f, lineHeight * 1.5f);
        const string Text = "alpha beta gamma delta epsilon";

        SizeF partialSize = graphics.MeasureString(
            Text,
            font,
            layoutArea,
            partial,
            out int partialCharacters,
            out int partialLines);
        SizeF wholeSize = graphics.MeasureString(
            Text,
            font,
            layoutArea,
            whole,
            out int wholeCharacters,
            out int wholeLines);

        Assert.Equal(2, partialLines);
        Assert.Equal(1, wholeLines);
        Assert.InRange(partialCharacters, wholeCharacters + 1, Text.Length - 1);
        Assert.InRange(wholeCharacters, 1, partialCharacters - 1);
        Assert.True(partialSize.Height > wholeSize.Height);
        Assert.InRange(wholeSize.Height, lineHeight - 0.01f, lineHeight + 0.01f);
    }

    [Fact]
    public void EllipsisPathPreservesTheFinalSlashDelimitedSegment()
    {
        const string Text = "C:/very/long/project/folder/re&port.txt";
        const string Prefix = "C:";
        const string Tail = "/report.txt";
        var context = new DrawingContext();
        var prefixContext = new DrawingContext();
        var tailContext = new DrawingContext();
        var ellipsisContext = new DrawingContext();
        using Graphics graphics = Graphics.FromProGpuDrawingContext(context);
        using Graphics prefixGraphics = Graphics.FromProGpuDrawingContext(prefixContext);
        using Graphics tailGraphics = Graphics.FromProGpuDrawingContext(tailContext);
        using Graphics ellipsisGraphics = Graphics.FromProGpuDrawingContext(ellipsisContext);
        using var font = new Font(FontFamily.GenericSansSerif, 18f);
        using var brush = new SolidBrush(Color.Black);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            HotkeyPrefix = HotkeyPrefix.Show,
            Trimming = StringTrimming.EllipsisPath
        };
        using var measurementFormat = new StringFormat(StringFormatFlags.NoWrap);
        float width = graphics.MeasureString(
            "C:/long…/report.txt",
            font,
            new SizeF(400f, 60f),
            measurementFormat).Width;

        SizeF measured = graphics.MeasureString(
            Text,
            font,
            new SizeF(width, 60f),
            format,
            out int charactersFitted,
            out int linesFilled);
        graphics.DrawString(Text, font, brush, new RectangleF(0f, 0f, width, 60f), format);
        prefixGraphics.DrawString(Prefix, font, brush, PointF.Empty, measurementFormat);
        tailGraphics.DrawString(Tail, font, brush, PointF.Empty, measurementFormat);
        ellipsisGraphics.DrawString("\u2026", font, brush, PointF.Empty, measurementFormat);

        ushort[] actual = Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).GlyphIndices!;
        ushort[] expectedPrefix = Assert.Single(
            prefixContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).GlyphIndices!;
        ushort[] expectedTail = Assert.Single(
            tailContext.Commands,
            static command => command.Type == RenderCommandType.DrawGlyphRun).GlyphIndices!;
        ushort ellipsis = Assert.Single(
            Assert.Single(
                ellipsisContext.Commands,
                static command => command.Type == RenderCommandType.DrawGlyphRun).GlyphIndices!);

        Assert.Equal(expectedPrefix, actual[..expectedPrefix.Length]);
        Assert.Equal(expectedTail, actual[^expectedTail.Length..]);
        Assert.Contains(ellipsis, actual);
        Assert.Single(
            context.Commands,
            static command => command.Type == RenderCommandType.DrawRect);
        Assert.InRange(charactersFitted, Prefix.Length + Tail.Length, Text.Length - 2);
        Assert.Equal(1, linesFilled);
        Assert.True(measured.Width <= width + 0.01f);
    }

    [Fact]
    public void WarmedEllipsisPathMeasurementHasBoundedManagedAllocation()
    {
        using var target = new Bitmap(180, 80);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 16f);
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisPath
        };
        char[] text = "C:/very/long/project/folder/report.txt".ToCharArray();
        _ = graphics.MeasureString(text.AsSpan(), font, new SizeF(160f, 60f), format);

        const int Iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            _ = graphics.MeasureString(text.AsSpan(), font, new SizeF(160f, 60f), format);
        }

        long bytesPerMeasure = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerMeasure, 1_024, 98_304);
    }

    [Fact]
    public void WarmedAdvancedFormatMeasurementHasBoundedManagedAllocation()
    {
        using var target = new Bitmap(180, 80);
        using Graphics graphics = Graphics.FromImage(target);
        using var font = new Font(FontFamily.GenericSansSerif, 16f);
        using var format = new StringFormat(
            StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces);
        format.SetTabStops(8f, [40f, 40f]);
        format.SetDigitSubstitution(0x0C01, StringDigitSubstitute.National);
        char[] text = "A\t123  ".ToCharArray();
        _ = graphics.MeasureString(text.AsSpan(), font, new SizeF(160f, 60f), format);

        const int Iterations = 128;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Iterations; index++)
        {
            _ = graphics.MeasureString(text.AsSpan(), font, new SizeF(160f, 60f), format);
        }

        long bytesPerMeasure = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;
        Assert.InRange(bytesPerMeasure, 1_024, 24_576);
    }
}
