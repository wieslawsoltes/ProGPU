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

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuBezierCurve
{
    public Vector2 P0;
    public Vector2 P1;
    public Vector2 P2;
    public Vector2 P3;
    public uint CurveType;      // 0 = Line, 1 = Quadratic, 2 = Cubic
    public uint Subdivisions;   // Populated during BVH construction
    public uint LineOffset;     // Populated during BVH construction
    public uint Pad;

    public GpuBezierCurve(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, uint curveType)
    {
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
        CurveType = curveType;
        Subdivisions = 0;
        LineOffset = 0;
        Pad = 0;
    }
}

public static class BvhBuilder
{
    public static (Vector2 Min, Vector2 Max) GetCurveBounds(in GpuBezierCurve curve)
    {
        if (curve.CurveType == 0) // Line
        {
            return (Vector2.Min(curve.P0, curve.P1), Vector2.Max(curve.P0, curve.P1));
        }
        else if (curve.CurveType == 1) // Quadratic Bezier
        {
            var min = Vector2.Min(curve.P0, Vector2.Min(curve.P1, curve.P2));
            var max = Vector2.Max(curve.P0, Vector2.Max(curve.P1, curve.P2));
            return (min, max);
        }
        else // Cubic Bezier
        {
            var min = Vector2.Min(curve.P0, Vector2.Min(curve.P1, Vector2.Min(curve.P2, curve.P3)));
            var max = Vector2.Max(curve.P0, Vector2.Max(curve.P1, Vector2.Max(curve.P2, curve.P3)));
            return (min, max);
        }
    }

    public static List<GpuBezierCurve> GetPathCurves(PathGeometry path)
    {
        var curves = new List<GpuBezierCurve>();

        foreach (var figure in path.Figures)
        {
            if (figure.Segments.Count == 0) continue;

            Vector2 currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    curves.Add(new GpuBezierCurve(currentPoint, line.Point, Vector2.Zero, Vector2.Zero, 0));
                    currentPoint = line.Point;
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    curves.Add(new GpuBezierCurve(currentPoint, quad.ControlPoint, quad.Point, Vector2.Zero, 1));
                    currentPoint = quad.Point;
                }
                else if (segment is CubicBezierSegment cubic)
                {
                    curves.Add(new GpuBezierCurve(currentPoint, cubic.ControlPoint1, cubic.ControlPoint2, cubic.Point, 2));
                    currentPoint = cubic.Point;
                }
            }

            if (figure.IsClosed && currentPoint != figure.StartPoint)
            {
                curves.Add(new GpuBezierCurve(currentPoint, figure.StartPoint, Vector2.Zero, Vector2.Zero, 0));
            }
        }

        return curves;
    }

    public static List<GpuBezierCurve> GetGlyphCurves(TtfFont font, ushort glyphId)
    {
        var curves = new List<GpuBezierCurve>();
        var outline = font.GetGlyphOutline(glyphId);
        if (outline == null) return curves;

        foreach (var figure in outline.Figures)
        {
            if (figure.Segments.Count == 0) continue;

            Vector2 currentPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is LineSegment line)
                {
                    curves.Add(new GpuBezierCurve(currentPoint, line.Point, Vector2.Zero, Vector2.Zero, 0));
                    currentPoint = line.Point;
                }
                else if (segment is QuadraticBezierSegment quad)
                {
                    curves.Add(new GpuBezierCurve(currentPoint, quad.ControlPoint, quad.Point, Vector2.Zero, 1));
                    currentPoint = quad.Point;
                }
            }

            if (figure.IsClosed && currentPoint != figure.StartPoint)
            {
                curves.Add(new GpuBezierCurve(currentPoint, figure.StartPoint, Vector2.Zero, Vector2.Zero, 0));
            }
        }

        return curves;
    }

    public static List<GpuBvhNode> BuildBvh(List<GpuBezierCurve> curves, out List<GpuBezierCurve> orderedCurves, out uint totalLines)
    {
        var nodes = new List<GpuBvhNode>();
        orderedCurves = new List<GpuBezierCurve>();

        if (curves.Count == 0)
        {
            totalLines = 0;
            return nodes;
        }

        int globalLineCount = 0;
        BuildRecursive(curves, 0, curves.Count, nodes, orderedCurves, ref globalLineCount);
        totalLines = (uint)globalLineCount;
        return nodes;
    }

    private static int BuildRecursive(List<GpuBezierCurve> curves, int start, int end, List<GpuBvhNode> nodes, List<GpuBezierCurve> orderedCurves, ref int globalLineCount)
    {
        int nodeIdx = nodes.Count;
        nodes.Add(new GpuBvhNode());

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        for (int i = start; i < end; i++)
        {
            var (cMin, cMax) = GetCurveBounds(curves[i]);
            minX = Math.Min(minX, cMin.X);
            minY = Math.Min(minY, cMin.Y);
            maxX = Math.Max(maxX, cMax.X);
            maxY = Math.Max(maxY, cMax.Y);
        }

        int count = end - start;
        const int MaxPrimitivesPerLeaf = 4;

        if (count <= MaxPrimitivesPerLeaf)
        {
            uint firstLineIdx = (uint)globalLineCount;
            uint totalLinesInLeaf = 0;
            for (int i = start; i < end; i++)
            {
                var curve = curves[i];
                uint subs = curve.CurveType switch
                {
                    0 => 1u,   // Line
                    1 => 12u,  // Quadratic
                    2 => 16u,  // Cubic
                    _ => 1u
                };
                curve.Subdivisions = subs;
                curve.LineOffset = (uint)globalLineCount;
                orderedCurves.Add(curve);
                globalLineCount += (int)subs;
                totalLinesInLeaf += subs;
            }

            nodes[nodeIdx] = new GpuBvhNode
            {
                MinBounds = new Vector2(minX, minY),
                MaxBounds = new Vector2(maxX, maxY),
                LeftChildOrFirstLine = firstLineIdx,
                PrimitiveCount = totalLinesInLeaf
            };
        }
        else
        {
            float sizeX = maxX - minX;
            float sizeY = maxY - minY;
            int axis = (sizeX > sizeY) ? 0 : 1;

            curves.Sort(start, count, Comparer<GpuBezierCurve>.Create((c1, c2) =>
            {
                var (min1, max1) = GetCurveBounds(c1);
                var (min2, max2) = GetCurveBounds(c2);
                float center1 = (axis == 0) ? (min1.X + max1.X) * 0.5f : (min1.Y + max1.Y) * 0.5f;
                float center2 = (axis == 0) ? (min2.X + max2.X) * 0.5f : (min2.Y + max2.Y) * 0.5f;
                return center1.CompareTo(center2);
            }));

            int mid = start + count / 2;

            int leftChild = BuildRecursive(curves, start, mid, nodes, orderedCurves, ref globalLineCount);
            int rightChild = BuildRecursive(curves, mid, end, nodes, orderedCurves, ref globalLineCount);

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
