using System;
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
    public void WpfShaderEffect_HonorsActiveOpacityMask()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);

        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Mask Source");
        texture.WritePixels(new byte[] { 255, 0, 0, 255 });

        var unmasked = new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = new Rect(20, 25, 40, 40),
            ShaderKey = "test_wpf_native_shader_effect_unmasked",
            SamplingMode = TextureSamplingMode.Nearest
        };

        var masked = new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = new Rect(90, 25, 40, 40),
            ShaderKey = "test_wpf_native_shader_effect_masked",
            SamplingMode = TextureSamplingMode.Nearest
        };

        window.Content = new MaskedShaderEffectVisual(unmasked, masked);

        try
        {
            window.Render();

            Assert.False(unmasked.IsFailed, unmasked.LastError);
            Assert.False(masked.IsFailed, masked.LastError);

            var pixels = window.ReadPixels();
            var background = ReadPixel(pixels, window.Width, x: 10, y: 10);
            var visible = ReadPixel(pixels, window.Width, x: 40, y: 45);
            var hidden = ReadPixel(pixels, window.Width, x: 110, y: 45);

            Assert.True(visible.R >= 220, $"Expected unmasked shader effect to render red, found {visible}.");
            Assert.True(visible.G <= 35, $"Expected unmasked shader effect to keep green low, found {visible}.");
            Assert.True(visible.B <= 35, $"Expected unmasked shader effect to keep blue low, found {visible}.");
            Assert.Equal(255, visible.A);

            AssertColorNear(background, hidden, tolerance: 12);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffect_PreservesTextureCoordinatesWhenClipped()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(160, 90);

        using var texture = new GpuTexture(
            window.Context,
            2,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Clip Source");
        texture.WritePixels(new byte[]
        {
            255, 0, 0, 255,
            0, 220, 0, 255
        });

        var effect = new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = new Rect(20f, 25f, 80f, 40f),
            ShaderKey = $"test_wpf_native_shader_effect_clipped_uv_{Guid.NewGuid():N}",
            SamplingMode = TextureSamplingMode.Nearest
        };

        window.Content = new ClippedShaderEffectVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);

            var pixels = window.ReadPixels();
            var clippedLeft = ReadPixel(pixels, window.Width, x: 65, y: 45);

            Assert.True(clippedLeft.G >= 180, $"Expected clipped shader effect to preserve right-half green UVs, found {clippedLeft}.");
            Assert.True(clippedLeft.R <= 80, $"Expected clipped shader effect not to stretch left red UVs, found {clippedLeft}.");
            Assert.Equal(255, clippedLeft.A);
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

    [Fact]
    public void WpfShaderEffectVisualBlendsPremultipliedVisualSource()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        var visual = new SemiTransparentShaderEffectSourceVisual();
        window.Content = visual;

        try
        {
            window.Render();

            var effect = Assert.IsType<WpfShaderEffect>(visual.Effect);
            Assert.False(effect.IsFailed, effect.LastError);

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 112, 145);
            Assert.InRange(pixel.G, 0, 16);
            Assert.InRange(pixel.B, 0, 16);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectAppliesOpacityOnceForStraightAlphaSource()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Straight Opacity Source",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels(new byte[] { 255, 0, 0, 255 });

        var effect = new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = new Rect(0f, 0f, 32f, 32f),
            ShaderKey = $"test_wpf_native_shader_effect_straight_opacity_{Guid.NewGuid():N}",
            SamplingMode = TextureSamplingMode.Nearest
        };

        window.Content = new StraightOpacityShaderEffectVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 120, 136);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectPremultipliesStraightSourceForScreenBlend()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        using var texture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Screen Straight Source",
            alphaMode: GpuTextureAlphaMode.Straight);
        texture.WritePixels(new byte[] { 255, 0, 0, 128 });

        var effect = new WpfShaderEffectParams
        {
            Texture = texture,
            Rect = new Rect(0f, 0f, 32f, 32f),
            ShaderKey = $"test_wpf_native_shader_effect_screen_straight_{Guid.NewGuid():N}",
            SamplingMode = TextureSamplingMode.Nearest
        };

        window.Content = new ScreenBlendShaderEffectVisual(effect);

        try
        {
            window.Render();

            Assert.False(effect.IsFailed, effect.LastError);

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 120, 136);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.Equal(255, pixel.A);
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void WpfShaderEffectShaderModuleCacheSeparatesSourceAlphaModes()
    {
        var window = HeadlessWindow.Shared;
        window.Resize(32, 32);

        var shaderKey = $"test_wpf_shader_effect_alpha_module_key_{Guid.NewGuid():N}";
        using var straightTexture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Straight Cache Source",
            alphaMode: GpuTextureAlphaMode.Straight);
        straightTexture.WritePixels(new byte[] { 255, 0, 0, 255 });

        using var premultipliedTexture = new GpuTexture(
            window.Context,
            1,
            1,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "WPF Shader Effect Premultiplied Cache Source",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        premultipliedTexture.WritePixels(new byte[] { 128, 0, 0, 128 });

        var straightEffect = new WpfShaderEffectParams
        {
            Texture = straightTexture,
            Rect = new Rect(0f, 0f, 32f, 32f),
            ShaderKey = shaderKey,
            SamplingMode = TextureSamplingMode.Nearest
        };
        var premultipliedEffect = new WpfShaderEffectParams
        {
            Texture = premultipliedTexture,
            Rect = new Rect(0f, 0f, 32f, 32f),
            ShaderKey = shaderKey,
            SamplingMode = TextureSamplingMode.Nearest
        };

        try
        {
            window.Content = new StraightOpacityShaderEffectVisual(straightEffect);
            window.Render();
            Assert.False(straightEffect.IsFailed, straightEffect.LastError);

            window.Content = new StraightOpacityShaderEffectVisual(premultipliedEffect);
            window.Render();
            Assert.False(premultipliedEffect.IsFailed, premultipliedEffect.LastError);

            var pixel = ReadPixel(window.ReadPixels(), window.Width, x: 16, y: 16);

            Assert.InRange(pixel.R, 52, 76);
            Assert.InRange(pixel.G, 0, 8);
            Assert.InRange(pixel.B, 0, 8);
            Assert.Equal(255, pixel.A);
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

    private sealed class StraightOpacityShaderEffectVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _effect;

        public StraightOpacityShaderEffectVisual(WpfShaderEffectParams effect)
        {
            _effect = effect;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushOpacity(0.5f);
            context.DrawWpfShaderEffect(_effect);
            context.PopOpacity();
        }
    }

    private sealed class ClippedShaderEffectVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _effect;

        public ClippedShaderEffectVisual(WpfShaderEffectParams effect)
        {
            _effect = effect;
            Width = 160f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.PushClip(new Rect(60f, 25f, 40f, 40f));
            context.DrawWpfShaderEffect(_effect);
            context.PopClip();
        }
    }

    private sealed class ScreenBlendShaderEffectVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _effect;

        public ScreenBlendShaderEffectVisual(WpfShaderEffectParams effect)
        {
            _effect = effect;
            Width = 32f;
            Height = 32f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
            context.PushBlendMode(GpuBlendMode.Screen);
            context.DrawWpfShaderEffect(_effect);
            context.PopBlendMode();
        }
    }

    private static RgbaPixel ReadPixel(byte[] pixels, uint width, int x, int y)
    {
        var index = ((y * (int)width) + x) * 4;
        return new RgbaPixel(
            pixels[index + 0],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void AssertColorNear(RgbaPixel expected, RgbaPixel actual, int tolerance)
    {
        Assert.InRange(Math.Abs(expected.R - actual.R), 0, tolerance);
        Assert.InRange(Math.Abs(expected.G - actual.G), 0, tolerance);
        Assert.InRange(Math.Abs(expected.B - actual.B), 0, tolerance);
        Assert.InRange(Math.Abs(expected.A - actual.A), 0, tolerance);
    }

    private readonly record struct RgbaPixel(byte R, byte G, byte B, byte A);

    private sealed class MaskedShaderEffectVisual : FrameworkElement
    {
        private readonly WpfShaderEffectParams _unmasked;
        private readonly WpfShaderEffectParams _masked;

        public MaskedShaderEffectVisual(WpfShaderEffectParams unmasked, WpfShaderEffectParams masked)
        {
            _unmasked = unmasked;
            _masked = masked;
            Width = 160f;
            Height = 90f;
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawWpfShaderEffect(_unmasked);
            context.PushOpacityMask(
                new SolidColorBrush(new Vector4(0f, 0f, 0f, 1f)),
                new Rect(90f, 25f, 40f, 40f));
            context.DrawWpfShaderEffect(_masked);
            context.PopOpacityMask();
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

    private sealed class SemiTransparentShaderEffectSourceVisual : FrameworkElement
    {
        public SemiTransparentShaderEffectSourceVisual()
        {
            Width = 32f;
            Height = 32f;
            Effect = new WpfShaderEffect(new WpfShaderEffectParams
            {
                ShaderKey = "test_visual_wpf_shader_effect_premultiplied_source",
                SamplingMode = TextureSamplingMode.Nearest
            });
        }

        public override void OnRender(DrawingContext context)
        {
            context.DrawRectangle(
                new SolidColorBrush(new Vector4(1f, 0f, 0f, 0.5f)),
                null,
                new Rect(0f, 0f, 32f, 32f));
        }
    }
}
