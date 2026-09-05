using Microsoft.UI.Xaml;
using ProGPU.Samples.Suntrail.Presentation;
using ProGPU.Samples.Suntrail.Rendering;

namespace ProGPU.Samples.Suntrail.iOS;

// Opt-in, bounded device workload. Never enabled during ordinary play.
internal sealed class DeviceMeasurement
{
    private const int Warmup = 120, Samples = 600;
    private readonly double[] _intervals = new double[Samples], _simulation = new double[Samples], _compositor = new double[Samples];
    private int _frame;
    private long _allocated, _uploaded;
    private GameView _view = null!;
    private Window _window = null!;
    private ProceduralPipeline _pipeline = null!;

    public void Attach(GameView view, Window window)
    {
        _view = view; _window = window;
        _pipeline = (ProceduralPipeline)window.Compositor!.GetDrawingExtension(ProceduralDrawingContextExtensions.Definition)!;
        _pipeline.EnableSpecializedShaders = Environment.GetEnvironmentVariable("SUNTRAIL_GENERIC_SHADER") != "1";
        window.Rendering += Record;
        Console.WriteLine($"SUNTRAIL_MEASURE specializedShaders={_pipeline.EnableSpecializedShaders}");
        Console.WriteLine("SUNTRAIL_MEASURE starting: 120 warmup, 600 measured frames");
    }

    private void Record(object? sender, double delta)
    {
        if (_frame == Warmup) { _allocated = GC.GetTotalAllocatedBytes(true); _uploaded = _pipeline.UploadedBytes; }
        int index = _frame++ - Warmup;
        if (index < 0) return;
        if (index < Samples)
        {
            _intervals[index] = delta * 1000;
            _simulation[index] = _window.FrameMetrics.AnimationTimeMs;
            _compositor[index] = _window.FrameMetrics.CompositorTimeMs;
            return;
        }
        _window.Rendering -= Record;
        long allocated = GC.GetTotalAllocatedBytes(true) - _allocated;
        _window.WgpuContext!.TryCaptureNativeResourceSnapshot(out var native);
        foreach (var values in new[] { _intervals, _simulation, _compositor }) Array.Sort(values);
        static string Percentiles(double[] a) => FormattableString.Invariant($"p50={a[299]:F3} p95={a[569]:F3} p99={a[593]:F3} max={a[^1]:F3}");
        Console.WriteLine("SUNTRAIL_MEASURE intervalMs " + Percentiles(_intervals));
        Console.WriteLine("SUNTRAIL_MEASURE simulationMs " + Percentiles(_simulation));
        Console.WriteLine("SUNTRAIL_MEASURE compositorMs " + Percentiles(_compositor));
        Console.WriteLine(FormattableString.Invariant($"SUNTRAIL_MEASURE allocatedBytes={allocated} uploadedBytes={_pipeline.UploadedBytes - _uploaded} metalBytes={native.MetalAllocatedBytes} draws={_pipeline.Draws} sprites={_view.Surface.Batch.Count} deaths={_view.Surface.Session.Deaths}"));
    }
}
