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
}
