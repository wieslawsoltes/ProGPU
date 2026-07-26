using System;
using ProGPU.Backend;

namespace ProGPU.Scene;

public static class DrawingContextShaderEffectExtensions
{
    public static void DrawWpfShaderEffect(this DrawingContext context, WpfShaderEffectParams parameters)
    {
        if (!parameters.HasAnyTexture())
        {
            return;
        }

        context.DrawExtension(
            CompositorBuiltInExtensions.WpfShaderEffect,
            dataParam: parameters);
    }

    public static void DrawWpfShaderEffect(
        this DrawingContext context,
        GpuTexture texture,
        Rect rect,
        string wgslEffectFunction,
        string? shaderKey = null,
        ReadOnlySpan<float> constants = default,
        TextureSamplingMode samplingMode = TextureSamplingMode.Linear)
    {
        if (texture == null)
        {
            return;
        }

        var constantArray = CopyConstants(constants);
        context.DrawWpfShaderEffect(new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = rect,
            ShaderSource = wgslEffectFunction,
            ShaderKey = shaderKey ?? string.Empty,
            Constants = constantArray,
            SamplingMode = samplingMode
        });
    }

    private static float[] CopyConstants(ReadOnlySpan<float> constants)
    {
        if (constants.IsEmpty)
        {
            return Array.Empty<float>();
        }

        var copiedConstants = new float[constants.Length];
        for (var i = 0; i < constants.Length; i++)
        {
            copiedConstants[i] = constants[i];
        }

        return copiedConstants;
    }
}
