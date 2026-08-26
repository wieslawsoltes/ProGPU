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
