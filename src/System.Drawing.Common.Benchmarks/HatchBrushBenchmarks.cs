using BenchmarkDotNet.Attributes;
using System.Drawing.Drawing2D;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class HatchBrushBenchmarks
{
    private HatchBrush _brush = null!;

    [GlobalSetup]
    public void CreateBrush()
    {
        _brush = new HatchBrush(
            HatchStyle.SolidDiamond,
            Color.CornflowerBlue,
            Color.Transparent);
    }

    [Benchmark]
    public ProGPU.Vector.Brush LowerEightByEightHatchTile() =>
        _brush.ToProGpuBrush();

    [GlobalCleanup]
    public void DisposeBrush() => _brush.Dispose();
}
