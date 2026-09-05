using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class SkyCacheTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public unsafe void UnsupportedExtentOrTransformUsesLiveRendering(bool translated)
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        var pipeline = new ProceduralPipeline();
        compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId, pipeline);
        uint width = translated ? 932u : 4100u;
        const uint height = 430;
        using var target = new GpuTexture(context, width, height, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Sky live fallback");
        var view = new GameSurface(); view.Session.StartLevel(0);
        view.Measure(new(width, height)); view.Arrange(new Rect(0, 0, width, height));
        if (translated) view.Offset = new(17, 11);
        view.Batch.Build(view.Session, new(width, height), 0); view.Invalidate();
        compositor.RenderScene(view, width, height, target.ViewPtr); var expected = target.ReadPixels();
        pipeline.EnableSkyCache = true;
        compositor.RenderScene(view, width, height, target.ViewPtr);
        Assert.Equal(expected, target.ReadPixels());
        Assert.Equal(0, pipeline.SkyBakeCount); Assert.Equal(0, pipeline.SkyResidentBytes);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public unsafe void RetinaReplayMatchesLiveAndInvalidatesForWorldAndSize(int dpi)
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        var pipeline = new ProceduralPipeline();
        compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId, pipeline);
        var view = new GameSurface();
        long bakes = 0;
        foreach (uint width in new uint[] { 844, 932 })
        {
            const uint height = 390;
            view.Measure(new(width, height)); view.Arrange(new Rect(0, 0, width, height));
            using var target = new GpuTexture(context, width * (uint)dpi, height * (uint)dpi,
                TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Retina sky comparison");
            for (int world = 0; world < 2; world++)
            {
                view.Session.StartLevel(world);
                for (int frame = 0; frame < 2; frame++)
                {
                    for (int tick = 0; tick < 180; tick++) view.Session.Step(RoutePilot.GetInput(view.Session));
                    view.Batch.Build(view.Session, new(width, height), view.Session.Time); view.Invalidate();
                    void Render() => compositor.RenderScene(view, width, height, target.Width, target.Height, dpi, target.ViewPtr);
                    pipeline.EnableSkyCache = false; Render(); var expected = target.ReadPixels();
                    pipeline.EnableSkyCache = true; Render();
                    Assert.Equal(expected, target.ReadPixels());
                    if (frame == 0) bakes++;
                    Assert.Equal(bakes, pipeline.SkyBakeCount);
                    Assert.Equal((long)target.Width * target.Height * 16, pipeline.SkyResidentBytes);
                    long uploaded = pipeline.UploadedBytes;
                    Render(); Assert.Equal(uploaded, pipeline.UploadedBytes);
                }
            }
        }
        pipeline.Dispose(); Assert.Equal(0, pipeline.SkyResidentBytes);
    }
}
