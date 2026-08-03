#pragma warning disable CS0618 // The shim internally composes its official legacy SKPath contract.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKPath : SKObject
{
    private const string UsePathBuilderMessage = "Use SKPathBuilder instead.";
    [ThreadStatic]
    private static PathGeometry? s_deferredGeometryCache;
    public override IntPtr Handle
    {
        get => base.Handle;
        protected set => base.Handle = value;
    }

    private PathFigure? _currentFigure;
    private Vector2 _currentPoint;
    private Vector2 _contourStart;
    private SKPathFillType _fillType = SKPathFillType.Winding;

    private PathGeometry? _geometry;
    private PackedPathData? _packedPathData;

    internal PackedPathData? PackedPathData => _packedPathData;

    public PathGeometry Geometry => EnsureWritableGeometry();
    internal PathGeometry RetainedGeometry => EnsureGeometry();
    public SKPathFillType FillType
    {
        get => _fillType;
        set
        {
            _fillType = value;
            if (_geometry is not null)
            {
                EnsureWritableGeometry().FillRule = value is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
                    ? FillRule.EvenOdd
                    : FillRule.Nonzero;
            }
        }
    }

    public SKPath()
        : base(SKObjectHandle.Create(), owns: true)
    {
        _packedPathData = PackedPathData.Rent();
    }

    internal SKPath(PackedPathData packedPathData, SKPathFillType fillType)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _geometry = null;
        _packedPathData = packedPathData ?? throw new ArgumentNullException(nameof(packedPathData));
        _currentPoint = packedPathData.CurrentPoint;
        _contourStart = packedPathData.ContourStart;
        _fillType = fillType;
    }

    private SKPath(PathGeometry geometry, SKPathFillType fillType)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _fillType = fillType;
    }

    public SKPath(SKPath path)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path._packedPathData is { } packed)
        {
            _packedPathData = packed.Clone();
            _currentPoint = packed.CurrentPoint;
            _contourStart = packed.ContourStart;
            _fillType = path._fillType;
            return;
        }

        PathFigure? copiedCurrentFigure = null;
        foreach (var figure in path.Geometry.Figures)
        {
            var copiedFigure = CloneFigure(figure, Vector2.Zero);
            Geometry.Figures.Add(copiedFigure);
            if (ReferenceEquals(figure, path._currentFigure))
            {
                copiedCurrentFigure = copiedFigure;
            }
        }

        _currentFigure = copiedCurrentFigure;
        _currentPoint = path._currentPoint;
        _contourStart = path._contourStart;
        FillType = path.FillType;
    }

    public static SKPath ParseSvgPathData(string svgPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(svgPath);
        var geometry = PathGeometry.Parse(svgPath);
        var path = new SKPath
        {
            FillType = geometry.FillRule == FillRule.EvenOdd
                ? SKPathFillType.EvenOdd
                : SKPathFillType.Winding
        };
        foreach (var figure in geometry.Figures)
        {
            path.Geometry.Figures.Add(figure);
        }

        path.RestoreCurrentState();

        return path;
    }

    public SKRect Bounds
    {
        get
        {
            if (_packedPathData is not null)
            {
                return _packedPathData.CalculateBounds();
            }

            var geometry = RetainedGeometry;
            if (geometry.IsCombined && !geometry.HasExactCombinedBounds)
            {
                geometry = EnsureSolvedGeometry();
            }

            return geometry.TryGetBounds(out var min, out var max)
                ? new SKRect(min.X, min.Y, max.X, max.Y)
                : SKRect.Empty;
        }
    }

    internal SKRect RetainedBounds
    {
        get
        {
            if (_packedPathData is { } packed)
            {
                return packed.CalculateBounds();
            }

            return RetainedGeometry.TryGetBounds(out var min, out var max)
                ? new SKRect(min.X, min.Y, max.X, max.Y)
                : SKRect.Empty;
        }
    }

    public SKRect TightBounds
    {
        get
        {
            if (_packedPathData is { } packed)
            {
                return packed.CalculateTightBounds();
            }

            if (RetainedGeometry.IsCombined)
            {
                return Bounds;
            }

            var bounds = new SKPathBoundsAccumulator();

            foreach (var figure in RetainedGeometry.Figures)
            {
                var current = figure.StartPoint;
                bounds.Include(current);

                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            bounds.Include(line.Point);
                            current = line.Point;
                            break;

                        case QuadraticBezierSegment quadratic:
                            SKPathTightBounds.IncludeQuadratic(
                                ref bounds,
                                current,
                                quadratic.ControlPoint,
                                quadratic.Point);
                            bounds.Include(quadratic.Point);
                            current = quadratic.Point;
                            break;

                        case CubicBezierSegment cubic:
                            SKPathTightBounds.IncludeCubic(
                                ref bounds,
                                current,
                                cubic.ControlPoint1,
                                cubic.ControlPoint2,
                                cubic.Point);
                            bounds.Include(cubic.Point);
                            current = cubic.Point;
                            break;

                        case ArcSegment arc:
                            if (ArcSegmentGeometry.TryGetArcBounds(current, arc, out var arcMin, out var arcMax))
                            {
                                bounds.Include(arcMin);
                                bounds.Include(arcMax);
                            }
                            else
                            {
                                bounds.Include(arc.Point);
                            }

                            current = arc.Point;
                            break;
                    }
                }
            }

            return bounds.ToRect();
        }
    }

    public bool IsEmpty
    {
        get
        {
            if (_packedPathData is { } packed)
            {
                return packed.IsEmpty;
            }

            var geometry = RetainedGeometry;
            if (!geometry.IsCombined)
            {
                return geometry.Figures.Count == 0;
            }

            return geometry.CombinedIsEmpty ?? EnsureSolvedGeometry().Figures.Count == 0;
        }
    }

    private PathGeometry EnsureGeometry()
    {
        if (_geometry is not null)
        {
            return _geometry;
        }

        if (_packedPathData is { } packed)
        {
            _geometry = packed.Materialize(_fillType);
            _packedPathData = null;
            packed.Dispose();
            RestoreCurrentState();
        }
        else
        {
            _geometry = new PathGeometry
            {
                FillRule = _fillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
                    ? FillRule.EvenOdd
                    : FillRule.Nonzero,
            };
        }

        return _geometry;
    }

    private PathGeometry EnsureSolvedGeometry()
    {
        var geometry = EnsureGeometry();
        if (!geometry.IsCombined)
        {
            return geometry;
        }

        PathGeometry solved;
        if (geometry.PathA is null || geometry.PathB is null)
        {
            solved = new PathGeometry { FillRule = geometry.FillRule };
        }
        else
        {
            solved = PathOpGeometrySolver.Combine(geometry.PathA, geometry.PathB, geometry.Op);
        }

        _geometry = solved;
        ReturnDeferredGeometry(geometry);
        RestoreCurrentState();
        return solved;
    }

    private PathGeometry EnsureWritableGeometry()
    {
        var geometry = EnsureSolvedGeometry();
        if (!geometry.IsSharedSnapshot)
        {
            return geometry;
        }

        var clone = new PathGeometry { FillRule = geometry.FillRule };
        foreach (var figure in geometry.Figures)
        {
            clone.Figures.Add(CloneFigure(figure, Vector2.Zero));
        }

        _geometry = clone;
        RestoreCurrentState();
        return clone;
    }

    internal void ReplaceWithOwned(SKPath source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source))
        {
            return;
        }

        _packedPathData?.Dispose();
        if (_geometry is { } previous &&
            !ReferenceEquals(previous, source._geometry))
        {
            ReturnDeferredGeometry(previous);
        }
        _packedPathData = source._packedPathData;
        _geometry = source._geometry;
        _currentFigure = source._currentFigure;
        _currentPoint = source._currentPoint;
        _contourStart = source._contourStart;
        _fillType = source._fillType;

        source._packedPathData = null;
        source._geometry = null;
        source._currentFigure = null;
        source.ResetCurrentState();
        source._fillType = SKPathFillType.Winding;
    }

    private void EnsureFigure()
    {
        if (_currentFigure is not null)
        {
            return;
        }

        var geometry = Geometry;
        if (_currentFigure is not null)
        {
            return;
        }

        _currentFigure = new PathFigure(_currentPoint);
        geometry.Figures.Add(_currentFigure);
        _contourStart = _currentPoint;
    }

    [Obsolete(UsePathBuilderMessage)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveTo(float x, float y)
    {
        if (_packedPathData is { } packed)
        {
            packed.MoveTo(x, y);
            _currentPoint = new Vector2(x, y);
            _contourStart = _currentPoint;
            return;
        }

        var point = new Vector2(x, y);
        if (_currentFigure is { IsClosed: false, Segments.Count: 0 })
        {
            _currentFigure.StartPoint = point;
            _currentPoint = point;
            _contourStart = point;
            return;
        }

        _currentFigure = new PathFigure(point);
        Geometry.Figures.Add(_currentFigure);
        _currentPoint = point;
        _contourStart = point;
    }

    [Obsolete(UsePathBuilderMessage)]
    public void MoveTo(SKPoint point) => MoveTo(point.X, point.Y);

    [Obsolete(UsePathBuilderMessage)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void LineTo(float x, float y)
    {
        if (_packedPathData is { } packed)
        {
            packed.LineTo(x, y);
            _currentPoint = new Vector2(x, y);
            return;
        }

        EnsureFigure();
        var point = new Vector2(x, y);
        _currentFigure!.Segments.Add(new LineSegment(point));
        _currentPoint = point;
    }

    [Obsolete(UsePathBuilderMessage)]
    public void LineTo(SKPoint point) => LineTo(point.X, point.Y);

    [Obsolete(UsePathBuilderMessage)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        if (_packedPathData is { } packed)
        {
            packed.QuadTo(x0, y0, x1, y1);
            _currentPoint = new Vector2(x1, y1);
            return;
        }

        EnsureFigure();
        var point = new Vector2(x1, y1);
        _currentFigure!.Segments.Add(new QuadraticBezierSegment(new Vector2(x0, y0), point));
        _currentPoint = point;
    }

    [Obsolete(UsePathBuilderMessage)]
    public void QuadTo(SKPoint point0, SKPoint point1) =>
        QuadTo(point0.X, point0.Y, point1.X, point1.Y);

    [Obsolete(UsePathBuilderMessage)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        if (_packedPathData is { } packed)
        {
            packed.CubicTo(x0, y0, x1, y1, x2, y2);
            _currentPoint = new Vector2(x2, y2);
            return;
        }

        EnsureFigure();
        var point = new Vector2(x2, y2);
        _currentFigure!.Segments.Add(new CubicBezierSegment(new Vector2(x0, y0), new Vector2(x1, y1), point));
        _currentPoint = point;
    }

    [Obsolete(UsePathBuilderMessage)]
    public void CubicTo(SKPoint point0, SKPoint point1, SKPoint point2) =>
        CubicTo(point0.X, point0.Y, point1.X, point1.Y, point2.X, point2.Y);

    [Obsolete(UsePathBuilderMessage)]
    public void ArcTo(float rx, float ry, float xAxisRotate, SKPathArcSize largeArc, SKPathDirection sweep, float x, float y)
    {
        EnsureFigure();
        var point = new Vector2(x, y);
        if (!float.IsFinite(rx) || !float.IsFinite(ry) || MathF.Abs(rx) <= PathEpsilon || MathF.Abs(ry) <= PathEpsilon)
        {
            LineTo(x, y);
            return;
        }

        var sd = sweep == SKPathDirection.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        _currentFigure!.Segments.Add(new ArcSegment(point, new Vector2(MathF.Abs(rx), MathF.Abs(ry)), xAxisRotate, largeArc == SKPathArcSize.Large, sd));
        _currentPoint = point;
    }

    [Obsolete(UsePathBuilderMessage)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Close()
    {
        if (_packedPathData is { } packed)
        {
            packed.Close();
            _currentPoint = packed.CurrentPoint;
            return;
        }

        if (_currentFigure != null)
        {
            _currentFigure.IsClosed = true;
            _currentPoint = _contourStart;
            _currentFigure = null;
        }
    }

    internal void AddTriangles(ReadOnlySpan<StrokeJoinTriangle> triangles)
    {
        if (_packedPathData is { } packed)
        {
            packed.AddTriangles(triangles);
            _currentPoint = packed.CurrentPoint;
            _contourStart = packed.ContourStart;
            return;
        }

        for (var index = 0; index < triangles.Length; index++)
        {
            var triangle = triangles[index];
            MoveTo(triangle.P0.X, triangle.P0.Y);
            LineTo(triangle.P1.X, triangle.P1.Y);
            LineTo(triangle.P2.X, triangle.P2.Y);
            Close();
        }
    }

    public void Reset()
    {
        if (_packedPathData is { } packed)
        {
            packed.Reset();
            ResetCurrentState();
            FillType = SKPathFillType.Winding;
            return;
        }

        Geometry.Figures.Clear();
        ResetCurrentState();
        FillType = SKPathFillType.Winding;
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddCircle(float x, float y, float radius, SKPathDirection dir = SKPathDirection.Clockwise)
    {
        AddOval(new SKRect(x - radius, y - radius, x + radius, y + radius), dir);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddOval(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        AppendOvalArc(rect, 0f, direction == SKPathDirection.Clockwise ? 360f : -360f, forceMoveTo: true);
        Close();
    }

    [Obsolete(UsePathBuilderMessage)]
    public void ConicTo(SKPoint point0, SKPoint point1, float w)
    {
        ConicTo(point0.X, point0.Y, point1.X, point1.Y, w);
    }

    public bool Contains(float x, float y)
    {
        if (!PathGeometryHitTesting.TryContainsFill(
                Geometry,
                new Vector2(x, y),
                0f,
                relativeTolerance: false,
                out var contains))
        {
            contains = Bounds is var bounds
                && x >= bounds.Left
                && x <= bounds.Right
                && y >= bounds.Top
                && y <= bounds.Bottom;
        }

        return FillType is SKPathFillType.InverseEvenOdd or SKPathFillType.InverseWinding
            ? !contains
            : contains;
    }

    public Iterator CreateIterator(bool forceClose) => new(this, forceClose);

    [Obsolete(UsePathBuilderMessage)]
    public void AddRect(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
        => AddRect(rect, direction, 0);

    [Obsolete(UsePathBuilderMessage)]
    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
        => AddRoundRect(
            rect,
            direction,
            direction == SKPathDirection.Clockwise ? 6u : 7u);

    [Obsolete(UsePathBuilderMessage)]
    public void AddRoundRect(SKRect rect, float rx, float ry, SKPathDirection dir = SKPathDirection.Clockwise)
    {
        AddRoundRect(new SKRoundRect(rect, rx, ry), dir);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddPath(
        SKPath other,
        float dx,
        float dy,
        SKPathAddMode mode = SKPathAddMode.Append)
    {
        ArgumentNullException.ThrowIfNull(other);
        AddPathCore(other, new Vector2(dx, dy), mode);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddPath(SKPath other, SKPathAddMode mode = SKPathAddMode.Append) =>
        AddPath(other, 0f, 0f, mode);

    [Obsolete(UsePathBuilderMessage)]
    public void AddPath(SKPath other, in SKMatrix matrix, SKPathAddMode mode = SKPathAddMode.Append)
    {
        using var copy = new SKPath(other);
        copy.Transform(matrix);
        AddPath(copy, mode);
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddPoly(ReadOnlySpan<SKPoint> points, bool close = true)
    {
        if (points.IsEmpty)
        {
            return;
        }

        MoveTo(points[0]);
        for (var i = 1; i < points.Length; i++)
        {
            LineTo(points[i]);
        }

        if (close)
        {
            Close();
        }
    }

    [Obsolete(UsePathBuilderMessage)]
    public void AddPoly(SKPoint[] points, bool close = true)
    {
        ArgumentNullException.ThrowIfNull(points);
        AddPoly(points.AsSpan(), close);
    }

    public RawIterator CreateRawIterator() => new(this);

    private static PathFigure CloneFigure(PathFigure figure, Vector2 offset)
    {
        var copy = new PathFigure(figure.StartPoint + offset, figure.IsClosed)
        {
            IsFilled = figure.IsFilled
        };
        foreach (var segment in figure.Segments)
        {
            copy.Segments.Add(CloneSegment(segment, offset));
        }

        return copy;
    }

    public void Transform(SKMatrix matrix)
    {
        if (matrix.IsIdentity)
        {
            return;
        }

        if (_packedPathData is { } packed)
        {
            packed.Transform(matrix);
            if (matrix.ScaleX == 1f && matrix.ScaleY == 1f &&
                matrix.SkewX == 0f && matrix.SkewY == 0f &&
                matrix.Persp0 == 0f && matrix.Persp1 == 0f && matrix.Persp2 == 1f &&
                float.IsFinite(matrix.TransX) && float.IsFinite(matrix.TransY))
            {
                var offset = new Vector2(matrix.TransX, matrix.TransY);
                _currentPoint += offset;
                _contourStart += offset;
            }
            else
            {
                var current = matrix.MapPoint(_currentPoint.X, _currentPoint.Y);
                var contourStart = matrix.MapPoint(_contourStart.X, _contourStart.Y);
                _currentPoint = new Vector2(current.X, current.Y);
                _contourStart = new Vector2(contourStart.X, contourStart.Y);
            }
            return;
        }

        var m = matrix.ToMatrix4x4();
        foreach (var fig in Geometry.Figures)
        {
            var sourceCurrentPoint = fig.StartPoint;
            fig.StartPoint = Vector2.Transform(fig.StartPoint, m);
            for (int i = 0; i < fig.Segments.Count; i++)
            {
                var seg = fig.Segments[i];
                if (seg is LineSegment line)
                {
                    sourceCurrentPoint = line.Point;
                    line.Point = Vector2.Transform(line.Point, m);
                }
                else if (seg is RationalConicQuadraticSegment conic)
                {
                    sourceCurrentPoint = conic.Point;
                    conic.ControlPoint = Vector2.Transform(conic.ControlPoint, m);
                    conic.Point = Vector2.Transform(conic.Point, m);
                    conic.OriginalStart = Vector2.Transform(conic.OriginalStart, m);
                    conic.OriginalControl = Vector2.Transform(conic.OriginalControl, m);
                    conic.OriginalEnd = Vector2.Transform(conic.OriginalEnd, m);
                }
                else if (seg is QuadraticBezierSegment quad)
                {
                    sourceCurrentPoint = quad.Point;
                    quad.ControlPoint = Vector2.Transform(quad.ControlPoint, m);
                    quad.Point = Vector2.Transform(quad.Point, m);
                }
                else if (seg is CubicBezierSegment cubic)
                {
                    sourceCurrentPoint = cubic.Point;
                    cubic.ControlPoint1 = Vector2.Transform(cubic.ControlPoint1, m);
                    cubic.ControlPoint2 = Vector2.Transform(cubic.ControlPoint2, m);
                    cubic.Point = Vector2.Transform(cubic.Point, m);
                }
                else if (seg is ArcSegment arc)
                {
                    var sourceEndPoint = arc.Point;
                    if (ArcSegmentGeometry.TryTransformArcSegment(
                            sourceCurrentPoint,
                            arc,
                            m,
                            out _,
                            out var transformedArc))
                    {
                        fig.Segments[i] = transformedArc;
                    }
                    else
                    {
                        fig.Segments[i] = new LineSegment(
                            Vector2.Transform(arc.Point, m),
                            arc.IsSmoothJoin,
                            arc.IsStroked);
                    }

                    sourceCurrentPoint = sourceEndPoint;
                }
            }
        }
        RestoreCurrentState();
    }

    private static PathSegment CloneSegment(PathSegment segment, Vector2 offset)
    {
        return segment switch
        {
            RationalConicQuadraticSegment conic => new RationalConicQuadraticSegment(
                conic.ControlPoint + offset,
                conic.Point + offset,
                conic.OriginalStart + offset,
                conic.OriginalControl + offset,
                conic.OriginalEnd + offset,
                conic.Weight,
                conic.SpanCount,
                conic.IsSmoothJoin,
                conic.IsStroked),
            LineSegment line => new LineSegment(
                line.Point + offset,
                line.IsSmoothJoin,
                line.IsStroked),
            QuadraticBezierSegment quad => new QuadraticBezierSegment(
                quad.ControlPoint + offset,
                quad.Point + offset,
                quad.IsSmoothJoin,
                quad.IsStroked),
            CubicBezierSegment cubic => new CubicBezierSegment(
                cubic.ControlPoint1 + offset,
                cubic.ControlPoint2 + offset,
                cubic.Point + offset,
                cubic.IsSmoothJoin,
                cubic.IsStroked),
            ArcSegment arc => new ArcSegment(
                arc.Point + offset,
                arc.Size,
                arc.RotationAngle,
                arc.IsLargeArc,
                arc.SweepDirection,
                arc.IsSmoothJoin,
                arc.IsStroked),
            _ => throw new NotSupportedException($"Unsupported SKPath segment type '{segment.GetType().FullName}'.")
        };
    }

    public SKPath Op(SKPath other, SKPathOp op)
    {
        ArgumentNullException.ThrowIfNull(other);
        var geometry = PathOpGeometrySolver.CreateDeferred(
            RetainedGeometry,
            other.RetainedGeometry,
            (int)op,
            RentDeferredGeometry());
        return new SKPath(geometry, ToSkPathFillType(geometry.FillRule));
    }

    public bool Op(SKPath other, SKPathOp op, SKPath result)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (result is null)
        {
            return false;
        }

        var geometry = PathOpGeometrySolver.CreateDeferred(
            RetainedGeometry,
            other.RetainedGeometry,
            (int)op,
            RentDeferredGeometry());
        result.ReplaceWithDeferredGeometry(geometry);
        return true;
    }

    private void ReplaceWithDeferredGeometry(PathGeometry geometry)
    {
        _packedPathData?.Dispose();
        _packedPathData = null;
        if (_geometry is { } previous)
        {
            ReturnDeferredGeometry(previous);
        }
        _geometry = geometry;
        _fillType = ToSkPathFillType(geometry.FillRule);
        ResetCurrentState();
    }

    private static void ApplySolvedGeometry(SKPath result, PathGeometry solvedGeometry)
    {
        result._packedPathData?.Dispose();
        result._packedPathData = null;
        if (result._geometry is { } previous)
        {
            ReturnDeferredGeometry(previous);
        }
        result._geometry = solvedGeometry;
        result._fillType = ToSkPathFillType(solvedGeometry.FillRule);
        result.RestoreCurrentState();
    }

    private static SKPathFillType ToSkPathFillType(FillRule fillRule)
    {
        return fillRule == FillRule.EvenOdd
            ? SKPathFillType.EvenOdd
            : SKPathFillType.Winding;
    }

    private static PathGeometry RentDeferredGeometry()
    {
        var geometry = s_deferredGeometryCache;
        if (geometry is null)
        {
            return new PathGeometry();
        }

        s_deferredGeometryCache = null;
        return geometry;
    }

    private static void ReturnDeferredGeometry(PathGeometry geometry)
    {
        if (s_deferredGeometryCache is null && geometry.TryResetDeferredForReuse())
        {
            s_deferredGeometryCache = geometry;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _packedPathData?.Dispose();
            _packedPathData = null;
            if (_geometry is { } geometry)
            {
                _geometry = null;
                ReturnDeferredGeometry(geometry);
            }
        }

        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
        base.DisposeNative();
    }

}

public enum SKRoundRectCorner
{
    UpperLeft = 0,
    UpperRight = 1,
    LowerRight = 2,
    LowerLeft = 3,
}

public enum SKRoundRectType
{
    Empty = 0,
    Rect = 1,
    Oval = 2,
    Simple = 3,
    NinePatch = 4,
    Complex = 5,
}

public class SKRoundRect : SKObject
{
    private const int CornerCount = 4;
    private const float NearlyZero = 1f / (1 << 12);
    private CornerRadiusBuffer _radii;
    private SKRect _rect;
    private byte _typeValue;

    private SKRoundRectType _type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (SKRoundRectType)_typeValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _typeValue = (byte)value;
    }

    public SKRect Rect => _rect;

    public SKPoint[] Radii
    {
        get
        {
            var radii = new SKPoint[CornerCount];
            _radii.CopyTo(radii);
            return radii;
        }
    }

    public SKRoundRectType Type => _type;

    public float Width => _rect.Width;

    public float Height => _rect.Height;

    public bool IsValid => ValidateState();

    public bool AllCornersCircular => CheckAllCornersCircular(NearlyZero);

    internal void CopyCornerRadii(Span<SKPoint> destination) => _radii.CopyTo(destination);

    public SKRoundRect()
        : base(SKObjectHandle.Create(), owns: true)
    {
        SetEmpty();
    }

    public SKRoundRect(SKRect rect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        SetRect(rect);
    }

    public SKRoundRect(SKRect rect, float radius)
        : this(rect, radius, radius)
    {
    }

    public SKRoundRect(SKRect rect, float xRadius, float yRadius)
        : base(SKObjectHandle.Create(), owns: true)
    {
        SetRect(rect, xRadius, yRadius);
    }

    public SKRoundRect(SKRoundRect rrect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _rect = rrect._rect;
        _type = rrect._type;
        _radii = rrect._radii.Clone();
    }

    public bool CheckAllCornersCircular(float tolerance)
    {
        for (var index = 0; index < CornerCount; index++)
        {
            if (!(MathF.Abs(_radii[index].X - _radii[index].Y) <= tolerance))
            {
                return false;
            }
        }

        return true;
    }

    public void SetEmpty()
    {
        _rect = SKRect.Empty;
        ClearRadii();
        _type = SKRoundRectType.Empty;
    }

    public void SetRect(SKRect rect)
    {
        if (!InitializeRect(rect))
        {
            return;
        }

        ClearRadii();
        _type = SKRoundRectType.Rect;
    }

    public void SetRect(SKRect rect, float xRadius, float yRadius)
    {
        if (!InitializeRect(rect))
        {
            return;
        }

        if (!float.IsFinite(xRadius) || !float.IsFinite(yRadius))
        {
            xRadius = 0f;
            yRadius = 0f;
        }

        if (_rect.Width < xRadius + xRadius || _rect.Height < yRadius + yRadius)
        {
            var scale = MathF.Min(_rect.Width / (xRadius + xRadius), _rect.Height / (yRadius + yRadius));
            xRadius *= scale;
            yRadius *= scale;
        }

        if (xRadius <= 0f || yRadius <= 0f)
        {
            SetRect(rect);
            return;
        }

        var type = SKRoundRectType.Simple;
        if (xRadius >= _rect.Width * 0.5f && yRadius >= _rect.Height * 0.5f)
        {
            type = SKRoundRectType.Oval;
            xRadius = _rect.Width * 0.5f;
            yRadius = _rect.Height * 0.5f;
        }

        FillRadii(new SKPoint(xRadius, yRadius));
        _type = type;
    }

    public void SetOval(SKRect rect)
    {
        if (!InitializeRect(rect))
        {
            return;
        }

        var xRadius = _rect.Width * 0.5f;
        var yRadius = _rect.Height * 0.5f;
        if (xRadius == 0f || yRadius == 0f)
        {
            ClearRadii();
            _type = SKRoundRectType.Rect;
            return;
        }

        FillRadii(new SKPoint(xRadius, yRadius));
        _type = SKRoundRectType.Oval;
    }

    public void SetNinePatch(
        SKRect rect,
        float leftRadius,
        float topRadius,
        float rightRadius,
        float bottomRadius)
    {
        if (!InitializeRect(rect))
        {
            return;
        }

        if (!float.IsFinite(leftRadius) || !float.IsFinite(topRadius) ||
            !float.IsFinite(rightRadius) || !float.IsFinite(bottomRadius))
        {
            SetRect(rect);
            return;
        }

        leftRadius = MathF.Max(leftRadius, 0f);
        topRadius = MathF.Max(topRadius, 0f);
        rightRadius = MathF.Max(rightRadius, 0f);
        bottomRadius = MathF.Max(bottomRadius, 0f);

        var scale = 1f;
        if (leftRadius + rightRadius > _rect.Width)
        {
            scale = _rect.Width / (leftRadius + rightRadius);
        }

        if (topRadius + bottomRadius > _rect.Height)
        {
            scale = MathF.Min(scale, _rect.Height / (topRadius + bottomRadius));
        }

        if (scale < 1f)
        {
            leftRadius *= scale;
            topRadius *= scale;
            rightRadius *= scale;
            bottomRadius *= scale;
        }

        if (leftRadius == rightRadius && topRadius == bottomRadius)
        {
            if (leftRadius >= _rect.Width * 0.5f && topRadius >= _rect.Height * 0.5f)
            {
                _type = SKRoundRectType.Oval;
                leftRadius = rightRadius = _rect.Width * 0.5f;
                topRadius = bottomRadius = _rect.Height * 0.5f;
            }
            else if (leftRadius == 0f || topRadius == 0f)
            {
                _type = SKRoundRectType.Rect;
                leftRadius = topRadius = rightRadius = bottomRadius = 0f;
            }
            else
            {
                _type = SKRoundRectType.Simple;
            }
        }
        else
        {
            _type = SKRoundRectType.NinePatch;
        }

        _radii.SetNinePatch(leftRadius, topRadius, rightRadius, bottomRadius);
        if (ClampToZero())
        {
            SetRect(rect);
        }
        else if (_type == SKRoundRectType.NinePatch && !RadiiAreNinePatch())
        {
            _type = SKRoundRectType.Complex;
        }
    }

    public void SetRectRadii(SKRect rect, SKPoint[] radii)
    {
        ArgumentNullException.ThrowIfNull(radii);
        SetRectRadii(rect, radii.AsSpan());
    }

    public void SetRectRadii(SKRect rect, ReadOnlySpan<SKPoint> radii)
    {
        if (radii.Length != 4)
        {
            throw new ArgumentException("Radii must have a length of 4.", nameof(radii));
        }

        if (!InitializeRect(rect))
        {
            return;
        }

        for (var index = 0; index < radii.Length; index++)
        {
            if (!float.IsFinite(radii[index].X) || !float.IsFinite(radii[index].Y))
            {
                SetRect(rect);
                return;
            }
        }

        _radii.Set(radii);

        if (ClampToZero())
        {
            SetRect(rect);
            return;
        }

        ScaleRadii();
    }

    public bool Contains(SKRect rect)
    {
        if (IsEmptyRect(rect) || IsEmptyRect(_rect) ||
            _rect.Left > rect.Left || _rect.Top > rect.Top ||
            _rect.Right < rect.Right || _rect.Bottom < rect.Bottom)
        {
            return false;
        }

        if (_type == SKRoundRectType.Rect)
        {
            return true;
        }

        return CheckCornerContainment(rect.Left, rect.Top) &&
               CheckCornerContainment(rect.Right, rect.Top) &&
               CheckCornerContainment(rect.Right, rect.Bottom) &&
               CheckCornerContainment(rect.Left, rect.Bottom);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SKPoint GetRadii(SKRoundRectCorner corner) => _radii[(int)corner];

    public void Deflate(SKSize size) => Deflate(size.Width, size.Height);

    public void Deflate(float dx, float dy) => Inset(dx, dy);

    public void Inflate(SKSize size) => Inflate(size.Width, size.Height);

    public void Inflate(float dx, float dy) => Inset(-dx, -dy);

    public void Offset(SKPoint pos) => Offset(pos.X, pos.Y);

    public void Offset(float dx, float dy)
    {
        _rect.Offset(dx, dy);
    }

    public bool TryTransform(SKMatrix matrix, out SKRoundRect? transformed)
    {
        if (matrix.IsIdentity)
        {
            transformed = new SKRoundRect(this);
            return true;
        }

        var diagonal = matrix.SkewX == 0f && matrix.SkewY == 0f;
        var antiDiagonal = matrix.ScaleX == 0f && matrix.ScaleY == 0f;
        if ((!diagonal && !antiDiagonal) ||
            matrix.Persp0 != 0f || matrix.Persp1 != 0f || matrix.Persp2 != 1f)
        {
            transformed = null;
            return false;
        }

        var newRect = MapBounds(_rect, matrix);
        if (!IsFinite(newRect))
        {
            transformed = null;
            return false;
        }

        if (_type == SKRoundRectType.Empty)
        {
            transformed = new SKRoundRect();
            return true;
        }

        if (_type == SKRoundRectType.Rect)
        {
            transformed = new SKRoundRect(newRect);
            return true;
        }

        if (_type == SKRoundRectType.Oval)
        {
            transformed = new SKRoundRect();
            transformed.SetOval(newRect);
            return true;
        }

        Span<SKPoint> mappedRadii = stackalloc SKPoint[4];
        for (var corner = 0; corner < 4; corner++)
        {
            GetCornerContour(corner, out var previous, out var control, out var next);
            previous = MapPoint(previous, matrix);
            control = MapPoint(control, matrix);
            next = MapPoint(next, matrix);

            var first = control - previous;
            var second = next - control;
            SKPoint radius;
            if (first.X != 0f)
            {
                radius = new SKPoint(MathF.Abs(first.X), MathF.Abs(second.Y));
            }
            else if (first.Y == 0f)
            {
                radius = new SKPoint(MathF.Abs(second.X), MathF.Abs(second.Y));
            }
            else
            {
                radius = new SKPoint(MathF.Abs(second.X), MathF.Abs(first.Y));
            }

            var targetCorner = control.X == newRect.Left
                ? control.Y == newRect.Top ? 0 : 3
                : control.Y == newRect.Top ? 1 : 2;
            mappedRadii[targetCorner] = radius;
        }

        transformed = new SKRoundRect();
        transformed.SetRectRadii(newRect, mappedRadii);
        return true;
    }

    public SKRoundRect? Transform(SKMatrix matrix) =>
        TryTransform(matrix, out var transformed) ? transformed : null;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
    }

    private bool InitializeRect(SKRect rect)
    {
        if (!IsFinite(rect))
        {
            SetEmpty();
            return false;
        }

        _rect = rect.Standardized;
        if (IsEmptyRect(_rect))
        {
            ClearRadii();
            _type = SKRoundRectType.Empty;
            return false;
        }

        return true;
    }

    private void Inset(float dx, float dy)
    {
        var rect = new SKRect(
            _rect.Left + dx,
            _rect.Top + dy,
            _rect.Right - dx,
            _rect.Bottom - dy);
        var degenerate = false;
        if (rect.Right <= rect.Left)
        {
            degenerate = true;
            rect.Left = rect.Right = Midpoint(rect.Left, rect.Right);
        }

        if (rect.Bottom <= rect.Top)
        {
            degenerate = true;
            rect.Top = rect.Bottom = Midpoint(rect.Top, rect.Bottom);
        }

        if (degenerate)
        {
            _rect = rect;
            ClearRadii();
            _type = SKRoundRectType.Empty;
            return;
        }

        if (!IsFinite(rect))
        {
            SetEmpty();
            return;
        }

        Span<SKPoint> radii = stackalloc SKPoint[4];
        for (var index = 0; index < radii.Length; index++)
        {
            radii[index] = new SKPoint(
                _radii[index].X == 0f ? 0f : _radii[index].X - dx,
                _radii[index].Y == 0f ? 0f : _radii[index].Y - dy);
        }

        SetRectRadii(rect, radii);
    }

    private void ScaleRadii()
    {
        var width = (double)_rect.Right - _rect.Left;
        var height = (double)_rect.Bottom - _rect.Top;
        if (TryClassifyFittingRadii(width, height))
        {
            return;
        }

        var scale = 1d;
        scale = ComputeMinimumScale(_radii[0].X, _radii[1].X, width, scale);
        scale = ComputeMinimumScale(_radii[1].Y, _radii[2].Y, height, scale);
        scale = ComputeMinimumScale(_radii[2].X, _radii[3].X, width, scale);
        scale = ComputeMinimumScale(_radii[3].Y, _radii[0].Y, height, scale);

        FlushToZero(0, 1, xAxis: true);
        FlushToZero(1, 2, xAxis: false);
        FlushToZero(2, 3, xAxis: true);
        FlushToZero(3, 0, xAxis: false);

        if (scale < 1d)
        {
            AdjustRadii(0, 1, xAxis: true, width, scale);
            AdjustRadii(1, 2, xAxis: false, height, scale);
            AdjustRadii(2, 3, xAxis: true, width, scale);
            AdjustRadii(3, 0, xAxis: false, height, scale);
        }

        ClampToZero();
        ComputeType();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryClassifyFittingRadii(double width, double height)
    {
        var upperLeft = _radii[0];
        var upperRight = _radii[1];
        var lowerRight = _radii[2];
        var lowerLeft = _radii[3];
        if (upperLeft.X <= 0f || upperLeft.Y <= 0f ||
            upperRight.X <= 0f || upperRight.Y <= 0f ||
            lowerRight.X <= 0f || lowerRight.Y <= 0f ||
            lowerLeft.X <= 0f || lowerLeft.Y <= 0f)
        {
            return false;
        }

        var top = upperLeft.X + upperRight.X;
        var right = upperRight.Y + lowerRight.Y;
        var bottom = lowerRight.X + lowerLeft.X;
        var left = lowerLeft.Y + upperLeft.Y;
        if (!(top != upperLeft.X && top != upperRight.X &&
            right != upperRight.Y && right != lowerRight.Y &&
            bottom != lowerRight.X && bottom != lowerLeft.X &&
            left != lowerLeft.Y && left != upperLeft.Y &&
            (double)upperLeft.X + upperRight.X <= width &&
            (double)upperRight.Y + lowerRight.Y <= height &&
            (double)lowerRight.X + lowerLeft.X <= width &&
            (double)lowerLeft.Y + upperLeft.Y <= height))
        {
            return false;
        }

        if (upperLeft == upperRight &&
            upperRight == lowerRight &&
            lowerRight == lowerLeft)
        {
            if (upperLeft.X >= _rect.Width * 0.5f &&
                upperLeft.Y >= _rect.Height * 0.5f)
            {
                FillRadii(new SKPoint(_rect.Width * 0.5f, _rect.Height * 0.5f));
                _type = SKRoundRectType.Oval;
            }
            else
            {
                _type = SKRoundRectType.Simple;
            }
        }
        else
        {
            _type = upperLeft.X == lowerLeft.X &&
                upperLeft.Y == upperRight.Y &&
                upperRight.X == lowerRight.X &&
                lowerLeft.Y == lowerRight.Y
                    ? SKRoundRectType.NinePatch
                    : SKRoundRectType.Complex;
        }

        return true;
    }

    private void ComputeType()
    {
        if (IsEmptyRect(_rect))
        {
            ClearRadii();
            _type = SKRoundRectType.Empty;
            return;
        }

        var allRadiiEqual = true;
        var allCornersSquare = _radii[0].X == 0f || _radii[0].Y == 0f;
        for (var index = 1; index < CornerCount; index++)
        {
            if (_radii[index].X != 0f && _radii[index].Y != 0f)
            {
                allCornersSquare = false;
            }

            if (_radii[index] != _radii[index - 1])
            {
                allRadiiEqual = false;
            }
        }

        if (allCornersSquare)
        {
            _type = SKRoundRectType.Rect;
        }
        else if (allRadiiEqual)
        {
            if (_radii[0].X >= _rect.Width * 0.5f && _radii[0].Y >= _rect.Height * 0.5f)
            {
                FillRadii(new SKPoint(_rect.Width * 0.5f, _rect.Height * 0.5f));
                _type = SKRoundRectType.Oval;
            }
            else
            {
                _type = SKRoundRectType.Simple;
            }
        }
        else
        {
            _type = RadiiAreNinePatch() ? SKRoundRectType.NinePatch : SKRoundRectType.Complex;
        }
    }

    private bool ClampToZero()
    {
        var allCornersSquare = true;
        for (var index = 0; index < CornerCount; index++)
        {
            if (_radii[index].X <= 0f || _radii[index].Y <= 0f)
            {
                _radii[index] = default;
            }
            else
            {
                allCornersSquare = false;
            }
        }

        return allCornersSquare;
    }

    private bool RadiiAreNinePatch() =>
        _radii[0].X == _radii[3].X &&
        _radii[0].Y == _radii[1].Y &&
        _radii[1].X == _radii[2].X &&
        _radii[3].Y == _radii[2].Y;

    private bool CheckCornerContainment(float x, float y)
    {
        SKPoint canonicalPoint;
        var index = 0;
        if (_type == SKRoundRectType.Oval)
        {
            canonicalPoint = new SKPoint(x - _rect.MidX, y - _rect.MidY);
        }
        else if (x < _rect.Left + _radii[0].X && y < _rect.Top + _radii[0].Y)
        {
            canonicalPoint = new SKPoint(x - (_rect.Left + _radii[0].X), y - (_rect.Top + _radii[0].Y));
        }
        else if (x < _rect.Left + _radii[3].X && y > _rect.Bottom - _radii[3].Y)
        {
            index = 3;
            canonicalPoint = new SKPoint(x - (_rect.Left + _radii[3].X), y - (_rect.Bottom - _radii[3].Y));
        }
        else if (x > _rect.Right - _radii[1].X && y < _rect.Top + _radii[1].Y)
        {
            index = 1;
            canonicalPoint = new SKPoint(x - (_rect.Right - _radii[1].X), y - (_rect.Top + _radii[1].Y));
        }
        else if (x > _rect.Right - _radii[2].X && y > _rect.Bottom - _radii[2].Y)
        {
            index = 2;
            canonicalPoint = new SKPoint(x - (_rect.Right - _radii[2].X), y - (_rect.Bottom - _radii[2].Y));
        }
        else
        {
            return true;
        }

        var radius = _radii[index];
        var distance = canonicalPoint.X * canonicalPoint.X * radius.Y * radius.Y +
                       canonicalPoint.Y * canonicalPoint.Y * radius.X * radius.X;
        var product = radius.X * radius.Y;
        return distance <= product * product;
    }

    private void GetCornerContour(int corner, out SKPoint previous, out SKPoint control, out SKPoint next)
    {
        var radius = _radii[corner];
        switch (corner)
        {
            case 0:
                previous = new SKPoint(_rect.Left, _rect.Top + radius.Y);
                control = new SKPoint(_rect.Left, _rect.Top);
                next = new SKPoint(_rect.Left + radius.X, _rect.Top);
                break;
            case 1:
                previous = new SKPoint(_rect.Right - radius.X, _rect.Top);
                control = new SKPoint(_rect.Right, _rect.Top);
                next = new SKPoint(_rect.Right, _rect.Top + radius.Y);
                break;
            case 2:
                previous = new SKPoint(_rect.Right, _rect.Bottom - radius.Y);
                control = new SKPoint(_rect.Right, _rect.Bottom);
                next = new SKPoint(_rect.Right - radius.X, _rect.Bottom);
                break;
            default:
                previous = new SKPoint(_rect.Left + radius.X, _rect.Bottom);
                control = new SKPoint(_rect.Left, _rect.Bottom);
                next = new SKPoint(_rect.Left, _rect.Bottom - radius.Y);
                break;
        }
    }

    private bool ValidateState()
    {
        if (!IsFinite(_rect) || _rect.Left > _rect.Right || _rect.Top > _rect.Bottom)
        {
            return false;
        }

        for (var index = 0; index < CornerCount; index++)
        {
            var radius = _radii[index];
            if (!float.IsFinite(radius.X) || !float.IsFinite(radius.Y) ||
                !IsValidRadius(radius.X, _rect.Left, _rect.Right) ||
                !IsValidRadius(radius.Y, _rect.Top, _rect.Bottom) ||
                (radius.X == 0f) != (radius.Y == 0f))
            {
                return false;
            }
        }

        return GetComputedType() == _type;
    }

    private SKRoundRectType GetComputedType()
    {
        if (IsEmptyRect(_rect))
        {
            for (var index = 0; index < CornerCount; index++)
            {
                if (_radii[index] != default)
                {
                    return (SKRoundRectType)(-1);
                }
            }

            return SKRoundRectType.Empty;
        }

        var allRadiiEqual = true;
        var allCornersSquare = _radii[0].X == 0f || _radii[0].Y == 0f;
        for (var index = 1; index < CornerCount; index++)
        {
            allCornersSquare &= _radii[index].X == 0f || _radii[index].Y == 0f;
            allRadiiEqual &= _radii[index] == _radii[index - 1];
        }

        if (allCornersSquare)
        {
            return SKRoundRectType.Rect;
        }

        if (allRadiiEqual)
        {
            return _radii[0].X >= _rect.Width * 0.5f && _radii[0].Y >= _rect.Height * 0.5f
                ? SKRoundRectType.Oval
                : SKRoundRectType.Simple;
        }

        return RadiiAreNinePatch() ? SKRoundRectType.NinePatch : SKRoundRectType.Complex;
    }

    private void FlushToZero(int firstIndex, int secondIndex, bool xAxis)
    {
        var first = xAxis ? _radii[firstIndex].X : _radii[firstIndex].Y;
        var second = xAxis ? _radii[secondIndex].X : _radii[secondIndex].Y;
        if (first + second == first)
        {
            second = 0f;
        }
        else if (first + second == second)
        {
            first = 0f;
        }

        SetRadiusAxis(firstIndex, xAxis, first);
        SetRadiusAxis(secondIndex, xAxis, second);
    }

    private void AdjustRadii(int firstIndex, int secondIndex, bool xAxis, double limit, double scale)
    {
        var first = (float)(GetRadiusAxis(firstIndex, xAxis) * scale);
        var second = (float)(GetRadiusAxis(secondIndex, xAxis) * scale);
        if (first + second > limit)
        {
            var firstIsMinimum = first <= second;
            var minimum = firstIsMinimum ? first : second;
            var maximum = (float)(limit - minimum);
            while (maximum + minimum > limit)
            {
                maximum = MathF.BitDecrement(maximum);
            }

            if (firstIsMinimum)
            {
                second = maximum;
            }
            else
            {
                first = maximum;
            }
        }

        SetRadiusAxis(firstIndex, xAxis, first);
        SetRadiusAxis(secondIndex, xAxis, second);
    }

    private float GetRadiusAxis(int index, bool xAxis) => xAxis ? _radii[index].X : _radii[index].Y;

    private void SetRadiusAxis(int index, bool xAxis, float value)
    {
        _radii[index] = xAxis
            ? new SKPoint(value, _radii[index].Y)
            : new SKPoint(_radii[index].X, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearRadii() => _radii.Clear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillRadii(SKPoint radius)
    {
        _radii.Fill(radius);
    }

    private static double ComputeMinimumScale(double first, double second, double limit, double current) =>
        first + second > limit ? Math.Min(current, limit / (first + second)) : current;

    private static bool IsValidRadius(float radius, float minimum, float maximum) =>
        minimum <= maximum && radius <= maximum - minimum &&
        minimum + radius <= maximum && maximum - radius >= minimum && radius >= 0f;

    private static SKRect MapBounds(SKRect rect, SKMatrix matrix)
    {
        var upperLeft = MapPoint(new SKPoint(rect.Left, rect.Top), matrix);
        var upperRight = MapPoint(new SKPoint(rect.Right, rect.Top), matrix);
        var lowerRight = MapPoint(new SKPoint(rect.Right, rect.Bottom), matrix);
        var lowerLeft = MapPoint(new SKPoint(rect.Left, rect.Bottom), matrix);
        return new SKRect(
            MathF.Min(MathF.Min(upperLeft.X, upperRight.X), MathF.Min(lowerRight.X, lowerLeft.X)),
            MathF.Min(MathF.Min(upperLeft.Y, upperRight.Y), MathF.Min(lowerRight.Y, lowerLeft.Y)),
            MathF.Max(MathF.Max(upperLeft.X, upperRight.X), MathF.Max(lowerRight.X, lowerLeft.X)),
            MathF.Max(MathF.Max(upperLeft.Y, upperRight.Y), MathF.Max(lowerRight.Y, lowerLeft.Y)));
    }

    private static SKPoint MapPoint(SKPoint point, SKMatrix matrix) => new(
        matrix.ScaleX * point.X + matrix.SkewX * point.Y + matrix.TransX,
        matrix.SkewY * point.X + matrix.ScaleY * point.Y + matrix.TransY);

    private static float Midpoint(float first, float second) => first * 0.5f + second * 0.5f;

    private static bool IsEmptyRect(SKRect rect) => rect.Left >= rect.Right || rect.Top >= rect.Bottom;

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) &&
        float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);

    private struct CornerRadiusBuffer
    {
        private SKPoint _radius0;
        private SKPoint _radius1;
        private SKPoint _radius2;
        private SKPoint _radius3;

        public SKPoint this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            readonly get => index switch
            {
                0 => _radius0,
                1 => _radius1,
                2 => _radius2,
                3 => _radius3,
                _ => throw new IndexOutOfRangeException(),
            };
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                switch (index)
                {
                    case 0: _radius0 = value; break;
                    case 1: _radius1 = value; break;
                    case 2: _radius2 = value; break;
                    case 3: _radius3 = value; break;
                    default: throw new IndexOutOfRangeException();
                }
            }
        }

        public void Clear()
        {
            _radius0 = default;
            _radius1 = default;
            _radius2 = default;
            _radius3 = default;
        }

        public void Fill(SKPoint radius)
        {
            _radius0 = radius;
            _radius1 = radius;
            _radius2 = radius;
            _radius3 = radius;
        }

        public void SetNinePatch(float left, float top, float right, float bottom)
        {
            _radius0 = new SKPoint(left, top);
            _radius1 = new SKPoint(right, top);
            _radius2 = new SKPoint(right, bottom);
            _radius3 = new SKPoint(left, bottom);
        }

        public void Set(ReadOnlySpan<SKPoint> radii)
        {
            _radius0 = radii[0];
            _radius1 = radii[1];
            _radius2 = radii[2];
            _radius3 = radii[3];
        }

        public readonly void CopyTo(Span<SKPoint> destination)
        {
            if (destination.Length < CornerCount)
            {
                throw new ArgumentException("Destination must hold four corner radii.", nameof(destination));
            }

            for (var index = 0; index < CornerCount; index++)
            {
                destination[index] = this[index];
            }
        }

        public readonly CornerRadiusBuffer Clone() => this;
    }
}

public class SKRegion : SKObject
{
    [ThreadStatic]
    private static RegionNormalizationStorage? s_threadNormalizationStorage;
    [ThreadStatic]
    private static List<SKRectI>? s_threadRectStorage;

    private readonly List<SKRectI> _rects = RentRectStorage();
    private RegionNormalizationStorage? _normalizationStorage;
    private SKRectI _bounds;
    private bool _rectsNormalized = true;

    public bool IsEmpty => _rects.Count == 0;

    public bool IsRect
    {
        get
        {
            EnsureNormalized();
            return _rects.Count == 1;
        }
    }

    public bool IsComplex
    {
        get
        {
            EnsureNormalized();
            return _rects.Count > 1;
        }
    }

    public SKRectI Bounds => _bounds;

    public SKRegion()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    public SKRegion(SKRectI rect)
        : base(SKObjectHandle.Create(), owns: true)
    {
        SetRect(rect);
    }

    public SKRegion(SKRegion region)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(region);
        SetRegion(region);
    }

    public SKRegion(SKPath path)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bounds = path.Bounds;
        using var clip = new SKRegion(new SKRectI(
            (int)MathF.Floor(bounds.Left),
            (int)MathF.Floor(bounds.Top),
            (int)MathF.Ceiling(bounds.Right),
            (int)MathF.Ceiling(bounds.Bottom)));
        SetPath(path, clip);
    }

    internal IReadOnlyList<SKRectI> Rects
    {
        get
        {
            EnsureNormalized();
            return _rects;
        }
    }

    public bool Contains(int x, int y)
    {
        if (!Contains(_bounds, x, y))
        {
            return false;
        }

        EnsureNormalized();
        foreach (var rect in _rects)
        {
            if (Contains(rect, x, y))
            {
                return true;
            }
        }

        return false;
    }

    public bool Contains(SKPointI xy) => Contains(xy.X, xy.Y);

    public bool Contains(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            return false;
        }

        using var remainder = new SKRegion(rect);
        remainder.Op(this, SKRegionOperation.Difference);
        return remainder.IsEmpty;
    }

    public bool Contains(SKRegion src)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (src.IsEmpty)
        {
            return false;
        }

        using var remainder = new SKRegion(src);
        remainder.Op(this, SKRegionOperation.Difference);
        return remainder.IsEmpty;
    }

    public bool Contains(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new SKRegion(path);
        return Contains(region);
    }

    public bool QuickContains(SKRectI rect) => Contains(rect);

    public bool QuickReject(SKRectI rect) => !Intersects(rect);

    public bool QuickReject(SKRegion region) => !Intersects(region);

    public bool QuickReject(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bounds = path.Bounds;
        return QuickReject(new SKRectI(
            (int)MathF.Floor(bounds.Left),
            (int)MathF.Floor(bounds.Top),
            (int)MathF.Ceiling(bounds.Right),
            (int)MathF.Ceiling(bounds.Bottom)));
    }

    public bool SetPath(SKPath path)
    {
        if (!TryGetSingleAxisAlignedRect(path, out var rect))
        {
            SetEmpty();
            return false;
        }

        SetSingleRect(rect);
        return !IsEmpty;
    }

    public bool SetPath(SKPath path, SKRegion clip)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(clip);
        SetEmpty();
        if (path.IsEmpty || clip.IsEmpty)
        {
            return false;
        }

        var pathBounds = path.Bounds;
        var scanBounds = Intersect(
            clip.Bounds,
            new SKRectI(
                (int)MathF.Floor(pathBounds.Left),
                (int)MathF.Floor(pathBounds.Top),
                (int)MathF.Ceiling(pathBounds.Right),
                (int)MathF.Ceiling(pathBounds.Bottom)));
        if (!IsValid(scanBounds))
        {
            return false;
        }

        var polygons = BuildScanPolygons(path);
        if (polygons.Count == 0)
        {
            return false;
        }

        var previousRuns = new Dictionary<(int Left, int Right), int>();
        for (var y = scanBounds.Top; y < scanBounds.Bottom; y++)
        {
            var currentRuns = new Dictionary<(int Left, int Right), int>();
            var x = scanBounds.Left;
            while (x < scanBounds.Right)
            {
                while (x < scanBounds.Right &&
                       (!clip.Contains(x, y) ||
                        !ContainsScanPolygons(
                            polygons,
                            path.FillType,
                            new Vector2(x + 0.5f, y + 0.5f))))
                {
                    x++;
                }
                if (x >= scanBounds.Right)
                {
                    break;
                }

                var left = x++;
                while (x < scanBounds.Right &&
                       clip.Contains(x, y) &&
                       ContainsScanPolygons(
                           polygons,
                           path.FillType,
                           new Vector2(x + 0.5f, y + 0.5f)))
                {
                    x++;
                }

                var run = (Left: left, Right: x);
                if (previousRuns.TryGetValue(run, out var rectIndex) &&
                    _rects[rectIndex].Bottom == y)
                {
                    var previous = _rects[rectIndex];
                    _rects[rectIndex] = new SKRectI(
                        previous.Left,
                        previous.Top,
                        previous.Right,
                        y + 1);
                }
                else
                {
                    rectIndex = _rects.Count;
                    _rects.Add(new SKRectI(left, y, x, y + 1));
                }

                currentRuns[run] = rectIndex;
            }

            previousRuns = currentRuns;
        }

        _rectsNormalized = false;
        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    private static List<Vector2[]> BuildScanPolygons(SKPath path)
    {
        const int quadraticSteps = 16;
        const int cubicSteps = 24;
        var polygons = new List<Vector2[]>();
        List<Vector2>? current = null;
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        while (true)
        {
            var verb = iterator.Next(points);
            switch (verb)
            {
                case SKPathVerb.Move:
                    FlushCurrent();
                    current = new List<Vector2>
                    {
                        new(points[0].X, points[0].Y),
                    };
                    break;

                case SKPathVerb.Line:
                    AddPoint(new Vector2(points[1].X, points[1].Y));
                    break;

                case SKPathVerb.Quad:
                    AppendQuadratic(
                        new Vector2(points[0].X, points[0].Y),
                        new Vector2(points[1].X, points[1].Y),
                        new Vector2(points[2].X, points[2].Y));
                    break;

                case SKPathVerb.Conic:
                    AppendConic(
                        new Vector2(points[0].X, points[0].Y),
                        new Vector2(points[1].X, points[1].Y),
                        new Vector2(points[2].X, points[2].Y),
                        iterator.ConicWeight());
                    break;

                case SKPathVerb.Cubic:
                    AppendCubic(
                        new Vector2(points[0].X, points[0].Y),
                        new Vector2(points[1].X, points[1].Y),
                        new Vector2(points[2].X, points[2].Y),
                        new Vector2(points[3].X, points[3].Y));
                    break;

                case SKPathVerb.Close:
                    FlushCurrent();
                    break;

                case SKPathVerb.Done:
                    FlushCurrent();
                    return polygons;
            }
        }

        void AppendQuadratic(Vector2 p0, Vector2 p1, Vector2 p2)
        {
            for (var step = 1; step <= quadraticSteps; step++)
            {
                var t = (float)step / quadraticSteps;
                var u = 1f - t;
                AddPoint(u * u * p0 + 2f * u * t * p1 + t * t * p2);
            }
        }

        void AppendConic(Vector2 p0, Vector2 p1, Vector2 p2, float weight)
        {
            for (var step = 1; step <= quadraticSteps; step++)
            {
                var t = (float)step / quadraticSteps;
                var u = 1f - t;
                var weightedMiddle = 2f * weight * u * t;
                var denominator = u * u + weightedMiddle + t * t;
                AddPoint(
                    (u * u * p0 + weightedMiddle * p1 + t * t * p2) /
                    denominator);
            }
        }

        void AppendCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            for (var step = 1; step <= cubicSteps; step++)
            {
                var t = (float)step / cubicSteps;
                var u = 1f - t;
                AddPoint(
                    u * u * u * p0 +
                    3f * u * u * t * p1 +
                    3f * u * t * t * p2 +
                    t * t * t * p3);
            }
        }

        void AddPoint(Vector2 point)
        {
            if (current == null ||
                !float.IsFinite(point.X) ||
                !float.IsFinite(point.Y))
            {
                return;
            }

            if (current.Count == 0 ||
                Vector2.DistanceSquared(current[^1], point) > 1e-12f)
            {
                current.Add(point);
            }
        }

        void FlushCurrent()
        {
            if (current is { Count: >= 3 })
            {
                polygons.Add(current.ToArray());
            }
            current = null;
        }
    }

    private static bool ContainsScanPolygons(
        List<Vector2[]> polygons,
        SKPathFillType fillType,
        Vector2 point)
    {
        var evenOdd = false;
        var winding = 0;
        foreach (var polygon in polygons)
        {
            var previous = polygon[^1];
            foreach (var current in polygon)
            {
                var upward = previous.Y <= point.Y && current.Y > point.Y;
                var downward = previous.Y > point.Y && current.Y <= point.Y;
                if (upward || downward)
                {
                    var intersectionX = previous.X +
                        (point.Y - previous.Y) *
                        (current.X - previous.X) /
                        (current.Y - previous.Y);
                    if (intersectionX > point.X)
                    {
                        evenOdd = !evenOdd;
                        winding += upward ? 1 : -1;
                    }
                }
                previous = current;
            }
        }

        var contains = fillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
            ? evenOdd
            : winding != 0;
        return fillType is SKPathFillType.InverseEvenOdd or SKPathFillType.InverseWinding
            ? !contains
            : contains;
    }

    public bool SetRects(ReadOnlySpan<SKRectI> rects)
    {
        SetEmpty();
        foreach (var rect in rects)
        {
            AddRect(rect);
        }

        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    public bool SetRegion(SKRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (ReferenceEquals(this, region))
        {
            return !IsEmpty;
        }

        region.EnsureNormalized();
        _rects.Clear();
        _rects.AddRange(region._rects);
        _bounds = region._bounds;
        _rectsNormalized = true;
        return !IsEmpty;
    }

    public bool SetRect(SKRectI rect)
    {
        SetSingleRect(rect);
        return !IsEmpty;
    }

    public void Translate(int x, int y)
    {
        for (var index = 0; index < _rects.Count; index++)
        {
            var rect = _rects[index];
            _rects[index] = new SKRectI(
                unchecked(rect.Left + x),
                unchecked(rect.Top + y),
                unchecked(rect.Right + x),
                unchecked(rect.Bottom + y));
        }

        if (!IsEmpty)
        {
            _bounds = new SKRectI(
                unchecked(_bounds.Left + x),
                unchecked(_bounds.Top + y),
                unchecked(_bounds.Right + x),
                unchecked(_bounds.Bottom + y));
        }
    }

    public SKPath GetBoundaryPath()
    {
        EnsureNormalized();
        var path = new SKPath();
        foreach (var rect in _rects)
        {
            path.AddRect(rect);
        }

        return path;
    }

    public bool Op(SKRectI rect, SKRegionOperation op)
    {
        if (op is not SKRegionOperation.Replace and not SKRegionOperation.Union)
        {
            EnsureNormalized();
        }

        switch (op)
        {
            case SKRegionOperation.Replace:
                SetSingleRect(rect);
                break;
            case SKRegionOperation.Intersect:
                IntersectWith(rect);
                break;
            case SKRegionOperation.Union:
                if (!IsValid(rect))
                {
                    return !IsEmpty;
                }

                foreach (var existing in _rects)
                {
                    if (Contains(existing, rect))
                    {
                        return true;
                    }
                }

                AddRect(rect);
                UnionBounds(rect);
                return true;
            case SKRegionOperation.Difference:
                DifferenceWith(rect);
                break;
            case SKRegionOperation.ReverseDifference:
                ReverseDifferenceWith(rect);
                break;
            case SKRegionOperation.XOR:
                XorWith(rect);
                break;
            default:
                return false;
        }

        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    public bool Op(SKPath path, SKRegionOperation op)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new SKRegion(path);
        return Op(region, op);
    }

    public bool Op(SKRegion region, SKRegionOperation op)
    {
        ArgumentNullException.ThrowIfNull(region);
        EnsureNormalized();
        region.EnsureNormalized();
        using var leftSnapshot = new SKRegion(this);
        using var rightSnapshot = new SKRegion(region);
        _rectsNormalized = false;
        switch (op)
        {
            case SKRegionOperation.Replace:
                return SetRegion(rightSnapshot);

            case SKRegionOperation.Intersect:
                _rects.Clear();
                foreach (var left in leftSnapshot._rects)
                {
                    foreach (var right in rightSnapshot._rects)
                    {
                        AddRect(Intersect(left, right));
                    }
                }
                break;

            case SKRegionOperation.Union:
                _rects.Clear();
                _rects.AddRange(leftSnapshot._rects);
                _rects.AddRange(rightSnapshot._rects);
                break;

            case SKRegionOperation.Difference:
                _rects.Clear();
                _rects.AddRange(leftSnapshot._rects);
                foreach (var cutter in rightSnapshot._rects)
                {
                    DifferenceWith(cutter);
                }
                break;

            case SKRegionOperation.ReverseDifference:
                _rects.Clear();
                _rects.AddRange(rightSnapshot._rects);
                foreach (var cutter in leftSnapshot._rects)
                {
                    DifferenceWith(cutter);
                }
                break;

            case SKRegionOperation.XOR:
                using (var leftOnly = new SKRegion(leftSnapshot))
                using (var rightOnly = new SKRegion(rightSnapshot))
                {
                    leftOnly.Op(rightSnapshot, SKRegionOperation.Difference);
                    rightOnly.Op(leftSnapshot, SKRegionOperation.Difference);
                    _rects.Clear();
                    _rects.AddRange(leftOnly._rects);
                    _rects.AddRange(rightOnly._rects);
                }
                break;

            default:
                return false;
        }

        NormalizeRects();
        UpdateBounds();
        return !IsEmpty;
    }

    public bool Op(int left, int top, int right, int bottom, SKRegionOperation op)
    {
        return Op(new SKRectI(left, top, right, bottom), op);
    }

    public void SetEmpty()
    {
        _rects.Clear();
        _bounds = SKRectI.Empty;
        _rectsNormalized = true;
    }

    public bool Intersects(SKRectI rect)
    {
        if (!IsValid(Intersect(_bounds, rect)))
        {
            return false;
        }

        EnsureNormalized();
        foreach (var existing in _rects)
        {
            if (IsValid(Intersect(existing, rect)))
            {
                return true;
            }
        }

        return false;
    }

    public bool Intersects(SKRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        foreach (var rect in region._rects)
        {
            if (Intersects(rect))
            {
                return true;
            }
        }

        return false;
    }

    public bool Intersects(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var region = new SKRegion(path);
        return Intersects(region);
    }

    public RectIterator CreateRectIterator()
    {
        EnsureNormalized();
        return new RectIterator(_rects);
    }

    public ClipIterator CreateClipIterator(SKRectI clip)
    {
        EnsureNormalized();
        return new ClipIterator(_rects, clip);
    }

    public SpanIterator CreateSpanIterator(int y, int left, int right)
    {
        EnsureNormalized();
        return new SpanIterator(_rects, y, left, right);
    }

    private void SetSingleRect(SKRectI rect)
    {
        _rects.Clear();
        AddRect(rect);
        UpdateBounds();
        _rectsNormalized = true;
    }

    private void AddRect(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            return;
        }

        _rects.Add(rect);
        _rectsNormalized = false;
    }

    private void IntersectWith(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            SetEmpty();
            return;
        }

        _rectsNormalized = false;
        for (int i = _rects.Count - 1; i >= 0; i--)
        {
            var intersection = Intersect(_rects[i], rect);
            if (IsValid(intersection))
            {
                _rects[i] = intersection;
            }
            else
            {
                _rects.RemoveAt(i);
            }
        }
    }

    private void DifferenceWith(SKRectI rect)
    {
        if (!IsValid(rect) || _rects.Count == 0)
        {
            return;
        }

        var result = new List<SKRectI>(_rects.Count);
        foreach (var source in _rects)
        {
            AddDifference(result, source, rect);
        }

        _rects.Clear();
        _rects.AddRange(result);
        _rectsNormalized = false;
    }

    private void ReverseDifferenceWith(SKRectI rect)
    {
        var result = new List<SKRectI>();
        AddIfValid(result, rect);
        foreach (var existing in _rects)
        {
            for (int i = result.Count - 1; i >= 0; i--)
            {
                var current = result[i];
                result.RemoveAt(i);
                AddDifference(result, current, existing);
            }
        }

        _rects.Clear();
        _rects.AddRange(result);
        _rectsNormalized = false;
    }

    private void XorWith(SKRectI rect)
    {
        var left = new List<SKRectI>();
        foreach (var existing in _rects)
        {
            AddDifference(left, existing, rect);
        }

        var right = new List<SKRectI>();
        AddIfValid(right, rect);
        foreach (var existing in _rects)
        {
            for (int i = right.Count - 1; i >= 0; i--)
            {
                var current = right[i];
                right.RemoveAt(i);
                AddDifference(right, current, existing);
            }
        }

        _rects.Clear();
        _rects.AddRange(left);
        _rects.AddRange(right);
        _rectsNormalized = false;
    }

    private static void AddDifference(List<SKRectI> result, SKRectI source, SKRectI cutter)
    {
        if (!IsValid(source))
        {
            return;
        }

        var overlap = Intersect(source, cutter);
        if (!IsValid(overlap))
        {
            result.Add(source);
            return;
        }

        AddIfValid(result, new SKRectI(source.Left, source.Top, source.Right, overlap.Top));
        AddIfValid(result, new SKRectI(source.Left, overlap.Bottom, source.Right, source.Bottom));
        AddIfValid(result, new SKRectI(source.Left, overlap.Top, overlap.Left, overlap.Bottom));
        AddIfValid(result, new SKRectI(overlap.Right, overlap.Top, source.Right, overlap.Bottom));
    }

    private static void AddIfValid(List<SKRectI> result, SKRectI rect)
    {
        if (IsValid(rect))
        {
            result.Add(rect);
        }
    }

    private void NormalizeRects()
    {
        if (_rectsNormalized)
        {
            return;
        }

        if (_rects.Count <= 1)
        {
            _rectsNormalized = true;
            return;
        }

        var storage = _normalizationStorage ??= RentNormalizationStorage();
        var yCoordinates = storage.YCoordinates;
        yCoordinates.Clear();
        foreach (var rect in _rects)
        {
            if (IsValid(rect))
            {
                yCoordinates.Add(rect.Top);
                yCoordinates.Add(rect.Bottom);
            }
        }
        yCoordinates.Sort();
        var uniqueCount = 0;
        for (var index = 0; index < yCoordinates.Count; index++)
        {
            if (uniqueCount == 0 || yCoordinates[index] != yCoordinates[uniqueCount - 1])
            {
                yCoordinates[uniqueCount++] = yCoordinates[index];
            }
        }
        if (uniqueCount < yCoordinates.Count)
        {
            yCoordinates.RemoveRange(uniqueCount, yCoordinates.Count - uniqueCount);
        }
        if (yCoordinates.Count < 2)
        {
            _rects.Clear();
            _rectsNormalized = true;
            return;
        }

        var normalized = storage.Rects;
        normalized.Clear();
        var previousBand = storage.BandA;
        var currentBand = storage.BandB;
        previousBand.Clear();
        currentBand.Clear();
        for (var yIndex = 0; yIndex + 1 < yCoordinates.Count; yIndex++)
        {
            var top = yCoordinates[yIndex];
            var bottom = yCoordinates[yIndex + 1];
            if (bottom <= top)
            {
                continue;
            }

            var intervals = storage.Intervals;
            intervals.Clear();
            foreach (var rect in _rects)
            {
                if (IsValid(rect) && rect.Top <= top && rect.Bottom >= bottom)
                {
                    intervals.Add((rect.Left, rect.Right));
                }
            }
            if (intervals.Count == 0)
            {
                previousBand.Clear();
                continue;
            }

            intervals.Sort(static (left, right) =>
            {
                var comparison = left.Left.CompareTo(right.Left);
                return comparison != 0 ? comparison : left.Right.CompareTo(right.Right);
            });

            var merged = storage.MergedIntervals;
            merged.Clear();
            var current = intervals[0];
            for (var intervalIndex = 1; intervalIndex < intervals.Count; intervalIndex++)
            {
                var next = intervals[intervalIndex];
                if (next.Left <= current.Right)
                {
                    current.Right = Math.Max(current.Right, next.Right);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }
            merged.Add(current);

            currentBand.Clear();
            foreach (var interval in merged)
            {
                if (previousBand.TryGetValue(interval, out var rectIndex) &&
                    normalized[rectIndex].Bottom == top)
                {
                    var previous = normalized[rectIndex];
                    normalized[rectIndex] = new SKRectI(
                        previous.Left,
                        previous.Top,
                        previous.Right,
                        bottom);
                }
                else
                {
                    rectIndex = normalized.Count;
                    normalized.Add(new SKRectI(interval.Left, top, interval.Right, bottom));
                }

                currentBand[interval] = rectIndex;
            }

            (previousBand, currentBand) = (currentBand, previousBand);
        }

        _rects.Clear();
        _rects.AddRange(normalized);
        _rectsNormalized = true;
    }

    private void EnsureNormalized() => NormalizeRects();

    private static RegionNormalizationStorage RentNormalizationStorage()
    {
        var storage = s_threadNormalizationStorage;
        if (storage is null)
        {
            return new RegionNormalizationStorage();
        }

        s_threadNormalizationStorage = null;
        return storage;
    }

    private static List<SKRectI> RentRectStorage()
    {
        var rects = s_threadRectStorage;
        if (rects is null)
        {
            return new List<SKRectI>();
        }

        s_threadRectStorage = null;
        return rects;
    }

    private void UnionBounds(SKRectI rect)
    {
        if (_rects.Count == 1)
        {
            _bounds = rect;
            return;
        }

        _bounds = new SKRectI(
            Math.Min(_bounds.Left, rect.Left),
            Math.Min(_bounds.Top, rect.Top),
            Math.Max(_bounds.Right, rect.Right),
            Math.Max(_bounds.Bottom, rect.Bottom));
    }

    private void UpdateBounds()
    {
        if (_rects.Count == 0)
        {
            _bounds = SKRectI.Empty;
            return;
        }

        var bounds = _rects[0];
        for (int i = 1; i < _rects.Count; i++)
        {
            var rect = _rects[i];
            bounds = new SKRectI(
                Math.Min(bounds.Left, rect.Left),
                Math.Min(bounds.Top, rect.Top),
                Math.Max(bounds.Right, rect.Right),
                Math.Max(bounds.Bottom, rect.Bottom));
        }

        _bounds = bounds;
    }

    private static SKRectI Intersect(SKRectI left, SKRectI right)
    {
        return new SKRectI(
            Math.Max(left.Left, right.Left),
            Math.Max(left.Top, right.Top),
            Math.Min(left.Right, right.Right),
            Math.Min(left.Bottom, right.Bottom));
    }

    private static bool Contains(SKRectI rect, int x, int y)
    {
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    private static bool Contains(SKRectI outer, SKRectI inner)
    {
        return IsValid(inner) &&
               outer.Left <= inner.Left &&
               outer.Top <= inner.Top &&
               outer.Right >= inner.Right &&
               outer.Bottom >= inner.Bottom;
    }

    private static bool IsValid(SKRectI rect)
    {
        return rect.Width > 0 && rect.Height > 0;
    }

    protected override void DisposeManaged()
    {
        _rects.Clear();
        if (s_threadRectStorage is null && _rects.Capacity <= 1_024)
        {
            s_threadRectStorage = _rects;
        }

        if (_normalizationStorage is { } storage)
        {
            _normalizationStorage = null;
            storage.Clear();
            if (s_threadNormalizationStorage is null && storage.CanRetain)
            {
                s_threadNormalizationStorage = storage;
            }
        }

        base.DisposeManaged();
    }

    private sealed class RegionNormalizationStorage
    {
        public List<int> YCoordinates { get; } = new();
        public List<(int Left, int Right)> Intervals { get; } = new();
        public List<(int Left, int Right)> MergedIntervals { get; } = new();
        public List<SKRectI> Rects { get; } = new();
        public Dictionary<(int Left, int Right), int> BandA { get; } = new();
        public Dictionary<(int Left, int Right), int> BandB { get; } = new();

        public bool CanRetain =>
            YCoordinates.Capacity <= 512 &&
            Intervals.Capacity <= 1_024 &&
            MergedIntervals.Capacity <= 1_024 &&
            Rects.Capacity <= 1_024 &&
            BandA.EnsureCapacity(0) <= 2_048 &&
            BandB.EnsureCapacity(0) <= 2_048;

        public void Clear()
        {
            YCoordinates.Clear();
            Intervals.Clear();
            MergedIntervals.Clear();
            Rects.Clear();
            BandA.Clear();
            BandB.Clear();
        }
    }

    private static bool TryGetSingleAxisAlignedRect(SKPath path, out SKRectI rect)
    {
        rect = SKRectI.Empty;
        if (path.Geometry.Figures.Count != 1)
        {
            return false;
        }

        var figure = path.Geometry.Figures[0];
        if (!figure.IsClosed || figure.Segments.Count != 3)
        {
            return false;
        }

        Span<Vector2> points = stackalloc Vector2[4];
        points[0] = figure.StartPoint;
        for (int i = 0; i < figure.Segments.Count; i++)
        {
            if (figure.Segments[i] is not LineSegment line)
            {
                return false;
            }

            points[i + 1] = line.Point;
        }

        float left = points[0].X;
        float right = points[0].X;
        float top = points[0].Y;
        float bottom = points[0].Y;
        for (int i = 1; i < points.Length; i++)
        {
            left = MathF.Min(left, points[i].X);
            right = MathF.Max(right, points[i].X);
            top = MathF.Min(top, points[i].Y);
            bottom = MathF.Max(bottom, points[i].Y);
        }

        if (!float.IsFinite(left) ||
            !float.IsFinite(right) ||
            !float.IsFinite(top) ||
            !float.IsFinite(bottom) ||
            right <= left ||
            bottom <= top)
        {
            return false;
        }

        bool hasTopLeft = false;
        bool hasTopRight = false;
        bool hasBottomRight = false;
        bool hasBottomLeft = false;
        foreach (var point in points)
        {
            if (Near(point.X, left) && Near(point.Y, top))
            {
                hasTopLeft = true;
            }
            else if (Near(point.X, right) && Near(point.Y, top))
            {
                hasTopRight = true;
            }
            else if (Near(point.X, right) && Near(point.Y, bottom))
            {
                hasBottomRight = true;
            }
            else if (Near(point.X, left) && Near(point.Y, bottom))
            {
                hasBottomLeft = true;
            }
            else
            {
                return false;
            }
        }

        if (!hasTopLeft || !hasTopRight || !hasBottomRight || !hasBottomLeft)
        {
            return false;
        }

        rect = new SKRectI(
            (int)MathF.Floor(left),
            (int)MathF.Floor(top),
            (int)MathF.Ceiling(right),
            (int)MathF.Ceiling(bottom));
        return IsValid(rect);
    }

    private static bool Near(float left, float right)
    {
        return MathF.Abs(left - right) <= 0.0001f;
    }

    public class RectIterator : SKObject
    {
        private SKRectI[] _rects;
        private int _index;

        internal RectIterator(IReadOnlyList<SKRectI> rects)
            : base(SKObjectHandle.Create(), owns: true)
        {
            _rects = CopyRects(rects);
        }

        public bool Next(out SKRectI rect)
        {
            if (_index >= _rects.Length)
            {
                rect = default;
                return false;
            }

            rect = _rects[_index++];
            return true;
        }

        protected override void DisposeNative()
        {
            _rects = Array.Empty<SKRectI>();
            base.DisposeNative();
        }
    }

    public class ClipIterator : SKObject
    {
        private SKRectI[] _rects;
        private int _index;

        internal ClipIterator(IReadOnlyList<SKRectI> rects, SKRectI clip)
            : base(SKObjectHandle.Create(), owns: true)
        {
            var clipped = new List<SKRectI>(rects.Count);
            foreach (var rect in rects)
            {
                if (IsValid(Intersect(rect, clip)))
                {
                    clipped.Add(rect);
                }
            }
            _rects = clipped.ToArray();
        }

        public bool Next(out SKRectI rect)
        {
            if (_index >= _rects.Length)
            {
                rect = default;
                return false;
            }

            rect = _rects[_index++];
            return true;
        }

        protected override void DisposeNative()
        {
            _rects = Array.Empty<SKRectI>();
            base.DisposeNative();
        }
    }

    public class SpanIterator : SKObject
    {
        private (int Left, int Right)[] _spans;
        private int _index;

        internal SpanIterator(
            IReadOnlyList<SKRectI> rects,
            int y,
            int left,
            int right)
            : base(SKObjectHandle.Create(), owns: true)
        {
            var spans = new List<(int Left, int Right)>();
            if (right > left)
            {
                foreach (var rect in rects)
                {
                    if (y >= rect.Top && y < rect.Bottom)
                    {
                        var spanLeft = Math.Max(left, rect.Left);
                        var spanRight = Math.Min(right, rect.Right);
                        if (spanRight > spanLeft)
                        {
                            spans.Add((spanLeft, spanRight));
                        }
                    }
                }
            }

            spans.Sort(static (first, second) => first.Left.CompareTo(second.Left));
            if (spans.Count > 1)
            {
                var write = 0;
                for (var read = 1; read < spans.Count; read++)
                {
                    if (spans[read].Left <= spans[write].Right)
                    {
                        spans[write] = (
                            spans[write].Left,
                            Math.Max(spans[write].Right, spans[read].Right));
                    }
                    else
                    {
                        spans[++write] = spans[read];
                    }
                }
                if (write + 1 < spans.Count)
                {
                    spans.RemoveRange(write + 1, spans.Count - write - 1);
                }
            }

            _spans = spans.ToArray();
        }

        public bool Next(out int left, out int right)
        {
            if (_index >= _spans.Length)
            {
                left = 0;
                right = 0;
                return false;
            }

            (left, right) = _spans[_index++];
            return true;
        }

        protected override void DisposeNative()
        {
            _spans = Array.Empty<(int Left, int Right)>();
            base.DisposeNative();
        }
    }

    private static SKRectI[] CopyRects(IReadOnlyList<SKRectI> rects)
    {
        var copy = new SKRectI[rects.Count];
        for (var index = 0; index < rects.Count; index++)
        {
            copy[index] = rects[index];
        }

        return copy;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }

    protected override void DisposeNative()
    {
        _rects.Clear();
        _bounds = default;
        base.DisposeNative();
    }
}

public enum SKPathVerb
{
    Move = 0,
    Line = 1,
    Quad = 2,
    Conic = 3,
    Cubic = 4,
    Close = 5,
    Done = 6
}
