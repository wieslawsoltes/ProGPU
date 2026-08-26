using BenchmarkDotNet.Attributes;
using ProGPU.Backend;
using ProGPU.Scene;
using System.Drawing;
using System.Numerics;

namespace ProGPU.SystemDrawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsFlushBenchmarks
{
    private readonly DrawingContext _context = new();
    private readonly WgpuContext _targetContext = new();
    private readonly Graphics _graphics;

    public GraphicsFlushBenchmarks()
    {
        _graphics = Graphics.FromProGpuDrawingContext(
            _context,
            new RectangleF(0, 0, 64, 64),
            Matrix4x4.Identity,
            _targetContext,
            _ => _context.Clear(),
            static () => { });
        _graphics.FillRectangle(Brushes.Black, 0, 0, 1, 1);
        _graphics.Flush();
    }

    [Benchmark]
    public int RecordAndFlushRectangle()
    {
        _graphics.FillRectangle(Brushes.Black, 1, 1, 8, 8);
        _graphics.Flush();
        return _context.Commands.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _graphics.Dispose();
        _targetContext.Dispose();
    }
}
