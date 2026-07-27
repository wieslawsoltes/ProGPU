using System.Numerics;

namespace ProGPU.Vector;

internal readonly record struct RoundedRectanglePathContour(
    float Left,
    float Top,
    float Right,
    float Bottom,
    Vector4 CornerRadiiX,
    Vector4 CornerRadiiY)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
}

internal static class RoundedRectanglePathGeometry
{
    public static bool TryReadCanonicalContour(
        PathFigure figure,
        out RoundedRectanglePathContour contour)
    {
        contour = default;
        if (!figure.IsClosed || !figure.IsFilled ||
            figure.Segments.Count is < 4 or > 8 ||
            !IsFinite(figure.StartPoint))
        {
            return false;
        }

        float left = figure.StartPoint.X;
        float top = figure.StartPoint.Y;
        float right = figure.StartPoint.X;
        float bottom = figure.StartPoint.Y;
        for (int index = 0; index < figure.Segments.Count; index++)
        {
            if (!TryGetEndPoint(figure.Segments[index], out Vector2 end) || !IsFinite(end))
            {
                return false;
            }

            left = MathF.Min(left, end.X);
            top = MathF.Min(top, end.Y);
            right = MathF.Max(right, end.X);
            bottom = MathF.Max(bottom, end.Y);
        }

        float width = right - left;
        float height = bottom - top;
        float tolerance = GetTolerance(width, height);
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            width <= tolerance || height <= tolerance ||
            !NearlyEqual(figure.StartPoint.Y, top, tolerance))
        {
            return false;
        }

        List<PathSegment> segments = figure.Segments;
        int segmentIndex = 0;
        Vector2 current = figure.StartPoint;
        var radiiX = Vector4.Zero;
        var radiiY = Vector4.Zero;

        if (!MatchLine(
                expectedHorizontal: true,
                fixedCoordinate: top,
                increasing: true,
                ref current) ||
            !MatchCorner(
                cornerIndex: 1,
                corner: new Vector2(right, top),
                ref current,
                ref radiiX,
                ref radiiY) ||
            !MatchLine(
                expectedHorizontal: false,
                fixedCoordinate: right,
                increasing: true,
                ref current) ||
            !MatchCorner(
                cornerIndex: 2,
                corner: new Vector2(right, bottom),
                ref current,
                ref radiiX,
                ref radiiY) ||
            !MatchLine(
                expectedHorizontal: true,
                fixedCoordinate: bottom,
                increasing: false,
                ref current) ||
            !MatchCorner(
                cornerIndex: 3,
                corner: new Vector2(left, bottom),
                ref current,
                ref radiiX,
                ref radiiY) ||
            !MatchLine(
                expectedHorizontal: false,
                fixedCoordinate: left,
                increasing: false,
                ref current) ||
            !MatchCorner(
                cornerIndex: 0,
                corner: new Vector2(left, top),
                ref current,
                ref radiiX,
                ref radiiY) ||
            segmentIndex != segments.Count ||
            !PointsEqual(current, figure.StartPoint, tolerance) ||
            radiiX.X + radiiX.Y > width + tolerance ||
            radiiX.W + radiiX.Z > width + tolerance ||
            radiiY.X + radiiY.W > height + tolerance ||
            radiiY.Y + radiiY.Z > height + tolerance)
        {
            return false;
        }

        contour = new RoundedRectanglePathContour(
            left,
            top,
            right,
            bottom,
            radiiX,
            radiiY);
        return true;

        bool MatchLine(
            bool expectedHorizontal,
            float fixedCoordinate,
            bool increasing,
            ref Vector2 point)
        {
            if (segmentIndex >= segments.Count ||
                segments[segmentIndex++] is not LineSegment line ||
                !IsFinite(line.Point))
            {
                return false;
            }

            Vector2 end = line.Point;
            float startVariable = expectedHorizontal ? point.X : point.Y;
            float endVariable = expectedHorizontal ? end.X : end.Y;
            float startFixed = expectedHorizontal ? point.Y : point.X;
            float endFixed = expectedHorizontal ? end.Y : end.X;
            if (!NearlyEqual(startFixed, fixedCoordinate, tolerance) ||
                !NearlyEqual(endFixed, fixedCoordinate, tolerance) ||
                increasing && endVariable + tolerance < startVariable ||
                !increasing && endVariable - tolerance > startVariable)
            {
                return false;
            }

            point = end;
            return true;
        }

        bool MatchCorner(
            int cornerIndex,
            Vector2 corner,
            ref Vector2 point,
            ref Vector4 xRadii,
            ref Vector4 yRadii)
        {
            if (segmentIndex >= segments.Count ||
                segments[segmentIndex] is not ArcSegment arc)
            {
                return PointsEqual(point, corner, tolerance);
            }

            segmentIndex++;
            Vector2 end = arc.Point;
            float radiusX;
            float radiusY;
            switch (cornerIndex)
            {
                case 0:
                    radiusX = end.X - left;
                    radiusY = point.Y - top;
                    if (!NearlyEqual(point.X, left, tolerance) ||
                        !NearlyEqual(end.Y, top, tolerance))
                    {
                        return false;
                    }
                    break;
                case 1:
                    radiusX = right - point.X;
                    radiusY = end.Y - top;
                    if (!NearlyEqual(point.Y, top, tolerance) ||
                        !NearlyEqual(end.X, right, tolerance))
                    {
                        return false;
                    }
                    break;
                case 2:
                    radiusX = point.X - end.X;
                    radiusY = bottom - point.Y;
                    if (!NearlyEqual(point.X, right, tolerance) ||
                        !NearlyEqual(end.Y, bottom, tolerance))
                    {
                        return false;
                    }
                    break;
                default:
                    radiusX = point.X - left;
                    radiusY = bottom - end.Y;
                    if (!NearlyEqual(point.Y, bottom, tolerance) ||
                        !NearlyEqual(end.X, left, tolerance))
                    {
                        return false;
                    }
                    break;
            }

            bool degenerate = radiusX <= tolerance && radiusY <= tolerance;
            if ((!degenerate && (radiusX <= tolerance || radiusY <= tolerance)) ||
                !float.IsFinite(arc.Size.X) ||
                !float.IsFinite(arc.Size.Y) ||
                MathF.Abs(MathF.Abs(arc.Size.X) - radiusX) > tolerance ||
                MathF.Abs(MathF.Abs(arc.Size.Y) - radiusY) > tolerance ||
                !float.IsFinite(arc.RotationAngle) ||
                MathF.Abs(arc.RotationAngle) > tolerance ||
                arc.IsLargeArc ||
                arc.SweepDirection != SweepDirection.Clockwise)
            {
                return false;
            }

            if (!degenerate)
            {
                SetComponent(ref xRadii, cornerIndex, radiusX);
                SetComponent(ref yRadii, cornerIndex, radiusY);
            }

            point = end;
            return true;
        }
    }

    public static bool Contains(
        RoundedRectanglePathContour outer,
        RoundedRectanglePathContour inner)
    {
        float tolerance = GetTolerance(outer.Width, outer.Height);
        if (inner.Left < outer.Left - tolerance ||
            inner.Top < outer.Top - tolerance ||
            inner.Right > outer.Right + tolerance ||
            inner.Bottom > outer.Bottom + tolerance ||
            inner.Width <= tolerance ||
            inner.Height <= tolerance)
        {
            return false;
        }

        for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
        {
            float innerRadiusX = GetComponent(inner.CornerRadiiX, cornerIndex);
            float innerRadiusY = GetComponent(inner.CornerRadiiY, cornerIndex);
            if (innerRadiusX <= tolerance && innerRadiusY <= tolerance)
            {
                if (!ContainsPoint(
                        outer,
                        GetCornerPoint(inner, cornerIndex),
                        tolerance))
                {
                    return false;
                }

                continue;
            }

            float outerRadiusX = GetComponent(outer.CornerRadiiX, cornerIndex);
            float outerRadiusY = GetComponent(outer.CornerRadiiY, cornerIndex);
            if (outerRadiusX <= tolerance ||
                outerRadiusY <= tolerance ||
                innerRadiusX > outerRadiusX + tolerance ||
                innerRadiusY > outerRadiusY + tolerance ||
                !PointsEqual(
                    GetCornerCenter(outer, cornerIndex),
                    GetCornerCenter(inner, cornerIndex),
                    tolerance))
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasPartialRoundedCorners(RoundedRectanglePathContour contour)
    {
        int roundedCornerCount = 0;
        for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
        {
            if (GetComponent(contour.CornerRadiiX, cornerIndex) > 0f &&
                GetComponent(contour.CornerRadiiY, cornerIndex) > 0f)
            {
                roundedCornerCount++;
            }
        }

        return roundedCornerCount is > 0 and < 4;
    }

    private static bool ContainsPoint(
        RoundedRectanglePathContour contour,
        Vector2 point,
        float tolerance)
    {
        if (point.X < contour.Left - tolerance ||
            point.X > contour.Right + tolerance ||
            point.Y < contour.Top - tolerance ||
            point.Y > contour.Bottom + tolerance)
        {
            return false;
        }

        for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
        {
            float radiusX = GetComponent(contour.CornerRadiiX, cornerIndex);
            float radiusY = GetComponent(contour.CornerRadiiY, cornerIndex);
            if (radiusX <= tolerance || radiusY <= tolerance)
            {
                continue;
            }

            Vector2 center = GetCornerCenter(contour, cornerIndex);
            Vector2 corner = GetCornerPoint(contour, cornerIndex);
            bool inCornerBox =
                (cornerIndex is 0 or 3 ? point.X < center.X : point.X > center.X) &&
                (cornerIndex is 0 or 1 ? point.Y < center.Y : point.Y > center.Y);
            if (!inCornerBox)
            {
                continue;
            }

            float normalizedX = (point.X - center.X) / radiusX;
            float normalizedY = (point.Y - center.Y) / radiusY;
            return normalizedX * normalizedX + normalizedY * normalizedY <= 1f + tolerance;
        }

        return true;
    }

    private static Vector2 GetCornerCenter(
        RoundedRectanglePathContour contour,
        int cornerIndex)
    {
        float radiusX = GetComponent(contour.CornerRadiiX, cornerIndex);
        float radiusY = GetComponent(contour.CornerRadiiY, cornerIndex);
        return cornerIndex switch
        {
            0 => new Vector2(contour.Left + radiusX, contour.Top + radiusY),
            1 => new Vector2(contour.Right - radiusX, contour.Top + radiusY),
            2 => new Vector2(contour.Right - radiusX, contour.Bottom - radiusY),
            _ => new Vector2(contour.Left + radiusX, contour.Bottom - radiusY)
        };
    }

    private static Vector2 GetCornerPoint(
        RoundedRectanglePathContour contour,
        int cornerIndex) =>
        cornerIndex switch
        {
            0 => new Vector2(contour.Left, contour.Top),
            1 => new Vector2(contour.Right, contour.Top),
            2 => new Vector2(contour.Right, contour.Bottom),
            _ => new Vector2(contour.Left, contour.Bottom)
        };

    private static float GetTolerance(float width, float height) =>
        MathF.Max(0.0001f, MathF.Max(MathF.Abs(width), MathF.Abs(height)) * 0.00001f);

    private static bool TryGetEndPoint(PathSegment segment, out Vector2 point)
    {
        point = segment switch
        {
            LineSegment line => line.Point,
            QuadraticBezierSegment quadratic => quadratic.Point,
            CubicBezierSegment cubic => cubic.Point,
            ArcSegment arc => arc.Point,
            _ => default
        };
        return segment is LineSegment or QuadraticBezierSegment or CubicBezierSegment or ArcSegment;
    }

    private static bool IsFinite(Vector2 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y);

    private static bool NearlyEqual(float first, float second, float tolerance) =>
        MathF.Abs(first - second) <= tolerance;

    private static bool PointsEqual(Vector2 first, Vector2 second, float tolerance) =>
        NearlyEqual(first.X, second.X, tolerance) &&
        NearlyEqual(first.Y, second.Y, tolerance);

    private static float GetComponent(Vector4 vector, int index) =>
        index switch
        {
            0 => vector.X,
            1 => vector.Y,
            2 => vector.Z,
            _ => vector.W
        };

    private static void SetComponent(ref Vector4 vector, int index, float value)
    {
        switch (index)
        {
            case 0: vector.X = value; break;
            case 1: vector.Y = value; break;
            case 2: vector.Z = value; break;
            default: vector.W = value; break;
        }
    }
}
