using System;
using System.Collections.Generic;
using System.Numerics;

#nullable enable
#pragma warning disable IDE0057, IDE0059, IDE0078, IDE0300, IDE0301, IDE0305

namespace ProGPU.Vector;

#if PROGPU_VECTOR_INTERNAL
internal
#else
public
#endif
static class PathGeometryHitTesting
{
    private const float Epsilon = 0.0001f;
    private const int QuadraticFlattenSegmentCount = 16;
    private const int CubicFlattenSegmentCount = 24;

    public static bool TryContainsFill(
        PathGeometry? geometry,
        Vector2 point,
        float tolerance,
        bool relativeTolerance,
        out bool contains)
        => TryContainsFillCore(
            geometry,
            point,
            tolerance,
            relativeTolerance,
            depth: 0,
            out contains);

    private static bool TryContainsFillCore(
        PathGeometry? geometry,
        Vector2 point,
        float tolerance,
        bool relativeTolerance,
        int depth,
        out bool contains)
    {
        contains = false;

        if (geometry == null ||
            !IsFinite(point) ||
            !float.IsFinite(tolerance))
        {
            return false;
        }

        if (geometry.IsCombined)
        {
            if (depth >= 256 || geometry.PathA == null || geometry.PathB == null ||
                (uint)geometry.Op > (uint)PathBooleanOperation.ReverseDifference ||
                !TryContainsFillCore(
                    geometry.PathA,
                    point,
                    tolerance,
                    relativeTolerance,
                    depth + 1,
                    out bool containsA) ||
                !TryContainsFillCore(
                    geometry.PathB,
                    point,
                    tolerance,
                    relativeTolerance,
                    depth + 1,
                    out bool containsB))
            {
                return false;
            }

            contains = (PathBooleanOperation)geometry.Op switch
            {
                PathBooleanOperation.Difference => containsA && !containsB,
                PathBooleanOperation.Intersect => containsA && containsB,
                PathBooleanOperation.Union => containsA || containsB,
                PathBooleanOperation.ExclusiveOr => containsA != containsB,
                PathBooleanOperation.ReverseDifference => containsB && !containsA,
                _ => false,
            };
            return true;
        }

        var figures = geometry.Figures;
        var polygons = new List<Vector2[]>(Math.Max(1, figures.Count));
        Vector2 min = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity, float.NegativeInfinity);

        for (int figureIndex = 0; figureIndex < figures.Count; figureIndex++)
        {
            PathFigure figure = figures[figureIndex];
            if (!figure.IsFilled)
            {
                continue;
            }

            if (!TryBuildFigurePolygon(figure, out Vector2[] polygon))
            {
                return false;
            }

            if (polygon.Length < 3)
            {
                continue;
            }

            polygons.Add(polygon);
            for (int pointIndex = 0; pointIndex < polygon.Length; pointIndex++)
            {
                Vector2 candidate = polygon[pointIndex];
                min = Vector2.Min(min, candidate);
                max = Vector2.Max(max, candidate);
            }
        }

        if (polygons.Count == 0)
        {
            return true;
        }

        float tolerancePadding = MathF.Max(0.0f, tolerance);
        if (relativeTolerance)
        {
            tolerancePadding *= MathF.Max(MathF.Abs(max.X - min.X), MathF.Abs(max.Y - min.Y));
        }

        if (!float.IsFinite(tolerancePadding))
        {
            return false;
        }

        float boundaryTolerance = MathF.Max(tolerancePadding, Epsilon);
        int winding = 0;
        bool evenOddContains = false;

        for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
        {
            Vector2[] polygon = polygons[polygonIndex];
            ReadOnlySpan<Vector2> points = polygon;
            if (IsPointOnBoundary(points, point, boundaryTolerance))
            {
                contains = true;
                return true;
            }

            if (geometry.FillRule == FillRule.EvenOdd)
            {
                if (ContainsEvenOdd(points, point))
                {
                    evenOddContains = !evenOddContains;
                }
            }
            else
            {
                winding += GetWindingContribution(points, point);
            }
        }

        contains = geometry.FillRule == FillRule.EvenOdd
            ? evenOddContains
            : winding != 0;
        return true;
    }

    private static bool TryBuildFigurePolygon(PathFigure figure, out Vector2[] polygon)
    {
        polygon = Array.Empty<Vector2>();
        if (!IsFinite(figure.StartPoint))
        {
            return false;
        }

        var segments = figure.Segments;
        var points = new List<Vector2>(EstimateFigurePointCapacity(segments));
        points.Add(figure.StartPoint);
        Vector2 currentPoint = figure.StartPoint;

        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            PathSegment segment = segments[segmentIndex];
            switch (segment)
            {
                case LineSegment line:
                    if (!AddPoint(points, line.Point))
                    {
                        return false;
                    }

                    currentPoint = line.Point;
                    break;

                case QuadraticBezierSegment quadratic:
                    if (!IsFinite(quadratic.ControlPoint) || !IsFinite(quadratic.Point))
                    {
                        return false;
                    }

                    for (int i = 1; i <= QuadraticFlattenSegmentCount; i++)
                    {
                        float t = (float)i / QuadraticFlattenSegmentCount;
                        if (!AddPoint(points, EvaluateQuadratic(currentPoint, quadratic.ControlPoint, quadratic.Point, t)))
                        {
                            return false;
                        }
                    }

                    currentPoint = quadratic.Point;
                    break;

                case CubicBezierSegment cubic:
                    if (!IsFinite(cubic.ControlPoint1) ||
                        !IsFinite(cubic.ControlPoint2) ||
                        !IsFinite(cubic.Point))
                    {
                        return false;
                    }

                    for (int i = 1; i <= CubicFlattenSegmentCount; i++)
                    {
                        float t = (float)i / CubicFlattenSegmentCount;
                        if (!AddPoint(points, EvaluateCubic(currentPoint, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, t)))
                        {
                            return false;
                        }
                    }

                    currentPoint = cubic.Point;
                    break;

                case ArcSegment arc:
                    Vector2[] arcPoints = ArcSegmentGeometry.FlattenArc(currentPoint, arc);
                    for (int i = 1; i < arcPoints.Length; i++)
                    {
                        if (!AddPoint(points, arcPoints[i]))
                        {
                            return false;
                        }
                    }

                    currentPoint = arc.Point;
                    break;

                default:
                    return false;
            }
        }

        polygon = CopyPoints(points);
        return true;
    }

    private static int EstimateFigurePointCapacity(List<PathSegment> segments)
    {
        var capacity = 1;
        for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            capacity += segments[segmentIndex] switch
            {
                LineSegment => 1,
                QuadraticBezierSegment => QuadraticFlattenSegmentCount,
                CubicBezierSegment => CubicFlattenSegmentCount,
                ArcSegment => CubicFlattenSegmentCount,
                _ => 1
            };
        }

        return capacity;
    }

    private static Vector2[] CopyPoints(List<Vector2> points)
    {
        var result = new Vector2[points.Count];
        for (int pointIndex = 0; pointIndex < result.Length; pointIndex++)
        {
            result[pointIndex] = points[pointIndex];
        }

        return result;
    }

    private static bool AddPoint(List<Vector2> points, Vector2 point)
    {
        if (!IsFinite(point))
        {
            return false;
        }

        if (points.Count == 0 || Vector2.DistanceSquared(points[points.Count - 1], point) > Epsilon * Epsilon)
        {
            points.Add(point);
        }

        return true;
    }

    private static bool ContainsEvenOdd(ReadOnlySpan<Vector2> points, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[j];
            bool crossesY = (a.Y > point.Y) != (b.Y > point.Y);
            if (crossesY)
            {
                float intersectionX = ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X;
                if (point.X < intersectionX)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static int GetWindingContribution(ReadOnlySpan<Vector2> points, Vector2 point)
    {
        int winding = 0;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            Vector2 a = points[j];
            Vector2 b = points[i];
            float cross = Cross(b - a, point - a);

            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && cross > 0.0f)
                {
                    winding++;
                }
            }
            else if (b.Y <= point.Y && cross < 0.0f)
            {
                winding--;
            }
        }

        return winding;
    }

    private static bool IsPointOnBoundary(ReadOnlySpan<Vector2> points, Vector2 point, float tolerance)
    {
        float toleranceSquared = tolerance * tolerance;
        for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
        {
            if (DistanceSquaredToSegment(point, points[j], points[i]) <= toleranceSquared)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistanceSquaredToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared <= Epsilon * Epsilon)
        {
            return Vector2.DistanceSquared(point, start);
        }

        float t = Vector2.Dot(point - start, segment) / lengthSquared;
        t = Math.Clamp(t, 0.0f, 1.0f);
        Vector2 projection = start + (segment * t);
        return Vector2.DistanceSquared(point, projection);
    }

    private static Vector2 EvaluateQuadratic(Vector2 start, Vector2 control, Vector2 end, float t)
    {
        float u = 1.0f - t;
        return (u * u * start) + (2.0f * u * t * control) + (t * t * end);
    }

    private static Vector2 EvaluateCubic(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end, float t)
    {
        float u = 1.0f - t;
        float tt = t * t;
        float uu = u * u;
        return (uu * u * start) +
               (3.0f * uu * t * control1) +
               (3.0f * u * tt * control2) +
               (tt * t * end);
    }

    private static float Cross(Vector2 left, Vector2 right)
    {
        return (left.X * right.Y) - (left.Y * right.X);
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
