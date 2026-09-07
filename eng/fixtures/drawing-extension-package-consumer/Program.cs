using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Vector;
using Silk.NET.WebGPU;

namespace PackageConsumer;

internal static class Program
{
    private sealed class Frame;
    private static readonly DrawingExtension<Frame> Definition = new("Package consumer triangle", static () => new Triangle());
    private static readonly Frame Data = new();
    private static readonly Rect Bounds = new(0, 0, 64, 64);

    public static void Main(string[] args)
    {
        // This fixture is copied outside the repository before building. It has
        // no signing key, project references, friend access, or source imports.
        if (typeof(Program).Assembly.GetName().GetPublicKeyToken()?.Length != 0)
            throw new Exception("The external package consumer must remain unsigned.");
        var window = new Window(); window.RegisterDrawingExtension(Definition); window.RegisterDrawingExtension(Definition);
        if (window.Compositor is not null) throw new Exception("Registration initialized the renderer eagerly.");
        var context = new DrawingContext(); context.DrawExtension(Definition, Bounds, Data);
        var command = context.Commands[0];
        if (!ReferenceEquals(command.DataParam, Data) || command.Rect != Bounds) throw new Exception("Payload or bounds changed.");
        if (!ShaderResource.Load<ProgramAnchor>("Consumer.wgsl").Contains("@fragment", StringComparison.Ordinal))
            throw new Exception("Packaged consumer shader was not embedded.");
        RecordBenchmark(command.ExtensionId);
        if (args.Contains("--gpu", StringComparer.Ordinal)) RunGpu();
        Console.WriteLine("Unsigned package-only drawing extension consumer passed.");
    }

    private sealed class ProgramAnchor;
    private static void Record(DrawingContext context, bool typed, int id)
    {
        context.Clear();
        if (typed) context.DrawExtension(Definition, Bounds, Data);
        else context.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawExtension, ExtensionId = id, Rect = Bounds, DataParam = Data });
    }
    private static void RecordBenchmark(int id)
    {
        const int iterations = 100_000;
        var context = new DrawingContext(); context.EnsureCommandCapacity(1);
        // Let tiered JIT/PGO finish before collecting steady-state samples.
        long warmup = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(warmup).TotalSeconds < 2)
        { Record(context, false, id); Record(context, true, id); }
        var legacy = new double[31]; var typed = new double[31];
        for (int sample = 0; sample < legacy.Length; sample++)
            for (int pass = 0; pass < 2; pass++)
            {
                bool useTyped = (sample + pass) % 2 == 0;
                long allocated = GC.GetAllocatedBytesForCurrentThread(); long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) Record(context, useTyped, id);
                double ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (bytes != 0) throw new Exception($"Recording allocated {bytes} bytes.");
                (useTyped ? typed : legacy)[sample] = ns;
            }
        Report("legacy.record.ns", legacy); Report("typed.record.ns", typed);
        Console.WriteLine("record.allocated=0; record.commands=1; payload.copied.bytes=0; recording.native.calls=0");
    }

    private static unsafe void RunGpu()
    {
        using var context = new WgpuContext(); context.Initialize(null);
        using var target = new GpuTexture(context, 64, 64, TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc);
        using var compositor = new Compositor(context, TextureFormat.Rgba8Unorm, CompositorOptions.Default with { EnableGpuHitTesting = false, PrimarySampleCount = 1 });
        var typed = (Triangle)compositor.RegisterDrawingExtension(Definition);
        var legacy = new Triangle(); compositor.RegisterExtension(7101, legacy);
        var typedVisual = new DrawingVisual { Size = new(64, 64) };
        typedVisual.Context.DrawExtension(Definition, Bounds, Data);
        var legacyVisual = new DrawingVisual { Size = new(64, 64) };
        legacyVisual.Context.Commands.Add(new RenderCommand { Type = RenderCommandType.DrawExtension, ExtensionId = 7101, Rect = Bounds, DataParam = Data });
        void Render(DrawingVisual visual) { compositor.RenderScene(visual, 64, 64, 64, 64, 1, target.ViewPtr); context.WaitIdle(); }
        Render(legacyVisual); byte[] baseline = target.ReadPixels();
        Render(typedVisual); byte[] actual = target.ReadPixels();
        if (!baseline.AsSpan().SequenceEqual(actual) || actual[1] < 190 || actual[0] > 40) throw new Exception("Extension pixels differ or the triangle did not render.");
        for (int i = 0; i < 120; i++) Render(i % 2 == 0 ? typedVisual : legacyVisual);
        var baselineTime = new double[600]; var typedTime = new double[600];
        for (int i = 0; i < 600; i++)
            for (int pass = 0; pass < 2; pass++)
            {
                bool useTyped = (i + pass) % 2 == 0;
                long start = Stopwatch.GetTimestamp(); Render(useTyped ? typedVisual : legacyVisual);
                (useTyped ? typedTime : baselineTime)[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        Report("legacy.submission.wait.ms", baselineTime); Report("typed.submission.wait.ms", typedTime);
        Console.WriteLine($"gpu.pixel.channels.equal={actual.Length}; typed.draws={typed.Draws}; legacy.draws={legacy.Draws}; retained.upload.bytes=0");
    }
    private static void Report(string name, double[] values)
    {
        Array.Sort(values);
        Console.WriteLine(FormattableString.Invariant($"{name}: p50={values[(int)(values.Length * .50)]:F3} p95={values[(int)(values.Length * .95)]:F3} p99={values[(int)(values.Length * .99)]:F3}"));
    }

    private sealed unsafe class Triangle : ICompositorExtension, IDisposable
    {
        private static readonly string Shader = ShaderResource.Load<ProgramAnchor>("Consumer.wgsl");
        private RenderPipelineCache? _cache;
        private RenderPipeline* _pipeline;
        public int Draws;
        public void Compile(Compositor compositor, IRenderDataProvider? provider, Matrix4x4 transform, ref RenderCommand command)
        {
            if (command.DataParam is not Frame) throw new Exception("Wrong retained payload.");
        }
        public void Render(Compositor compositor, void* encoder, bool offscreen, in Compositor.CompositorDrawCall command)
        {
            if (_pipeline == null)
            {
                _cache = new(compositor.Context);
                var shader = _cache.GetOrCreateShader("consumer", Shader);
                _pipeline = _cache.GetOrCreateRenderPipeline("triangle", shader, ReadOnlySpan<VertexBufferLayout>.Empty,
                    targetFormat: compositor.RenderFormat, sampleCount: 1, sourceAlphaMode: GpuTextureAlphaMode.Premultiplied);
            }
            var api = compositor.Context.Api; var pass = (RenderPassEncoder*)encoder;
            api.RenderPassEncoderSetPipeline(pass, _pipeline); api.RenderPassEncoderDraw(pass, 3, 1, 0, 0); Draws++;
        }
        public void Dispose() { _cache?.Dispose(); _cache = null; _pipeline = null; }
    }
}
