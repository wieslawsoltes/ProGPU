using ProGPU.Scene;
using System.Drawing.Drawing2D;
using Xunit;

namespace System.Drawing.Tests;

public sealed class GraphicsStringFormatQualityTests
{
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
}
