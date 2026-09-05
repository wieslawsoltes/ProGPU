using System.Numerics;

namespace ProGPU.CAD;

[Flags]
public enum CadMesh3DSubobjectFilter : byte
{
    None = 0,
    Vertex = 1,
    Edge = 2,
    Face = 4,
    All = Vertex | Edge | Face,
}

public enum CadMesh3DSubobjectKind : byte
{
    Vertex = 1,
    Edge = 2,
    Face = 3,
}

/// <summary>
/// One exact authored modern-MESH subobject identity. Identity is stable only
/// within <see cref="ContentGeneration"/> and must be rejected after scene
/// replacement.
/// </summary>
public readonly record struct CadMesh3DSubobjectId(
    ulong ContentGeneration,
    ulong Handle,
    int ComponentIndex,
    CadMesh3DSubobjectKind Kind,
    int Index);

/// <summary>One nearest-first exact projected modern-MESH subobject hit.</summary>
public readonly record struct CadMesh3DSubobjectSelectionResult(
    CadMesh3DSubobjectId Id,
    CadPoint3D Point,
    double DistanceFromCamera,
    double ProjectedDistance,
    int BatchIndex,
    int TriangleIndex);

/// <summary>Bounded subobject-hit collection and BVH traversal counters.</summary>
public readonly record struct CadMesh3DSubobjectQueryResult(
    ulong ContentGeneration,
    int HitCount,
    bool WasTruncated,
    int IntersectedTriangleCount,
    int VisitedNodeCount,
    int TestedTriangleCount);

/// <summary>Exact projected subobject-region results and work counters.</summary>
public readonly record struct CadMesh3DSubobjectRegionQueryResult(
    ulong ContentGeneration,
    int SubobjectWrittenCount,
    int SubobjectTotalCount,
    int IntersectedTriangleCount,
    int VisitedNodeCount,
    int TestedTriangleCount)
{
    public bool AreSubobjectsTruncated =>
        SubobjectWrittenCount != SubobjectTotalCount;
}

public sealed partial class CadMesh3DSelectionIndex
{
    private const double SubobjectDepthTieTolerance = 1e-6;

    private readonly record struct SubobjectComponentReference(
        ulong Handle,
        int ComponentIndex,
        int VertexOffset,
        int VertexCount,
        int EdgeOffset,
        int EdgeCount,
        int FaceOffset,
        int FaceCount);

    private static void BuildSubobjectReferences(
        CadRecordedMesh3DScene scene,
        int maxSubobjects,
        out SubobjectComponentReference[] components,
        out int[] batchComponentIndices,
        out CadMesh3DSubobjectId[] ids,
        out int[] primitiveCounts)
    {
        ReadOnlySpan<CadMesh3DSubobjectComponent> sourceComponents =
            scene.SubobjectComponents.Span;
        int subobjectCount = 0;
        for (int index = 0; index < sourceComponents.Length; index++)
        {
            CadMesh3DSubobjectComponent component = sourceComponents[index];
            subobjectCount = checked(
                subobjectCount +
                component.VertexPositions.Length +
                component.Edges.Length +
                component.Faces.Length);
            if (subobjectCount > maxSubobjects)
            {
                throw new InvalidOperationException(
                    $"CAD 3D selection subobjects exceed the configured limit of {maxSubobjects}.");
            }
        }

        components = new SubobjectComponentReference[sourceComponents.Length];
        ids = new CadMesh3DSubobjectId[subobjectCount];
        primitiveCounts = new int[subobjectCount];
        var componentIndices = new Dictionary<int, int>(sourceComponents.Length);
        int destination = 0;
        for (int index = 0; index < sourceComponents.Length; index++)
        {
            CadMesh3DSubobjectComponent source = sourceComponents[index];
            if (!componentIndices.TryAdd(source.ComponentIndex, index))
            {
                throw new InvalidOperationException(
                    "A retained Mesh3D scene contains duplicate subobject component identity.");
            }
            int vertexOffset = destination;
            destination = FillIds(
                ids,
                destination,
                scene.ContentGeneration,
                source.Handle,
                source.ComponentIndex,
                CadMesh3DSubobjectKind.Vertex,
                source.VertexPositions.Length);
            int edgeOffset = destination;
            destination = FillIds(
                ids,
                destination,
                scene.ContentGeneration,
                source.Handle,
                source.ComponentIndex,
                CadMesh3DSubobjectKind.Edge,
                source.Edges.Length);
            int faceOffset = destination;
            destination = FillIds(
                ids,
                destination,
                scene.ContentGeneration,
                source.Handle,
                source.ComponentIndex,
                CadMesh3DSubobjectKind.Face,
                source.Faces.Length);
            components[index] = new SubobjectComponentReference(
                source.Handle,
                source.ComponentIndex,
                vertexOffset,
                source.VertexPositions.Length,
                edgeOffset,
                source.Edges.Length,
                faceOffset,
                source.Faces.Length);
        }

        ReadOnlySpan<CadMesh3DDrawBatch> batches = scene.DrawBatches.Span;
        batchComponentIndices = new int[batches.Length];
        Array.Fill(batchComponentIndices, -1);
        for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            CadMesh3DDrawBatch batch = batches[batchIndex];
            if (batch.ComponentIndex < 0)
            {
                continue;
            }
            if (!componentIndices.TryGetValue(
                    batch.ComponentIndex,
                    out int componentIndex) ||
                components[componentIndex].Handle != batch.Handle)
            {
                throw new InvalidOperationException(
                    "A retained Mesh3D draw batch references unknown subobject topology.");
            }
            batchComponentIndices[batchIndex] = componentIndex;
            CountBatchPrimitiveAnnotations(
                batch,
                components[componentIndex],
                primitiveCounts);
        }
        for (int index = 0; index < primitiveCounts.Length; index++)
        {
            if (primitiveCounts[index] <= 0)
            {
                throw new InvalidOperationException(
                    "A retained modern-MESH subobject has no render-primitive annotation.");
            }
        }

        static int FillIds(
            CadMesh3DSubobjectId[] destinationIds,
            int offset,
            ulong generation,
            ulong handle,
            int componentIndex,
            CadMesh3DSubobjectKind kind,
            int count)
        {
            for (int index = 0; index < count; index++)
            {
                destinationIds[offset + index] = new CadMesh3DSubobjectId(
                    generation,
                    handle,
                    componentIndex,
                    kind,
                    index);
            }
            return checked(offset + count);
        }
    }

    private static void CountBatchPrimitiveAnnotations(
        CadMesh3DDrawBatch batch,
        SubobjectComponentReference component,
        int[] primitiveCounts)
    {
        ReadOnlySpan<Vector3> positions = batch.Positions.Span;
        ReadOnlySpan<uint> indices = batch.Indices.Span;
        ReadOnlySpan<int> vertices = batch.VertexSubobjectIndices.Span;
        ReadOnlySpan<int> edges = batch.EdgeSubobjectIndices.Span;
        ReadOnlySpan<int> faces = batch.TriangleFaceSubobjectIndices.Span;
        if (vertices.Length != positions.Length ||
            edges.Length != indices.Length ||
            faces.Length != indices.Length / 3)
        {
            throw new InvalidOperationException(
                "A retained modern-MESH draw batch has inconsistent subobject annotations.");
        }
        for (int triangle = 0; triangle < faces.Length; triangle++)
        {
            int triangleOffset = triangle * 3;
            Increment(CadMesh3DSubobjectKind.Face, faces[triangle]);
            for (int corner = 0; corner < 3; corner++)
            {
                int positionIndex = checked((int)indices[triangleOffset + corner]);
                if ((uint)positionIndex >= (uint)positions.Length)
                {
                    throw new InvalidOperationException(
                        "A retained modern-MESH annotation exceeds its vertex range.");
                }
                Increment(CadMesh3DSubobjectKind.Vertex, vertices[positionIndex]);
                Increment(CadMesh3DSubobjectKind.Edge, edges[triangleOffset + corner]);
            }
        }

        void Increment(CadMesh3DSubobjectKind kind, int localIndex)
        {
            if (localIndex < 0)
            {
                return;
            }
            int stateIndex = GetSubobjectStateIndex(
                component,
                kind,
                localIndex);
            primitiveCounts[stateIndex] = checked(
                primitiveCounts[stateIndex] + 1);
        }
    }

    private static int GetSubobjectStateIndex(
        in SubobjectComponentReference component,
        CadMesh3DSubobjectKind kind,
        int localIndex)
    {
        int offset;
        int count;
        switch (kind)
        {
            case CadMesh3DSubobjectKind.Vertex:
                offset = component.VertexOffset;
                count = component.VertexCount;
                break;
            case CadMesh3DSubobjectKind.Edge:
                offset = component.EdgeOffset;
                count = component.EdgeCount;
                break;
            case CadMesh3DSubobjectKind.Face:
                offset = component.FaceOffset;
                count = component.FaceCount;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if ((uint)localIndex >= (uint)count)
        {
            throw new InvalidOperationException(
                "A retained modern-MESH annotation exceeds its authored subobject range.");
        }
        return checked(offset + localIndex);
    }

    /// <summary>
    /// Returns nearest-first authored modern-MESH vertex, edge, or face hits
    /// inside one square logical-pixel aperture.
    /// </summary>
    /// <remarks>
    /// The query reuses the generation-owned triangle BVH and per-triangle
    /// authored-topology annotations. Typical work is O(log T + H*K), with
    /// conservative O(T*K) worst-case work for T triangles, H candidates, and
    /// caller-owned bounded result capacity K. Warm queries allocate no managed
    /// memory.
    /// </remarks>
    public CadMesh3DSubobjectQueryResult QuerySubobjects(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        CadMesh3DSubobjectFilter filter,
        Span<CadMesh3DSubobjectSelectionResult> destination,
        float targetHeight = DefaultPickTargetHeight)
    {
        if (filter == CadMesh3DSubobjectFilter.None ||
            (filter & ~CadMesh3DSubobjectFilter.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }
        if (destination.IsEmpty || destination.Length > MaximumHitCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"The 3D subobject-hit destination must contain between 1 and {MaximumHitCount} entries.");
        }
        if (!float.IsFinite(targetHeight) ||
            targetHeight < 0.0f || targetHeight > MaximumPickTargetHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetHeight),
                $"The 3D pick target height must be between 0 and {MaximumPickTargetHeight} logical pixels.");
        }
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
            return new CadMesh3DSubobjectQueryResult(
                ContentGeneration,
                0,
                false,
                0,
                0,
                0);
        }

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
        ClipVolume clipVolume = CreateClipVolume(viewProjection, clipRectangle);
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            throw new InvalidOperationException(
                "The retained Mesh3D view-projection matrix is not invertible.");
        }
        float ndcX = viewportPoint.X / viewportSize.X * 2.0f - 1.0f;
        float ndcY = 1.0f - viewportPoint.Y / viewportSize.Y * 2.0f;
        CadPoint3D nearPoint = Unproject(inverse, ndcX, ndcY, 0.0f);
        CadPoint3D farPoint = Unproject(inverse, ndcX, ndcY, 1.0f);
        CadPoint3D ray = farPoint - nearPoint;
        double maximumDistance = ray.Length;
        if (!double.IsFinite(maximumDistance) || maximumDistance <= 0.0)
        {
            throw new InvalidOperationException(
                "The retained Mesh3D subobject ray is not finite and non-degenerate.");
        }
        CadPoint3D direction = ray / maximumDistance;
        CadPoint3D cameraPoint = ToCadPoint(camera.Position);

        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsClipBounds(_nodes[0], clipVolume))
        {
            return new CadMesh3DSubobjectQueryResult(
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
                    CadMesh3DDrawBatch batch =
                        _scene.DrawBatches.Span[reference.BatchIndex];
                    if (batch.ComponentIndex < 0)
                    {
                        continue;
                    }
                    if (!ClassifyClipTriangle(
                            reference,
                            clipVolume,
                            out _))
                    {
                        continue;
                    }
                    intersectedTriangles++;
                    if ((filter & CadMesh3DSubobjectFilter.Face) != 0)
                    {
                        CollectSubobjectFace(
                            reference,
                            batch,
                            nearPoint,
                            direction,
                            maximumDistance,
                            cameraPoint,
                            clipVolume,
                            viewProjection,
                            viewportSize,
                            viewportPoint,
                            destination,
                            ref hitCount,
                            ref wasTruncated);
                    }
                    if ((filter & CadMesh3DSubobjectFilter.Edge) != 0)
                    {
                        CollectSubobjectEdges(
                            reference,
                            batch,
                            cameraPoint,
                            clipVolume,
                            viewProjection,
                            viewportSize,
                            viewportPoint,
                            destination,
                            ref hitCount,
                            ref wasTruncated);
                    }
                    if ((filter & CadMesh3DSubobjectFilter.Vertex) != 0)
                    {
                        CollectSubobjectVertices(
                            reference,
                            batch,
                            cameraPoint,
                            clipVolume,
                            viewProjection,
                            viewportSize,
                            viewportPoint,
                            destination,
                            ref hitCount,
                            ref wasTruncated);
                    }
                }
                continue;
            }

            bool hitLeft = IntersectsClipBounds(_nodes[node.Left], clipVolume);
            bool hitRight = IntersectsClipBounds(_nodes[node.Right], clipVolume);
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

        return new CadMesh3DSubobjectQueryResult(
            ContentGeneration,
            hitCount,
            wasTruncated,
            intersectedTriangles,
            visitedNodes,
            testedTriangles);
    }

    /// <summary>
    /// Selects authored modern-MESH subobjects through an exact projected
    /// rectangular Window or Crossing volume.
    /// </summary>
    /// <remarks>
    /// <paramref name="subobjectPrimitiveScratch"/> must contain at least
    /// <see cref="SubobjectCount"/> entries and is cleared by the query.
    /// Work is O(S + N + C) for S subobjects, N visited nodes, and C tested
    /// triangles. Warm queries allocate no managed memory.
    /// </remarks>
    public CadMesh3DSubobjectRegionQueryResult QuerySubobjectRegion(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        Vector2 firstViewportPoint,
        Vector2 secondViewportPoint,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        Span<int> subobjectPrimitiveScratch,
        Span<CadMesh3DSubobjectId> destination)
    {
        ValidateSubobjectRegionQuery(
            viewport,
            viewportSize,
            mode,
            filter,
            subobjectPrimitiveScratch,
            destination);
        if (!IsFinite(firstViewportPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(firstViewportPoint));
        }
        if (!IsFinite(secondViewportPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(secondViewportPoint));
        }
        Span<int> states = subobjectPrimitiveScratch[..SubobjectCount];
        states.Clear();
        if (_nodes.Length == 0 || SubobjectCount == 0)
        {
            return EmptySubobjectRegionResult();
        }

        Vector2 first = Vector2.Clamp(
            firstViewportPoint,
            Vector2.Zero,
            viewportSize);
        Vector2 second = Vector2.Clamp(
            secondViewportPoint,
            Vector2.Zero,
            viewportSize);
        float minimumX = MathF.Min(first.X, second.X);
        float maximumX = MathF.Max(first.X, second.X);
        float minimumY = MathF.Min(first.Y, second.Y);
        float maximumY = MathF.Max(first.Y, second.Y);
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
        return QuerySubobjectRegionCore(
            clipVolume,
            viewProjection,
            viewportSize,
            default,
            mode,
            filter,
            isProjectedPath: false,
            isFence: false,
            states,
            destination);
    }

    /// <summary>
    /// Selects authored subobjects through an implicitly closed simple
    /// projected polygon.
    /// </summary>
    public CadMesh3DSubobjectRegionQueryResult QuerySubobjectPolygon(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> polygon,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        Span<int> subobjectPrimitiveScratch,
        Span<CadMesh3DSubobjectId> destination)
    {
        polygon = NormalizeClosedPath(polygon);
        ValidateProjectedPath(polygon, isClosed: true);
        ValidateSimplePolygon(polygon);
        return QuerySubobjectProjectedPathCore(
            viewport,
            viewportSize,
            polygon,
            mode,
            filter,
            isFence: false,
            subobjectPrimitiveScratch,
            destination);
    }

    /// <summary>
    /// Selects authored subobjects through an implicitly closed even-odd
    /// freehand lasso. The lasso may cross itself.
    /// </summary>
    public CadMesh3DSubobjectRegionQueryResult QuerySubobjectLasso(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> lasso,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        Span<int> subobjectPrimitiveScratch,
        Span<CadMesh3DSubobjectId> destination)
    {
        lasso = NormalizeClosedPath(lasso);
        ValidateProjectedPath(lasso, isClosed: true);
        return QuerySubobjectProjectedPathCore(
            viewport,
            viewportSize,
            lasso,
            mode,
            filter,
            isFence: false,
            subobjectPrimitiveScratch,
            destination);
    }

    /// <summary>
    /// Selects authored subobjects crossed by an open projected fence. The
    /// fence may cross itself.
    /// </summary>
    public CadMesh3DSubobjectRegionQueryResult QuerySubobjectFence(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> fence,
        CadMesh3DSubobjectFilter filter,
        Span<int> subobjectPrimitiveScratch,
        Span<CadMesh3DSubobjectId> destination)
    {
        ValidateProjectedPath(fence, isClosed: false);
        return QuerySubobjectProjectedPathCore(
            viewport,
            viewportSize,
            fence,
            CadBoundsSelectionMode.Crossing,
            filter,
            isFence: true,
            subobjectPrimitiveScratch,
            destination);
    }

    private CadMesh3DSubobjectRegionQueryResult QuerySubobjectProjectedPathCore(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        bool isFence,
        Span<int> subobjectPrimitiveScratch,
        Span<CadMesh3DSubobjectId> destination)
    {
        ValidateSubobjectRegionQuery(
            viewport,
            viewportSize,
            mode,
            filter,
            subobjectPrimitiveScratch,
            destination);
        Span<int> states = subobjectPrimitiveScratch[..SubobjectCount];
        states.Clear();
        if (_nodes.Length == 0 || SubobjectCount == 0)
        {
            return EmptySubobjectRegionResult();
        }

        GetProjectedPathBounds(path, out Vector2 minimum, out Vector2 maximum);
        if (isFence)
        {
            minimum -= Vector2.One;
            maximum += Vector2.One;
        }
        if (maximum.X < 0.0f || maximum.Y < 0.0f ||
            minimum.X > viewportSize.X || minimum.Y > viewportSize.Y)
        {
            return EmptySubobjectRegionResult();
        }
        minimum = Vector2.Clamp(minimum, Vector2.Zero, viewportSize);
        maximum = Vector2.Clamp(maximum, Vector2.Zero, viewportSize);
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y)
        {
            return EmptySubobjectRegionResult();
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
        return QuerySubobjectRegionCore(
            clipVolume,
            viewProjection,
            viewportSize,
            path,
            mode,
            filter,
            isProjectedPath: true,
            isFence,
            states,
            destination);
    }

    private CadMesh3DSubobjectRegionQueryResult QuerySubobjectRegionCore(
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        bool isProjectedPath,
        bool isFence,
        Span<int> states,
        Span<CadMesh3DSubobjectId> destination)
    {
        Span<int> stack = stackalloc int[QueryStackCapacity];
        int stackCount = 0;
        if (!IntersectsClipBounds(_nodes[0], clipVolume))
        {
            return EmptySubobjectRegionResult();
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
                    bool intersects = isProjectedPath
                        ? ClassifyProjectedPathTriangle(
                            reference,
                            clipVolume,
                            viewProjection,
                            viewportSize,
                            path,
                            isFence,
                            out bool isContained)
                        : ClassifyClipTriangle(
                            reference,
                            clipVolume,
                            out isContained);
                    if (!intersects)
                    {
                        continue;
                    }
                    intersectedTriangles++;
                    AccumulateTriangleSubobjects(
                        reference,
                        clipVolume,
                        viewProjection,
                        viewportSize,
                        path,
                        mode,
                        filter,
                        isProjectedPath,
                        isFence,
                        isContained,
                        states);
                }
                continue;
            }

            bool hitLeft = IntersectsClipBounds(_nodes[node.Left], clipVolume);
            bool hitRight = IntersectsClipBounds(_nodes[node.Right], clipVolume);
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

        int writtenCount = 0;
        int totalCount = 0;
        for (int index = 0; index < _subobjectIds.Length; index++)
        {
            bool isHit = isFence || mode == CadBoundsSelectionMode.Crossing
                ? states[index] != 0
                : states[index] == _subobjectPrimitiveCounts[index];
            if (!isHit)
            {
                continue;
            }
            if (writtenCount < destination.Length)
            {
                destination[writtenCount++] = _subobjectIds[index];
            }
            totalCount++;
        }
        return new CadMesh3DSubobjectRegionQueryResult(
            ContentGeneration,
            writtenCount,
            totalCount,
            intersectedTriangles,
            visitedNodes,
            testedTriangles);
    }

    private void AccumulateTriangleSubobjects(
        TriangleReference reference,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        bool isProjectedPath,
        bool isFence,
        bool triangleContained,
        Span<int> states)
    {
        int componentIndex =
            _batchSubobjectComponentIndices[reference.BatchIndex];
        if (componentIndex < 0)
        {
            return;
        }
        SubobjectComponentReference component =
            _subobjectComponents[componentIndex];
        CadMesh3DDrawBatch batch =
            _scene.DrawBatches.Span[reference.BatchIndex];
        int triangleOffset = checked(reference.TriangleIndex * 3);

        if ((filter & CadMesh3DSubobjectFilter.Face) != 0)
        {
            int faceIndex = batch.TriangleFaceSubobjectIndices.Span[
                reference.TriangleIndex];
            RecordSubobjectPrimitive(
                component,
                CadMesh3DSubobjectKind.Face,
                faceIndex,
                intersects: faceIndex >= 0,
                triangleContained,
                mode,
                isFence,
                states);
        }

        ReadOnlySpan<Vector3> positions = batch.Positions.Span;
        ReadOnlySpan<uint> indices = batch.Indices.Span;
        if ((filter & CadMesh3DSubobjectFilter.Edge) != 0)
        {
            ReadOnlySpan<int> edgeIndices = batch.EdgeSubobjectIndices.Span;
            for (int corner = 0; corner < 3; corner++)
            {
                int edgeIndex = edgeIndices[triangleOffset + corner];
                if (edgeIndex < 0)
                {
                    continue;
                }
                Vector3 start = positions[
                    (int)indices[triangleOffset + corner]];
                Vector3 end = positions[
                    (int)indices[triangleOffset + ((corner + 1) % 3)]];
                bool intersects;
                bool isContained;
                if (isProjectedPath)
                {
                    intersects = ClassifyProjectedPathSegment(
                        start,
                        end,
                        clipVolume,
                        viewProjection,
                        viewportSize,
                        path,
                        isFence,
                        out isContained);
                }
                else
                {
                    intersects = TryClipSegment(
                        start,
                        end,
                        clipVolume,
                        out _,
                        out _);
                    isContained = GetOutsideMask(start, clipVolume) == 0 &&
                        GetOutsideMask(end, clipVolume) == 0;
                }
                RecordSubobjectPrimitive(
                    component,
                    CadMesh3DSubobjectKind.Edge,
                    edgeIndex,
                    intersects,
                    isContained,
                    mode,
                    isFence,
                    states);
            }
        }

        if ((filter & CadMesh3DSubobjectFilter.Vertex) != 0)
        {
            ReadOnlySpan<int> vertexIndices =
                batch.VertexSubobjectIndices.Span;
            for (int corner = 0; corner < 3; corner++)
            {
                int positionIndex =
                    (int)indices[triangleOffset + corner];
                int vertexIndex = vertexIndices[positionIndex];
                if (vertexIndex < 0)
                {
                    continue;
                }
                Vector3 point = positions[positionIndex];
                bool intersects;
                bool isContained;
                if (isProjectedPath)
                {
                    intersects = ClassifyProjectedPathPoint(
                        point,
                        clipVolume,
                        viewProjection,
                        viewportSize,
                        path,
                        isFence,
                        out isContained);
                }
                else
                {
                    intersects = GetOutsideMask(point, clipVolume) == 0;
                    isContained = intersects;
                }
                RecordSubobjectPrimitive(
                    component,
                    CadMesh3DSubobjectKind.Vertex,
                    vertexIndex,
                    intersects,
                    isContained,
                    mode,
                    isFence,
                    states);
            }
        }
    }

    private static void RecordSubobjectPrimitive(
        in SubobjectComponentReference component,
        CadMesh3DSubobjectKind kind,
        int localIndex,
        bool intersects,
        bool isContained,
        CadBoundsSelectionMode mode,
        bool isFence,
        Span<int> states)
    {
        if (!intersects || localIndex < 0)
        {
            return;
        }
        int stateIndex = GetSubobjectStateIndex(
            component,
            kind,
            localIndex);
        if (isFence || mode == CadBoundsSelectionMode.Crossing)
        {
            states[stateIndex] = 1;
        }
        else if (isContained)
        {
            states[stateIndex] = checked(states[stateIndex] + 1);
        }
    }

    private static bool ClassifyProjectedPathSegment(
        Vector3 start,
        Vector3 end,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        bool isFence,
        out bool isContained)
    {
        bool clipContained = GetOutsideMask(start, clipVolume) == 0 &&
            GetOutsideMask(end, clipVolume) == 0;
        if (!TryClipSegment(
                start,
                end,
                clipVolume,
                out Vector3 clippedStart,
                out Vector3 clippedEnd))
        {
            isContained = false;
            return false;
        }
        Vector2 first = ProjectToViewport(
            clippedStart,
            viewProjection,
            viewportSize);
        Vector2 second = ProjectToViewport(
            clippedEnd,
            viewProjection,
            viewportSize);
        if (isFence)
        {
            isContained = false;
            return FenceIntersectsSegment(path, first, second);
        }

        PointLocation firstLocation = GetPointLocation(first, path);
        PointLocation secondLocation = GetPointLocation(second, path);
        bool overlaps = firstLocation != PointLocation.Outside ||
            secondLocation != PointLocation.Outside;
        bool touchesBoundary = false;
        for (int edge = 0; edge < path.Length; edge++)
        {
            if (!SegmentsIntersectInclusive(
                    first,
                    second,
                    path[edge],
                    path[(edge + 1) % path.Length]))
            {
                continue;
            }
            overlaps = true;
            touchesBoundary = true;
        }
        isContained = clipContained &&
            firstLocation == PointLocation.Inside &&
            secondLocation == PointLocation.Inside &&
            !touchesBoundary;
        return overlaps;
    }

    private static bool ClassifyProjectedPathPoint(
        Vector3 point,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        ReadOnlySpan<Vector2> path,
        bool isFence,
        out bool isContained)
    {
        if (GetOutsideMask(point, clipVolume) != 0)
        {
            isContained = false;
            return false;
        }
        Vector2 projected = ProjectToViewport(
            point,
            viewProjection,
            viewportSize);
        if (isFence)
        {
            isContained = false;
            for (int span = 0; span + 1 < path.Length; span++)
            {
                if (PointOnSegment(projected, path[span], path[span + 1]))
                {
                    return true;
                }
            }
            return false;
        }
        PointLocation location = GetPointLocation(projected, path);
        isContained = location == PointLocation.Inside;
        return location != PointLocation.Outside;
    }

    private static bool FenceIntersectsSegment(
        ReadOnlySpan<Vector2> fence,
        Vector2 first,
        Vector2 second)
    {
        for (int span = 0; span + 1 < fence.Length; span++)
        {
            if (SegmentsIntersectInclusive(
                    first,
                    second,
                    fence[span],
                    fence[span + 1]))
            {
                return true;
            }
        }
        return false;
    }

    private void ValidateSubobjectRegionQuery(
        in CadMesh3DViewport viewport,
        Vector2 viewportSize,
        CadBoundsSelectionMode mode,
        CadMesh3DSubobjectFilter filter,
        Span<int> subobjectPrimitiveScratch,
        Span<CadMesh3DSubobjectId> destination)
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
        if (filter == CadMesh3DSubobjectFilter.None ||
            (filter & ~CadMesh3DSubobjectFilter.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }
        if (subobjectPrimitiveScratch.Length < SubobjectCount)
        {
            throw new ArgumentException(
                $"At least {SubobjectCount} subobject-primitive scratch entries are required.",
                nameof(subobjectPrimitiveScratch));
        }
        if (destination.IsEmpty || destination.Length > MaximumHitCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"The 3D subobject-region destination must contain between 1 and {MaximumHitCount} entries.");
        }
    }

    private CadMesh3DSubobjectRegionQueryResult EmptySubobjectRegionResult() =>
        new(ContentGeneration, 0, 0, 0, 0, 0);

    private void CollectSubobjectFace(
        TriangleReference reference,
        CadMesh3DDrawBatch batch,
        CadPoint3D nearPoint,
        CadPoint3D direction,
        double maximumDistance,
        CadPoint3D cameraPoint,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        Span<CadMesh3DSubobjectSelectionResult> destination,
        ref int hitCount,
        ref bool wasTruncated)
    {
        ReadOnlySpan<int> faceIndices =
            batch.TriangleFaceSubobjectIndices.Span;
        if ((uint)reference.TriangleIndex >= (uint)faceIndices.Length ||
            faceIndices[reference.TriangleIndex] < 0)
        {
            return;
        }
        CadPoint3D point;
        double distance;
        if (TryIntersectTriangle(
                reference,
                nearPoint,
                direction,
                maximumDistance,
                out double rayDistance,
                out _,
                out _,
                out _))
        {
            point = nearPoint + direction * rayDistance;
            distance = (point - cameraPoint).Length;
        }
        else if (!TryGetClipTriangleClosestPoint(
                     reference,
                     clipVolume,
                     cameraPoint,
                     out point,
                     out distance,
                     out _,
                     out _))
        {
            return;
        }
        InsertSubobjectHit(
            destination,
            ref hitCount,
            ref wasTruncated,
            new CadMesh3DSubobjectSelectionResult(
                new CadMesh3DSubobjectId(
                    ContentGeneration,
                    batch.Handle,
                    batch.ComponentIndex,
                    CadMesh3DSubobjectKind.Face,
                    faceIndices[reference.TriangleIndex]),
                RebaseOrigin + point,
                distance,
                GetProjectedDistance(
                    point,
                    viewProjection,
                    viewportSize,
                    viewportPoint),
                reference.BatchIndex,
                reference.TriangleIndex));
    }

    private void CollectSubobjectEdges(
        TriangleReference reference,
        CadMesh3DDrawBatch batch,
        CadPoint3D cameraPoint,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        Span<CadMesh3DSubobjectSelectionResult> destination,
        ref int hitCount,
        ref bool wasTruncated)
    {
        ReadOnlySpan<int> edgeIndices = batch.EdgeSubobjectIndices.Span;
        ReadOnlySpan<Vector3> positions = batch.Positions.Span;
        ReadOnlySpan<uint> indices = batch.Indices.Span;
        int triangleOffset = checked(reference.TriangleIndex * 3);
        if (triangleOffset + 2 >= edgeIndices.Length)
        {
            return;
        }
        for (int corner = 0; corner < 3; corner++)
        {
            int edgeIndex = edgeIndices[triangleOffset + corner];
            if (edgeIndex < 0)
            {
                continue;
            }
            Vector3 start = positions[(int)indices[triangleOffset + corner]];
            Vector3 end = positions[
                (int)indices[triangleOffset + ((corner + 1) % 3)]];
            if (!TryClipSegment(start, end, clipVolume,
                    out Vector3 clippedStart,
                    out Vector3 clippedEnd))
            {
                continue;
            }
            Vector3 point = ClosestProjectedPoint(
                clippedStart,
                clippedEnd,
                viewProjection,
                viewportSize,
                viewportPoint,
                out double projectedDistance);
            CadPoint3D localPoint = ToCadPoint(point);
            InsertSubobjectHit(
                destination,
                ref hitCount,
                ref wasTruncated,
                new CadMesh3DSubobjectSelectionResult(
                    new CadMesh3DSubobjectId(
                        ContentGeneration,
                        batch.Handle,
                        batch.ComponentIndex,
                        CadMesh3DSubobjectKind.Edge,
                        edgeIndex),
                    RebaseOrigin + localPoint,
                    (localPoint - cameraPoint).Length,
                    projectedDistance,
                    reference.BatchIndex,
                    reference.TriangleIndex));
        }
    }

    private void CollectSubobjectVertices(
        TriangleReference reference,
        CadMesh3DDrawBatch batch,
        CadPoint3D cameraPoint,
        in ClipVolume clipVolume,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        Span<CadMesh3DSubobjectSelectionResult> destination,
        ref int hitCount,
        ref bool wasTruncated)
    {
        ReadOnlySpan<int> vertexIndices = batch.VertexSubobjectIndices.Span;
        ReadOnlySpan<Vector3> positions = batch.Positions.Span;
        ReadOnlySpan<uint> indices = batch.Indices.Span;
        int triangleOffset = checked(reference.TriangleIndex * 3);
        for (int corner = 0; corner < 3; corner++)
        {
            int positionIndex = (int)indices[triangleOffset + corner];
            if ((uint)positionIndex >= (uint)vertexIndices.Length)
            {
                continue;
            }
            int vertexIndex = vertexIndices[positionIndex];
            Vector3 point = positions[positionIndex];
            if (vertexIndex < 0 || GetOutsideMask(point, clipVolume) != 0)
            {
                continue;
            }
            CadPoint3D localPoint = ToCadPoint(point);
            InsertSubobjectHit(
                destination,
                ref hitCount,
                ref wasTruncated,
                new CadMesh3DSubobjectSelectionResult(
                    new CadMesh3DSubobjectId(
                        ContentGeneration,
                        batch.Handle,
                        batch.ComponentIndex,
                        CadMesh3DSubobjectKind.Vertex,
                        vertexIndex),
                    RebaseOrigin + localPoint,
                    (localPoint - cameraPoint).Length,
                    GetProjectedDistance(
                        localPoint,
                        viewProjection,
                        viewportSize,
                        viewportPoint),
                    reference.BatchIndex,
                    reference.TriangleIndex));
        }
    }

    private static double GetProjectedDistance(
        CadPoint3D point,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        Vector2 viewportPoint) =>
        Vector2.Distance(
            ProjectToViewport(
                new Vector3((float)point.X, (float)point.Y, (float)point.Z),
                viewProjection,
                viewportSize),
            viewportPoint);

    private static Vector3 ClosestProjectedPoint(
        Vector3 start,
        Vector3 end,
        in Matrix4x4 viewProjection,
        Vector2 viewportSize,
        Vector2 viewportPoint,
        out double projectedDistance)
    {
        Vector2 projectedStart = ProjectToViewport(
            start,
            viewProjection,
            viewportSize);
        Vector2 projectedEnd = ProjectToViewport(
            end,
            viewProjection,
            viewportSize);
        Vector2 segment = projectedEnd - projectedStart;
        double denominator = Vector2.Dot(segment, segment);
        double parameter = denominator <= 0.0
            ? 0.0
            : Math.Clamp(
                Vector2.Dot(viewportPoint - projectedStart, segment) /
                    denominator,
                0.0,
                1.0);
        Vector2 projectedPoint = projectedStart + segment * (float)parameter;
        projectedDistance = Vector2.Distance(projectedPoint, viewportPoint);
        return Vector3.Lerp(start, end, (float)parameter);
    }

    private static void InsertSubobjectHit(
        Span<CadMesh3DSubobjectSelectionResult> destination,
        ref int hitCount,
        ref bool wasTruncated,
        CadMesh3DSubobjectSelectionResult candidate)
    {
        int existing = -1;
        for (int index = 0; index < hitCount; index++)
        {
            if (HaveSameSubobjectId(destination[index].Id, candidate.Id))
            {
                existing = index;
                break;
            }
        }
        if (existing >= 0)
        {
            if (CompareSubobjectHits(candidate, destination[existing]) >= 0)
            {
                return;
            }
            for (int index = existing; index + 1 < hitCount; index++)
            {
                destination[index] = destination[index + 1];
            }
            hitCount--;
        }
        else if (hitCount == destination.Length)
        {
            wasTruncated = true;
            if (CompareSubobjectHits(candidate, destination[hitCount - 1]) >= 0)
            {
                return;
            }
            hitCount--;
        }
        int insertion = hitCount;
        while (insertion > 0 &&
               CompareSubobjectHits(candidate, destination[insertion - 1]) < 0)
        {
            destination[insertion] = destination[insertion - 1];
            insertion--;
        }
        destination[insertion] = candidate;
        hitCount++;
    }

    private static bool HaveSameSubobjectId(
        in CadMesh3DSubobjectId first,
        in CadMesh3DSubobjectId second) =>
        first.ContentGeneration == second.ContentGeneration &&
        first.Handle == second.Handle &&
        first.ComponentIndex == second.ComponentIndex &&
        first.Kind == second.Kind &&
        first.Index == second.Index;

    private static int CompareSubobjectHits(
        in CadMesh3DSubobjectSelectionResult first,
        in CadMesh3DSubobjectSelectionResult second)
    {
        double depthTolerance = SubobjectDepthTieTolerance * Math.Max(
            1.0,
            Math.Max(first.DistanceFromCamera, second.DistanceFromCamera));
        double depthDifference = first.DistanceFromCamera -
            second.DistanceFromCamera;
        if (Math.Abs(depthDifference) > depthTolerance)
        {
            return depthDifference < 0.0 ? -1 : 1;
        }
        int comparison = GetKindPriority(first.Id.Kind).CompareTo(
            GetKindPriority(second.Id.Kind));
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = first.ProjectedDistance.CompareTo(second.ProjectedDistance);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = first.Id.ComponentIndex.CompareTo(second.Id.ComponentIndex);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = ((byte)first.Id.Kind).CompareTo((byte)second.Id.Kind);
        return comparison != 0
            ? comparison
            : first.Id.Index.CompareTo(second.Id.Index);
    }

    private static int GetKindPriority(CadMesh3DSubobjectKind kind) => kind switch
    {
        CadMesh3DSubobjectKind.Vertex => 0,
        CadMesh3DSubobjectKind.Edge => 1,
        CadMesh3DSubobjectKind.Face => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryClipSegment(
        Vector3 start,
        Vector3 end,
        in ClipVolume clipVolume,
        out Vector3 clippedStart,
        out Vector3 clippedEnd)
    {
        double minimum = 0.0;
        double maximum = 1.0;
        for (int planeIndex = 0; planeIndex < 6; planeIndex++)
        {
            Vector4 plane = clipVolume.GetPlane(planeIndex);
            double startDistance = GetClipDistance(start, plane);
            double endDistance = GetClipDistance(end, plane);
            if (startDistance < 0.0 && endDistance < 0.0)
            {
                clippedStart = default;
                clippedEnd = default;
                return false;
            }
            if (startDistance >= 0.0 && endDistance >= 0.0)
            {
                continue;
            }
            double parameter = startDistance /
                (startDistance - endDistance);
            if (startDistance < 0.0)
            {
                minimum = Math.Max(minimum, parameter);
            }
            else
            {
                maximum = Math.Min(maximum, parameter);
            }
            if (minimum > maximum)
            {
                clippedStart = default;
                clippedEnd = default;
                return false;
            }
        }
        clippedStart = Vector3.Lerp(start, end, (float)minimum);
        clippedEnd = Vector3.Lerp(start, end, (float)maximum);
        return true;
    }
}
