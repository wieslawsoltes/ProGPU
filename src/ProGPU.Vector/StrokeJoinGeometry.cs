using System;
using System.Numerics;

namespace ProGPU.Vector;

public readonly struct StrokeJoinTriangle
{
    public StrokeJoinTriangle(Vector2 p0, Vector2 p1, Vector2 p2)
    {
        P0 = p0;
        P1 = p1;
        P2 = p2;
    }

    public Vector2 P0 { get; }
    public Vector2 P1 { get; }
    public Vector2 P2 { get; }
}

public static class StrokeJoinGeometry
{
    public const int MaxTrianglesPerJoin = 8;

    private const float Epsilon = 0.0001f;

    public static StrokeJoinTriangle[] CreateLineJoin(
        PenLineJoin lineJoin,
        float thickness,
        float miterLimit,
        Vector2 previousPoint,
        Vector2 joinPoint,
        Vector2 nextPoint,
        bool isSmoothJoin = false,
        int maxRoundSegments = MaxTrianglesPerJoin)
    {
        return CreateDirectionalJoin(
            lineJoin,
            thickness,
            miterLimit,
            joinPoint,
            joinPoint - previousPoint,
            nextPoint - joinPoint,
            isSmoothJoin,
            maxRoundSegments);
    }

    public static int WriteLineJoin(
        Span<StrokeJoinTriangle> destination,
        PenLineJoin lineJoin,
        float thickness,
        float miterLimit,
        Vector2 previousPoint,
        Vector2 joinPoint,
        Vector2 nextPoint,
        bool isSmoothJoin = false,
        int maxRoundSegments = MaxTrianglesPerJoin)
    {
        if (isSmoothJoin || !float.IsFinite(thickness) || thickness <= Epsilon ||
            !TryNormalize(joinPoint - previousPoint, out var incomingDirection) ||
            !TryNormalize(nextPoint - joinPoint, out var outgoingDirection))
        {
            return 0;
        }

        var turn = Cross(incomingDirection, outgoingDirection);
        if (MathF.Abs(turn) <= Epsilon)
        {
            return 0;
        }

        var radius = thickness * 0.5f;
        var outerSign = turn > 0f ? -1f : 1f;
        var previousOuterPoint = joinPoint + GetLeftNormal(incomingDirection) * outerSign * radius;
        var nextOuterPoint = joinPoint + GetLeftNormal(outgoingDirection) * outerSign * radius;
        if (lineJoin == PenLineJoin.Bevel)
        {
            EnsureDestination(destination, 1);
            destination[0] = new StrokeJoinTriangle(previousOuterPoint, joinPoint, nextOuterPoint);
            return 1;
        }

        if (lineJoin == PenLineJoin.Miter)
        {
            var clampedMiterLimit = float.IsFinite(miterLimit) && miterLimit >= 1f ? miterLimit : 1f;
            var hasMiter = TryIntersectLines(
                previousOuterPoint,
                incomingDirection,
                nextOuterPoint,
                outgoingDirection,
                out var miterPoint) &&
                Vector2.Distance(joinPoint, miterPoint) <= radius * clampedMiterLimit + Epsilon;
            var count = hasMiter ? 2 : 1;
            EnsureDestination(destination, count);
            destination[0] = new StrokeJoinTriangle(previousOuterPoint, joinPoint, nextOuterPoint);
            if (hasMiter)
            {
                destination[1] = new StrokeJoinTriangle(previousOuterPoint, miterPoint, nextOuterPoint);
            }
            return count;
        }

        var start = MathF.Atan2(
            previousOuterPoint.Y - joinPoint.Y,
            previousOuterPoint.X - joinPoint.X);
        var end = MathF.Atan2(
            nextOuterPoint.Y - joinPoint.Y,
            nextOuterPoint.X - joinPoint.X);
        if (turn > 0f)
        {
            while (end < start)
            {
                end += MathF.PI * 2f;
            }
        }
        else
        {
            while (end > start)
            {
                end -= MathF.PI * 2f;
            }
        }

        var sweep = end - start;
        var segmentCount = Math.Clamp(
            (int)MathF.Ceiling(MathF.Abs(sweep) / (MathF.PI / 8f)),
            1,
            Math.Clamp(maxRoundSegments, 1, MaxTrianglesPerJoin));
        EnsureDestination(destination, segmentCount);
        for (var index = 0; index < segmentCount; index++)
        {
            var angle0 = start + sweep * index / segmentCount;
            var angle1 = start + sweep * (index + 1) / segmentCount;
            var point0 = new Vector2(
                joinPoint.X + MathF.Cos(angle0) * radius,
                joinPoint.Y + MathF.Sin(angle0) * radius);
            var point1 = new Vector2(
                joinPoint.X + MathF.Cos(angle1) * radius,
                joinPoint.Y + MathF.Sin(angle1) * radius);
            destination[index] = new StrokeJoinTriangle(joinPoint, point0, point1);
        }

        return segmentCount;
    }

    private static void EnsureDestination(Span<StrokeJoinTriangle> destination, int count)
    {
        if (destination.Length < count)
        {
            throw new ArgumentException("The destination cannot hold the generated join triangles.", nameof(destination));
        }
    }

    public static StrokeJoinTriangle[] CreateDirectionalJoin(
        PenLineJoin lineJoin,
        float thickness,
        float miterLimit,
        Vector2 joinPoint,
        Vector2 incomingDirection,
        Vector2 outgoingDirection,
        bool isSmoothJoin = false,
        int maxRoundSegments = MaxTrianglesPerJoin)
    {
        if (isSmoothJoin || !float.IsFinite(thickness) || thickness <= Epsilon)
        {
            return Array.Empty<StrokeJoinTriangle>();
        }

        if (!TryNormalize(incomingDirection, out incomingDirection) ||
            !TryNormalize(outgoingDirection, out outgoingDirection))
        {
            return Array.Empty<StrokeJoinTriangle>();
        }

        var turn = Cross(incomingDirection, outgoingDirection);
        if (MathF.Abs(turn) <= Epsilon)
        {
            return Array.Empty<StrokeJoinTriangle>();
        }

        var radius = thickness * 0.5f;
        var outerSign = turn > 0f ? -1f : 1f;
        var previousOuterNormal = GetLeftNormal(incomingDirection) * outerSign;
        var nextOuterNormal = GetLeftNormal(outgoingDirection) * outerSign;
        var previousOuterPoint = joinPoint + previousOuterNormal * radius;
        var nextOuterPoint = joinPoint + nextOuterNormal * radius;

        return lineJoin switch
        {
            PenLineJoin.Bevel => CreateBevelJoin(previousOuterPoint, joinPoint, nextOuterPoint),
            PenLineJoin.Round => CreateRoundJoin(
                previousOuterPoint,
                joinPoint,
                nextOuterPoint,
                turn,
                Math.Clamp(maxRoundSegments, 1, MaxTrianglesPerJoin)),
            _ => CreateMiterJoin(
                incomingDirection,
                outgoingDirection,
                previousOuterPoint,
                joinPoint,
                nextOuterPoint,
                radius,
                miterLimit)
        };
    }

    private static StrokeJoinTriangle[] CreateBevelJoin(
        Vector2 previousOuterPoint,
        Vector2 joinPoint,
        Vector2 nextOuterPoint)
    {
        return new[] { new StrokeJoinTriangle(previousOuterPoint, joinPoint, nextOuterPoint) };
    }

    private static StrokeJoinTriangle[] CreateMiterJoin(
        Vector2 incomingDirection,
        Vector2 outgoingDirection,
        Vector2 previousOuterPoint,
        Vector2 joinPoint,
        Vector2 nextOuterPoint,
        float radius,
        float miterLimit)
    {
        var clampedMiterLimit = float.IsFinite(miterLimit) && miterLimit >= 1.0f ? miterLimit : 1.0f;
        if (TryIntersectLines(previousOuterPoint, incomingDirection, nextOuterPoint, outgoingDirection, out var miterPoint) &&
            Vector2.Distance(joinPoint, miterPoint) <= radius * clampedMiterLimit + Epsilon)
        {
            return new[]
            {
                new StrokeJoinTriangle(previousOuterPoint, joinPoint, nextOuterPoint),
                new StrokeJoinTriangle(previousOuterPoint, miterPoint, nextOuterPoint)
            };
        }

        return CreateBevelJoin(previousOuterPoint, joinPoint, nextOuterPoint);
    }

    private static StrokeJoinTriangle[] CreateRoundJoin(
        Vector2 previousOuterPoint,
        Vector2 joinPoint,
        Vector2 nextOuterPoint,
        float turn,
        int maxSegments)
    {
        var start = MathF.Atan2(previousOuterPoint.Y - joinPoint.Y, previousOuterPoint.X - joinPoint.X);
        var end = MathF.Atan2(nextOuterPoint.Y - joinPoint.Y, nextOuterPoint.X - joinPoint.X);

        if (turn > 0f)
        {
            while (end < start)
            {
                end += MathF.PI * 2f;
            }
        }
        else
        {
            while (end > start)
            {
                end -= MathF.PI * 2f;
            }
        }

        var sweep = end - start;
        var segmentCount = Math.Clamp(
            (int)MathF.Ceiling(MathF.Abs(sweep) / (MathF.PI / 8f)),
            1,
            maxSegments);
        var triangles = new StrokeJoinTriangle[segmentCount];

        for (int i = 0; i < segmentCount; i++)
        {
            var a0 = start + sweep * i / segmentCount;
            var a1 = start + sweep * (i + 1) / segmentCount;
            var p0 = new Vector2(
                joinPoint.X + MathF.Cos(a0) * Vector2.Distance(joinPoint, previousOuterPoint),
                joinPoint.Y + MathF.Sin(a0) * Vector2.Distance(joinPoint, previousOuterPoint));
            var p1 = new Vector2(
                joinPoint.X + MathF.Cos(a1) * Vector2.Distance(joinPoint, previousOuterPoint),
                joinPoint.Y + MathF.Sin(a1) * Vector2.Distance(joinPoint, previousOuterPoint));

            triangles[i] = new StrokeJoinTriangle(joinPoint, p0, p1);
        }

        return triangles;
    }

    private static bool TryNormalize(Vector2 vector, out Vector2 normalized)
    {
        var length = vector.Length();
        if (!float.IsFinite(length) || length <= Epsilon)
        {
            normalized = default;
            return false;
        }

        normalized = vector / length;
        return true;
    }

    private static Vector2 GetLeftNormal(Vector2 direction)
    {
        return new Vector2(-direction.Y, direction.X);
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.X * b.Y - a.Y * b.X;
    }

    private static bool TryIntersectLines(
        Vector2 point,
        Vector2 direction,
        Vector2 otherPoint,
        Vector2 otherDirection,
        out Vector2 intersection)
    {
        var denominator = Cross(direction, otherDirection);
        if (MathF.Abs(denominator) <= Epsilon)
        {
            intersection = default;
            return false;
        }

        var t = Cross(otherPoint - point, otherDirection) / denominator;
        intersection = point + direction * t;
        return true;
    }
}
