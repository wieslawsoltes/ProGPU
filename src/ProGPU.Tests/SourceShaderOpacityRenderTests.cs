using System;
using System.Linq;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Scene;
using ProGPU.Tests.Headless;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SourceShaderOpacityRenderTests
{
    [Fact]
    public void SourceOpacityOrderingUsesSharedNativeProvider()
    {
        using var window = new HeadlessWindow(64, 64);
        VerifyOrdering(window.Context);
    }

    [Fact]
    public void SourceOpacityOrderingUsesSharedDawnProvider()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var dawn = DawnGpuContext.CreateMetalPresentation();
        VerifyOrdering(dawn.Context);
    }

    private static unsafe void VerifyOrdering(WgpuContext context)
    {
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        compositor.ClearColor = new Vector4(0, 0, 0, 1);
        using var target = new GpuTexture(context, 64, 64, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Source shader opacity target",
            alphaMode: GpuTextureAlphaMode.Premultiplied);

        byte[] Render(Visual visual)
        {
            compositor.RenderScene(visual, 64, 64, target.ViewPtr);
            if (visual.Effect is WpfShaderEffect effect) Assert.False(effect.IsFailed, effect.LastError);
            return target.ReadPixels();
        }

        // Constant output is independent of a completely transparent input.
        // Geometry clipping belongs to the output, even when padding is present.
        foreach (bool cached in new[] { false, true })
        foreach (bool scalarZero in new[] { false, true })
        foreach (bool sourceOrdering in new[] { false, true })
        {
            var visual = CreateVisual(CreateEffect(sourceOrdering, constant: true), cached);
            visual.Opacity = scalarZero ? 0f : 1f;
            visual.OpacityMask = new SolidColorBrush(scalarZero ? Vector4.One : Vector4.Zero);
            visual.ClipBounds = new Rect(4, 2, 24, 12);
            var cold = Render(visual);
            Assert.Equal(cold, Render(visual));
            for (int y = 0; y < 64; ++y)
            for (int x = 0; x < 64; ++x)
            {
                int index = (y * 64 + x) * 4;
                byte red = sourceOrdering && x >= 20 && x < 44 && y >= 18 && y < 30 ? (byte)255 : (byte)0;
                Assert.Equal(red, cold[index]);
                Assert.Equal((byte)0, cold[index + 1]);
                Assert.Equal((byte)0, cold[index + 2]);
                Assert.Equal((byte)255, cold[index + 3]);
            }
        }

        foreach (bool cached in new[] { false, true })
        {
            var source = CreateVisual(CreateEffect(sourceOrdering: true, constant: false), cached);
            var ordinary = CreateVisual(effect: null, cached);
            source.Opacity = ordinary.Opacity = .5f;
            foreach (bool reversed in new[] { false, true, false })
            {
                // Replace the real mutable source property; do not mutate cache
                // bytes, force invalidation every frame or manufacture a result.
                var mask = new LinearGradientBrush(Vector2.Zero, new Vector2(32, 0),
                    [new GradientStop(new Vector4(1, 1, 1, reversed ? 1 : 0), 0),
                     new GradientStop(new Vector4(1, 1, 1, reversed ? 0 : 1), 1)]);
                source.OpacityMask = ordinary.OpacityMask = mask;
                byte[] baseline = Render(ordinary);
                byte[] cold = Render(source);
                Assert.Equal(baseline, cold);
                Assert.Equal(cold, Render(source));
                Assert.Contains(cold.Where((_, index) => index % 4 == 0), value => value > 0 && value < 128);
                using var independent = new Compositor(context, TextureFormat.Rgba8Unorm,
                    CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
                independent.ClearColor = compositor.ClearColor;
                independent.RenderScene(source, 64, 64, target.ViewPtr);
                Assert.Equal(cold, target.ReadPixels());
            }
        }

        // Parent alpha is still outside the child's effect. Opting in on the
        // source must not reopen an ancestor-excluded subtree.
        var hiddenParent = new ContainerVisual { Size = new Vector2(64), Opacity = 0f };
        hiddenParent.AddChild(CreateVisual(CreateEffect(sourceOrdering: true, constant: true), cached: false));
        byte[] hidden = Render(hiddenParent);
        for (int index = 0; index < hidden.Length; index += 4)
        {
            Assert.Equal((byte)0, hidden[index]);
            Assert.Equal((byte)0, hidden[index + 1]);
            Assert.Equal((byte)0, hidden[index + 2]);
            Assert.Equal((byte)255, hidden[index + 3]);
        }
    }

    private static DrawingVisual CreateVisual(WpfShaderEffect? effect, bool cached)
    {
        var visual = new DrawingVisual
        {
            Size = new Vector2(32, 16), Offset = new Vector2(16, 16),
            Effect = effect, CacheAsLayer = cached, OpacityMaskBounds = new Rect(0, 0, 32, 16)
        };
        visual.Context.DrawRectangle(new SolidColorBrush(Vector4.One), null, new Rect(0, 0, 32, 16));
        return visual;
    }

    private static WpfShaderEffect CreateEffect(bool sourceOrdering, bool constant) =>
        new(new WpfShaderEffectParams
        {
            ShaderKey = constant ? "source_opacity_constant" : "source_opacity_input",
            SamplingMode = TextureSamplingMode.Nearest,
            ShaderSource = constant
                ? "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }"
                : "fn wpf_effect_main(uv: vec2<f32>, inputColor: vec4<f32>) -> vec4<f32> { return inputColor; }"
        }) { Padding = 2f, CaptureSourceVisualOpacity = sourceOrdering };
}
