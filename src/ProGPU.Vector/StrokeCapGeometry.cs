using System;
using System.Numerics;

namespace ProGPU.Vector;

public static class StrokeCapGeometry
{
    public const int MaxTrianglesPerCap = 8;

    private const float Epsilon = 0.0001f;

    public static StrokeJoinTriangle[] CreateLineCap(
        PenLineCap lineCap,
        float thickness,
        Vector2 lineStart,
        Vector2 lineEnd,
        bool isStart,
        int maxRoundSegments = MaxTrianglesPerCap)
    {
        return CreateDirectionalCap(
            lineCap,
            thickness,
            isStart ? lineStart : lineEnd,
            lineEnd - lineStart,
            isStart,
            maxRoundSegments);
    }

    public static int WriteLineCap(
        Span<StrokeJoinTriangle> destination,
        PenLineCap lineCap,
        float thickness,
        Vector2 lineStart,
        Vector2 lineEnd,
        bool isStart,
        int maxRoundSegments = MaxTrianglesPerCap)
    {
        if (lineCap == PenLineCap.Flat || !float.IsFinite(thickness) || thickness <= Epsilon)
        {
            return 0;
        }

        var delta = lineEnd - lineStart;
        var length = delta.Length();
        if (!float.IsFinite(length) || length <= Epsilon)
        {
            return 0;
        }

        var direction = delta / length;
        var center = isStart ? lineStart : lineEnd;
        var radius = thickness * 0.5f;
        var outward = isStart ? -direction : direction;
        var normal = new Vector2(-direction.Y, direction.X) * radius;
        if (lineCap == PenLineCap.Square)
        {
            EnsureDestination(destination, 2);
            var inner0 = center - normal;
            var inner1 = center + normal;
            var outer0 = center + outward * radius - normal;
            var outer1 = center + outward * radius + normal;
            destination[0] = new StrokeJoinTriangle(inner0, outer0, outer1);
            destination[1] = new StrokeJoinTriangle(inner0, outer1, inner1);
            return 2;
        }

        if (lineCap == PenLineCap.Triangle)
        {
            EnsureDestination(destination, 1);
            destination[0] = new StrokeJoinTriangle(
                center - normal,
                center + outward * radius,
                center + normal);
            return 1;
        }

        var segmentCount = Math.Clamp(maxRoundSegments, 1, MaxTrianglesPerCap);
        EnsureDestination(destination, segmentCount);
        var baseAngle = MathF.Atan2(outward.Y, outward.X);
        var start = baseAngle - MathF.PI * 0.5f;
        for (var index = 0; index < segmentCount; index++)
        {
            var angle0 = start + MathF.PI * index / segmentCount;
            var angle1 = start + MathF.PI * (index + 1) / segmentCount;
            var point0 = new Vector2(
                center.X + MathF.Cos(angle0) * radius,
                center.Y + MathF.Sin(angle0) * radius);
            var point1 = new Vector2(
                center.X + MathF.Cos(angle1) * radius,
                center.Y + MathF.Sin(angle1) * radius);
            destination[index] = new StrokeJoinTriangle(center, point0, point1);
        }

        return segmentCount;
    }

    private static void EnsureDestination(Span<StrokeJoinTriangle> destination, int count)
    {
        if (destination.Length < count)
        {
            throw new ArgumentException("The destination cannot hold the generated cap triangles.", nameof(destination));
        }
    }

    public static StrokeJoinTriangle[] CreateDirectionalCap(
        PenLineCap lineCap,
        float thickness,
        Vector2 center,
        Vector2 directionAlongPath,
        bool isStart,
        int maxRoundSegments = MaxTrianglesPerCap)
    {
        if (lineCap == PenLineCap.Flat || !float.IsFinite(thickness) || thickness <= Epsilon)
        {
            return Array.Empty<StrokeJoinTriangle>();
        }

        var length = directionAlongPath.Length();
        if (!float.IsFinite(length) || length <= Epsilon)
        {
            return Array.Empty<StrokeJoinTriangle>();
        }

        var direction = directionAlongPath / length;
        var radius = thickness * 0.5f;
        var outward = isStart ? -direction : direction;
        var normal = new Vector2(-direction.Y, direction.X) * radius;

        return lineCap switch
        {
            PenLineCap.Square => CreateSquareCap(center, outward, normal, radius),
            PenLineCap.Round => CreateRoundCap(center, outward, radius, Math.Clamp(maxRoundSegments, 1, MaxTrianglesPerCap)),
            PenLineCap.Triangle => CreateTriangleCap(center, outward, normal, radius),
            _ => Array.Empty<StrokeJoinTriangle>()
        };
    }

    private static StrokeJoinTriangle[] CreateSquareCap(Vector2 center, Vector2 outward, Vector2 normal, float radius)
    {
        var inner0 = center - normal;
        var inner1 = center + normal;
        var outer0 = center + outward * radius - normal;
        var outer1 = center + outward * radius + normal;

        return new[]
        {
            new StrokeJoinTriangle(inner0, outer0, outer1),
            new StrokeJoinTriangle(inner0, outer1, inner1)
        };
    }

    private static StrokeJoinTriangle[] CreateTriangleCap(Vector2 center, Vector2 outward, Vector2 normal, float radius)
    {
        return new[]
        {
            new StrokeJoinTriangle(center - normal, center + outward * radius, center + normal)
        };
    }

    private static StrokeJoinTriangle[] CreateRoundCap(Vector2 center, Vector2 outward, float radius, int maxSegments)
    {
        var baseAngle = MathF.Atan2(outward.Y, outward.X);
        var start = baseAngle - MathF.PI * 0.5f;
        var sweep = MathF.PI;
        var triangles = new StrokeJoinTriangle[maxSegments];

        for (int i = 0; i < maxSegments; i++)
        {
            var a0 = start + sweep * i / maxSegments;
            var a1 = start + sweep * (i + 1) / maxSegments;
            var p0 = new Vector2(
                center.X + MathF.Cos(a0) * radius,
                center.Y + MathF.Sin(a0) * radius);
            var p1 = new Vector2(
                center.X + MathF.Cos(a1) * radius,
                center.Y + MathF.Sin(a1) * radius);

            triangles[i] = new StrokeJoinTriangle(center, p0, p1);
        }

        return triangles;
    }
}
