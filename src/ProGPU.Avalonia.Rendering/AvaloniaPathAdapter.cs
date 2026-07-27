using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using ProGPU.Scene;
using ProGPU.Vector;
using SkiaSharp;
using VectorFillRule = ProGPU.Vector.FillRule;
using VectorPath = ProGPU.Vector.PathGeometry;
using VectorPathFigure = ProGPU.Vector.PathFigure;

namespace Avalonia.ProGpu;

/// <summary>
/// Typed CPU-side bridge between Avalonia geometry contracts and retained
/// ProGPU paths.
/// </summary>
internal abstract class AvaloniaPathAdapter : IGeometryImpl
{
    private PathMeasure? _measure;
    private RenderCommandGeometryCache? _renderCommandCache;

    public abstract VectorPath Path { get; }

    public Rect Bounds => AvaloniaPathMetrics.CalculateBounds(Path);

    public double ContourLength => Measure.TotalLength;

    protected void InvalidateMeasure()
    {
        _measure = null;
        _renderCommandCache = null;
    }

    public RenderCommandGeometryCache GetRenderCommandGeometryCache() =>
        _renderCommandCache ??= RenderCommandGeometryCache.ForPath(Path);

    public Rect GetRenderBounds(IPen? pen)
    {
        var bounds = Bounds;
        if (pen is null || !double.IsFinite(pen.Thickness) || pen.Thickness <= 0)
        {
            return bounds;
        }

        return bounds.Inflate(pen.Thickness * 0.5);
    }

    public IGeometryImpl GetWidenedGeometry(IPen pen)
    {
        ArgumentNullException.ThrowIfNull(pen);
        if (!double.IsFinite(pen.Thickness) || pen.Thickness <= 0)
        {
            return new ProGpuPathShape(new VectorPath());
        }

        using var source = CreateSkiaPath(Path);
        using var paint = CreateStrokePaint(pen);
        using var widened = paint.GetFillPath(source);
        return widened is null
            ? new ProGpuPathShape(new VectorPath())
            : new ProGpuPathShape(
                widened.Geometry.CreateTransformed(Matrix4x4.Identity));
    }

    public bool FillContains(Point point) =>
        ContainsFill(Path, new Vector2((float)point.X, (float)point.Y));

    public IGeometryImpl? Intersect(IGeometryImpl geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry is AvaloniaPathAdapter other
            ? AvaloniaGeometryFactory.Combine(
                GeometryCombineMode.Intersect,
                this,
                other)
            : null;
    }

    public bool StrokeContains(IPen? pen, Point point)
    {
        if (pen is null || !double.IsFinite(pen.Thickness) || pen.Thickness <= 0)
        {
            return false;
        }

        var maximumDistance = pen.Thickness * 0.5;
        return Measure.DistanceSquaredTo(
                   new Vector2((float)point.X, (float)point.Y)) <=
               maximumDistance * maximumDistance;
    }

    public ITransformedGeometryImpl WithTransform(Matrix transform) =>
        new AvaloniaTransformedPath(this, transform);

    public bool TryGetPointAtDistance(double distance, out Point point)
    {
        if (Measure.TryGetPoint(distance, out var value, out _))
        {
            point = new Point(value.X, value.Y);
            return true;
        }

        point = default;
        return false;
    }

    public bool TryGetPointAndTangentAtDistance(
        double distance,
        out Point point,
        out Point tangent)
    {
        if (Measure.TryGetPoint(distance, out var value, out var direction))
        {
            point = new Point(value.X, value.Y);
            tangent = new Point(direction.X, direction.Y);
            return true;
        }

        point = default;
        tangent = default;
        return false;
    }

    public bool TryGetSegment(
        double startDistance,
        double stopDistance,
        bool startOnBeginFigure,
        [NotNullWhen(true)] out IGeometryImpl? segmentGeometry)
    {
        if (!Measure.TryCreateSegment(
                startDistance,
                stopDistance,
                out var segment))
        {
            segmentGeometry = null;
            return false;
        }

        segmentGeometry = new ProGpuPathShape(segment);
        return true;
    }

    private PathMeasure Measure => _measure ??= PathMeasure.Create(Path);

    private static bool ContainsFill(VectorPath path, Vector2 point)
    {
        if (path.IsCombined)
        {
            if (path.PathA is null || path.PathB is null)
            {
                return false;
            }

            var left = ContainsFill(path.PathA, point);
            var right = ContainsFill(path.PathB, point);
            return path.Op switch
            {
                0 => left && !right,
                1 => left && right,
                2 => left || right,
                3 => left != right,
                4 => right && !left,
                _ => false
            };
        }

        return PathGeometryHitTesting.TryContainsFill(
                   path,
                   point,
                   tolerance: 0,
                   relativeTolerance: false,
                   out var contains) &&
               contains;
    }

    private static SKPath CreateSkiaPath(VectorPath path)
    {
        var result = new SKPath
        {
            FillType = path.FillRule == VectorFillRule.EvenOdd
                ? SKPathFillType.EvenOdd
                : SKPathFillType.Winding
        };

        var snapshot = path.CreateTransformed(Matrix4x4.Identity);
        for (var index = 0; index < snapshot.Figures.Count; index++)
        {
            result.Geometry.Figures.Add(snapshot.Figures[index]);
        }

        return result;
    }

    private static SKPaint CreateStrokePaint(IPen pen)
    {
        var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)pen.Thickness,
            StrokeMiter = (float)pen.MiterLimit,
            StrokeCap = pen.LineCap switch
            {
                Avalonia.Media.PenLineCap.Round => SKStrokeCap.Round,
                Avalonia.Media.PenLineCap.Square => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            },
            StrokeJoin = pen.LineJoin switch
            {
                Avalonia.Media.PenLineJoin.Bevel => SKStrokeJoin.Bevel,
                Avalonia.Media.PenLineJoin.Round => SKStrokeJoin.Round,
                _ => SKStrokeJoin.Miter
            }
        };

        var dashes = pen.DashStyle?.Dashes;
        if (dashes is { Count: > 0 })
        {
            var intervals = new float[dashes.Count];
            for (var index = 0; index < intervals.Length; index++)
            {
                intervals[index] = (float)dashes[index];
            }

            using var effect = SKPathEffect.CreateDash(
                intervals,
                (float)pen.DashStyle!.Offset);
            paint.PathEffect = effect;
        }

        return paint;
    }
}

internal static class AvaloniaPathMetrics
{
    public static Rect CalculateBounds(VectorPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.TryGetBounds(out var minimum, out var maximum))
        {
            return default;
        }

        return new Rect(
            minimum.X,
            minimum.Y,
            Math.Max(0, maximum.X - minimum.X),
            Math.Max(0, maximum.Y - minimum.Y));
    }
}

internal sealed class AvaloniaStreamPath : AvaloniaPathAdapter, IStreamGeometryImpl
{
    public AvaloniaStreamPath()
        : this(new VectorPath { FillRule = VectorFillRule.EvenOdd })
    {
    }

    public AvaloniaStreamPath(VectorPath path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public override VectorPath Path { get; }

    public IStreamGeometryImpl Clone() =>
        new AvaloniaStreamPath(Path.CreateTransformed(Matrix4x4.Identity));

    public IStreamGeometryContextImpl Open()
    {
        Path.Figures.Clear();
        Path.IsCombined = false;
        Path.PathA = null;
        Path.PathB = null;
        InvalidateMeasure();
        return new StreamWriter(this);
    }

    private sealed class StreamWriter : IStreamGeometryContextImpl
    {
        private readonly AvaloniaStreamPath _owner;
        private VectorPathFigure? _figure;
        private bool _disposed;

        public StreamWriter(AvaloniaStreamPath owner)
        {
            _owner = owner;
        }

        public void SetFillRule(Avalonia.Media.FillRule fillRule)
        {
            ThrowIfDisposed();
            if (_owner.Path.Figures.Count != 0)
            {
                throw new InvalidOperationException(
                    "The fill rule must be selected before beginning a figure.");
            }

            _owner.Path.FillRule =
                fillRule == Avalonia.Media.FillRule.EvenOdd
                    ? VectorFillRule.EvenOdd
                    : VectorFillRule.Nonzero;
        }

        public void BeginFigure(Point startPoint, bool isFilled = true)
        {
            ThrowIfDisposed();
            if (_figure is not null)
            {
                throw new InvalidOperationException(
                    "The current figure must be ended before beginning another.");
            }

            _figure = new VectorPathFigure(ToVector(startPoint))
            {
                IsFilled = isFilled
            };
            _owner.Path.Figures.Add(_figure);
            _owner.InvalidateMeasure();
        }

        public void LineTo(Point point, bool isStroked = true)
        {
            Figure.Segments.Add(
                new ProGPU.Vector.LineSegment(
                    ToVector(point),
                    isStroked: isStroked));
            _owner.InvalidateMeasure();
        }

#if AVALONIA11
        public void LineTo(Point point) => LineTo(point, isStroked: true);
#endif

        public void QuadraticBezierTo(
            Point controlPoint,
            Point endPoint,
            bool isStroked = true)
        {
            Figure.Segments.Add(
                new ProGPU.Vector.QuadraticBezierSegment(
                    ToVector(controlPoint),
                    ToVector(endPoint),
                    isStroked: isStroked));
            _owner.InvalidateMeasure();
        }

#if AVALONIA11
        public void QuadraticBezierTo(Point controlPoint, Point endPoint) =>
            QuadraticBezierTo(controlPoint, endPoint, isStroked: true);
#endif

        public void CubicBezierTo(
            Point controlPoint1,
            Point controlPoint2,
            Point endPoint,
            bool isStroked = true)
        {
            Figure.Segments.Add(
                new ProGPU.Vector.CubicBezierSegment(
                    ToVector(controlPoint1),
                    ToVector(controlPoint2),
                    ToVector(endPoint),
                    isStroked: isStroked));
            _owner.InvalidateMeasure();
        }

#if AVALONIA11
        public void CubicBezierTo(
            Point controlPoint1,
            Point controlPoint2,
            Point endPoint) =>
            CubicBezierTo(
                controlPoint1,
                controlPoint2,
                endPoint,
                isStroked: true);
#endif

        public void ArcTo(
            Point point,
            Size size,
            double rotationAngle,
            bool isLargeArc,
            Avalonia.Media.SweepDirection sweepDirection,
            bool isStroked = true)
        {
            Figure.Segments.Add(
                new ProGPU.Vector.ArcSegment(
                    ToVector(point),
                    new Vector2(
                        (float)Math.Abs(size.Width),
                        (float)Math.Abs(size.Height)),
                    (float)(rotationAngle * 180.0 / Math.PI),
                    isLargeArc,
                    sweepDirection == Avalonia.Media.SweepDirection.Clockwise
                        ? ProGPU.Vector.SweepDirection.Clockwise
                        : ProGPU.Vector.SweepDirection.Counterclockwise,
                    isStroked: isStroked));
            _owner.InvalidateMeasure();
        }

#if AVALONIA11
        public void ArcTo(
            Point point,
            Size size,
            double rotationAngle,
            bool isLargeArc,
            Avalonia.Media.SweepDirection sweepDirection) =>
            ArcTo(
                point,
                size,
                rotationAngle,
                isLargeArc,
                sweepDirection,
                isStroked: true);
#endif

        public void EndFigure(bool isClosed)
        {
            ThrowIfDisposed();
            if (_figure is null)
            {
                throw new InvalidOperationException("No figure is active.");
            }

            _figure.IsClosed = isClosed;
            _figure = null;
            _owner.InvalidateMeasure();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _figure = null;
            _disposed = true;
        }

        private VectorPathFigure Figure
        {
            get
            {
                ThrowIfDisposed();
                return _figure ?? throw new InvalidOperationException(
                    "BeginFigure must be called before adding a segment.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private static Vector2 ToVector(Point point) =>
            new((float)point.X, (float)point.Y);
    }
}

internal sealed class PathMeasure
{
    private const int QuadraticSteps = 16;
    private const int CubicSteps = 24;

    private readonly MeasuredLine[] _lines;

    private PathMeasure(MeasuredLine[] lines, double totalLength)
    {
        _lines = lines;
        TotalLength = totalLength;
    }

    public double TotalLength { get; }

    public static PathMeasure Create(VectorPath path)
    {
        if (path.IsCombined)
        {
            return new PathMeasure(Array.Empty<MeasuredLine>(), 0);
        }

        var lines = new List<MeasuredLine>();
        double totalLength = 0;
        for (var figureIndex = 0; figureIndex < path.Figures.Count; figureIndex++)
        {
            var figure = path.Figures[figureIndex];
            var start = figure.StartPoint;
            var current = start;
            for (var segmentIndex = 0;
                 segmentIndex < figure.Segments.Count;
                 segmentIndex++)
            {
                var segment = figure.Segments[segmentIndex];
                switch (segment)
                {
                    case ProGPU.Vector.LineSegment line:
                        AddLine(lines, ref totalLength, current, line.Point, line.IsStroked);
                        current = line.Point;
                        break;

                    case ProGPU.Vector.QuadraticBezierSegment quadratic:
                        var quadraticStart = current;
                        for (var step = 1; step <= QuadraticSteps; step++)
                        {
                            var t = (float)step / QuadraticSteps;
                            var next = EvaluateQuadratic(
                                quadraticStart,
                                quadratic.ControlPoint,
                                quadratic.Point,
                                t);
                            AddLine(
                                lines,
                                ref totalLength,
                                current,
                                next,
                                quadratic.IsStroked);
                            current = next;
                        }

                        break;

                    case ProGPU.Vector.CubicBezierSegment cubic:
                        var cubicStart = current;
                        for (var step = 1; step <= CubicSteps; step++)
                        {
                            var t = (float)step / CubicSteps;
                            var next = EvaluateCubic(
                                cubicStart,
                                cubic.ControlPoint1,
                                cubic.ControlPoint2,
                                cubic.Point,
                                t);
                            AddLine(
                                lines,
                                ref totalLength,
                                current,
                                next,
                                cubic.IsStroked);
                            current = next;
                        }

                        break;

                    case ProGPU.Vector.ArcSegment arc:
                        var points = ArcSegmentGeometry.FlattenArc(current, arc);
                        for (var pointIndex = 1; pointIndex < points.Length; pointIndex++)
                        {
                            AddLine(
                                lines,
                                ref totalLength,
                                current,
                                points[pointIndex],
                                arc.IsStroked);
                            current = points[pointIndex];
                        }

                        break;
                }
            }

            if (figure.IsClosed)
            {
                AddLine(lines, ref totalLength, current, start, isStroked: true);
            }
        }

        return new PathMeasure(lines.ToArray(), totalLength);
    }

    public bool TryGetPoint(
        double distance,
        out Vector2 point,
        out Vector2 tangent)
    {
        point = default;
        tangent = default;
        if (!double.IsFinite(distance) ||
            distance < 0 ||
            distance > TotalLength ||
            _lines.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < _lines.Length; index++)
        {
            var line = _lines[index];
            if (distance <= line.EndDistance || index == _lines.Length - 1)
            {
                var local = line.Length <= double.Epsilon
                    ? 0
                    : (float)((distance - line.StartDistance) / line.Length);
                point = Vector2.Lerp(line.Start, line.End, Math.Clamp(local, 0, 1));
                tangent = line.Direction;
                return true;
            }
        }

        return false;
    }

    public double DistanceSquaredTo(Vector2 point)
    {
        var best = double.PositiveInfinity;
        for (var index = 0; index < _lines.Length; index++)
        {
            var line = _lines[index];
            if (!line.IsStroked)
            {
                continue;
            }

            var delta = line.End - line.Start;
            var denominator = Vector2.Dot(delta, delta);
            var t = denominator <= float.Epsilon
                ? 0
                : Math.Clamp(Vector2.Dot(point - line.Start, delta) / denominator, 0, 1);
            var nearest = line.Start + delta * t;
            best = Math.Min(best, Vector2.DistanceSquared(point, nearest));
        }

        return best;
    }

    public bool TryCreateSegment(
        double startDistance,
        double stopDistance,
        out VectorPath path)
    {
        path = new VectorPath { FillRule = VectorFillRule.Nonzero };
        if (!double.IsFinite(startDistance) ||
            !double.IsFinite(stopDistance) ||
            startDistance < 0 ||
            stopDistance < startDistance ||
            stopDistance > TotalLength ||
            !TryGetPoint(startDistance, out var first, out _))
        {
            return false;
        }

        var figure = new VectorPathFigure(first) { IsFilled = false };
        path.Figures.Add(figure);
        for (var index = 0; index < _lines.Length; index++)
        {
            var line = _lines[index];
            if (line.EndDistance <= startDistance)
            {
                continue;
            }

            if (line.StartDistance >= stopDistance)
            {
                break;
            }

            var endDistance = Math.Min(stopDistance, line.EndDistance);
            if (!TryGetPoint(endDistance, out var end, out _))
            {
                return false;
            }

            figure.Segments.Add(new ProGPU.Vector.LineSegment(end));
            if (endDistance >= stopDistance)
            {
                break;
            }
        }

        return true;
    }

    private static void AddLine(
        List<MeasuredLine> lines,
        ref double totalLength,
        Vector2 start,
        Vector2 end,
        bool isStroked)
    {
        var length = Vector2.Distance(start, end);
        if (!float.IsFinite(length) || length <= float.Epsilon)
        {
            return;
        }

        lines.Add(
            new MeasuredLine(
                start,
                end,
                Vector2.Normalize(end - start),
                length,
                totalLength,
                totalLength + length,
                isStroked));
        totalLength += length;
    }

    private static Vector2 EvaluateQuadratic(
        Vector2 start,
        Vector2 control,
        Vector2 end,
        float t)
    {
        var oneMinusT = 1 - t;
        return oneMinusT * oneMinusT * start +
               2 * oneMinusT * t * control +
               t * t * end;
    }

    private static Vector2 EvaluateCubic(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 end,
        float t)
    {
        var oneMinusT = 1 - t;
        return oneMinusT * oneMinusT * oneMinusT * start +
               3 * oneMinusT * oneMinusT * t * control1 +
               3 * oneMinusT * t * t * control2 +
               t * t * t * end;
    }

    private readonly record struct MeasuredLine(
        Vector2 Start,
        Vector2 End,
        Vector2 Direction,
        double Length,
        double StartDistance,
        double EndDistance,
        bool IsStroked);
}
