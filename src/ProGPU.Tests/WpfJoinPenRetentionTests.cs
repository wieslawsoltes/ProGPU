using System.Buffers.Binary;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class WpfJoinPenRetentionTests
{
    [Theory]
    [InlineData(PenLineJoin.Miter, false)]
    [InlineData(PenLineJoin.Miter, true)]
    [InlineData(PenLineJoin.Bevel, false)]
    [InlineData(PenLineJoin.Bevel, true)]
    [InlineData(PenLineJoin.Round, false)]
    [InlineData(PenLineJoin.Round, true)]
    public void MaterialSnapshotPictureCloneAndVersionEightRetainBothPolicies(
        PenLineJoin join, bool clipMiter)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, join)
        {
            ClipMiterAtLimit = clipMiter,
            DashArray = [2, 1]
        };
        Assert.False(pen.UseWpfJoinSemantics);
        pen.UseWpfJoinSemantics = true;
        Pen snapshot = pen.WithBrush(new SolidColorBrush(new Vector4(0, 1, 0, 1)));
        pen.UseWpfJoinSemantics = false;
        pen.ClipMiterAtLimit = !clipMiter;
        pen.LineJoin = PenLineJoin.MiterOrBevel;
        pen.DashArray = [9, 8];
        Assert.True(snapshot.UseWpfJoinSemantics);
        Assert.Equal(clipMiter, snapshot.ClipMiterAtLimit);
        Assert.Equal(join, snapshot.LineJoin);
        Assert.Equal(new double[] { 2, 1 }, snapshot.DashArray);

        using var picture = Picture(snapshot);
        using var clone = picture.Clone();
        Assert.True(picture.SharesRetainedCommandStorageWith(clone));
        Assert.True(clone.GetCommand(0).Pen!.UseWpfJoinSemantics);
        byte[] archive = PictureArchive.Serialize(clone, new SKRect(0, 0, 64, 64));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8, 4)));
        using SKPicture? restored = SKPicture.Deserialize(archive);
        Assert.NotNull(restored);
        Pen actual = Assert.Single(restored.Picture.Commands).Pen!;
        Assert.True(actual.UseWpfJoinSemantics);
        Assert.Equal(clipMiter, actual.ClipMiterAtLimit);
        Assert.Equal(join, actual.LineJoin);
        Assert.Equal(snapshot.Thickness, actual.Thickness);
        Assert.Equal(snapshot.MiterLimit, actual.MiterLimit);
        Assert.Equal(snapshot.DashArray, actual.DashArray);
    }

    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    [InlineData(PenLineJoin.Round)]
    public void PolicyMutationRefreshesBothDashCachesWithoutChangingDashPlacement(PenLineJoin join)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, join, miterLimit: 1)
        {
            DashArray = [100, 1]
        };
        var cache = RenderCommandGeometryCache.ForStrokePath(
            PrimitivePathGeometry.CreateRectangle(4, 4, 24, 24));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var firstPath, out var firstPen));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var firstCoverage));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var warmPath, out var warmPen));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var warmCoverage));
        Assert.Same(firstPath, warmPath);
        Assert.Same(firstPen, warmPen);
        Assert.Same(firstCoverage.GeometryCache, warmCoverage.GeometryCache);

        pen.UseWpfJoinSemantics = true;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var changedPath, out var changedPen));
        Assert.Same(firstPath, changedPath); // Policy changes corners, not dash placement.
        Assert.NotSame(firstPen, changedPen);
        Assert.False(firstPen.UseWpfJoinSemantics);
        Assert.True(changedPen.UseWpfJoinSemantics);
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var changedCoverage));
        Assert.NotSame(firstCoverage.GeometryCache, changedCoverage.GeometryCache);
        if (firstCoverage.Pen is { } firstCoveragePen)
            Assert.False(firstCoveragePen.UseWpfJoinSemantics);
        if (changedCoverage.Pen is { } changedCoveragePen)
            Assert.True(changedCoveragePen.UseWpfJoinSemantics);
        Assert.True(cache.TryGetDashedStrokePath(pen, out _, out var changedWarmPen));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var changedWarmCoverage));
        Assert.Same(changedPen, changedWarmPen);
        Assert.Same(changedCoverage.GeometryCache, changedWarmCoverage.GeometryCache);
    }

    [Theory]
    [InlineData(0)] // Fixed width.
    [InlineData(1)] // Hairline width.
    [InlineData(2)] // Explicit MiterOrBevel, not source WPF.
    public void UnsupportedPolicyCannotReuseOrReplaceAValidDashGeneration(int invalidStyle)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4)
        {
            UseWpfJoinSemantics = true,
            DashArray = [100, 1]
        };
        var cache = RenderCommandGeometryCache.ForStrokePath(
            PrimitivePathGeometry.CreateRectangle(4, 4, 24, 24));
        Assert.True(cache.TryGetDashedStrokePath(pen, out var validPath, out var validPen));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var validCoverage));
        switch (invalidStyle)
        {
            case 0: pen.StrokeTransformMode = PenStrokeTransformMode.Fixed; break;
            case 1: pen.Thickness = Pen.HairlineThickness; break;
            case 2: pen.LineJoin = PenLineJoin.MiterOrBevel; break;
        }
        Assert.Throws<NotSupportedException>(() => cache.TryGetDashedStrokePath(pen, out _, out _));
        Assert.Throws<NotSupportedException>(() => cache.TryGetLinearDashCoverage(pen, 4, out _));
        Assert.True(validPen.UseWpfJoinSemantics);
        Assert.Equal(4f, validPen.Thickness);
        Assert.Equal(PenLineJoin.Miter, validPen.LineJoin);
        Assert.Equal(PenStrokeTransformMode.Normal, validPen.StrokeTransformMode);

        pen.Thickness = 4;
        pen.LineJoin = PenLineJoin.Miter;
        pen.StrokeTransformMode = PenStrokeTransformMode.Normal;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var restoredPath, out var restoredPen));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var restoredCoverage));
        Assert.Same(validPath, restoredPath);
        Assert.Same(validPen, restoredPen);
        Assert.Same(validCoverage.GeometryCache, restoredCoverage.GeometryCache);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void HistoricalArchivesDefaultFalseAndRejectLossyPolicyWrites(int version)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, PenLineJoin.Round)
        {
            // The v7 field remains independent from the new v8 field.
            ClipMiterAtLimit = version == 7
        };
        using var picture = Picture(pen);
        byte[] archive = PictureArchive.Serialize(picture, new SKRect(0, 0, 64, 64), version);
        Assert.Equal(version, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8, 4)));
        using SKPicture? restored = SKPicture.Deserialize(archive);
        Assert.NotNull(restored);
        Pen actual = Assert.Single(restored.Picture.Commands).Pen!;
        Assert.False(actual.UseWpfJoinSemantics);
        Assert.Equal(version == 7, actual.ClipMiterAtLimit);

        pen.UseWpfJoinSemantics = true;
        using var sourcePolicy = Picture(pen);
        Assert.Throws<NotSupportedException>(() =>
            PictureArchive.Serialize(sourcePolicy, new SKRect(0, 0, 64, 64), version));
    }

    [Fact]
    public void OverdrawMaterialCopyRetainsBothPoliciesAndOwnsPriorPenState()
    {
        var targetContext = new DrawingContext();
        using var target = new SKCanvas(targetContext, 64, 64);
        using var overdraw = new SKOverdrawCanvas(target);
        var pen = new Pen(new SolidColorBrush(new Vector4(1, 0, 0, 1)), 4,
            PenLineJoin.Miter, miterLimit: 1, dashArray: [2, 1])
        {
            UseWpfJoinSemantics = true,
            ClipMiterAtLimit = true
        };
        overdraw.DrawingContext.DrawLine(pen, new Vector2(4, 4), new Vector2(28, 4));
        Assert.Equal(3, targetContext.Commands.Count);
        Assert.Equal(RenderCommandType.PushBlendMode, targetContext.Commands[0].Type);
        Assert.Equal(RenderCommandType.DrawLine, targetContext.Commands[1].Type);
        Assert.Equal(RenderCommandType.PopBlendMode, targetContext.Commands[2].Type);
        Pen actual = targetContext.Commands[1].Pen!;
        Assert.NotSame(pen, actual);
        Assert.NotSame(pen.Brush, actual.Brush);
        Assert.Equal(1f / 255f, Assert.IsType<SolidColorBrush>(actual.Brush).Color.W);
        pen.UseWpfJoinSemantics = false;
        pen.ClipMiterAtLimit = false;
        pen.LineJoin = PenLineJoin.MiterOrBevel;
        pen.DashArray = [9, 8];
        Assert.True(actual.UseWpfJoinSemantics);
        Assert.True(actual.ClipMiterAtLimit);
        Assert.Equal(PenLineJoin.Miter, actual.LineJoin);
        Assert.Equal(1f, actual.MiterLimit);
        Assert.Equal(new double[] { 2, 1 }, actual.DashArray);
    }

    private static GpuPicture Picture(Pen pen) => new(
        [new RenderCommand
        {
            Type = RenderCommandType.DrawLine,
            Position = new Vector2(4, 4),
            Position2 = new Vector2(28, 4),
            Pen = pen,
            IsPenThicknessLocal = true
        }], [], [], [], []);
}
