using System;
using System.Diagnostics;
using System.Linq;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PathAtlasExactRecoveryTests
{
    [Fact]
    public void ExactRecoveryPacksFeasibleSetRejectedByAllGreedyStrategies()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 50);
        PathGeometry[] paths =
        [
            PrimitivePathGeometry.CreateRectangle(0f, 0f, 26f, 2f),
            PrimitivePathGeometry.CreateRectangle(3f, 5f, 8f, 2f),
            PrimitivePathGeometry.CreateRectangle(7f, 11f, 14f, 8f),
            PrimitivePathGeometry.CreateRectangle(13f, 17f, 20f, 2f),
            PrimitivePathGeometry.CreateRectangle(19f, 23f, 2f, 26f)
        ];

        PathAtlas.PathInfo[] initial = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();

        Assert.True(atlas.CapacityExceeded);
        Assert.Contains(initial, static info => info.Width == 0);

        atlas.ResetForRenderRetry();
        PathAtlas.PathInfo[] recovered = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();

        Assert.False(atlas.CapacityExceeded);
        Assert.Equal(new uint[] { 34, 16, 22, 28, 10 }, recovered.Select(static info => info.Width));
        Assert.Equal(new uint[] { 10, 10, 16, 10, 34 }, recovered.Select(static info => info.Height));
        AssertNonOverlapping(recovered, atlas.AtlasSize);

        var firstPacking = recovered.Select(static info => (info.X, info.Y)).ToArray();
        atlas.ResetForRenderRetry();
        PathAtlas.PathInfo[] repeated = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();
        Assert.Equal(firstPacking, repeated.Select(static info => (info.X, info.Y)).ToArray());
    }

    [Fact]
    public void ExactRecoveryAdversarialSetStopsAtDeterministicCandidateBudget()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 66);
        (float Width, float Height)[] logicalSizes =
        [
            (6f, 14f),
            (10f, 4f),
            (2f, 18f),
            (8f, 2f),
            (16f, 2f),
            (2f, 22f),
            (18f, 14f),
            (2f, 4f),
            (18f, 12f),
            (30f, 8f)
        ];
        PathGeometry[] paths = logicalSizes
            .Select((size, index) => PrimitivePathGeometry.CreateRectangle(
                index * 3f,
                index * 5f,
                size.Width,
                size.Height))
            .ToArray();
        _ = paths.Select(path => atlas.GetOrCreatePath(path, scale: 1f)).ToArray();
        Assert.True(atlas.CapacityExceeded);

        Stopwatch stopwatch = Stopwatch.StartNew();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(atlas.ResetForRenderRetry);
        stopwatch.Stop();

        Assert.True(atlas.LastExactRecoveryBudgetExceeded);
        Assert.Equal(250_000, atlas.LastExactRecoveryCandidateCount);
        Assert.Equal(924, atlas.LastExactRecoveryNodeCount);
        Assert.Contains("250000 candidates", exception.Message, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Budgeted exact recovery should terminate promptly; elapsed {stopwatch.Elapsed}.");
    }

    [Fact]
    public void RecoveryPreservesPriorControlCatalogPathsNeededByFullSceneRetry()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 1024);
        (uint Width, uint Height)[] rasterSizes =
        [
            (270, 270),
            (264, 264),
            (32, 32),
            (29, 22),
            (23, 23),
            (10, 32),
            (20, 15),
            (16, 16),
            (359, 362),
            (359, 362),
            (27, 28),
            (113, 10),
            (28, 27),
            (28, 26)
        ];
        PathGeometry[] paths = rasterSizes
            .Select((size, index) => PrimitivePathGeometry.CreateRectangle(
                index * 3f,
                index * 5f,
                size.Width - 8f,
                size.Height - 8f))
            .ToArray();

        int[] priorOrder = [8, 9, 10, 11, 12, 13, 2, 3, 4, 5, 6, 7];
        PathAtlas.PathInfo[] priorFrame = priorOrder
            .Select(index => atlas.GetOrCreatePath(paths[index], scale: 1f))
            .ToArray();
        Assert.Equal(
            priorOrder.Select(index => rasterSizes[index].Width),
            priorFrame.Select(static info => info.Width));
        // Growing the first-pass atlas invalidates normalized coordinates even
        // when every rectangle was placed, so the compositor performs the same
        // bounded repack before it can submit that frame.
        atlas.ResetForRenderRetry();
        Assert.Equal(512u, atlas.AtlasWidth);
        Assert.Equal(1024u, atlas.AtlasHeight);

        atlas.CleanupFrame();
        // Compositor recovery transactions can advance the atlas frame counter
        // without replaying every retained path, so the latest complete set is
        // not necessarily numbered exactly currentFrame - 1.
        atlas.CleanupFrame();
        _ = paths
            .Take(8)
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();
        Assert.True(atlas.CapacityExceeded);
        atlas.ResetForRenderRetry();
        Assert.Equal(paths.Length, atlas.CachedPathCount);
        Assert.Equal(1024u, atlas.AtlasWidth);
        Assert.Equal(1024u, atlas.AtlasHeight);
        PathAtlas.PathInfo[] recovered = paths
            .Select(path => atlas.GetOrCreatePath(path, scale: 1f))
            .ToArray();

        Assert.False(atlas.CapacityExceeded);
        Assert.Equal(rasterSizes.Select(static size => size.Width), recovered.Select(static info => info.Width));
        Assert.Equal(rasterSizes.Select(static size => size.Height), recovered.Select(static info => info.Height));
        AssertNonOverlapping(recovered, atlas.AtlasSize);
    }

    private static void AssertNonOverlapping(
        PathAtlas.PathInfo[] paths,
        uint atlasSize)
    {
        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            PathAtlas.PathInfo path = paths[pathIndex];
            Assert.True(path.X + path.Width + 2 <= atlasSize);
            Assert.True(path.Y + path.Height + 2 <= atlasSize);
            for (int otherIndex = pathIndex + 1; otherIndex < paths.Length; otherIndex++)
            {
                PathAtlas.PathInfo other = paths[otherIndex];
                Assert.True(
                    path.X + path.Width + 2 <= other.X ||
                    other.X + other.Width + 2 <= path.X ||
                    path.Y + path.Height + 2 <= other.Y ||
                    other.Y + other.Height + 2 <= path.Y);
            }
        }
    }
}
