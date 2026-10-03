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

public sealed class SourceShaderPaddingRenderTests
{
    [Fact]
    public void SourcePaddingFrameUsesSharedNativeProvider()
    {
        using var window = new HeadlessWindow(64, 64);
        VerifyFrames(window.Context);
    }

    [Fact]
    public void SourcePaddingFrameUsesSharedDawnProvider()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dawn = DawnGpuContext.CreateMetalPresentation();
        VerifyFrames(dawn.Context);
    }

    private static unsafe void VerifyFrames(WgpuContext context)
    {
        var options = CompositorOptions.Default with
        {
            EnableGpuHitTesting = false,
            PrimarySampleCount = 1
        };
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm, options);
        compositor.ClearColor = new Vector4(0, 0, 0, 1);
        using var target = new GpuTexture(context, 64, 64, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Source shader padding target",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        byte[] Render(Compositor renderer, DrawingVisual visual, WpfShaderEffect effect)
        {
            renderer.RenderScene(visual, 64, 64, target.ViewPtr);
            Assert.False(effect.IsFailed, effect.LastError);
            return target.ReadPixels();
        }

        foreach (bool constant in new[] { true, false })
        {
            // Both programs consume the same actual source. Constant output
            // exposes every padded edge; implicit input must keep the ink at
            // its original position and leave the padded region transparent.
            var effect = CreateEffect(constant);
            var visual = new DrawingVisual
            {
                Size = new Vector2(16, 8),
                Offset = new Vector2(16, 20),
                Effect = effect
            };
            visual.Context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 16, 8));
            byte[]? first = null;

            // Six retained states, not a cross-product of unrelated policies.
            // The initial/swap/reset states have exactly the same capture
            // dimensions: only the four original edges change its origin.
            for (int state = 0; state < 6; ++state)
            {
                switch (state)
                {
                    case 0:
                        effect.SourceCapture = new ShaderEffectSourceCapture(0, 0, 16, 8, 2, 6, 4, 8);
                        break;
                    case 1:
                        effect.SourceCapture = new ShaderEffectSourceCapture(0, 0, 16, 8, 6, 2, 8, 4);
                        break;
                    case 2:
                        effect.SourceCapture = new ShaderEffectSourceCapture(0, 0, 16, 8, 2, 6, 4, 8);
                        break;
                    case 3:
                        // Source space really starts at (10,20), while the
                        // recorded local ink is rebased to (0,0). The handoff
                        // moves the origin, not the already resolved extent.
                        effect.SourceCapture = new ShaderEffectSourceCapture(10, 20, 16, 8, 2, 6, 4, 8);
                        visual.EffectSourceTranslation = new Vector2(-10, -20);
                        break;
                    case 4:
                        // Source opacity is consumed before shader evaluation;
                        // the real geometry clip still cuts the final output.
                        visual.Opacity = .5f;
                        visual.ClipBounds = new Rect(-2, -1, 12, 6);
                        break;
                    case 5:
                        // Reset both nullable source properties on the same
                        // owner. Ordinary scalar padding remains exactly 3.
                        effect.SourceCapture = null;
                        visual.EffectSourceTranslation = null;
                        visual.Opacity = 1f;
                        visual.ClipBounds = null;
                        break;
                }

                byte[] expected = ExpectedFrame(constant, state);
                byte[] cold = Render(compositor, visual, effect);
                Assert.Equal(64 * 64 * 4, cold.Length);
                Assert.Equal(expected, cold);
                // No re-recording, manual invalidation or property assignment
                // intervenes between these real warm-cache submissions.
                Assert.Equal(cold, Render(compositor, visual, effect));
                using var independent = new Compositor(context, TextureFormat.Rgba8Unorm, options);
                independent.ClearColor = compositor.ClearColor;
                Assert.Equal(expected, Render(independent, visual, effect));

                if (state == 0) first = cold;
                if (state is 2 or 3) Assert.Equal(first, cold);
                Assert.Same(effect, visual.Effect);
            }
        }
    }

    private static byte[] ExpectedFrame(bool constant, int state)
    {
        // Literal integer physical rectangles at DPI 1, independent of
        // EffectCaptureFrame and of any texture/readback metadata. No ceil,
        // max-side padding, bounds lookup or product frame calculation is used.
        (int left, int top, int right, int bottom) bounds = constant
            ? state switch
            {
                1 => (8, 14, 36, 30),
                4 => (14, 19, 26, 25),
                5 => (13, 17, 35, 31),
                _ => (12, 18, 40, 34)
            }
            : state == 4 ? (16, 20, 26, 25) : (16, 20, 32, 28);
        byte ink = !constant && state == 4 ? (byte)128 : (byte)255;
        var pixels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; ++y)
        for (int x = 0; x < 64; ++x)
        {
            int index = (y * 64 + x) * 4;
            if (x >= bounds.left && x < bounds.right && y >= bounds.top && y < bounds.bottom)
            {
                pixels[index] = ink;
                pixels[index + 1] = constant ? (byte)0 : ink;
                pixels[index + 2] = constant ? (byte)0 : ink;
            }
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static WpfShaderEffect CreateEffect(bool constant) =>
        new(new WpfShaderEffectParams
        {
            ShaderKey = constant ? "source_padding_constant" : "source_padding_input",
            SamplingMode = TextureSamplingMode.Nearest,
            ShaderSource = constant
                ? "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }"
                : "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return inputColor; }"
        }) { Padding = 3f, CaptureSourceVisualOpacity = true };
}
