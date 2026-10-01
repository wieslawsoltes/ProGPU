using System.Numerics;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

// Synthetic neutral records exercise ownership/recording contracts, not actual
// font execution, Windows hinting parity or a manufactured native paragraph.
public sealed class HintedGlyphGeometryRecordingTests
{
    private static readonly Brush White = new SolidColorBrush(Vector4.One);

    private static HintedGlyphGeometry Create(IDisposable owner, float dpi = 2f) => new(dpi,
        [new GpuGlyphRecord { StartSegment = 0, SegmentCount = 1, MinX = -2, MinY = -4, MaxX = 6, MaxY = 8 }],
        [new GpuSegment { P0 = new(-2, -4), P1 = new(6, 8), SegmentType = 1 }],
        [new(0, 0, 11, 0, 0, 0, 0, 0, 0, 1, 0, new(10, 20), new(4, 0)),
         new(1, 1, 32, 0, 0, 1, 1, uint.MaxValue, 1, 2, 0, new(14, 20), new(2, 0)),
         new(2, 2, 11, 0, 0, 2, 0, 0, 2, 3, 0, new(16, 20), new(4, 0))], owner);

    private sealed class Owner : IDisposable
    {
        internal int Calls;
        internal int Failures;
        internal Action? DuringDispose;
        public void Dispose()
        {
            Calls++;
            DuringDispose?.Invoke();
            if (Failures-- > 0) throw new InvalidOperationException("original retirement fault");
        }
    }

    [Fact]
    public void OriginalOccurrenceAndPhysicalGeometryStayExact()
    {
        var owner = new Owner();
        using HintedGlyphGeometry geometry = Create(owner);
        Assert.Equal(3, geometry.OccurrenceCount);
        Assert.Equal(uint.MaxValue, geometry.Occurrences[1].OutlineIndex);
        Assert.Equal(geometry.Occurrences[0].OutlineIndex, geometry.Occurrences[2].OutlineIndex);
        Assert.Equal(new Vector2(16, 20), geometry.Occurrences[2].Position);
        Assert.Equal(8f, geometry.Outlines[0].MaxY);
        Assert.Equal(new Vector2(6, 8), geometry.Segments[0].P1);
        Assert.Equal(0, owner.Calls);
    }

    [Fact]
    public void HeldUseSurvivesWrapperDisposalAndEndsOnce()
    {
        var owner = new Owner();
        HintedGlyphGeometry geometry = Create(owner);
        IDisposable lease = geometry.RetainForRecording();
        geometry.Dispose();
        Assert.True(geometry.IsDisposed);
        Assert.True(geometry.HasRenderStorage);
        Assert.Equal(3, geometry.RenderOccurrences.Length);
        Assert.Equal(0, owner.Calls);
        Assert.Throws<ObjectDisposedException>(() => geometry.RetainForRecording());
        Assert.Throws<ObjectDisposedException>(() => { _ = geometry.Occurrences.Length; });
        lease.Dispose();
        lease.Dispose();
        geometry.Dispose();
        Assert.Equal(1, owner.Calls);
        Assert.False(geometry.HasRenderStorage);
    }

    [Fact]
    public void FailedLastUseRetirementRetriesWithoutDecrementingAgain()
    {
        var owner = new Owner { Failures = 1 };
        HintedGlyphGeometry geometry = Create(owner);
        IDisposable lease = geometry.RetainForRecording();
        geometry.Dispose();
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(lease.Dispose);
        Assert.Equal("original retirement fault", failure.Message);
        Assert.False(geometry.HasRenderStorage);
        Assert.Throws<ObjectDisposedException>(() => { _ = geometry.RenderOutlines.Length; });
        lease.Dispose();
        geometry.Dispose();
        Assert.Equal(2, owner.Calls);
    }

    [Fact]
    public void ReentrantRetirementCannotExecuteTheOwnerTwice()
    {
        var owner = new Owner();
        HintedGlyphGeometry geometry = Create(owner);
        owner.DuringDispose = geometry.Dispose;
        geometry.Dispose();
        Assert.Equal(1, owner.Calls);
        Assert.False(geometry.HasRenderStorage);
    }

    [Fact]
    public void ContextAndIndependentSnapshotsShareOneOriginalOwner()
    {
        var owner = new Owner();
        HintedGlyphGeometry geometry = Create(owner);
        var context = new DrawingContext();
        Rect ink = new(9, 16, 10, 6);
        context.DrawHintedGlyphs(geometry, Vector2.Zero, ink, White);
        context.DrawHintedGlyphsRange(geometry, 2, 1, Vector2.Zero, ink, White);
        Assert.Equal(1, context.RetainedResourceCount);
        GpuPicture first = context.CreatePictureSnapshot();
        GpuPicture second = context.CreatePictureSnapshot();
        geometry.Dispose();
        context.Clear();
        Assert.Equal(0, owner.Calls);
        Assert.Same(geometry, first.GetCommand(0).HintedGlyphGeometry);
        Assert.Equal(2, second.GetCommand(1).GlyphRangeStart);
        Assert.Equal(1, second.GetCommand(1).GlyphRangeCount);
        // Optional incremental pages do not yet own producer leases. The
        // ordinary compiled-scene retained cache has independent frame owners.
        Assert.False(first.GetCommand(0).SupportsRetainedCompositionPicture);
        first.Dispose();
        Assert.True(geometry.HasRenderStorage);
        second.Dispose();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void MixedCommandSnapshotPreservesEachPayloadAndOriginalGeometryOwner()
    {
        var owner = new Owner();
        using HintedGlyphGeometry geometry = Create(owner);
        object before = new(), after = new();
        var context = new DrawingContext();
        context.DrawExtension(101, dataParam: before);
        context.DrawHintedGlyphs(geometry, Vector2.Zero, new(9, 16, 10, 6), White);
        context.DrawExtension(102, dataParam: after);
        using GpuPicture picture = context.CreatePictureSnapshot();
        geometry.Dispose();
        context.Clear();
        context.DrawExtension(103, dataParam: new object());

        Assert.Equal(0, owner.Calls);
        Assert.Same(before, picture.GetCommand(0).DataParam);
        Assert.Null(picture.GetCommand(0).HintedGlyphGeometry);
        Assert.Same(geometry, picture.GetCommand(1).HintedGlyphGeometry);
        Assert.Same(after, picture.GetCommand(2).DataParam);
        Assert.Null(picture.GetCommand(2).HintedGlyphGeometry);
        picture.Dispose();
        Assert.Equal(1, owner.Calls);
        context.Clear();
        Assert.Equal(1, owner.Calls);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, 1)]
    public void InvalidRangesPublishNoCommandOrLease(int start, int count)
    {
        using HintedGlyphGeometry geometry = Create(new Owner());
        var context = new DrawingContext();
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphsRange(
            geometry, start, count, Vector2.Zero, new(9, 16, 10, 6), White));
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void EmptyRangeIsNotAnImplicitWholeParagraph()
    {
        using HintedGlyphGeometry geometry = Create(new Owner());
        var context = new DrawingContext();
        context.DrawHintedGlyphsRange(geometry, 3, 0, Vector2.Zero, Rect.Empty, White);
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void ChangedBasisAndTruncatedSourceInkBoundsFailBeforePublication()
    {
        using HintedGlyphGeometry geometry = Create(new Owner());
        var context = new DrawingContext();
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphs(geometry, Vector2.Zero,
            new(9, 16, 10, 6), White, Matrix4x4.CreateScale(1.0001f)));
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphs(geometry, Vector2.Zero,
            new(9, 16, 9, 6), White));
        Assert.Throws<ArgumentException>(() => context.DrawHintedGlyphs(geometry, Vector2.Zero,
            new(9, 16, 10, 6), White, textRenderingMode: TextRenderingMode.ClearType));
        Assert.Empty(context.Commands);
        Assert.Equal(0, context.RetainedResourceCount);
    }

    [Fact]
    public void ExactDpiAndOriginalOriginAreSharedRendererAdmission()
    {
        using HintedGlyphGeometry geometry = Create(new Owner());
        var context = new DrawingContext();
        context.DrawHintedGlyphs(geometry, new(3, 5), new(12, 21, 10, 6), White,
            Matrix4x4.CreateTranslation(0.25f, -0.125f, 0));
        RenderCommand command = context.Commands[0];
        Assert.True(HintedGlyphCommandGeometry.TryValidate(command, 2f,
            Matrix3x2.CreateTranslation(0.25f, -0.125f), out int start, out int count));
        Assert.Equal(0, start);
        Assert.Equal(3, count);
        Assert.False(HintedGlyphCommandGeometry.TryValidate(command, MathF.BitIncrement(2f),
            Matrix3x2.Identity, out _, out _));
        Assert.True(HintedGlyphCommandGeometry.TryGetInkBounds(command, out Rect ink, out bool hasInk));
        Assert.True(hasInk);
        Assert.Equal(new Rect(12, 21, 10, 6), ink);
        context.Clear();
    }

    [Fact]
    public void RasterBoundsRemainSeparateFromSourceInputAndPaintBounds()
    {
        using HintedGlyphGeometry geometry = Create(new Owner());
        var context = new DrawingContext();
        context.DrawHintedGlyphs(geometry, Vector2.Zero, new(9, 16, 10, 6), White);
        using GpuPicture picture = context.CreatePictureSnapshot();
        Assert.True(GpuPictureBounds.TryGetBounds(picture, out Rect bounds));
        Assert.Equal(new Rect(7, 14, 14, 10), bounds);
        Assert.Equal(new Rect(9, 16, 10, 6), picture.GetCommand(0).Rect);
        using var input = new GpuRenderCommandHitTestCacheBuilder();
        Assert.Throws<NotSupportedException>(() => input.AddCommand(picture.GetCommand(0), Matrix4x4.Identity));
        Assert.Equal(0, input.PrimitiveCount);
        context.Clear();
    }

    [Fact]
    public void RasterEndpointsUseTheOriginalWriterAdditionOrder()
    {
        const float sourceX = 100_000_000f;
        using var geometry = new HintedGlyphGeometry(2f,
            [new GpuGlyphRecord { SegmentCount = 1, MinX = -4f, MaxX = -3.875f, MinY = 0f, MaxY = 1f }],
            [new GpuSegment { P0 = new(-4f, 0f), P1 = new(-3.875f, 1f), SegmentType = 0 }],
            [new(0, 0, 7, 0, 0, 0, 0, 0, 0, 1, 0, new(sourceX, 20f), Vector2.Zero)],
            new Owner());
        var command = new RenderCommand
        {
            Type = RenderCommandType.DrawHintedGlyphs,
            HintedGlyphGeometry = geometry,
            GlyphRangeCount = 1
        };
        Assert.True(HintedGlyphCommandGeometry.TryGetRasterBounds(command, out Rect bounds, out bool hasInk));
        Assert.True(hasInk);
        // Read the writer position at runtime, rather than allowing compile-time
        // constant folding to substitute a different intermediate precision.
        float writerX = geometry.RenderOccurrences[0].Position.X;
        Assert.Equal(writerX, bounds.X);
        Assert.Equal(writerX + (-4f + 4.5f), bounds.Right);
        Assert.NotEqual((writerX + -4f) + 4.5f, bounds.Right);
    }

    [Fact]
    public void ContextDrainsEveryOwnerAndPreservesFirstRetirementFailure()
    {
        var first = new Owner { Failures = 1 };
        var second = new Owner();
        HintedGlyphGeometry a = Create(first), b = Create(second);
        var context = new DrawingContext();
        context.DrawHintedGlyphs(a, Vector2.Zero, new(9, 16, 10, 6), White);
        context.DrawHintedGlyphs(b, Vector2.Zero, new(9, 16, 10, 6), White);
        a.Dispose(); b.Dispose();
        Assert.Equal("original retirement fault", Assert.Throws<InvalidOperationException>(context.Clear).Message);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(0, context.RetainedResourceCount);
        context.Clear();
        a.Dispose();
        Assert.Equal(2, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AppendComposesTranslationWithoutChangingSourceInkDomain(int transformKind)
    {
        var owner = new Owner();
        HintedGlyphGeometry geometry = Create(owner);
        var source = new DrawingContext();
        Matrix4x4 original = transformKind == 0 ? default : transformKind == 1 ?
            Matrix4x4.Identity : Matrix4x4.CreateTranslation(0.25f, -0.125f, 0);
        source.DrawHintedGlyphs(geometry, Vector2.Zero, new(9, 16, 10, 6), White, original);
        geometry.Dispose();
        var target = new DrawingContext();
        target.Append(source, new(100, 25));
        source.Clear();
        RenderCommand command = target.Commands[0];
        Assert.Equal(Vector2.Zero, command.Position);
        Assert.Equal(new Rect(9, 16, 10, 6), command.Rect);
        Vector2 expected = new Vector2(100, 25) + (transformKind == 2 ? new(0.25f, -0.125f) : Vector2.Zero);
        Assert.Equal(expected.X, command.Transform.M41);
        Assert.Equal(expected.Y, command.Transform.M42);
        Assert.True(HintedGlyphCommandGeometry.TryValidate(command, 2f,
            Matrix3x2.CreateTranslation(expected), out _, out _));
        Assert.True(geometry.HasRenderStorage);
        Assert.Equal(0, owner.Calls);
        target.Clear();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void ThrowingAppendCallbackCannotLeavePublishedCommandWithoutItsOwner()
    {
        var owner = new Owner();
        HintedGlyphGeometry geometry = Create(owner);
        var source = new DrawingContext();
        source.DrawHintedGlyphs(geometry, Vector2.Zero, new(9, 16, 10, 6), White);
        geometry.Dispose();
        var target = new DrawingContext();
        target.SubscribeCommandAdded(_ => throw new InvalidOperationException("original command callback"));
        Assert.Equal("original command callback", Assert.Throws<InvalidOperationException>(() => target.Append(source)).Message);
        Assert.Single(target.Commands);
        Assert.Equal(1, target.RetainedResourceCount);
        source.Clear();
        Assert.Equal(0, owner.Calls);
        Assert.True(geometry.HasRenderStorage);
        target.Clear();
        Assert.Equal(1, owner.Calls);
    }

    [Fact]
    public void CompiledReplayLeaseExtendsAnAlreadyRecordedDisposedWrapper()
    {
        var owner = new Owner();
        HintedGlyphGeometry geometry = Create(owner);
        Assert.Throws<ObjectDisposedException>(() => geometry.RetainForReplay());
        IDisposable recorded = geometry.RetainForRecording();
        geometry.Dispose();
        IDisposable replay = geometry.RetainForReplay();
        recorded.Dispose();
        Assert.Equal(0, owner.Calls);
        Assert.True(geometry.HasRenderStorage);
        replay.Dispose();
        Assert.Equal(1, owner.Calls);
        Assert.Throws<ObjectDisposedException>(() => geometry.RetainForReplay());
    }
}
