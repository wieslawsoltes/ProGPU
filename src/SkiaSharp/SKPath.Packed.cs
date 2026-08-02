using System.Buffers;
using System.Numerics;
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

/// <summary>
/// Retains the common path-builder verbs in one pooled, contiguous stream. The
/// public object graph is expanded only when a caller requests <see cref="SKPath.Geometry"/>
/// or an operation requires the legacy mutable representation.
/// </summary>
internal sealed class PackedPathData : IDisposable
{
    private const int InitialCommandCapacity = 64;
    private const int MaximumRetainedCommandCapacity = 1_024;
    [ThreadStatic]
    private static PackedPathData? s_threadCache;
    private PackedPathCommand[]? _commands;
    private int _count;
    private Vector2 _currentPoint;
    private Vector2 _contourStart;
    private Vector2 _boundsMin;
    private Vector2 _boundsMax;
    private bool _hasOpenFigure;
    private bool _figureHasSegments;
    private bool _hasBounds;
    private int _isRented;

    private PackedPathData()
    {
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;
    public Vector2 CurrentPoint => _currentPoint;

    public static PackedPathData Rent()
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

        Volatile.Write(ref data._isRented, 1);
        return data;
    }

    public void MoveTo(float x, float y)
    {
        var point = new Vector2(x, y);
        if (_hasOpenFigure && !_figureHasSegments &&
            _count > 0 && Commands[_count - 1].Kind == PackedPathCommandKind.Move)
        {
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

    public void LineTo(float x, float y)
    {
        EnsureFigure();
        var point = new Vector2(x, y);
        Append(new PackedPathCommand(PackedPathCommandKind.Line, point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(point);
    }

    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        EnsureFigure();
        var point = new Vector2(x1, y1);
        Append(new PackedPathCommand(
            PackedPathCommandKind.Quadratic,
            new Vector2(x0, y0),
            point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(new Vector2(x0, y0));
        IncludeBounds(point);
    }

    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        EnsureFigure();
        var point = new Vector2(x2, y2);
        Append(new PackedPathCommand(
            PackedPathCommandKind.Cubic,
            new Vector2(x0, y0),
            new Vector2(x1, y1),
            point));
        _currentPoint = point;
        _figureHasSegments = true;
        IncludeBounds(new Vector2(x0, y0));
        IncludeBounds(new Vector2(x1, y1));
        IncludeBounds(point);
    }

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

    public void Reset()
    {
        _count = 0;
        _currentPoint = default;
        _contourStart = default;
        _boundsMin = default;
        _boundsMax = default;
        _hasOpenFigure = false;
        _figureHasSegments = false;
        _hasBounds = false;
    }

    public PackedPathData Clone()
    {
        var clone = Rent();
        clone._count = _count;
        clone._currentPoint = _currentPoint;
        clone._contourStart = _contourStart;
        clone._boundsMin = _boundsMin;
        clone._boundsMax = _boundsMax;
        clone._hasOpenFigure = _hasOpenFigure;
        clone._figureHasSegments = _figureHasSegments;
        clone._hasBounds = _hasBounds;
        if (_count != 0)
        {
            clone._commands = ArrayPool<PackedPathCommand>.Shared.Rent(
                Math.Max(InitialCommandCapacity, _count));
            Commands.AsSpan(0, _count).CopyTo(clone._commands);
        }

        return clone;
    }

    public SKRect CalculateBounds()
    {
        return _hasBounds
            ? new SKRect(_boundsMin.X, _boundsMin.Y, _boundsMax.X, _boundsMax.Y)
            : SKRect.Empty;
    }

    public PathGeometry Materialize(SKPathFillType fillType)
    {
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
        if (_commands is { Length: > MaximumRetainedCommandCapacity } oversized)
        {
            _commands = null;
            ArrayPool<PackedPathCommand>.Shared.Return(oversized);
        }

        if (s_threadCache is null)
        {
            s_threadCache = this;
            return;
        }

        if (_commands is { } commands)
        {
            _commands = null;
            ArrayPool<PackedPathCommand>.Shared.Return(commands);
        }
    }

    private PackedPathCommand[] Commands =>
        _commands ?? throw new InvalidOperationException("The packed path has no command storage.");

    private void EnsureFigure()
    {
        if (!_hasOpenFigure)
        {
            MoveTo(_currentPoint.X, _currentPoint.Y);
        }
    }

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
        for (var index = 0; index < _count; index++)
        {
            ref readonly var command = ref Commands[index];
            switch (command.Kind)
            {
                case PackedPathCommandKind.Move:
                case PackedPathCommandKind.Line:
                    IncludeBounds(command.Point0);
                    break;
                case PackedPathCommandKind.Quadratic:
                    IncludeBounds(command.Point0);
                    IncludeBounds(command.Point1);
                    break;
                case PackedPathCommandKind.Cubic:
                    IncludeBounds(command.Point0);
                    IncludeBounds(command.Point1);
                    IncludeBounds(command.Point2);
                    break;
            }
        }
    }

    private void Append(PackedPathCommand command)
    {
        if (_commands is null)
        {
            _commands = ArrayPool<PackedPathCommand>.Shared.Rent(InitialCommandCapacity);
        }
        else if (_count == _commands.Length)
        {
            var expanded = ArrayPool<PackedPathCommand>.Shared.Rent(
                checked(_commands.Length * 2));
            _commands.AsSpan(0, _count).CopyTo(expanded);
            ArrayPool<PackedPathCommand>.Shared.Return(_commands);
            _commands = expanded;
        }

        _commands[_count++] = command;
    }
}
