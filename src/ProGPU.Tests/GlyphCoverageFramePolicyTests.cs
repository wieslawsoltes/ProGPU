using System.Numerics;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.Tests;

public sealed class GlyphCoverageFramePolicyTests
{
    [Theory]
    [InlineData(96u, 80u, 96u, 80u, 1f)]
    [InlineData(48u, 40u, 96u, 80u, 2f)]
    [InlineData(80u, 64u, 100u, 80u, 1.25f)]
    [InlineData(80u, 64u, 120u, 96u, 1.5f)]
    public void OriginalFullRootFramesAreCertified(
        uint logicalWidth, uint logicalHeight, uint width, uint height, float dpi)
    {
        Assert.Equal(-1f, Certificate(logicalWidth, logicalHeight, width, height, dpi));
    }

    [Theory]
    [InlineData(0u, 48u, 96u, 96u, 2f)]
    [InlineData(48u, 0u, 96u, 96u, 2f)]
    [InlineData(48u, 48u, 0u, 96u, 2f)]
    [InlineData(48u, 48u, 96u, 0u, 2f)]
    [InlineData(48u, 48u, 95u, 96u, 2f)]
    [InlineData(48u, 48u, 96u, 95u, 2f)]
    [InlineData(48u, 48u, 96u, 96u, 0f)]
    [InlineData(48u, 48u, 96u, 96u, -2f)]
    [InlineData(48u, 48u, 96u, 96u, float.NaN)]
    [InlineData(48u, 48u, 96u, 96u, float.PositiveInfinity)]
    [InlineData(48u, 48u, 96u, 96u, float.Epsilon)]
    [InlineData(48u, 48u, 96u, 96u, float.MaxValue)]
    [InlineData(16777217u, 48u, 16777217u, 48u, 1f)]
    [InlineData(48u, 16777217u, 48u, 16777217u, 1f)]
    public void UnprovenDimensionMappingsStayOnOriginalPath(
        uint logicalWidth, uint logicalHeight, uint width, uint height, float dpi)
    {
        Assert.Equal(0f, Certificate(logicalWidth, logicalHeight, width, height, dpi));
    }

    [Theory]
    [InlineData(0f, 0f, 95f, 96f)]
    [InlineData(0f, 0f, 96f, 95f)]
    [InlineData(1f, 0f, 96f, 96f)]
    [InlineData(0f, 1f, 96f, 96f)]
    [InlineData(float.Epsilon, 0f, 96f, 96f)]
    [InlineData(0f, float.Epsilon, 96f, 96f)]
    [InlineData(float.NaN, 0f, 96f, 96f)]
    [InlineData(0f, 0f, float.PositiveInfinity, 96f)]
    public void NonFullOrShiftedViewportsAreNotCertified(float x, float y, float width, float height)
    {
        Assert.Equal(0f, Certificate(viewport: new Vector4(x, y, width, height)));
    }

    [Fact]
    public void EveryChangedProjectionComponentRejectsWithoutTolerance()
    {
        Matrix4x4 original = Projection(48, 48);
        for (int row = 0; row != 4; ++row)
        {
            for (int column = 0; column != 4; ++column)
            {
                Matrix4x4 changed = original;
                changed[row, column] = MathF.BitIncrement(changed[row, column]);
                Assert.Equal(0f, Certificate(projection: changed));
                changed[row, column] = float.NaN;
                Assert.Equal(0f, Certificate(projection: changed));
            }
        }
        Assert.Equal(-1f, Certificate(projection: original));
    }

    [Fact]
    public void MultisamplingAndGpuTransformsKeepOriginalPath()
    {
        Assert.Equal(0f, Certificate(sampleCount: 0));
        Assert.Equal(0f, Certificate(sampleCount: 4));
        Assert.Equal(0f, Certificate(hasGpuTransforms: true));
        Assert.Equal(-1f, Certificate());
    }

    private static float Certificate(
        uint logicalWidth = 48, uint logicalHeight = 48,
        uint width = 96, uint height = 96, float dpi = 2f,
        uint sampleCount = 1, bool hasGpuTransforms = false,
        Matrix4x4? projection = null, Vector4? viewport = null) =>
        GlyphCoverageFramePolicy.GetRootCertificate(
            logicalWidth, logicalHeight, width, height, dpi, sampleCount, hasGpuTransforms,
            projection ?? Projection(logicalWidth, logicalHeight),
            viewport ?? new Vector4(0f, 0f, width, height));

    private static Matrix4x4 Projection(uint width, uint height) => new(
        2f / width, 0f, 0f, 0f,
        0f, -2f / height, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 1f, 0f, 1f);
}
