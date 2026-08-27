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
    private Pen _transformedStrokePen = null!;
    private PointF[] _warpDestination = null!;
    private StringFormat _textFormat = null!;
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
        _transformedStrokePen = new Pen(Color.Black, 3f) { LineJoin = LineJoin.Round };
        _transformedStrokePen.ScaleTransform(2.5f, 0.75f);
        _transformedStrokePen.RotateTransform(20f, MatrixOrder.Append);
        _warpDestination =
        [
            new PointF(0f, 0f),
            new PointF(256f, 8f),
            new PointF(12f, 160f),
            new PointF(220f, 192f),
        ];
        _textFormat = StringFormat.GenericTypographic;
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

    [Benchmark]
    public int WidenAnisotropicPenClone()
    {
        using var clone = (GraphicsPath)_strokePath.Clone();
        clone.Widen(_transformedStrokePen);
        return clone.PointCount;
    }

    [Benchmark]
    public int WarpRetainedCurveClone()
    {
        using var clone = (GraphicsPath)_path.Clone();
        clone.Warp(_warpDestination, new RectangleF(0f, 0f, 184f, 92f), null, WarpMode.Bilinear, 0.25f);
        return clone.PointCount;
    }

    [Benchmark]
    public int AddShapedTextOutline()
    {
        using var path = new GraphicsPath();
        path.AddString("LibreWinForms", FontFamily.GenericSansSerif, 0, 24f, PointF.Empty, _textFormat);
        return path.PointCount;
    }

    [GlobalCleanup]
    public void DisposePath()
    {
        _iterator.Dispose();
        _path.Dispose();
        _strokePath.Dispose();
        _strokePen.Dispose();
        _transformedStrokePen.Dispose();
        _textFormat.Dispose();
    }
}
