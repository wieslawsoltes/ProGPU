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

public sealed partial class CadMesh3DSelectionIndex
{
    private const double SubobjectDepthTieTolerance = 1e-6;

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
