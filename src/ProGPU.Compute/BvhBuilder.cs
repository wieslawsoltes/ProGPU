using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Vector;
using ProGPU.Text;

namespace ProGPU.Compute;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct GpuLineSegment
{
    public Vector2 Start;
    public Vector2 End;

    public GpuLineSegment(Vector2 start, Vector2 end)
    {
        Start = start;
        End = end;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuBvhNode
{
    public Vector2 MinBounds;
    public Vector2 MaxBounds;
    public uint LeftChildOrFirstLine;
    public uint PrimitiveCount;
    public uint RightChild;
    public uint Pad1;
}

public static class BvhBuilder
{
    private const float FlatnessToleranceSq = 0.25f * 0.25f;
#if false
#endif


    public static List<GpuLineSegment> FlattenPath(PathGeometry path)
    {
        var lines = new List<GpuLineSegment>();

        foreach (var figure in path.Figures)
        {
            if (figure.Segments.Count == 0) continue;

            Vector2 currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    lines.Add(new GpuLineSegment(currentPoint, line.Point));
                    currentPoint = line.Point;
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    FlattenQuadratic(currentPoint, quad.ControlPoint, quad.Point, lines);
                    currentPoint = quad.Point;
                }
                else if (segment is CubicBezierSegment cubic)
                {
                    FlattenCubic(currentPoint, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, lines);
                    currentPoint = cubic.Point;
                }
            }

            if (figure.IsClosed && currentPoint != figure.StartPoint)
            {
                lines.Add(new GpuLineSegment(currentPoint, figure.StartPoint));
            }
        }

        return lines;
    }

    public static List<GpuLineSegment> FlattenGlyph(TtfFont font, ushort glyphId)
    {
        var lines = new List<GpuLineSegment>();
        var outline = font.GetGlyphOutline(glyphId);
        if (outline == null) return lines;

        foreach (var figure in outline.Figures)
        {
            if (figure.Segments.Count == 0) continue;

            Vector2 currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    lines.Add(new GpuLineSegment(currentPoint, line.Point));
                    currentPoint = line.Point;
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    FlattenQuadratic(currentPoint, quad.ControlPoint, quad.Point, lines);
                    currentPoint = quad.Point;
                }
            }

            if (figure.IsClosed && currentPoint != figure.StartPoint)
            {
                lines.Add(new GpuLineSegment(currentPoint, figure.StartPoint));
            }
        }

        return lines;
    }

    private static void FlattenQuadratic(Vector2 p0, Vector2 p1, Vector2 p2, List<GpuLineSegment> lines)
    {
        Vector2 ab = p2 - p0;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f)
        {
            lines.Add(new GpuLineSegment(p0, p2));
            return;
        }

        Vector2 ap = p1 - p0;
        float t = Math.Clamp(Vector2.Dot(ap, ab) / abLenSq, 0f, 1f);
        Vector2 proj = p0 + t * ab;
        float distSq = (p1 - proj).LengthSquared();

        if (distSq <= FlatnessToleranceSq)
        {
            lines.Add(new GpuLineSegment(p0, p2));
        }
        else
        {
            Vector2 q0 = (p0 + p1) * 0.5f;
            Vector2 q1 = (p1 + p2) * 0.5f;
            Vector2 r = (q0 + q1) * 0.5f;

            FlattenQuadratic(p0, q0, r, lines);
            FlattenQuadratic(r, q1, p2, lines);
        }
    }

    private static void FlattenCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, List<GpuLineSegment> lines)
    {
        Vector2 ab = p3 - p0;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f)
        {
            lines.Add(new GpuLineSegment(p0, p3));
            return;
        }

        Vector2 ap1 = p1 - p0;
        float t1 = Math.Clamp(Vector2.Dot(ap1, ab) / abLenSq, 0f, 1f);
        float dist1Sq = (p1 - (p0 + t1 * ab)).LengthSquared();

        Vector2 ap2 = p2 - p0;
        float t2 = Math.Clamp(Vector2.Dot(ap2, ab) / abLenSq, 0f, 1f);
        float dist2Sq = (p2 - (p0 + t2 * ab)).LengthSquared();

        if (dist1Sq <= FlatnessToleranceSq && dist2Sq <= FlatnessToleranceSq)
        {
            lines.Add(new GpuLineSegment(p0, p3));
        }
        else
        {
            Vector2 q0 = (p0 + p1) * 0.5f;
            Vector2 q1 = (p1 + p2) * 0.5f;
            Vector2 q2 = (p2 + p3) * 0.5f;
            Vector2 r0 = (q0 + q1) * 0.5f;
            Vector2 r1 = (q1 + q2) * 0.5f;
            Vector2 s = (r0 + r1) * 0.5f;

            FlattenCubic(p0, q0, r0, s, lines);
            FlattenCubic(s, r1, q2, p3, lines);
        }
    }

    public static List<GpuBvhNode> BuildBvh(List<GpuLineSegment> segments, out List<GpuLineSegment> orderedSegments)
    {
        var nodes = new List<GpuBvhNode>();
        orderedSegments = new List<GpuLineSegment>();

        if (segments.Count == 0)
        {
            return nodes;
        }

        BuildRecursive(segments, 0, segments.Count, nodes, orderedSegments);
        return nodes;
    }

    private static int BuildRecursive(List<GpuLineSegment> segments, int start, int end, List<GpuBvhNode> nodes, List<GpuLineSegment> orderedSegments)
    {
        int nodeIdx = nodes.Count;
        nodes.Add(new GpuBvhNode());

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = start; i < end; i++)
        {
            var s = segments[i];
            minX = Math.Min(minX, Math.Min(s.Start.X, s.End.X));
            minY = Math.Min(minY, Math.Min(s.Start.Y, s.End.Y));
            maxX = Math.Max(maxX, Math.Max(s.Start.X, s.End.X));
            maxY = Math.Max(maxY, Math.Max(s.Start.Y, s.End.Y));
        }

        int count = end - start;
        const int MaxPrimitivesPerLeaf = 4;

        if (count <= MaxPrimitivesPerLeaf)
        {
            uint firstLineIdx = (uint)orderedSegments.Count;
            for (int i = start; i < end; i++)
            {
                orderedSegments.Add(segments[i]);
            }

            nodes[nodeIdx] = new GpuBvhNode
            {
                MinBounds = new Vector2(minX, minY),
                MaxBounds = new Vector2(maxX, maxY),
                LeftChildOrFirstLine = firstLineIdx,
                PrimitiveCount = (uint)count
            };
        }
        else
        {
            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            int axis = (sizeX > sizeY) ? 0 : 1;

            segments.Sort(start, count, Comparer<GpuLineSegment>.Create((s1, s2) =>
            {
                float c1 = (axis == 0) ? (s1.Start.X + s1.End.X) * 0.5f : (s1.Start.Y + s1.End.Y) * 0.5f;
                float c2 = (axis == 0) ? (s2.Start.X + s2.End.X) * 0.5f : (s2.Start.Y + s2.End.Y) * 0.5f;
                return c1.CompareTo(c2);
            }));

            int mid = start + count / 2;

            int leftChild = BuildRecursive(segments, start, mid, nodes, orderedSegments);
            int rightChild = BuildRecursive(segments, mid, end, nodes, orderedSegments);

            nodes[nodeIdx] = new GpuBvhNode
            {
                MinBounds = new Vector2(minX, minY),
                MaxBounds = new Vector2(maxX, maxY),
                LeftChildOrFirstLine = (uint)leftChild,
                PrimitiveCount = 0,
                RightChild = (uint)rightChild
            };
        }

        return nodeIdx;
    }
}
