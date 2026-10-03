using System;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceShaderRasterFrameRenderTests
{
    [Fact]
    public void SourceRasterMappingUsesSharedNativeProvider()
    {
        using var window = new HeadlessWindow(64, 64);
        VerifyFrames(window.Context);
    }

    [Fact]
    public void SourceRasterMappingUsesSharedDawnProvider()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dawn = DawnGpuContext.CreateMetalPresentation();
        VerifyFrames(dawn.Context);
    }

    private static unsafe void VerifyFrames(WgpuContext context)
    {
        var options = CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 };
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm, options);
        compositor.ClearColor = new Vector4(0, 0, 0, 1);
        using var target = new GpuTexture(context, 160, 96, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Source effect raster mapping target",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        byte[] Render(Compositor renderer, DrawingVisual visual, WpfShaderEffect effect, RenderTargetViewport viewport)
        {
            // Logical size and semantic DPI deliberately stay unchanged while
            // the actual two physical viewport axes and origin change.
            renderer.RenderScene(visual, 64, 64, 160, 96, viewport, 1, target.ViewPtr);
            Assert.False(effect.IsFailed, effect.LastError);
            return target.ReadPixels();
        }

        foreach (bool constant in new[] { true, false })
        {
            var effect = new WpfShaderEffect(new WpfShaderEffectParams
            {
                ShaderKey = constant ? "source_raster_constant" : "source_raster_input",
                SamplingMode = TextureSamplingMode.Nearest,
                ShaderSource = constant
                    ? "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }"
                    : "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return inputColor; }"
            })
            {
                SourceCapture = new ShaderEffectSourceCapture(.125, .25, 9.5, 8, 0, 0, 0, 0),
                CaptureSourceVisualOpacity = true
            };
            var visual = new DrawingVisual { Size = new Vector2(16), Effect = effect };
            visual.Context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(2, 2, 4, 4));
            byte[]? first = null;

            for (int state = 0; state < 6; ++state)
            {
                var viewport = state switch
                {
                    1 => new RenderTargetViewport(8, 8, 128, 32),
                    2 => new RenderTargetViewport(8, 8, 64, 64),
                    3 => new RenderTargetViewport(16, 12, 128, 64),
                    _ => new RenderTargetViewport(8, 8, 128, 64)
                };
                if (state == 4)
                {
                    effect.SourceCapture = new ShaderEffectSourceCapture(10.125, 20.25, 9.5, 8, 0, 0, 0, 0);
                    visual.EffectSourceTranslation = new Vector2(-10, -20);
                }
                else if (state == 5)
                {
                    effect.SourceCapture = new ShaderEffectSourceCapture(.125, .25, 9.5, 8, 0, 0, 0, 0);
                    visual.EffectSourceTranslation = null;
                }

                byte[] expected = ExpectedFrame(constant, state);
                byte[] cold = Render(compositor, visual, effect, viewport);
                Assert.Equal(160 * 96 * 4, cold.Length);
                Assert.Equal(expected, cold);
                Assert.Equal(cold, Render(compositor, visual, effect, viewport));
                using var independent = new Compositor(context, TextureFormat.Rgba8Unorm, options);
                independent.ClearColor = compositor.ClearColor;
                Assert.Equal(expected, Render(independent, visual, effect, viewport));
                if (state == 0) first = cold;
                if (state is 4 or 5) Assert.Equal(first, cold);
            }
        }
    }

    private static byte[] ExpectedFrame(bool constant, int state)
    {
        // Literal device-pixel bounds, independent of the product's capture
        // frame, projection helper, UVs and texture dimensions. Output edges
        // avoid pixel centers; implicit ink has integral physical edges.
        (int left, int top, int right, int bottom) bounds = constant
            ? state switch
            {
                1 => (8, 8, 27, 12),
                2 => (8, 8, 18, 16),
                3 => (16, 12, 35, 20),
                _ => (8, 8, 27, 16)
            }
            : state switch
            {
                1 => (12, 9, 20, 11),
                2 => (10, 10, 14, 14),
                3 => (20, 14, 28, 18),
                _ => (12, 10, 20, 14)
            };
        var pixels = new byte[160 * 96 * 4];
        for (int y = 0; y < 96; ++y)
        for (int x = 0; x < 160; ++x)
        {
            int index = (y * 160 + x) * 4;
            if (x >= bounds.left && x < bounds.right && y >= bounds.top && y < bounds.bottom)
            {
                pixels[index] = 255;
                pixels[index + 1] = constant ? (byte)0 : (byte)255;
                pixels[index + 2] = constant ? (byte)0 : (byte)255;
            }
            pixels[index + 3] = 255;
        }
        return pixels;
    }
}
