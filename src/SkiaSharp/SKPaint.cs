using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;

namespace SkiaSharp;

public enum SKPaintStyle
{
    Fill = 0,
    Stroke = 1,
    StrokeAndFill = 2,
}

public partial class SKPaint : SKObject
{
    private const float HairlineStrokeWidth = 1f;
    private SKShader? _shader;

    public SKPaintStyle Style { get; set; } = SKPaintStyle.Fill;
    public SKColor Color { get; set; } = SKColors.Black;
    public SKColorF ColorF
    {
        get => new(Color.R / 255f, Color.G / 255f, Color.B / 255f, Color.A / 255f);
        set => Color = new SKColor(
            ToColorByte(value.R),
            ToColorByte(value.G),
            ToColorByte(value.B),
            ToColorByte(value.A));
    }
    public bool IsStroke
    {
        get => Style != SKPaintStyle.Fill;
        set => Style = value ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
    }
    public float StrokeWidth { get; set; }
    public float StrokeMiter { get; set; } = 4f;
    public SKStrokeCap StrokeCap { get; set; } = SKStrokeCap.Butt;
    public SKStrokeJoin StrokeJoin { get; set; } = SKStrokeJoin.Miter;
    public SKShader? Shader
    {
        get => _shader;
        set
        {
            if (ReferenceEquals(_shader, value))
            {
                return;
            }

            value?.AddReference();
            _shader?.ReleaseReference();
            _shader = value;
        }
    }
    public SKColorFilter? ColorFilter { get; set; }
    public SKImageFilter? ImageFilter { get; set; }
    public SKPathEffect? PathEffect { get; set; }
    public SKBlendMode BlendMode { get; set; } = SKBlendMode.SrcOver;
    public bool IsAntialias
    {
        get => _isAntialias;
        set
        {
            _isAntialias = value;
            UpdateLegacyFontEdging();
        }
    }
    [Obsolete("Use SKFont.Typeface instead.", true)]
    public SKTypeface Typeface
    {
        get => _legacyFont.Typeface;
        set => _legacyFont.Typeface = value;
    }
    [Obsolete("Use SKFont.Size instead.", true)]
    public float TextSize
    {
        get => _legacyFont.Size;
        set => _legacyFont.Size = value;
    }

    public SKPaint Clone()
    {
        var clone = new SKPaint
        {
            Style = Style,
            Color = Color,
            StrokeWidth = StrokeWidth,
            StrokeMiter = StrokeMiter,
            StrokeCap = StrokeCap,
            StrokeJoin = StrokeJoin,
            Shader = Shader,
            ColorFilter = ColorFilter,
            ImageFilter = ImageFilter,
            PathEffect = PathEffect,
            BlendMode = BlendMode,
            IsAntialias = IsAntialias,
            IsDither = IsDither,
            MaskFilter = MaskFilter,
        };
        clone.CopyLegacyTextStateFrom(this);
        return clone;
    }

    public Brush? ToBrush()
    {
        if (Style == SKPaintStyle.Stroke) return null;

        if (Shader != null)
        {
            return ApplyPaintAlphaToShaderBrush(
                SKShader.ApplyColorFilter(Shader.ToBrush(), ColorFilter),
                Color);
        }

        var color = GetFilteredColor();
        var c = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
        return new SolidColorBrush(c);
    }

    public Pen? ToPen()
    {
        return ToPen(1f);
    }

    public Pen? ToPen(float strokeScale)
    {
        if (Style == SKPaintStyle.Fill) return null;

        var scaledStrokeWidth = ScaleStrokeWidth(StrokeWidth, strokeScale);
        Brush penBrush;
        if (Shader != null)
        {
            penBrush = ApplyPaintAlphaToShaderBrush(
                SKShader.ApplyColorFilter(Shader.ToBrush(), ColorFilter),
                Color);
        }
        else
        {
            var color = GetFilteredColor();
            var c = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            penBrush = new SolidColorBrush(c);
        }
        var (dashArray, dashOffset) = MapDashEffect(PathEffect, scaledStrokeWidth);

        return new Pen(
            penBrush,
            scaledStrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    internal Pen? ToLocalPen(float strokeScale)
    {
        if (Style == SKPaintStyle.Fill) return null;

        var localStrokeWidth = StrokeWidth;
        if (localStrokeWidth == 0f)
        {
            localStrokeWidth = float.IsFinite(strokeScale) && strokeScale > 0f
                ? HairlineStrokeWidth / strokeScale
                : HairlineStrokeWidth;
        }

        Brush penBrush;
        if (Shader != null)
        {
            penBrush = ApplyPaintAlphaToShaderBrush(
                SKShader.ApplyColorFilter(Shader.ToBrush(), ColorFilter),
                Color);
        }
        else
        {
            var color = GetFilteredColor();
            penBrush = new SolidColorBrush(new Vector4(
                color.R / 255.0f,
                color.G / 255.0f,
                color.B / 255.0f,
                color.A / 255.0f));
        }

        var (dashArray, dashOffset) = MapDashEffect(PathEffect, localStrokeWidth);
        return new Pen(
            penBrush,
            localStrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    internal Pen ToPen(Brush brush, float strokeScale)
    {
        var scaledStrokeWidth = ScaleStrokeWidth(StrokeWidth, strokeScale);
        var (dashArray, dashOffset) = MapDashEffect(PathEffect, scaledStrokeWidth);
        return new Pen(
            brush,
            scaledStrokeWidth,
            MapStrokeJoin(StrokeJoin),
            StrokeMiter,
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            MapStrokeCap(StrokeCap),
            dashArray,
            dashOffset);
    }

    public void Reset()
    {
        Style = SKPaintStyle.Fill;
        Color = SKColors.Black;
        StrokeWidth = 0f;
        StrokeMiter = 4f;
        StrokeCap = SKStrokeCap.Butt;
        StrokeJoin = SKStrokeJoin.Miter;
        Shader = null;
        ColorFilter = null;
        ImageFilter = null;
        PathEffect = null;
        BlendMode = SKBlendMode.SrcOver;
        MaskFilter = null;
        ResetLegacyTextState();
    }

    protected override void Dispose(bool disposing)
    {
        Shader = null;
        _legacyFont.Dispose();
        base.Dispose(disposing);
    }

    public bool GetFillPath(SKPath source, SKPath destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Reset();
        if (Style == SKPaintStyle.Fill)
        {
            destination.AddPath(source);
            return true;
        }

        if (Style == SKPaintStyle.StrokeAndFill)
        {
            destination.AddPath(source);
        }

        if (!float.IsFinite(StrokeWidth) || StrokeWidth <= 0f)
        {
            return !destination.IsEmpty;
        }

        var halfWidth = StrokeWidth / 2f;
        foreach (var figure in source.Geometry.Figures)
        {
            if (TryAddOvalStroke(destination, figure, halfWidth))
            {
                continue;
            }

            var points = FlattenFigure(figure);
            RemoveConsecutiveDuplicatePoints(points);
            if (figure.IsClosed &&
                points.Count > 1 &&
                Vector2.DistanceSquared(points[0], points[^1]) <= 0.0000001f)
            {
                points.RemoveAt(points.Count - 1);
            }

            if (!figure.IsClosed && figure.Segments.Count > 0 && IsDegenerateFigure(points))
            {
                if (StrokeCap == SKStrokeCap.Round)
                {
                    destination.AddCircle(figure.StartPoint.X, figure.StartPoint.Y, halfWidth);
                }
                else if (StrokeCap == SKStrokeCap.Square)
                {
                    destination.AddRect(new SKRect(
                        figure.StartPoint.X - halfWidth,
                        figure.StartPoint.Y - halfWidth,
                        figure.StartPoint.X + halfWidth,
                        figure.StartPoint.Y + halfWidth));
                }

                continue;
            }

            if (PathEffect is { Intervals.Length: > 0 } pathEffect)
            {
                AddDashedStrokeSegments(destination, points, figure.IsClosed, halfWidth, pathEffect);
                continue;
            }

            for (var i = 1; i < points.Count; i++)
            {
                AddStrokeSegment(destination, points[i - 1], points[i], halfWidth);
            }

            if (figure.IsClosed && points.Count > 1)
            {
                AddStrokeSegment(destination, points[^1], points[0], halfWidth);
            }

            AddStrokeJoins(destination, points, figure.IsClosed, halfWidth * 2f);
            if (!figure.IsClosed)
            {
                AddStrokeCaps(destination, points, halfWidth * 2f);
            }
        }

        return !destination.IsEmpty;
    }

    internal static void NormalizeStrokeWinding(SKPath source, SKPath stroke)
    {
        var desiredWinding = GetDominantWinding(source.Geometry.Figures);
        var figures = stroke.Geometry.Figures;
        for (var index = 0; index < figures.Count; index++)
        {
            var points = FlattenFigure(figures[index]);
            var winding = GetSignedArea(points);
            if (MathF.Abs(winding) > 0.0001f && MathF.Sign(winding) != desiredWinding)
            {
                figures[index] = ReverseFigure(figures[index]);
            }
        }
    }

    private static int GetDominantWinding(IReadOnlyList<PathFigure> figures)
    {
        var dominantArea = 0f;
        for (var index = 0; index < figures.Count; index++)
        {
            var area = GetSignedArea(FlattenFigure(figures[index]));
            if (MathF.Abs(area) > MathF.Abs(dominantArea))
            {
                dominantArea = area;
            }
        }

        return dominantArea < 0f ? -1 : 1;
    }

    private static float GetSignedArea(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 3)
        {
            return 0f;
        }

        var twiceArea = 0f;
        var previous = points[^1];
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            twiceArea += previous.X * current.Y - current.X * previous.Y;
            previous = current;
        }

        return twiceArea * 0.5f;
    }

    private static PathFigure ReverseFigure(PathFigure source)
    {
        var segments = source.Segments;
        if (segments.Count == 0)
        {
            return source;
        }

        var segmentStarts = new Vector2[segments.Count];
        var current = source.StartPoint;
        for (var index = 0; index < segments.Count; index++)
        {
            segmentStarts[index] = current;
            current = GetSegmentEnd(segments[index]);
        }

        var reversed = new PathFigure(current, source.IsClosed)
        {
            IsFilled = source.IsFilled
        };
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var endpoint = segmentStarts[index];
            switch (segments[index])
            {
                case LineSegment line:
                    reversed.Segments.Add(new LineSegment(
                        endpoint,
                        line.IsSmoothJoin,
                        line.IsStroked));
                    break;
                case QuadraticBezierSegment quadratic:
                    reversed.Segments.Add(new QuadraticBezierSegment(
                        quadratic.ControlPoint,
                        endpoint,
                        quadratic.IsSmoothJoin,
                        quadratic.IsStroked));
                    break;
                case CubicBezierSegment cubic:
                    reversed.Segments.Add(new CubicBezierSegment(
                        cubic.ControlPoint2,
                        cubic.ControlPoint1,
                        endpoint,
                        cubic.IsSmoothJoin,
                        cubic.IsStroked));
                    break;
                case ArcSegment arc:
                    reversed.Segments.Add(new ArcSegment(
                        endpoint,
                        arc.Size,
                        arc.RotationAngle,
                        arc.IsLargeArc,
                        arc.SweepDirection == SweepDirection.Clockwise
                            ? SweepDirection.Counterclockwise
                            : SweepDirection.Clockwise,
                        arc.IsSmoothJoin,
                        arc.IsStroked));
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported stroke path segment '{segments[index].GetType().FullName}'.");
            }
        }

        return reversed;
    }

    private static Vector2 GetSegmentEnd(PathSegment segment) => segment switch
    {
        LineSegment line => line.Point,
        QuadraticBezierSegment quadratic => quadratic.Point,
        CubicBezierSegment cubic => cubic.Point,
        ArcSegment arc => arc.Point,
        _ => throw new NotSupportedException(
            $"Unsupported stroke path segment '{segment.GetType().FullName}'.")
    };

    private static void RemoveConsecutiveDuplicatePoints(List<Vector2> points)
    {
        var writeIndex = 1;
        for (var readIndex = 1; readIndex < points.Count; readIndex++)
        {
            if (Vector2.DistanceSquared(points[writeIndex - 1], points[readIndex]) > 0.0000001f)
            {
                points[writeIndex++] = points[readIndex];
            }
        }

        if (writeIndex < points.Count)
        {
            points.RemoveRange(writeIndex, points.Count - writeIndex);
        }
    }

    private void AddStrokeJoins(
        SKPath destination,
        IReadOnlyList<Vector2> points,
        bool isClosed,
        float strokeWidth)
    {
        if (points.Count < 3)
        {
            return;
        }

        var first = isClosed ? 0 : 1;
        var end = isClosed ? points.Count : points.Count - 1;
        var lineJoin = MapStrokeJoin(StrokeJoin);
        for (var index = first; index < end; index++)
        {
            var previous = points[(index - 1 + points.Count) % points.Count];
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            AddStrokeTriangles(
                destination,
                StrokeJoinGeometry.CreateLineJoin(
                    lineJoin,
                    strokeWidth,
                    StrokeMiter,
                    previous,
                    current,
                    next));
        }
    }

    private void AddStrokeCaps(
        SKPath destination,
        IReadOnlyList<Vector2> points,
        float strokeWidth)
    {
        if (points.Count < 2 || StrokeCap == SKStrokeCap.Butt)
        {
            return;
        }

        var lineCap = MapStrokeCap(StrokeCap);
        if (TryFindDistinctPoint(points, 0, 1, out var firstNeighbor))
        {
            AddStrokeTriangles(
                destination,
                StrokeCapGeometry.CreateLineCap(
                    lineCap,
                    strokeWidth,
                    points[0],
                    firstNeighbor,
                    isStart: true));
        }

        if (TryFindDistinctPoint(points, points.Count - 1, -1, out var lastNeighbor))
        {
            AddStrokeTriangles(
                destination,
                StrokeCapGeometry.CreateLineCap(
                    lineCap,
                    strokeWidth,
                    lastNeighbor,
                    points[^1],
                    isStart: false));
        }
    }

    private static bool TryFindDistinctPoint(
        IReadOnlyList<Vector2> points,
        int originIndex,
        int step,
        out Vector2 point)
    {
        var origin = points[originIndex];
        for (var index = originIndex + step;
             index >= 0 && index < points.Count;
             index += step)
        {
            point = points[index];
            if (Vector2.DistanceSquared(origin, point) > 0.0000001f)
            {
                return true;
            }
        }

        point = default;
        return false;
    }

    private static void AddStrokeTriangles(
        SKPath destination,
        IReadOnlyList<StrokeJoinTriangle> triangles)
    {
        for (var index = 0; index < triangles.Count; index++)
        {
            var triangle = triangles[index];
            destination.MoveTo(triangle.P0.X, triangle.P0.Y);
            destination.LineTo(triangle.P1.X, triangle.P1.Y);
            destination.LineTo(triangle.P2.X, triangle.P2.Y);
            destination.Close();
        }
    }

    private static bool IsDegenerateFigure(IReadOnlyList<Vector2> points)
    {
        var start = points[0];
        for (var i = 1; i < points.Count; i++)
        {
            if (Vector2.DistanceSquared(start, points[i]) > 0.0000001f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAddOvalStroke(SKPath destination, PathFigure figure, float halfWidth)
    {
        if (!figure.IsClosed || figure.Segments.Count != 2
            || figure.Segments[0] is not ArcSegment first
            || figure.Segments[1] is not ArcSegment second
            || !first.IsLargeArc
            || !second.IsLargeArc
            || first.SweepDirection != second.SweepDirection
            || MathF.Abs(first.RotationAngle) > 0.0001f
            || MathF.Abs(second.RotationAngle) > 0.0001f
            || Vector2.DistanceSquared(first.Size, second.Size) > 0.0001f
            || Vector2.DistanceSquared(second.Point, figure.StartPoint) > 0.0001f
            || MathF.Abs(MathF.Abs(first.Point.X - figure.StartPoint.X) - 2f * first.Size.X) > 0.0001f
            || MathF.Abs(first.Point.Y - figure.StartPoint.Y) > 0.0001f)
        {
            return false;
        }

        var center = (figure.StartPoint + first.Point) / 2f;
        var radiusX = MathF.Abs(first.Size.X);
        var radiusY = MathF.Abs(first.Size.Y);
        var direction = first.SweepDirection == SweepDirection.Clockwise
            ? SKPathDirection.Clockwise
            : SKPathDirection.CounterClockwise;
        destination.AddOval(
            new SKRect(
                center.X - radiusX - halfWidth,
                center.Y - radiusY - halfWidth,
                center.X + radiusX + halfWidth,
                center.Y + radiusY + halfWidth),
            direction);

        var innerRadiusX = radiusX - halfWidth;
        var innerRadiusY = radiusY - halfWidth;
        if (innerRadiusX > 0f && innerRadiusY > 0f)
        {
            destination.AddOval(
                new SKRect(
                    center.X - innerRadiusX,
                    center.Y - innerRadiusY,
                    center.X + innerRadiusX,
                    center.Y + innerRadiusY),
                direction == SKPathDirection.Clockwise
                    ? SKPathDirection.CounterClockwise
                    : SKPathDirection.Clockwise);
        }

        return true;
    }

    private void AddDashedStrokeSegments(
        SKPath destination,
        List<Vector2> points,
        bool isClosed,
        float halfWidth,
        SKPathEffect pathEffect)
    {
        if (points.Count < 2)
        {
            return;
        }

        var intervals = pathEffect.Intervals;
        var patternLength = 0f;
        for (var i = 0; i < intervals.Length; i++)
        {
            if (float.IsFinite(intervals[i]) && intervals[i] > 0f)
            {
                patternLength += intervals[i];
            }
        }

        if (patternLength <= 0f)
        {
            return;
        }

        var phase = pathEffect.Phase % patternLength;
        if (phase < 0f)
        {
            phase += patternLength;
        }

        var patternIndex = 0;
        while (phase >= intervals[patternIndex] && intervals[patternIndex] > 0f)
        {
            phase -= intervals[patternIndex];
            patternIndex = (patternIndex + 1) % intervals.Length;
        }

        var remainingInPattern = MathF.Max(0f, intervals[patternIndex] - phase);
        var segmentCount = isClosed ? points.Count : points.Count - 1;
        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var start = points[segmentIndex];
            var end = points[(segmentIndex + 1) % points.Count];
            var delta = end - start;
            var length = delta.Length();
            if (!float.IsFinite(length) || length <= 0.0001f)
            {
                continue;
            }

            var direction = delta / length;
            var distance = 0f;
            while (distance < length - 0.0001f)
            {
                if (remainingInPattern <= 0.0001f)
                {
                    AdvanceDashPattern(intervals, ref patternIndex, ref remainingInPattern);
                }

                var step = MathF.Min(remainingInPattern, length - distance);
                if ((patternIndex & 1) == 0 && step > 0.0001f)
                {
                    var dashStart = start + direction * distance;
                    var dashEnd = start + direction * (distance + step);
                    if (StrokeCap == SKStrokeCap.Square)
                    {
                        dashStart -= direction * halfWidth;
                        dashEnd += direction * halfWidth;
                    }

                    AddStrokeSegment(destination, dashStart, dashEnd, halfWidth);
                    if (StrokeCap == SKStrokeCap.Round)
                    {
                        destination.AddCircle(dashStart.X, dashStart.Y, halfWidth);
                        destination.AddCircle(dashEnd.X, dashEnd.Y, halfWidth);
                    }
                }

                distance += step;
                remainingInPattern -= step;
            }
        }
    }

    private static void AdvanceDashPattern(
        float[] intervals,
        ref int patternIndex,
        ref float remainingInPattern)
    {
        for (var i = 0; i < intervals.Length; i++)
        {
            patternIndex = (patternIndex + 1) % intervals.Length;
            remainingInPattern = intervals[patternIndex];
            if (remainingInPattern > 0.0001f)
            {
                return;
            }
        }
    }

    private static List<Vector2> FlattenFigure(PathFigure figure)
    {
        const int curveSegments = 24;
        var result = new List<Vector2> { figure.StartPoint };
        var current = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    result.Add(line.Point);
                    current = line.Point;
                    break;
                case QuadraticBezierSegment quadratic:
                    for (var i = 1; i <= curveSegments; i++)
                    {
                        result.Add(BezierSegmentGeometry.EvaluateQuadratic(
                            current,
                            quadratic.ControlPoint,
                            quadratic.Point,
                            (float)i / curveSegments));
                    }

                    current = quadratic.Point;
                    break;
                case CubicBezierSegment cubic:
                    for (var i = 1; i <= curveSegments; i++)
                    {
                        result.Add(BezierSegmentGeometry.EvaluateCubic(
                            current,
                            cubic.ControlPoint1,
                            cubic.ControlPoint2,
                            cubic.Point,
                            (float)i / curveSegments));
                    }

                    current = cubic.Point;
                    break;
                case ArcSegment arc:
                    var arcPoints = ArcSegmentGeometry.FlattenArc(current, arc, MathF.PI / 24f);
                    for (var i = 1; i < arcPoints.Length; i++)
                    {
                        result.Add(arcPoints[i]);
                    }

                    current = arc.Point;
                    break;
            }
        }

        return result;
    }

    private static void AddStrokeSegment(SKPath path, Vector2 start, Vector2 end, float halfWidth)
    {
        var direction = end - start;
        if (direction.LengthSquared() <= 0.0000001f)
        {
            return;
        }

        direction = Vector2.Normalize(direction);
        var normal = new Vector2(-direction.Y, direction.X) * halfWidth;
        path.MoveTo(start.X + normal.X, start.Y + normal.Y);
        path.LineTo(end.X + normal.X, end.Y + normal.Y);
        path.LineTo(end.X - normal.X, end.Y - normal.Y);
        path.LineTo(start.X - normal.X, start.Y - normal.Y);
        path.Close();
    }

    private static byte ToColorByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
    }

    private SKColor GetFilteredColor()
    {
        return ColorFilter?.Apply(Color) ?? Color;
    }

    private static Brush ApplyPaintAlphaToShaderBrush(Brush brush, SKColor paintColor)
    {
        brush.Opacity *= paintColor.A / 255.0f;
        return brush;
    }

    private static float ScaleStrokeWidth(float strokeWidth, float strokeScale)
    {
        if (strokeWidth == 0f)
        {
            return HairlineStrokeWidth;
        }

        if (!float.IsFinite(strokeScale) || strokeScale <= 0f)
        {
            return strokeWidth;
        }

        return strokeWidth * strokeScale;
    }

    private static PenLineCap MapStrokeCap(SKStrokeCap cap)
    {
        return cap switch
        {
            SKStrokeCap.Round => PenLineCap.Round,
            SKStrokeCap.Square => PenLineCap.Square,
            _ => PenLineCap.Flat
        };
    }

    private static PenLineJoin MapStrokeJoin(SKStrokeJoin join)
    {
        return join switch
        {
            SKStrokeJoin.Round => PenLineJoin.Round,
            SKStrokeJoin.Bevel => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter
        };
    }

    private static (double[]? DashArray, double DashOffset) MapDashEffect(SKPathEffect? pathEffect, float strokeWidth)
    {
        if (pathEffect == null)
        {
            return (null, 0.0);
        }

        if (!float.IsFinite(strokeWidth) || strokeWidth <= 0f)
        {
            throw new NotSupportedException("Dash path effects require a positive finite stroke width.");
        }

        if (pathEffect.Intervals.Length == 0 || (pathEffect.Intervals.Length % 2) != 0)
        {
            throw new NotSupportedException("Dash path effects require an even number of intervals.");
        }

        var dashArray = new double[pathEffect.Intervals.Length];
        for (var i = 0; i < pathEffect.Intervals.Length; i++)
        {
            var interval = pathEffect.Intervals[i];
            if (!float.IsFinite(interval) || interval < 0f)
            {
                throw new NotSupportedException("Dash path effect intervals must be finite and non-negative.");
            }

            dashArray[i] = interval / strokeWidth;
        }

        if (!float.IsFinite(pathEffect.Phase))
        {
            throw new NotSupportedException("Dash path effect phase must be finite.");
        }

        return (dashArray, pathEffect.Phase / strokeWidth);
    }
}

public class SKShader : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    private readonly Func<Brush>? _brushCreator;
    private readonly PictureShaderData? _picture;
    private readonly ImageShaderData? _image;
    private readonly ComposedShaderData? _composed;
    private readonly PerlinNoiseShaderData? _perlinNoise;
    private SKColorFilter? _colorFilter;
    private int _referenceCount = 1;
    private bool _disposed;

    private SKShader(Func<Brush> brushCreator)
    {
        _brushCreator = brushCreator;
    }

    private SKShader(PictureShaderData picture)
    {
        _picture = picture;
    }

    private SKShader(ImageShaderData image)
    {
        _image = image;
    }

    private SKShader(ComposedShaderData composed)
    {
        _composed = composed;
    }

    private SKShader(PerlinNoiseShaderData perlinNoise)
    {
        _perlinNoise = perlinNoise;
    }

    public Brush ToBrush()
    {
        if (_brushCreator != null)
        {
            return ApplyColorFilter(_brushCreator(), _colorFilter);
        }

        if (_perlinNoise is { } perlinNoise)
        {
            return ApplyColorFilter(
                new PerlinNoiseBrush(
                    perlinNoise.IsTurbulence,
                    new Vector2(perlinNoise.BaseFrequencyX, perlinNoise.BaseFrequencyY),
                    perlinNoise.NumOctaves,
                    perlinNoise.Seed,
                    new Vector2(perlinNoise.TileSize.X, perlinNoise.TileSize.Y)),
                _colorFilter);
        }

        if (_picture != null || _image != null || _composed != null)
        {
            throw new NotSupportedException("Picture shaders are rendered by SKCanvas and cannot be converted to a vector brush.");
        }

        throw new NotSupportedException("The shader cannot be converted to a vector brush.");
    }

    internal PictureShaderData? Picture => _picture;
    internal ImageShaderData? Image => _image;
    internal ComposedShaderData? Composed => _composed;
    internal SKColorFilter? ColorFilter => _colorFilter;
    internal PerlinNoiseShaderData? PerlinNoise => _perlinNoise;

    internal static SKShader CreatePicture(
        GpuPicture picture,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix,
        SKRect tileRect)
    {
        return new SKShader(new PictureShaderData(picture, tileModeX, tileModeY, localMatrix, tileRect));
    }

    internal static SKShader CreateImage(
        SKImage image,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix)
    {
        return CreateImage(image, tileModeX, tileModeY, localMatrix, SKSamplingOptions.Default);
    }

    internal static SKShader CreateImage(
        SKImage image,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix,
        SKSamplingOptions sampling)
    {
        return new SKShader(new ImageShaderData(image, tileModeX, tileModeY, localMatrix, sampling));
    }

    internal void AddReference()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SKShader));
        }

        checked
        {
            _referenceCount++;
        }
    }

    internal void ReleaseReference()
    {
        if (_referenceCount <= 0)
        {
            return;
        }

        _referenceCount--;
        if (_referenceCount == 0)
        {
            _picture?.Dispose();
            _image?.Dispose();
            _composed?.Dispose();
        }
    }

    public static SKShader CreateColor(SKColor color)
    {
        return new SKShader(() =>
        {
            var c = new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, color.A / 255.0f);
            return new SolidColorBrush(c);
        });
    }

    public static SKShader CreateColor(SKColorF color, SKColorSpace colorSpace)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        return new SKShader(() => new SolidColorBrush(new Vector4(
            Math.Clamp(color.R, 0f, 1f),
            Math.Clamp(color.G, 0f, 1f),
            Math.Clamp(color.B, 0f, 1f),
            Math.Clamp(color.A, 0f, 1f))));
    }

    public static SKShader CreateColor(SKColor color, SKColorSpace colorSpace)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        return CreateColor(color);
    }

    public static SKShader CreatePicture(
        SKPicture source,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY,
        SKMatrix localMatrix,
        SKRect tileRect)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ToShader(tileModeX, tileModeY, localMatrix, tileRect);
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() =>
        {
            var stops = CreateGradientStops(colors, colorPos);
            return new LinearGradientBrush(new Vector2(start.X, start.Y), new Vector2(end.X, end.Y), stops)
            {
                SpreadMethod = spreadMethod
            };
        });
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColorF[] colors,
        SKColorSpace colorSpace,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        return CreateLinearGradient(start, end, colors, colorSpace, colorPos, mode, SKMatrix.Identity);
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColorF[] colors,
        SKColorSpace colorSpace,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() => new LinearGradientBrush(
            new Vector2(start.X, start.Y),
            new Vector2(end.X, end.Y),
            CreateGradientStops(colors, colorPos))
        {
            SpreadMethod = spreadMethod,
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix),
            ColorInterpolationMode = colorSpace.IsLinear
                ? GradientColorInterpolationMode.ScRgbLinearInterpolation
                : GradientColorInterpolationMode.SRgbLinearInterpolation,
        });
    }

    public static SKShader CreateLinearGradient(
        SKPoint start,
        SKPoint end,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() => new LinearGradientBrush(
            new Vector2(start.X, start.Y),
            new Vector2(end.X, end.Y),
            CreateGradientStops(colors, colorPos))
        {
            SpreadMethod = spreadMethod,
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix)
        });
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() =>
        {
            var stops = CreateGradientStops(colors, colorPos);
            return new RadialGradientBrush(new Vector2(center.X, center.Y), radius, stops)
            {
                SpreadMethod = spreadMethod
            };
        });
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColorF[] colors,
        SKColorSpace colorSpace,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        return CreateRadialGradient(center, radius, colors, colorSpace, colorPos, mode, SKMatrix.Identity);
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColorF[] colors,
        SKColorSpace colorSpace,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() => new RadialGradientBrush(
            new Vector2(center.X, center.Y),
            radius,
            CreateGradientStops(colors, colorPos))
        {
            SpreadMethod = spreadMethod,
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix),
            ColorInterpolationMode = colorSpace.IsLinear
                ? GradientColorInterpolationMode.ScRgbLinearInterpolation
                : GradientColorInterpolationMode.SRgbLinearInterpolation,
        });
    }

    public static SKShader CreateRadialGradient(
        SKPoint center,
        float radius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() => new RadialGradientBrush(
            new Vector2(center.X, center.Y),
            radius,
            CreateGradientStops(colors, colorPos))
        {
            SpreadMethod = spreadMethod,
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix)
        });
    }

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() =>
        {
            var stops = CreateGradientStops(colors, colorPos);

            return new TwoPointConicalGradientBrush(
                new Vector2(start.X, start.Y),
                startRadius,
                new Vector2(end.X, end.Y),
                endRadius,
                stops)
            {
                SpreadMethod = spreadMethod
            };
        });
    }

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColorF[] colors,
        SKColorSpace colorSpace,
        float[]? colorPos,
        SKShaderTileMode mode)
    {
        return CreateTwoPointConicalGradient(
            start,
            startRadius,
            end,
            endRadius,
            colors,
            colorSpace,
            colorPos,
            mode,
            SKMatrix.Identity);
    }

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColorF[] colors,
        SKColorSpace colorSpace,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() => new TwoPointConicalGradientBrush(
            new Vector2(start.X, start.Y),
            startRadius,
            new Vector2(end.X, end.Y),
            endRadius,
            CreateGradientStops(colors, colorPos))
        {
            SpreadMethod = spreadMethod,
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix),
            ColorInterpolationMode = colorSpace.IsLinear
                ? GradientColorInterpolationMode.ScRgbLinearInterpolation
                : GradientColorInterpolationMode.SRgbLinearInterpolation,
        });
    }

    public static SKShader CreatePerlinNoiseFractalNoise(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed,
        SKPointI tileSize) =>
        new(new PerlinNoiseShaderData(
            false,
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            tileSize));

    public static SKShader CreatePerlinNoiseTurbulence(
        float baseFrequencyX,
        float baseFrequencyY,
        int numOctaves,
        float seed,
        SKPointI tileSize) =>
        new(new PerlinNoiseShaderData(
            true,
            baseFrequencyX,
            baseFrequencyY,
            numOctaves,
            seed,
            tileSize));

    public static SKShader CreateTwoPointConicalGradient(
        SKPoint start,
        float startRadius,
        SKPoint end,
        float endRadius,
        SKColor[] colors,
        float[]? colorPos,
        SKShaderTileMode mode,
        SKMatrix localMatrix)
    {
        var spreadMethod = MapTileMode(mode);
        return new SKShader(() => new TwoPointConicalGradientBrush(
            new Vector2(start.X, start.Y),
            startRadius,
            new Vector2(end.X, end.Y),
            endRadius,
            CreateGradientStops(colors, colorPos))
        {
            SpreadMethod = spreadMethod,
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix)
        });
    }

    public static SKShader CreateSweepGradient(
        SKPoint center,
        SKColor[] colors,
        float[]? colorPos,
        SKMatrix localMatrix)
    {
        return new SKShader(() => new SweepGradientBrush(
            new Vector2(center.X, center.Y),
            CreateGradientStops(colors, colorPos))
        {
            CoordinateTransform = GetShaderCoordinateTransform(localMatrix)
        });
    }

    public static SKShader CreateBitmap(
        SKBitmap bitmap,
        SKShaderTileMode tileModeX,
        SKShaderTileMode tileModeY)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return CreateImage(SKImage.FromBitmap(bitmap), tileModeX, tileModeY, SKMatrix.Identity);
    }

    public static SKShader CreateCompose(SKShader destination, SKShader source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        return new SKShader(new ComposedShaderData(destination, source));
    }

    public SKShader WithColorFilter(SKColorFilter colorFilter)
    {
        _colorFilter = colorFilter ?? throw new ArgumentNullException(nameof(colorFilter));
        return this;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseReference();
    }

    ~SKShader()
    {
        if (!_disposed)
        {
            _disposed = true;
            ReleaseReference();
        }
    }

    internal sealed class PictureShaderData : IDisposable
    {
        public PictureShaderData(
            GpuPicture picture,
            SKShaderTileMode tileModeX,
            SKShaderTileMode tileModeY,
            SKMatrix localMatrix,
            SKRect tileRect)
        {
            Picture = picture;
            TileModeX = tileModeX;
            TileModeY = tileModeY;
            LocalMatrix = localMatrix;
            TileRect = tileRect;
        }

        public GpuPicture Picture { get; }
        public SKShaderTileMode TileModeX { get; }
        public SKShaderTileMode TileModeY { get; }
        public SKMatrix LocalMatrix { get; }
        public SKRect TileRect { get; }

        public void Dispose()
        {
            Picture.Dispose();
        }
    }

    internal sealed class ImageShaderData : IDisposable
    {
        public ImageShaderData(
            SKImage image,
            SKShaderTileMode tileModeX,
            SKShaderTileMode tileModeY,
            SKMatrix localMatrix)
            : this(image, tileModeX, tileModeY, localMatrix, SKSamplingOptions.Default)
        {
        }

        public ImageShaderData(
            SKImage image,
            SKShaderTileMode tileModeX,
            SKShaderTileMode tileModeY,
            SKMatrix localMatrix,
            SKSamplingOptions sampling)
        {
            Image = image;
            TileModeX = tileModeX;
            TileModeY = tileModeY;
            LocalMatrix = localMatrix;
            Sampling = sampling;
        }

        public SKImage Image { get; }
        public SKShaderTileMode TileModeX { get; }
        public SKShaderTileMode TileModeY { get; }
        public SKMatrix LocalMatrix { get; }
        public SKSamplingOptions Sampling { get; }
        public SKRect TileRect => new(0f, 0f, Image.Width, Image.Height);

        public void Dispose()
        {
            Image.Dispose();
        }
    }

    internal sealed class ComposedShaderData : IDisposable
    {
        public ComposedShaderData(SKShader destination, SKShader source)
        {
            Destination = destination;
            Source = source;
            Destination.AddReference();
            Source.AddReference();
        }

        public SKShader Destination { get; }
        public SKShader Source { get; }

        public void Dispose()
        {
            Destination.ReleaseReference();
            Source.ReleaseReference();
        }
    }

    internal sealed record PerlinNoiseShaderData(
        bool IsTurbulence,
        float BaseFrequencyX,
        float BaseFrequencyY,
        int NumOctaves,
        float Seed,
        SKPointI TileSize);

    internal static Brush ApplyColorFilter(Brush brush, SKColorFilter? colorFilter)
    {
        if (colorFilter == null)
        {
            return brush;
        }

        switch (brush)
        {
            case SolidColorBrush solid:
                solid.Color = ApplyColorFilter(solid.Color, colorFilter);
                break;
            case LinearGradientBrush linear:
                ApplyColorFilter(linear.Stops, colorFilter);
                break;
            case RadialGradientBrush radial:
                ApplyColorFilter(radial.Stops, colorFilter);
                break;
            case TwoPointConicalGradientBrush conical:
                ApplyColorFilter(conical.Stops, colorFilter);
                break;
            case SweepGradientBrush sweep:
                ApplyColorFilter(sweep.Stops, colorFilter);
                break;
        }

        return brush;
    }

    private static void ApplyColorFilter(GradientStop[] stops, SKColorFilter colorFilter)
    {
        for (var i = 0; i < stops.Length; i++)
        {
            var stop = stops[i];
            stop.Color = ApplyColorFilter(stop.Color, colorFilter);
            stops[i] = stop;
        }
    }

    private static Vector4 ApplyColorFilter(Vector4 color, SKColorFilter colorFilter)
    {
        var filtered = colorFilter.Apply(new SKColor(
            ToByte(color.X),
            ToByte(color.Y),
            ToByte(color.Z),
            ToByte(color.W)));
        return new Vector4(
            filtered.R / 255f,
            filtered.G / 255f,
            filtered.B / 255f,
            filtered.A / 255f);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);
    }

    private static GradientStop[] CreateGradientStops(SKColor[] colors, float[]? colorPos)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colorPos != null && colorPos.Length < colors.Length)
        {
            throw new ArgumentException("Color position array must have at least as many entries as the color array.", nameof(colorPos));
        }

        var stops = new GradientStop[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            var c = new Vector4(colors[i].R / 255.0f, colors[i].G / 255.0f, colors[i].B / 255.0f, colors[i].A / 255.0f);
            float offset = colorPos != null
                ? colorPos[i]
                : colors.Length <= 1
                    ? 0f
                    : (float)i / (colors.Length - 1);
            stops[i] = new GradientStop(c, offset);
        }

        return stops;
    }

    private static GradientStop[] CreateGradientStops(SKColorF[] colors, float[]? colorPos)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colorPos != null && colorPos.Length < colors.Length)
        {
            throw new ArgumentException("Color position array must have at least as many entries as the color array.", nameof(colorPos));
        }

        var stops = new GradientStop[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var offset = colorPos != null
                ? colorPos[i]
                : colors.Length <= 1
                    ? 0f
                    : (float)i / (colors.Length - 1);
            stops[i] = new GradientStop(new Vector4(
                Math.Clamp(colors[i].R, 0f, 1f),
                Math.Clamp(colors[i].G, 0f, 1f),
                Math.Clamp(colors[i].B, 0f, 1f),
                Math.Clamp(colors[i].A, 0f, 1f)), offset);
        }

        return stops;
    }

    private static GradientSpreadMethod MapTileMode(SKShaderTileMode mode)
    {
        return mode switch
        {
            SKShaderTileMode.Clamp => GradientSpreadMethod.Pad,
            SKShaderTileMode.Repeat => GradientSpreadMethod.Repeat,
            SKShaderTileMode.Mirror => GradientSpreadMethod.Reflect,
            SKShaderTileMode.Decal => GradientSpreadMethod.Decal,
            _ => GradientSpreadMethod.Pad
        };
    }

    private static Matrix4x4 GetShaderCoordinateTransform(SKMatrix localMatrix)
    {
        return Matrix4x4.Invert(localMatrix.ToMatrix4x4(), out var inverse)
            ? inverse
            : Matrix4x4.Identity;
    }
}

public class SKColorFilter : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public SKColor Color { get; }
    public SKBlendMode Mode { get; }
    private readonly byte[]? _alphaTable;
    private readonly byte[]? _redTable;
    private readonly byte[]? _greenTable;
    private readonly byte[]? _blueTable;
    private readonly float[]? _colorMatrix;
    private readonly bool _lumaColor;

    private SKColorFilter(SKColor color, SKBlendMode mode)
    {
        Color = color;
        Mode = mode;
    }

    private SKColorFilter(byte[] alpha, byte[] red, byte[] green, byte[] blue)
    {
        _alphaTable = (byte[])alpha.Clone();
        _redTable = (byte[])red.Clone();
        _greenTable = (byte[])green.Clone();
        _blueTable = (byte[])blue.Clone();
    }

    private SKColorFilter(float[] colorMatrix)
    {
        _colorMatrix = (float[])colorMatrix.Clone();
    }

    private SKColorFilter(bool lumaColor)
    {
        _lumaColor = lumaColor;
    }

    internal float[]? ColorMatrix => _colorMatrix;
    internal bool IsLumaColor => _lumaColor;

    internal bool TryGetColorTables(
        out ReadOnlyMemory<byte> alpha,
        out ReadOnlyMemory<byte> red,
        out ReadOnlyMemory<byte> green,
        out ReadOnlyMemory<byte> blue)
    {
        if (_alphaTable != null && _redTable != null && _greenTable != null && _blueTable != null)
        {
            alpha = _alphaTable;
            red = _redTable;
            green = _greenTable;
            blue = _blueTable;
            return true;
        }

        alpha = default;
        red = default;
        green = default;
        blue = default;
        return false;
    }

    internal bool TryGetImageEffectColorMatrix(out ImageEffectColorMatrix matrix)
    {
        if (_colorMatrix != null)
        {
            matrix = new ImageEffectColorMatrix(
                new Vector4(_colorMatrix[0], _colorMatrix[1], _colorMatrix[2], _colorMatrix[3]),
                new Vector4(_colorMatrix[5], _colorMatrix[6], _colorMatrix[7], _colorMatrix[8]),
                new Vector4(_colorMatrix[10], _colorMatrix[11], _colorMatrix[12], _colorMatrix[13]),
                new Vector4(_colorMatrix[15], _colorMatrix[16], _colorMatrix[17], _colorMatrix[18]),
                new Vector4(_colorMatrix[4], _colorMatrix[9], _colorMatrix[14], _colorMatrix[19]));
            return true;
        }

        if (_lumaColor)
        {
            matrix = new ImageEffectColorMatrix(
                Vector4.Zero,
                Vector4.Zero,
                Vector4.Zero,
                new Vector4(0.2126f, 0.7152f, 0.0722f, 0f),
                new Vector4(1f, 1f, 1f, 0f));
            return true;
        }

        matrix = default;
        return false;
    }

    public static SKColorFilter CreateBlendMode(SKColor color, SKBlendMode mode)
    {
        return new SKColorFilter(color, mode);
    }

    public static SKColorFilter CreateTable(byte[] alpha, byte[] red, byte[] green, byte[] blue)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        if (alpha.Length < 256 || red.Length < 256 || green.Length < 256 || blue.Length < 256)
        {
            throw new ArgumentException("Color filter tables must contain 256 entries.");
        }

        return new SKColorFilter(alpha, red, green, blue);
    }

    public static SKColorFilter CreateColorMatrix(float[] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length != 20)
        {
            throw new ArgumentException("Color matrices must contain 20 values.", nameof(matrix));
        }

        return new SKColorFilter(matrix);
    }

    public static SKColorFilter CreateLumaColor() => new(lumaColor: true);

    public SKColor Apply(SKColor destination)
    {
        if (_colorMatrix != null)
        {
            var red = destination.R / 255f;
            var green = destination.G / 255f;
            var blue = destination.B / 255f;
            var alpha = destination.A / 255f;
            return new SKColor(
                ToByte(red * _colorMatrix[0] + green * _colorMatrix[1] + blue * _colorMatrix[2] + alpha * _colorMatrix[3] + _colorMatrix[4]),
                ToByte(red * _colorMatrix[5] + green * _colorMatrix[6] + blue * _colorMatrix[7] + alpha * _colorMatrix[8] + _colorMatrix[9]),
                ToByte(red * _colorMatrix[10] + green * _colorMatrix[11] + blue * _colorMatrix[12] + alpha * _colorMatrix[13] + _colorMatrix[14]),
                ToByte(red * _colorMatrix[15] + green * _colorMatrix[16] + blue * _colorMatrix[17] + alpha * _colorMatrix[18] + _colorMatrix[19]));
        }

        if (_lumaColor)
        {
            var luma = ToByte(
                destination.R / 255f * 0.2126f +
                destination.G / 255f * 0.7152f +
                destination.B / 255f * 0.0722f);
            return new SKColor(255, 255, 255, luma);
        }

        if (_alphaTable != null && _redTable != null && _greenTable != null && _blueTable != null)
        {
            return new SKColor(
                _redTable[destination.R],
                _greenTable[destination.G],
                _blueTable[destination.B],
                _alphaTable[destination.A]);
        }

        var source = ToPremultiplied(Color);
        var dest = ToPremultiplied(destination);
        var result = Mode switch
        {
            SKBlendMode.Clear => Vector4.Zero,
            SKBlendMode.Src => source,
            SKBlendMode.Dst => dest,
            SKBlendMode.SrcOver => SourceOver(source, dest),
            SKBlendMode.DstOver => SourceOver(dest, source),
            SKBlendMode.SrcIn => source * dest.W,
            SKBlendMode.DstIn => dest * source.W,
            SKBlendMode.SrcOut => source * (1f - dest.W),
            SKBlendMode.DstOut => dest * (1f - source.W),
            SKBlendMode.SrcATop => (source * dest.W) + (dest * (1f - source.W)),
            SKBlendMode.DstATop => (dest * source.W) + (source * (1f - dest.W)),
            SKBlendMode.Xor => (source * (1f - dest.W)) + (dest * (1f - source.W)),
            SKBlendMode.Plus => Vector4.Min(source + dest, Vector4.One),
            SKBlendMode.Modulate => source * dest,
            SKBlendMode.Multiply => BlendSeparable(source, dest, static (s, d) => s * d),
            SKBlendMode.Screen => BlendSeparable(source, dest, static (s, d) => s + d - (s * d)),
            SKBlendMode.Overlay => BlendSeparable(source, dest, Overlay),
            SKBlendMode.Darken => BlendSeparable(source, dest, static (s, d) => MathF.Min(s, d)),
            SKBlendMode.Lighten => BlendSeparable(source, dest, static (s, d) => MathF.Max(s, d)),
            SKBlendMode.ColorDodge => BlendSeparable(source, dest, ColorDodge),
            SKBlendMode.ColorBurn => BlendSeparable(source, dest, ColorBurn),
            SKBlendMode.HardLight => BlendSeparable(source, dest, HardLight),
            SKBlendMode.SoftLight => BlendSeparable(source, dest, SoftLight),
            SKBlendMode.Difference => BlendSeparable(source, dest, static (s, d) => MathF.Abs(d - s)),
            SKBlendMode.Exclusion => BlendSeparable(source, dest, static (s, d) => s + d - (2f * s * d)),
            SKBlendMode.Hue => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(SetSaturation(s, Saturation(d)), Luminosity(d))),
            SKBlendMode.Saturation => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(SetSaturation(d, Saturation(s)), Luminosity(d))),
            SKBlendMode.Color => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(s, Luminosity(d))),
            SKBlendMode.Luminosity => BlendNonSeparable(
                source,
                dest,
                static (s, d) => SetLuminosity(d, Luminosity(s))),
            _ => throw new NotSupportedException($"SKColorFilter blend mode '{Mode}' is not supported.")
        };

        return FromPremultiplied(result);
    }

    public void Dispose() { }

    private static Vector4 ToPremultiplied(SKColor color)
    {
        var alpha = color.A / 255f;
        return new Vector4(
            color.R / 255f * alpha,
            color.G / 255f * alpha,
            color.B / 255f * alpha,
            alpha);
    }

    private static SKColor FromPremultiplied(Vector4 color)
    {
        var alpha = Clamp01(color.W);
        if (alpha <= 0f)
        {
            return SKColor.Empty;
        }

        return new SKColor(
            ToByte(color.X / alpha),
            ToByte(color.Y / alpha),
            ToByte(color.Z / alpha),
            ToByte(alpha));
    }

    private static Vector4 SourceOver(Vector4 source, Vector4 dest)
    {
        return source + (dest * (1f - source.W));
    }

    private static Vector4 BlendSeparable(Vector4 source, Vector4 dest, Func<float, float, float> blend)
    {
        var sourceAlpha = source.W;
        var destAlpha = dest.W;
        var alpha = sourceAlpha + destAlpha - (sourceAlpha * destAlpha);
        var rgb = new Vector3(
            BlendComponent(source.X, dest.X, sourceAlpha, destAlpha, blend),
            BlendComponent(source.Y, dest.Y, sourceAlpha, destAlpha, blend),
            BlendComponent(source.Z, dest.Z, sourceAlpha, destAlpha, blend));
        return new Vector4(rgb, alpha);
    }

    private static float BlendComponent(float source, float dest, float sourceAlpha, float destAlpha, Func<float, float, float> blend)
    {
        var straightSource = sourceAlpha > 0f ? source / sourceAlpha : 0f;
        var straightDest = destAlpha > 0f ? dest / destAlpha : 0f;
        return (source * (1f - destAlpha))
            + (dest * (1f - sourceAlpha))
            + (sourceAlpha * destAlpha * blend(straightSource, straightDest));
    }

    private static Vector4 BlendNonSeparable(
        Vector4 source,
        Vector4 dest,
        Func<Vector3, Vector3, Vector3> blend)
    {
        var sourceAlpha = source.W;
        var destAlpha = dest.W;
        var alpha = sourceAlpha + destAlpha - (sourceAlpha * destAlpha);
        var straightSource = sourceAlpha > 0f
            ? new Vector3(source.X, source.Y, source.Z) / sourceAlpha
            : Vector3.Zero;
        var straightDest = destAlpha > 0f
            ? new Vector3(dest.X, dest.Y, dest.Z) / destAlpha
            : Vector3.Zero;
        var rgb = (new Vector3(source.X, source.Y, source.Z) * (1f - destAlpha))
            + (new Vector3(dest.X, dest.Y, dest.Z) * (1f - sourceAlpha))
            + (sourceAlpha * destAlpha * blend(straightSource, straightDest));
        return new Vector4(rgb, alpha);
    }

    private static float Overlay(float source, float dest) =>
        dest <= 0.5f
            ? 2f * source * dest
            : 1f - (2f * (1f - source) * (1f - dest));

    private static float ColorDodge(float source, float dest)
    {
        if (dest <= 0f)
        {
            return 0f;
        }

        return source >= 1f
            ? 1f
            : MathF.Min(1f, dest / (1f - source));
    }

    private static float ColorBurn(float source, float dest)
    {
        if (dest >= 1f)
        {
            return 1f;
        }

        return source <= 0f
            ? 0f
            : 1f - MathF.Min(1f, (1f - dest) / source);
    }

    private static float HardLight(float source, float dest) =>
        source <= 0.5f
            ? 2f * source * dest
            : 1f - (2f * (1f - source) * (1f - dest));

    private static float SoftLight(float source, float dest)
    {
        if (source <= 0.5f)
        {
            return dest - ((1f - (2f * source)) * dest * (1f - dest));
        }

        var softenedDest = dest <= 0.25f
            ? (((16f * dest) - 12f) * dest + 4f) * dest
            : MathF.Sqrt(dest);
        return dest + (((2f * source) - 1f) * (softenedDest - dest));
    }

    private static float Luminosity(Vector3 color) =>
        (0.3f * color.X) + (0.59f * color.Y) + (0.11f * color.Z);

    private static float Saturation(Vector3 color) =>
        MathF.Max(color.X, MathF.Max(color.Y, color.Z)) -
        MathF.Min(color.X, MathF.Min(color.Y, color.Z));

    private static Vector3 SetSaturation(Vector3 color, float saturation)
    {
        var minimum = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var maximum = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        if (maximum <= minimum)
        {
            return Vector3.Zero;
        }

        return (color - new Vector3(minimum)) * (saturation / (maximum - minimum));
    }

    private static Vector3 SetLuminosity(Vector3 color, float luminosity)
    {
        var delta = luminosity - Luminosity(color);
        return ClipColor(color + new Vector3(delta));
    }

    private static Vector3 ClipColor(Vector3 color)
    {
        var luminosity = Luminosity(color);
        var minimum = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        if (minimum < 0f)
        {
            color = new Vector3(luminosity) +
                ((color - new Vector3(luminosity)) * (luminosity / (luminosity - minimum)));
        }

        var maximum = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        if (maximum > 1f)
        {
            color = new Vector3(luminosity) +
                ((color - new Vector3(luminosity)) * ((1f - luminosity) / (maximum - luminosity)));
        }

        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(Clamp01(value) * 255f), 0f, 255f);
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }
}

public class SKImageFilter : IDisposable
{
    internal enum FilterKind
    {
        Blur,
        DropShadow,
        Arithmetic,
        BlendMode,
        ColorFilter,
        Dilate,
        DisplacementMap,
        DistantLitDiffuse,
        DistantLitSpecular,
        Erode,
        Image,
        MatrixConvolution,
        Merge,
        Offset,
        Shader,
        Picture,
        PointLitDiffuse,
        PointLitSpecular,
        SpotLitDiffuse,
        SpotLitSpecular,
        Tile,
    }

    private SKImageFilter(FilterKind kind, object? parameters, SKImageFilter? input, SKRect? cropRect)
    {
        Kind = kind;
        Parameters = parameters;
        Input = input;
        CropRect = cropRect;
    }

    public IntPtr Handle { get; } = SKObjectHandle.Create();
    internal FilterKind Kind { get; }
    internal object? Parameters { get; }
    internal SKImageFilter? Input { get; }
    internal SKRect? CropRect { get; }

    public bool IsBlur => Kind == FilterKind.Blur;
    public bool IsDropShadow => Kind == FilterKind.DropShadow;
    public float SigmaX => Parameters switch
    {
        BlurData blur => blur.SigmaX,
        DropShadowData shadow => shadow.SigmaX,
        _ => 0f,
    };
    public float SigmaY => Parameters switch
    {
        BlurData blur => blur.SigmaY,
        DropShadowData shadow => shadow.SigmaY,
        _ => 0f,
    };
    public float Dx => Parameters is DropShadowData shadow ? shadow.Dx : 0f;
    public float Dy => Parameters is DropShadowData shadow ? shadow.Dy : 0f;
    public SKColor ShadowColor => Parameters is DropShadowData shadow ? shadow.Color : SKColor.Empty;

    public static SKImageFilter CreateBlur(float sigmaX, float sigmaY, SKImageFilter? input = null) =>
        new(FilterKind.Blur, new BlurData(sigmaX, sigmaY, SKShaderTileMode.Clamp), input, null);

    public static SKImageFilter CreateBlur(
        float sigmaX,
        float sigmaY,
        SKShaderTileMode tileMode,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Blur, new BlurData(sigmaX, sigmaY, tileMode), input, cropRect);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color) =>
        CreateDropShadow(dx, dy, sigmaX, sigmaY, color, null, null);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input) =>
        CreateDropShadow(dx, dy, sigmaX, sigmaY, color, input, null);

    public static SKImageFilter CreateDropShadow(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input,
        SKRect? cropRect) =>
        new(FilterKind.DropShadow, new DropShadowData(dx, dy, sigmaX, sigmaY, color, ShadowOnly: false), input, cropRect);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color) =>
        CreateDropShadowOnly(dx, dy, sigmaX, sigmaY, color, null, null);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input) =>
        CreateDropShadowOnly(dx, dy, sigmaX, sigmaY, color, input, null);

    public static SKImageFilter CreateDropShadowOnly(
        float dx,
        float dy,
        float sigmaX,
        float sigmaY,
        SKColor color,
        SKImageFilter? input,
        SKRect? cropRect) =>
        new(FilterKind.DropShadow, new DropShadowData(dx, dy, sigmaX, sigmaY, color, ShadowOnly: true), input, cropRect);

    public static SKImageFilter CreateArithmetic(
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePremultipliedColor,
        SKImageFilter background,
        SKImageFilter? foreground = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Arithmetic, new ArithmeticData(k1, k2, k3, k4, enforcePremultipliedColor, background, foreground), null, cropRect);

    public static SKImageFilter CreateBlendMode(
        SKBlendMode mode,
        SKImageFilter background,
        SKImageFilter? foreground = null,
        SKRect? cropRect = null) =>
        new(FilterKind.BlendMode, new BlendModeData(mode, background, foreground), null, cropRect);

    public static SKImageFilter CreateColorFilter(
        SKColorFilter colorFilter,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.ColorFilter, colorFilter ?? throw new ArgumentNullException(nameof(colorFilter)), input, cropRect);

    public static SKImageFilter CreateDilate(
        float radiusX,
        float radiusY,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Dilate, new MorphologyData(radiusX, radiusY), input, cropRect);

    public static SKImageFilter CreateDisplacementMapEffect(
        SKColorChannel xChannelSelector,
        SKColorChannel yChannelSelector,
        float scale,
        SKImageFilter displacement,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.DisplacementMap, new DisplacementData(xChannelSelector, yChannelSelector, scale, displacement), input, cropRect);

    public static SKImageFilter CreateDistantLitDiffuse(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.DistantLitDiffuse, new DistantLightData(direction, lightColor, surfaceScale, kd, 0f), input, cropRect);

    public static SKImageFilter CreateDistantLitSpecular(
        SKPoint3 direction,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.DistantLitSpecular, new DistantLightData(direction, lightColor, surfaceScale, ks, shininess), input, cropRect);

    public static SKImageFilter CreateErode(
        float radiusX,
        float radiusY,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Erode, new MorphologyData(radiusX, radiusY), input, cropRect);

    public static SKImageFilter CreateImage(
        SKImage image,
        SKRect source,
        SKRect destination,
        SKSamplingOptions sampling) =>
        new(FilterKind.Image, new ImageData(image, source, destination, sampling), null, null);

    public static SKImageFilter CreateMatrixConvolution(
        SKSizeI kernelSize,
        float[] kernel,
        float gain,
        float bias,
        SKPointI kernelOffset,
        SKShaderTileMode tileMode,
        bool convolveAlpha,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.MatrixConvolution, new MatrixConvolutionData(
            kernelSize,
            (float[])(kernel ?? throw new ArgumentNullException(nameof(kernel))).Clone(),
            gain,
            bias,
            kernelOffset,
            tileMode,
            convolveAlpha), input, cropRect);

    public static SKImageFilter CreateMerge(SKImageFilter[] filters, SKRect? cropRect = null) =>
        new(FilterKind.Merge, (SKImageFilter[])(filters ?? throw new ArgumentNullException(nameof(filters))).Clone(), null, cropRect);

    public static SKImageFilter CreateOffset(
        float dx,
        float dy,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.Offset, new OffsetData(dx, dy), input, cropRect);

    public static SKImageFilter CreateShader(SKShader shader, bool dither, SKRect? cropRect = null) =>
        new(FilterKind.Shader, new ShaderData(shader ?? throw new ArgumentNullException(nameof(shader)), dither), null, cropRect);

    public static SKImageFilter CreatePicture(SKPicture picture, SKRect targetRect) =>
        new(FilterKind.Picture, new PictureData(picture ?? throw new ArgumentNullException(nameof(picture)), targetRect), null, null);

    public static SKImageFilter CreatePointLitDiffuse(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.PointLitDiffuse, new PointLightData(location, lightColor, surfaceScale, kd, 0f), input, cropRect);

    public static SKImageFilter CreatePointLitSpecular(
        SKPoint3 location,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.PointLitSpecular, new PointLightData(location, lightColor, surfaceScale, ks, shininess), input, cropRect);

    public static SKImageFilter CreateSpotLitDiffuse(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float kd,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.SpotLitDiffuse, new SpotLightData(location, target, specularExponent, cutoffAngle, lightColor, surfaceScale, kd, 0f), input, cropRect);

    public static SKImageFilter CreateSpotLitSpecular(
        SKPoint3 location,
        SKPoint3 target,
        float specularExponent,
        float cutoffAngle,
        SKColor lightColor,
        float surfaceScale,
        float ks,
        float shininess,
        SKImageFilter? input = null,
        SKRect? cropRect = null) =>
        new(FilterKind.SpotLitSpecular, new SpotLightData(location, target, specularExponent, cutoffAngle, lightColor, surfaceScale, ks, shininess), input, cropRect);

    public static SKImageFilter CreateTile(SKRect source, SKRect destination, SKImageFilter? input = null) =>
        new(FilterKind.Tile, new TileData(source, destination), input, null);

    public void Dispose() { }

    internal sealed record BlurData(float SigmaX, float SigmaY, SKShaderTileMode TileMode);
    internal sealed record DropShadowData(float Dx, float Dy, float SigmaX, float SigmaY, SKColor Color, bool ShadowOnly);
    internal sealed record ArithmeticData(float K1, float K2, float K3, float K4, bool EnforcePremultipliedColor, SKImageFilter Background, SKImageFilter? Foreground);
    internal sealed record BlendModeData(SKBlendMode Mode, SKImageFilter Background, SKImageFilter? Foreground);
    internal sealed record MorphologyData(float RadiusX, float RadiusY);
    internal sealed record DisplacementData(SKColorChannel XChannel, SKColorChannel YChannel, float Scale, SKImageFilter Displacement);
    internal sealed record DistantLightData(SKPoint3 Direction, SKColor Color, float SurfaceScale, float Constant, float Shininess);
    internal sealed record ImageData(SKImage Image, SKRect Source, SKRect Destination, SKSamplingOptions Sampling);
    internal sealed record MatrixConvolutionData(SKSizeI KernelSize, float[] Kernel, float Gain, float Bias, SKPointI KernelOffset, SKShaderTileMode TileMode, bool ConvolveAlpha);
    internal sealed record OffsetData(float Dx, float Dy);
    internal sealed record ShaderData(SKShader Shader, bool Dither);
    internal sealed record PictureData(SKPicture Picture, SKRect TargetRect);
    internal sealed record PointLightData(SKPoint3 Location, SKColor Color, float SurfaceScale, float Constant, float Shininess);
    internal sealed record SpotLightData(SKPoint3 Location, SKPoint3 Target, float SpecularExponent, float CutoffAngle, SKColor Color, float SurfaceScale, float Constant, float Shininess);
    internal sealed record TileData(SKRect Source, SKRect Destination);
}

public class SKPathEffect : IDisposable
{
    public IntPtr Handle { get; } = SKObjectHandle.Create();
    public float[] Intervals { get; }
    public float Phase { get; }

    private SKPathEffect(float[] intervals, float phase)
    {
        Intervals = (float[])intervals.Clone();
        Phase = phase;
    }

    public static SKPathEffect CreateDash(float[] intervals, float phase)
    {
        return new SKPathEffect(intervals, phase);
    }

    public void Dispose() { }
}

public class SKMaskFilter : IDisposable
{
    public float Sigma { get; }

    private SKMaskFilter(float sigma)
    {
        Sigma = sigma;
    }

    public static SKMaskFilter CreateBlur(SKBlurStyle style, float sigma)
    {
        return new SKMaskFilter(sigma);
    }

    public void Dispose() { }
}

public enum SKBlurStyle
{
    Normal = 0,
    Solid = 1,
    Outer = 2,
    Inner = 3,
}
