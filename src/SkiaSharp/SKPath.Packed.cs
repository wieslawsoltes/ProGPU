using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Vector;

namespace SkiaSharp;

internal enum PackedPathCommandKind : byte
{
    Move,
    Line,
    Quadratic,
    Cubic,
    Close,
}

internal readonly struct PackedPathCommand
{
    public PackedPathCommand(
        PackedPathCommandKind kind,
        Vector2 point0 = default,
        Vector2 point1 = default,
        Vector2 point2 = default)
    {
        Kind = kind;
        Point0 = point0;
        Point1 = point1;
        Point2 = point2;
    }

    public PackedPathCommandKind Kind { get; }
    public Vector2 Point0 { get; }
    public Vector2 Point1 { get; }
    public Vector2 Point2 { get; }
}

internal struct SKPathBoundsAccumulator
{
    private Vector2 _min;
    private Vector2 _max;
    private bool _hasBounds;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Include(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            return;
        }

        if (!_hasBounds)
        {
            _min = point;
            _max = point;
            _hasBounds = true;
            return;
        }

        _min = Vector2.Min(_min, point);
        _max = Vector2.Max(_max, point);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly SKRect ToRect() => _hasBounds
        ? new SKRect(_min.X, _min.Y, _max.X, _max.Y)
        : SKRect.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Translate(Vector2 offset)
    {
        if (_hasBounds)
        {
            _min += offset;
            _max += offset;
        }
    }
}

internal static class SKPathTightBounds
{
    private const double Epsilon = 1e-12;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncludeQuadratic(
        ref SKPathBoundsAccumulator bounds,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2)
    {
        if (p1.X < MathF.Min(p0.X, p2.X) || p1.X > MathF.Max(p0.X, p2.X))
        {
            IncludeQuadraticAxis(ref bounds, p0.X, p1.X, p2.X, p0, p1, p2);
        }

        if (p1.Y < MathF.Min(p0.Y, p2.Y) || p1.Y > MathF.Max(p0.Y, p2.Y))
        {
            IncludeQuadraticAxis(ref bounds, p0.Y, p1.Y, p2.Y, p0, p1, p2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void IncludeCubic(
        ref SKPathBoundsAccumulator bounds,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3)
    {
        var minX = MathF.Min(p0.X, p3.X);
        var maxX = MathF.Max(p0.X, p3.X);
        if (p1.X < minX || p1.X > maxX || p2.X < minX || p2.X > maxX)
        {
            IncludeCubicAxis(ref bounds, p0.X, p1.X, p2.X, p3.X, p0, p1, p2, p3);
        }

        var minY = MathF.Min(p0.Y, p3.Y);
        var maxY = MathF.Max(p0.Y, p3.Y);
        if (p1.Y < minY || p1.Y > maxY || p2.Y < minY || p2.Y > maxY)
        {
            IncludeCubicAxis(ref bounds, p0.Y, p1.Y, p2.Y, p3.Y, p0, p1, p2, p3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IncludeQuadraticAxis(
        ref SKPathBoundsAccumulator bounds,
        double v0,
        double v1,
        double v2,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2)
    {
        var denominator = v0 - 2d * v1 + v2;
        if (Math.Abs(denominator) <= Epsilon)
        {
            return;
        }

        var t = (v0 - v1) / denominator;
        if (t > 0d && t < 1d)
        {
            bounds.Include(EvaluateQuadratic(p0, p1, p2, t));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IncludeCubicAxis(
        ref SKPathBoundsAccumulator bounds,
        double v0,
        double v1,
        double v2,
        double v3,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3)
    {
        var a = -v0 + 3d * v1 - 3d * v2 + v3;
        var b = 2d * (v0 - 2d * v1 + v2);
        var c = v1 - v0;

        if (Math.Abs(a) <= Epsilon)
        {
            if (Math.Abs(b) > Epsilon)
            {
                IncludeCubicAt(ref bounds, -c / b, p0, p1, p2, p3);
            }

            return;
        }

        var discriminant = b * b - 4d * a * c;
        if (discriminant < 0d)
        {
            return;
        }

        var root = Math.Sqrt(Math.Max(0d, discriminant));
        var denominator = 2d * a;
        IncludeCubicAt(ref bounds, (-b + root) / denominator, p0, p1, p2, p3);
        if (root > Epsilon)
        {
            IncludeCubicAt(ref bounds, (-b - root) / denominator, p0, p1, p2, p3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IncludeCubicAt(
        ref SKPathBoundsAccumulator bounds,
        double t,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3)
    {
        if (t > 0d && t < 1d && double.IsFinite(t))
        {
            bounds.Include(EvaluateCubic(p0, p1, p2, p3, t));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 EvaluateQuadratic(Vector2 p0, Vector2 p1, Vector2 p2, double t)
    {
        var oneMinusT = 1d - t;
        return new Vector2(
            (float)(oneMinusT * oneMinusT * p0.X + 2d * oneMinusT * t * p1.X + t * t * p2.X),
            (float)(oneMinusT * oneMinusT * p0.Y + 2d * oneMinusT * t * p1.Y + t * t * p2.Y));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 EvaluateCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t)
    {
        var oneMinusT = 1d - t;
        var oneMinusTSquared = oneMinusT * oneMinusT;
        var tSquared = t * t;
        return new Vector2(
            (float)(
                oneMinusTSquared * oneMinusT * p0.X +
                3d * oneMinusTSquared * t * p1.X +
                3d * oneMinusT * tSquared * p2.X +
                tSquared * t * p3.X),
            (float)(
                oneMinusTSquared * oneMinusT * p0.Y +
                3d * oneMinusTSquared * t * p1.Y +
                3d * oneMinusT * tSquared * p2.Y +
                tSquared * t * p3.Y));
    }
}

/// <summary>
/// Retains the common path-builder verbs in one pooled, contiguous stream. The
/// public object graph is expanded only when a caller requests <see cref="SKPath.Geometry"/>
/// or an operation requires the legacy mutable representation.
/// </summary>
internal sealed class PackedPathData : IDisposable
{
    private const int InitialCommandCapacity = 64;
    private const int MaximumRetainedCommandCapacity = 4_096;
    [ThreadStatic]
    private static PackedPathData? s_threadCache;
    private sealed class CommandBuffer
    {
        private int _references = 1;

        public CommandBuffer(PackedPathCommand[] commands)
        {
            Commands = commands;
        }

        public PackedPathCommand[] Commands { get; set; }
        public bool IsShared => Volatile.Read(ref _references) != 1;

        public void AddReference() => Interlocked.Increment(ref _references);

        public bool Release() => Interlocked.Decrement(ref _references) == 0;
    }

    private CommandBuffer? _commandBuffer;
    private int _count;
    private Vector2 _currentPoint;
    private Vector2 _contourStart;
    private Vector2 _boundsMin;
    private Vector2 _boundsMax;
    private SKPathBoundsAccumulator _tightBounds;
    private bool _hasOpenFigure;
    private bool _figureHasSegments;
    private bool _hasBounds;
    private bool _trackTightBounds;
    private Vector2 _pendingTranslation;
    private int _isRented;

    private PackedPathData()
    {
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;
    public Vector2 CurrentPoint => _currentPoint;
    public Vector2 ContourStart => _contourStart;
    internal ReadOnlySpan<PackedPathCommand> CommandSpan
    {
        get
        {
            ApplyPendingTranslation();
            return _count == 0 ? ReadOnlySpan<PackedPathCommand>.Empty : Commands.AsSpan(0, _count);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PackedPathData Rent(bool trackTightBounds = true)
    {
        var data = s_threadCache;
        if (data is null)
        {
            data = new PackedPathData();
        }
        else
        {
            s_threadCache = null;
        }

        data._trackTightBounds = trackTightBounds;
        Volatile.Write(ref data._isRented, 1);
        return data;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveTo(float x, float y)
    {
        ApplyPendingTranslation();
        var point = new Vector2(x, y);
        if (_hasOpenFigure && !_figureHasSegments &&
            _count > 0 && Commands[_count - 1].Kind == PackedPathCommandKind.Move)
        {
            EnsureWritableCommandStorage(_count);
            Commands[_count - 1] = new PackedPathCommand(PackedPathCommandKind.Move, point);
            RecalculateBounds();
        }
        else
        {
            Append(new PackedPathCommand(PackedPathCommandKind.Move, point));
        }

        _currentPoint = point;
        _contourStart = point;
        _hasOpenFigure = true;
        _figureHasSegments = false;
        IncludeBounds(point);
        if (_trackTightBounds)
        {
            _tightBounds.Include(point);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveToBoundsOnly(float x, float y)
    {
        ApplyPendingTranslation();
        var point = new Vector2(x, y);
        if (_hasOpenFigure && !_figureHasSegments &&
            _count > 0 && Commands[_count - 1].Kind == PackedPathCommandKind.Move)
        {
            EnsureWritableCommandStorage(_count);
            Commands[_count - 1] = new PackedPathCommand(PackedPathCommandKind.Move, point);
            RecalculateBounds();
        }
        else
        {
            Append(new PackedPathCommand(PackedPathCommandKind.Move, point));
        }

        _currentPoint = point;
        _contourStart = point;
        _hasOpenFigure = true;
        _figureHasSegments = false;
        IncludeBounds(point);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LineTo(float x, float y)
    {
        EnsureFigure();
        var point = new Vector2(x, y);
        Append(new PackedPathCommand(PackedPathCommandKind.Line, point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(point);
        if (_trackTightBounds)
        {
            _tightBounds.Include(point);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LineToBoundsOnly(float x, float y)
    {
        EnsureFigureBoundsOnly();
        var point = new Vector2(x, y);
        Append(new PackedPathCommand(PackedPathCommandKind.Line, point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(point);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        EnsureFigure();
        var point = new Vector2(x1, y1);
        Append(new PackedPathCommand(
            PackedPathCommandKind.Quadratic,
            new Vector2(x0, y0),
            point));
        if (_trackTightBounds)
        {
            SKPathTightBounds.IncludeQuadratic(
                ref _tightBounds,
                _currentPoint,
                new Vector2(x0, y0),
                point);
        }
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(new Vector2(x0, y0));
        IncludeBounds(point);
        if (_trackTightBounds)
        {
            _tightBounds.Include(point);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QuadToBoundsOnly(float x0, float y0, float x1, float y1)
    {
        EnsureFigureBoundsOnly();
        var control = new Vector2(x0, y0);
        var point = new Vector2(x1, y1);
        Append(new PackedPathCommand(PackedPathCommandKind.Quadratic, control, point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(control);
        IncludeBounds(point);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        EnsureFigure();
        var point = new Vector2(x2, y2);
        Append(new PackedPathCommand(
            PackedPathCommandKind.Cubic,
            new Vector2(x0, y0),
            new Vector2(x1, y1),
            point));
        if (_trackTightBounds)
        {
            SKPathTightBounds.IncludeCubic(
                ref _tightBounds,
                _currentPoint,
                new Vector2(x0, y0),
                new Vector2(x1, y1),
                point);
        }
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(new Vector2(x0, y0));
        IncludeBounds(new Vector2(x1, y1));
        IncludeBounds(point);
        if (_trackTightBounds)
        {
            _tightBounds.Include(point);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CubicToBoundsOnly(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        EnsureFigureBoundsOnly();
        var firstControl = new Vector2(x0, y0);
        var secondControl = new Vector2(x1, y1);
        var point = new Vector2(x2, y2);
        Append(new PackedPathCommand(
            PackedPathCommandKind.Cubic,
            firstControl,
            secondControl,
            point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(firstControl);
        IncludeBounds(secondControl);
        IncludeBounds(point);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Close()
    {
        if (!_hasOpenFigure)
        {
            return;
        }

        Append(new PackedPathCommand(PackedPathCommandKind.Close));
        _currentPoint = _contourStart;
        _hasOpenFigure = false;
        _figureHasSegments = false;
    }

    public void AddTriangles(ReadOnlySpan<StrokeJoinTriangle> triangles)
    {
        if (triangles.IsEmpty)
        {
            return;
        }

        ApplyPendingTranslation();
        EnsureWritableCommandStorage(checked(_count + triangles.Length * 4));
        var commands = Commands;
        for (var index = 0; index < triangles.Length; index++)
        {
            var triangle = triangles[index];
            commands[_count++] = new PackedPathCommand(PackedPathCommandKind.Move, triangle.P0);
            commands[_count++] = new PackedPathCommand(PackedPathCommandKind.Line, triangle.P1);
            commands[_count++] = new PackedPathCommand(PackedPathCommandKind.Line, triangle.P2);
            commands[_count++] = new PackedPathCommand(PackedPathCommandKind.Close);
            IncludeBounds(triangle.P0);
            IncludeBounds(triangle.P1);
            IncludeBounds(triangle.P2);
            if (_trackTightBounds)
            {
                _tightBounds.Include(triangle.P0);
                _tightBounds.Include(triangle.P1);
                _tightBounds.Include(triangle.P2);
            }
        }

        _currentPoint = triangles[^1].P0;
        _contourStart = _currentPoint;
        _hasOpenFigure = false;
        _figureHasSegments = false;
    }

    public void Reset()
    {
        _count = 0;
        _currentPoint = default;
        _contourStart = default;
        _boundsMin = default;
        _boundsMax = default;
        _tightBounds = default;
        _hasOpenFigure = false;
        _figureHasSegments = false;
        _hasBounds = false;
        _pendingTranslation = default;
    }

    public PackedPathData Clone()
    {
        var clone = Rent(_trackTightBounds);
        clone.ReleaseCommandBuffer();
        clone._count = _count;
        clone._currentPoint = _currentPoint;
        clone._contourStart = _contourStart;
        clone._boundsMin = _boundsMin;
        clone._boundsMax = _boundsMax;
        clone._tightBounds = _tightBounds;
        clone._hasOpenFigure = _hasOpenFigure;
        clone._figureHasSegments = _figureHasSegments;
        clone._hasBounds = _hasBounds;
        clone._pendingTranslation = _pendingTranslation;
        if (_commandBuffer is { } commandBuffer)
        {
            commandBuffer.AddReference();
            clone._commandBuffer = commandBuffer;
        }

        return clone;
    }

    public void Transform(SKMatrix matrix)
    {
        if (matrix.IsIdentity)
        {
            return;
        }

        if (matrix.ScaleX == 1f && matrix.ScaleY == 1f &&
            matrix.SkewX == 0f && matrix.SkewY == 0f &&
            matrix.Persp0 == 0f && matrix.Persp1 == 0f && matrix.Persp2 == 1f &&
            float.IsFinite(matrix.TransX) && float.IsFinite(matrix.TransY))
        {
            Translate(new Vector2(matrix.TransX, matrix.TransY));
            return;
        }

        ApplyPendingTranslation();
        EnsureWritableCommandStorage(_count);

        _hasBounds = false;
        _boundsMin = default;
        _boundsMax = default;
        _tightBounds = default;
        var current = Vector2.Zero;
        for (var index = 0; index < _count; index++)
        {
            ref var command = ref Commands[index];
            switch (command.Kind)
            {
                case PackedPathCommandKind.Move:
                case PackedPathCommandKind.Line:
                {
                    var point = MapPoint(matrix, command.Point0);
                    command = new PackedPathCommand(command.Kind, point);
                    current = point;
                    IncludeBounds(point);
                    if (_trackTightBounds)
                    {
                        _tightBounds.Include(point);
                    }
                    break;
                }

                case PackedPathCommandKind.Quadratic:
                {
                    var control = MapPoint(matrix, command.Point0);
                    var point = MapPoint(matrix, command.Point1);
                    command = new PackedPathCommand(command.Kind, control, point);
                    if (_trackTightBounds)
                    {
                        SKPathTightBounds.IncludeQuadratic(
                            ref _tightBounds,
                            current,
                            control,
                            point);
                    }
                    current = point;
                    IncludeBounds(control);
                    IncludeBounds(point);
                    if (_trackTightBounds)
                    {
                        _tightBounds.Include(point);
                    }
                    break;
                }

                case PackedPathCommandKind.Cubic:
                {
                    var firstControl = MapPoint(matrix, command.Point0);
                    var secondControl = MapPoint(matrix, command.Point1);
                    var point = MapPoint(matrix, command.Point2);
                    command = new PackedPathCommand(command.Kind, firstControl, secondControl, point);
                    if (_trackTightBounds)
                    {
                        SKPathTightBounds.IncludeCubic(
                            ref _tightBounds,
                            current,
                            firstControl,
                            secondControl,
                            point);
                    }
                    current = point;
                    IncludeBounds(firstControl);
                    IncludeBounds(secondControl);
                    IncludeBounds(point);
                    if (_trackTightBounds)
                    {
                        _tightBounds.Include(point);
                    }
                    break;
                }
            }
        }

        _currentPoint = MapPoint(matrix, _currentPoint);
        _contourStart = MapPoint(matrix, _contourStart);
    }

    private void Translate(Vector2 offset)
    {
        _pendingTranslation += offset;
        if (_hasBounds)
        {
            _boundsMin += offset;
            _boundsMax += offset;
        }
        _tightBounds.Translate(offset);
        _currentPoint += offset;
        _contourStart += offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PackedPathCommand TranslateCommand(PackedPathCommand command, Vector2 offset)
    {
        return command.Kind switch
        {
            PackedPathCommandKind.Move or PackedPathCommandKind.Line =>
                new PackedPathCommand(command.Kind, command.Point0 + offset),
            PackedPathCommandKind.Quadratic =>
                new PackedPathCommand(command.Kind, command.Point0 + offset, command.Point1 + offset),
            PackedPathCommandKind.Cubic =>
                new PackedPathCommand(
                    command.Kind,
                    command.Point0 + offset,
                    command.Point1 + offset,
                    command.Point2 + offset),
            _ => command,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 MapPoint(SKMatrix matrix, Vector2 point)
    {
        var mapped = matrix.MapPoint(point.X, point.Y);
        return new Vector2(mapped.X, mapped.Y);
    }

    public SKRect CalculateBounds()
    {
        return _hasBounds
            ? new SKRect(_boundsMin.X, _boundsMin.Y, _boundsMax.X, _boundsMax.Y)
            : SKRect.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKRect CalculateTightBounds()
    {
        if (_trackTightBounds)
        {
            return _tightBounds.ToRect();
        }

        ApplyPendingTranslation();
        var bounds = new SKPathBoundsAccumulator();
        var current = Vector2.Zero;
        for (var index = 0; index < _count; index++)
        {
            ref readonly var command = ref Commands[index];
            switch (command.Kind)
            {
                case PackedPathCommandKind.Move:
                case PackedPathCommandKind.Line:
                    current = command.Point0;
                    bounds.Include(current);
                    break;
                case PackedPathCommandKind.Quadratic:
                    SKPathTightBounds.IncludeQuadratic(
                        ref bounds,
                        current,
                        command.Point0,
                        command.Point1);
                    current = command.Point1;
                    bounds.Include(current);
                    break;
                case PackedPathCommandKind.Cubic:
                    SKPathTightBounds.IncludeCubic(
                        ref bounds,
                        current,
                        command.Point0,
                        command.Point1,
                        command.Point2);
                    current = command.Point2;
                    bounds.Include(current);
                    break;
            }
        }

        return bounds.ToRect();
    }

    public PathGeometry Materialize(SKPathFillType fillType)
    {
        ApplyPendingTranslation();
        var geometry = new PathGeometry
        {
            FillRule = fillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
                ? FillRule.EvenOdd
                : FillRule.Nonzero,
        };
        PathFigure? figure = null;
        for (var index = 0; index < _count; index++)
        {
            ref readonly var command = ref Commands[index];
            switch (command.Kind)
            {
                case PackedPathCommandKind.Move:
                    figure = new PathFigure(command.Point0);
                    geometry.Figures.Add(figure);
                    break;
                case PackedPathCommandKind.Line:
                    figure!.Segments.Add(new LineSegment(command.Point0));
                    break;
                case PackedPathCommandKind.Quadratic:
                    figure!.Segments.Add(new QuadraticBezierSegment(
                        command.Point0,
                        command.Point1));
                    break;
                case PackedPathCommandKind.Cubic:
                    figure!.Segments.Add(new CubicBezierSegment(
                        command.Point0,
                        command.Point1,
                        command.Point2));
                    break;
                case PackedPathCommandKind.Close:
                    figure!.IsClosed = true;
                    figure = null;
                    break;
            }
        }

        return geometry;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isRented, 0) == 0)
        {
            return;
        }

        Reset();
        if (_commandBuffer is { IsShared: true })
        {
            ReleaseCommandBuffer();
        }
        else if (_commandBuffer is { Commands.Length: > MaximumRetainedCommandCapacity })
        {
            ReleaseCommandBuffer();
        }

        if (s_threadCache is null)
        {
            s_threadCache = this;
            return;
        }

        ReleaseCommandBuffer();
    }

    private PackedPathCommand[] Commands =>
        _commandBuffer?.Commands ?? throw new InvalidOperationException("The packed path has no command storage.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureFigure()
    {
        if (!_hasOpenFigure)
        {
            MoveTo(_currentPoint.X, _currentPoint.Y);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureFigureBoundsOnly()
    {
        if (!_hasOpenFigure)
        {
            MoveToBoundsOnly(_currentPoint.X, _currentPoint.Y);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncludeBounds(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            return;
        }

        if (!_hasBounds)
        {
            _boundsMin = point;
            _boundsMax = point;
            _hasBounds = true;
            return;
        }

        _boundsMin = Vector2.Min(_boundsMin, point);
        _boundsMax = Vector2.Max(_boundsMax, point);
    }

    private void RecalculateBounds()
    {
        _hasBounds = false;
        _boundsMin = default;
        _boundsMax = default;
        _tightBounds = default;
        var current = Vector2.Zero;
        for (var index = 0; index < _count; index++)
        {
            ref readonly var command = ref Commands[index];
            switch (command.Kind)
            {
                case PackedPathCommandKind.Move:
                case PackedPathCommandKind.Line:
                    current = command.Point0;
                    IncludeBounds(command.Point0);
                    if (_trackTightBounds)
                    {
                        _tightBounds.Include(command.Point0);
                    }
                    break;
                case PackedPathCommandKind.Quadratic:
                    if (_trackTightBounds)
                    {
                        SKPathTightBounds.IncludeQuadratic(
                            ref _tightBounds,
                            current,
                            command.Point0,
                            command.Point1);
                    }
                    current = command.Point1;
                    IncludeBounds(command.Point0);
                    IncludeBounds(command.Point1);
                    if (_trackTightBounds)
                    {
                        _tightBounds.Include(command.Point1);
                    }
                    break;
                case PackedPathCommandKind.Cubic:
                    if (_trackTightBounds)
                    {
                        SKPathTightBounds.IncludeCubic(
                            ref _tightBounds,
                            current,
                            command.Point0,
                            command.Point1,
                            command.Point2);
                    }
                    current = command.Point2;
                    IncludeBounds(command.Point0);
                    IncludeBounds(command.Point1);
                    IncludeBounds(command.Point2);
                    if (_trackTightBounds)
                    {
                        _tightBounds.Include(command.Point2);
                    }
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Append(PackedPathCommand command)
    {
        ApplyPendingTranslation();
        EnsureWritableCommandStorage(checked(_count + 1));
        Commands[_count++] = command;
    }

    private void ApplyPendingTranslation()
    {
        if (_pendingTranslation == default || _count == 0)
        {
            _pendingTranslation = default;
            return;
        }

        var offset = _pendingTranslation;
        EnsureWritableCommandStorage(_count);
        for (var index = 0; index < _count; index++)
        {
            ref var command = ref Commands[index];
            command = TranslateCommand(command, offset);
        }
        _pendingTranslation = default;
    }

    private void EnsureWritableCommandStorage(int requiredCapacity)
    {
        if (_commandBuffer is null)
        {
            _commandBuffer = new CommandBuffer(ArrayPool<PackedPathCommand>.Shared.Rent(
                Math.Max(InitialCommandCapacity, requiredCapacity)));
            return;
        }

        if (!_commandBuffer.IsShared && _commandBuffer.Commands.Length >= requiredCapacity)
        {
            return;
        }

        var previous = _commandBuffer;
        var capacity = previous.Commands.Length >= requiredCapacity
            ? previous.Commands.Length
            : checked(previous.Commands.Length * 2);
        var replacement = ArrayPool<PackedPathCommand>.Shared.Rent(
            Math.Max(capacity, requiredCapacity));
        previous.Commands.AsSpan(0, _count).CopyTo(replacement);
        if (!previous.IsShared)
        {
            var previousCommands = previous.Commands;
            previous.Commands = replacement;
            ArrayPool<PackedPathCommand>.Shared.Return(previousCommands);
            return;
        }

        _commandBuffer = new CommandBuffer(replacement);
        if (previous.Release())
        {
            ArrayPool<PackedPathCommand>.Shared.Return(previous.Commands);
        }
    }

    private void ReleaseCommandBuffer()
    {
        if (_commandBuffer is not { } commandBuffer)
        {
            return;
        }

        _commandBuffer = null;
        if (commandBuffer.Release())
        {
            ArrayPool<PackedPathCommand>.Shared.Return(commandBuffer.Commands);
        }
    }
}
