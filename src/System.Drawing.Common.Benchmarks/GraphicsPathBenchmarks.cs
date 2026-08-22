using BenchmarkDotNet.Attributes;
using System.Drawing.Drawing2D;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsPathBenchmarks
{
    private GraphicsPath _path = null!;
    private GraphicsPathIterator _iterator = null!;
    private GraphicsPath _strokePath = null!;
    private Pen _strokePen = null!;
    private PointF[] _points = null!;
    private byte[] _types = null!;

    [GlobalSetup]
    public void CreatePath()
    {
        _path = new GraphicsPath();
        for (int index = 0; index < 16; index++)
        {
            _path.AddEllipse(index * 8f, index * 4f, 64f, 32f);
        }

        _points = new PointF[_path.PointCount];
        _types = new byte[_path.PointCount];
        _iterator = new GraphicsPathIterator(_path);
        _strokePath = new GraphicsPath();
        _strokePath.AddLines(
        [
            new PointF(0f, 0f),
            new PointF(128f, 0f),
            new PointF(128f, 64f),
            new PointF(16f, 64f)
        ]);
        _strokePen = new Pen(Color.Black, 3f) { LineJoin = LineJoin.Round };
    }

    [Benchmark]
    public int ExportRetainedPathToCallerStorage() =>
        _path.GetPathPoints(_points) + _path.GetPathTypes(_types);

    [Benchmark]
    public int EnumerateIteratorToCallerStorage() =>
        _iterator.Enumerate(_points, _types);

    [Benchmark]
    public RectangleF QueryAnalyticBounds() => _path.GetBounds();

    [Benchmark]
    public bool QueryRetainedStrokeOutline() => _strokePath.IsOutlineVisible(64f, 1f, _strokePen);

    [Benchmark]
    public int WidenRetainedCurveClone()
    {
        using var clone = (GraphicsPath)_path.Clone();
        clone.Widen(_strokePen);
        return clone.PointCount;
    }

    [GlobalCleanup]
    public void DisposePath()
    {
        _iterator.Dispose();
        _path.Dispose();
        _strokePath.Dispose();
        _strokePen.Dispose();
    }
}
