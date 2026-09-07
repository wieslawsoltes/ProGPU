using System.Numerics;
using ProGPU.Backend;
using ProGPU.GameEngine.Rendering;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;
using ProGPU.Tests.Headless;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class MaterialPageTests
{
    [Fact]
    public void VisiblePagesArePinnedAndStaleHandlesCannotCommit()
    {
        var cache = new MaterialPageCache<int>(2);
        cache.BeginFrame(); Assert.True(cache.TryReserve(1, out var first)); cache.Commit(first);
        Assert.True(cache.TryReserve(2, out var second)); cache.Commit(second);
        Assert.False(cache.TryReserve(3, out _));
        cache.BeginFrame(); Assert.True(cache.TryPin(1, out _));
        Assert.True(cache.TryReserve(3, out var third)); cache.Commit(third);
        Assert.True(cache.IsReady(first)); Assert.False(cache.IsReady(second));
        Assert.Throws<InvalidOperationException>(() => cache.Commit(second));
        Assert.False(cache.IsReady(default)); Assert.Equal(2, cache.Count);
        cache.BeginFrame(); Assert.True(cache.TryReserve(4, out var canceled)); cache.Cancel(canceled);
        Assert.False(cache.TryPin(4, out _)); Assert.False(cache.IsReady(canceled));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 1)]
    [InlineData(7, 3)]
    public unsafe void MaterialCompilerConvergesAndRetainsLiveLighting(int world, int dpi)
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        var pipeline = (ProceduralPipeline)compositor.RegisterDrawingExtension(ProceduralDrawingContextExtensions.Definition);
        const uint width = 932, height = 430;
        using var target = new GpuTexture(context, width * (uint)dpi, height * (uint)dpi, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "Material page image comparison");
        var view = new GameSurface(); view.Session.StartLevel(world);
        view.Measure(new(width, height)); view.Arrange(new Rect(0, 0, width, height));
        for (int i = 0; i < 1440; i++) view.Session.Step(RoutePilot.GetInput(view.Session));
        view.Batch.Build(view.Session, new(width, height), view.Session.Time); view.Invalidate();
        void Render() => compositor.RenderScene(view, width, height, target.Width, target.Height, dpi, target.ViewPtr);
        var errors = new List<string>(); void Error(ErrorType type, string message) => errors.Add(message);
        WgpuContext.OnWebGpuError += Error;
        try
        {
            Render(); var direct = target.ReadPixels();
            pipeline.EnableMaterialPages = true;
            for (int i = 0; i < 40; i++) Render();
            context.WaitIdle(); Assert.True(errors.Count == 0, string.Join("\n", errors));
            var cached = target.ReadPixels();
            string folder = Path.Combine(FindRoot(), "artifacts/suntrail/material-pages"); Directory.CreateDirectory(folder);
            PngEncoder.SavePng(Path.Combine(folder, $"world-{world}-direct.png"), direct, target.Width, target.Height);
            PngEncoder.SavePng(Path.Combine(folder, $"world-{world}-cached.png"), cached, target.Width, target.Height);
            double sum = 0, squared = 0; int maximum = 0, large = 0;
            for (int i = 0; i < direct.Length; i++)
            {
                int d = Math.Abs(direct[i] - cached[i]); sum += d; squared += d*d; maximum = Math.Max(maximum, d); if (d > 16) large++;
            }
            string report = $"world={world} dpi={dpi} mean={sum/direct.Length:F4} rms={Math.Sqrt(squared/direct.Length):F4} max={maximum} large={large}/{direct.Length} bakes={pipeline.MaterialBakeCount} visible={pipeline.MaterialVisiblePages} fallback={pipeline.MaterialFallbackPages} resident={pipeline.MaterialResidentPages} bytes={pipeline.MaterialResidentBytes}";
            File.AppendAllText(Path.Combine(folder, "quality.txt"), report + "\n");
            Assert.True(sum / direct.Length < 2, report);
            Assert.True(large < direct.Length / 100, report);
            long uploads = pipeline.UploadedBytes, bakes = pipeline.MaterialBakeCount;
            for (int i = 0; i < 3; i++) Render();
            Assert.Equal(uploads, pipeline.UploadedBytes); Assert.Equal(bakes, pipeline.MaterialBakeCount);
            Assert.Equal(cached, target.ReadPixels());
            Assert.Equal(0, pipeline.MaterialFallbackPages);
            Assert.True(pipeline.MaterialResidentBytes <= 192L*1024*1024);
            pipeline.EnableMaterialPages = false; Render(); Assert.Equal(direct, target.ReadPixels());
        }
        finally { WgpuContext.OnWebGpuError -= Error; }
    }

    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !Directory.Exists(Path.Combine(path.FullName, "artifacts"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository artifact directory missing.");
    }
}
