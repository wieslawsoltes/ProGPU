using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProGPU.CAD;

/// <summary>Bounds retained projected-selection index construction.</summary>
public sealed class CadMesh3DSelectionOptions
{
    public const int DefaultMaxTriangles = 10_000_000;
    public const int DefaultMaxSubobjects = 10_000_000;
    public const int DefaultLeafTriangleCount = 8;

    public int MaxTriangles { get; init; } = DefaultMaxTriangles;

    public int MaxSubobjects { get; init; } = DefaultMaxSubobjects;

    public int LeafTriangleCount { get; init; } = DefaultLeafTriangleCount;
}

/// <summary>Immutable construction and residency counters for one 3D index.</summary>
public readonly record struct CadMesh3DSelectionIndexStatistics(
    int TriangleCount,
    int NodeCount,
    int LeafCount,
    int MaximumDepth,
    long RetainedByteCount)
{
    public int SubobjectCount { get; init; }
}

/// <summary>One exact nearest retained-triangle projected-selection result.</summary>
public readonly record struct CadMesh3DSelectionResult(
    bool IsHit,
    ulong ContentGeneration,
    ulong Handle,
    int BatchIndex,
    int TriangleIndex,
    CadPoint3D Point,
    double DistanceFromCamera,
    Vector3 BarycentricCoordinates,
    bool IsFrontFace,
    int VisitedNodeCount,
    int TestedTriangleCount)
{
    internal static CadMesh3DSelectionResult Miss(
        ulong contentGeneration,
        int visitedNodeCount = 0,
        int testedTriangleCount = 0) =>
        new(
            false,
            contentGeneration,
            0,
            -1,
            -1,
            default,
            double.PositiveInfinity,
            default,
            false,
            visitedNodeCount,
            testedTriangleCount);
}

/// <summary>Bounded semantic-hit collection and traversal counters.</summary>
public readonly record struct CadMesh3DSelectionHitQueryResult(
    ulong ContentGeneration,
    int HitCount,
    bool WasTruncated,
    int IntersectedTriangleCount,
    int VisitedNodeCount,
    int TestedTriangleCount);

/// <summary>Exact projected Window/Crossing results and traversal counters.</summary>
public readonly record struct CadMesh3DRegionQueryResult(
    ulong ContentGeneration,
    int HandleWrittenCount,
    int HandleTotalCount,
    int IntersectedTriangleCount,
    int VisitedNodeCount,
    int TestedTriangleCount)
{
    public bool AreHandlesTruncated => HandleWrittenCount != HandleTotalCount;
}

/// <summary>
/// Immutable device-independent triangle accelerator for one retained Mesh3D
/// generation.
/// </summary>
/// <remarks>
/// Construction is O(T log T) time and O(T) storage for T triangles. A query
/// is typically O(log T + H) for H exact triangle candidates and O(T) in the
/// conservative worst case. Warm queries use fixed stack storage and allocate
/// no managed memory.
/// </remarks>
public sealed partial class CadMesh3DSelectionIndex
{
    public const int MaximumHitCount = 256;
    public const int MaximumProjectedPathPointCount = 4_096;
    public const float DefaultPickTargetHeight = 3.0f;
    public const float MaximumPickTargetHeight = 256.0f;

    private const int MortonBitsPerAxis = 10;
    private const int QueryStackCapacity = 64;
    private const double ParallelTolerance = 1e-14;
    private const double BarycentricTolerance = 1e-12;
    private const double TieTolerance = 1e-12;
    private const double ProjectedPredicateTolerance = 1e-9;

    private readonly CadRecordedMesh3DScene _scene;
    private readonly TriangleReference[] _triangles;
    private readonly BvhNode[] _nodes;
    private readonly int[] _batchSemanticRootIndices;
    private readonly SemanticRootReference[] _semanticRoots;
    private readonly SubobjectComponentReference[] _subobjectComponents;
    private readonly int[] _batchSubobjectComponentIndices;
    private readonly CadMesh3DSubobjectId[] _subobjectIds;
    private readonly int[] _subobjectPrimitiveCounts;

    public ulong ContentGeneration => _scene.ContentGeneration;

    public CadPoint3D RebaseOrigin => _scene.RebaseOrigin;

    public int SemanticRootCount => _semanticRoots.Length;

    public int SubobjectCount => _subobjectIds.Length;

    public CadMesh3DSelectionIndexStatistics Statistics { get; }

    private CadMesh3DSelectionIndex(
        CadRecordedMesh3DScene scene,
        TriangleReference[] triangles,
        BvhNode[] nodes,
        int[] batchSemanticRootIndices,
        SemanticRootReference[] semanticRoots,
        SubobjectComponentReference[] subobjectComponents,
        int[] batchSubobjectComponentIndices,
        CadMesh3DSubobjectId[] subobjectIds,
        int[] subobjectPrimitiveCounts,
        CadMesh3DSelectionIndexStatistics statistics)
    {
        _scene = scene;
        _triangles = triangles;
        _nodes = nodes;
        _batchSemanticRootIndices = batchSemanticRootIndices;
        _semanticRoots = semanticRoots;
        _subobjectComponents = subobjectComponents;
        _batchSubobjectComponentIndices = batchSubobjectComponentIndices;
        _subobjectIds = subobjectIds;
        _subobjectPrimitiveCounts = subobjectPrimitiveCounts;
        Statistics = statistics;
    }

    /// <summary>Builds one deterministic Morton-ordered balanced AABB tree.</summary>
    public static CadMesh3DSelectionIndex Build(
        CadRecordedMesh3DScene scene,
        CadMesh3DSelectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new CadMesh3DSelectionOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTriangles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxSubobjects, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.LeafTriangleCount,
            1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.LeafTriangleCount,
            64);

        ReadOnlySpan<CadMesh3DDrawBatch> batches = scene.DrawBatches.Span;
        int triangleCount = 0;
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int indexCount = batches[batchIndex].Indices.Length;
            if (indexCount % 3 != 0)
            {
                throw new InvalidOperationException(
                    "A retained Mesh3D batch does not contain a triangle list.");
            }
            triangleCount = checked(triangleCount + indexCount / 3);
            if (triangleCount > options.MaxTriangles)
            {
                throw new InvalidOperationException(
                    $"CAD 3D selection triangles exceed the configured limit of {options.MaxTriangles}.");
            }
        }

        if (triangleCount == 0)
        {
            return new CadMesh3DSelectionIndex(
                scene,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                new CadMesh3DSelectionIndexStatistics(0, 0, 0, 0, 0));
        }

        var rootByHandle = new Dictionary<ulong, int>(batches.Length);
        var semanticRoots = new List<SemanticRootReference>(batches.Length);
        var batchSemanticRootIndices = new int[batches.Length];
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadMesh3DDrawBatch batch = batches[batchIndex];
            int batchTriangleCount = batch.Indices.Length / 3;
            if (!rootByHandle.TryGetValue(batch.Handle, out int rootIndex))
            {
                rootIndex = semanticRoots.Count;
                rootByHandle.Add(batch.Handle, rootIndex);
                semanticRoots.Add(new SemanticRootReference(
                    batch.Handle,
                    batchTriangleCount));
            }
            else
            {
                SemanticRootReference semanticRoot = semanticRoots[rootIndex];
                semanticRoots[rootIndex] = semanticRoot with
                {
                    TriangleCount = checked(
                        semanticRoot.TriangleCount + batchTriangleCount),
                };
            }
            batchSemanticRootIndices[batchIndex] = rootIndex;
        }

        var items = new TriangleBuildItem[triangleCount];
        Vector3 centroidMinimum = new(float.PositiveInfinity);
        Vector3 centroidMaximum = new(float.NegativeInfinity);
        int destination = 0;
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadMesh3DDrawBatch batch = batches[batchIndex];
            ReadOnlySpan<Vector3> positions = batch.Positions.Span;
            ReadOnlySpan<uint> indices = batch.Indices.Span;
            for (int indexOffset = 0;
                 indexOffset < indices.Length;
                 indexOffset += 3)
            {
                uint firstIndex = indices[indexOffset];
                uint secondIndex = indices[indexOffset + 1];
                uint thirdIndex = indices[indexOffset + 2];
                if (firstIndex >= positions.Length ||
                    secondIndex >= positions.Length ||
                    thirdIndex >= positions.Length)
                {
                    throw new InvalidOperationException(
                        "A retained Mesh3D selection index exceeds its vertex range.");
                }

                Vector3 first = positions[(int)firstIndex];
                Vector3 second = positions[(int)secondIndex];
                Vector3 third = positions[(int)thirdIndex];
                Vector3 minimum = Vector3.Min(first, Vector3.Min(second, third));
                Vector3 maximum = Vector3.Max(first, Vector3.Max(second, third));
                Vector3 centroid = (first + second + third) / 3.0f;
                if (!IsFinite(minimum) || !IsFinite(maximum) || !IsFinite(centroid))
                {
                    throw new InvalidOperationException(
                        "A retained Mesh3D triangle contains a non-finite coordinate.");
                }

                centroidMinimum = Vector3.Min(centroidMinimum, centroid);
                centroidMaximum = Vector3.Max(centroidMaximum, centroid);
                items[destination++] = new TriangleBuildItem(
                    0,
                    batchIndex,
                    indexOffset / 3,
                    centroid,
                    minimum,
                    maximum);
            }
        }

        for (int index = 0; index < items.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            TriangleBuildItem item = items[index];
            item.MortonCode = CreateMortonCode(
                item.Centroid,
                centroidMinimum,
                centroidMaximum);
            items[index] = item;
        }
        Array.Sort(items);

        int leafCount = CountLeafNodes(
            triangleCount,
            options.LeafTriangleCount,
            cancellationToken);
        var nodes = new BvhNode[checked(leafCount * 2 - 1)];
        int nodeCursor = 0;
        int actualLeafCount = 0;
        int maximumDepth = 0;
        int root = BuildNode(
            items,
            nodes,
            ref nodeCursor,
            0,
            items.Length,
            options.LeafTriangleCount,
            1,
            ref actualLeafCount,
            ref maximumDepth,
            cancellationToken);
        if (root != 0 || nodeCursor != nodes.Length || actualLeafCount != leafCount)
        {
            throw new InvalidOperationException(
                "The retained Mesh3D selection tree produced an invalid topology.");
        }

        var triangles = new TriangleReference[triangleCount];
        for (int index = 0; index < items.Length; index++)
        {
            triangles[index] = new TriangleReference(
                items[index].BatchIndex,
                items[index].TriangleIndex);
        }

        BuildSubobjectReferences(
            scene,
            options.MaxSubobjects,
            out SubobjectComponentReference[] subobjectComponents,
            out int[] batchSubobjectComponentIndices,
            out CadMesh3DSubobjectId[] subobjectIds,
            out int[] subobjectPrimitiveCounts);

        long retainedBytes = checked(
            (long)triangles.Length * Unsafe.SizeOf<TriangleReference>() +
            (long)nodes.Length * Unsafe.SizeOf<BvhNode>() +
            (long)batchSemanticRootIndices.Length * sizeof(int) +
            (long)semanticRoots.Count * Unsafe.SizeOf<SemanticRootReference>() +
            (long)subobjectComponents.Length *
                Unsafe.SizeOf<SubobjectComponentReference>() +
            (long)batchSubobjectComponentIndices.Length * sizeof(int) +
            (long)subobjectIds.Length * Unsafe.SizeOf<CadMesh3DSubobjectId>() +
            (long)subobjectPrimitiveCounts.Length * sizeof(int));
        return new CadMesh3DSelectionIndex(
            scene,
            triangles,
            nodes,
            batchSemanticRootIndices,
            semanticRoots.ToArray(),
            subobjectComponents,
            batchSubobjectComponentIndices,
            subobjectIds,
            subobjectPrimitiveCounts,
            new CadMesh3DSelectionIndexStatistics(
                triangleCount,
                nodes.Length,
                leafCount,
                maximumDepth,
                retainedBytes)
            {
                SubobjectCount = subobjectIds.Length,
            });
    }

    /// <summary>
    /// Returns the nearest retained triangle below one logical viewport point.
    /// </summary>
    public CadMesh3DSelectionResult Query(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint)
    {
        if (viewport.RebaseOrigin != RebaseOrigin)
        {
            throw new ArgumentException(
                "The 3D selection viewport does not match the indexed scene rebase origin.",
                nameof(viewport));
        }
        if (!IsFinite(viewportSize) ||
            viewportSize.X <= 0.0f || viewportSize.Y <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportSize),
                "The 3D selection viewport size must be finite and positive.");
        }
        if (!IsFinite(viewportPoint))
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportPoint),
                "The 3D selection point must be finite.");
        }
        if (_nodes.Length == 0 ||
            viewportPoint.X < 0.0f || viewportPoint.X > viewportSize.X ||
            viewportPoint.Y < 0.0f || viewportPoint.Y > viewportSize.Y)
        {
            return CadMesh3DSelectionResult.Miss(ContentGeneration);
        }

        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        Matrix4x4 viewProjection = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(viewportSize.X / viewportSize.Y);
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            throw new InvalidOperationException(
                "The retained Mesh3D view-projection matrix is not invertible.");
        }

        float ndcX = viewportPoint.X / viewportSize.X * 2.0f - 1.0f;
        float ndcY = 1.0f - viewportPoint.Y / viewportSize.Y * 2.0f;
        CadPoint3D nearPoint = Unproject(inverse, ndcX, ndcY, 0.0f);
        CadPoint3D farPoint = Unproject(inverse, ndcX, ndcY, 1.0f);
        CadPoint3D segment = farPoint - nearPoint;
        double maximumDistance = segment.Length;
        if (!double.IsFinite(maximumDistance) || maximumDistance <= 0.0)
        {
            throw new InvalidOperationException(
                "The retained Mesh3D selection ray is not finite and non-degenerate.");
        }
        CadPoint3D direction = segment * (1.0 / maximumDistance);

        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsBounds(
                _nodes[0],
                nearPoint,
                direction,
                maximumDistance,
                out _))
        {
            return CadMesh3DSelectionResult.Miss(ContentGeneration);
        }
        stack[stackCount++] = 0;

        bool hasHit = false;
        int bestBatch = -1;
        int bestTriangle = -1;
        double bestDistance = maximumDistance;
        double bestU = 0.0;
        double bestV = 0.0;
        bool bestFrontFace = false;
        int visitedNodes = 0;
        int testedTriangles = 0;

        while (stackCount > 0)
        {
            int nodeIndex = stack[--stackCount];
            BvhNode node = _nodes[nodeIndex];
            visitedNodes++;
            if (node.Count > 0)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    TriangleReference reference =
                        _triangles[node.Start + offset];
                    testedTriangles++;
                    if (!TryIntersectTriangle(
                            reference,
                            nearPoint,
                            direction,
                            bestDistance,
                            out double distance,
                            out double u,
                            out double v,
                            out bool frontFace))
                    {
                        continue;
                    }

                    double tieWindow = TieTolerance *
                        Math.Max(1.0, Math.Max(distance, bestDistance));
                    bool isNearer = !hasHit || distance < bestDistance - tieWindow;
                    bool isTie = hasHit &&
                        Math.Abs(distance - bestDistance) <= tieWindow;
                    if (!isNearer &&
                        (!isTie ||
                         reference.BatchIndex > bestBatch ||
                         reference.BatchIndex == bestBatch &&
                         reference.TriangleIndex >= bestTriangle))
                    {
                        continue;
                    }

                    hasHit = true;
                    bestBatch = reference.BatchIndex;
                    bestTriangle = reference.TriangleIndex;
                    bestDistance = distance;
                    bestU = u;
                    bestV = v;
                    bestFrontFace = frontFace;
                }
                continue;
            }

            bool hitLeft = IntersectsBounds(
                _nodes[node.Left],
                nearPoint,
                direction,
                bestDistance,
                out double leftDistance);
            bool hitRight = IntersectsBounds(
                _nodes[node.Right],
                nearPoint,
                direction,
                bestDistance,
                out double rightDistance);
            if (!hitLeft && !hitRight)
            {
                continue;
            }
            if (stackCount + (hitLeft && hitRight ? 2 : 1) > stack.Length)
            {
                throw new InvalidOperationException(
                    "The balanced Mesh3D selection tree exceeds its traversal stack contract.");
            }

            if (hitLeft && hitRight)
            {
                if (leftDistance <= rightDistance)
                {
                    stack[stackCount++] = node.Right;
                    stack[stackCount++] = node.Left;
                }
                else
                {
                    stack[stackCount++] = node.Left;
                    stack[stackCount++] = node.Right;
                }
            }
            else
            {
                stack[stackCount++] = hitLeft ? node.Left : node.Right;
            }
        }

        if (!hasHit)
        {
            return CadMesh3DSelectionResult.Miss(
                ContentGeneration,
                visitedNodes,
                testedTriangles);
        }

        CadPoint3D localHit = nearPoint + direction * bestDistance;
        CadPoint3D worldHit = RebaseOrigin + localHit;
        CadPoint3D cameraLocal = new(
            camera.Position.X,
            camera.Position.Y,
            camera.Position.Z);
        double cameraDistance = (localHit - cameraLocal).Length;
        CadMesh3DDrawBatch batch = _scene.DrawBatches.Span[bestBatch];
        return new CadMesh3DSelectionResult(
            true,
            ContentGeneration,
            batch.Handle,
            bestBatch,
            bestTriangle,
            worldHit,
            cameraDistance,
            new Vector3(
                (float)(1.0 - bestU - bestV),
                (float)bestU,
                (float)bestV),
            bestFrontFace,
            visitedNodes,
            testedTriangles);
    }

    /// <summary>
    /// Returns an exact point hit when available, otherwise the nearest
    /// retained triangle intersecting a square projected pick target.
    /// </summary>
    /// <remarks>
    /// <paramref name="targetHeight"/> is the complete logical-pixel target
    /// height, not a radius. Zero preserves exact point-query behavior. The
    /// fallback is typically O(log T + H), conservatively O(T), and uses only
    /// fixed stack storage.
    /// </remarks>
    public CadMesh3DSelectionResult QueryAperture(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        float targetHeight = DefaultPickTargetHeight)
    {
        if (!float.IsFinite(targetHeight) ||
            targetHeight < 0.0f ||
            targetHeight > MaximumPickTargetHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetHeight),
                $"The 3D pick target height must be between 0 and {MaximumPickTargetHeight} logical pixels.");
        }

        CadMesh3DSelectionResult exact = Query(
            viewport,
            viewportSize,
            viewportPoint);
        if (exact.IsHit ||
            targetHeight == 0.0f ||
            _nodes.Length == 0 ||
            viewportPoint.X < 0.0f || viewportPoint.X > viewportSize.X ||
            viewportPoint.Y < 0.0f || viewportPoint.Y > viewportSize.Y)
        {
            return exact;
        }

        Span<CadMesh3DSelectionResult> destination =
            stackalloc CadMesh3DSelectionResult[1];
        CadMesh3DSelectionHitQueryResult fallback = QueryApertureCore(
            viewport,
            viewportSize,
            viewportPoint,
            targetHeight,
            destination,
            exact.VisitedNodeCount,
            exact.TestedTriangleCount);
        return fallback.HitCount == 0
            ? CadMesh3DSelectionResult.Miss(
                ContentGeneration,
                fallback.VisitedNodeCount,
                fallback.TestedTriangleCount)
            : destination[0];
    }

    /// <summary>
    /// Returns nearest-first unique semantic roots below an exact point or,
    /// when that point misses, inside a square projected pick target.
    /// </summary>
    public CadMesh3DSelectionHitQueryResult QueryApertureHits(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        Span<CadMesh3DSelectionResult> destination,
        float targetHeight = DefaultPickTargetHeight)
    {
        if (destination.IsEmpty || destination.Length > MaximumHitCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"The 3D semantic-hit destination must contain between 1 and {MaximumHitCount} entries.");
        }
        if (!float.IsFinite(targetHeight) ||
            targetHeight < 0.0f ||
            targetHeight > MaximumPickTargetHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetHeight),
                $"The 3D pick target height must be between 0 and {MaximumPickTargetHeight} logical pixels.");
        }

        CadMesh3DSelectionHitQueryResult exact = QueryCore(
            viewport,
            viewportSize,
            viewportPoint,
            destination);
        if (exact.HitCount != 0 ||
            targetHeight == 0.0f ||
            _nodes.Length == 0 ||
            viewportPoint.X < 0.0f || viewportPoint.X > viewportSize.X ||
            viewportPoint.Y < 0.0f || viewportPoint.Y > viewportSize.Y)
        {
            return exact;
        }

        return QueryApertureCore(
            viewport,
            viewportSize,
            viewportPoint,
            targetHeight,
            destination,
            exact.VisitedNodeCount,
            exact.TestedTriangleCount);
    }

    /// <summary>
    /// Returns nearest-first unique semantic roots below one logical viewport
    /// point into caller-owned bounded storage.
    /// </summary>
    public CadMesh3DSelectionHitQueryResult QueryHits(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        Span<CadMesh3DSelectionResult> destination)
    {
        if (destination.IsEmpty || destination.Length > MaximumHitCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"The 3D semantic-hit destination must contain between 1 and {MaximumHitCount} entries.");
        }
        return QueryCore(
            viewport,
            viewportSize,
            viewportPoint,
            destination);
    }

    private CadMesh3DSelectionHitQueryResult QueryCore(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        Span<CadMesh3DSelectionResult> destination)
    {
        if (viewport.RebaseOrigin != RebaseOrigin)
        {
            throw new ArgumentException(
                "The 3D selection viewport does not match the indexed scene rebase origin.",
                nameof(viewport));
        }
        if (!IsFinite(viewportSize) ||
            viewportSize.X <= 0.0f || viewportSize.Y <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportSize),
                "The 3D selection viewport size must be finite and positive.");
        }
        if (!IsFinite(viewportPoint))
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportPoint),
                "The 3D selection point must be finite.");
        }
        if (_nodes.Length == 0 ||
            viewportPoint.X < 0.0f || viewportPoint.X > viewportSize.X ||
            viewportPoint.Y < 0.0f || viewportPoint.Y > viewportSize.Y)
        {
            return new CadMesh3DSelectionHitQueryResult(
                ContentGeneration,
                0,
                false,
                0,
                0,
                0);
        }

        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        Matrix4x4 viewProjection = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(viewportSize.X / viewportSize.Y);
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            throw new InvalidOperationException(
                "The retained Mesh3D view-projection matrix is not invertible.");
        }

        float ndcX = viewportPoint.X / viewportSize.X * 2.0f - 1.0f;
        float ndcY = 1.0f - viewportPoint.Y / viewportSize.Y * 2.0f;
        CadPoint3D nearPoint = Unproject(inverse, ndcX, ndcY, 0.0f);
        CadPoint3D farPoint = Unproject(inverse, ndcX, ndcY, 1.0f);
        CadPoint3D segment = farPoint - nearPoint;
        double maximumDistance = segment.Length;
        if (!double.IsFinite(maximumDistance) || maximumDistance <= 0.0)
        {
            throw new InvalidOperationException(
                "The retained Mesh3D selection ray is not finite and non-degenerate.");
        }
        CadPoint3D direction = segment * (1.0 / maximumDistance);

        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsBounds(
                _nodes[0],
                nearPoint,
                direction,
                maximumDistance,
                out _))
        {
            return new CadMesh3DSelectionHitQueryResult(
                ContentGeneration,
                0,
                false,
                0,
                0,
                0);
        }
        stack[stackCount++] = 0;

        int hitCount = 0;
        bool wasTruncated = false;
        int intersectedTriangles = 0;
        int visitedNodes = 0;
        int testedTriangles = 0;

        while (stackCount > 0)
        {
            int nodeIndex = stack[--stackCount];
            BvhNode node = _nodes[nodeIndex];
            visitedNodes++;
            if (node.Count > 0)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    TriangleReference reference =
                        _triangles[node.Start + offset];
                    testedTriangles++;
                    if (!TryIntersectTriangle(
                            reference,
                            nearPoint,
                            direction,
                            maximumDistance,
                            out double distance,
                            out double u,
                            out double v,
                            out bool frontFace))
                    {
                        continue;
                    }

                    intersectedTriangles++;
                    InsertSemanticHit(
                        destination,
                        ref hitCount,
                        ref wasTruncated,
                        CreateHitResult(
                            camera,
                            nearPoint,
                            direction,
                            reference,
                            distance,
                            u,
                            v,
                            frontFace,
                            0,
                            0));
                }
                continue;
            }

            bool hitLeft = IntersectsBounds(
                _nodes[node.Left],
                nearPoint,
                direction,
                maximumDistance,
                out double leftDistance);
            bool hitRight = IntersectsBounds(
                _nodes[node.Right],
                nearPoint,
                direction,
                maximumDistance,
                out double rightDistance);
            if (!hitLeft && !hitRight)
            {
                continue;
            }
            if (stackCount + (hitLeft && hitRight ? 2 : 1) > stack.Length)
            {
                throw new InvalidOperationException(
                    "The balanced Mesh3D selection tree exceeds its traversal stack contract.");
            }

            if (hitLeft && hitRight)
            {
                if (leftDistance <= rightDistance)
                {
                    stack[stackCount++] = node.Right;
                    stack[stackCount++] = node.Left;
                }
                else
                {
                    stack[stackCount++] = node.Left;
                    stack[stackCount++] = node.Right;
                }
            }
            else
            {
                stack[stackCount++] = hitLeft ? node.Left : node.Right;
            }
        }

        for (int index = 0; index < hitCount; index++)
        {
            destination[index] = destination[index] with
            {
                VisitedNodeCount = visitedNodes,
                TestedTriangleCount = testedTriangles,
            };
        }
        return new CadMesh3DSelectionHitQueryResult(
            ContentGeneration,
            hitCount,
            wasTruncated,
            intersectedTriangles,
            visitedNodes,
            testedTriangles);
    }

    private CadMesh3DSelectionResult CreateHitResult(
        in CadMesh3DProjectionCamera camera,
        CadPoint3D nearPoint,
        CadPoint3D direction,
        TriangleReference reference,
        double distance,
        double u,
        double v,
        bool frontFace,
        int visitedNodes,
        int testedTriangles)
    {
        CadPoint3D localHit = nearPoint + direction * distance;
        CadPoint3D cameraLocal = new(
            camera.Position.X,
            camera.Position.Y,
            camera.Position.Z);
        CadMesh3DDrawBatch batch =
            _scene.DrawBatches.Span[reference.BatchIndex];
        return new CadMesh3DSelectionResult(
            true,
            ContentGeneration,
            batch.Handle,
            reference.BatchIndex,
            reference.TriangleIndex,
            RebaseOrigin + localHit,
            (localHit - cameraLocal).Length,
            new Vector3(
                (float)(1.0 - u - v),
                (float)u,
                (float)v),
            frontFace,
            visitedNodes,
            testedTriangles);
    }

    private static void InsertSemanticHit(
        Span<CadMesh3DSelectionResult> destination,
        ref int count,
        ref bool wasTruncated,
        CadMesh3DSelectionResult candidate)
    {
        int existing = -1;
        for (int index = 0; index < count; index++)
        {
            if (destination[index].Handle == candidate.Handle)
            {
                existing = index;
                break;
            }
        }

        if (existing >= 0)
        {
            if (CompareHits(candidate, destination[existing]) >= 0)
            {
                return;
            }
            for (int index = existing; index + 1 < count; index++)
            {
                destination[index] = destination[index + 1];
            }
            count--;
        }
        else if (count == destination.Length)
        {
            wasTruncated = true;
            if (CompareHits(candidate, destination[count - 1]) >= 0)
            {
                return;
            }
            count--;
        }

        int insertion = count;
        while (insertion > 0 &&
               CompareHits(candidate, destination[insertion - 1]) < 0)
        {
            destination[insertion] = destination[insertion - 1];
            insertion--;
        }
        destination[insertion] = candidate;
        count++;
    }

    private static int CompareHits(
        in CadMesh3DSelectionResult first,
        in CadMesh3DSelectionResult second)
    {
        double tieWindow = TieTolerance * Math.Max(
            1.0,
            Math.Max(first.DistanceFromCamera, second.DistanceFromCamera));
        double difference = first.DistanceFromCamera - second.DistanceFromCamera;
        if (Math.Abs(difference) > tieWindow)
        {
            return difference < 0.0 ? -1 : 1;
        }
        int comparison = first.BatchIndex.CompareTo(second.BatchIndex);
        return comparison != 0
            ? comparison
            : first.TriangleIndex.CompareTo(second.TriangleIndex);
    }

    private CadMesh3DSelectionHitQueryResult QueryApertureCore(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        float targetHeight,
        Span<CadMesh3DSelectionResult> destination,
        int priorVisitedNodeCount,
        int priorTestedTriangleCount)
    {
        float halfTarget = targetHeight * 0.5f;
        Vector2 first = Vector2.Clamp(
            viewportPoint - new Vector2(halfTarget),
            Vector2.Zero,
            viewportSize);
        Vector2 second = Vector2.Clamp(
            viewportPoint + new Vector2(halfTarget),
            Vector2.Zero,
            viewportSize);
        var clipRectangle = new ClipRectangle(
            first.X / viewportSize.X * 2.0f - 1.0f,
            second.X / viewportSize.X * 2.0f - 1.0f,
            1.0f - second.Y / viewportSize.Y * 2.0f,
            1.0f - first.Y / viewportSize.Y * 2.0f);
        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        Matrix4x4 viewProjection = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(viewportSize.X / viewportSize.Y);
        ClipVolume clipVolume = CreateClipVolume(
            viewProjection,
            clipRectangle);
        CadPoint3D cameraPoint = ToCadPoint(camera.Position);

        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsClipBounds(_nodes[0], clipVolume))
        {
            return new CadMesh3DSelectionHitQueryResult(
                ContentGeneration,
                0,
                false,
                0,
                priorVisitedNodeCount,
                priorTestedTriangleCount);
        }
        stack[stackCount++] = 0;

        int hitCount = 0;
        bool wasTruncated = false;
        int intersectedTriangles = 0;
        int visitedNodes = priorVisitedNodeCount;
        int testedTriangles = priorTestedTriangleCount;
        while (stackCount > 0)
        {
            int nodeIndex = stack[--stackCount];
            BvhNode node = _nodes[nodeIndex];
            visitedNodes++;
            if (node.Count > 0)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    TriangleReference reference =
                        _triangles[node.Start + offset];
                    testedTriangles++;
                    if (!TryGetClipTriangleClosestPoint(
                            reference,
                            clipVolume,
                            cameraPoint,
                            out CadPoint3D point,
                            out double distance,
                            out Vector3 barycentric,
                            out bool isFrontFace))
                    {
                        continue;
                    }

                    intersectedTriangles++;
                    CadMesh3DDrawBatch batch =
                        _scene.DrawBatches.Span[reference.BatchIndex];
                    InsertSemanticHit(
                        destination,
                        ref hitCount,
                        ref wasTruncated,
                        new CadMesh3DSelectionResult(
                            true,
                            ContentGeneration,
                            batch.Handle,
                            reference.BatchIndex,
                            reference.TriangleIndex,
                            RebaseOrigin + point,
                            distance,
                            barycentric,
                            isFrontFace,
                            0,
                            0));
                }
                continue;
            }

            bool hitLeft = IntersectsClipBounds(
                _nodes[node.Left],
                clipVolume);
            bool hitRight = IntersectsClipBounds(
                _nodes[node.Right],
                clipVolume);
            if (!hitLeft && !hitRight)
            {
                continue;
            }
            if (stackCount + (hitLeft && hitRight ? 2 : 1) > stack.Length)
            {
                throw new InvalidOperationException(
                    "The balanced Mesh3D selection tree exceeds its traversal stack contract.");
            }
            if (hitRight)
            {
                stack[stackCount++] = node.Right;
            }
            if (hitLeft)
            {
                stack[stackCount++] = node.Left;
            }
        }

        for (int index = 0; index < hitCount; index++)
        {
            destination[index] = destination[index] with
            {
                VisitedNodeCount = visitedNodes,
                TestedTriangleCount = testedTriangles,
            };
        }
        return new CadMesh3DSelectionHitQueryResult(
            ContentGeneration,
            hitCount,
            wasTruncated,
            intersectedTriangles,
            visitedNodes,
            testedTriangles);
    }

    /// <summary>
    /// Selects semantic roots through an exact projected rectangular clip
    /// volume. Window requires every retained root triangle to be contained;
    /// Crossing accepts any clipped triangle intersection.
    /// </summary>
    /// <remarks>
    /// <paramref name="semanticRootTriangleScratch"/> must provide at least
    /// <see cref="SemanticRootCount"/> entries and is cleared by the query.
    /// Work is O(R + N + C) for R semantic roots, N visited BVH nodes, and C
    /// tested triangle candidates. Storage is O(R) in caller-owned memory.
    /// </remarks>
    public CadMesh3DRegionQueryResult QueryRegion(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 firstViewportPoint,
        Vector2 secondViewportPoint,
        CadBoundsSelectionMode mode,
        Span<int> semanticRootTriangleScratch,
        Span<ulong> destinationHandles)
    {
        if (viewport.RebaseOrigin != RebaseOrigin)
        {
            throw new ArgumentException(
                "The 3D selection viewport does not match the indexed scene rebase origin.",
                nameof(viewport));
        }
        if (!IsFinite(viewportSize) ||
            viewportSize.X <= 0.0f || viewportSize.Y <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportSize),
                "The 3D selection viewport size must be finite and positive.");
        }
        if (!IsFinite(firstViewportPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(firstViewportPoint));
        }
        if (!IsFinite(secondViewportPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(secondViewportPoint));
        }
        if (mode is not CadBoundsSelectionMode.Window and
            not CadBoundsSelectionMode.Crossing)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (semanticRootTriangleScratch.Length < _semanticRoots.Length)
        {
            throw new ArgumentException(
                $"At least {_semanticRoots.Length} semantic-root scratch entries are required.",
                nameof(semanticRootTriangleScratch));
        }

        Span<int> rootStates = semanticRootTriangleScratch[
            .._semanticRoots.Length];
        rootStates.Clear();
        if (_nodes.Length == 0)
        {
            return new CadMesh3DRegionQueryResult(
                ContentGeneration,
                0,
                0,
                0,
                0,
                0);
        }

        Vector2 clampedFirst = Vector2.Clamp(
            firstViewportPoint,
            Vector2.Zero,
            viewportSize);
        Vector2 clampedSecond = Vector2.Clamp(
            secondViewportPoint,
            Vector2.Zero,
            viewportSize);
        float minimumX = MathF.Min(clampedFirst.X, clampedSecond.X);
        float maximumX = MathF.Max(clampedFirst.X, clampedSecond.X);
        float minimumY = MathF.Min(clampedFirst.Y, clampedSecond.Y);
        float maximumY = MathF.Max(clampedFirst.Y, clampedSecond.Y);
        var clipRectangle = new ClipRectangle(
            minimumX / viewportSize.X * 2.0f - 1.0f,
            maximumX / viewportSize.X * 2.0f - 1.0f,
            1.0f - maximumY / viewportSize.Y * 2.0f,
            1.0f - minimumY / viewportSize.Y * 2.0f);
        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        Matrix4x4 viewProjection = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(viewportSize.X / viewportSize.Y);
        ClipVolume clipVolume = CreateClipVolume(
            viewProjection,
            clipRectangle);

        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsClipBounds(
                _nodes[0],
                clipVolume))
        {
            return new CadMesh3DRegionQueryResult(
                ContentGeneration,
                0,
                0,
                0,
                0,
                0);
        }
        stack[stackCount++] = 0;

        int intersectedTriangles = 0;
        int visitedNodes = 0;
        int testedTriangles = 0;
        while (stackCount > 0)
        {
            int nodeIndex = stack[--stackCount];
            BvhNode node = _nodes[nodeIndex];
            visitedNodes++;
            if (node.Count > 0)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    TriangleReference reference =
                        _triangles[node.Start + offset];
                    testedTriangles++;
                    if (!ClassifyClipTriangle(
                            reference,
                            clipVolume,
                            out bool isContained))
                    {
                        continue;
                    }

                    intersectedTriangles++;
                    int rootIndex =
                        _batchSemanticRootIndices[reference.BatchIndex];
                    if (mode == CadBoundsSelectionMode.Crossing)
                    {
                        rootStates[rootIndex] = 1;
                    }
                    else if (isContained)
                    {
                        rootStates[rootIndex] = checked(
                            rootStates[rootIndex] + 1);
                    }
                }
                continue;
            }

            bool hitLeft = IntersectsClipBounds(
                _nodes[node.Left],
                clipVolume);
            bool hitRight = IntersectsClipBounds(
                _nodes[node.Right],
                clipVolume);
            if (!hitLeft && !hitRight)
            {
                continue;
            }
            if (stackCount + (hitLeft && hitRight ? 2 : 1) > stack.Length)
            {
                throw new InvalidOperationException(
                    "The balanced Mesh3D selection tree exceeds its traversal stack contract.");
            }
            if (hitRight)
            {
                stack[stackCount++] = node.Right;
            }
            if (hitLeft)
            {
                stack[stackCount++] = node.Left;
            }
        }

        int handleWrittenCount = 0;
        int handleTotalCount = 0;
        for (int rootIndex = 0;
             rootIndex < _semanticRoots.Length;
             rootIndex++)
        {
            SemanticRootReference root = _semanticRoots[rootIndex];
            bool isHit = mode == CadBoundsSelectionMode.Crossing
                ? rootStates[rootIndex] != 0
                : rootStates[rootIndex] == root.TriangleCount;
            if (!isHit)
            {
                continue;
            }
            if (handleWrittenCount < destinationHandles.Length)
            {
                destinationHandles[handleWrittenCount++] = root.Handle;
            }
            handleTotalCount++;
        }

        return new CadMesh3DRegionQueryResult(
            ContentGeneration,
            handleWrittenCount,
            handleTotalCount,
            intersectedTriangles,
            visitedNodes,
            testedTriangles);
    }

    /// <summary>
    /// Selects semantic roots through an implicitly closed simple projected
    /// polygon. Window requires complete strict containment; Crossing accepts
    /// any projected overlap.
    /// </summary>
    /// <remarks>
    /// The polygon may be clockwise or counterclockwise but may not touch or
    /// cross itself. Validation is O(P^2). Query work is O(R + N + C*P) for P
    /// polygon points, R semantic roots, N visited nodes, and C candidates.
    /// Storage is bounded fixed stack plus O(R) caller-owned scratch.
    /// </remarks>
    public CadMesh3DRegionQueryResult QueryPolygon(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> polygon,
        CadBoundsSelectionMode mode,
        Span<int> semanticRootTriangleScratch,
        Span<ulong> destinationHandles)
    {
        polygon = NormalizeClosedPath(polygon);
        ValidateProjectedPath(polygon, isClosed: true);
        ValidateSimplePolygon(polygon);
        return QueryProjectedPathCore(
            viewport,
            viewportSize,
            polygon,
            mode,
            isFence: false,
            semanticRootTriangleScratch,
            destinationHandles);
    }

    /// <summary>
    /// Selects semantic roots through an implicitly closed freehand projected
    /// lasso using the even-odd fill rule. A lasso may cross itself.
    /// </summary>
    public CadMesh3DRegionQueryResult QueryLasso(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> lasso,
        CadBoundsSelectionMode mode,
        Span<int> semanticRootTriangleScratch,
        Span<ulong> destinationHandles)
    {
        lasso = NormalizeClosedPath(lasso);
        ValidateProjectedPath(lasso, isClosed: true);
        return QueryProjectedPathCore(
            viewport,
            viewportSize,
            lasso,
            mode,
            isFence: false,
            semanticRootTriangleScratch,
            destinationHandles);
    }

    /// <summary>
    /// Selects semantic roots crossed by an open projected fence. A fence may
    /// cross itself and selects a face when any fence span enters or crosses
    /// its visible projected area.
    /// </summary>
    public CadMesh3DRegionQueryResult QueryFence(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> fence,
        Span<int> semanticRootTriangleScratch,
        Span<ulong> destinationHandles)
    {
        ValidateProjectedPath(fence, isClosed: false);
        return QueryProjectedPathCore(
            viewport,
            viewportSize,
            fence,
            CadBoundsSelectionMode.Crossing,
            isFence: true,
            semanticRootTriangleScratch,
            destinationHandles);
    }

    private CadMesh3DRegionQueryResult QueryProjectedPathCore(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        CadBoundsSelectionMode mode,
        bool isFence,
        Span<int> semanticRootTriangleScratch,
        Span<ulong> destinationHandles)
    {
        ValidateProjectedQuery(
            viewport,
            viewportSize,
            mode,
            semanticRootTriangleScratch);
        Span<int> rootStates = semanticRootTriangleScratch[
            .._semanticRoots.Length];
        rootStates.Clear();
        if (_nodes.Length == 0)
        {
            return new CadMesh3DRegionQueryResult(
                ContentGeneration,
                0,
                0,
                0,
                0,
                0);
        }

        GetProjectedPathBounds(
            path,
            out Vector2 minimum,
            out Vector2 maximum);
        if (isFence)
        {
            // The fence remains mathematically zero-width. This one-pixel
            // expansion is only a conservative BVH/near-far broad phase;
            // exact projected segment/polygon predicates decide every hit.
            minimum -= Vector2.One;
            maximum += Vector2.One;
        }
        if (maximum.X < 0.0f || maximum.Y < 0.0f ||
            minimum.X > viewportSize.X || minimum.Y > viewportSize.Y)
        {
            return new CadMesh3DRegionQueryResult(
                ContentGeneration,
                0,
                0,
                0,
                0,
                0);
        }
        minimum = Vector2.Clamp(minimum, Vector2.Zero, viewportSize);
        maximum = Vector2.Clamp(maximum, Vector2.Zero, viewportSize);
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y)
        {
            return new CadMesh3DRegionQueryResult(
                ContentGeneration,
                0,
                0,
                0,
                0,
                0);
        }

        var clipRectangle = new ClipRectangle(
            minimum.X / viewportSize.X * 2.0f - 1.0f,
            maximum.X / viewportSize.X * 2.0f - 1.0f,
            1.0f - maximum.Y / viewportSize.Y * 2.0f,
            1.0f - minimum.Y / viewportSize.Y * 2.0f);
        CadMesh3DProjectionCamera camera = viewport.CreateProjectionCamera();
        Matrix4x4 viewProjection = camera.CreateViewMatrix() *
            camera.CreateProjectionMatrix(viewportSize.X / viewportSize.Y);
        ClipVolume clipVolume = CreateClipVolume(
            viewProjection,
            clipRectangle);

        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsClipBounds(_nodes[0], clipVolume))
        {
            return new CadMesh3DRegionQueryResult(
                ContentGeneration,
                0,
                0,
                0,
                0,
                0);
        }
        stack[stackCount++] = 0;

        int intersectedTriangles = 0;
        int visitedNodes = 0;
        int testedTriangles = 0;
        while (stackCount > 0)
        {
            int nodeIndex = stack[--stackCount];
            BvhNode node = _nodes[nodeIndex];
            visitedNodes++;
            if (node.Count > 0)
            {
                for (int offset = 0; offset < node.Count; offset++)
                {
                    TriangleReference reference =
                        _triangles[node.Start + offset];
                    testedTriangles++;
                    if (!ClassifyProjectedPathTriangle(
                            reference,
                            clipVolume,
                            viewProjection,
                            viewportSize,
                            path,
                            isFence,
                            out bool isContained))
                    {
                        continue;
                    }

                    intersectedTriangles++;
                    int rootIndex =
                        _batchSemanticRootIndices[reference.BatchIndex];
                    if (isFence || mode == CadBoundsSelectionMode.Crossing)
                    {
                        rootStates[rootIndex] = 1;
                    }
                    else if (isContained)
                    {
                        rootStates[rootIndex] = checked(
                            rootStates[rootIndex] + 1);
                    }
                }
                continue;
            }

            bool hitLeft = IntersectsClipBounds(
                _nodes[node.Left],
                clipVolume);
            bool hitRight = IntersectsClipBounds(
                _nodes[node.Right],
                clipVolume);
            if (!hitLeft && !hitRight)
            {
                continue;
            }
            if (stackCount + (hitLeft && hitRight ? 2 : 1) > stack.Length)
            {
                throw new InvalidOperationException(
                    "The balanced Mesh3D selection tree exceeds its traversal stack contract.");
            }
            if (hitRight)
            {
                stack[stackCount++] = node.Right;
            }
            if (hitLeft)
            {
                stack[stackCount++] = node.Left;
            }
        }

        int handleWrittenCount = 0;
        int handleTotalCount = 0;
        for (int rootIndex = 0;
             rootIndex < _semanticRoots.Length;
             rootIndex++)
        {
            SemanticRootReference root = _semanticRoots[rootIndex];
            bool isHit = isFence || mode == CadBoundsSelectionMode.Crossing
                ? rootStates[rootIndex] != 0
                : rootStates[rootIndex] == root.TriangleCount;
            if (!isHit)
            {
                continue;
            }
            if (handleWrittenCount < destinationHandles.Length)
            {
                destinationHandles[handleWrittenCount++] = root.Handle;
            }
            handleTotalCount++;
        }

        return new CadMesh3DRegionQueryResult(
            ContentGeneration,
            handleWrittenCount,
            handleTotalCount,
            intersectedTriangles,
            visitedNodes,
            testedTriangles);
    }

    private static ReadOnlySpan<Vector2> NormalizeClosedPath(
        ReadOnlySpan<Vector2> path) =>
        path.Length > 1 && path[0] == path[^1]
            ? path[..^1]
            : path;

    private static void ValidateProjectedPath(
        ReadOnlySpan<Vector2> path,
        bool isClosed)
    {
        int minimumCount = isClosed ? 3 : 2;
        if (path.Length < minimumCount ||
            path.Length > MaximumProjectedPathPointCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(path),
                $"A projected selection path must contain between {minimumCount} and {MaximumProjectedPathPointCount} points.");
        }
        for (int index = 0; index < path.Length; index++)
        {
            if (!IsFinite(path[index]))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(path),
                    "Projected selection path points must be finite.");
            }
            if (index > 0 && path[index] == path[index - 1])
            {
                throw new ArgumentException(
                    "Projected selection path spans must be non-degenerate.",
                    nameof(path));
            }
        }
        if (isClosed && path[0] == path[^1])
        {
            throw new ArgumentException(
                "The normalized projected polygon contains a degenerate closing span.",
                nameof(path));
        }
        if (isClosed && !HasNonCollinearSpan(path))
        {
            throw new ArgumentException(
                "A projected selection path must contain non-collinear area.",
                nameof(path));
        }
    }

    private static void ValidateSimplePolygon(ReadOnlySpan<Vector2> polygon)
    {
        for (int firstEdge = 0;
             firstEdge < polygon.Length;
             firstEdge++)
        {
            int firstNext = (firstEdge + 1) % polygon.Length;
            for (int secondEdge = firstEdge + 1;
                 secondEdge < polygon.Length;
                 secondEdge++)
            {
                int secondNext = (secondEdge + 1) % polygon.Length;
                if (firstEdge == secondEdge ||
                    firstNext == secondEdge ||
                    secondNext == firstEdge)
                {
                    continue;
                }
                if (SegmentsIntersectInclusive(
                        polygon[firstEdge],
                        polygon[firstNext],
                        polygon[secondEdge],
                        polygon[secondNext]))
                {
                    throw new ArgumentException(
                        "A projected selection polygon may not touch or cross itself.",
                        nameof(polygon));
                }
            }
        }
    }

    private void ValidateProjectedQuery(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        CadBoundsSelectionMode mode,
        Span<int> semanticRootTriangleScratch)
    {
        if (viewport.RebaseOrigin != RebaseOrigin)
        {
            throw new ArgumentException(
                "The 3D selection viewport does not match the indexed scene rebase origin.",
                nameof(viewport));
        }
        if (!IsFinite(viewportSize) ||
            viewportSize.X <= 0.0f || viewportSize.Y <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewportSize),
                "The 3D selection viewport size must be finite and positive.");
        }
        if (mode is not CadBoundsSelectionMode.Window and
            not CadBoundsSelectionMode.Crossing)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (semanticRootTriangleScratch.Length < _semanticRoots.Length)
        {
            throw new ArgumentException(
                $"At least {_semanticRoots.Length} semantic-root scratch entries are required.",
                nameof(semanticRootTriangleScratch));
        }
    }

    private static void GetProjectedPathBounds(
        ReadOnlySpan<Vector2> path,
        out Vector2 minimum,
        out Vector2 maximum)
    {
        minimum = new Vector2(float.PositiveInfinity);
        maximum = new Vector2(float.NegativeInfinity);
        foreach (Vector2 point in path)
        {
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }
    }

    private static bool HasNonCollinearSpan(ReadOnlySpan<Vector2> path)
    {
        Vector2 first = path[0];
        Vector2 second = path[1];
        for (int third = 2; third < path.Length; third++)
        {
            if (GetOrientation(first, second, path[third]) != 0)
            {
                return true;
            }
        }
        return false;
    }

    private bool ClassifyProjectedPathTriangle(
        TriangleReference reference,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        bool isFence,
        out bool isContained)
    {
        GetTrianglePositions(
            reference,
            out Vector3 first,
            out Vector3 second,
            out Vector3 third);
        Span<Vector3> firstBuffer = stackalloc Vector3[12];
        Span<Vector3> secondBuffer = stackalloc Vector3[12];
        if (!TryClipTriangle(
                first,
                second,
                third,
                clipVolume,
                firstBuffer,
                secondBuffer,
                out int count,
                out bool resultInFirstBuffer,
                out bool isClipContained))
        {
            isContained = false;
            return false;
        }

        Span<Vector3> clipped = resultInFirstBuffer
            ? firstBuffer
            : secondBuffer;
        Span<Vector2> projected = stackalloc Vector2[12];
        for (int index = 0; index < count; index++)
        {
            projected[index] = ProjectToViewport(
                clipped[index],
                viewProjection,
                viewportSize);
        }
        projected = projected[..count];

        if (isFence)
        {
            isContained = false;
            return FenceIntersectsPolygon(path, projected);
        }

        bool overlaps = PolygonsOverlap(path, projected);
        isContained = isClipContained &&
            PolygonStrictlyContainsPolygon(path, projected);
        return overlaps;
    }

    private static Vector2 ProjectToViewport(
        Vector3 point,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize)
    {
        Vector4 clip = Vector4.Transform(
            new Vector4(point, 1.0f),
            viewProjection);
        if (!float.IsFinite(clip.X) ||
            !float.IsFinite(clip.Y) ||
            !float.IsFinite(clip.W) ||
            clip.W <= 0.0f)
        {
            throw new InvalidOperationException(
                "The retained Mesh3D projected path produced a non-finite visible point.");
        }
        float inverseW = 1.0f / clip.W;
        return new Vector2(
            (clip.X * inverseW + 1.0f) * 0.5f * viewportSize.X,
            (1.0f - clip.Y * inverseW) * 0.5f * viewportSize.Y);
    }

    private static bool PolygonsOverlap(
        ReadOnlySpan<Vector2> first,
        ReadOnlySpan<Vector2> second)
    {
        for (int firstEdge = 0; firstEdge < first.Length; firstEdge++)
        {
            Vector2 firstStart = first[firstEdge];
            Vector2 firstEnd = first[(firstEdge + 1) % first.Length];
            for (int secondEdge = 0;
                 secondEdge < second.Length;
                 secondEdge++)
            {
                if (SegmentsIntersectInclusive(
                        firstStart,
                        firstEnd,
                        second[secondEdge],
                        second[(secondEdge + 1) % second.Length]))
                {
                    return true;
                }
            }
        }
        return GetPointLocation(first[0], second) != PointLocation.Outside ||
            GetPointLocation(second[0], first) != PointLocation.Outside;
    }

    private static bool PolygonStrictlyContainsPolygon(
        ReadOnlySpan<Vector2> container,
        ReadOnlySpan<Vector2> candidate)
    {
        foreach (Vector2 point in candidate)
        {
            if (GetPointLocation(point, container) != PointLocation.Inside)
            {
                return false;
            }
        }
        for (int candidateEdge = 0;
             candidateEdge < candidate.Length;
             candidateEdge++)
        {
            Vector2 candidateStart = candidate[candidateEdge];
            Vector2 candidateEnd =
                candidate[(candidateEdge + 1) % candidate.Length];
            for (int containerEdge = 0;
                 containerEdge < container.Length;
                 containerEdge++)
            {
                if (SegmentsIntersectInclusive(
                        candidateStart,
                        candidateEnd,
                        container[containerEdge],
                        container[(containerEdge + 1) % container.Length]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool FenceIntersectsPolygon(
        ReadOnlySpan<Vector2> fence,
        ReadOnlySpan<Vector2> polygon)
    {
        for (int fenceIndex = 0;
             fenceIndex + 1 < fence.Length;
             fenceIndex++)
        {
            Vector2 first = fence[fenceIndex];
            Vector2 second = fence[fenceIndex + 1];
            if (GetPointLocation(first, polygon) != PointLocation.Outside ||
                GetPointLocation(second, polygon) != PointLocation.Outside)
            {
                return true;
            }
            for (int polygonEdge = 0;
                 polygonEdge < polygon.Length;
                 polygonEdge++)
            {
                if (SegmentsIntersectInclusive(
                        first,
                        second,
                        polygon[polygonEdge],
                        polygon[(polygonEdge + 1) % polygon.Length]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static PointLocation GetPointLocation(
        Vector2 point,
        ReadOnlySpan<Vector2> polygon)
    {
        bool isInside = false;
        Vector2 previous = polygon[^1];
        foreach (Vector2 current in polygon)
        {
            if (PointOnSegment(point, previous, current))
            {
                return PointLocation.Boundary;
            }
            bool crosses = (previous.Y > point.Y) !=
                (current.Y > point.Y);
            if (crosses)
            {
                double intersectionX = previous.X +
                    ((double)point.Y - previous.Y) *
                    ((double)current.X - previous.X) /
                    ((double)current.Y - previous.Y);
                if (intersectionX > point.X)
                {
                    isInside = !isInside;
                }
            }
            previous = current;
        }
        return isInside ? PointLocation.Inside : PointLocation.Outside;
    }

    private static bool SegmentsIntersectInclusive(
        Vector2 firstStart,
        Vector2 firstEnd,
        Vector2 secondStart,
        Vector2 secondEnd)
    {
        int firstOrientation = GetOrientation(
            firstStart,
            firstEnd,
            secondStart);
        int secondOrientation = GetOrientation(
            firstStart,
            firstEnd,
            secondEnd);
        int thirdOrientation = GetOrientation(
            secondStart,
            secondEnd,
            firstStart);
        int fourthOrientation = GetOrientation(
            secondStart,
            secondEnd,
            firstEnd);
        if (firstOrientation * secondOrientation < 0 &&
            thirdOrientation * fourthOrientation < 0)
        {
            return true;
        }
        return firstOrientation == 0 &&
                PointOnSegment(secondStart, firstStart, firstEnd) ||
            secondOrientation == 0 &&
                PointOnSegment(secondEnd, firstStart, firstEnd) ||
            thirdOrientation == 0 &&
                PointOnSegment(firstStart, secondStart, secondEnd) ||
            fourthOrientation == 0 &&
                PointOnSegment(firstEnd, secondStart, secondEnd);
    }

    private static bool PointOnSegment(
        Vector2 point,
        Vector2 first,
        Vector2 second)
    {
        if (GetOrientation(first, second, point) != 0)
        {
            return false;
        }
        double tolerance = GetProjectedCoordinateTolerance(
            first,
            second,
            point);
        return point.X >= Math.Min(first.X, second.X) - tolerance &&
            point.X <= Math.Max(first.X, second.X) + tolerance &&
            point.Y >= Math.Min(first.Y, second.Y) - tolerance &&
            point.Y <= Math.Max(first.Y, second.Y) + tolerance;
    }

    private static int GetOrientation(
        Vector2 first,
        Vector2 second,
        Vector2 third)
    {
        double cross =
            ((double)second.X - first.X) *
                ((double)third.Y - first.Y) -
            ((double)second.Y - first.Y) *
                ((double)third.X - first.X);
        double tolerance = GetProjectedOrientationTolerance(
            first,
            second,
            third);
        return cross > tolerance ? 1 : cross < -tolerance ? -1 : 0;
    }

    private static double GetProjectedOrientationTolerance(
        Vector2 first,
        Vector2 second,
        Vector2 third)
    {
        double scale = GetProjectedCoordinateScale(
            first,
            second,
            third);
        return ProjectedPredicateTolerance * scale * scale;
    }

    private static double GetProjectedCoordinateTolerance(
        Vector2 first,
        Vector2 second,
        Vector2 third) =>
        ProjectedPredicateTolerance * GetProjectedCoordinateScale(
            first,
            second,
            third);

    private static double GetProjectedCoordinateScale(
        Vector2 first,
        Vector2 second,
        Vector2 third) => Math.Max(
            1.0,
            Math.Max(
                Math.Max(
                    Math.Abs((double)second.X - first.X),
                    Math.Abs((double)second.Y - first.Y)),
                Math.Max(
                    Math.Abs((double)third.X - first.X),
                    Math.Abs((double)third.Y - first.Y))));

    private bool ClassifyClipTriangle(
        TriangleReference reference,
        in ClipVolume clipVolume,
        out bool isContained)
    {
        GetTrianglePositions(
            reference,
            out Vector3 first,
            out Vector3 second,
            out Vector3 third);
        Span<Vector3> firstBuffer = stackalloc Vector3[12];
        Span<Vector3> secondBuffer = stackalloc Vector3[12];
        return TryClipTriangle(
            first,
            second,
            third,
            clipVolume,
            firstBuffer,
            secondBuffer,
            out _,
            out _,
            out isContained);
    }

    private bool TryGetClipTriangleClosestPoint(
        TriangleReference reference,
        in ClipVolume clipVolume,
        CadPoint3D cameraPoint,
        out CadPoint3D closestPoint,
        out double distance,
        out Vector3 barycentric,
        out bool isFrontFace)
    {
        GetTrianglePositions(
            reference,
            out Vector3 first,
            out Vector3 second,
            out Vector3 third);
        Span<Vector3> firstBuffer = stackalloc Vector3[12];
        Span<Vector3> secondBuffer = stackalloc Vector3[12];
        if (!TryClipTriangle(
                first,
                second,
                third,
                clipVolume,
                firstBuffer,
                secondBuffer,
                out int count,
                out bool resultInFirstBuffer,
                out _))
        {
            closestPoint = default;
            distance = double.PositiveInfinity;
            barycentric = default;
            isFrontFace = false;
            return false;
        }

        Span<Vector3> polygon = resultInFirstBuffer
            ? firstBuffer
            : secondBuffer;
        CadPoint3D polygonFirst = ToCadPoint(polygon[0]);
        closestPoint = polygonFirst;
        double bestSquaredDistance = GetSquaredLength(
            polygonFirst - cameraPoint);
        for (int index = 0; index < count; index++)
        {
            CadPoint3D candidate = ClosestPointOnSegment(
                cameraPoint,
                ToCadPoint(polygon[index]),
                ToCadPoint(polygon[(index + 1) % count]));
            double squaredDistance = GetSquaredLength(
                candidate - cameraPoint);
            if (squaredDistance < bestSquaredDistance)
            {
                closestPoint = candidate;
                bestSquaredDistance = squaredDistance;
            }
        }
        for (int index = 1; index + 1 < count; index++)
        {
            CadPoint3D candidate = ClosestPointOnTriangle(
                cameraPoint,
                polygonFirst,
                ToCadPoint(polygon[index]),
                ToCadPoint(polygon[index + 1]));
            double squaredDistance = GetSquaredLength(
                candidate - cameraPoint);
            if (squaredDistance < bestSquaredDistance)
            {
                closestPoint = candidate;
                bestSquaredDistance = squaredDistance;
            }
        }
        if (!double.IsFinite(bestSquaredDistance))
        {
            distance = double.PositiveInfinity;
            barycentric = default;
            isFrontFace = false;
            return false;
        }

        CadPoint3D originalFirst = ToCadPoint(first);
        CadPoint3D firstEdge = ToCadPoint(second) - originalFirst;
        CadPoint3D secondEdge = ToCadPoint(third) - originalFirst;
        CadPoint3D fromFirst = closestPoint - originalFirst;
        double firstFirst = CadPoint3D.Dot(firstEdge, firstEdge);
        double firstSecond = CadPoint3D.Dot(firstEdge, secondEdge);
        double secondSecond = CadPoint3D.Dot(secondEdge, secondEdge);
        double pointFirst = CadPoint3D.Dot(fromFirst, firstEdge);
        double pointSecond = CadPoint3D.Dot(fromFirst, secondEdge);
        double denominator = firstFirst * secondSecond -
            firstSecond * firstSecond;
        if (!double.IsFinite(denominator) || denominator <= 0.0)
        {
            distance = double.PositiveInfinity;
            barycentric = default;
            isFrontFace = false;
            return false;
        }
        double secondWeight =
            (secondSecond * pointFirst - firstSecond * pointSecond) /
            denominator;
        double thirdWeight =
            (firstFirst * pointSecond - firstSecond * pointFirst) /
            denominator;
        secondWeight = Math.Clamp(secondWeight, 0.0, 1.0);
        thirdWeight = Math.Clamp(thirdWeight, 0.0, 1.0 - secondWeight);
        barycentric = new Vector3(
            (float)(1.0 - secondWeight - thirdWeight),
            (float)secondWeight,
            (float)thirdWeight);
        CadPoint3D normal = CadPoint3D.Cross(firstEdge, secondEdge);
        isFrontFace = CadPoint3D.Dot(
            normal,
            closestPoint - cameraPoint) < 0.0;
        distance = Math.Sqrt(bestSquaredDistance);
        return double.IsFinite(distance);
    }

    private static bool TryClipTriangle(
        Vector3 first,
        Vector3 second,
        Vector3 third,
        in ClipVolume clipVolume,
        Span<Vector3> firstBuffer,
        Span<Vector3> secondBuffer,
        out int count,
        out bool resultInFirstBuffer,
        out bool isContained)
    {
        int firstOutside = GetOutsideMask(first, clipVolume);
        int secondOutside = GetOutsideMask(second, clipVolume);
        int thirdOutside = GetOutsideMask(third, clipVolume);
        isContained = (firstOutside | secondOutside | thirdOutside) == 0;
        firstBuffer[0] = first;
        firstBuffer[1] = second;
        firstBuffer[2] = third;
        count = 3;
        resultInFirstBuffer = true;
        if (isContained)
        {
            return true;
        }
        if ((firstOutside & secondOutside & thirdOutside) != 0)
        {
            count = 0;
            return false;
        }

        Span<Vector3> source = firstBuffer;
        Span<Vector3> destination = secondBuffer;
        for (int plane = 0; plane < 6; plane++)
        {
            int outputCount = 0;
            Vector4 clipPlane = clipVolume.GetPlane(plane);
            Vector3 previous = source[count - 1];
            double previousDistance = GetClipDistance(
                previous,
                clipPlane);
            bool previousInside = previousDistance >= 0.0;
            for (int index = 0; index < count; index++)
            {
                Vector3 current = source[index];
                double currentDistance = GetClipDistance(
                    current,
                    clipPlane);
                bool currentInside = currentDistance >= 0.0;
                if (currentInside != previousInside)
                {
                    double denominator = previousDistance - currentDistance;
                    if (denominator == 0.0 || outputCount >= destination.Length)
                    {
                        throw new InvalidOperationException(
                            "The projected Mesh3D clipping polygon exceeded its bounded contract.");
                    }
                    float parameter = (float)(previousDistance / denominator);
                    destination[outputCount++] = previous +
                        (current - previous) * parameter;
                }
                if (currentInside)
                {
                    if (outputCount >= destination.Length)
                    {
                        throw new InvalidOperationException(
                            "The projected Mesh3D clipping polygon exceeded its bounded contract.");
                    }
                    destination[outputCount++] = current;
                }
                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }
            if (outputCount == 0)
            {
                count = 0;
                return false;
            }
            Span<Vector3> swap = source;
            source = destination;
            destination = swap;
            count = outputCount;
            resultInFirstBuffer = !resultInFirstBuffer;
        }
        return true;
    }

    private static CadPoint3D ClosestPointOnTriangle(
        CadPoint3D point,
        CadPoint3D first,
        CadPoint3D second,
        CadPoint3D third)
    {
        CadPoint3D firstEdge = second - first;
        CadPoint3D secondEdge = third - first;
        CadPoint3D fromFirst = point - first;
        double firstDot = CadPoint3D.Dot(firstEdge, fromFirst);
        double secondDot = CadPoint3D.Dot(secondEdge, fromFirst);
        if (firstDot <= 0.0 && secondDot <= 0.0)
        {
            return first;
        }

        CadPoint3D fromSecond = point - second;
        double thirdDot = CadPoint3D.Dot(firstEdge, fromSecond);
        double fourthDot = CadPoint3D.Dot(secondEdge, fromSecond);
        if (thirdDot >= 0.0 && fourthDot <= thirdDot)
        {
            return second;
        }

        double firstArea = firstDot * fourthDot - thirdDot * secondDot;
        if (firstArea <= 0.0 && firstDot >= 0.0 && thirdDot <= 0.0)
        {
            double denominator = firstDot - thirdDot;
            return denominator == 0.0
                ? first
                : firstEdge * (firstDot / denominator) + first;
        }

        CadPoint3D fromThird = point - third;
        double fifthDot = CadPoint3D.Dot(firstEdge, fromThird);
        double sixthDot = CadPoint3D.Dot(secondEdge, fromThird);
        if (sixthDot >= 0.0 && fifthDot <= sixthDot)
        {
            return third;
        }

        double secondArea = fifthDot * secondDot - firstDot * sixthDot;
        if (secondArea <= 0.0 && secondDot >= 0.0 && sixthDot <= 0.0)
        {
            double denominator = secondDot - sixthDot;
            return denominator == 0.0
                ? first
                : secondEdge * (secondDot / denominator) + first;
        }

        double thirdArea = thirdDot * sixthDot - fifthDot * fourthDot;
        if (thirdArea <= 0.0 &&
            fourthDot - thirdDot >= 0.0 &&
            fifthDot - sixthDot >= 0.0)
        {
            CadPoint3D edge = third - second;
            double denominator =
                (fourthDot - thirdDot) + (fifthDot - sixthDot);
            return denominator == 0.0
                ? second
                : edge * ((fourthDot - thirdDot) / denominator) + second;
        }

        double sum = firstArea + secondArea + thirdArea;
        if (!double.IsFinite(sum) || sum == 0.0)
        {
            CadPoint3D firstCandidate = ClosestPointOnSegment(
                point,
                first,
                second);
            CadPoint3D secondCandidate = ClosestPointOnSegment(
                point,
                second,
                third);
            CadPoint3D thirdCandidate = ClosestPointOnSegment(
                point,
                third,
                first);
            double firstDistance = GetSquaredLength(firstCandidate - point);
            double secondDistance = GetSquaredLength(secondCandidate - point);
            double thirdDistance = GetSquaredLength(thirdCandidate - point);
            return firstDistance <= secondDistance && firstDistance <= thirdDistance
                ? firstCandidate
                : secondDistance <= thirdDistance
                    ? secondCandidate
                    : thirdCandidate;
        }
        double inverseSum = 1.0 / sum;
        double secondWeight = secondArea * inverseSum;
        double thirdWeight = firstArea * inverseSum;
        return first + firstEdge * secondWeight + secondEdge * thirdWeight;
    }

    private static CadPoint3D ClosestPointOnSegment(
        CadPoint3D point,
        CadPoint3D first,
        CadPoint3D second)
    {
        CadPoint3D segment = second - first;
        double denominator = GetSquaredLength(segment);
        if (!double.IsFinite(denominator) || denominator <= 0.0)
        {
            return first;
        }
        double parameter = Math.Clamp(
            CadPoint3D.Dot(point - first, segment) / denominator,
            0.0,
            1.0);
        return first + segment * parameter;
    }

    private static double GetSquaredLength(CadPoint3D value) =>
        CadPoint3D.Dot(value, value);

    private void GetTrianglePositions(
        TriangleReference reference,
        out Vector3 first,
        out Vector3 second,
        out Vector3 third)
    {
        CadMesh3DDrawBatch batch =
            _scene.DrawBatches.Span[reference.BatchIndex];
        ReadOnlySpan<Vector3> positions = batch.Positions.Span;
        ReadOnlySpan<uint> indices = batch.Indices.Span;
        int indexOffset = checked(reference.TriangleIndex * 3);
        first = positions[(int)indices[indexOffset]];
        second = positions[(int)indices[indexOffset + 1]];
        third = positions[(int)indices[indexOffset + 2]];
    }

    private static bool IntersectsClipBounds(
        in BvhNode node,
        in ClipVolume clipVolume)
    {
        for (int planeIndex = 0; planeIndex < 6; planeIndex++)
        {
            Vector4 plane = clipVolume.GetPlane(planeIndex);
            var support = new Vector3(
                plane.X >= 0.0f ? node.Maximum.X : node.Minimum.X,
                plane.Y >= 0.0f ? node.Maximum.Y : node.Minimum.Y,
                plane.Z >= 0.0f ? node.Maximum.Z : node.Minimum.Z);
            if (GetClipDistance(support, plane) < 0.0)
            {
                return false;
            }
        }
        return true;
    }

    private static ClipVolume CreateClipVolume(
        in Matrix4x4 viewProjection,
        in ClipRectangle rectangle)
    {
        var clipX = new Vector4(
            viewProjection.M11,
            viewProjection.M21,
            viewProjection.M31,
            viewProjection.M41);
        var clipY = new Vector4(
            viewProjection.M12,
            viewProjection.M22,
            viewProjection.M32,
            viewProjection.M42);
        var clipZ = new Vector4(
            viewProjection.M13,
            viewProjection.M23,
            viewProjection.M33,
            viewProjection.M43);
        var clipW = new Vector4(
            viewProjection.M14,
            viewProjection.M24,
            viewProjection.M34,
            viewProjection.M44);
        var result = new ClipVolume(
            clipX - rectangle.Left * clipW,
            rectangle.Right * clipW - clipX,
            clipY - rectangle.Bottom * clipW,
            rectangle.Top * clipW - clipY,
            clipZ,
            clipW - clipZ);
        for (int index = 0; index < 6; index++)
        {
            Vector4 plane = result.GetPlane(index);
            if (!float.IsFinite(plane.X) ||
                !float.IsFinite(plane.Y) ||
                !float.IsFinite(plane.Z) ||
                !float.IsFinite(plane.W))
            {
                throw new InvalidOperationException(
                    "The retained Mesh3D selection matrix produced a non-finite clip plane.");
            }
        }
        return result;
    }

    private static int GetOutsideMask(
        Vector3 point,
        in ClipVolume clipVolume)
    {
        int result = 0;
        for (int plane = 0; plane < 6; plane++)
        {
            if (GetClipDistance(point, clipVolume.GetPlane(plane)) < 0.0)
            {
                result |= 1 << plane;
            }
        }
        return result;
    }

    private static double GetClipDistance(
        Vector3 point,
        Vector4 plane) =>
        (double)point.X * plane.X +
        (double)point.Y * plane.Y +
        (double)point.Z * plane.Z +
        plane.W;

    private bool TryIntersectTriangle(
        TriangleReference reference,
        CadPoint3D origin,
        CadPoint3D direction,
        double maximumDistance,
        out double distance,
        out double u,
        out double v,
        out bool isFrontFace)
    {
        CadMesh3DDrawBatch batch =
            _scene.DrawBatches.Span[reference.BatchIndex];
        ReadOnlySpan<Vector3> positions = batch.Positions.Span;
        ReadOnlySpan<uint> indices = batch.Indices.Span;
        int indexOffset = checked(reference.TriangleIndex * 3);
        CadPoint3D first = ToCadPoint(positions[(int)indices[indexOffset]]);
        CadPoint3D second = ToCadPoint(positions[(int)indices[indexOffset + 1]]);
        CadPoint3D third = ToCadPoint(positions[(int)indices[indexOffset + 2]]);
        CadPoint3D firstEdge = second - first;
        CadPoint3D secondEdge = third - first;
        CadPoint3D determinantVector = CadPoint3D.Cross(direction, secondEdge);
        double determinant = CadPoint3D.Dot(firstEdge, determinantVector);
        double determinantScale = firstEdge.Length * secondEdge.Length;
        if (!double.IsFinite(determinantScale) ||
            determinantScale <= 0.0 ||
            Math.Abs(determinant) <= ParallelTolerance * determinantScale)
        {
            distance = 0.0;
            u = 0.0;
            v = 0.0;
            isFrontFace = false;
            return false;
        }

        double inverseDeterminant = 1.0 / determinant;
        CadPoint3D fromFirst = origin - first;
        u = CadPoint3D.Dot(fromFirst, determinantVector) * inverseDeterminant;
        if (u < -BarycentricTolerance || u > 1.0 + BarycentricTolerance)
        {
            distance = 0.0;
            v = 0.0;
            isFrontFace = determinant > 0.0;
            return false;
        }
        CadPoint3D cross = CadPoint3D.Cross(fromFirst, firstEdge);
        v = CadPoint3D.Dot(direction, cross) * inverseDeterminant;
        if (v < -BarycentricTolerance ||
            u + v > 1.0 + BarycentricTolerance)
        {
            distance = 0.0;
            isFrontFace = determinant > 0.0;
            return false;
        }
        distance = CadPoint3D.Dot(secondEdge, cross) * inverseDeterminant;
        isFrontFace = determinant > 0.0;
        if (!double.IsFinite(distance) ||
            distance < 0.0 || distance > maximumDistance)
        {
            return false;
        }

        u = Math.Clamp(u, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0 - u);
        return true;
    }

    private static bool IntersectsBounds(
        BvhNode node,
        CadPoint3D origin,
        CadPoint3D direction,
        double maximumDistance,
        out double entryDistance)
    {
        double minimum = 0.0;
        double maximum = maximumDistance;
        if (!IntersectAxis(
                origin.X,
                direction.X,
                node.Minimum.X,
                node.Maximum.X,
                ref minimum,
                ref maximum) ||
            !IntersectAxis(
                origin.Y,
                direction.Y,
                node.Minimum.Y,
                node.Maximum.Y,
                ref minimum,
                ref maximum) ||
            !IntersectAxis(
                origin.Z,
                direction.Z,
                node.Minimum.Z,
                node.Maximum.Z,
                ref minimum,
                ref maximum))
        {
            entryDistance = 0.0;
            return false;
        }
        entryDistance = minimum;
        return true;
    }

    private static bool IntersectAxis(
        double origin,
        double direction,
        double minimumBound,
        double maximumBound,
        ref double minimum,
        ref double maximum)
    {
        if (direction == 0.0)
        {
            return origin >= minimumBound && origin <= maximumBound;
        }

        double inverse = 1.0 / direction;
        double first = (minimumBound - origin) * inverse;
        double second = (maximumBound - origin) * inverse;
        if (first > second)
        {
            (first, second) = (second, first);
        }
        minimum = Math.Max(minimum, first);
        maximum = Math.Min(maximum, second);
        return minimum <= maximum;
    }

    private static CadPoint3D Unproject(
        Matrix4x4 inverse,
        float x,
        float y,
        float z)
    {
        Vector4 homogeneous = Vector4.Transform(new Vector4(x, y, z, 1.0f), inverse);
        if (!float.IsFinite(homogeneous.X) ||
            !float.IsFinite(homogeneous.Y) ||
            !float.IsFinite(homogeneous.Z) ||
            !float.IsFinite(homogeneous.W) ||
            homogeneous.W == 0.0f)
        {
            throw new InvalidOperationException(
                "The retained Mesh3D selection matrix produced a non-finite point.");
        }
        double inverseW = 1.0 / homogeneous.W;
        return new CadPoint3D(
            homogeneous.X * inverseW,
            homogeneous.Y * inverseW,
            homogeneous.Z * inverseW);
    }

    private static int BuildNode(
        TriangleBuildItem[] items,
        BvhNode[] nodes,
        ref int nodeCursor,
        int start,
        int count,
        int leafTriangleCount,
        int depth,
        ref int leafCount,
        ref int maximumDepth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int nodeIndex = nodeCursor++;
        maximumDepth = Math.Max(maximumDepth, depth);
        if (count <= leafTriangleCount)
        {
            Vector3 minimum = new(float.PositiveInfinity);
            Vector3 maximum = new(float.NegativeInfinity);
            for (int index = start; index < start + count; index++)
            {
                minimum = Vector3.Min(minimum, items[index].Minimum);
                maximum = Vector3.Max(maximum, items[index].Maximum);
            }
            nodes[nodeIndex] = BvhNode.CreateLeaf(minimum, maximum, start, count);
            leafCount++;
            return nodeIndex;
        }

        int leftCount = count / 2;
        int left = BuildNode(
            items,
            nodes,
            ref nodeCursor,
            start,
            leftCount,
            leafTriangleCount,
            depth + 1,
            ref leafCount,
            ref maximumDepth,
            cancellationToken);
        int right = BuildNode(
            items,
            nodes,
            ref nodeCursor,
            start + leftCount,
            count - leftCount,
            leafTriangleCount,
            depth + 1,
            ref leafCount,
            ref maximumDepth,
            cancellationToken);
        BvhNode leftNode = nodes[left];
        BvhNode rightNode = nodes[right];
        nodes[nodeIndex] = BvhNode.CreateBranch(
            Vector3.Min(leftNode.Minimum, rightNode.Minimum),
            Vector3.Max(leftNode.Maximum, rightNode.Maximum),
            left,
            right);
        return nodeIndex;
    }

    private static int CountLeafNodes(
        int triangleCount,
        int leafTriangleCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (triangleCount <= leafTriangleCount)
        {
            return 1;
        }

        int leftCount = triangleCount / 2;
        return checked(
            CountLeafNodes(
                leftCount,
                leafTriangleCount,
                cancellationToken) +
            CountLeafNodes(
                triangleCount - leftCount,
                leafTriangleCount,
                cancellationToken));
    }

    private static uint CreateMortonCode(
        Vector3 centroid,
        Vector3 minimum,
        Vector3 maximum)
    {
        uint x = Quantize(centroid.X, minimum.X, maximum.X);
        uint y = Quantize(centroid.Y, minimum.Y, maximum.Y);
        uint z = Quantize(centroid.Z, minimum.Z, maximum.Z);
        uint result = 0;
        for (int bit = 0; bit < MortonBitsPerAxis; bit++)
        {
            int destination = bit * 3;
            result |= ((x >> bit) & 1U) << destination;
            result |= ((y >> bit) & 1U) << (destination + 1);
            result |= ((z >> bit) & 1U) << (destination + 2);
        }
        return result;
    }

    private static uint Quantize(float value, float minimum, float maximum)
    {
        if (maximum <= minimum)
        {
            return 0;
        }
        double normalized = Math.Clamp(
            ((double)value - minimum) / ((double)maximum - minimum),
            0.0,
            1.0);
        return (uint)Math.Min(1023, (int)(normalized * 1023.0));
    }

    private static CadPoint3D ToCadPoint(Vector3 point) =>
        new(point.X, point.Y, point.Z);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private readonly record struct TriangleReference(
        int BatchIndex,
        int TriangleIndex);

    private readonly record struct SemanticRootReference(
        ulong Handle,
        int TriangleCount);

    private readonly record struct ClipRectangle(
        float Left,
        float Right,
        float Bottom,
        float Top);

    private readonly record struct ClipVolume(
        Vector4 Left,
        Vector4 Right,
        Vector4 Bottom,
        Vector4 Top,
        Vector4 Near,
        Vector4 Far)
    {
        internal Vector4 GetPlane(int index) => index switch
        {
            0 => Left,
            1 => Right,
            2 => Bottom,
            3 => Top,
            4 => Near,
            5 => Far,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
    }

    private enum PointLocation : byte
    {
        Outside,
        Inside,
        Boundary,
    }

    private struct TriangleBuildItem : IComparable<TriangleBuildItem>
    {
        internal uint MortonCode;
        internal readonly int BatchIndex;
        internal readonly int TriangleIndex;
        internal readonly Vector3 Centroid;
        internal readonly Vector3 Minimum;
        internal readonly Vector3 Maximum;

        internal TriangleBuildItem(
            uint mortonCode,
            int batchIndex,
            int triangleIndex,
            Vector3 centroid,
            Vector3 minimum,
            Vector3 maximum)
        {
            MortonCode = mortonCode;
            BatchIndex = batchIndex;
            TriangleIndex = triangleIndex;
            Centroid = centroid;
            Minimum = minimum;
            Maximum = maximum;
        }

        public readonly int CompareTo(TriangleBuildItem other)
        {
            int comparison = MortonCode.CompareTo(other.MortonCode);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = BatchIndex.CompareTo(other.BatchIndex);
            return comparison != 0
                ? comparison
                : TriangleIndex.CompareTo(other.TriangleIndex);
        }
    }

    private readonly record struct BvhNode(
        Vector3 Minimum,
        Vector3 Maximum,
        int Left,
        int Right,
        int Start,
        int Count)
    {
        internal static BvhNode CreateLeaf(
            Vector3 minimum,
            Vector3 maximum,
            int start,
            int count) =>
            new(minimum, maximum, -1, -1, start, count);

        internal static BvhNode CreateBranch(
            Vector3 minimum,
            Vector3 maximum,
            int left,
            int right) =>
            new(minimum, maximum, left, right, 0, 0);
    }
}
