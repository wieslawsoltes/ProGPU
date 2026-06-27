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
static class RectangleGeometryHitTesting
{
    public static bool ContainsFill(
        Vector2 point,
        Vector2 min,
        Vector2 max,
        Vector2 radius)
    {
        if (!IsFinite(point) || !IsFinite(min) || !IsFinite(max) || !IsFinite(radius))
        {
            return false;
        }

        if (max.X < min.X || max.Y < min.Y)
        {
            return false;
        }

        if (!ContainsAxisAligned(point, min, max))
        {
            return false;
        }

        float width = max.X - min.X;
        float height = max.Y - min.Y;
        float radiusX = MathF.Min(MathF.Abs(radius.X), width * 0.5f);
        float radiusY = MathF.Min(MathF.Abs(radius.Y), height * 0.5f);
        if (radiusX <= 0.0f || radiusY <= 0.0f)
        {
            return true;
        }

        float left = min.X + radiusX;
        float right = max.X - radiusX;
        float top = min.Y + radiusY;
        float bottom = max.Y - radiusY;
        if ((point.X >= left && point.X <= right) ||
            (point.Y >= top && point.Y <= bottom))
        {
            return true;
        }

        float centerX = point.X < left ? left : right;
        float centerY = point.Y < top ? top : bottom;
        float normalizedX = (point.X - centerX) / radiusX;
        float normalizedY = (point.Y - centerY) / radiusY;
        return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1.0f;
    }

    public static bool ContainsStroke(
        Vector2 point,
        Vector2 min,
        Vector2 max,
        Vector2 radius,
        float strokeThickness,
        float tolerance,
        bool relativeTolerance)
    {
        if (!IsFinite(point) || !IsFinite(min) || !IsFinite(max) || !IsFinite(radius))
        {
            return false;
        }

        if (max.X < min.X || max.Y < min.Y || !float.IsFinite(strokeThickness) || strokeThickness <= 0.0f)
        {
            return false;
        }

        if (!float.IsFinite(tolerance))
        {
            return false;
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

        float halfStroke = (MathF.Abs(strokeThickness) * 0.5f) + tolerancePadding;
        if (halfStroke <= 0.0f || !float.IsFinite(halfStroke))
        {
            return false;
        }

        var padding = new Vector2(halfStroke, halfStroke);
        Vector2 outerMin = min - padding;
        Vector2 outerMax = max + padding;
        Vector2 outerRadius = new(
            MathF.Abs(radius.X) + halfStroke,
            MathF.Abs(radius.Y) + halfStroke);

        if (!ContainsFill(point, outerMin, outerMax, outerRadius))
        {
            return false;
        }

        Vector2 innerMin = min + padding;
        Vector2 innerMax = max - padding;
        if (innerMax.X <= innerMin.X || innerMax.Y <= innerMin.Y)
        {
            return true;
        }

        Vector2 innerRadius = new(
            MathF.Max(0.0f, MathF.Abs(radius.X) - halfStroke),
            MathF.Max(0.0f, MathF.Abs(radius.Y) - halfStroke));

        return !ContainsFill(point, innerMin, innerMax, innerRadius);
    }

    private static bool ContainsAxisAligned(Vector2 point, Vector2 min, Vector2 max)
    {
        return point.X >= min.X &&
               point.X <= max.X &&
               point.Y >= min.Y &&
               point.Y <= max.Y;
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
