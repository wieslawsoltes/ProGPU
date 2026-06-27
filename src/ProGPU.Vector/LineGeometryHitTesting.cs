using System;
using System.Numerics;

#nullable enable
#pragma warning disable IDE0057, IDE0059, IDE0078, IDE0300, IDE0301, IDE0305

namespace ProGPU.Vector;

#if PROGPU_VECTOR_INTERNAL
internal
#else
public
#endif
enum LineGeometryCap
{
    Flat = 0,
    Square = 1,
    Round = 2,
    Triangle = 3
}

#if PROGPU_VECTOR_INTERNAL
internal
#else
public
#endif
static class LineGeometryHitTesting
{
    private const float Epsilon = 0.0001f;

    public static bool ContainsFill(Vector2 point, Vector2 start, Vector2 end)
    {
        if (!IsFinite(point) || !IsFinite(start) || !IsFinite(end))
        {
            return false;
        }

        return false;
    }

    public static bool ContainsStroke(
        Vector2 point,
        Vector2 start,
        Vector2 end,
        float strokeThickness,
        float tolerance,
        bool relativeTolerance,
        LineGeometryCap startCap,
        LineGeometryCap endCap)
    {
        if (!IsFinite(point) || !IsFinite(start) || !IsFinite(end))
        {
            return false;
        }

        if (!float.IsFinite(strokeThickness) || strokeThickness <= 0.0f || !float.IsFinite(tolerance))
        {
            return false;
        }

        Vector2 segment = end - start;
        float tolerancePadding = MathF.Max(0.0f, tolerance);
        if (relativeTolerance)
        {
            tolerancePadding *= MathF.Max(MathF.Abs(segment.X), MathF.Abs(segment.Y));
        }

        if (!float.IsFinite(tolerancePadding))
        {
            return false;
        }

        float halfStroke = (MathF.Abs(strokeThickness) * 0.5f) + tolerancePadding;
        if (halfStroke <= 0.0f || !float.IsFinite(halfStroke))
        {
            return false;
        }

        float length = segment.Length();
        if (!float.IsFinite(length) || length <= Epsilon)
        {
            return ContainsDegenerateStroke(point, start, halfStroke, startCap, endCap);
        }

        Vector2 direction = segment / length;
        Vector2 offset = point - start;
        float along = Vector2.Dot(offset, direction);
        float signedDistance = Cross(offset, direction);
        float distance = MathF.Abs(signedDistance);

        if (along >= 0.0f && along <= length && distance <= halfStroke)
        {
            return true;
        }

        if (along < 0.0f)
        {
            return ContainsStartCap(point, start, direction, signedDistance, along, halfStroke, startCap);
        }

        return ContainsEndCap(point, end, direction, signedDistance, along - length, halfStroke, endCap);
    }

    private static bool ContainsDegenerateStroke(
        Vector2 point,
        Vector2 center,
        float radius,
        LineGeometryCap startCap,
        LineGeometryCap endCap)
    {
        if (!float.IsFinite(radius) || radius <= 0.0f)
        {
            return false;
        }

        if (startCap == LineGeometryCap.Flat && endCap == LineGeometryCap.Flat)
        {
            return false;
        }

        return Vector2.DistanceSquared(point, center) <= radius * radius;
    }

    private static bool ContainsStartCap(
        Vector2 point,
        Vector2 start,
        Vector2 direction,
        float signedDistance,
        float along,
        float halfStroke,
        LineGeometryCap cap)
    {
        return cap switch
        {
            LineGeometryCap.Square => along >= -halfStroke && MathF.Abs(signedDistance) <= halfStroke,
            LineGeometryCap.Round => Vector2.DistanceSquared(point, start) <= halfStroke * halfStroke,
            LineGeometryCap.Triangle => ContainsTriangleCap(point, start, -direction, halfStroke),
            _ => false
        };
    }

    private static bool ContainsEndCap(
        Vector2 point,
        Vector2 end,
        Vector2 direction,
        float signedDistance,
        float pastEnd,
        float halfStroke,
        LineGeometryCap cap)
    {
        return cap switch
        {
            LineGeometryCap.Square => pastEnd <= halfStroke && MathF.Abs(signedDistance) <= halfStroke,
            LineGeometryCap.Round => Vector2.DistanceSquared(point, end) <= halfStroke * halfStroke,
            LineGeometryCap.Triangle => ContainsTriangleCap(point, end, direction, halfStroke),
            _ => false
        };
    }

    private static bool ContainsTriangleCap(Vector2 point, Vector2 baseCenter, Vector2 outward, float halfStroke)
    {
        Vector2 normal = new(-outward.Y, outward.X);
        Vector2 base0 = baseCenter - normal * halfStroke;
        Vector2 base1 = baseCenter + normal * halfStroke;
        Vector2 apex = baseCenter + outward * halfStroke;
        return ContainsTriangle(point, base0, base1, apex);
    }

    private static bool ContainsTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        float denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= Epsilon)
        {
            return false;
        }

        float alpha = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / denominator;
        float beta = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / denominator;
        float gamma = 1.0f - alpha - beta;
        return alpha >= 0.0f && beta >= 0.0f && gamma >= 0.0f;
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
