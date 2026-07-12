using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Vector;

namespace SkiaSharp;

public partial class SKPath : SKObject
{
    public override IntPtr Handle
    {
        get => base.Handle;
        protected set => base.Handle = value;
    }

    private PathFigure? _currentFigure;
    private Vector2 _currentPoint;
    private Vector2 _contourStart;
    private SKPathFillType _fillType = SKPathFillType.Winding;

    public PathGeometry Geometry { get; } = new();
    public SKPathFillType FillType
    {
        get => _fillType;
        set
        {
            _fillType = value;
            Geometry.FillRule = value is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd
                ? FillRule.EvenOdd
                : FillRule.Nonzero;
        }
    }

    public SKPath()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    public SKPath(SKPath source)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(source);
        PathFigure? copiedCurrentFigure = null;
        foreach (var figure in source.Geometry.Figures)
        {
            var copiedFigure = CloneFigure(figure, Vector2.Zero);
            Geometry.Figures.Add(copiedFigure);
            if (ReferenceEquals(figure, source._currentFigure))
            {
                copiedCurrentFigure = copiedFigure;
            }
        }

        _currentFigure = copiedCurrentFigure;
        _currentPoint = source._currentPoint;
        _contourStart = source._contourStart;
        FillType = source.FillType;
    }

    public static SKPath ParseSvgPathData(string pathData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathData);
        var geometry = PathGeometry.Parse(pathData);
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
            return Geometry.TryGetBounds(out var min, out var max)
                ? new SKRect(min.X, min.Y, max.X, max.Y)
                : SKRect.Empty;
        }
    }

    public SKRect TightBounds
    {
        get
        {
            if (Geometry.IsCombined)
            {
                return Bounds;
            }

            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var hasBounds = false;

            void Include(Vector2 point)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                {
                    return;
                }

                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
                hasBounds = true;
            }

            foreach (var figure in Geometry.Figures)
            {
                var current = figure.StartPoint;
                Include(current);

                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            Include(line.Point);
                            current = line.Point;
                            break;

                        case QuadraticBezierSegment quadratic:
                            IncludeQuadraticExtrema(current, quadratic.ControlPoint, quadratic.Point, Include);
                            Include(quadratic.Point);
                            current = quadratic.Point;
                            break;

                        case CubicBezierSegment cubic:
                            IncludeCubicExtrema(
                                current,
                                cubic.ControlPoint1,
                                cubic.ControlPoint2,
                                cubic.Point,
                                Include);
                            Include(cubic.Point);
                            current = cubic.Point;
                            break;

                        case ArcSegment arc:
                            if (ArcSegmentGeometry.TryGetArcBounds(current, arc, out var arcMin, out var arcMax))
                            {
                                Include(arcMin);
                                Include(arcMax);
                            }
                            else
                            {
                                Include(arc.Point);
                            }

                            current = arc.Point;
                            break;
                    }
                }
            }

            return hasBounds
                ? new SKRect(min.X, min.Y, max.X, max.Y)
                : SKRect.Empty;
        }
    }

    public bool IsEmpty => Geometry.Figures.Count == 0;

    private static void IncludeQuadraticExtrema(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Action<Vector2> include)
    {
        IncludeQuadraticAxisExtremum(p0.X, p1.X, p2.X, p0, p1, p2, include);
        IncludeQuadraticAxisExtremum(p0.Y, p1.Y, p2.Y, p0, p1, p2, include);
    }

    private static void IncludeQuadraticAxisExtremum(
        float v0,
        float v1,
        float v2,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Action<Vector2> include)
    {
        var denominator = v0 - 2f * v1 + v2;
        if (MathF.Abs(denominator) <= 1e-6f)
        {
            return;
        }

        var t = (v0 - v1) / denominator;
        if (t > 0f && t < 1f)
        {
            var oneMinusT = 1f - t;
            include(oneMinusT * oneMinusT * p0 + 2f * oneMinusT * t * p1 + t * t * p2);
        }
    }

    private static void IncludeCubicExtrema(
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Action<Vector2> include)
    {
        IncludeCubicAxisExtrema(p0.X, p1.X, p2.X, p3.X, p0, p1, p2, p3, include);
        IncludeCubicAxisExtrema(p0.Y, p1.Y, p2.Y, p3.Y, p0, p1, p2, p3, include);
    }

    private static void IncludeCubicAxisExtrema(
        float v0,
        float v1,
        float v2,
        float v3,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Action<Vector2> include)
    {
        var a = -v0 + 3f * v1 - 3f * v2 + v3;
        var b = 2f * (v0 - 2f * v1 + v2);
        var c = v1 - v0;

        if (MathF.Abs(a) <= 1e-6f)
        {
            if (MathF.Abs(b) > 1e-6f)
            {
                IncludeCubicAt(-c / b, p0, p1, p2, p3, include);
            }

            return;
        }

        var discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
        {
            return;
        }

        var root = MathF.Sqrt(MathF.Max(0f, discriminant));
        var denominator = 2f * a;
        IncludeCubicAt((-b + root) / denominator, p0, p1, p2, p3, include);
        if (root > 1e-6f)
        {
            IncludeCubicAt((-b - root) / denominator, p0, p1, p2, p3, include);
        }
    }

    private static void IncludeCubicAt(
        float t,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 p3,
        Action<Vector2> include)
    {
        if (t <= 0f || t >= 1f || !float.IsFinite(t))
        {
            return;
        }

        var oneMinusT = 1f - t;
        include(
            oneMinusT * oneMinusT * oneMinusT * p0 +
            3f * oneMinusT * oneMinusT * t * p1 +
            3f * oneMinusT * t * t * p2 +
            t * t * t * p3);
    }

    private void EnsureFigure()
    {
        if (_currentFigure == null)
        {
            _currentFigure = new PathFigure(_currentPoint);
            Geometry.Figures.Add(_currentFigure);
            _contourStart = _currentPoint;
        }
    }

    public void MoveTo(float x, float y)
    {
        var point = new Vector2(x, y);
        _currentFigure = new PathFigure(point);
        Geometry.Figures.Add(_currentFigure);
        _currentPoint = point;
        _contourStart = point;
    }

    public void MoveTo(SKPoint p) => MoveTo(p.X, p.Y);

    public void LineTo(float x, float y)
    {
        EnsureFigure();
        var point = new Vector2(x, y);
        _currentFigure!.Segments.Add(new LineSegment(point));
        _currentPoint = point;
    }

    public void LineTo(SKPoint p) => LineTo(p.X, p.Y);

    public void QuadTo(float x0, float y0, float x1, float y1)
    {
        EnsureFigure();
        var point = new Vector2(x1, y1);
        _currentFigure!.Segments.Add(new QuadraticBezierSegment(new Vector2(x0, y0), point));
        _currentPoint = point;
    }

    public void QuadTo(SKPoint p0, SKPoint p1) => QuadTo(p0.X, p0.Y, p1.X, p1.Y);

    public void CubicTo(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        EnsureFigure();
        var point = new Vector2(x2, y2);
        _currentFigure!.Segments.Add(new CubicBezierSegment(new Vector2(x0, y0), new Vector2(x1, y1), point));
        _currentPoint = point;
    }

    public void CubicTo(SKPoint p0, SKPoint p1, SKPoint p2) => CubicTo(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);

    public void ArcTo(float rx, float ry, float xAxisRotation, SKPathArcSize largeArc, SKPathDirection sweep, float x, float y)
    {
        EnsureFigure();
        var point = new Vector2(x, y);
        if (!float.IsFinite(rx) || !float.IsFinite(ry) || MathF.Abs(rx) <= PathEpsilon || MathF.Abs(ry) <= PathEpsilon)
        {
            LineTo(x, y);
            return;
        }

        var sd = sweep == SKPathDirection.Clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        _currentFigure!.Segments.Add(new ArcSegment(point, new Vector2(MathF.Abs(rx), MathF.Abs(ry)), xAxisRotation, largeArc == SKPathArcSize.Large, sd));
        _currentPoint = point;
    }

    public void Close()
    {
        if (_currentFigure != null)
        {
            _currentFigure.IsClosed = true;
            _currentPoint = _contourStart;
            _currentFigure = null;
        }
    }

    public void Reset()
    {
        Geometry.Figures.Clear();
        ResetCurrentState();
        FillType = SKPathFillType.Winding;
    }

    public void AddCircle(float x, float y, float radius, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        AddOval(new SKRect(x - radius, y - radius, x + radius, y + radius), direction);
    }

    public void AddOval(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        AppendOvalArc(rect, 0f, direction == SKPathDirection.Clockwise ? 360f : -360f, forceMoveTo: true);
        Close();
    }

    public void ConicTo(SKPoint control, SKPoint end, float weight)
    {
        ConicTo(control.X, control.Y, end.X, end.Y, weight);
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

    public void AddRect(SKRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
        => AddRect(rect, direction, 0);

    public void AddRoundRect(SKRoundRect rect, SKPathDirection direction = SKPathDirection.Clockwise)
        => AddRoundRect(
            rect,
            direction,
            direction == SKPathDirection.Clockwise ? 6u : 7u);

    public void AddRoundRect(SKRect rect, float rx, float ry, SKPathDirection direction = SKPathDirection.Clockwise)
    {
        AddRoundRect(new SKRoundRect(rect, rx, ry), direction);
    }

    public void AddPath(
        SKPath other,
        float x,
        float y,
        SKPathAddMode mode = SKPathAddMode.Append)
    {
        ArgumentNullException.ThrowIfNull(other);
        AddPathCore(other, new Vector2(x, y), mode);
    }

    public void AddPath(SKPath other, SKPathAddMode mode = SKPathAddMode.Append) =>
        AddPath(other, 0f, 0f, mode);

    public void AddPath(SKPath other, in SKMatrix matrix, SKPathAddMode mode = SKPathAddMode.Append)
    {
        using var copy = new SKPath(other);
        copy.Transform(matrix);
        AddPath(copy, mode);
    }

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
        var result = new SKPath();
        var solvedGeometry = PathOpGeometrySolver.Combine(this.Geometry, other.Geometry, (int)op);
        ApplySolvedGeometry(result, solvedGeometry);
        return result;
    }

    public bool Op(SKPath other, SKPathOp op, SKPath result)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(result);
        var solvedGeometry = PathOpGeometrySolver.Combine(Geometry, other.Geometry, (int)op);
        ApplySolvedGeometry(result, solvedGeometry);
        return true;
    }

    private static void ApplySolvedGeometry(SKPath result, PathGeometry solvedGeometry)
    {
        result.Geometry.Figures.Clear();
        result.FillType = ToSkPathFillType(solvedGeometry.FillRule);
        foreach (var fig in solvedGeometry.Figures)
        {
            result.Geometry.Figures.Add(fig);
        }

        result.RestoreCurrentState();
    }

    private static SKPathFillType ToSkPathFillType(FillRule fillRule)
    {
        return fillRule == FillRule.EvenOdd
            ? SKPathFillType.EvenOdd
            : SKPathFillType.Winding;
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

public class SKRoundRect : IDisposable
{
    private const float NearlyZero = 1f / (1 << 12);
    private readonly SKPoint[] _radii = new SKPoint[4];
    private SKRect _rect;
    private SKRoundRectType _type;

    public SKRect Rect => _rect;

    public SKPoint[] Radii => (SKPoint[])_radii.Clone();

    public SKRoundRectType Type => _type;

    public float Width => _rect.Width;

    public float Height => _rect.Height;

    public bool IsValid => ValidateState();

    public bool AllCornersCircular => CheckAllCornersCircular(NearlyZero);

    internal SKPoint[] CornerRadii => _radii;

    public SKRoundRect()
    {
        SetEmpty();
    }

    public SKRoundRect(SKRect rect)
    {
        SetRect(rect);
    }

    public SKRoundRect(SKRect rect, float radius)
        : this(rect, radius, radius)
    {
    }

    public SKRoundRect(SKRect rect, float xRadius, float yRadius)
    {
        SetRect(rect, xRadius, yRadius);
    }

    public SKRoundRect(SKRoundRect rrect)
    {
        ArgumentNullException.ThrowIfNull(rrect);
        _rect = rrect._rect;
        _type = rrect._type;
        Array.Copy(rrect._radii, _radii, _radii.Length);
    }

    public bool CheckAllCornersCircular(float tolerance)
    {
        for (var index = 0; index < _radii.Length; index++)
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
        Array.Clear(_radii);
        _type = SKRoundRectType.Empty;
    }

    public void SetRect(SKRect rect)
    {
        if (!InitializeRect(rect))
        {
            return;
        }

        Array.Clear(_radii);
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

        Array.Fill(_radii, new SKPoint(xRadius, yRadius));
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
            Array.Clear(_radii);
            _type = SKRoundRectType.Rect;
            return;
        }

        Array.Fill(_radii, new SKPoint(xRadius, yRadius));
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

        _radii[0] = new SKPoint(leftRadius, topRadius);
        _radii[1] = new SKPoint(rightRadius, topRadius);
        _radii[2] = new SKPoint(rightRadius, bottomRadius);
        _radii[3] = new SKPoint(leftRadius, bottomRadius);
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

            _radii[index] = radii[index];
        }

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

    void IDisposable.Dispose()
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
            Array.Clear(_radii);
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
            Array.Clear(_radii);
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

    private void ComputeType()
    {
        if (IsEmptyRect(_rect))
        {
            Array.Clear(_radii);
            _type = SKRoundRectType.Empty;
            return;
        }

        var allRadiiEqual = true;
        var allCornersSquare = _radii[0].X == 0f || _radii[0].Y == 0f;
        for (var index = 1; index < _radii.Length; index++)
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
                Array.Fill(_radii, new SKPoint(_rect.Width * 0.5f, _rect.Height * 0.5f));
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
        for (var index = 0; index < _radii.Length; index++)
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

        for (var index = 0; index < _radii.Length; index++)
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
            for (var index = 0; index < _radii.Length; index++)
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
        for (var index = 1; index < _radii.Length; index++)
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
}

public class SKRegion : IDisposable
{
    private readonly List<SKRectI> _rects = new();
    private SKRectI _bounds;

    public bool IsEmpty => _rects.Count == 0;

    public SKRectI Bounds => _bounds;

    public SKRegion() { }

    internal IReadOnlyList<SKRectI> Rects => _rects;

    public bool Contains(int x, int y)
    {
        foreach (var rect in _rects)
        {
            if (Contains(rect, x, y))
            {
                return true;
            }
        }

        return false;
    }

    public bool SetPath(SKPath path)
    {
        if (!TryGetSingleAxisAlignedRect(path, out var rect))
        {
            _rects.Clear();
            _bounds = SKRectI.Empty;
            return false;
        }

        SetSingleRect(rect);
        return !IsEmpty;
    }

    public bool SetRect(SKRectI rect)
    {
        SetSingleRect(rect);
        return !IsEmpty;
    }

    public bool Op(SKRectI rect, SKRegionOperation op)
    {
        switch (op)
        {
            case SKRegionOperation.Replace:
                SetSingleRect(rect);
                break;
            case SKRegionOperation.Intersect:
                IntersectWith(rect);
                break;
            case SKRegionOperation.Union:
                AddRect(rect);
                break;
            case SKRegionOperation.Difference:
                DifferenceWith(rect);
                break;
            case SKRegionOperation.ReverseDifference:
                ReverseDifferenceWith(rect);
                break;
            case SKRegionOperation.Xor:
                XorWith(rect);
                break;
            default:
                return false;
        }

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
    }

    public bool Intersects(SKRectI rect)
    {
        foreach (var existing in _rects)
        {
            if (IsValid(Intersect(existing, rect)))
            {
                return true;
            }
        }

        return false;
    }

    public SKRegionRectIterator CreateRectIterator()
    {
        return new SKRegionRectIterator(_rects);
    }

    private void SetSingleRect(SKRectI rect)
    {
        _rects.Clear();
        AddRect(rect);
        UpdateBounds();
    }

    private void AddRect(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            return;
        }

        _rects.Add(rect);
    }

    private void IntersectWith(SKRectI rect)
    {
        if (!IsValid(rect))
        {
            _rects.Clear();
            return;
        }

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

    private static bool IsValid(SKRectI rect)
    {
        return rect.Width > 0 && rect.Height > 0;
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

    public void Dispose() { }
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

internal sealed class LegacySKPathRawIterator : IDisposable
{
    private readonly List<PathOperation> _operations = new();
    private int _index;
    private float _conicWeight = 1f;

    internal LegacySKPathRawIterator(SKPath path, bool forceClose)
    {
        foreach (var figure in path.Geometry.Figures)
        {
            var current = figure.StartPoint;
            _operations.Add(new PathOperation(SKPathVerb.Move, current, default, default, default));
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        _operations.Add(new PathOperation(SKPathVerb.Line, current, line.Point, default, default));
                        current = line.Point;
                        break;
                    case QuadraticBezierSegment quadratic:
                        _operations.Add(new PathOperation(
                            SKPathVerb.Quad,
                            current,
                            quadratic.ControlPoint,
                            quadratic.Point,
                            default));
                        current = quadratic.Point;
                        break;
                    case CubicBezierSegment cubic:
                        _operations.Add(new PathOperation(
                            SKPathVerb.Cubic,
                            current,
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point));
                        current = cubic.Point;
                        break;
                    case ArcSegment arc:
                        var flattened = ArcSegmentGeometry.FlattenArc(current, arc, MathF.PI / 32f);
                        for (var i = 1; i < flattened.Length; i++)
                        {
                            _operations.Add(new PathOperation(
                                SKPathVerb.Line,
                                flattened[i - 1],
                                flattened[i],
                                default,
                                default));
                        }

                        current = arc.Point;
                        break;
                }
            }

            if (figure.IsClosed || forceClose)
            {
                _operations.Add(new PathOperation(SKPathVerb.Close, current, figure.StartPoint, default, default));
            }
        }
    }

    public SKPathVerb Next(SKPoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (_index >= _operations.Count)
        {
            return SKPathVerb.Done;
        }

        var operation = _operations[_index++];
        SetPoint(points, 0, operation.P0);
        SetPoint(points, 1, operation.P1);
        SetPoint(points, 2, operation.P2);
        SetPoint(points, 3, operation.P3);
        _conicWeight = 1f;
        return operation.Verb;
    }

    public float ConicWeight() => _conicWeight;

    public void Dispose()
    {
        _operations.Clear();
    }

    private static void SetPoint(SKPoint[] points, int index, Vector2 value)
    {
        if (index < points.Length)
        {
            points[index] = new SKPoint(value.X, value.Y);
        }
    }

    private readonly record struct PathOperation(
        SKPathVerb Verb,
        Vector2 P0,
        Vector2 P1,
        Vector2 P2,
        Vector2 P3);
}

public sealed class SKRegionRectIterator : IDisposable
{
    private readonly SKRectI[] _rects;
    private int _index;

    internal SKRegionRectIterator(IReadOnlyList<SKRectI> rects)
    {
        _rects = new SKRectI[rects.Count];
        for (var i = 0; i < rects.Count; i++)
        {
            _rects[i] = rects[i];
        }
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

    public void Dispose()
    {
    }
}
