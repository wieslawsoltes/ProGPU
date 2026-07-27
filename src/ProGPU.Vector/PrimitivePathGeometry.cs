using System;
using System.Numerics;

namespace ProGPU.Vector;

public static class PrimitivePathGeometry
{
    private const float Epsilon = 0.0001f;

    /// <summary>
    /// Recognizes a filled, closed axis-aligned rectangle without allocating.
    /// Redundant collinear edges are accepted when their signed area still
    /// covers the complete bounding rectangle.
    /// </summary>
    public static bool TryGetAxisAlignedRectangleBounds(
        PathGeometry path,
        out Vector2 min,
        out Vector2 max)
    {
        min = default;
        max = default;
        if (path.IsCombined || path.Figures.Count != 1)
            return false;

        PathFigure figure = path.Figures[0];
        if (!figure.IsFilled ||
            !figure.IsClosed ||
            figure.Segments.Count < 4)
        {
            return false;
        }

        Vector2 start = figure.StartPoint;
        Vector2 current = start;
        min = start;
        max = start;
        double twiceArea = 0d;
        for (int index = 0; index < figure.Segments.Count; index++)
        {
            Vector2 next;
            switch (figure.Segments[index])
            {
                case LineSegment line:
                    next = line.Point;
                    break;
                case ArcSegment arc
                    when MathF.Abs(arc.Size.X) <= Epsilon ||
                         MathF.Abs(arc.Size.Y) <= Epsilon:
                    next = arc.Point;
                    break;
                default:
                    return false;
            }

            Vector2 delta = next - current;
            if (MathF.Abs(delta.X) > Epsilon &&
                MathF.Abs(delta.Y) > Epsilon)
            {
                return false;
            }

            twiceArea +=
                (double)current.X * next.Y -
                (double)next.X * current.Y;
            min = Vector2.Min(min, next);
            max = Vector2.Max(max, next);
            current = next;
        }

        Vector2 closingDelta = start - current;
        if (MathF.Abs(closingDelta.X) > Epsilon &&
            MathF.Abs(closingDelta.Y) > Epsilon)
        {
            return false;
        }
        twiceArea +=
            (double)current.X * start.Y -
            (double)start.X * current.Y;

        float width = max.X - min.X;
        float height = max.Y - min.Y;
        if (width <= Epsilon || height <= Epsilon)
            return false;

        double rectangleArea = (double)width * height;
        double polygonArea = Math.Abs(twiceArea) * 0.5d;
        return Math.Abs(polygonArea - rectangleArea) <=
            Math.Max(Epsilon, rectangleArea * Epsilon);
    }

    public static PathGeometry CreateRectangle(float x, float y, float width, float height)
    {
        var geometry = new PathGeometry();
        if (!IsPositiveFinite(width) || !IsPositiveFinite(height))
        {
            return geometry;
        }

        var figure = new PathFigure(new Vector2(x, y), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(x + width, y)));
        figure.Segments.Add(new LineSegment(new Vector2(x + width, y + height)));
        figure.Segments.Add(new LineSegment(new Vector2(x, y + height)));
        figure.Segments.Add(new LineSegment(new Vector2(x, y)));
        geometry.Figures.Add(figure);
        return geometry;
    }

    public static PathGeometry CreateRoundedRectangle(float x, float y, float width, float height, float radiusX, float radiusY)
    {
        var geometry = new PathGeometry();
        if (!IsPositiveFinite(width) || !IsPositiveFinite(height))
        {
            return geometry;
        }

        radiusX = MathF.Min(MathF.Abs(radiusX), width * 0.5f);
        radiusY = MathF.Min(MathF.Abs(radiusY), height * 0.5f);
        if (radiusX <= Epsilon || radiusY <= Epsilon)
        {
            return CreateRectangle(x, y, width, height);
        }

        var right = x + width;
        var bottom = y + height;
        var figure = new PathFigure(new Vector2(x + radiusX, y), isClosed: true);
        figure.Segments.Add(new LineSegment(new Vector2(right - radiusX, y)));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(right, y + radiusY), radiusX, radiusY));
        figure.Segments.Add(new LineSegment(new Vector2(right, bottom - radiusY)));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(right - radiusX, bottom), radiusX, radiusY));
        figure.Segments.Add(new LineSegment(new Vector2(x + radiusX, bottom)));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(x, bottom - radiusY), radiusX, radiusY));
        figure.Segments.Add(new LineSegment(new Vector2(x, y + radiusY)));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(x + radiusX, y), radiusX, radiusY));
        geometry.Figures.Add(figure);
        return geometry;
    }

    public static PathGeometry CreateEllipse(Vector2 center, float radiusX, float radiusY)
    {
        var geometry = new PathGeometry();
        if (!IsPositiveFinite(radiusX) || !IsPositiveFinite(radiusY))
        {
            return geometry;
        }

        var figure = new PathFigure(new Vector2(center.X + radiusX, center.Y), isClosed: true);
        figure.Segments.Add(CreateClockwiseArc(new Vector2(center.X, center.Y + radiusY), radiusX, radiusY));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(center.X - radiusX, center.Y), radiusX, radiusY));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(center.X, center.Y - radiusY), radiusX, radiusY));
        figure.Segments.Add(CreateClockwiseArc(new Vector2(center.X + radiusX, center.Y), radiusX, radiusY));
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static ArcSegment CreateClockwiseArc(Vector2 point, float radiusX, float radiusY)
    {
        return new ArcSegment(
            point,
            new Vector2(radiusX, radiusY),
            rotationAngle: 0f,
            isLargeArc: false,
            SweepDirection.Clockwise);
    }

    private static bool IsPositiveFinite(float value)
    {
        return float.IsFinite(value) && value > Epsilon;
    }
}
