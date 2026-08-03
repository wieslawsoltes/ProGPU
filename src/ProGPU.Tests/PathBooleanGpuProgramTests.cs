using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class PathBooleanGpuProgramTests
{
    [Fact]
    public void NestedBooleanTreeUsesExactPostfixGpuProgram()
    {
        using var atlas = new PathAtlas(HeadlessWindow.Shared.Context, atlasSize: 256);
        PathGeometry pathA = PrimitivePathGeometry.CreateRectangle(4f, 4f, 16f, 16f);
        PathGeometry pathB = PrimitivePathGeometry.CreateRectangle(12f, 4f, 16f, 16f);
        PathGeometry pathC = PrimitivePathGeometry.CreateRectangle(8f, 8f, 16f, 16f);
        var intersection = new PathGeometry
        {
            IsCombined = true,
            Op = 1,
            PathA = pathB,
            PathB = pathC
        };
        var union = new PathGeometry
        {
            IsCombined = true,
            Op = 2,
            PathA = pathA,
            PathB = intersection
        };
        PathAtlas.PathInfo info = atlas.GetOrCreatePath(
            union,
            scale: 1f,
            sampleGrid: PathAtlas.HighPrecisionCoverageSampleGrid);

        atlas.RasterizePendingPaths();

        byte[] pixels = atlas.AtlasTexture.ReadPixels();
        Assert.Equal(1, atlas.LastDirectBooleanRasterizationCount);
        Assert.Equal(1, atlas.LastBooleanProgramRasterizationCount);
        AssertMembership(pixels, info, worldX: 6, worldY: 10, expectedInside: true);
        AssertMembership(pixels, info, worldX: 10, worldY: 10, expectedInside: true);
        AssertMembership(pixels, info, worldX: 22, worldY: 10, expectedInside: true);
        AssertMembership(pixels, info, worldX: 26, worldY: 10, expectedInside: false);
        AssertMembership(pixels, info, worldX: 22, worldY: 6, expectedInside: false);
    }

    private static void AssertMembership(
        byte[] pixels,
        PathAtlas.PathInfo info,
        int worldX,
        int worldY,
        bool expectedInside)
    {
        var localX = checked((uint)(worldX - (int)info.MinX));
        var localY = checked((uint)(worldY - (int)info.MinY));
        int offset = checked((int)((info.Y + localY) * 256u + info.X + localX));
        byte coverage = pixels[offset];
        if (expectedInside)
        {
            Assert.InRange(coverage, (byte)252, byte.MaxValue);
        }
        else
        {
            Assert.InRange(coverage, byte.MinValue, (byte)3);
        }
    }
}
