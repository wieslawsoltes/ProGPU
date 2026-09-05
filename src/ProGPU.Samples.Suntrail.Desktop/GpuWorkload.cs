using System.Diagnostics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;
using Silk.NET.WebGPU;

namespace ProGPU.Samples.Suntrail.Desktop;

/// <summary>Opt-in serialized GPU completion workload; timings are latency, not displayed FPS.</summary>
internal static class GpuWorkload
{
    public static unsafe void Run(string prefix, bool cacheSky, int frames)
    {
        const uint width = 932, height = 430, dpi = 3;
        using var context = new WgpuContext(); context.Initialize(null);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm);
        var pipeline = new ProceduralPipeline { EnableSkyCache = cacheSky };
        compositor.RegisterExtension(ProceduralDrawingContextExtensions.ExtensionId, pipeline);
        using var target = new GpuTexture(context, width * dpi, height * dpi, TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment, "Suntrail phone-sized latency workload");
        var view = new GameSurface(); view.Session.StartLevel(0);
        view.Measure(new(width, height)); view.Arrange(new Rect(0, 0, width, height));
        var latency = new double[frames]; var cpu = new double[frames];
        long allocated = 0, uploads = 0;
        // Warm both the GPU and CLR, then restart the simulation to identical input/time.
        for (int frame = -120; frame < frames; frame++)
        {
            if (frame == 0) { view.Session.StartLevel(0); allocated = GC.GetTotalAllocatedBytes(true); uploads = pipeline.UploadedBytes; }
            for (int tick = 0; tick < 2; tick++) view.Session.Step(RoutePilot.GetInput(view.Session));
            view.Batch.Build(view.Session, new(width, height), view.Session.Time); view.Invalidate();
            long start = Stopwatch.GetTimestamp();
            compositor.RenderScene(view, width, height, target.Width, target.Height, dpi, target.ViewPtr);
            double submitted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.WaitIdle();
            if (frame >= 0) { latency[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds; cpu[frame] = submitted; }
        }
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        uploads = pipeline.UploadedBytes - uploads;
        context.TryCaptureNativeResourceSnapshot(out var native);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(prefix))!);
        using (var writer = new StreamWriter(prefix + ".csv"))
        {
            writer.WriteLine("frame,submit_cpu_ms,serialized_completion_ms");
            for (int i = 0; i < frames; i++) writer.WriteLine(FormattableString.Invariant($"{i},{cpu[i]:F6},{latency[i]:F6}"));
        }
        Array.Sort(latency); Array.Sort(cpu);
        double P(double[] values, double p) => values[(int)Math.Ceiling(values.Length * p) - 1];
        var summary = FormattableString.Invariant($"adapter={context.AdapterName} logical={width}x{height} framebuffer={target.Width}x{target.Height} dpi={dpi} samples={compositor.Options.PrimarySampleCount} frames={frames} warmup=120 skyCache={cacheSky}\nserializedCompletionMs p50={P(latency,.5):F3} p95={P(latency,.95):F3} p99={P(latency,.99):F3}\nsubmitCpuMs p50={P(cpu,.5):F3} p95={P(cpu,.95):F3} p99={P(cpu,.99):F3}\nallocatedBytes={allocated} uploadBytes={uploads} skyBakes={pipeline.SkyBakeCount} skyResidentBytes={pipeline.SkyResidentBytes} metalAllocatedBytes={native.MetalAllocatedBytes}\nfinalX={view.Session.Position.X} finalY={view.Session.Position.Y} tick={view.Session.Tick} deaths={view.Session.Deaths}\n");
        File.WriteAllText(prefix + ".txt", summary); Console.Write(summary);
    }
}
