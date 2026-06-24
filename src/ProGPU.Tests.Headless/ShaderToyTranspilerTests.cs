using ProGPU.Transpiler;
using Xunit;

namespace ProGPU.Tests.Headless;

public sealed class ShaderToyTranspilerTests
{
    [Fact]
    public void MainImageBareReturnReturnsCurrentFragColor()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    color = vec4(1.0, 0.0, 0.0, 1.0);
    if (coord.x < 0.5) {
        return;
    }
    color = vec4(0.0, 1.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("fn mainImage(coord: vec2<f32>) -> vec4<f32>", wgsl);
        Assert.Contains("return color;", wgsl);
        Assert.DoesNotContain("return;\n", wgsl);
    }

    [Fact]
    public void VectorScalarAddSubBroadcastsScalarOperands()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    vec2 uv = coord.xy;
    uv = uv + 1.0;
    uv = 2.0 - uv;
    color = vec4(uv, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("uv = (uv + vec2<f32>(1.0));", wgsl);
        Assert.Contains("uv = (vec2<f32>(2.0) - uv);", wgsl);
    }

    [Fact]
    public void ShaderToyFrameRateUniformMapsToInputStruct()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    float rate = iFrameRate;
    color = vec4(rate, 0.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("inputs.iFrameRate", wgsl);
        Assert.DoesNotContain("= iFrameRate;", wgsl);
    }

    [Fact]
    public void ScalarBuiltinArgumentsBroadcastToVectorOperands()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    vec3 a = mix(vec3(0.0), vec3(1.0), 0.5);
    vec3 b = smoothstep(0.0, 1.0, a);
    vec3 c = step(0.25, b);
    color = vec4(c, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("mix(vec3<f32>(0.0), vec3<f32>(1.0), vec3<f32>(0.5))", wgsl);
        Assert.Contains("smoothstep(vec3<f32>(0.0), vec3<f32>(1.0), a)", wgsl);
        Assert.Contains("step(vec3<f32>(0.25), b)", wgsl);
    }

    [Fact]
    public void EmbeddedIncrementDecrementThrowsInsteadOfEmittingInvalidWgsl()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    int i = 0;
    int j = i++;
    color = vec4(float(j), 0.0, 0.0, 1.0);
}
""";

        var exception = Assert.Throws<NotSupportedException>(() => ShaderToyTranspiler.Translate(glsl));

        Assert.Contains("embedded increment/decrement", exception.Message);
    }

    [Fact]
    public void StandaloneIncrementDecrementStillEmitsMutatingStatements()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    int i = 0;
    i++;
    --i;
    color = vec4(float(i), 0.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("i = i + 1;", wgsl);
        Assert.Contains("i = i - 1;", wgsl);
    }

    [Fact]
    public void LocalArrayDeclarationPreservesElementCount()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    float weights[3];
    weights[0] = 0.25;
    weights[1] = 0.5;
    weights[2] = 0.25;
    color = vec4(weights[0] + weights[1] + weights[2], 0.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var weights: array<f32, 3>;", wgsl);
        Assert.Contains("weights[0] = 0.25;", wgsl);
        Assert.DoesNotContain("var weights: f32;", wgsl);
    }
}
