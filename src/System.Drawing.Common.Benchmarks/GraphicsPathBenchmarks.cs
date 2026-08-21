using BenchmarkDotNet.Attributes;
using System.Drawing.Drawing2D;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class GraphicsPathBenchmarks
{
    private GraphicsPath _path = null!;
    private GraphicsPathIterator _iterator = null!;
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
    }

    [Benchmark]
    public int ExportRetainedPathToCallerStorage() =>
        _path.GetPathPoints(_points) + _path.GetPathTypes(_types);

    [Benchmark]
    public int EnumerateIteratorToCallerStorage() =>
        _iterator.Enumerate(_points, _types);

    [Benchmark]
    public RectangleF QueryAnalyticBounds() => _path.GetBounds();

    [GlobalCleanup]
    public void DisposePath()
    {
        _iterator.Dispose();
        _path.Dispose();
    }
}
