using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProGPU.CAD;

/// <summary>Bounds retained projected-selection index construction.</summary>
public sealed class CadMesh3DSelectionOptions
{
    public const int DefaultMaxTriangles = 10_000_000;
    public const int DefaultLeafTriangleCount = 8;

    public int MaxTriangles { get; init; } = DefaultMaxTriangles;

    public int LeafTriangleCount { get; init; } = DefaultLeafTriangleCount;
}

/// <summary>Immutable construction and residency counters for one 3D index.</summary>
public readonly record struct CadMesh3DSelectionIndexStatistics(
    int TriangleCount,
    int NodeCount,
    int LeafCount,
    int MaximumDepth,
    long RetainedByteCount);

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
public sealed class CadMesh3DSelectionIndex
{
    public const int MaximumHitCount = 256;

    private const int MortonBitsPerAxis = 10;
    private const int QueryStackCapacity = 64;
    private const double ParallelTolerance = 1e-14;
    private const double BarycentricTolerance = 1e-12;
    private const double TieTolerance = 1e-12;

    private readonly CadRecordedMesh3DScene _scene;
    private readonly TriangleReference[] _triangles;
    private readonly BvhNode[] _nodes;
    private readonly int[] _batchSemanticRootIndices;
    private readonly SemanticRootReference[] _semanticRoots;

    public ulong ContentGeneration => _scene.ContentGeneration;

    public CadPoint3D RebaseOrigin => _scene.RebaseOrigin;

    public int SemanticRootCount => _semanticRoots.Length;

    public CadMesh3DSelectionIndexStatistics Statistics { get; }

    private CadMesh3DSelectionIndex(
        CadRecordedMesh3DScene scene,
        TriangleReference[] triangles,
        BvhNode[] nodes,
        int[] batchSemanticRootIndices,
        SemanticRootReference[] semanticRoots,
        CadMesh3DSelectionIndexStatistics statistics)
    {
        _scene = scene;
        _triangles = triangles;
        _nodes = nodes;
        _batchSemanticRootIndices = batchSemanticRootIndices;
        _semanticRoots = semanticRoots;
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

        long retainedBytes = checked(
            (long)triangles.Length * Unsafe.SizeOf<TriangleReference>() +
            (long)nodes.Length * Unsafe.SizeOf<BvhNode>() +
            (long)batchSemanticRootIndices.Length * sizeof(int) +
            (long)semanticRoots.Count * Unsafe.SizeOf<SemanticRootReference>());
        return new CadMesh3DSelectionIndex(
            scene,
            triangles,
            nodes,
            batchSemanticRootIndices,
            semanticRoots.ToArray(),
            new CadMesh3DSelectionIndexStatistics(
                triangleCount,
                nodes.Length,
                leafCount,
                maximumDepth,
                retainedBytes));
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
        int firstOutside = GetOutsideMask(first, clipVolume);
        int secondOutside = GetOutsideMask(second, clipVolume);
        int thirdOutside = GetOutsideMask(third, clipVolume);
        isContained = (firstOutside | secondOutside | thirdOutside) == 0;
        if (isContained)
        {
            return true;
        }
        if ((firstOutside & secondOutside & thirdOutside) != 0)
        {
            return false;
        }

        Span<Vector3> firstBuffer = stackalloc Vector3[12];
        Span<Vector3> secondBuffer = stackalloc Vector3[12];
        firstBuffer[0] = first;
        firstBuffer[1] = second;
        firstBuffer[2] = third;
        int count = 3;
        for (int plane = 0; plane < 6; plane++)
        {
            int outputCount = 0;
            Vector4 clipPlane = clipVolume.GetPlane(plane);
            Vector3 previous = firstBuffer[count - 1];
            double previousDistance = GetClipDistance(
                previous,
                clipPlane);
            bool previousInside = previousDistance >= 0.0;
            for (int index = 0; index < count; index++)
            {
                Vector3 current = firstBuffer[index];
                double currentDistance = GetClipDistance(
                    current,
                    clipPlane);
                bool currentInside = currentDistance >= 0.0;
                if (currentInside != previousInside)
                {
                    double denominator = previousDistance - currentDistance;
                    if (denominator == 0.0 || outputCount >= secondBuffer.Length)
                    {
                        throw new InvalidOperationException(
                            "The projected Mesh3D clipping polygon exceeded its bounded contract.");
                    }
                    float parameter = (float)(previousDistance / denominator);
                    secondBuffer[outputCount++] = previous +
                        (current - previous) * parameter;
                }
                if (currentInside)
                {
                    if (outputCount >= secondBuffer.Length)
                    {
                        throw new InvalidOperationException(
                            "The projected Mesh3D clipping polygon exceeded its bounded contract.");
                    }
                    secondBuffer[outputCount++] = current;
                }
                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }
            if (outputCount == 0)
            {
                return false;
            }
            Span<Vector3> swap = firstBuffer;
            firstBuffer = secondBuffer;
            secondBuffer = swap;
            count = outputCount;
        }
        return true;
    }

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
