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
    public void UnsignedHexLiteralsPreserveUintType()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    uint mask = 0xffffffffu;
    uint low = 0xffu;
    color = vec4(float(low), float(mask), 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var mask: u32 = 4294967295u;", wgsl);
        Assert.Contains("var low: u32 = 255u;", wgsl);
    }

    [Fact]
    public void NumericPreprocessorConditionsEvaluateIntegerConstants()
    {
        const string glsl = """
#define MODE 2
void mainImage(out vec4 color, in vec2 coord) {
#if 2
    float enabled = 1.0;
#else
    float enabled = 0.0;
#endif
#if MODE == 2 && (4 >> 1) == 2
    enabled = enabled + 2.0;
#endif
#if 10
    enabled = enabled + 3.0;
#endif
#if 0
    enabled = enabled + 8.0;
#elif MODE >= 2
    enabled = enabled + 4.0;
#endif
    color = vec4(enabled, 0.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var enabled: f32 = 1.0;", wgsl);
        Assert.Contains("enabled = (enabled + 2.0);", wgsl);
        Assert.Contains("enabled = (enabled + 3.0);", wgsl);
        Assert.Contains("enabled = (enabled + 4.0);", wgsl);
        Assert.DoesNotContain("var enabled: f32 = 0.0;", wgsl);
        Assert.DoesNotContain("enabled = (enabled + 8.0);", wgsl);
    }

    [Fact]
    public void IntegerVectorSwizzlesPreserveVectorFamily()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    ivec3 a = ivec3(1, 2, 3);
    ivec2 b = a.xy + 1;
    uvec3 c = uvec3(1u, 2u, 3u);
    uvec2 d = c.xy + 1u;
    bvec3 flags = bvec3(true, false, true);
    bvec2 flagPair = flags.xy;
    color = vec4(float(b.x), float(d.x), flagPair.x ? 1.0 : 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var b: vec2<i32> = (a.xy + vec2<i32>(1));", wgsl);
        Assert.Contains("var d: vec2<u32> = (c.xy + vec2<u32>(1u));", wgsl);
        Assert.Contains("var flagPair: vec2<bool> = flags.xy;", wgsl);
        Assert.DoesNotContain("a.xy + vec2<f32>(1)", wgsl);
        Assert.DoesNotContain("c.xy + vec2<f32>(1u)", wgsl);
    }

    [Fact]
    public void IntegerVectorElementAccessPreservesScalarFamily()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    uvec2 u = uvec2(1u, 2u);
    u[0]++;
    ivec2 i = ivec2(3, 4);
    i[1]--;
    bvec2 flags = bvec2(true, false);
    bool enabled = flags[0];
    color = vec4(float(u[0]), float(i[1]), enabled ? 1.0 : 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("u[0] = u[0] + 1u;", wgsl);
        Assert.Contains("i[1] = i[1] - 1;", wgsl);
        Assert.Contains("var enabled: bool = flags[0];", wgsl);
        Assert.DoesNotContain("u[0] = u[0] + 1.0;", wgsl);
    }

    [Fact]
    public void ShiftOperatorsEmitWgsl()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    uint x = 1u << 3u;
    x >>= 1u;
    x <<= 2u;
    uint y = x >> 1u;
    color = vec4(float(y), 0.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var x: u32 = (1u << 3u);", wgsl);
        Assert.Contains("x >>= 1u;", wgsl);
        Assert.Contains("x <<= 2u;", wgsl);
        Assert.Contains("var y: u32 = (x >> 1u);", wgsl);
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
    public void ScalarPowArgumentBroadcastsToVectorOperand()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    vec3 rgb = pow(vec3(0.5), 2.2);
    color = vec4(rgb, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("pow(vec3<f32>(0.5), vec3<f32>(2.2))", wgsl);
    }

    [Fact]
    public void ScalarFirstPowResolvesAsVectorResult()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    vec3 rgb = pow(2.0, vec3(0.5)) + 1.0;
    color = vec4(rgb, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var rgb: vec3<f32> = (pow(vec3<f32>(2.0), vec3<f32>(0.5)) + vec3<f32>(1.0));", wgsl);
        Assert.DoesNotContain("pow(2.0, vec3<f32>(0.5)) + 1.0", wgsl);
    }

    [Fact]
    public void StandaloneIncrementDecrementEmitsTypeCorrectMutatingStatements()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    int i = 0;
    i++;
    --i;
    float t = 0.0;
    t++;
    --t;
    uint u = uint(0);
    u++;
    --u;
    color = vec4(t + float(i) + float(u), 0.0, 0.0, 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("i = i + 1;", wgsl);
        Assert.Contains("i = i - 1;", wgsl);
        Assert.Contains("t = t + 1.0;", wgsl);
        Assert.Contains("t = t - 1.0;", wgsl);
        Assert.Contains("u = u + 1u;", wgsl);
        Assert.Contains("u = u - 1u;", wgsl);
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

    [Fact]
    public void ScalarMatrixConstructorExpandsToDiagonalComponents()
    {
        const string glsl = """
void mainImage(out vec4 color, in vec2 coord) {
    mat3 m = mat3(1.0);
    color = vec4(m[0][0], m[1][1], m[2][2], 1.0);
}
""";

        var wgsl = ShaderToyTranspiler.Translate(glsl);

        Assert.Contains("var m: mat3x3<f32> = mat3x3<f32>(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0);", wgsl);
        Assert.DoesNotContain("mat3x3<f32>(1.0);", wgsl);
    }
}
