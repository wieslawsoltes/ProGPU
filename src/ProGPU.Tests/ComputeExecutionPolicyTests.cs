using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public class ComputeExecutionPolicyTests
{
    [Theory]
    [InlineData(null, GpuComputeExecutionPreference.Fastest)]
    [InlineData("auto", GpuComputeExecutionPreference.Fastest)]
    [InlineData("compute", GpuComputeExecutionPreference.NativeCompute)]
    [InlineData("raster", GpuComputeExecutionPreference.RasterShader)]
    [InlineData("simd", GpuComputeExecutionPreference.IntrinsicSimdCpu)]
    [InlineData("scalar", GpuComputeExecutionPreference.ScalarCpu)]
    public void TextPreferenceIsTyped(
        string? value,
        GpuComputeExecutionPreference expected)
    {
        Assert.Equal(
            expected,
            GpuComputeExecutionPolicy.ParsePreference(value));
    }

    [Fact]
    public void UnknownTextPreferenceFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GpuComputeExecutionPolicy.ParsePreference("approximate"));
    }

    [Fact]
    public void FastestGlyphPolicyKeepsParallelsD3D12OnGpuRasterShader()
    {
        Assert.Equal(
            GpuComputeExecutionPath.RasterShader,
            GpuComputeExecutionPolicy.ResolveGlyphRasterization(
                GpuComputeExecutionPreference.Fastest,
                BackendType.D3D12,
                "Parallels Display Adapter (WDDM)"));
        Assert.Equal(
            GpuComputeExecutionPath.NativeCompute,
            GpuComputeExecutionPolicy.ResolveGlyphRasterization(
                GpuComputeExecutionPreference.Fastest,
                BackendType.Metal,
                "Apple M3 Pro"));
    }

    [Theory]
    [InlineData(
        GpuComputeExecutionPreference.RasterShader,
        GpuComputeExecutionPath.RasterShader)]
    [InlineData(
        GpuComputeExecutionPreference.IntrinsicSimdCpu,
        GpuComputeExecutionPath.IntrinsicSimdCpu)]
    [InlineData(
        GpuComputeExecutionPreference.ScalarCpu,
        GpuComputeExecutionPath.ScalarCpu)]
    public void ForcedGlyphPolicyIsDeterministic(
        GpuComputeExecutionPreference preference,
        GpuComputeExecutionPath expected)
    {
        Assert.Equal(
            expected,
            GpuComputeExecutionPolicy.ResolveGlyphRasterization(
                preference,
                BackendType.D3D12,
                "Parallels Display Adapter (WDDM)"));
    }

    [Fact]
    public void ForcedKnownUnsupportedComputeFailsBeforeResourceCreation()
    {
        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => GpuComputeExecutionPolicy.ResolveGlyphRasterization(
                GpuComputeExecutionPreference.NativeCompute,
                BackendType.D3D12,
                "Parallels Display Adapter (WDDM)"));

        Assert.Contains("Select 'raster'", exception.Message);
        Assert.Equal(
            GpuComputeExecutionPath.NativeCompute,
            GpuComputeExecutionPolicy.ResolveGlyphRasterization(
                GpuComputeExecutionPreference.NativeCompute,
                BackendType.Metal,
                "Apple M3 Pro"));
    }

    [Fact]
    public void IntrinsicSimdGlyphCoverageMatchesScalarOracle()
    {
        GpuSegment[] segments =
        [
            Line(new(0, 0), new(8, 0)),
            Line(new(8, 0), new(8, 8)),
            Line(new(8, 8), new(0, 8)),
            Line(new(0, 8), new(0, 0))
        ];
        var record = new GpuGlyphRecord
        {
            StartSegment = 0,
            SegmentCount = (uint)segments.Length,
            MinX = 0,
            MinY = 0,
            MaxX = 8,
            MaxY = 8
        };

        byte[] scalar = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -2, -10, 1f, 0.25f, 12, 12,
            useSimd: false);
        byte[] simd = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -2, -10, 1f, 0.25f, 12, 12,
            useSimd: true);

        Assert.Equal(scalar, simd);
        Assert.Contains(simd, value => value != 0);
    }

    [Fact]
    public void IntrinsicSimdCurvedGlyphCoverageMatchesScalarOracle()
    {
        GpuSegment[] segments =
        [
            Line(new(0, 0), new(10, 0)),
            Quadratic(new(10, 0), new(13, 6), new(8, 11)),
            Cubic(new(8, 11), new(3, 14), new(-3, 7), new(0, 0))
        ];
        var record = new GpuGlyphRecord
        {
            StartSegment = 0,
            SegmentCount = (uint)segments.Length,
            MinX = -3,
            MinY = 0,
            MaxX = 13,
            MaxY = 14
        };

        byte[] scalar = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -5, -16, 1.125f, 0.375f, 22, 20,
            useSimd: false);
        byte[] simd = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -5, -16, 1.125f, 0.375f, 22, 20,
            useSimd: true);

        Assert.Equal(scalar, simd);
        Assert.Contains(simd, value => value is > 0 and < 255);
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(15U)]
    [InlineData(16U)]
    [InlineData(17U)]
    [InlineData(31U)]
    public void IntrinsicSimdGlyphCoverageHandlesVectorTails(uint width)
    {
        GpuSegment[] segments =
        [
            Line(new(-2, 0), new(13, 0)),
            Quadratic(new(13, 0), new(18, 8), new(9, 15)),
            Cubic(new(9, 15), new(2, 18), new(-7, 8), new(-2, 0))
        ];
        var record = new GpuGlyphRecord
        {
            StartSegment = 0,
            SegmentCount = (uint)segments.Length,
            MinX = -7,
            MinY = 0,
            MaxX = 18,
            MaxY = 18
        };

        byte[] scalar = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -8, -20, 0.875f, 0.3125f, width, 23,
            useSimd: false);
        byte[] simd = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -8, -20, 0.875f, 0.3125f, width, 23,
            useSimd: true);

        Assert.Equal(scalar, simd);
    }

    [Fact]
    public void IntrinsicSimdGlyphCoveragePreservesOpposedContourWindings()
    {
        GpuSegment[] segments =
        [
            Line(new(0, 0), new(16, 0)),
            Line(new(16, 0), new(16, 16)),
            Line(new(16, 16), new(0, 16)),
            Line(new(0, 16), new(0, 0)),
            Line(new(5, 5), new(5, 11)),
            Line(new(5, 11), new(11, 11)),
            Line(new(11, 11), new(11, 5)),
            Line(new(11, 5), new(5, 5))
        ];
        var record = new GpuGlyphRecord
        {
            StartSegment = 0,
            SegmentCount = (uint)segments.Length,
            MinX = 0,
            MinY = 0,
            MaxX = 16,
            MaxY = 16
        };

        byte[] scalar = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -2, -18, 1f, 0.375f, 20, 20,
            useSimd: false);
        byte[] simd = GlyphAtlas.RasterizeGlyphCoverageCpu(
            segments, record, -2, -18, 1f, 0.375f, 20, 20,
            useSimd: true);

        Assert.Equal(scalar, simd);
        Assert.Contains((byte)0, simd);
        Assert.Contains((byte)255, simd);
    }

    [Fact]
    public void IntrinsicSimdEmptyGlyphMatchesScalarOracle()
    {
        var record = new GpuGlyphRecord();

        byte[] scalar = GlyphAtlas.RasterizeGlyphCoverageCpu(
            [], record, 0, 0, 1f, 0f, 17, 3,
            useSimd: false);
        byte[] simd = GlyphAtlas.RasterizeGlyphCoverageCpu(
            [], record, 0, 0, 1f, 0f, 17, 3,
            useSimd: true);

        Assert.Equal(scalar, simd);
        Assert.All(simd, value => Assert.Equal((byte)0, value));
    }

    private static GpuSegment Line(Vector2 start, Vector2 end) => new()
    {
        P0 = start,
        P1 = end,
        SegmentType = 0
    };

    private static GpuSegment Quadratic(
        Vector2 start,
        Vector2 control,
        Vector2 end) => new()
    {
        P0 = start,
        P1 = control,
        P2 = end,
        SegmentType = 1
    };

    private static GpuSegment Cubic(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end) => new()
    {
        P0 = start,
        P1 = control1,
        P2 = control2,
        P3 = end,
        SegmentType = 2
    };
}
