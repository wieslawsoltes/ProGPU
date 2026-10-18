using System.Numerics;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

// Independent placement contracts over authored physical outlines, not source-font parity.
public sealed class HintedIntegerGlyphOriginTests
{
    [Theory]
    [InlineData(1f, .2f, .3f, 1f)]
    [InlineData(2f, .125f, .125f, .5f)]
    [InlineData(2f, -.125f, -.125f, 0f)]
    [InlineData(1.5f, .25f, .25f, 2f / 3f)]
    public void FullFrameOriginRoundsOnceAndRetainedSnapshotKeepsOriginalPlacement(
        float dpi, float position, float translation, float expected)
    {
        using var geometry = Geometry(dpi, new(position, position));
        var context = new DrawingContext();
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(geometry, Vector2.Zero, out Rect ink, out _));
        var originalPlacements = geometry.FontPlacements.ToArray();
        context.DrawHintedGlyphsWithRasterOrigin(geometry, Vector2.Zero, ink,
            new SolidColorBrush(Vector4.One), TextHintingMode.IntegerPixel,
            Matrix4x4.CreateTranslation(translation, translation, 0), hitTestId: 37);
        using var snapshot = context.CreatePictureSnapshot();
        context.Clear();
        var command = snapshot.GetCommand(0);
        Assert.Equal(TextHintingMode.IntegerPixel, command.TextHintingMode);
        Assert.Equal(37, command.HitTestId);
        Assert.Equal(ink, command.Rect);
        Assert.Equal(originalPlacements, geometry.FontPlacements.ToArray());
        Assert.True(HintedGlyphCommandGeometry.TryGetRasterOrigin(command, new(position, position),
            new(translation, translation), out var actual));
        Assert.Equal(new Vector2(expected), actual);
        Assert.True(GpuPictureBounds.TryGetBounds(snapshot, out Rect snapshotBounds));
        Assert.True(HintedGlyphCommandGeometry.TryGetRasterBounds(command, new(translation), out var targetBounds, out _));
        Assert.Equal(targetBounds, snapshotBounds);
    }

    [Theory]
    [InlineData(TextHintingMode.Fixed, TextRenderingMode.Aliased)]
    [InlineData(TextHintingMode.Animated, TextRenderingMode.Aliased)]
    [InlineData((TextHintingMode)99, TextRenderingMode.Aliased)]
    [InlineData(TextHintingMode.IntegerPixel, TextRenderingMode.Grayscale)]
    [InlineData(TextHintingMode.IntegerPixel, TextRenderingMode.ClearType)]
    public void UnsupportedPolicyRejectsBeforePublication(TextHintingMode hint, TextRenderingMode rendering)
    {
        using var geometry = Geometry(2, Vector2.Zero);
        var context = new DrawingContext();
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphsWithRasterOrigin(geometry,
            Vector2.Zero, new(0, -2, 2, 2), new SolidColorBrush(Vector4.One), hint,
            textRenderingMode: rendering));
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void LateNoInkPhysicalOverflowRejectsWholeRangeButNotIndependentSelectedRange()
    {
        using var geometry = Geometry(2, Vector2.Zero, new(float.MaxValue, 0));
        var context = new DrawingContext();
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphsWithRasterOrigin(geometry,
            Vector2.Zero, new(0, -2, 2, 2), new SolidColorBrush(Vector4.One), TextHintingMode.IntegerPixel));
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
        context.DrawHintedGlyphsRangeWithRasterOrigin(geometry, 0, 1,
            Vector2.Zero, new(0, -2, 2, 2), new SolidColorBrush(Vector4.One), TextHintingMode.IntegerPixel);
        Assert.Single(context.Commands);
        context.Clear();
    }

    [Fact]
    public void OriginalOverloadKeepsFractionalPaintAndExplicitEmptyRangeStaysEmpty()
    {
        using var geometry = Geometry(2, new(.125f));
        var context = new DrawingContext();
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(geometry, Vector2.Zero, out Rect ink, out _));
        context.DrawHintedGlyphs(geometry, Vector2.Zero, ink, new SolidColorBrush(Vector4.One),
            textRenderingMode: TextRenderingMode.Aliased);
        var command = Assert.Single(context.Commands);
        Assert.Equal(TextHintingMode.Auto, command.TextHintingMode);
        Assert.True(HintedGlyphCommandGeometry.TryGetRasterOrigin(command, new(.125f), new(.125f), out var origin));
        Assert.Equal(new Vector2(.25f), origin);
        context.Clear();
        context.DrawHintedGlyphsRangeWithRasterOrigin(geometry, 1, 0, Vector2.Zero,
            Rect.Empty, new SolidColorBrush(Vector4.One), TextHintingMode.IntegerPixel);
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    private static HintedGlyphGeometry Geometry(float dpi, Vector2 position, Vector2? noInk = null)
    {
        FontGlyphPlacement[] placements = noInk.HasValue
            ? [new(0, 1, 0, position), new(1, 2, uint.MaxValue, noInk.Value)]
            : [new(0, 1, 0, position)];
        return HintedGlyphGeometry.FromFontPlacements(dpi,
            [new GpuGlyphRecord { SegmentCount = 4, MinX = 0, MinY = 0, MaxX = 4, MaxY = 4 }],
            [Line(new(0, 0), new(4, 0)), Line(new(4, 0), new(4, 4)),
             Line(new(4, 4), new(0, 4)), Line(new(0, 4), new(0, 0))], placements, new Owner());
    }
    private static GpuSegment Line(Vector2 a, Vector2 b) => new() { P0 = a, P1 = b };
    private sealed class Owner : IDisposable { public void Dispose() { } }
}
