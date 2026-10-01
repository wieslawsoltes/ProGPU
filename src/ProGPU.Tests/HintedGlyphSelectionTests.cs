using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

// Synthetic records qualify selection/recording ownership, not native font or
// raster execution. The native factory remains the only public producer.
public sealed class HintedGlyphSelectionTests
{
    private sealed class Owner : IDisposable
    {
        internal int Calls;
        internal int Failures;
        public void Dispose()
        {
            Calls++;
            if (Failures-- > 0) throw new InvalidOperationException("original selected owner fault");
        }
    }

    private static HintedGlyphGeometry Create(Owner owner) => new(2f,
        [new GpuGlyphRecord { SegmentCount = 1, MinX = -2, MinY = -4, MaxX = 6, MaxY = 8 }],
        [new GpuSegment { P0 = new(-2, -4), P1 = new(6, 8), SegmentType = 1 }],
        [new(0, 5, 11, 0, 2, 1, 0, 0, 8, 9, 1, new(10, 20), new(4, 0)),
         new(1, 4, 32, 0, 2, 0, 1, uint.MaxValue, 7, 8, 1, new(14, 20), new(2, 0)),
         new(2, 3, 11, 0, 1, 0, 0, 0, 6, 7, 1, new(16, 20), new(4, 0))], owner);

    [Fact]
    public void SelectionPreservesEveryOriginalFieldAndSharesPhysicalStorage()
    {
        var owner = new Owner();
        using var original = Create(owner);
        int[] indices = [2, 1, 0, 2];
        using var selected = original.SelectOccurrences(indices);
        Array.Fill(indices, -1);
        Assert.Equal(4, selected.OccurrenceCount);
        Assert.Equal(original.DpiScale, selected.DpiScale);
        Assert.Equal(original.Occurrences[2], selected.Occurrences[0]);
        Assert.Equal(original.Occurrences[1], selected.Occurrences[1]);
        Assert.Equal(original.Occurrences[0], selected.Occurrences[2]);
        Assert.Equal(original.Occurrences[2], selected.Occurrences[3]);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(original.Outlines),
            ref MemoryMarshal.GetReference(selected.Outlines)));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(original.Segments),
            ref MemoryMarshal.GetReference(selected.Segments)));
        Assert.Equal(0, owner.Calls);
    }

    [Fact]
    public void NestedSelectionsIndexTheirOwnViewWithoutRenumberingOriginalIdentity()
    {
        var owner = new Owner();
        using var original = Create(owner);
        using var first = original.SelectOccurrences([2, 0, 1, 2]);
        using var second = first.SelectOccurrences([3, 1, 3]);
        original.Dispose();
        first.Dispose();
        Assert.Equal(new uint[] { 2, 0, 2 }, second.Occurrences.ToArray().Select(value => value.PositionedIndex));
        Assert.Equal(0, owner.Calls);
        second.Dispose();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void SiblingSelectionsAndRecordedPicturesKeepOneProducerAlive()
    {
        var owner = new Owner();
        using var original = Create(owner);
        using var first = original.SelectOccurrences([2, 1, 2]);
        using var sibling = original.SelectOccurrences([0]);
        var context = new DrawingContext();
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(first, new(3, 5), out Rect bounds, out bool ink));
        Assert.True(ink);
        Assert.Equal(new Rect(18, 21, 4, 6), bounds);
        context.DrawHintedGlyphs(first, new(3, 5), bounds, new SolidColorBrush(Vector4.One),
            default, TextRenderingMode.Grayscale, hitTestId: 117);
        using GpuPicture picture = context.CreatePictureSnapshot();
        original.Dispose();
        first.Dispose();
        sibling.Dispose();
        context.Clear();
        Assert.Equal(0, owner.Calls);
        Assert.Equal(117, picture.GetCommand(0).HitTestId);
        Assert.Same(first, picture.GetCommand(0).HintedGlyphGeometry);
        Assert.Equal(new uint[] { 2, 1, 2 }, first.RenderOccurrences.ToArray().Select(value => value.PositionedIndex));
        picture.Dispose();
        Assert.Equal(1, owner.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void LateInvalidSelectionCannotPublishAnOwner(int invalid)
    {
        var owner = new Owner();
        using var original = Create(owner);
        Assert.Throws<ArgumentOutOfRangeException>(() => original.SelectOccurrences([2, 1, invalid]));
        Assert.Equal(3, original.OccurrenceCount);
        Assert.Equal(0, owner.Calls);
        original.Dispose();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void EmptySelectionIsAnOwnedEmptyViewNotTheWholeParagraph()
    {
        var owner = new Owner();
        using var original = Create(owner);
        using var empty = original.SelectOccurrences([]);
        original.Dispose();
        Assert.Equal(0, empty.OccurrenceCount);
        Assert.Equal(0, owner.Calls);
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(empty, Vector2.Zero, out Rect bounds, out bool ink));
        Assert.False(ink);
        Assert.Equal(default, bounds);
        var context = new DrawingContext();
        context.DrawHintedGlyphs(empty, Vector2.Zero, bounds, new SolidColorBrush(Vector4.One),
            default, TextRenderingMode.Grayscale, hitTestId: 5);
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
        empty.Dispose();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void FailedSelectedRetirementKeepsTheOriginalOwnerForRetry()
    {
        var owner = new Owner { Failures = 1 };
        using var original = Create(owner);
        using var selected = original.SelectOccurrences([2, 1]);
        original.Dispose();
        Assert.Equal("original selected owner fault", Assert.Throws<InvalidOperationException>(selected.Dispose).Message);
        Assert.True(selected.IsDisposed);
        Assert.False(selected.HasRenderStorage);
        Assert.Throws<ObjectDisposedException>(() => selected.SelectOccurrences([]));
        selected.Dispose();
        original.Dispose();
        Assert.Equal(2, owner.Calls);
    }

    [Fact]
    public void DisposedParentRejectsNewSelectionsWhileItsExistingViewStaysLive()
    {
        using var original = Create(new Owner());
        using var selected = original.SelectOccurrences([0]);
        original.Dispose();
        Assert.Throws<ObjectDisposedException>(() => original.SelectOccurrences([]));
        Assert.Throws<ObjectDisposedException>(() => DrawingContext.TryGetHintedGlyphInkBounds(original,
            Vector2.Zero, out _, out _));
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(selected, Vector2.Zero, out _, out bool ink));
        Assert.True(ink);
    }

    [Fact]
    public void NoInkSelectionDoesNotUseAdvancesOrInventOutlineBounds()
    {
        using var original = Create(new Owner());
        using var selected = original.SelectOccurrences([1, 1]);
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(selected, new(17, 29), out Rect bounds, out bool ink));
        Assert.False(ink);
        Assert.Equal(default, bounds);
    }

    [Fact]
    public void BoundsFailureDoesNotPublishEarlierInk()
    {
        using var invalid = new HintedGlyphGeometry(2f,
            [new GpuGlyphRecord { SegmentCount = 1, MinX = 0, MinY = 0, MaxX = 2, MaxY = 2 }],
            [new GpuSegment { P0 = Vector2.Zero, P1 = new(2, 2), SegmentType = 0 }],
            [new(0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, Vector2.Zero, Vector2.One),
             new(1, 1, 1, 0, 0, 1, 0, 0, 1, 2, 0, new(float.NaN, 0), Vector2.One)],
            new Owner());
        Assert.False(DrawingContext.TryGetHintedGlyphInkBounds(invalid, Vector2.Zero, out Rect bounds, out bool ink));
        Assert.False(ink);
        Assert.Equal(default, bounds);
    }

    [Fact]
    public void ExplicitRangeRecordsTheOriginalHitOwnerAndFailedRangePublishesNothing()
    {
        using var original = Create(new Owner());
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphsRange(original, 2, 2,
            Vector2.Zero, new(9, 16, 10, 6), brush, default, TextRenderingMode.Grayscale, hitTestId: 19));
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
        context.DrawHintedGlyphsRange(original, 2, 1, Vector2.Zero,
            new(15, 16, 4, 6), brush, default, TextRenderingMode.Grayscale, hitTestId: 19);
        using GpuPicture picture = context.CreatePictureSnapshot();
        Assert.Equal(19, picture.GetCommand(0).HitTestId);
        Assert.Equal(2, picture.GetCommand(0).GlyphRangeStart);
        Assert.Equal(1, picture.GetCommand(0).GlyphRangeCount);
        context.Clear();
    }

    [Fact]
    public void OriginalDefaultTransformCallRemainsUnambiguousAndHasNoHitOwner()
    {
        using var original = Create(new Owner());
        var context = new DrawingContext();
        var brush = new SolidColorBrush(Vector4.One);
        context.DrawHintedGlyphs(original, Vector2.Zero, new(9, 16, 10, 6), brush, default);
        context.DrawHintedGlyphsRange(original, 2, 1, Vector2.Zero, new(15, 16, 4, 6), brush, default);
        Assert.All(context.Commands, command => Assert.Equal(0, command.HitTestId));
        context.Clear();
    }

    [Theory]
    [InlineData(1.25f, 9f / 64f)]
    [InlineData(1.5f, 5f / 64f)]
    [InlineData(1.75f, 3f / 64f)]
    [InlineData(3f, 5f / 64f)]
    public void PhysicalInkUsesTheOriginalDivisionAtFractionalDpi(float dpi, float physical)
    {
        using var geometry = new HintedGlyphGeometry(dpi,
            [new GpuGlyphRecord { SegmentCount = 1, MinX = 0, MinY = -physical, MaxX = physical, MaxY = 0 }],
            [new GpuSegment { P0 = new(0, -physical), P1 = new(physical, 0), SegmentType = 0 }],
            [new(0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, Vector2.Zero, Vector2.One)], new Owner());
        // Use captured runtime fields: do not let constant folding substitute
        // double arithmetic for either independently evaluated float route.
        float value = geometry.Outlines[0].MaxX;
        float original = value / geometry.DpiScale;
        float reciprocalProduct = value * (1f / geometry.DpiScale);
        Assert.NotEqual(original, reciprocalProduct);
        Assert.True(DrawingContext.TryGetHintedGlyphInkBounds(geometry, Vector2.Zero, out Rect bounds, out bool ink));
        Assert.True(ink);
        Assert.Equal(new Rect(0, 0, original, original), bounds);
    }
}
