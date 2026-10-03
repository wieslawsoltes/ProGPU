using System.Buffers.Binary;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class ClippedMiterPenArchiveTests
{
    [Theory]
    [InlineData(PenLineJoin.Miter)]
    [InlineData(PenLineJoin.Bevel)]
    [InlineData(PenLineJoin.Round)]
    [InlineData(PenLineJoin.MiterOrBevel)]
    public void RawIntentSurvivesMaterialSnapshotPictureCloneAndVersionSeven(PenLineJoin join)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, join) { DashArray = [2, 1] };
        Assert.False(pen.ClipMiterAtLimit);
        pen.ClipMiterAtLimit = true;
        Pen snapshot = pen.WithBrush(new SolidColorBrush(new Vector4(0, 1, 0, 1)));
        pen.ClipMiterAtLimit = false;
        pen.LineJoin = PenLineJoin.Round;
        Assert.True(snapshot.ClipMiterAtLimit);
        Assert.Equal(join, snapshot.LineJoin);
        Assert.Equal(new double[] { 2, 1 }, snapshot.DashArray);
        using var picture = Picture(snapshot);
        using var clone = picture.Clone();
        Assert.True(picture.SharesRetainedCommandStorageWith(clone));
        Assert.True(clone.GetCommand(0).Pen!.ClipMiterAtLimit);
        byte[] archive = PictureArchive.Serialize(picture, new SKRect(0, 0, 64, 64), 7);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8, 4)));
        using SKPicture? restored = SKPicture.Deserialize(archive);
        Assert.NotNull(restored);
        Pen actual = Assert.Single(restored.Picture.Commands).Pen!;
        Assert.True(actual.ClipMiterAtLimit);
        Assert.Equal(join, actual.LineJoin);
        Assert.Equal(snapshot.DashArray, actual.DashArray);
    }

    [Fact]
    public void BothDashCachesKeepInactiveRawPolicyInTheirGenerationKeys()
    {
        var path = PrimitivePathGeometry.CreateRectangle(4, 4, 24, 24);
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, PenLineJoin.MiterOrBevel)
        {
            DashArray = [100, 1]
        };
        var cache = RenderCommandGeometryCache.ForStrokePath(path);
        Assert.True(cache.TryGetDashedStrokePath(pen, out var firstPath, out var firstPen));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var firstCoverage));
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var warmCoverage));
        Assert.Same(firstCoverage.GeometryCache, warmCoverage.GeometryCache);
        pen.ClipMiterAtLimit = true;
        Assert.True(cache.TryGetDashedStrokePath(pen, out var changedPath, out var changedPen));
        Assert.Same(firstPath, changedPath); // Dash placement is independent of corner policy.
        Assert.NotSame(firstPen, changedPen);
        Assert.False(firstPen.ClipMiterAtLimit);
        Assert.True(changedPen.ClipMiterAtLimit);
        Assert.True(cache.TryGetLinearDashCoverage(pen, pen.Thickness, out var changedCoverage));
        Assert.NotSame(firstCoverage.GeometryCache, changedCoverage.GeometryCache);
        if (changedCoverage.Pen is { } changedCoveragePen)
            Assert.True(changedCoveragePen.ClipMiterAtLimit);
        if (firstCoverage.Pen is { } firstCoveragePen)
            Assert.False(firstCoveragePen.ClipMiterAtLimit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void HistoricalArchivesDefaultFalseAndRejectLossyRawIntent(int version)
    {
        var pen = new Pen(new SolidColorBrush(Vector4.One), 4, PenLineJoin.Round);
        using var picture = Picture(pen);
        byte[] archive = PictureArchive.Serialize(picture, new SKRect(0, 0, 64, 64), version);
        Assert.Equal(version, BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(8, 4)));
        using SKPicture? restored = SKPicture.Deserialize(archive);
        Assert.NotNull(restored);
        Assert.False(Assert.Single(restored.Picture.Commands).Pen!.ClipMiterAtLimit);
        pen.ClipMiterAtLimit = true; // Inactive on Round, but still retained source intent.
        using var clipped = Picture(pen);
        Assert.Throws<NotSupportedException>(() =>
            PictureArchive.Serialize(clipped, new SKRect(0, 0, 64, 64), version));
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
