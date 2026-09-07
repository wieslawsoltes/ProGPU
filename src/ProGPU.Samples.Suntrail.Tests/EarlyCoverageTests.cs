using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class EarlyCoverageTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public unsafe void EarlyCoveragePreservesMaterialPixels(int world)
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        var pipeline = (ProceduralPipeline)compositor.RegisterDrawingExtension(ProceduralDrawingContextExtensions.Definition);
        var view = new GameSurface(); view.Session.StartLevel(world);
        foreach (int dpi in new[] { 1, 3 })
        {
            const uint width = 932, height = 430;
            using var target = new GpuTexture(context, width * (uint)dpi, height * (uint)dpi, TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Coverage differential");
            view.Measure(new(width, height)); view.Arrange(new Rect(0, 0, width, height));
            for (int frame = 0; frame < 3; frame++)
            {
                for (int tick = 0; tick < 220; tick++) view.Session.Step(RoutePilot.GetInput(view.Session));
                view.Batch.Build(view.Session, new(width, height), view.Session.Time); view.Invalidate();
                pipeline.EnableEarlyCoverage = false;
                compositor.RenderScene(view, width, height, target.Width, target.Height, dpi, target.ViewPtr); var expected = target.ReadPixels();
                pipeline.EnableEarlyCoverage = true;
                compositor.RenderScene(view, width, height, target.Width, target.Height, dpi, target.ViewPtr);
                AssertSamePixels(expected, target.ReadPixels(), $"world-{world + 1}-dpi-{dpi}-frame-{frame}");
            }
        }
    }

    // Preserve exact RGBA8 output, including antialiased edges. Keep per-capture diagnostics.
    internal static void AssertSamePixels(byte[] expected, byte[] actual, string name)
    {
        Assert.Equal(expected.Length, actual.Length);
        int maximum = 0, changed = 0; long total = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int delta = Math.Abs(expected[i] - actual[i]); maximum = Math.Max(maximum, delta);
            total += delta; if (delta != 0) changed++;
        }
        string report = FormattableString.Invariant($"maximum={maximum}, changed={changed}/{expected.Length}, mean={(double)total / expected.Length:F8}");
        var folder = Environment.GetEnvironmentVariable("SUNTRAIL_ARTIFACTS") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/suntrail"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"coverage-{name}.txt"), report);
        Assert.True(maximum == 0, report);
    }
}
