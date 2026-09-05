using BenchmarkDotNet.Attributes;
using System.Drawing.Drawing2D;

namespace System.Drawing.Benchmarks;

[MemoryDiagnoser]
public class MatrixBenchmarks
{
    private Matrix _matrix = null!;
    private PointF[] _points = null!;

    [GlobalSetup]
    public void CreatePoints()
    {
        _matrix = new Matrix();
        _matrix.Rotate(0.125f);
        _points = new PointF[1024];
        for (int index = 0; index < _points.Length; index++)
        {
            _points[index] = new PointF(index, -index);
        }
    }

    [Benchmark(OperationsPerInvoke = 1024)]
    public float TransformPointBatch()
    {
        _matrix.TransformPoints((ReadOnlySpan<PointF>)_points);
        return _points[0].X;
    }

    [GlobalCleanup]
    public void DisposeMatrix() => _matrix.Dispose();
}
