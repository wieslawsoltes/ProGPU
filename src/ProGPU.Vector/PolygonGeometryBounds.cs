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
static class PolygonGeometryBounds
{
    public static bool TryGetBounds(
        ReadOnlySpan<Vector2> points,
        Matrix4x4 geometryTransform,
        Matrix4x4 worldTransform,
        float strokeThickness,
        out Vector2 min,
        out Vector2 max)
    {
        Matrix4x4 transform = geometryTransform * worldTransform;
        return TryGetBounds(points, transform, strokeThickness, out min, out max);
    }

    public static bool TryGetBounds(
        ReadOnlySpan<Vector2> points,
        Matrix4x4 transform,
        float strokeThickness,
        out Vector2 min,
        out Vector2 max)
    {
        min = default;
        max = default;

        if (points.IsEmpty || !float.IsFinite(strokeThickness))
        {
            return false;
        }

        strokeThickness = MathF.Abs(strokeThickness);

        var minValue = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var maxValue = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

        for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            Vector2 point = points[pointIndex];
            Vector2 transformed = Vector2.Transform(point, transform);
            if (!IsFinite(transformed))
            {
                return false;
            }

            minValue = Vector2.Min(minValue, transformed);
            maxValue = Vector2.Max(maxValue, transformed);
        }

        if (strokeThickness > 0.0f)
        {
            float scale = TransformMetrics.GetStrokeScale(transform);
            float inflation = strokeThickness * scale * 0.5f;
            if (!float.IsFinite(inflation))
            {
                return false;
            }

            var padding = new Vector2(inflation, inflation);
            minValue -= padding;
            maxValue += padding;
        }

        min = minValue;
        max = maxValue;
        return true;
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }
}
