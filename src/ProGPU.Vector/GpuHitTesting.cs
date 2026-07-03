using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Vector;

public enum GpuHitTestPrimitiveKind : uint
{
    AxisAlignedBounds = 0,
    RectangleFill = 1,
    RectangleStroke = 2,
    EllipseFill = 3,
    EllipseStroke = 4,
    LineStroke = 5,
    PathFill = 6,
    PathStroke = 7
}

public enum GpuHitTestIntersectionDetail : uint
{
    NotCalculated = 0,
    Empty = 1,
    FullyInside = 2,
    FullyContains = 3,
    Intersects = 4
}

[Flags]
public enum GpuHitTestPrimitiveFlags : uint
{
    None = 0,
    Visible = 1 << 0,
    HitTestVisible = 1 << 1
}

[StructLayout(LayoutKind.Sequential, Size = 128)]
public readonly struct GpuHitTestPrimitive
{
    public readonly Vector2 BoundsMin;
    public readonly Vector2 BoundsMax;
    public readonly Vector4 Data0;
    public readonly Vector4 Data1;
    public readonly Vector4 Data2;
    public readonly Vector4 InverseTransform0;
    public readonly Vector4 InverseTransform1;
    public readonly GpuHitTestPrimitiveKind Kind;
    public readonly GpuHitTestPrimitiveFlags Flags;
    public readonly int Id;
    public readonly float ZIndex;
    public readonly uint ClipStartSegment;
    public readonly uint ClipSegmentCount;
    public readonly uint ClipFillRule;
    public readonly uint ClipFlags;

    public GpuHitTestPrimitive(
        GpuHitTestPrimitiveKind kind,
        int id,
        Vector2 boundsMin,
        Vector2 boundsMax,
        Vector4 data0,
        Vector4 data1,
        Vector4 data2,
        Vector4 inverseTransform0,
        Vector4 inverseTransform1,
        float zIndex,
        GpuHitTestPrimitiveFlags flags = GpuHitTestPrimitiveFlags.Visible | GpuHitTestPrimitiveFlags.HitTestVisible,
        uint clipStartSegment = 0,
        uint clipSegmentCount = 0,
        FillRule clipFillRule = FillRule.Nonzero,
        uint clipFlags = 0)
    {
        Kind = kind;
        Id = id;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        Data0 = data0;
        Data1 = data1;
        Data2 = data2;
        InverseTransform0 = inverseTransform0;
        InverseTransform1 = inverseTransform1;
        ZIndex = zIndex;
        Flags = flags;
        ClipStartSegment = clipStartSegment;
        ClipSegmentCount = clipSegmentCount;
        ClipFillRule = (uint)clipFillRule;
        ClipFlags = clipFlags;
    }

    public GpuHitTestPrimitive WithWorldBounds(Vector2 boundsMin, Vector2 boundsMax)
    {
        return new GpuHitTestPrimitive(
            Kind,
            Id,
            boundsMin,
            boundsMax,
            Data0,
            Data1,
            Data2,
            InverseTransform0,
            InverseTransform1,
            ZIndex,
            Flags,
            ClipStartSegment,
            ClipSegmentCount,
            (FillRule)ClipFillRule,
            ClipFlags);
    }

    public GpuHitTestPrimitive WithClip(uint startSegment, uint segmentCount, FillRule fillRule)
    {
        return new GpuHitTestPrimitive(
            Kind,
            Id,
            BoundsMin,
            BoundsMax,
            Data0,
            Data1,
            Data2,
            InverseTransform0,
            InverseTransform1,
            ZIndex,
            Flags,
            startSegment,
            segmentCount,
            fillRule,
            segmentCount > 0 ? 1u : 0u);
    }

    public static GpuHitTestPrimitive Bounds(int id, Vector2 min, Vector2 max, float zIndex = 0f)
    {
        return Bounds(id, min, max, Matrix4x4.Identity, zIndex);
    }

    public static GpuHitTestPrimitive Bounds(int id, Vector2 min, Vector2 max, Matrix4x4 transform, float zIndex = 0f)
    {
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.AxisAlignedBounds,
            id,
            TransformBoundsMin(min, max, transform),
            TransformBoundsMax(min, max, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            Vector4.Zero,
            Vector4.Zero,
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive RectangleFill(int id, Vector2 min, Vector2 max, Vector2 radius, float zIndex = 0f)
    {
        return RectangleFill(id, min, max, radius, Matrix4x4.Identity, zIndex);
    }

    public static GpuHitTestPrimitive RectangleFill(int id, Vector2 min, Vector2 max, Vector2 radius, Matrix4x4 transform, float zIndex = 0f)
    {
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.RectangleFill,
            id,
            TransformBoundsMin(min, max, transform),
            TransformBoundsMax(min, max, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            new Vector4(radius.X, radius.Y, 0f, 0f),
            Vector4.Zero,
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive RectangleStroke(
        int id,
        Vector2 min,
        Vector2 max,
        Vector2 radius,
        float strokeThickness,
        float tolerance = 0f,
        float zIndex = 0f)
    {
        return RectangleStroke(id, min, max, radius, strokeThickness, tolerance, Matrix4x4.Identity, zIndex);
    }

    public static GpuHitTestPrimitive RectangleStroke(
        int id,
        Vector2 min,
        Vector2 max,
        Vector2 radius,
        float strokeThickness,
        float tolerance,
        Matrix4x4 transform,
        float zIndex = 0f)
    {
        float padding = MathF.Max(0f, (MathF.Abs(strokeThickness) * 0.5f) + MathF.Max(0f, tolerance));
        Vector2 paddedMin = min - new Vector2(padding);
        Vector2 paddedMax = max + new Vector2(padding);
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.RectangleStroke,
            id,
            TransformBoundsMin(paddedMin, paddedMax, transform),
            TransformBoundsMax(paddedMin, paddedMax, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            new Vector4(radius.X, radius.Y, strokeThickness, tolerance),
            Vector4.Zero,
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive EllipseFill(int id, Vector2 min, Vector2 max, float zIndex = 0f)
    {
        return EllipseFill(id, min, max, Matrix4x4.Identity, zIndex);
    }

    public static GpuHitTestPrimitive EllipseFill(int id, Vector2 min, Vector2 max, Matrix4x4 transform, float zIndex = 0f)
    {
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.EllipseFill,
            id,
            TransformBoundsMin(min, max, transform),
            TransformBoundsMax(min, max, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            Vector4.Zero,
            CreateEllipseHitTestData(min, max),
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive EllipseStroke(
        int id,
        Vector2 min,
        Vector2 max,
        float strokeThickness,
        float tolerance = 0f,
        float zIndex = 0f)
    {
        return EllipseStroke(id, min, max, strokeThickness, tolerance, Matrix4x4.Identity, zIndex);
    }

    public static GpuHitTestPrimitive EllipseStroke(
        int id,
        Vector2 min,
        Vector2 max,
        float strokeThickness,
        float tolerance,
        Matrix4x4 transform,
        float zIndex = 0f)
    {
        float padding = MathF.Max(0f, (MathF.Abs(strokeThickness) * 0.5f) + MathF.Max(0f, tolerance));
        Vector2 paddedMin = min - new Vector2(padding);
        Vector2 paddedMax = max + new Vector2(padding);
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.EllipseStroke,
            id,
            TransformBoundsMin(paddedMin, paddedMax, transform),
            TransformBoundsMax(paddedMin, paddedMax, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            new Vector4(strokeThickness, tolerance, 0f, 0f),
            CreateEllipseHitTestData(min, max),
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive LineStroke(
        int id,
        Vector2 start,
        Vector2 end,
        float strokeThickness,
        LineGeometryCap startCap,
        LineGeometryCap endCap,
        float tolerance = 0f,
        float zIndex = 0f)
    {
        return LineStroke(id, start, end, strokeThickness, startCap, endCap, tolerance, Matrix4x4.Identity, zIndex);
    }

    public static GpuHitTestPrimitive LineStroke(
        int id,
        Vector2 start,
        Vector2 end,
        float strokeThickness,
        LineGeometryCap startCap,
        LineGeometryCap endCap,
        float tolerance,
        Matrix4x4 transform,
        float zIndex = 0f)
    {
        float padding = MathF.Max(0f, (MathF.Abs(strokeThickness) * 0.5f) + MathF.Max(0f, tolerance));
        Vector2 min = Vector2.Min(start, end) - new Vector2(padding);
        Vector2 max = Vector2.Max(start, end) + new Vector2(padding);
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.LineStroke,
            id,
            TransformBoundsMin(min, max, transform),
            TransformBoundsMax(min, max, transform),
            new Vector4(start.X, start.Y, end.X, end.Y),
            new Vector4(strokeThickness, tolerance, (uint)startCap, (uint)endCap),
            CreateLineStrokeHitTestData(start, end),
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive PathFill(
        int id,
        Vector2 min,
        Vector2 max,
        uint startSegment,
        uint segmentCount,
        FillRule fillRule,
        Matrix4x4 transform,
        float zIndex = 0f)
    {
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.PathFill,
            id,
            TransformBoundsMin(min, max, transform),
            TransformBoundsMax(min, max, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            new Vector4(startSegment, segmentCount, (uint)fillRule, 0f),
            Vector4.Zero,
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    public static GpuHitTestPrimitive PathStroke(
        int id,
        Vector2 min,
        Vector2 max,
        uint startSegment,
        uint segmentCount,
        float strokeThickness,
        float tolerance,
        Matrix4x4 transform,
        float zIndex = 0f)
    {
        float padding = MathF.Max(0f, (MathF.Abs(strokeThickness) * 0.5f) + MathF.Max(0f, tolerance));
        Vector2 paddedMin = min - new Vector2(padding);
        Vector2 paddedMax = max + new Vector2(padding);
        return new GpuHitTestPrimitive(
            GpuHitTestPrimitiveKind.PathStroke,
            id,
            TransformBoundsMin(paddedMin, paddedMax, transform),
            TransformBoundsMax(paddedMin, paddedMax, transform),
            new Vector4(min.X, min.Y, max.X, max.Y),
            new Vector4(startSegment, segmentCount, strokeThickness, tolerance),
            Vector4.Zero,
            CreateInverseTransformRow0(transform),
            CreateInverseTransformRow1(transform),
            zIndex);
    }

    private static Vector2 TransformBoundsMin(Vector2 min, Vector2 max, Matrix4x4 transform)
    {
        GetTransformedBounds(min, max, transform, out Vector2 transformedMin, out _);
        return transformedMin;
    }

    private static Vector4 CreateEllipseHitTestData(Vector2 min, Vector2 max)
    {
        Vector2 radii = (max - min) * 0.5f;
        Vector2 center = (min + max) * 0.5f;
        float inverseRadiusX = float.IsFinite(radii.X) && MathF.Abs(radii.X) > 0.0001f ? 1f / radii.X : 0f;
        float inverseRadiusY = float.IsFinite(radii.Y) && MathF.Abs(radii.Y) > 0.0001f ? 1f / radii.Y : 0f;
        return new Vector4(center.X, center.Y, inverseRadiusX, inverseRadiusY);
    }

    private static Vector4 CreateLineStrokeHitTestData(Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float length = segment.Length();
        Vector2 direction = float.IsFinite(length) && length > 0.0001f
            ? segment / length
            : Vector2.Zero;
        float cachedLength = float.IsFinite(length) ? length : 0f;
        return new Vector4(direction.X, direction.Y, cachedLength, 0f);
    }

    private static Vector2 TransformBoundsMax(Vector2 min, Vector2 max, Matrix4x4 transform)
    {
        GetTransformedBounds(min, max, transform, out _, out Vector2 transformedMax);
        return transformedMax;
    }

    private static void GetTransformedBounds(Vector2 min, Vector2 max, Matrix4x4 transform, out Vector2 transformedMin, out Vector2 transformedMax)
    {
        Vector2 p0 = Vector2.Transform(min, transform);
        Vector2 p1 = Vector2.Transform(new Vector2(max.X, min.Y), transform);
        Vector2 p2 = Vector2.Transform(max, transform);
        Vector2 p3 = Vector2.Transform(new Vector2(min.X, max.Y), transform);
        transformedMin = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3));
        transformedMax = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
    }

    private static Vector4 CreateInverseTransformRow0(Matrix4x4 transform)
    {
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
        {
            inverse = Matrix4x4.Identity;
        }

        return new Vector4(inverse.M11, inverse.M21, inverse.M41, 0f);
    }

    private static Vector4 CreateInverseTransformRow1(Matrix4x4 transform)
    {
        if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
        {
            inverse = Matrix4x4.Identity;
        }

        return new Vector4(inverse.M12, inverse.M22, inverse.M42, 0f);
    }
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public readonly struct GpuHitTestNode
{
    public readonly Vector2 BoundsMin;
    public readonly Vector2 BoundsMax;
    public readonly uint FirstChild;
    public readonly uint ChildCount;
    public readonly uint FirstPrimitive;
    public readonly uint PrimitiveCount;

    public GpuHitTestNode(
        Vector2 boundsMin,
        Vector2 boundsMax,
        uint firstChild,
        uint childCount,
        uint firstPrimitive,
        uint primitiveCount)
    {
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        FirstChild = firstChild;
        ChildCount = childCount;
        FirstPrimitive = firstPrimitive;
        PrimitiveCount = primitiveCount;
    }
}

[StructLayout(LayoutKind.Sequential, Size = 40)]
public struct GpuHitTestQuery
{
    public Vector2 Point;
    public Vector2 RegionMax;
    public uint RootNodeIndex;
    public uint PrimitiveCount;
    public uint NodeCount;
    public uint PrimitiveIndexCount;
    public uint Flags;
    public uint PathSegmentCount;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct GpuHitTestResult
{
    public uint Hit;
    public int Id;
    public uint PrimitiveIndex;
    public float ZIndex;
    public uint CandidateCount;
    public uint NodesVisited;
    public uint PreciseTests;
    public uint IntersectionDetail;

    public readonly bool HasHit => Hit != 0;
}

public sealed class GpuHitTestIndex
{
    private GpuHitTestIndex(
        GpuHitTestPrimitive[] primitives,
        GpuHitTestNode[] nodes,
        uint[] primitiveIndices,
        GpuPathSegment[] pathSegments)
    {
        PrimitiveArray = primitives;
        NodeArray = nodes;
        PrimitiveIndexArray = primitiveIndices;
        PathSegmentArray = pathSegments;
        Primitives = primitives;
        Nodes = nodes;
        PrimitiveIndices = primitiveIndices;
        PathSegments = pathSegments;
    }

    public IReadOnlyList<GpuHitTestPrimitive> Primitives { get; }
    public IReadOnlyList<GpuHitTestNode> Nodes { get; }
    public IReadOnlyList<uint> PrimitiveIndices { get; }
    public IReadOnlyList<GpuPathSegment> PathSegments { get; }
    internal GpuHitTestPrimitive[] PrimitiveArray { get; }
    internal GpuHitTestNode[] NodeArray { get; }
    internal uint[] PrimitiveIndexArray { get; }
    internal GpuPathSegment[] PathSegmentArray { get; }

    public static GpuHitTestIndex Build(
        ReadOnlySpan<GpuHitTestPrimitive> primitives,
        int maxDepth = 8,
        int maxPrimitivesPerNode = 32)
    {
        return Build(primitives, ReadOnlySpan<GpuPathSegment>.Empty, maxDepth, maxPrimitivesPerNode);
    }

    public static GpuHitTestIndex Build(
        ReadOnlySpan<GpuHitTestPrimitive> primitives,
        ReadOnlySpan<GpuPathSegment> pathSegments,
        int maxDepth = 8,
        int maxPrimitivesPerNode = 32)
    {
        if (maxDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        if (maxPrimitivesPerNode <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPrimitivesPerNode));
        }

        var primitiveArray = primitives.ToArray();
        var pathSegmentArray = pathSegments.ToArray();
        if (primitiveArray.Length == 0)
        {
            return new GpuHitTestIndex(
                primitiveArray,
                [new GpuHitTestNode(Vector2.Zero, Vector2.Zero, 0, 0, 0, 0)],
                [],
                pathSegmentArray);
        }

        Vector2 min = primitiveArray[0].BoundsMin;
        Vector2 max = primitiveArray[0].BoundsMax;
        for (int i = 1; i < primitiveArray.Length; i++)
        {
            min = Vector2.Min(min, primitiveArray[i].BoundsMin);
            max = Vector2.Max(max, primitiveArray[i].BoundsMax);
        }

        var builder = new Builder(primitiveArray, maxDepth, maxPrimitivesPerNode);
        builder.AddRootNode(min, max);
        return new GpuHitTestIndex(
            primitiveArray,
            builder.Nodes.ToArray(),
            builder.PrimitiveIndices.ToArray(),
            pathSegmentArray);
    }

    private sealed class Builder
    {
        private readonly GpuHitTestPrimitive[] _primitives;
        private readonly int _maxDepth;
        private readonly int _maxPrimitivesPerNode;

        public Builder(GpuHitTestPrimitive[] primitives, int maxDepth, int maxPrimitivesPerNode)
        {
            _primitives = primitives;
            _maxDepth = maxDepth;
            _maxPrimitivesPerNode = maxPrimitivesPerNode;
        }

        public List<GpuHitTestNode> Nodes { get; } = [];
        public List<uint> PrimitiveIndices { get; } = [];

        public int AddRootNode(Vector2 min, Vector2 max)
        {
            int nodeIndex = Nodes.Count;
            Nodes.Add(default);
            FillNode(nodeIndex, min, max, new RootPrimitiveIndices(_primitives.Length), depth: 0);
            return nodeIndex;
        }

        private void FillNode<TPrimitiveIndices>(
            int nodeIndex,
            Vector2 min,
            Vector2 max,
            TPrimitiveIndices primitiveIndices,
            int depth)
            where TPrimitiveIndices : struct, IPrimitiveIndexSource
        {
            int primitiveCount = primitiveIndices.Count;
            if (depth >= _maxDepth || primitiveCount <= _maxPrimitivesPerNode || min == max)
            {
                WriteLeaf(nodeIndex, min, max, primitiveIndices);
                return;
            }

            Vector2 center = (min + max) * 0.5f;
            List<int>? retained = null;
            List<int>? child0 = null;
            List<int>? child1 = null;
            List<int>? child2 = null;
            List<int>? child3 = null;

            for (int i = 0; i < primitiveCount; i++)
            {
                int primitiveIndex = primitiveIndices[i];
                var primitive = _primitives[primitiveIndex];
                int childIndex = FindContainingChild(primitive.BoundsMin, primitive.BoundsMax, min, max, center);
                if (childIndex >= 0)
                {
                    AddChildPrimitive(ref child0, ref child1, ref child2, ref child3, childIndex, primitiveIndex);
                }
                else
                {
                    retained ??= [];
                    retained.Add(primitiveIndex);
                }
            }

            int childCount = CountNonEmpty(child0, child1, child2, child3);
            int retainedCount = retained?.Count ?? 0;
            if (childCount == 0 || childCount == 1 && retainedCount == 0 && FirstNonEmpty(child0, child1, child2, child3)!.Count == primitiveCount)
            {
                WriteLeaf(nodeIndex, min, max, primitiveIndices);
                return;
            }

            uint firstPrimitive = (uint)PrimitiveIndices.Count;
            if (retained != null)
            {
                foreach (int retainedPrimitive in retained)
                {
                    PrimitiveIndices.Add((uint)retainedPrimitive);
                }
            }

            uint firstChild = (uint)Nodes.Count;
            int child0NodeIndex = AddChildNodeSlot(child0);
            int child1NodeIndex = AddChildNodeSlot(child1);
            int child2NodeIndex = AddChildNodeSlot(child2);
            int child3NodeIndex = AddChildNodeSlot(child3);

            Nodes[nodeIndex] = new GpuHitTestNode(
                min,
                max,
                firstChild,
                (uint)childCount,
                firstPrimitive,
                (uint)retainedCount);

            FillChildNode(child0NodeIndex, 0, child0, min, max, center, depth);
            FillChildNode(child1NodeIndex, 1, child1, min, max, center, depth);
            FillChildNode(child2NodeIndex, 2, child2, min, max, center, depth);
            FillChildNode(child3NodeIndex, 3, child3, min, max, center, depth);
        }

        private void WriteLeaf<TPrimitiveIndices>(int nodeIndex, Vector2 min, Vector2 max, TPrimitiveIndices primitiveIndices)
            where TPrimitiveIndices : struct, IPrimitiveIndexSource
        {
            uint firstPrimitive = (uint)PrimitiveIndices.Count;
            int primitiveCount = primitiveIndices.Count;
            for (int i = 0; i < primitiveCount; i++)
            {
                PrimitiveIndices.Add((uint)primitiveIndices[i]);
            }

            Nodes[nodeIndex] = new GpuHitTestNode(
                min,
                max,
                firstChild: 0,
                childCount: 0,
                firstPrimitive,
                (uint)primitiveCount);
        }

        private static void AddChildPrimitive(
            ref List<int>? child0,
            ref List<int>? child1,
            ref List<int>? child2,
            ref List<int>? child3,
            int childIndex,
            int primitiveIndex)
        {
            switch (childIndex)
            {
                case 0:
                    child0 ??= [];
                    child0.Add(primitiveIndex);
                    break;
                case 1:
                    child1 ??= [];
                    child1.Add(primitiveIndex);
                    break;
                case 2:
                    child2 ??= [];
                    child2.Add(primitiveIndex);
                    break;
                default:
                    child3 ??= [];
                    child3.Add(primitiveIndex);
                    break;
            }
        }

        private int AddChildNodeSlot(List<int>? childPrimitives)
        {
            if (childPrimitives is not { Count: > 0 })
            {
                return -1;
            }

            int childNodeIndex = Nodes.Count;
            Nodes.Add(default);
            return childNodeIndex;
        }

        private void FillChildNode(
            int childNodeIndex,
            int childIndex,
            List<int>? childPrimitives,
            Vector2 min,
            Vector2 max,
            Vector2 center,
            int depth)
        {
            if (childNodeIndex < 0 || childPrimitives is not { Count: > 0 })
            {
                return;
            }

            var bounds = GetChildBounds(childIndex, min, max, center);
            FillNode(childNodeIndex, bounds.Min, bounds.Max, new ListPrimitiveIndices(childPrimitives), depth + 1);
        }

        private interface IPrimitiveIndexSource
        {
            int Count { get; }

            int this[int index] { get; }
        }

        private readonly struct RootPrimitiveIndices : IPrimitiveIndexSource
        {
            public RootPrimitiveIndices(int count)
            {
                Count = count;
            }

            public int Count { get; }

            public int this[int index] => index;
        }

        private readonly struct ListPrimitiveIndices : IPrimitiveIndexSource
        {
            private readonly List<int> _indices;

            public ListPrimitiveIndices(List<int> indices)
            {
                _indices = indices;
            }

            public int Count => _indices.Count;

            public int this[int index] => _indices[index];
        }

        private static int FindContainingChild(Vector2 primitiveMin, Vector2 primitiveMax, Vector2 nodeMin, Vector2 nodeMax, Vector2 center)
        {
            for (int i = 0; i < 4; i++)
            {
                var child = GetChildBounds(i, nodeMin, nodeMax, center);
                if (primitiveMin.X >= child.Min.X &&
                    primitiveMax.X <= child.Max.X &&
                    primitiveMin.Y >= child.Min.Y &&
                    primitiveMax.Y <= child.Max.Y)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CountNonEmpty(List<int>? child0, List<int>? child1, List<int>? child2, List<int>? child3)
        {
            return (child0 is { Count: > 0 } ? 1 : 0)
                + (child1 is { Count: > 0 } ? 1 : 0)
                + (child2 is { Count: > 0 } ? 1 : 0)
                + (child3 is { Count: > 0 } ? 1 : 0);
        }

        private static List<int>? FirstNonEmpty(List<int>? child0, List<int>? child1, List<int>? child2, List<int>? child3)
        {
            return child0 is { Count: > 0 } ? child0 :
                child1 is { Count: > 0 } ? child1 :
                child2 is { Count: > 0 } ? child2 :
                child3 is { Count: > 0 } ? child3 :
                null;
        }

        private static (Vector2 Min, Vector2 Max) GetChildBounds(int index, Vector2 min, Vector2 max, Vector2 center)
        {
            return index switch
            {
                0 => (min, center),
                1 => (new Vector2(center.X, min.Y), new Vector2(max.X, center.Y)),
                2 => (new Vector2(min.X, center.Y), new Vector2(center.X, max.Y)),
                _ => (center, max)
            };
        }
    }
}

public sealed class GpuHitTestDeviceIndex : IDisposable
{
    private static readonly uint QueryBufferSize = checked((uint)Marshal.SizeOf<GpuHitTestQuery>());
    private const uint ResultBufferSize = 32;
    public const int MaxHitResultCount = 256;

    private bool _isDisposed;

    public GpuHitTestDeviceIndex(WgpuContext context, GpuHitTestIndex index)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Index = index ?? throw new ArgumentNullException(nameof(index));

        if (index.PrimitiveArray.Length == 0 ||
            index.NodeArray.Length == 0 ||
            index.PrimitiveIndexArray.Length == 0)
        {
            throw new ArgumentException("A GPU hit-test device index requires at least one primitive and primitive index.", nameof(index));
        }

        PrimitiveCount = checked((uint)index.PrimitiveArray.Length);
        NodeCount = checked((uint)index.NodeArray.Length);
        PrimitiveIndexCount = checked((uint)index.PrimitiveIndexArray.Length);
        PathSegmentCount = checked((uint)index.PathSegmentArray.Length);

        QueryBuffer = new GpuBuffer(context, QueryBufferSize, BufferUsage.Storage | BufferUsage.CopyDst, "GPU Hit Test Query");
        NodeBuffer = new GpuBuffer(
            context,
            checked((uint)(index.NodeArray.Length * Marshal.SizeOf<GpuHitTestNode>())),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "GPU Hit Test Nodes");
        PrimitiveIndexBuffer = new GpuBuffer(
            context,
            checked((uint)(index.PrimitiveIndexArray.Length * sizeof(uint))),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "GPU Hit Test Primitive Indices");
        PrimitiveBuffer = new GpuBuffer(
            context,
            checked((uint)(index.PrimitiveArray.Length * Marshal.SizeOf<GpuHitTestPrimitive>())),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "GPU Hit Test Primitives");
        GpuPathSegment[] pathSegments = index.PathSegmentArray.Length > 0
            ? index.PathSegmentArray
            : [default];
        PathSegmentBuffer = new GpuBuffer(
            context,
            checked((uint)(pathSegments.Length * Marshal.SizeOf<GpuPathSegment>())),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "GPU Hit Test Path Segments");
        ResultBuffer = new GpuBuffer(context, ResultBufferSize, BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc, "GPU Hit Test Result");
        ResultListBuffer = new GpuBuffer(
            context,
            checked((uint)((MaxHitResultCount + 1) * Marshal.SizeOf<GpuHitTestResult>())),
            BufferUsage.Storage | BufferUsage.CopyDst | BufferUsage.CopySrc,
            "GPU Hit Test Result List");

        NodeBuffer.Write(index.NodeArray);
        PrimitiveIndexBuffer.Write(index.PrimitiveIndexArray);
        PrimitiveBuffer.Write(index.PrimitiveArray);
        PathSegmentBuffer.Write(pathSegments);
    }

    public GpuHitTestIndex Index { get; }
    internal WgpuContext Context { get; }
    internal GpuBuffer QueryBuffer { get; }
    internal GpuBuffer NodeBuffer { get; }
    internal GpuBuffer PrimitiveIndexBuffer { get; }
    internal GpuBuffer PrimitiveBuffer { get; }
    internal GpuBuffer PathSegmentBuffer { get; }
    internal GpuBuffer ResultBuffer { get; }
    internal GpuBuffer ResultListBuffer { get; }
    internal uint PrimitiveCount { get; }
    internal uint NodeCount { get; }
    internal uint PrimitiveIndexCount { get; }
    internal uint PathSegmentCount { get; }

    public static bool TryCreate(WgpuContext context, GpuHitTestIndex index, out GpuHitTestDeviceIndex? deviceIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);

        if (index.PrimitiveArray.Length == 0 ||
            index.NodeArray.Length == 0 ||
            index.PrimitiveIndexArray.Length == 0)
        {
            deviceIndex = null;
            return false;
        }

        deviceIndex = new GpuHitTestDeviceIndex(context, index);
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        QueryBuffer.Dispose();
        NodeBuffer.Dispose();
        PrimitiveIndexBuffer.Dispose();
        PrimitiveBuffer.Dispose();
        PathSegmentBuffer.Dispose();
        ResultBuffer.Dispose();
        ResultListBuffer.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}

public static unsafe class GpuHitTestEngine
{
    private const uint QueryModeBoundsFlag = 0x8000_0000u;
    private const uint QueryModeEllipseRegionFlag = 0x4000_0000u;
    private const uint ResultBufferSize = 32;

    public static bool TryHitTestPoint(GpuHitTestIndex index, Vector2 point, out GpuHitTestResult result)
    {
        var context = WgpuContext.Current;
        if (context == null)
        {
            var activeContexts = WgpuContext.ActiveContexts;
            if (activeContexts.Count > 0)
            {
                context = activeContexts[0];
            }
        }

        if (context == null)
        {
            result = default;
            return false;
        }

        return TryHitTestPoint(context, index, point, out result);
    }

    public static bool TryHitTestPoint(WgpuContext context, GpuHitTestIndex index, Vector2 point, out GpuHitTestResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);

        if (!GpuHitTestDeviceIndex.TryCreate(context, index, out GpuHitTestDeviceIndex? deviceIndex) || deviceIndex == null)
        {
            result = default;
            return false;
        }

        using (deviceIndex)
        using (var cache = new RenderPipelineCache(context))
        {
            return TryHitTestPoint(context, cache, deviceIndex, point, out result);
        }
    }

    public static bool TryHitTestPoint(
        WgpuContext context,
        RenderPipelineCache cache,
        GpuHitTestDeviceIndex deviceIndex,
        Vector2 point,
        out GpuHitTestResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(deviceIndex);
        if (!ReferenceEquals(context, deviceIndex.Context))
        {
            throw new ArgumentException("The GPU hit-test device index belongs to a different WebGPU context.", nameof(deviceIndex));
        }

        lock (context.RenderLock)
        {
            var query = new GpuHitTestQuery
            {
                Point = point,
                RegionMax = point,
                RootNodeIndex = 0,
                PrimitiveCount = deviceIndex.PrimitiveCount,
                NodeCount = deviceIndex.NodeCount,
                PrimitiveIndexCount = deviceIndex.PrimitiveIndexCount,
                PathSegmentCount = deviceIndex.PathSegmentCount
            };
            var initialResult = new GpuHitTestResult
            {
                Hit = 0,
                Id = -1,
                PrimitiveIndex = uint.MaxValue,
                ZIndex = float.NegativeInfinity
            };

            deviceIndex.QueryBuffer.WriteSingle(query);
            deviceIndex.ResultBuffer.WriteSingle(initialResult);

            var shader = cache.GetOrCreateShader("GpuHitTesting.Query", ShaderSource, "GpuHitTesting.Query");
            var pipeline = cache.GetOrCreateComputePipeline("GpuHitTesting.Query", shader, "cs_main");
            BindGroupLayout* bindGroupLayout = null;
            BindGroup* bindGroup = null;
            CommandEncoder* encoder = null;
            CommandBuffer* commandBuffer = null;

            try
            {
                bindGroupLayout = context.Wgpu.ComputePipelineGetBindGroupLayout(pipeline, 0);
                var entries = stackalloc BindGroupEntry[6];
                entries[0] = new BindGroupEntry { Binding = 0, Buffer = deviceIndex.QueryBuffer.BufferPtr, Offset = 0, Size = deviceIndex.QueryBuffer.Size };
                entries[1] = new BindGroupEntry { Binding = 1, Buffer = deviceIndex.NodeBuffer.BufferPtr, Offset = 0, Size = deviceIndex.NodeBuffer.Size };
                entries[2] = new BindGroupEntry { Binding = 2, Buffer = deviceIndex.PrimitiveIndexBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PrimitiveIndexBuffer.Size };
                entries[3] = new BindGroupEntry { Binding = 3, Buffer = deviceIndex.PrimitiveBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PrimitiveBuffer.Size };
                entries[4] = new BindGroupEntry { Binding = 4, Buffer = deviceIndex.ResultBuffer.BufferPtr, Offset = 0, Size = deviceIndex.ResultBuffer.Size };
                entries[5] = new BindGroupEntry { Binding = 5, Buffer = deviceIndex.PathSegmentBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PathSegmentBuffer.Size };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = bindGroupLayout,
                    EntryCount = 6,
                    Entries = entries
                };
                bindGroup = context.Wgpu.DeviceCreateBindGroup(context.Device, &bgDesc);
                if (bindGroup == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test bind group.");
                }

                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test Encoder") };
                encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
                SilkMarshal.Free((nint)encoderDesc.Label);
                if (encoder == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test command encoder.");
                }

                var passDesc = new ComputePassDescriptor();
                var pass = context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
                context.Wgpu.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                context.Wgpu.ComputePassEncoderEnd(pass);
                context.Wgpu.ComputePassEncoderRelease(pass);

                var commandBufferDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test Submit") };
                commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
                SilkMarshal.Free((nint)commandBufferDesc.Label);
                if (commandBuffer == null)
                {
                    throw new InvalidOperationException("Failed to finish GPU hit-test command encoder.");
                }

                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                context.Wgpu.CommandBufferRelease(commandBuffer);
                commandBuffer = null;
                context.Wgpu.CommandEncoderRelease(encoder);
                encoder = null;

                byte[] bytes = deviceIndex.ResultBuffer.ReadBytes(0, ResultBufferSize);
                result = MemoryMarshal.Read<GpuHitTestResult>(bytes);
                return result.HasHit;
            }
            finally
            {
                if (commandBuffer != null)
                {
                    context.Wgpu.CommandBufferRelease(commandBuffer);
                }

                if (encoder != null)
                {
                    context.Wgpu.CommandEncoderRelease(encoder);
                }

                if (bindGroup != null)
                {
                    context.Wgpu.BindGroupRelease(bindGroup);
                }

                if (bindGroupLayout != null)
                {
                    context.Wgpu.BindGroupLayoutRelease(bindGroupLayout);
                }
            }
        }
    }

    public static bool TryHitTestPointAll(
        WgpuContext context,
        GpuHitTestIndex index,
        Vector2 point,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);

        if (!GpuHitTestDeviceIndex.TryCreate(context, index, out GpuHitTestDeviceIndex? deviceIndex) || deviceIndex == null)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        using (deviceIndex)
        using (var cache = new RenderPipelineCache(context))
        {
            return TryHitTestPointAll(context, cache, deviceIndex, point, results, out hitCount, out summary);
        }
    }

    public static bool TryHitTestPointAll(
        WgpuContext context,
        RenderPipelineCache cache,
        GpuHitTestDeviceIndex deviceIndex,
        Vector2 point,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(deviceIndex);
        if (!ReferenceEquals(context, deviceIndex.Context))
        {
            throw new ArgumentException("The GPU hit-test device index belongs to a different WebGPU context.", nameof(deviceIndex));
        }

        if (results.IsEmpty)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        int requestedCount = Math.Min(results.Length, GpuHitTestDeviceIndex.MaxHitResultCount);
        uint requestedCountU = checked((uint)requestedCount);
        var query = CreateQuery(deviceIndex, point, point, requestedCountU);

        return TryQueryAll(context, cache, deviceIndex, query, results, out hitCount, out summary);
    }

    public static bool TryQueryBoundsAll(
        WgpuContext context,
        GpuHitTestIndex index,
        Vector2 min,
        Vector2 max,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);

        if (!GpuHitTestDeviceIndex.TryCreate(context, index, out GpuHitTestDeviceIndex? deviceIndex) || deviceIndex == null)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        using (deviceIndex)
        using (var cache = new RenderPipelineCache(context))
        {
            return TryQueryBoundsAll(context, cache, deviceIndex, min, max, results, out hitCount, out summary);
        }
    }

    public static bool TryQueryBoundsAll(
        WgpuContext context,
        RenderPipelineCache cache,
        GpuHitTestDeviceIndex deviceIndex,
        Vector2 min,
        Vector2 max,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(deviceIndex);
        if (!ReferenceEquals(context, deviceIndex.Context))
        {
            throw new ArgumentException("The GPU hit-test device index belongs to a different WebGPU context.", nameof(deviceIndex));
        }

        if (results.IsEmpty)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        int requestedCount = Math.Min(results.Length, GpuHitTestDeviceIndex.MaxHitResultCount);
        uint requestedCountU = checked((uint)requestedCount);
        var query = CreateQuery(deviceIndex, min, max, QueryModeBoundsFlag | requestedCountU);

        return TryQueryAll(context, cache, deviceIndex, query, results, out hitCount, out summary);
    }

    public static bool TryQueryEllipseAll(
        WgpuContext context,
        GpuHitTestIndex index,
        Vector2 min,
        Vector2 max,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(index);

        if (!GpuHitTestDeviceIndex.TryCreate(context, index, out GpuHitTestDeviceIndex? deviceIndex) || deviceIndex == null)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        using (deviceIndex)
        using (var cache = new RenderPipelineCache(context))
        {
            return TryQueryEllipseAll(context, cache, deviceIndex, min, max, results, out hitCount, out summary);
        }
    }

    public static bool TryQueryEllipseAll(
        WgpuContext context,
        RenderPipelineCache cache,
        GpuHitTestDeviceIndex deviceIndex,
        Vector2 min,
        Vector2 max,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(deviceIndex);
        if (!ReferenceEquals(context, deviceIndex.Context))
        {
            throw new ArgumentException("The GPU hit-test device index belongs to a different WebGPU context.", nameof(deviceIndex));
        }

        if (results.IsEmpty)
        {
            hitCount = 0;
            summary = default;
            return false;
        }

        int requestedCount = Math.Min(results.Length, GpuHitTestDeviceIndex.MaxHitResultCount);
        uint requestedCountU = checked((uint)requestedCount);
        var query = CreateQuery(deviceIndex, min, max, QueryModeBoundsFlag | QueryModeEllipseRegionFlag | requestedCountU);

        return TryQueryAll(context, cache, deviceIndex, query, results, out hitCount, out summary);
    }

    private static GpuHitTestQuery CreateQuery(
        GpuHitTestDeviceIndex deviceIndex,
        Vector2 point,
        Vector2 regionMax,
        uint flags)
    {
        return new GpuHitTestQuery
        {
            Point = point,
            RegionMax = regionMax,
            RootNodeIndex = 0,
            PrimitiveCount = deviceIndex.PrimitiveCount,
            NodeCount = deviceIndex.NodeCount,
            PrimitiveIndexCount = deviceIndex.PrimitiveIndexCount,
            Flags = flags,
            PathSegmentCount = deviceIndex.PathSegmentCount
        };
    }

    private static bool TryQueryAll(
        WgpuContext context,
        RenderPipelineCache cache,
        GpuHitTestDeviceIndex deviceIndex,
        GpuHitTestQuery query,
        Span<GpuHitTestResult> results,
        out int hitCount,
        out GpuHitTestResult summary)
    {
        int requestedCount = Math.Min(results.Length, GpuHitTestDeviceIndex.MaxHitResultCount);
        lock (context.RenderLock)
        {
            int resultSize = Marshal.SizeOf<GpuHitTestResult>();
            int resultBufferElementCount = requestedCount + 1;
            var initialResult = new GpuHitTestResult
            {
                Hit = 0,
                Id = -1,
                PrimitiveIndex = uint.MaxValue,
                ZIndex = float.NegativeInfinity
            };
            GpuHitTestResult[]? rentedInitialResults = null;
            Span<GpuHitTestResult> initialResults = resultBufferElementCount <= 64
                ? stackalloc GpuHitTestResult[resultBufferElementCount]
                : (rentedInitialResults = ArrayPool<GpuHitTestResult>.Shared.Rent(resultBufferElementCount)).AsSpan(0, resultBufferElementCount);
            initialResults.Fill(initialResult);

            deviceIndex.QueryBuffer.WriteSingle(query);
            deviceIndex.ResultListBuffer.Write(initialResults);

            var shader = cache.GetOrCreateShader("GpuHitTesting.Query", ShaderSource, "GpuHitTesting.Query");
            var pipeline = cache.GetOrCreateComputePipeline("GpuHitTesting.Query", shader, "cs_main");
            BindGroupLayout* bindGroupLayout = null;
            BindGroup* bindGroup = null;
            CommandEncoder* encoder = null;
            CommandBuffer* commandBuffer = null;

            try
            {
                bindGroupLayout = context.Wgpu.ComputePipelineGetBindGroupLayout(pipeline, 0);
                var entries = stackalloc BindGroupEntry[6];
                entries[0] = new BindGroupEntry { Binding = 0, Buffer = deviceIndex.QueryBuffer.BufferPtr, Offset = 0, Size = deviceIndex.QueryBuffer.Size };
                entries[1] = new BindGroupEntry { Binding = 1, Buffer = deviceIndex.NodeBuffer.BufferPtr, Offset = 0, Size = deviceIndex.NodeBuffer.Size };
                entries[2] = new BindGroupEntry { Binding = 2, Buffer = deviceIndex.PrimitiveIndexBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PrimitiveIndexBuffer.Size };
                entries[3] = new BindGroupEntry { Binding = 3, Buffer = deviceIndex.PrimitiveBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PrimitiveBuffer.Size };
                entries[4] = new BindGroupEntry { Binding = 4, Buffer = deviceIndex.ResultListBuffer.BufferPtr, Offset = 0, Size = checked((uint)(initialResults.Length * resultSize)) };
                entries[5] = new BindGroupEntry { Binding = 5, Buffer = deviceIndex.PathSegmentBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PathSegmentBuffer.Size };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = bindGroupLayout,
                    EntryCount = 6,
                    Entries = entries
                };
                bindGroup = context.Wgpu.DeviceCreateBindGroup(context.Device, &bgDesc);
                if (bindGroup == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test bind group.");
                }

                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test List Encoder") };
                encoder = context.Wgpu.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
                SilkMarshal.Free((nint)encoderDesc.Label);
                if (encoder == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test command encoder.");
                }

                var passDesc = new ComputePassDescriptor();
                var pass = context.Wgpu.CommandEncoderBeginComputePass(encoder, &passDesc);
                context.Wgpu.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Wgpu.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                context.Wgpu.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                context.Wgpu.ComputePassEncoderEnd(pass);
                context.Wgpu.ComputePassEncoderRelease(pass);

                var commandBufferDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test List Submit") };
                commandBuffer = context.Wgpu.CommandEncoderFinish(encoder, &commandBufferDesc);
                SilkMarshal.Free((nint)commandBufferDesc.Label);
                if (commandBuffer == null)
                {
                    throw new InvalidOperationException("Failed to finish GPU hit-test command encoder.");
                }

                context.Wgpu.QueueSubmit(context.Queue, 1, &commandBuffer);
                context.Wgpu.CommandBufferRelease(commandBuffer);
                commandBuffer = null;
                context.Wgpu.CommandEncoderRelease(encoder);
                encoder = null;

                uint readSize = checked((uint)(initialResults.Length * resultSize));
                byte[] bytes = deviceIndex.ResultListBuffer.ReadBytes(0, readSize);
                summary = MemoryMarshal.Read<GpuHitTestResult>(bytes);
                hitCount = 0;
                for (int i = 0; i < requestedCount; i++)
                {
                    var result = MemoryMarshal.Read<GpuHitTestResult>(bytes.AsSpan((i + 1) * resultSize));
                    if (!result.HasHit)
                    {
                        break;
                    }

                    results[hitCount++] = result;
                }

                return hitCount > 0;
            }
            finally
            {
                if (commandBuffer != null)
                {
                    context.Wgpu.CommandBufferRelease(commandBuffer);
                }

                if (encoder != null)
                {
                    context.Wgpu.CommandEncoderRelease(encoder);
                }

                if (bindGroup != null)
                {
                    context.Wgpu.BindGroupRelease(bindGroup);
                }

                if (bindGroupLayout != null)
                {
                    context.Wgpu.BindGroupLayoutRelease(bindGroupLayout);
                }

                if (rentedInitialResults != null)
                {
                    ArrayPool<GpuHitTestResult>.Shared.Return(rentedInitialResults);
                }
            }
        }
    }

    internal const string ShaderSource = """
struct HitTestQuery {
    point: vec2<f32>,
    region_max: vec2<f32>,
    root_node_index: u32,
    primitive_count: u32,
    node_count: u32,
    primitive_index_count: u32,
    flags: u32,
    path_segment_count: u32,
};

struct HitTestNode {
    bounds_min: vec2<f32>,
    bounds_max: vec2<f32>,
    first_child: u32,
    child_count: u32,
    first_primitive: u32,
    primitive_count: u32,
};

struct HitTestPrimitive {
    bounds_min: vec2<f32>,
    bounds_max: vec2<f32>,
    data0: vec4<f32>,
    data1: vec4<f32>,
    data2: vec4<f32>,
    inverse_transform0: vec4<f32>,
    inverse_transform1: vec4<f32>,
    kind: u32,
    flags: u32,
    id: i32,
    z_index: f32,
    clip_start_segment: u32,
    clip_segment_count: u32,
    clip_fill_rule: u32,
    clip_flags: u32,
};

struct PathSegment {
    p0: vec2<f32>,
    p1: vec2<f32>,
    p2: vec2<f32>,
    p3: vec2<f32>,
    segment_type: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
};

struct HitTestResult {
    hit: u32,
    id: i32,
    primitive_index: u32,
    z_index: f32,
    candidate_count: u32,
    nodes_visited: u32,
    precise_tests: u32,
    intersection_detail: u32,
};

@group(0) @binding(0) var<storage, read> query: HitTestQuery;
@group(0) @binding(1) var<storage, read> nodes: array<HitTestNode>;
@group(0) @binding(2) var<storage, read> primitive_indices: array<u32>;
@group(0) @binding(3) var<storage, read> primitives: array<HitTestPrimitive>;
@group(0) @binding(4) var<storage, read_write> results: array<HitTestResult>;
@group(0) @binding(5) var<storage, read> path_segments: array<PathSegment>;

const FLAG_VISIBLE: u32 = 1u;
const FLAG_HIT_TEST_VISIBLE: u32 = 2u;
const KIND_BOUNDS: u32 = 0u;
const KIND_RECT_FILL: u32 = 1u;
const KIND_RECT_STROKE: u32 = 2u;
const KIND_ELLIPSE_FILL: u32 = 3u;
const KIND_ELLIPSE_STROKE: u32 = 4u;
const KIND_LINE_STROKE: u32 = 5u;
const KIND_PATH_FILL: u32 = 6u;
const KIND_PATH_STROKE: u32 = 7u;
const FILL_RULE_EVEN_ODD: u32 = 0u;
const CAP_FLAT: u32 = 0u;
const CAP_SQUARE: u32 = 1u;
const CAP_ROUND: u32 = 2u;
const CAP_TRIANGLE: u32 = 3u;
const SEGMENT_LINE: u32 = 0u;
const SEGMENT_QUADRATIC: u32 = 1u;
const SEGMENT_CUBIC: u32 = 2u;
const SEGMENT_ARC: u32 = 3u;
const PATH_QUADRATIC_STEPS: u32 = 16u;
const PATH_CUBIC_STEPS: u32 = 24u;
const PATH_ARC_STEPS: u32 = 24u;
const QUERY_MODE_BOUNDS: u32 = 2147483648u;
const QUERY_MODE_ELLIPSE_REGION: u32 = 1073741824u;
const QUERY_RESULT_CAPACITY_MASK: u32 = 65535u;
const INTERSECTION_DETAIL_NOT_CALCULATED: u32 = 0u;
const INTERSECTION_DETAIL_EMPTY: u32 = 1u;
const INTERSECTION_DETAIL_FULLY_INSIDE: u32 = 2u;
const INTERSECTION_DETAIL_FULLY_CONTAINS: u32 = 3u;
const INTERSECTION_DETAIL_INTERSECTS: u32 = 4u;

fn finite2(value: vec2<f32>) -> bool {
    return all(abs(value) < vec2<f32>(3.402823e38, 3.402823e38));
}

fn contains_bounds(point: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>) -> bool {
    return point.x >= min_value.x && point.x <= max_value.x && point.y >= min_value.y && point.y <= max_value.y;
}

fn intersects_bounds(a_min: vec2<f32>, a_max: vec2<f32>, b_min: vec2<f32>, b_max: vec2<f32>) -> bool {
    return a_max.x >= b_min.x && a_min.x <= b_max.x && a_max.y >= b_min.y && a_min.y <= b_max.y;
}

fn contains_rect_bounds(outer_min: vec2<f32>, outer_max: vec2<f32>, inner_min: vec2<f32>, inner_max: vec2<f32>) -> bool {
    return inner_min.x >= outer_min.x && inner_max.x <= outer_max.x && inner_min.y >= outer_min.y && inner_max.y <= outer_max.y;
}

fn query_uses_bounds() -> bool {
    return (query.flags & QUERY_MODE_BOUNDS) != 0u;
}

fn query_uses_ellipse_region() -> bool {
    return (query.flags & QUERY_MODE_ELLIPSE_REGION) != 0u;
}

fn query_result_capacity() -> u32 {
    return query.flags & QUERY_RESULT_CAPACITY_MASK;
}

fn query_region_min() -> vec2<f32> {
    return min(query.point, query.region_max);
}

fn query_region_max() -> vec2<f32> {
    return max(query.point, query.region_max);
}

fn query_intersects_bounds(min_value: vec2<f32>, max_value: vec2<f32>) -> bool {
    if (query_uses_ellipse_region()) {
        return rect_intersects_ellipse(min_value, max_value, query_region_min(), query_region_max());
    }

    if (query_uses_bounds()) {
        return intersects_bounds(query_region_min(), query_region_max(), min_value, max_value);
    }

    return contains_bounds(query.point, min_value, max_value);
}

fn transform_to_local(point: vec2<f32>, primitive: HitTestPrimitive) -> vec2<f32> {
    return vec2<f32>(
        point.x * primitive.inverse_transform0.x + point.y * primitive.inverse_transform0.y + primitive.inverse_transform0.z,
        point.x * primitive.inverse_transform1.x + point.y * primitive.inverse_transform1.y + primitive.inverse_transform1.z);
}

fn transform_bounds_to_local_min(region_min: vec2<f32>, region_max: vec2<f32>, primitive: HitTestPrimitive) -> vec2<f32> {
    let p0 = transform_to_local(region_min, primitive);
    let p1 = transform_to_local(vec2<f32>(region_max.x, region_min.y), primitive);
    let p2 = transform_to_local(region_max, primitive);
    let p3 = transform_to_local(vec2<f32>(region_min.x, region_max.y), primitive);
    return min(min(p0, p1), min(p2, p3));
}

fn transform_bounds_to_local_max(region_min: vec2<f32>, region_max: vec2<f32>, primitive: HitTestPrimitive) -> vec2<f32> {
    let p0 = transform_to_local(region_min, primitive);
    let p1 = transform_to_local(vec2<f32>(region_max.x, region_min.y), primitive);
    let p2 = transform_to_local(region_max, primitive);
    let p3 = transform_to_local(vec2<f32>(region_min.x, region_max.y), primitive);
    return max(max(p0, p1), max(p2, p3));
}

fn contains_rounded_rect(point: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>, radius: vec2<f32>) -> bool {
    if (!contains_bounds(point, min_value, max_value)) {
        return false;
    }

    let size = max_value - min_value;
    let rx = min(abs(radius.x), size.x * 0.5);
    let ry = min(abs(radius.y), size.y * 0.5);
    if (rx <= 0.0 || ry <= 0.0) {
        return true;
    }

    let left = min_value.x + rx;
    let right = max_value.x - rx;
    let top = min_value.y + ry;
    let bottom = max_value.y - ry;
    if ((point.x >= left && point.x <= right) || (point.y >= top && point.y <= bottom)) {
        return true;
    }

    let center = vec2<f32>(select(right, left, point.x < left), select(bottom, top, point.y < top));
    let normalized = (point - center) / vec2<f32>(rx, ry);
    return dot(normalized, normalized) <= 1.0;
}

fn rect_intersects_rounded_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>, radius: vec2<f32>) -> bool {
    if (!intersects_bounds(rect_min, rect_max, min_value, max_value)) {
        return false;
    }

    let size = max_value - min_value;
    let rx = min(abs(radius.x), size.x * 0.5);
    let ry = min(abs(radius.y), size.y * 0.5);
    if (rx <= 0.0 || ry <= 0.0) {
        return true;
    }

    if (intersects_bounds(rect_min, rect_max, vec2<f32>(min_value.x + rx, min_value.y), vec2<f32>(max_value.x - rx, max_value.y)) ||
        intersects_bounds(rect_min, rect_max, vec2<f32>(min_value.x, min_value.y + ry), vec2<f32>(max_value.x, max_value.y - ry))) {
        return true;
    }

    let corner_radius = vec2<f32>(rx, ry);
    return rect_intersects_ellipse(rect_min, rect_max, min_value, min_value + corner_radius * 2.0) ||
        rect_intersects_ellipse(rect_min, rect_max, vec2<f32>(max_value.x - 2.0 * rx, min_value.y), vec2<f32>(max_value.x, min_value.y + 2.0 * ry)) ||
        rect_intersects_ellipse(rect_min, rect_max, vec2<f32>(max_value.x - 2.0 * rx, max_value.y - 2.0 * ry), max_value) ||
        rect_intersects_ellipse(rect_min, rect_max, vec2<f32>(min_value.x, max_value.y - 2.0 * ry), vec2<f32>(min_value.x + 2.0 * rx, max_value.y));
}

fn rounded_rect_contains_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>, radius: vec2<f32>) -> bool {
    return contains_rounded_rect(rect_min, min_value, max_value, radius) &&
        contains_rounded_rect(vec2<f32>(rect_max.x, rect_min.y), min_value, max_value, radius) &&
        contains_rounded_rect(rect_max, min_value, max_value, radius) &&
        contains_rounded_rect(vec2<f32>(rect_min.x, rect_max.y), min_value, max_value, radius);
}

fn rect_intersects_rect_stroke(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.z);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.w);
    let half_stroke = stroke * 0.5 + tolerance;
    let original_min = primitive.data0.xy;
    let original_max = primitive.data0.zw;
    let outer_min = original_min - vec2<f32>(half_stroke, half_stroke);
    let outer_max = original_max + vec2<f32>(half_stroke, half_stroke);
    let outer_radius = abs(primitive.data1.xy) + vec2<f32>(half_stroke, half_stroke);
    if (!rect_intersects_rounded_rect(rect_min, rect_max, outer_min, outer_max, outer_radius)) {
        return false;
    }

    let inner_min = original_min + vec2<f32>(half_stroke, half_stroke);
    let inner_max = original_max - vec2<f32>(half_stroke, half_stroke);
    if (inner_max.x <= inner_min.x || inner_max.y <= inner_min.y) {
        return true;
    }

    let inner_radius = max(vec2<f32>(0.0, 0.0), abs(primitive.data1.xy) - vec2<f32>(half_stroke, half_stroke));
    return !rounded_rect_contains_rect(rect_min, rect_max, inner_min, inner_max, inner_radius);
}

fn rect_contains_ellipse_region(rect_min: vec2<f32>, rect_max: vec2<f32>, query_center: vec2<f32>, query_inverse_radii: vec2<f32>) -> bool {
    if (rect_max.x < rect_min.x || rect_max.y < rect_min.y) {
        return false;
    }

    let query_radii = ellipse_radii_from_inverse(query_inverse_radii);
    if (query_radii.x <= 0.0 || query_radii.y <= 0.0) {
        return false;
    }

    let query_min = query_center - query_radii;
    let query_max = query_center + query_radii;
    return contains_rect_bounds(rect_min, rect_max, query_min, query_max);
}

fn rounded_rect_intersects_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>, radius: vec2<f32>) -> bool {
    let query_radii = ellipse_radii_from_inverse(query_inverse_radii);
    if (query_radii.x <= 0.0 || query_radii.y <= 0.0) {
        return false;
    }

    let query_min = query_center - query_radii;
    let query_max = query_center + query_radii;
    if (!intersects_bounds(query_min, query_max, min_value, max_value)) {
        return false;
    }

    let size = max_value - min_value;
    let rx = min(abs(radius.x), size.x * 0.5);
    let ry = min(abs(radius.y), size.y * 0.5);
    if (rx <= 0.0 || ry <= 0.0) {
        return rect_intersects_ellipse_from_inverse_radii(min_value, max_value, query_center, query_inverse_radii);
    }

    if (rect_intersects_ellipse_from_inverse_radii(
            vec2<f32>(min_value.x + rx, min_value.y),
            vec2<f32>(max_value.x - rx, max_value.y),
            query_center,
            query_inverse_radii) ||
        rect_intersects_ellipse_from_inverse_radii(
            vec2<f32>(min_value.x, min_value.y + ry),
            vec2<f32>(max_value.x, max_value.y - ry),
            query_center,
            query_inverse_radii)) {
        return true;
    }

    let corner_inverse_radii = vec2<f32>(1.0 / rx, 1.0 / ry);
    return ellipses_may_intersect(query_center, query_inverse_radii, vec2<f32>(min_value.x + rx, min_value.y + ry), corner_inverse_radii) ||
        ellipses_may_intersect(query_center, query_inverse_radii, vec2<f32>(max_value.x - rx, min_value.y + ry), corner_inverse_radii) ||
        ellipses_may_intersect(query_center, query_inverse_radii, vec2<f32>(max_value.x - rx, max_value.y - ry), corner_inverse_radii) ||
        ellipses_may_intersect(query_center, query_inverse_radii, vec2<f32>(min_value.x + rx, max_value.y - ry), corner_inverse_radii);
}

fn rounded_rect_contains_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>, radius: vec2<f32>) -> bool {
    if (!rect_contains_ellipse_region(min_value, max_value, query_center, query_inverse_radii)) {
        return false;
    }

    let size = max_value - min_value;
    let rx = min(abs(radius.x), size.x * 0.5);
    let ry = min(abs(radius.y), size.y * 0.5);
    if (rx <= 0.0 || ry <= 0.0) {
        return true;
    }

    return rect_contains_ellipse_region(
            vec2<f32>(min_value.x + rx, min_value.y),
            vec2<f32>(max_value.x - rx, max_value.y),
            query_center,
            query_inverse_radii) ||
        rect_contains_ellipse_region(
            vec2<f32>(min_value.x, min_value.y + ry),
            vec2<f32>(max_value.x, max_value.y - ry),
            query_center,
            query_inverse_radii);
}

fn rect_stroke_intersects_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.z);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.w);
    let half_stroke = stroke * 0.5 + tolerance;
    let original_min = primitive.data0.xy;
    let original_max = primitive.data0.zw;
    let outer_min = original_min - vec2<f32>(half_stroke, half_stroke);
    let outer_max = original_max + vec2<f32>(half_stroke, half_stroke);
    let outer_radius = abs(primitive.data1.xy) + vec2<f32>(half_stroke, half_stroke);
    if (!rounded_rect_intersects_ellipse_region(query_center, query_inverse_radii, outer_min, outer_max, outer_radius)) {
        return false;
    }

    let inner_min = original_min + vec2<f32>(half_stroke, half_stroke);
    let inner_max = original_max - vec2<f32>(half_stroke, half_stroke);
    if (inner_max.x <= inner_min.x || inner_max.y <= inner_min.y) {
        return true;
    }

    let inner_radius = max(vec2<f32>(0.0, 0.0), abs(primitive.data1.xy) - vec2<f32>(half_stroke, half_stroke));
    return !rounded_rect_contains_ellipse_region(query_center, query_inverse_radii, inner_min, inner_max, inner_radius);
}

fn contains_rect_stroke(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.z);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.w);
    let half_stroke = stroke * 0.5 + tolerance;
    let original_min = primitive.data0.xy;
    let original_max = primitive.data0.zw;
    let outer_min = original_min - vec2<f32>(half_stroke, half_stroke);
    let outer_max = original_max + vec2<f32>(half_stroke, half_stroke);
    let outer_radius = abs(primitive.data1.xy) + vec2<f32>(half_stroke, half_stroke);
    if (!contains_rounded_rect(point, outer_min, outer_max, outer_radius)) {
        return false;
    }

    let inner_min = original_min + vec2<f32>(half_stroke, half_stroke);
    let inner_max = original_max - vec2<f32>(half_stroke, half_stroke);
    if (inner_max.x <= inner_min.x || inner_max.y <= inner_min.y) {
        return true;
    }

    let inner_radius = max(vec2<f32>(0.0, 0.0), abs(primitive.data1.xy) - vec2<f32>(half_stroke, half_stroke));
    return !contains_rounded_rect(point, inner_min, inner_max, inner_radius);
}

fn contains_ellipse(point: vec2<f32>, min_value: vec2<f32>, max_value: vec2<f32>) -> bool {
    if (!contains_bounds(point, min_value, max_value)) {
        return false;
    }

    let radii = (max_value - min_value) * 0.5;
    if (radii.x <= 0.0 || radii.y <= 0.0) {
        return false;
    }

    let center = (min_value + max_value) * 0.5;
    let normalized = (point - center) / radii;
    return dot(normalized, normalized) <= 1.0;
}

fn contains_ellipse_from_inverse_radii(point: vec2<f32>, center: vec2<f32>, inverse_radii: vec2<f32>) -> bool {
    if (inverse_radii.x <= 0.0 || inverse_radii.y <= 0.0) {
        return false;
    }

    let normalized = (point - center) * inverse_radii;
    return dot(normalized, normalized) <= 1.0;
}

fn contains_cached_ellipse(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    return contains_bounds(point, primitive.data0.xy, primitive.data0.zw) &&
        contains_ellipse_from_inverse_radii(point, primitive.data2.xy, primitive.data2.zw);
}

fn rect_intersects_ellipse(rect_min: vec2<f32>, rect_max: vec2<f32>, ellipse_min: vec2<f32>, ellipse_max: vec2<f32>) -> bool {
    let radii = (ellipse_max - ellipse_min) * 0.5;
    if (radii.x <= 0.0 || radii.y <= 0.0) {
        return false;
    }

    let center = (ellipse_min + ellipse_max) * 0.5;
    let closest = clamp(center, rect_min, rect_max);
    let normalized = (closest - center) / radii;
    return dot(normalized, normalized) <= 1.0;
}

fn rect_intersects_ellipse_from_inverse_radii(rect_min: vec2<f32>, rect_max: vec2<f32>, center: vec2<f32>, inverse_radii: vec2<f32>) -> bool {
    if (inverse_radii.x <= 0.0 || inverse_radii.y <= 0.0) {
        return false;
    }

    let closest = clamp(center, rect_min, rect_max);
    let normalized = (closest - center) * inverse_radii;
    return dot(normalized, normalized) <= 1.0;
}

fn rect_intersects_cached_ellipse(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    return rect_intersects_ellipse_from_inverse_radii(rect_min, rect_max, primitive.data2.xy, primitive.data2.zw);
}

fn ellipse_contains_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, ellipse_min: vec2<f32>, ellipse_max: vec2<f32>) -> bool {
    return contains_ellipse(rect_min, ellipse_min, ellipse_max) &&
        contains_ellipse(vec2<f32>(rect_max.x, rect_min.y), ellipse_min, ellipse_max) &&
        contains_ellipse(rect_max, ellipse_min, ellipse_max) &&
        contains_ellipse(vec2<f32>(rect_min.x, rect_max.y), ellipse_min, ellipse_max);
}

fn ellipse_contains_rect_from_inverse_radii(rect_min: vec2<f32>, rect_max: vec2<f32>, center: vec2<f32>, inverse_radii: vec2<f32>) -> bool {
    return contains_ellipse_from_inverse_radii(rect_min, center, inverse_radii) &&
        contains_ellipse_from_inverse_radii(vec2<f32>(rect_max.x, rect_min.y), center, inverse_radii) &&
        contains_ellipse_from_inverse_radii(rect_max, center, inverse_radii) &&
        contains_ellipse_from_inverse_radii(vec2<f32>(rect_min.x, rect_max.y), center, inverse_radii);
}

fn cached_ellipse_contains_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    return ellipse_contains_rect_from_inverse_radii(rect_min, rect_max, primitive.data2.xy, primitive.data2.zw);
}

fn ellipse_inverse_radii_from_bounds(min_value: vec2<f32>, max_value: vec2<f32>) -> vec2<f32> {
    let radii = (max_value - min_value) * 0.5;
    if (radii.x <= 0.0 || radii.y <= 0.0) {
        return vec2<f32>(0.0, 0.0);
    }

    return vec2<f32>(1.0 / radii.x, 1.0 / radii.y);
}

fn ellipse_radii_from_inverse(inverse_radii: vec2<f32>) -> vec2<f32> {
    if (inverse_radii.x <= 0.0 || inverse_radii.y <= 0.0) {
        return vec2<f32>(0.0, 0.0);
    }

    return vec2<f32>(1.0 / inverse_radii.x, 1.0 / inverse_radii.y);
}

fn ellipse_centers_close(a: vec2<f32>, b: vec2<f32>) -> bool {
    return abs(a.x - b.x) <= 0.00001 && abs(a.y - b.y) <= 0.00001;
}

fn ellipses_may_intersect(a_center: vec2<f32>, a_inverse_radii: vec2<f32>, b_center: vec2<f32>, b_inverse_radii: vec2<f32>) -> bool {
    let a_radii = ellipse_radii_from_inverse(a_inverse_radii);
    let b_radii = ellipse_radii_from_inverse(b_inverse_radii);
    if (a_radii.x <= 0.0 || a_radii.y <= 0.0 || b_radii.x <= 0.0 || b_radii.y <= 0.0) {
        return false;
    }

    let radii_sum = a_radii + b_radii;
    let normalized = (b_center - a_center) / radii_sum;
    return dot(normalized, normalized) <= 1.0;
}

fn ellipse_contains_ellipse_same_center(container_center: vec2<f32>, container_inverse_radii: vec2<f32>, contained_center: vec2<f32>, contained_inverse_radii: vec2<f32>) -> bool {
    if (!ellipse_centers_close(container_center, contained_center)) {
        return false;
    }

    let container_radii = ellipse_radii_from_inverse(container_inverse_radii);
    let contained_radii = ellipse_radii_from_inverse(contained_inverse_radii);
    if (container_radii.x <= 0.0 || container_radii.y <= 0.0 || contained_radii.x <= 0.0 || contained_radii.y <= 0.0) {
        return false;
    }

    return contained_radii.x <= container_radii.x + 0.00001 &&
        contained_radii.y <= container_radii.y + 0.00001;
}

fn ellipse_stroke_intersects_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.x);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.y);
    let half_stroke = stroke * 0.5 + tolerance;
    let primitive_center = primitive.data2.xy;
    let primitive_radii = ellipse_radii_from_inverse(primitive.data2.zw);
    if (primitive_radii.x <= 0.0 || primitive_radii.y <= 0.0) {
        return false;
    }

    let outer_inverse_radii = vec2<f32>(1.0, 1.0) / (primitive_radii + vec2<f32>(half_stroke, half_stroke));
    if (!ellipses_may_intersect(query_center, query_inverse_radii, primitive_center, outer_inverse_radii)) {
        return false;
    }

    let inner_radii = primitive_radii - vec2<f32>(half_stroke, half_stroke);
    if (inner_radii.x <= 0.0 || inner_radii.y <= 0.0) {
        return true;
    }

    let inner_inverse_radii = vec2<f32>(1.0, 1.0) / inner_radii;
    return !ellipse_contains_ellipse_same_center(primitive_center, inner_inverse_radii, query_center, query_inverse_radii);
}

fn rect_intersects_ellipse_stroke(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.x);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.y);
    let half_stroke = stroke * 0.5 + tolerance;
    let center = primitive.data2.xy;
    let inverse_radii = primitive.data2.zw;
    if (inverse_radii.x <= 0.0 || inverse_radii.y <= 0.0) {
        return false;
    }

    let radii = vec2<f32>(1.0 / inverse_radii.x, 1.0 / inverse_radii.y);
    let outer_inverse_radii = vec2<f32>(1.0, 1.0) / (radii + vec2<f32>(half_stroke, half_stroke));
    if (!rect_intersects_ellipse_from_inverse_radii(rect_min, rect_max, center, outer_inverse_radii)) {
        return false;
    }

    let inner_radii = radii - vec2<f32>(half_stroke, half_stroke);
    if (inner_radii.x <= 0.0 || inner_radii.y <= 0.0) {
        return true;
    }

    let inner_inverse_radii = vec2<f32>(1.0, 1.0) / inner_radii;
    return !ellipse_contains_rect_from_inverse_radii(rect_min, rect_max, center, inner_inverse_radii);
}

fn contains_ellipse_stroke(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.x);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.y);
    let half_stroke = stroke * 0.5 + tolerance;
    let center = primitive.data2.xy;
    let inverse_radii = primitive.data2.zw;
    if (inverse_radii.x <= 0.0 || inverse_radii.y <= 0.0) {
        return false;
    }

    let radii = vec2<f32>(1.0 / inverse_radii.x, 1.0 / inverse_radii.y);
    let outer_inverse_radii = vec2<f32>(1.0, 1.0) / (radii + vec2<f32>(half_stroke, half_stroke));
    if (!contains_ellipse_from_inverse_radii(point, center, outer_inverse_radii)) {
        return false;
    }

    let inner_radii = radii - vec2<f32>(half_stroke, half_stroke);
    if (inner_radii.x <= 0.0 || inner_radii.y <= 0.0) {
        return true;
    }

    let inner_inverse_radii = vec2<f32>(1.0, 1.0) / inner_radii;
    return !contains_ellipse_from_inverse_radii(point, center, inner_inverse_radii);
}

fn cross2(a: vec2<f32>, b: vec2<f32>) -> f32 {
    return a.x * b.y - a.y * b.x;
}

fn point_in_triangle(point: vec2<f32>, a: vec2<f32>, b: vec2<f32>, c: vec2<f32>) -> bool {
    let d0 = cross2(b - a, point - a);
    let d1 = cross2(c - b, point - b);
    let d2 = cross2(a - c, point - c);
    let has_neg = d0 < 0.0 || d1 < 0.0 || d2 < 0.0;
    let has_pos = d0 > 0.0 || d1 > 0.0 || d2 > 0.0;
    return !(has_neg && has_pos);
}

fn contains_triangle_cap(point: vec2<f32>, base_center: vec2<f32>, outward: vec2<f32>, half_stroke: f32) -> bool {
    let normal = vec2<f32>(-outward.y, outward.x);
    let a = base_center - normal * half_stroke;
    let b = base_center + normal * half_stroke;
    let c = base_center + outward * half_stroke;
    return point_in_triangle(point, a, b, c);
}

fn contains_line_cap(point: vec2<f32>, center: vec2<f32>, direction: vec2<f32>, signed_distance: f32, along: f32, half_stroke: f32, cap: u32, is_start: bool) -> bool {
    if (cap == CAP_SQUARE) {
        return abs(signed_distance) <= half_stroke && abs(along) <= half_stroke;
    }

    if (cap == CAP_ROUND) {
        let delta = point - center;
        return dot(delta, delta) <= half_stroke * half_stroke;
    }

    if (cap == CAP_TRIANGLE) {
        let outward = select(direction, -direction, is_start);
        return contains_triangle_cap(point, center, outward, half_stroke);
    }

    return false;
}

fn contains_line_stroke(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start = primitive.data0.xy;
    let end = primitive.data0.zw;
    let stroke = abs(primitive.data1.x);
    if (stroke <= 0.0) {
        return false;
    }

    let tolerance = max(0.0, primitive.data1.y);
    let half_stroke = stroke * 0.5 + tolerance;
    let direction = primitive.data2.xy;
    let segment_length = primitive.data2.z;
    let start_cap = u32(primitive.data1.z);
    let end_cap = u32(primitive.data1.w);
    if (segment_length <= 0.0001) {
        if (start_cap == CAP_FLAT && end_cap == CAP_FLAT) {
            return false;
        }

        let delta = point - start;
        return dot(delta, delta) <= half_stroke * half_stroke;
    }

    let offset = point - start;
    let along = dot(offset, direction);
    let signed_distance = cross2(offset, direction);
    if (along >= 0.0 && along <= segment_length && abs(signed_distance) <= half_stroke) {
        return true;
    }

    if (along < 0.0) {
        return contains_line_cap(point, start, direction, signed_distance, along, half_stroke, start_cap, true);
    }

    return contains_line_cap(point, end, direction, signed_distance, along - segment_length, half_stroke, end_cap, false);
}

fn distance_squared_to_segment(point: vec2<f32>, start: vec2<f32>, end: vec2<f32>) -> f32 {
    let segment = end - start;
    let length_squared = dot(segment, segment);
    if (length_squared <= 0.00000001) {
        let delta = point - start;
        return dot(delta, delta);
    }

    let t = clamp(dot(point - start, segment) / length_squared, 0.0, 1.0);
    let projection = start + segment * t;
    let delta = point - projection;
    return dot(delta, delta);
}

fn segment_intersects_ellipse_region(start: vec2<f32>, end: vec2<f32>, query_center: vec2<f32>, query_inverse_radii: vec2<f32>) -> bool {
    if (query_inverse_radii.x <= 0.0 || query_inverse_radii.y <= 0.0) {
        return false;
    }

    let normalized_start = (start - query_center) * query_inverse_radii;
    let normalized_end = (end - query_center) * query_inverse_radii;
    return distance_squared_to_segment(vec2<f32>(0.0, 0.0), normalized_start, normalized_end) <= 1.0;
}

fn stroked_segment_intersects_ellipse_region(start: vec2<f32>, end: vec2<f32>, half_stroke: f32, query_center: vec2<f32>, query_inverse_radii: vec2<f32>) -> bool {
    if (half_stroke <= 0.0 || query_inverse_radii.x <= 0.0 || query_inverse_radii.y <= 0.0) {
        return false;
    }

    let normalized_start = (start - query_center) * query_inverse_radii;
    let normalized_end = (end - query_center) * query_inverse_radii;
    let normalized_stroke_radius = half_stroke * max(query_inverse_radii.x, query_inverse_radii.y);
    let hit_radius = 1.0 + normalized_stroke_radius;
    return distance_squared_to_segment(vec2<f32>(0.0, 0.0), normalized_start, normalized_end) <= hit_radius * hit_radius;
}

fn line_stroke_intersects_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let stroke = abs(primitive.data1.x);
    if (stroke <= 0.0 || query_inverse_radii.x <= 0.0 || query_inverse_radii.y <= 0.0) {
        return false;
    }

    let half_stroke = (stroke * 0.5) + max(0.0, primitive.data1.y);
    let start_cap = u32(primitive.data1.z);
    let end_cap = u32(primitive.data1.w);
    let direction = primitive.data2.xy;
    let segment_length = primitive.data2.z;
    let normalized_stroke_radius = half_stroke * max(query_inverse_radii.x, query_inverse_radii.y);
    let hit_radius = 1.0 + normalized_stroke_radius;
    if (segment_length <= 0.0001) {
        if (start_cap == CAP_FLAT && end_cap == CAP_FLAT) {
            return false;
        }

        let normalized_point = (primitive.data0.xy - query_center) * query_inverse_radii;
        return dot(normalized_point, normalized_point) <= hit_radius * hit_radius;
    }

    let start_extension = select(half_stroke, 0.0, start_cap == CAP_FLAT);
    let end_extension = select(half_stroke, 0.0, end_cap == CAP_FLAT);
    let start = (primitive.data0.xy - (direction * start_extension) - query_center) * query_inverse_radii;
    let end = (primitive.data0.zw + (direction * end_extension) - query_center) * query_inverse_radii;
    return distance_squared_to_segment(vec2<f32>(0.0, 0.0), start, end) <= hit_radius * hit_radius;
}

fn distance_squared_to_rect(point: vec2<f32>, rect_min: vec2<f32>, rect_max: vec2<f32>) -> f32 {
    let closest = clamp(point, rect_min, rect_max);
    let delta = point - closest;
    return dot(delta, delta);
}

fn segments_intersect(a: vec2<f32>, b: vec2<f32>, c: vec2<f32>, d: vec2<f32>) -> bool {
    let ab = b - a;
    let cd = d - c;
    let denominator = cross2(ab, cd);
    let ca = c - a;
    if (abs(denominator) <= 0.000001) {
        if (abs(cross2(ca, ab)) > 0.0001) {
            return false;
        }

        let ab_min = min(a, b);
        let ab_max = max(a, b);
        let cd_min = min(c, d);
        let cd_max = max(c, d);
        return intersects_bounds(ab_min, ab_max, cd_min, cd_max);
    }

    let t = cross2(ca, cd) / denominator;
    let u = cross2(ca, ab) / denominator;
    return t >= 0.0 && t <= 1.0 && u >= 0.0 && u <= 1.0;
}

fn segment_intersects_rect(start: vec2<f32>, end: vec2<f32>, rect_min: vec2<f32>, rect_max: vec2<f32>) -> bool {
    if (contains_bounds(start, rect_min, rect_max) || contains_bounds(end, rect_min, rect_max)) {
        return true;
    }

    let top_left = rect_min;
    let top_right = vec2<f32>(rect_max.x, rect_min.y);
    let bottom_right = rect_max;
    let bottom_left = vec2<f32>(rect_min.x, rect_max.y);
    return segments_intersect(start, end, top_left, top_right) ||
        segments_intersect(start, end, top_right, bottom_right) ||
        segments_intersect(start, end, bottom_right, bottom_left) ||
        segments_intersect(start, end, bottom_left, top_left);
}

fn point_in_quad(point: vec2<f32>, a: vec2<f32>, b: vec2<f32>, c: vec2<f32>, d: vec2<f32>) -> bool {
    return point_in_triangle(point, a, b, c) || point_in_triangle(point, a, c, d);
}

fn quad_intersects_rect(a: vec2<f32>, b: vec2<f32>, c: vec2<f32>, d: vec2<f32>, rect_min: vec2<f32>, rect_max: vec2<f32>) -> bool {
    if (contains_bounds(a, rect_min, rect_max) ||
        contains_bounds(b, rect_min, rect_max) ||
        contains_bounds(c, rect_min, rect_max) ||
        contains_bounds(d, rect_min, rect_max)) {
        return true;
    }

    let top_left = rect_min;
    let top_right = vec2<f32>(rect_max.x, rect_min.y);
    let bottom_right = rect_max;
    let bottom_left = vec2<f32>(rect_min.x, rect_max.y);
    if (point_in_quad(top_left, a, b, c, d) ||
        point_in_quad(top_right, a, b, c, d) ||
        point_in_quad(bottom_right, a, b, c, d) ||
        point_in_quad(bottom_left, a, b, c, d)) {
        return true;
    }

    return segment_intersects_rect(a, b, rect_min, rect_max) ||
        segment_intersects_rect(b, c, rect_min, rect_max) ||
        segment_intersects_rect(c, d, rect_min, rect_max) ||
        segment_intersects_rect(d, a, rect_min, rect_max);
}

fn stroked_segment_intersects_rect(start: vec2<f32>, end: vec2<f32>, rect_min: vec2<f32>, rect_max: vec2<f32>, half_stroke: f32) -> bool {
    if (half_stroke <= 0.0) {
        return false;
    }

    if (segment_intersects_rect(start, end, rect_min, rect_max)) {
        return true;
    }

    let stroke_squared = half_stroke * half_stroke;
    if (distance_squared_to_rect(start, rect_min, rect_max) <= stroke_squared ||
        distance_squared_to_rect(end, rect_min, rect_max) <= stroke_squared) {
        return true;
    }

    let top_left = rect_min;
    let top_right = vec2<f32>(rect_max.x, rect_min.y);
    let bottom_right = rect_max;
    let bottom_left = vec2<f32>(rect_min.x, rect_max.y);
    return distance_squared_to_segment(top_left, start, end) <= stroke_squared ||
        distance_squared_to_segment(top_right, start, end) <= stroke_squared ||
        distance_squared_to_segment(bottom_right, start, end) <= stroke_squared ||
        distance_squared_to_segment(bottom_left, start, end) <= stroke_squared;
}

fn triangle_cap_intersects_rect(center: vec2<f32>, direction: vec2<f32>, half_stroke: f32, rect_min: vec2<f32>, rect_max: vec2<f32>) -> bool {
    let normal = vec2<f32>(-direction.y, direction.x);
    let a = center - normal * half_stroke;
    let b = center + normal * half_stroke;
    let c = center + direction * half_stroke;
    if (contains_bounds(a, rect_min, rect_max) ||
        contains_bounds(b, rect_min, rect_max) ||
        contains_bounds(c, rect_min, rect_max)) {
        return true;
    }

    let top_left = rect_min;
    let top_right = vec2<f32>(rect_max.x, rect_min.y);
    let bottom_right = rect_max;
    let bottom_left = vec2<f32>(rect_min.x, rect_max.y);
    if (point_in_triangle(top_left, a, b, c) ||
        point_in_triangle(top_right, a, b, c) ||
        point_in_triangle(bottom_right, a, b, c) ||
        point_in_triangle(bottom_left, a, b, c)) {
        return true;
    }

    return segment_intersects_rect(a, b, rect_min, rect_max) ||
        segment_intersects_rect(b, c, rect_min, rect_max) ||
        segment_intersects_rect(c, a, rect_min, rect_max);
}

fn line_stroke_intersects_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start = primitive.data0.xy;
    let end = primitive.data0.zw;
    let stroke = abs(primitive.data1.x);
    if (stroke <= 0.0) {
        return false;
    }

    let half_stroke = (stroke * 0.5) + max(0.0, primitive.data1.y);
    if (half_stroke <= 0.0) {
        return false;
    }

    let start_cap = u32(primitive.data1.z);
    let end_cap = u32(primitive.data1.w);
    let direction = primitive.data2.xy;
    let segment_length = primitive.data2.z;
    if (segment_length <= 0.0001) {
        if (start_cap == CAP_FLAT && end_cap == CAP_FLAT) {
            return false;
        }

        return distance_squared_to_rect(start, rect_min, rect_max) <= half_stroke * half_stroke;
    }

    let normal = vec2<f32>(-direction.y, direction.x);
    let start_extension = select(0.0, half_stroke, start_cap == CAP_SQUARE);
    let end_extension = select(0.0, half_stroke, end_cap == CAP_SQUARE);
    let body_start = start - direction * start_extension;
    let body_end = end + direction * end_extension;
    let a = body_start + normal * half_stroke;
    let b = body_end + normal * half_stroke;
    let c = body_end - normal * half_stroke;
    let d = body_start - normal * half_stroke;
    if (quad_intersects_rect(a, b, c, d, rect_min, rect_max)) {
        return true;
    }

    if (start_cap == CAP_ROUND && distance_squared_to_rect(start, rect_min, rect_max) <= half_stroke * half_stroke) {
        return true;
    }

    if (end_cap == CAP_ROUND && distance_squared_to_rect(end, rect_min, rect_max) <= half_stroke * half_stroke) {
        return true;
    }

    if (start_cap == CAP_TRIANGLE && triangle_cap_intersects_rect(start, -direction, half_stroke, rect_min, rect_max)) {
        return true;
    }

    if (end_cap == CAP_TRIANGLE && triangle_cap_intersects_rect(end, direction, half_stroke, rect_min, rect_max)) {
        return true;
    }

    return false;
}

fn evaluate_quadratic(start: vec2<f32>, control: vec2<f32>, end: vec2<f32>, t: f32) -> vec2<f32> {
    let u = 1.0 - t;
    return (u * u * start) + (2.0 * u * t * control) + (t * t * end);
}

fn evaluate_cubic(start: vec2<f32>, control0: vec2<f32>, control1: vec2<f32>, end: vec2<f32>, t: f32) -> vec2<f32> {
    let u = 1.0 - t;
    let tt = t * t;
    let uu = u * u;
    return (uu * u * start) +
        (3.0 * uu * t * control0) +
        (3.0 * u * tt * control1) +
        (tt * t * end);
}

fn evaluate_arc(segment: PathSegment, t: f32) -> vec2<f32> {
    let theta_start = bitcast<f32>(segment.pad0);
    let delta_theta = bitcast<f32>(segment.pad1);
    let rotation = bitcast<f32>(segment.pad2);
    let theta = theta_start + delta_theta * t;
    let local = vec2<f32>(cos(theta) * segment.p3.x, sin(theta) * segment.p3.y);
    let c = cos(rotation);
    let s = sin(rotation);
    return segment.p2 + vec2<f32>(
        (local.x * c) - (local.y * s),
        (local.x * s) + (local.y * c));
}

struct PathEdgeResult {
    boundary: bool,
    crosses: bool,
    winding_delta: i32,
};

fn test_path_fill_edge(point: vec2<f32>, start: vec2<f32>, end: vec2<f32>, tolerance: f32) -> PathEdgeResult {
    var edge: PathEdgeResult;
    edge.boundary = false;
    edge.crosses = false;
    edge.winding_delta = 0;

    if (distance_squared_to_segment(point, start, end) <= tolerance * tolerance) {
        edge.boundary = true;
        return edge;
    }

    let crosses_y = (start.y > point.y) != (end.y > point.y);
    if (crosses_y) {
        let intersection_x = ((end.x - start.x) * (point.y - start.y) / (end.y - start.y)) + start.x;
        edge.crosses = point.x < intersection_x;

        let cross = cross2(end - start, point - start);
        if (start.y <= point.y) {
            if (end.y > point.y && cross > 0.0) {
                edge.winding_delta = 1;
            }
        } else if (end.y <= point.y && cross < 0.0) {
            edge.winding_delta = -1;
        }
    }

    return edge;
}

struct PathFillState {
    boundary: bool,
    even_odd: bool,
    winding: i32,
};

fn accumulate_path_fill_edge(point: vec2<f32>, start: vec2<f32>, end: vec2<f32>, tolerance: f32, fill_rule: u32, state: PathFillState) -> PathFillState {
    var next = state;
    let edge = test_path_fill_edge(point, start, end, tolerance);
    if (edge.boundary) {
        next.boundary = true;
        return next;
    }

    if (fill_rule == FILL_RULE_EVEN_ODD) {
        if (edge.crosses) {
            next.even_odd = !next.even_odd;
        }
    } else {
        next.winding = next.winding + edge.winding_delta;
    }

    return next;
}

fn contains_path_fill_segments(point: vec2<f32>, start_segment: u32, segment_count: u32, fill_rule: u32) -> bool {
    if (segment_count == 0u || start_segment >= query.path_segment_count) {
        return false;
    }

    let end_segment = min(start_segment + segment_count, query.path_segment_count);
    var state: PathFillState;
    state.boundary = false;
    state.even_odd = false;
    state.winding = 0;

    var segment_index = start_segment;
    loop {
        if (segment_index >= end_segment) {
            break;
        }

        let segment = path_segments[segment_index];
        if (segment.segment_type == SEGMENT_LINE) {
            state = accumulate_path_fill_edge(point, segment.p0, segment.p1, 0.0001, fill_rule, state);
        } else if (segment.segment_type == SEGMENT_QUADRATIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_QUADRATIC_STEPS) {
                    break;
                }

                let next_point = evaluate_quadratic(segment.p0, segment.p1, segment.p2, f32(step) / f32(PATH_QUADRATIC_STEPS));
                state = accumulate_path_fill_edge(point, previous, next_point, 0.0001, fill_rule, state);
                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_CUBIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_CUBIC_STEPS) {
                    break;
                }

                let next_point = evaluate_cubic(segment.p0, segment.p1, segment.p2, segment.p3, f32(step) / f32(PATH_CUBIC_STEPS));
                state = accumulate_path_fill_edge(point, previous, next_point, 0.0001, fill_rule, state);
                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_ARC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_ARC_STEPS) {
                    break;
                }

                let next_point = evaluate_arc(segment, f32(step) / f32(PATH_ARC_STEPS));
                state = accumulate_path_fill_edge(point, previous, next_point, 0.0001, fill_rule, state);
                previous = next_point;
                step = step + 1u;
            }
        }

        if (state.boundary) {
            return true;
        }

        segment_index = segment_index + 1u;
    }

    if (fill_rule == FILL_RULE_EVEN_ODD) {
        return state.even_odd;
    }

    return state.winding != 0;
}

fn contains_path_fill(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    let fill_rule = u32(primitive.data1.z + 0.5);
    return contains_path_fill_segments(point, start_segment, segment_count, fill_rule);
}

fn path_fill_segments_intersect_rect_range(rect_min: vec2<f32>, rect_max: vec2<f32>, start_segment: u32, segment_count: u32) -> bool {
    if (segment_count == 0u || start_segment >= query.path_segment_count) {
        return false;
    }

    let end_segment = min(start_segment + segment_count, query.path_segment_count);
    var segment_index = start_segment;
    loop {
        if (segment_index >= end_segment) {
            break;
        }

        let segment = path_segments[segment_index];
        if (segment.segment_type == SEGMENT_LINE) {
            if (segment_intersects_rect(segment.p0, segment.p1, rect_min, rect_max)) {
                return true;
            }
        } else if (segment.segment_type == SEGMENT_QUADRATIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_QUADRATIC_STEPS) {
                    break;
                }

                let next_point = evaluate_quadratic(segment.p0, segment.p1, segment.p2, f32(step) / f32(PATH_QUADRATIC_STEPS));
                if (segment_intersects_rect(previous, next_point, rect_min, rect_max)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_CUBIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_CUBIC_STEPS) {
                    break;
                }

                let next_point = evaluate_cubic(segment.p0, segment.p1, segment.p2, segment.p3, f32(step) / f32(PATH_CUBIC_STEPS));
                if (segment_intersects_rect(previous, next_point, rect_min, rect_max)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_ARC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_ARC_STEPS) {
                    break;
                }

                let next_point = evaluate_arc(segment, f32(step) / f32(PATH_ARC_STEPS));
                if (segment_intersects_rect(previous, next_point, rect_min, rect_max)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        }

        segment_index = segment_index + 1u;
    }

    return false;
}

fn path_fill_segments_intersect_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    return path_fill_segments_intersect_rect_range(rect_min, rect_max, start_segment, segment_count);
}

fn path_fill_segments_intersect_ellipse_region_range(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, start_segment: u32, segment_count: u32) -> bool {
    if (segment_count == 0u || start_segment >= query.path_segment_count) {
        return false;
    }

    let end_segment = min(start_segment + segment_count, query.path_segment_count);
    var segment_index = start_segment;
    loop {
        if (segment_index >= end_segment) {
            break;
        }

        let segment = path_segments[segment_index];
        if (segment.segment_type == SEGMENT_LINE) {
            if (segment_intersects_ellipse_region(segment.p0, segment.p1, query_center, query_inverse_radii)) {
                return true;
            }
        } else if (segment.segment_type == SEGMENT_QUADRATIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_QUADRATIC_STEPS) {
                    break;
                }

                let next_point = evaluate_quadratic(segment.p0, segment.p1, segment.p2, f32(step) / f32(PATH_QUADRATIC_STEPS));
                if (segment_intersects_ellipse_region(previous, next_point, query_center, query_inverse_radii)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_CUBIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_CUBIC_STEPS) {
                    break;
                }

                let next_point = evaluate_cubic(segment.p0, segment.p1, segment.p2, segment.p3, f32(step) / f32(PATH_CUBIC_STEPS));
                if (segment_intersects_ellipse_region(previous, next_point, query_center, query_inverse_radii)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_ARC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_ARC_STEPS) {
                    break;
                }

                let next_point = evaluate_arc(segment, f32(step) / f32(PATH_ARC_STEPS));
                if (segment_intersects_ellipse_region(previous, next_point, query_center, query_inverse_radii)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        }

        segment_index = segment_index + 1u;
    }

    return false;
}

fn path_fill_segments_intersect_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    return path_fill_segments_intersect_ellipse_region_range(query_center, query_inverse_radii, start_segment, segment_count);
}

fn classify_path_fill_rect_intersection_detail_range(rect_min: vec2<f32>, rect_max: vec2<f32>, start_segment: u32, segment_count: u32, fill_rule: u32) -> u32 {
    let top_left = rect_min;
    let top_right = vec2<f32>(rect_max.x, rect_min.y);
    let bottom_right = rect_max;
    let bottom_left = vec2<f32>(rect_min.x, rect_max.y);
    let top_left_inside = contains_path_fill_segments(top_left, start_segment, segment_count, fill_rule);
    let top_right_inside = contains_path_fill_segments(top_right, start_segment, segment_count, fill_rule);
    let bottom_right_inside = contains_path_fill_segments(bottom_right, start_segment, segment_count, fill_rule);
    let bottom_left_inside = contains_path_fill_segments(bottom_left, start_segment, segment_count, fill_rule);
    let path_boundary_intersects_region = path_fill_segments_intersect_rect_range(rect_min, rect_max, start_segment, segment_count);
    if (top_left_inside && top_right_inside && bottom_right_inside && bottom_left_inside) {
        if (!path_boundary_intersects_region) {
            return INTERSECTION_DETAIL_FULLY_CONTAINS;
        }

        return INTERSECTION_DETAIL_INTERSECTS;
    }

    if (top_left_inside || top_right_inside || bottom_right_inside || bottom_left_inside ||
        path_boundary_intersects_region) {
        return INTERSECTION_DETAIL_INTERSECTS;
    }

    return INTERSECTION_DETAIL_EMPTY;
}

fn classify_path_fill_rect_intersection_detail(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> u32 {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    let fill_rule = u32(primitive.data1.z + 0.5);
    return classify_path_fill_rect_intersection_detail_range(rect_min, rect_max, start_segment, segment_count, fill_rule);
}

fn classify_path_fill_ellipse_region_intersection_detail_range(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, start_segment: u32, segment_count: u32, fill_rule: u32) -> u32 {
    let radii = ellipse_radii_from_inverse(query_inverse_radii);
    if (radii.x <= 0.0 || radii.y <= 0.0) {
        return INTERSECTION_DETAIL_EMPTY;
    }

    let boundary_intersects_region = path_fill_segments_intersect_ellipse_region_range(query_center, query_inverse_radii, start_segment, segment_count);
    if (boundary_intersects_region ||
        contains_path_fill_segments(query_center, start_segment, segment_count, fill_rule) ||
        contains_path_fill_segments(query_center + vec2<f32>(radii.x, 0.0), start_segment, segment_count, fill_rule) ||
        contains_path_fill_segments(query_center - vec2<f32>(radii.x, 0.0), start_segment, segment_count, fill_rule) ||
        contains_path_fill_segments(query_center + vec2<f32>(0.0, radii.y), start_segment, segment_count, fill_rule) ||
        contains_path_fill_segments(query_center - vec2<f32>(0.0, radii.y), start_segment, segment_count, fill_rule)) {
        return INTERSECTION_DETAIL_INTERSECTS;
    }

    return INTERSECTION_DETAIL_EMPTY;
}

fn classify_path_fill_ellipse_region_intersection_detail(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> u32 {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    let fill_rule = u32(primitive.data1.z + 0.5);
    return classify_path_fill_ellipse_region_intersection_detail_range(query_center, query_inverse_radii, start_segment, segment_count, fill_rule);
}

fn path_stroke_line_hit(point: vec2<f32>, start: vec2<f32>, end: vec2<f32>, half_stroke: f32) -> bool {
    return distance_squared_to_segment(point, start, end) <= half_stroke * half_stroke;
}

fn contains_path_stroke(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    let stroke = abs(primitive.data1.z);
    if (stroke <= 0.0 || segment_count == 0u || start_segment >= query.path_segment_count) {
        return false;
    }

    let half_stroke = (stroke * 0.5) + max(0.0, primitive.data1.w);
    let end_segment = min(start_segment + segment_count, query.path_segment_count);
    var segment_index = start_segment;
    loop {
        if (segment_index >= end_segment) {
            break;
        }

        let segment = path_segments[segment_index];
        if (segment.segment_type == SEGMENT_LINE) {
            if (path_stroke_line_hit(point, segment.p0, segment.p1, half_stroke)) {
                return true;
            }
        } else if (segment.segment_type == SEGMENT_QUADRATIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_QUADRATIC_STEPS) {
                    break;
                }

                let next_point = evaluate_quadratic(segment.p0, segment.p1, segment.p2, f32(step) / f32(PATH_QUADRATIC_STEPS));
                if (path_stroke_line_hit(point, previous, next_point, half_stroke)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_CUBIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_CUBIC_STEPS) {
                    break;
                }

                let next_point = evaluate_cubic(segment.p0, segment.p1, segment.p2, segment.p3, f32(step) / f32(PATH_CUBIC_STEPS));
                if (path_stroke_line_hit(point, previous, next_point, half_stroke)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_ARC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_ARC_STEPS) {
                    break;
                }

                let next_point = evaluate_arc(segment, f32(step) / f32(PATH_ARC_STEPS));
                if (path_stroke_line_hit(point, previous, next_point, half_stroke)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        }

        segment_index = segment_index + 1u;
    }

    return false;
}

fn path_stroke_intersects_rect(rect_min: vec2<f32>, rect_max: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    let stroke = abs(primitive.data1.z);
    if (stroke <= 0.0 || segment_count == 0u || start_segment >= query.path_segment_count) {
        return false;
    }

    let half_stroke = (stroke * 0.5) + max(0.0, primitive.data1.w);
    let end_segment = min(start_segment + segment_count, query.path_segment_count);
    var segment_index = start_segment;
    loop {
        if (segment_index >= end_segment) {
            break;
        }

        let segment = path_segments[segment_index];
        if (segment.segment_type == SEGMENT_LINE) {
            if (stroked_segment_intersects_rect(segment.p0, segment.p1, rect_min, rect_max, half_stroke)) {
                return true;
            }
        } else if (segment.segment_type == SEGMENT_QUADRATIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_QUADRATIC_STEPS) {
                    break;
                }

                let next_point = evaluate_quadratic(segment.p0, segment.p1, segment.p2, f32(step) / f32(PATH_QUADRATIC_STEPS));
                if (stroked_segment_intersects_rect(previous, next_point, rect_min, rect_max, half_stroke)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_CUBIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_CUBIC_STEPS) {
                    break;
                }

                let next_point = evaluate_cubic(segment.p0, segment.p1, segment.p2, segment.p3, f32(step) / f32(PATH_CUBIC_STEPS));
                if (stroked_segment_intersects_rect(previous, next_point, rect_min, rect_max, half_stroke)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_ARC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_ARC_STEPS) {
                    break;
                }

                let next_point = evaluate_arc(segment, f32(step) / f32(PATH_ARC_STEPS));
                if (stroked_segment_intersects_rect(previous, next_point, rect_min, rect_max, half_stroke)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        }

        segment_index = segment_index + 1u;
    }

    return false;
}

fn path_stroke_intersects_ellipse_region(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    let start_segment = u32(primitive.data1.x + 0.5);
    let segment_count = u32(primitive.data1.y + 0.5);
    let stroke = abs(primitive.data1.z);
    if (stroke <= 0.0 || segment_count == 0u || start_segment >= query.path_segment_count) {
        return false;
    }

    let half_stroke = (stroke * 0.5) + max(0.0, primitive.data1.w);
    let end_segment = min(start_segment + segment_count, query.path_segment_count);
    var segment_index = start_segment;
    loop {
        if (segment_index >= end_segment) {
            break;
        }

        let segment = path_segments[segment_index];
        if (segment.segment_type == SEGMENT_LINE) {
            if (stroked_segment_intersects_ellipse_region(segment.p0, segment.p1, half_stroke, query_center, query_inverse_radii)) {
                return true;
            }
        } else if (segment.segment_type == SEGMENT_QUADRATIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_QUADRATIC_STEPS) {
                    break;
                }

                let next_point = evaluate_quadratic(segment.p0, segment.p1, segment.p2, f32(step) / f32(PATH_QUADRATIC_STEPS));
                if (stroked_segment_intersects_ellipse_region(previous, next_point, half_stroke, query_center, query_inverse_radii)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_CUBIC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_CUBIC_STEPS) {
                    break;
                }

                let next_point = evaluate_cubic(segment.p0, segment.p1, segment.p2, segment.p3, f32(step) / f32(PATH_CUBIC_STEPS));
                if (stroked_segment_intersects_ellipse_region(previous, next_point, half_stroke, query_center, query_inverse_radii)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        } else if (segment.segment_type == SEGMENT_ARC) {
            var previous = segment.p0;
            var step = 1u;
            loop {
                if (step > PATH_ARC_STEPS) {
                    break;
                }

                let next_point = evaluate_arc(segment, f32(step) / f32(PATH_ARC_STEPS));
                if (stroked_segment_intersects_ellipse_region(previous, next_point, half_stroke, query_center, query_inverse_radii)) {
                    return true;
                }

                previous = next_point;
                step = step + 1u;
            }
        }

        segment_index = segment_index + 1u;
    }

    return false;
}

fn primitive_is_hit_test_visible(primitive: HitTestPrimitive) -> bool {
    return (primitive.flags & FLAG_VISIBLE) != 0u && (primitive.flags & FLAG_HIT_TEST_VISIBLE) != 0u;
}

fn primitive_is_axis_aligned(primitive: HitTestPrimitive) -> bool {
    return abs(primitive.inverse_transform0.y) <= 0.00001 && abs(primitive.inverse_transform1.x) <= 0.00001;
}

fn primitive_has_clip(primitive: HitTestPrimitive) -> bool {
    return primitive.clip_segment_count != 0u &&
        primitive.clip_start_segment < query.path_segment_count &&
        primitive.clip_flags != 0u;
}

fn point_inside_primitive_clip(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    if (!primitive_has_clip(primitive)) {
        return true;
    }

    return contains_path_fill_segments(
        point,
        primitive.clip_start_segment,
        primitive.clip_segment_count,
        primitive.clip_fill_rule);
}

fn primitive_clip_intersection_detail_for_bounds(region_min: vec2<f32>, region_max: vec2<f32>, primitive: HitTestPrimitive) -> u32 {
    if (!primitive_has_clip(primitive)) {
        return INTERSECTION_DETAIL_INTERSECTS;
    }

    return classify_path_fill_rect_intersection_detail_range(
        region_min,
        region_max,
        primitive.clip_start_segment,
        primitive.clip_segment_count,
        primitive.clip_fill_rule);
}

fn primitive_clip_intersection_detail_for_ellipse(query_center: vec2<f32>, query_inverse_radii: vec2<f32>, primitive: HitTestPrimitive) -> u32 {
    if (!primitive_has_clip(primitive)) {
        return INTERSECTION_DETAIL_INTERSECTS;
    }

    return classify_path_fill_ellipse_region_intersection_detail_range(
        query_center,
        query_inverse_radii,
        primitive.clip_start_segment,
        primitive.clip_segment_count,
        primitive.clip_fill_rule);
}

fn primitive_can_fully_contain_query_bounds(primitive: HitTestPrimitive) -> bool {
    if (primitive_has_clip(primitive)) {
        return false;
    }

    if (primitive.kind == KIND_BOUNDS) {
        return primitive_is_axis_aligned(primitive);
    }

    if (primitive.kind == KIND_RECT_FILL) {
        return primitive_is_axis_aligned(primitive) && abs(primitive.data1.x) <= 0.00001 && abs(primitive.data1.y) <= 0.00001;
    }

    return false;
}

fn primitive_uses_precise_bounds_region_test(primitive: HitTestPrimitive) -> bool {
    return primitive_has_clip(primitive) ||
        primitive_is_axis_aligned(primitive) &&
        (primitive.kind == KIND_RECT_FILL ||
            primitive.kind == KIND_RECT_STROKE ||
            primitive.kind == KIND_ELLIPSE_FILL ||
            primitive.kind == KIND_ELLIPSE_STROKE ||
            primitive.kind == KIND_LINE_STROKE ||
            primitive.kind == KIND_PATH_FILL ||
            primitive.kind == KIND_PATH_STROKE);
}

fn primitive_uses_precise_ellipse_region_test(primitive: HitTestPrimitive) -> bool {
    return primitive_has_clip(primitive) ||
        primitive_is_axis_aligned(primitive) &&
        (primitive.kind == KIND_BOUNDS ||
            primitive.kind == KIND_RECT_FILL ||
            primitive.kind == KIND_RECT_STROKE ||
            primitive.kind == KIND_ELLIPSE_FILL ||
            primitive.kind == KIND_ELLIPSE_STROKE ||
            primitive.kind == KIND_LINE_STROKE ||
            primitive.kind == KIND_PATH_FILL ||
            primitive.kind == KIND_PATH_STROKE);
}

fn classify_ellipse_region_intersection_detail(primitive: HitTestPrimitive) -> u32 {
    let region_min = query_region_min();
    let region_max = query_region_max();
    if (!rect_intersects_ellipse(primitive.bounds_min, primitive.bounds_max, region_min, region_max)) {
        return INTERSECTION_DETAIL_EMPTY;
    }

    let query_center_for_clip = (region_min + region_max) * 0.5;
    let query_inverse_radii_for_clip = ellipse_inverse_radii_from_bounds(region_min, region_max);
    if (primitive_clip_intersection_detail_for_ellipse(query_center_for_clip, query_inverse_radii_for_clip, primitive) == INTERSECTION_DETAIL_EMPTY) {
        return INTERSECTION_DETAIL_EMPTY;
    }

    if (primitive_uses_precise_ellipse_region_test(primitive)) {
        let local_region_min = transform_bounds_to_local_min(region_min, region_max, primitive);
        let local_region_max = transform_bounds_to_local_max(region_min, region_max, primitive);
        let primitive_min = primitive.data0.xy;
        let primitive_max = primitive.data0.zw;
        if (primitive.kind == KIND_ELLIPSE_FILL) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            let primitive_center = primitive.data2.xy;
            let primitive_inverse_radii = primitive.data2.zw;
            if (!ellipses_may_intersect(query_center, query_inverse_radii, primitive_center, primitive_inverse_radii)) {
                return INTERSECTION_DETAIL_EMPTY;
            }

            if (ellipse_contains_ellipse_same_center(query_center, query_inverse_radii, primitive_center, primitive_inverse_radii)) {
                return INTERSECTION_DETAIL_FULLY_INSIDE;
            }

            if (ellipse_contains_ellipse_same_center(primitive_center, primitive_inverse_radii, query_center, query_inverse_radii)) {
                return INTERSECTION_DETAIL_FULLY_CONTAINS;
            }
        } else if (primitive.kind == KIND_ELLIPSE_STROKE) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            if (!ellipse_stroke_intersects_ellipse_region(query_center, query_inverse_radii, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else if (primitive.kind == KIND_RECT_FILL) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            if (!rounded_rect_intersects_ellipse_region(query_center, query_inverse_radii, primitive_min, primitive_max, primitive.data1.xy)) {
                return INTERSECTION_DETAIL_EMPTY;
            }

            if (abs(primitive.data1.x) <= 0.00001 && abs(primitive.data1.y) <= 0.00001) {
                if (contains_rect_bounds(primitive_min, primitive_max, local_region_min, local_region_max)) {
                    return INTERSECTION_DETAIL_FULLY_CONTAINS;
                }

                if (ellipse_contains_rect(primitive_min, primitive_max, local_region_min, local_region_max)) {
                    return INTERSECTION_DETAIL_FULLY_INSIDE;
                }
            } else {
                if (rounded_rect_contains_ellipse_region(query_center, query_inverse_radii, primitive_min, primitive_max, primitive.data1.xy)) {
                    return INTERSECTION_DETAIL_FULLY_CONTAINS;
                }
            }
        } else if (primitive.kind == KIND_RECT_STROKE) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            if (!rect_stroke_intersects_ellipse_region(query_center, query_inverse_radii, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else if (primitive.kind == KIND_LINE_STROKE) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            if (!line_stroke_intersects_ellipse_region(query_center, query_inverse_radii, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else if (primitive.kind == KIND_PATH_FILL) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            return classify_path_fill_ellipse_region_intersection_detail(query_center, query_inverse_radii, primitive);
        } else if (primitive.kind == KIND_PATH_STROKE) {
            let query_center = (local_region_min + local_region_max) * 0.5;
            let query_inverse_radii = ellipse_inverse_radii_from_bounds(local_region_min, local_region_max);
            if (!path_stroke_intersects_ellipse_region(query_center, query_inverse_radii, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else {
            if (!rect_intersects_ellipse(primitive_min, primitive_max, local_region_min, local_region_max)) {
                return INTERSECTION_DETAIL_EMPTY;
            }

            if (contains_rect_bounds(primitive_min, primitive_max, local_region_min, local_region_max)) {
                return INTERSECTION_DETAIL_FULLY_CONTAINS;
            }

            if (ellipse_contains_rect(primitive_min, primitive_max, local_region_min, local_region_max)) {
                return INTERSECTION_DETAIL_FULLY_INSIDE;
            }
        }
    }

    return INTERSECTION_DETAIL_INTERSECTS;
}

fn classify_bounds_intersection_detail(primitive: HitTestPrimitive) -> u32 {
    if (query_uses_ellipse_region()) {
        return classify_ellipse_region_intersection_detail(primitive);
    }

    let region_min = query_region_min();
    let region_max = query_region_max();
    if (!intersects_bounds(region_min, region_max, primitive.bounds_min, primitive.bounds_max)) {
        return INTERSECTION_DETAIL_EMPTY;
    }

    if (primitive_clip_intersection_detail_for_bounds(region_min, region_max, primitive) == INTERSECTION_DETAIL_EMPTY) {
        return INTERSECTION_DETAIL_EMPTY;
    }

    if (contains_rect_bounds(region_min, region_max, primitive.bounds_min, primitive.bounds_max)) {
        return INTERSECTION_DETAIL_FULLY_INSIDE;
    }

    if (primitive_uses_precise_bounds_region_test(primitive)) {
        let local_region_min = transform_bounds_to_local_min(region_min, region_max, primitive);
        let local_region_max = transform_bounds_to_local_max(region_min, region_max, primitive);
        if (primitive.kind == KIND_RECT_FILL) {
            if (!rect_intersects_rounded_rect(local_region_min, local_region_max, primitive.data0.xy, primitive.data0.zw, primitive.data1.xy)) {
                return INTERSECTION_DETAIL_EMPTY;
            }

            if (rounded_rect_contains_rect(local_region_min, local_region_max, primitive.data0.xy, primitive.data0.zw, primitive.data1.xy)) {
                return INTERSECTION_DETAIL_FULLY_CONTAINS;
            }
        } else if (primitive.kind == KIND_RECT_STROKE) {
            if (!rect_intersects_rect_stroke(local_region_min, local_region_max, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else if (primitive.kind == KIND_ELLIPSE_FILL) {
            if (!rect_intersects_cached_ellipse(local_region_min, local_region_max, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }

            if (cached_ellipse_contains_rect(local_region_min, local_region_max, primitive)) {
                return INTERSECTION_DETAIL_FULLY_CONTAINS;
            }
        } else if (primitive.kind == KIND_ELLIPSE_STROKE) {
            if (!rect_intersects_ellipse_stroke(local_region_min, local_region_max, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else if (primitive.kind == KIND_LINE_STROKE) {
            if (!line_stroke_intersects_rect(local_region_min, local_region_max, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        } else if (primitive.kind == KIND_PATH_FILL) {
            return classify_path_fill_rect_intersection_detail(local_region_min, local_region_max, primitive);
        } else if (primitive.kind == KIND_PATH_STROKE) {
            if (!path_stroke_intersects_rect(local_region_min, local_region_max, primitive)) {
                return INTERSECTION_DETAIL_EMPTY;
            }
        }
    }

    if (primitive_can_fully_contain_query_bounds(primitive) && contains_rect_bounds(primitive.bounds_min, primitive.bounds_max, region_min, region_max)) {
        return INTERSECTION_DETAIL_FULLY_CONTAINS;
    }

    return INTERSECTION_DETAIL_INTERSECTS;
}

fn precise_hit(point: vec2<f32>, primitive: HitTestPrimitive) -> bool {
    if (!primitive_is_hit_test_visible(primitive)) {
        return false;
    }

    if (!contains_bounds(point, primitive.bounds_min, primitive.bounds_max)) {
        return false;
    }

    if (!point_inside_primitive_clip(point, primitive)) {
        return false;
    }

    let local_point = transform_to_local(point, primitive);

    if (primitive.kind == KIND_BOUNDS) {
        return contains_bounds(local_point, primitive.data0.xy, primitive.data0.zw);
    }

    if (primitive.kind == KIND_RECT_FILL) {
        return contains_rounded_rect(local_point, primitive.data0.xy, primitive.data0.zw, primitive.data1.xy);
    }

    if (primitive.kind == KIND_RECT_STROKE) {
        return contains_rect_stroke(local_point, primitive);
    }

    if (primitive.kind == KIND_ELLIPSE_FILL) {
        return contains_cached_ellipse(local_point, primitive);
    }

    if (primitive.kind == KIND_ELLIPSE_STROKE) {
        return contains_ellipse_stroke(local_point, primitive);
    }

    if (primitive.kind == KIND_LINE_STROKE) {
        return contains_line_stroke(local_point, primitive);
    }

    if (primitive.kind == KIND_PATH_FILL) {
        return contains_path_fill(local_point, primitive);
    }

    if (primitive.kind == KIND_PATH_STROKE) {
        return contains_path_stroke(local_point, primitive);
    }

    return false;
}

fn write_hit_result(slot: u32, primitive_index: u32, primitive: HitTestPrimitive, intersection_detail: u32) {
    results[slot].hit = 1u;
    results[slot].id = primitive.id;
    results[slot].primitive_index = primitive_index;
    results[slot].z_index = primitive.z_index;
    results[slot].intersection_detail = intersection_detail;
}

fn clear_hit_result(slot: u32) {
    results[slot].hit = 0u;
    results[slot].id = -1;
    results[slot].primitive_index = 4294967295u;
    results[slot].z_index = -3.4028234663852886e38;
    results[slot].intersection_detail = INTERSECTION_DETAIL_NOT_CALCULATED;
}

fn stored_result_count(capacity: u32) -> u32 {
    var count = 0u;
    loop {
        if (count >= capacity || results[count + 1u].hit == 0u) {
            break;
        }

        count = count + 1u;
    }

    return count;
}

fn find_stored_hit_slot(id: i32, stored_count: u32) -> u32 {
    var slot = 1u;
    loop {
        if (slot > stored_count) {
            break;
        }

        if (results[slot].hit != 0u && results[slot].id == id) {
            return slot;
        }

        slot = slot + 1u;
    }

    return 0u;
}

fn remove_stored_hit_slot(slot: u32, stored_count: u32) {
    var current = slot;
    loop {
        if (current >= stored_count) {
            break;
        }

        results[current] = results[current + 1u];
        current = current + 1u;
    }

    clear_hit_result(stored_count);
}

fn record_hit(primitive_index: u32, primitive: HitTestPrimitive, intersection_detail: u32) {
    let capacity = query_result_capacity();
    if (capacity == 0u) {
        if (results[0].hit == 0u || primitive.z_index >= results[0].z_index) {
            write_hit_result(0u, primitive_index, primitive, intersection_detail);
        }

        return;
    }

    let total_count = results[0].hit + 1u;
    results[0].hit = total_count;

    var stored_count = stored_result_count(capacity);
    let existing_slot = find_stored_hit_slot(primitive.id, stored_count);
    if (existing_slot != 0u) {
        if (results[existing_slot].z_index >= primitive.z_index) {
            return;
        }

        remove_stored_hit_slot(existing_slot, stored_count);
        stored_count = stored_count - 1u;
    }

    if (stored_count >= capacity && primitive.z_index <= results[capacity].z_index) {
        return;
    }

    var result_count = stored_count;
    if (result_count < capacity) {
        result_count = result_count + 1u;
    }

    var slot = result_count;
    loop {
        if (slot <= 1u) {
            break;
        }

        let previous = results[slot - 1u];
        if (previous.hit != 0u && previous.z_index > primitive.z_index) {
            break;
        }

        results[slot] = previous;
        slot = slot - 1u;
    }

    write_hit_result(slot, primitive_index, primitive, intersection_detail);
}

@compute @workgroup_size(1)
fn cs_main(@builtin(global_invocation_id) global_id: vec3<u32>) {
    if (global_id.x != 0u || query.node_count == 0u || query.primitive_count == 0u || !finite2(query.point) || !finite2(query.region_max)) {
        return;
    }

    var stack: array<u32, 64>;
    var stack_count = 1u;
    stack[0] = query.root_node_index;

    loop {
        if (stack_count == 0u) {
            break;
        }

        stack_count = stack_count - 1u;
        let node_index = stack[stack_count];
        if (node_index >= query.node_count) {
            continue;
        }

        let node = nodes[node_index];
        results[0].nodes_visited = results[0].nodes_visited + 1u;
        if (!query_intersects_bounds(node.bounds_min, node.bounds_max)) {
            continue;
        }

        var local_primitive = 0u;
        loop {
            if (local_primitive >= node.primitive_count) {
                break;
            }

            let primitive_lookup = node.first_primitive + local_primitive;
            if (primitive_lookup < query.primitive_index_count) {
                let primitive_index = primitive_indices[primitive_lookup];
                if (primitive_index < query.primitive_count) {
                    let primitive = primitives[primitive_index];
                    if (query_uses_bounds()) {
                        if (primitive_is_hit_test_visible(primitive) && query_intersects_bounds(primitive.bounds_min, primitive.bounds_max)) {
                            results[0].candidate_count = results[0].candidate_count + 1u;
                            if (primitive_uses_precise_bounds_region_test(primitive)) {
                                results[0].precise_tests = results[0].precise_tests + 1u;
                            }

                            let intersection_detail = classify_bounds_intersection_detail(primitive);
                            if (intersection_detail != INTERSECTION_DETAIL_EMPTY) {
                                record_hit(primitive_index, primitive, intersection_detail);
                            }
                        }
                    } else if (contains_bounds(query.point, primitive.bounds_min, primitive.bounds_max)) {
                        results[0].candidate_count = results[0].candidate_count + 1u;
                        results[0].precise_tests = results[0].precise_tests + 1u;
                        if (precise_hit(query.point, primitive)) {
                            record_hit(primitive_index, primitive, INTERSECTION_DETAIL_NOT_CALCULATED);
                        }
                    }
                }
            }

            local_primitive = local_primitive + 1u;
        }

        var child = 0u;
        loop {
            if (child >= node.child_count) {
                break;
            }

            if (stack_count < 64u) {
                stack[stack_count] = node.first_child + child;
                stack_count = stack_count + 1u;
            }

            child = child + 1u;
        }
    }
}
""";
}
