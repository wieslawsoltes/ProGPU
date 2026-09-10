using BenchmarkDotNet.Attributes;
using ProGPU.Scene;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsPrimitiveBenchmarks
{
    private DrawingContext _context = null!;
    private Graphics _graphics = null!;
    private Pen _pen = null!;
    private PointF[] _points = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new DrawingContext();
        _graphics = Graphics.FromProGpuDrawingContext(
            _context,
            new RectangleF(0f, 0f, 64f, 64f));
        _pen = new Pen(Color.Black);
        _points = [new(0f, 10f), new(10f, 0f), new(20f, 20f), new(30f, 10f)];
        RecordCurveSpan();
    }

    [Benchmark]
    public int RecordCurveSpan()
    {
        _context.Commands.Clear();
        _graphics.DrawCurve(_pen, _points.AsSpan(), 0, 3, 0.5f);
        return _context.Commands.Count;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _graphics.Dispose();
        _pen.Dispose();
    }
}
