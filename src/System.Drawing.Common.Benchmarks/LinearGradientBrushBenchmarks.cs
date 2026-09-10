using BenchmarkDotNet.Attributes;
using System.Drawing.Drawing2D;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class LinearGradientBrushBenchmarks
{
    private LinearGradientBrush _brush = null!;

    [GlobalSetup]
    public void CreateBrush()
    {
        _brush = new LinearGradientBrush(
            new RectangleF(0f, 0f, 128f, 64f),
            Color.Black,
            Color.White,
            LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend(8)
            {
                Colors =
                [
                    Color.Black,
                    Color.Navy,
                    Color.Blue,
                    Color.Cyan,
                    Color.Lime,
                    Color.Yellow,
                    Color.Red,
                    Color.White
                ],
                Positions = [0f, 0.12f, 0.28f, 0.42f, 0.58f, 0.72f, 0.88f, 1f]
            },
            GammaCorrection = true,
            WrapMode = WrapMode.TileFlipXY
        };
    }

    [Benchmark]
    public ProGPU.Vector.Brush LowerEightStopGradient() => _brush.ToProGpuBrush();

    [GlobalCleanup]
    public void DisposeBrush() => _brush.Dispose();
}
