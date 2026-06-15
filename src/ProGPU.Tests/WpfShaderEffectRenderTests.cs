using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class WpfShaderEffectRenderTests
{
    [Fact]
    public void WpfShaderEffect_RendersThroughNativeGpuPipeline()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(128, 96);

        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Test Texture");
        texture.WritePixels(new byte[] { 16, 96, 160, 255 });

        var effect = new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = new Rect(24, 20, 64, 48),
            ShaderKey = "test_wpf_native_shader_effect_tint",
            Constants = new[] { 1f, 0.25f, 0.75f, 1f },
            SamplingMode = TextureSamplingMode.Nearest,
            ShaderSource = @"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    let tint = wpf_constant(0u);
    return vec4<f32>(tint.r, inputColor.g * tint.g, tint.b, inputColor.a);
}
"
        };

        window.Content = new ShaderEffectVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);

            var pixels = window.ReadPixels();
            var center = (((20 + 24) * 128) + (24 + 32)) * 4;

            Assert.InRange(pixels[center + 0], 240, 255);
            Assert.InRange(pixels[center + 1], 18, 32);
            Assert.InRange(pixels[center + 2], 180, 205);
            Assert.Equal(255, pixels[center + 3]);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffect_CanSampleNativeSamplerRegister()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(128, 96);

        using var source0 = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Register 0 Texture");
        source0.WritePixels(new byte[] { 255, 0, 0, 255 });

        using var source1 = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Register 1 Texture");
        source1.WritePixels(new byte[] { 0, 192, 64, 255 });

        var effect = new WpfShaderEffectParams
        {
            Rect = new Rect(24, 20, 64, 48),
            ShaderKey = "test_wpf_native_shader_effect_sampler_register_1",
            Samplers = new[]
            {
                new WpfShaderEffectSampler(0, source0, TextureSamplingMode.Nearest),
                new WpfShaderEffectSampler(1, source1, TextureSamplingMode.Nearest)
            },
            ShaderSource = @"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return wpf_sample_register(1u, uv);
}
"
        };

        window.Content = new ShaderEffectVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);

            var pixels = window.ReadPixels();
            var center = (((20 + 24) * 128) + (24 + 32)) * 4;

            Assert.InRange(pixels[center + 0], 0, 12);
            Assert.InRange(pixels[center + 1], 180, 205);
            Assert.InRange(pixels[center + 2], 54, 78);
            Assert.Equal(255, pixels[center + 3]);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectVisual_RendersVisualSourceThroughNativeGpuPipeline()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(128, 96);

        using var sampler0 = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Conflicting Register 0 Texture");
        sampler0.WritePixels(new byte[] { 255, 0, 0, 255 });

        var visual = new ShaderEffectSourceVisual(sampler0);
        window.Content = visual;

        try
        {
            window.Render();

            var effect = Assert.IsType<WpfShaderEffect>(visual.Effect);
            Assert.False(effect.IsFailed, effect.LastError);

            var pixels = window.ReadPixels();
            var center = ((44 * 128) + 56) * 4;

            Assert.InRange(pixels[center + 0], 190, 220);
            Assert.InRange(pixels[center + 1], 20, 35);
            Assert.InRange(pixels[center + 2], 240, 255);
            Assert.Equal(255, pixels[center + 3]);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectVisual_CanBindVisualSourceToNativeSamplerRegister()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(128, 96);

        using var sampler0 = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Explicit Register 0 Texture");
        sampler0.WritePixels(new byte[] { 255, 0, 0, 255 });

        var visual = new ShaderEffectSourceVisual(
            sampler0,
            shaderKey: "test_visual_wpf_shader_effect_source_register_1",
            sourceTextureRegisterIndex: 1,
            shaderSource: @"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(inputColor.g, inputColor.r * 0.5, 1.0, inputColor.a);
}
");
        window.Content = visual;

        try
        {
            window.Render();

            var effect = Assert.IsType<WpfShaderEffect>(visual.Effect);
            Assert.False(effect.IsFailed, effect.LastError);

            var pixels = window.ReadPixels();
            var center = ((44 * 128) + 56) * 4;

            Assert.InRange(pixels[center + 0], 190, 220);
            Assert.InRange(pixels[center + 1], 20, 35);
            Assert.InRange(pixels[center + 2], 240, 255);
            Assert.Equal(255, pixels[center + 3]);
        }
        finally
        {
            window.Content = null;
        }
    }

    private sealed class ShaderEffectVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _effect;

        public ShaderEffectVisual(WpfShaderEffectParams effect)
        {
            _effect = effect;
            Width = 128f;
            Height = 96f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawWpfShaderEffect(_effect);
        }
    }

    private sealed class ShaderEffectSourceVisual : FrameworkElement
    {
        public ShaderEffectSourceVisual(
            GpuTexture conflictingSampler0,
            string shaderKey = "test_visual_wpf_shader_effect_source_tint",
            int sourceTextureRegisterIndex = 0,
            string? shaderSource = null)
        {
            Width = 128f;
            Height = 96f;
            Effect = new WpfShaderEffect(new WpfShaderEffectParams
            {
                ShaderKey = shaderKey,
                SamplingMode = TextureSamplingMode.Nearest,
                SourceTextureRegisterIndex = sourceTextureRegisterIndex,
                Samplers = new[]
                {
                    new WpfShaderEffectSampler(0, conflictingSampler0, TextureSamplingMode.Nearest)
                },
                ShaderSource = shaderSource ?? @"
fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> {
    return vec4<f32>(inputColor.g, inputColor.r * 0.5, 1.0, inputColor.a);
}
"
            });
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.1f, 1f)),
                null,
                new Rect(24, 20, 64, 48));
        }
    }
}
