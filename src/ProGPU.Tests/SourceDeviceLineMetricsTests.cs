using System.Numerics;
using System.Text.Json;
using ProGPU.Text;
using ProGPU.Text.Shaping;
using Xunit;

namespace ProGPU.Tests;

public partial class SfntFontFaceTests
{

    [Fact]
    public void SourceDeviceLinesMatchAllExactOriginalWindowsMetricRecords()
    {
        using Stream input = typeof(SfntFontFaceTests).Assembly.GetManifestResourceStream("SourceDeviceLines.metrics.json")!;
        using JsonDocument document = JsonDocument.Parse(input);
        int matched = 0, absent = 0;
        foreach (JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (!item.GetProperty("tables").TryGetProperty("VDMX", out JsonElement raw))
            {
                Assert.Equal("Consolas", item.GetProperty("family").GetString());
                absent++;
                continue;
            }
            byte[] table = Convert.FromHexString(raw.GetString()!);
            ushort size = checked((ushort)-item.GetProperty("descriptor").GetProperty("Height").GetInt32());
            Assert.True(SfntVerticalDeviceMetrics.TryRead(table, size, 1, 1, out var metrics, out bool found));
            Assert.True(found);
            JsonElement native = item.GetProperty("metrics");
            Assert.Equal(native.GetProperty("Height").GetInt32(), metrics.Height);
            Assert.Equal(native.GetProperty("Ascent").GetInt32(), metrics.MaximumY);
            Assert.Equal(-native.GetProperty("Descent").GetInt32(), metrics.MinimumY);
            matched++;
        }
        Assert.Equal(72, matched);
        Assert.Equal(18, absent);
    }

    [Theory]
    [InlineData(ShapingDirection.LeftToRight)]
    [InlineData(ShapingDirection.RightToLeft)]
    public void SourceDeviceLinesKeepTheExplicitParagraphDirectionAndSelectionRows(ShapingDirection direction)
    {
        var (font, metrics) = SourceLineFont();
        var source = new TextLayout("AA\nA", font, 12, shapingOptions: new() { Direction = direction },
            formattingOptions: new() { SourceDeviceLineMetrics = metrics });
        var ordinary = new TextLayout("AA\nA", font, 12, shapingOptions: new() { Direction = direction });
        Assert.Equal(ordinary.Glyphs.Select(g => (g.GlyphIndex, g.Cluster, g.BidiLevel, g.Position.X, g.Glyph.Advance)),
            source.Glyphs.Select(g => (g.GlyphIndex, g.Cluster, g.BidiLevel, g.Position.X, g.Glyph.Advance)));
        Assert.Equal(30, source.ContentSize.Y);
        Assert.All(source.GetSelectionRectangles(0, 4), box => Assert.Equal(15, box.Height));
        Assert.Equal(15, source.GetCaretStop(3).Position.Y);
    }

    private static (TtfFont Font, SourceDeviceLineMetrics Metrics) SourceLineFont()
    {
        var font = new TtfFont(VdmxFont(("VDMX", VdmxTable())));
        Assert.True(SourceDeviceLineMetrics.TryCreate(font, 12, out var metrics));
        return (font, metrics);
    }

    [Fact]
    public void SourceDeviceLinesOwnTheExactFontAndOriginalSignedRecord()
    {
        var (font, metrics) = SourceLineFont();
        Assert.Same(font, metrics.Font);
        Assert.Equal(new FontVerticalDeviceMetrics(12, 12, -3), metrics.Metrics);
        Assert.Equal(15, metrics.Height);
        Assert.Equal(12, metrics.Ascent);
        Assert.Equal(800, font.Ascender);
        Assert.Equal(-200, font.Descender);
        Assert.Equal(50, font.LineGap);
        byte[] table = VdmxTable();
        VdmxU16(table, 40, -2); VdmxU16(table, 42, -17);
        Assert.True(SourceDeviceLineMetrics.TryCreate(new TtfFont(VdmxFont(("VDMX", table))), 12, out var signed));
        Assert.Equal(-2, signed.Ascent);
        Assert.Equal(15, signed.Height);
    }

    [Fact]
    public void SourceDeviceLinesDoNotInventMissingOrVariableRecords()
    {
        var (font, _) = SourceLineFont();
        Assert.False(SourceDeviceLineMetrics.TryCreate(font, 13, out var missing));
        Assert.Null(missing);
        Assert.False(SourceDeviceLineMetrics.TryCreate(new TtfFont(VdmxFont()), 12, out missing));
        Assert.Null(missing);
        Assert.False(SourceDeviceLineMetrics.TryCreate(new TtfFont(VdmxFont(("VDMX", VdmxTable()), ("fvar", []))), 12, out missing));
        Assert.Null(missing);
        Assert.Throws<InvalidDataException>(() => SourceDeviceLineMetrics.TryCreate(new TtfFont(VdmxFont(("VDMX", new byte[4]))), 12, out _));
    }

    [Theory]
    [InlineData("A", float.PositiveInfinity)]
    [InlineData("A\r\n\r\nA", float.PositiveInfinity)]
    [InlineData("AAAA", 1f)]
    public void SourceDeviceLinesShareOriginalGlyphsAdvancesAndOwnedRowFrames(string text, float width)
    {
        var (font, metrics) = SourceLineFont();
        var ordinary = new TextLayout(text, font, 12, width);
        var source = new TextLayout(text, font, 12, width,
            formattingOptions: new() { SourceDeviceLineMetrics = metrics });
        Assert.Equal(ordinary.Glyphs.Count, source.Glyphs.Count);
        Assert.Equal(ordinary.ContentSize.X, source.ContentSize.X);
        float originalHeight = (font.Ascender - font.Descender + font.LineGap) * (12f / font.UnitsPerEm);
        float originalAscent = font.Ascender * (12f / font.UnitsPerEm);
        for (int i = 0; i < source.Glyphs.Count; i++)
        {
            var before = ordinary.Glyphs[i]; var after = source.Glyphs[i];
            Assert.Same(before.Font, after.Font);
            Assert.Equal(before.GlyphIndex, after.GlyphIndex);
            Assert.Equal(before.Cluster, after.Cluster);
            Assert.Equal(before.BidiLevel, after.BidiLevel);
            Assert.Equal(before.Position.X, after.Position.X);
            Assert.Equal(before.Glyph.Advance, after.Glyph.Advance);
            int row = (int)MathF.Round((before.Position.Y - originalAscent) / originalHeight);
            Assert.Equal(row * 15 + 12, after.Position.Y);
            Assert.Equal(15f, source.GetCaretStop(after.Cluster).Height);
            Assert.Equal(row * 15, source.GetCaretStop(after.Cluster).Position.Y);
        }
        var snapshot = source.CreateInteractionSnapshot();
        var carets = snapshot.CaretStops.ToArray();
        source.GenerateLayout(null);
        Assert.Equal(carets, source.GetVisualCaretStops());
        source.ClearForReuse();
        Assert.Equal(carets, snapshot.CaretStops.ToArray());
    }

    [Theory]
    [InlineData(13f)]
    [InlineData(12.5f)]
    [InlineData(float.NaN)]
    public void SourceDeviceLinesRejectDifferentEmBeforeReplacingAReusableGeneration(float size)
    {
        var (font, metrics) = SourceLineFont();
        var layout = new TextLayout("A", font, 12);
        var glyphs = layout.Glyphs.ToArray();
        Assert.Throws<ArgumentException>(() => layout.Reset("changed", font, size, 100,
            TextAlignment.Left, null, null, new() { SourceDeviceLineMetrics = metrics }));
        Assert.Equal("A", layout.Text);
        Assert.Equal(glyphs, layout.Glyphs);
        Assert.Null(layout.FormattingOptions.SourceDeviceLineMetrics);
    }

    [Fact]
    public void SourceDeviceLinesRejectDifferentOwnerAndVerticalDirection()
    {
        var (font, metrics) = SourceLineFont();
        var other = new TtfFont(VdmxFont(("VDMX", VdmxTable())));
        Assert.Throws<ArgumentException>(() => new TextLayout("A", other, 12,
            formattingOptions: new() { SourceDeviceLineMetrics = metrics }));
        Assert.Throws<ArgumentException>(() => new TextLayout("A", font, 12,
            shapingOptions: new() { Direction = ShapingDirection.TopToBottom },
            formattingOptions: new() { SourceDeviceLineMetrics = metrics }));
    }

    [Fact]
    public void SourceDeviceEmptyTextRetainsARealCaretBandWithoutInventedMeasuredContent()
    {
        var (font, metrics) = SourceLineFont();
        var layout = new TextLayout("", font, 12,
            formattingOptions: new() { SourceDeviceLineMetrics = metrics });
        Assert.Equal(Vector2.Zero, layout.ContentSize);
        Assert.Equal(Vector2.Zero, layout.MeasuredSize);
        Assert.Empty(layout.Glyphs);
        Assert.Equal(new TextCaretStop(0, false, Vector2.Zero, 15, 0), layout.GetCaretStop(0));
        Assert.Equal(layout.GetCaretStop(0), layout.CreateInteractionSnapshot().GetCaretStop(0));
    }
}
