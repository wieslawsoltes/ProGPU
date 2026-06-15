using System;
using ProGPU.Backend;

namespace ProGPU.Scene;

public static class DrawingContextShaderEffectExtensions
{
    public static void DrawWpfShaderEffect(this DrawingContext context, WpfShaderEffectParams parameters)
    {
        if (parameters.Texture == null)
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

        var constantArray = constants.IsEmpty ? Array.Empty<float>() : constants.ToArray();
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
}
