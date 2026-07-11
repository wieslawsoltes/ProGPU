using ProGPU.Backend;
using Xunit;

namespace ProGPU.Tests;

public sealed class CurveStrokeShaderTests
{
    [Fact]
    public void CurveStrokeShaderFallsBackWhenEndpointDerivativeIsZero()
    {
        Assert.Contains("tangent = p2 - p0;", Shaders.VectorShader);
        Assert.Contains("tangent = select(p3 - p1, p2 - p0, t <= 0.5);", Shaders.VectorShader);
        Assert.Contains("tangent = p3 - p0;", Shaders.VectorShader);
    }

    [Fact]
    public void CurveStrokeShaderGammaCorrectsAntialiasedCoverage()
    {
        Assert.Contains(
            "let antialiasedAlpha = pow(linearAntialiasedAlpha, 0.7);",
            Shaders.VectorShader);
        Assert.Contains(
            "let correctedCoverage = pow(coverage, coverageGamma);",
            Shaders.VectorShader);
    }

    [Fact]
    public void AffineCurveStrokeShaderKeepsSharedCrossSectionsOpaque()
    {
        Assert.Contains("sType >= 14u && sType <= 17u", Shaders.VectorShader);
        Assert.Contains("if (sType == 15u)", Shaders.VectorShader);
        Assert.Contains("exteriorEdgeMask = 5u;", Shaders.VectorShader);
        Assert.Contains("internalInside", Shaders.VectorShader);
    }
}
