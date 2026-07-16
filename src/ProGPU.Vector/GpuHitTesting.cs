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

        if (primitives.Length == 0)
        {
            return new GpuHitTestIndex(
                CopySpan(primitives),
                [new GpuHitTestNode(Vector2.Zero, Vector2.Zero, 0, 0, 0, 0)],
                [],
                CopySpan(pathSegments));
        }

        Vector2 min = primitives[0].BoundsMin;
        Vector2 max = primitives[0].BoundsMax;
        for (int i = 1; i < primitives.Length; i++)
        {
            min = Vector2.Min(min, primitives[i].BoundsMin);
            max = Vector2.Max(max, primitives[i].BoundsMax);
        }

        var builder = new Builder(primitives, maxDepth, maxPrimitivesPerNode);
        builder.AddRootNode(min, max);
        return new GpuHitTestIndex(
            CopySpan(primitives),
            CopyList(builder.Nodes),
            CopyList(builder.PrimitiveIndices),
            CopySpan(pathSegments));
    }

    private static T[] CopyList<T>(List<T> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<T>();
        }

        var array = new T[values.Count];
        for (int i = 0; i < array.Length; i++)
        {
            array[i] = values[i];
        }

        return array;
    }

    private static T[] CopySpan<T>(ReadOnlySpan<T> values)
    {
        if (values.Length == 0)
        {
            return Array.Empty<T>();
        }

        var array = new T[values.Length];
        values.CopyTo(array);
        return array;
    }

    private ref struct Builder
    {
        private const int MaxPreallocatedNodeCapacity = 65_536;

        private readonly ReadOnlySpan<GpuHitTestPrimitive> _primitives;
        private readonly int _maxDepth;
        private readonly int _maxPrimitivesPerNode;

        public Builder(ReadOnlySpan<GpuHitTestPrimitive> primitives, int maxDepth, int maxPrimitivesPerNode)
        {
            _primitives = primitives;
            _maxDepth = maxDepth;
            _maxPrimitivesPerNode = maxPrimitivesPerNode;
            Nodes = new List<GpuHitTestNode>(EstimateNodeCapacity(primitives.Length, maxPrimitivesPerNode));
            PrimitiveIndices = new List<uint>(primitives.Length);
        }

        public List<GpuHitTestNode> Nodes { get; }
        public List<uint> PrimitiveIndices { get; }

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
            PrimitiveIndexBucket retained = default;
            PrimitiveIndexBucket child0 = default;
            PrimitiveIndexBucket child1 = default;
            PrimitiveIndexBucket child2 = default;
            PrimitiveIndexBucket child3 = default;

            try
            {
                for (int i = 0; i < primitiveCount; i++)
                {
                    int primitiveIndex = primitiveIndices[i];
                    var primitive = _primitives[primitiveIndex];
                    int childIndex = FindContainingChild(primitive.BoundsMin, primitive.BoundsMax, center);
                    if (childIndex >= 0)
                    {
                        AddChildPrimitive(ref child0, ref child1, ref child2, ref child3, childIndex, primitiveIndex);
                    }
                    else
                    {
                        retained.Add(primitiveIndex);
                    }
                }

                int childCount = CountNonEmpty(in child0, in child1, in child2, in child3);
                int retainedCount = retained.Count;
                if (childCount == 0 || HasSingleUnsplitChild(childCount, retainedCount, primitiveCount, in child0, in child1, in child2, in child3))
                {
                    WriteLeaf(nodeIndex, min, max, primitiveIndices);
                    return;
                }

                uint firstPrimitive = (uint)PrimitiveIndices.Count;
                AddPrimitiveIndices(retained);

                uint firstChild = (uint)Nodes.Count;
                int child0NodeIndex = AddChildNodeSlot(in child0);
                int child1NodeIndex = AddChildNodeSlot(in child1);
                int child2NodeIndex = AddChildNodeSlot(in child2);
                int child3NodeIndex = AddChildNodeSlot(in child3);

                Nodes[nodeIndex] = new GpuHitTestNode(
                    min,
                    max,
                    firstChild,
                    (uint)childCount,
                    firstPrimitive,
                    (uint)retainedCount);

                FillChildNode(child0NodeIndex, 0, in child0, min, max, center, depth);
                FillChildNode(child1NodeIndex, 1, in child1, min, max, center, depth);
                FillChildNode(child2NodeIndex, 2, in child2, min, max, center, depth);
                FillChildNode(child3NodeIndex, 3, in child3, min, max, center, depth);
            }
            finally
            {
                retained.Dispose();
                child0.Dispose();
                child1.Dispose();
                child2.Dispose();
                child3.Dispose();
            }
        }

        private void WriteLeaf<TPrimitiveIndices>(int nodeIndex, Vector2 min, Vector2 max, TPrimitiveIndices primitiveIndices)
            where TPrimitiveIndices : struct, IPrimitiveIndexSource
        {
            uint firstPrimitive = (uint)PrimitiveIndices.Count;
            int primitiveCount = primitiveIndices.Count;
            AddPrimitiveIndices(primitiveIndices);

            Nodes[nodeIndex] = new GpuHitTestNode(
                min,
                max,
                firstChild: 0,
                childCount: 0,
                firstPrimitive,
                (uint)primitiveCount);
        }

        private void AddPrimitiveIndices<TPrimitiveIndices>(TPrimitiveIndices primitiveIndices)
            where TPrimitiveIndices : struct, IPrimitiveIndexSource
        {
            int primitiveCount = primitiveIndices.Count;
            for (int i = 0; i < primitiveCount; i++)
            {
                PrimitiveIndices.Add((uint)primitiveIndices[i]);
            }
        }

        private static void AddChildPrimitive(
            ref PrimitiveIndexBucket child0,
            ref PrimitiveIndexBucket child1,
            ref PrimitiveIndexBucket child2,
            ref PrimitiveIndexBucket child3,
            int childIndex,
            int primitiveIndex)
        {
            switch (childIndex)
            {
                case 0:
                    child0.Add(primitiveIndex);
                    break;
                case 1:
                    child1.Add(primitiveIndex);
                    break;
                case 2:
                    child2.Add(primitiveIndex);
                    break;
                default:
                    child3.Add(primitiveIndex);
                    break;
            }
        }

        private int AddChildNodeSlot(in PrimitiveIndexBucket childPrimitives)
        {
            if (childPrimitives.Count == 0)
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
            in PrimitiveIndexBucket childPrimitives,
            Vector2 min,
            Vector2 max,
            Vector2 center,
            int depth)
        {
            if (childNodeIndex < 0 || childPrimitives.Count == 0)
            {
                return;
            }

            var bounds = GetChildBounds(childIndex, min, max, center);
            FillNode(childNodeIndex, bounds.Min, bounds.Max, childPrimitives, depth + 1);
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

        private struct PrimitiveIndexBucket : IPrimitiveIndexSource, IDisposable
        {
            private const int InitialCapacity = 4;

            private int _first;
            private int[]? _items;
            private int _count;

            public readonly int Count => _count;

            public readonly int this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _items is null ? _first : _items[index];
                }
            }

            public void Add(int primitiveIndex)
            {
                if (_items is { } items)
                {
                    if (_count == items.Length)
                    {
                        Grow(items);
                        items = _items!;
                    }

                    items[_count] = primitiveIndex;
                    _count++;
                    return;
                }

                if (_count == 0)
                {
                    _first = primitiveIndex;
                    _count = 1;
                    return;
                }

                int[] rented = ArrayPool<int>.Shared.Rent(InitialCapacity);
                rented[0] = _first;
                rented[1] = primitiveIndex;
                _items = rented;
                _count = 2;
            }

            public void Dispose()
            {
                if (_items is { } items)
                {
                    ArrayPool<int>.Shared.Return(items);
                    _items = null;
                }

                _first = 0;
                _count = 0;
            }

            private void Grow(int[] items)
            {
                int[] expanded = ArrayPool<int>.Shared.Rent(items.Length * 2);
                items.AsSpan(0, _count).CopyTo(expanded);
                ArrayPool<int>.Shared.Return(items);
                _items = expanded;
            }
        }

        private static int EstimateNodeCapacity(int primitiveCount, int maxPrimitivesPerNode)
        {
            int leafEstimate = Math.Max(1, (primitiveCount + maxPrimitivesPerNode - 1) / maxPrimitivesPerNode);
            long estimated = 1L + ((long)leafEstimate * 2L);
            long maxReasonable = Math.Min(((long)primitiveCount * 2L) + 1L, MaxPreallocatedNodeCapacity);
            return (int)Math.Clamp(estimated, 1L, maxReasonable);
        }

        private static int FindContainingChild(Vector2 primitiveMin, Vector2 primitiveMax, Vector2 center)
        {
            bool fitsLeft = primitiveMax.X <= center.X;
            bool fitsRight = primitiveMin.X >= center.X;
            bool fitsTop = primitiveMax.Y <= center.Y;
            bool fitsBottom = primitiveMin.Y >= center.Y;

            if (fitsTop)
            {
                if (fitsLeft)
                {
                    return 0;
                }

                if (fitsRight)
                {
                    return 1;
                }
            }

            if (fitsBottom)
            {
                if (fitsLeft)
                {
                    return 2;
                }

                if (fitsRight)
                {
                    return 3;
                }
            }

            return -1;
        }

        private static int CountNonEmpty(
            in PrimitiveIndexBucket child0,
            in PrimitiveIndexBucket child1,
            in PrimitiveIndexBucket child2,
            in PrimitiveIndexBucket child3)
        {
            return (child0.Count > 0 ? 1 : 0)
                + (child1.Count > 0 ? 1 : 0)
                + (child2.Count > 0 ? 1 : 0)
                + (child3.Count > 0 ? 1 : 0);
        }

        private static bool HasSingleUnsplitChild(
            int childCount,
            int retainedCount,
            int primitiveCount,
            in PrimitiveIndexBucket child0,
            in PrimitiveIndexBucket child1,
            in PrimitiveIndexBucket child2,
            in PrimitiveIndexBucket child3)
        {
            return childCount == 1 &&
                retainedCount == 0 &&
                FirstNonEmptyCount(in child0, in child1, in child2, in child3) == primitiveCount;
        }

        private static int FirstNonEmptyCount(
            in PrimitiveIndexBucket child0,
            in PrimitiveIndexBucket child1,
            in PrimitiveIndexBucket child2,
            in PrimitiveIndexBucket child3)
        {
            return child0.Count > 0 ? child0.Count :
                child1.Count > 0 ? child1.Count :
                child2.Count > 0 ? child2.Count :
                child3.Count;
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
    private const int ResultBufferSizeBytes = 32;
    private const uint ResultBufferSize = ResultBufferSizeBytes;
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
    private const int ResultBufferSizeBytes = 32;
    private const uint ResultBufferSize = ResultBufferSizeBytes;
    private const int HitTestStackReadbackByteLimit = 16 * 1024;

    public static bool TryHitTestPoint(GpuHitTestIndex index, Vector2 point, out GpuHitTestResult result)
    {
        var context = WgpuContext.Current;
        if (context == null && WgpuContext.TryGetFirstActiveContext(out var activeContext))
        {
            context = activeContext;
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
                bindGroupLayout = context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
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
                bindGroup = context.Api.DeviceCreateBindGroup(context.Device, &bgDesc);
                if (bindGroup == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test bind group.");
                }

                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test Encoder") };
                encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
                SilkMarshal.Free((nint)encoderDesc.Label);
                if (encoder == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test command encoder.");
                }

                var passDesc = new ComputePassDescriptor();
                var pass = context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
                context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                context.Api.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                context.Api.ComputePassEncoderEnd(pass);
                context.Api.ComputePassEncoderRelease(pass);

                var commandBufferDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test Submit") };
                commandBuffer = context.Api.CommandEncoderFinish(encoder, &commandBufferDesc);
                SilkMarshal.Free((nint)commandBufferDesc.Label);
                if (commandBuffer == null)
                {
                    throw new InvalidOperationException("Failed to finish GPU hit-test command encoder.");
                }

                context.Api.QueueSubmit(context.Queue, 1, &commandBuffer);
                context.Api.CommandBufferRelease(commandBuffer);
                commandBuffer = null;
                context.Api.CommandEncoderRelease(encoder);
                encoder = null;

                Span<byte> bytes = stackalloc byte[ResultBufferSizeBytes];
                deviceIndex.ResultBuffer.ReadBytes(bytes);
                result = MemoryMarshal.Read<GpuHitTestResult>(bytes);
                return result.HasHit;
            }
            finally
            {
                if (commandBuffer != null)
                {
                    context.Api.CommandBufferRelease(commandBuffer);
                }

                if (encoder != null)
                {
                    context.Api.CommandEncoderRelease(encoder);
                }

                if (bindGroup != null)
                {
                    context.Api.BindGroupRelease(bindGroup);
                }

                if (bindGroupLayout != null)
                {
                    context.Api.BindGroupLayoutRelease(bindGroupLayout);
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
                bindGroupLayout = context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
                var entries = stackalloc BindGroupEntry[6];
                entries[0] = new BindGroupEntry { Binding = 0, Buffer = deviceIndex.QueryBuffer.BufferPtr, Offset = 0, Size = deviceIndex.QueryBuffer.Size };
                entries[1] = new BindGroupEntry { Binding = 1, Buffer = deviceIndex.NodeBuffer.BufferPtr, Offset = 0, Size = deviceIndex.NodeBuffer.Size };
                entries[2] = new BindGroupEntry { Binding = 2, Buffer = deviceIndex.PrimitiveIndexBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PrimitiveIndexBuffer.Size };
                entries[3] = new BindGroupEntry { Binding = 3, Buffer = deviceIndex.PrimitiveBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PrimitiveBuffer.Size };
                entries[4] = new BindGroupEntry { Binding = 4, Buffer = deviceIndex.ResultListBuffer.BufferPtr, Offset = 0, Size = deviceIndex.ResultListBuffer.Size };
                entries[5] = new BindGroupEntry { Binding = 5, Buffer = deviceIndex.PathSegmentBuffer.BufferPtr, Offset = 0, Size = deviceIndex.PathSegmentBuffer.Size };

                var bgDesc = new BindGroupDescriptor
                {
                    Layout = bindGroupLayout,
                    EntryCount = 6,
                    Entries = entries
                };
                bindGroup = context.Api.DeviceCreateBindGroup(context.Device, &bgDesc);
                if (bindGroup == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test bind group.");
                }

                var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test List Encoder") };
                encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDesc);
                SilkMarshal.Free((nint)encoderDesc.Label);
                if (encoder == null)
                {
                    throw new InvalidOperationException("Failed to create GPU hit-test command encoder.");
                }

                var passDesc = new ComputePassDescriptor();
                var pass = context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
                context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
                context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                context.Api.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                context.Api.ComputePassEncoderEnd(pass);
                context.Api.ComputePassEncoderRelease(pass);

                var commandBufferDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("GPU Hit Test List Submit") };
                commandBuffer = context.Api.CommandEncoderFinish(encoder, &commandBufferDesc);
                SilkMarshal.Free((nint)commandBufferDesc.Label);
                if (commandBuffer == null)
                {
                    throw new InvalidOperationException("Failed to finish GPU hit-test command encoder.");
                }

                context.Api.QueueSubmit(context.Queue, 1, &commandBuffer);
                context.Api.CommandBufferRelease(commandBuffer);
                commandBuffer = null;
                context.Api.CommandEncoderRelease(encoder);
                encoder = null;

                int readSizeBytes = checked(initialResults.Length * resultSize);
                byte[]? rentedReadbackBytes = null;
                Span<byte> readbackBytes = readSizeBytes <= HitTestStackReadbackByteLimit
                    ? stackalloc byte[readSizeBytes]
                    : (rentedReadbackBytes = ArrayPool<byte>.Shared.Rent(readSizeBytes)).AsSpan(0, readSizeBytes);
                try
                {
                    deviceIndex.ResultListBuffer.ReadBytes(readbackBytes);
                    summary = MemoryMarshal.Read<GpuHitTestResult>(readbackBytes);
                    hitCount = 0;
                    for (int i = 0; i < requestedCount; i++)
                    {
                        var result = MemoryMarshal.Read<GpuHitTestResult>(readbackBytes[((i + 1) * resultSize)..]);
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
                    if (rentedReadbackBytes != null)
                    {
                        ArrayPool<byte>.Shared.Return(rentedReadbackBytes);
                    }
                }
            }
            finally
            {
                if (commandBuffer != null)
                {
                    context.Api.CommandBufferRelease(commandBuffer);
                }

                if (encoder != null)
                {
                    context.Api.CommandEncoderRelease(encoder);
                }

                if (bindGroup != null)
                {
                    context.Api.BindGroupRelease(bindGroup);
                }

                if (bindGroupLayout != null)
                {
                    context.Api.BindGroupLayoutRelease(bindGroupLayout);
                }

                if (rentedInitialResults != null)
                {
                    ArrayPool<GpuHitTestResult>.Shared.Return(rentedInitialResults);
                }
            }
        }
    }

    internal static readonly string ShaderSource = ShaderResource.Load(typeof(GpuHitTestEngine), "GpuHitTesting.wgsl");
}
