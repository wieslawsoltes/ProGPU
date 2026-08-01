using System.Runtime.InteropServices;
using SkiaSharp;
using Xunit;

namespace ProGPU.Tests;

public sealed class SkRuntimeEffectCompatibilityTests
{
    private const string ShaderSource = """
        uniform float gain;
        uniform float2 offset;
        uniform float4 tint;
        uniform shader source;

        half4 main(float2 position) {
            return source.eval(position + offset) * gain + tint;
        }
        """;

    [Fact]
    public void ShaderCompilationDiscoversUniformAndChildContractsOnce()
    {
        using var effect = SKRuntimeEffect.CreateShader(ShaderSource, out var errors);

        Assert.NotNull(effect);
        Assert.Equal(string.Empty, errors);
        Assert.Equal(new[] { "gain", "offset", "tint" }, effect.Uniforms);
        Assert.Equal(new[] { "source" }, effect.Children);
        Assert.Equal(28, effect.UniformSize);
    }

    [Fact]
    public void UniformCollectionPacksValuesIntoOneDeterministicBlock()
    {
        using var effect = SKRuntimeEffect.CreateShader(ShaderSource, out _);
        using var uniforms = new SKRuntimeEffectUniforms(effect);
        uniforms["gain"] = 0.5f;
        uniforms["offset"] = new SKPoint(12f, -4f);
        uniforms["tint"] = new SKColorF(1f, 0.25f, 0.5f, 0.75f);

        using var data = uniforms.ToData();
        Assert.Equal(28, data.Size);
        Assert.Equal(
            new[] { 0.5f, 12f, -4f, 1f, 0.25f, 0.5f, 0.75f },
            MemoryMarshal.Cast<byte, float>(data.Span).ToArray());

        uniforms.Reset();
        using var reset = uniforms.ToData();
        Assert.All(reset.Span.ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void ChildrenAndBuildersRetainTypedSlots()
    {
        using var child = SKShader.CreateColor(SKColors.Red);
        using var builder = SKRuntimeEffect.BuildShader(ShaderSource);
        builder.Uniforms["gain"] = 1f;
        builder.Uniforms["offset"] = SKPoint.Empty;
        builder.Uniforms["tint"] = SKColors.Transparent;
        builder.Children["source"] = child;

        Assert.True(builder.Children.Contains("source"));
        Assert.Equal(child, Assert.Single(builder.Children.ToArray()));
        using var shader = builder.Build(SKMatrix.CreateTranslation(3f, 4f));
        Assert.NotNull(shader);

        builder.Children.Reset();
        Assert.Null(Assert.Single(builder.Children.ToArray()));
    }

    [Fact]
    public void InvalidSourceReturnsErrorsAndBuilderThrowsTypedException()
    {
        Assert.Null(SKRuntimeEffect.CreateShader("uniform float gain;", out var errors));
        Assert.NotEmpty(errors);
        Assert.Throws<SKRuntimeEffectBuilderException>(
            () => SKRuntimeEffect.BuildShader("uniform float gain;"));
    }

    [Fact]
    public void UniformValueRejectsMismatchedSlotSize()
    {
        using var effect = SKRuntimeEffect.CreateShader(ShaderSource, out _);
        using var uniforms = new SKRuntimeEffectUniforms(effect);

        Assert.Throws<ArgumentException>(() => uniforms["offset"] = 1f);
        Assert.Throws<ArgumentException>(() => uniforms["missing"] = 1f);
    }
}
