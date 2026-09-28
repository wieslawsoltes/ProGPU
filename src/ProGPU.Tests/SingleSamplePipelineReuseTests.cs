using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class SingleSamplePipelineReuseTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (uint samples in new uint[] { 1, 4 })
        foreach (bool offscreenFirst in new[] { false, true })
        for (int primitive = 0; primitive < 3; primitive++)
            yield return [samples, offscreenFirst, primitive];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public unsafe void MatchingSingleSampleTargetsReusePipelinesAndExactPixels(uint samples, bool offscreenFirst, int primitive)
    {
        using var context = new WgpuContext();
        context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm,
            CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = samples });
        using var target = new GpuTexture(context, 32, 32, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Pipeline reuse target");
        var visual = new DrawingVisual { Size = new Vector2(32) };
        visual.Context.DrawRectangle(new SolidColorBrush(new Vector4(0.1f, 0.2f, 0.3f, 1)), null, new Rect(0, 0, 32, 32));
        var brush = new SolidColorBrush(new Vector4(0.2f, 0.9f, 0.4f, 0.8f));
        switch (primitive)
        {
            case 0: visual.Context.DrawRectangle(brush, null, new Rect(4, 4, 24, 24)); break;
            case 1: visual.Context.DrawRoundedRectangle(brush, null, new Rect(4, 4, 24, 24), 6); break;
            case 2: visual.Context.DrawEllipse(brush, null, new Vector2(16), 12, 10); break;
        }
        Assert.Equal(0, context.CachedDeviceRenderPipelineCount);
        Draw(offscreenFirst);
        int initialCount = context.CachedDeviceRenderPipelineCount;
        Assert.True(initialCount > 0);
        byte[] first = target.ReadPixels();
        Assert.True(first[(16 * 32 + 16) * 4 + 1] > 170);
        Draw(!offscreenFirst);
        int expectedCount = samples == 1 ? initialCount : 2 * initialCount;
        Assert.Equal(expectedCount, context.CachedDeviceRenderPipelineCount);
        byte[] second = target.ReadPixels();
        Assert.True(second[(16 * 32 + 16) * 4 + 1] > 170);
        if (samples == 1)
            Assert.Equal(first, second);
        Draw(offscreenFirst);
        Draw(!offscreenFirst);
        Assert.Equal(expectedCount, context.CachedDeviceRenderPipelineCount);
        Assert.Equal(second, target.ReadPixels());
        compositor.Dispose();
        Assert.Equal(0, context.CachedDeviceRenderPipelineCount);

        void Draw(bool offscreen)
        {
            if (offscreen)
                compositor.RenderOffscreen(visual, 32, 32, target, padding: 0, dpiScale: 1);
            else
                compositor.RenderScene(visual, 32, 32, target.ViewPtr);
        }
    }
}
