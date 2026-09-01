using System.Numerics;

namespace ProGPU.CAD;

public sealed class CadMesh3DSceneOptions
{
    public const int DefaultMaxDrawBatches = 1_000_000;

    public bool IncludeNonPlottableLayers { get; init; } = true;
    public int MaxDrawBatches { get; init; } = DefaultMaxDrawBatches;
}

public readonly record struct CadMesh3DSceneStatistics(
    int SourceMeshCount,
    int FaceRangeCount,
    int TriangleCount,
    int DrawBatchCount)
{
    /// <summary>Retained SOLID and 3DFACE source-record count.</summary>
    public int SourceFaceCount { get; init; }
    public int SubobjectComponentCount { get; init; }
    public int SubobjectVertexCount { get; init; }
    public int SubobjectEdgeCount { get; init; }
    public int SubobjectFaceCount { get; init; }
}

/// <summary>
/// One immutable modern-MESH authored topology component in rebased local
/// coordinates. Its component index is the owning snapshot mesh primitive
/// index and remains independent from style batching.
/// </summary>
public sealed class CadMesh3DSubobjectComponent
{
    private readonly Vector3[] _vertexPositions;
    private readonly Vector3[] _edgePoints;
    private readonly CadMesh3DSubobjectEdge[] _edges;
    private readonly CadMesh3DSubobjectFace[] _faces;
    private readonly int[] _faceEdgeIndices;

    public ulong Handle { get; }
    public int ComponentIndex { get; }
    public ReadOnlyMemory<Vector3> VertexPositions => _vertexPositions;
    public ReadOnlyMemory<Vector3> EdgePoints => _edgePoints;
    public ReadOnlyMemory<CadMesh3DSubobjectEdge> Edges => _edges;
    public ReadOnlyMemory<CadMesh3DSubobjectFace> Faces => _faces;
    public ReadOnlyMemory<int> FaceEdgeIndices => _faceEdgeIndices;

    internal CadMesh3DSubobjectComponent(
        ulong handle,
        int componentIndex,
        Vector3[] vertexPositions,
        Vector3[] edgePoints,
        CadMesh3DSubobjectEdge[] edges,
        CadMesh3DSubobjectFace[] faces,
        int[] faceEdgeIndices)
    {
        Handle = handle;
        ComponentIndex = componentIndex;
        _vertexPositions = vertexPositions;
        _edgePoints = edgePoints;
        _edges = edges;
        _faces = faces;
        _faceEdgeIndices = faceEdgeIndices;
    }
}

/// <summary>One immutable, contiguous, same-style triangle-list draw.</summary>
public sealed class CadMesh3DDrawBatch
{
    private readonly Vector3[] _positions;
    private readonly Vector3[] _normals;
    private readonly Vector2[] _textureCoordinates;
    private readonly uint[] _indices;
    private readonly int[] _vertexSubobjectIndices;
    private readonly int[] _edgeSubobjectIndices;
    private readonly int[] _triangleFaceSubobjectIndices;

    public ulong Handle { get; }
    public int LayerIndex { get; }
    public int StyleIndex { get; }
    public CadColor32 Color { get; }
    public CadBounds3D Bounds { get; }
    public ReadOnlyMemory<Vector3> Positions => _positions;
    public ReadOnlyMemory<Vector3> Normals => _normals;
    public ReadOnlyMemory<Vector2> TextureCoordinates => _textureCoordinates;
    public ReadOnlyMemory<uint> Indices => _indices;
    public int ComponentIndex { get; }
    public ReadOnlyMemory<int> VertexSubobjectIndices =>
        _vertexSubobjectIndices;
    public ReadOnlyMemory<int> EdgeSubobjectIndices => _edgeSubobjectIndices;
    public ReadOnlyMemory<int> TriangleFaceSubobjectIndices =>
        _triangleFaceSubobjectIndices;

    internal CadMesh3DDrawBatch(
        ulong handle,
        int layerIndex,
        int styleIndex,
        CadColor32 color,
        CadBounds3D bounds,
        Vector3[] positions,
        Vector3[] normals,
        Vector2[] textureCoordinates,
        uint[] indices)
    {
        Handle = handle;
        LayerIndex = layerIndex;
        StyleIndex = styleIndex;
        Color = color;
        Bounds = bounds;
        _positions = positions;
        _normals = normals;
        _textureCoordinates = textureCoordinates;
        _indices = indices;
        ComponentIndex = -1;
        _vertexSubobjectIndices = [];
        _edgeSubobjectIndices = [];
        _triangleFaceSubobjectIndices = [];
    }

    internal CadMesh3DDrawBatch(
        ulong handle,
        int layerIndex,
        int styleIndex,
        CadColor32 color,
        CadBounds3D bounds,
        Vector3[] positions,
        Vector3[] normals,
        Vector2[] textureCoordinates,
        uint[] indices,
        int componentIndex,
        int[] vertexSubobjectIndices,
        int[] edgeSubobjectIndices,
        int[] triangleFaceSubobjectIndices)
        : this(
            handle,
            layerIndex,
            styleIndex,
            color,
            bounds,
            positions,
            normals,
            textureCoordinates,
            indices)
    {
        ComponentIndex = componentIndex;
        _vertexSubobjectIndices = vertexSubobjectIndices;
        _edgeSubobjectIndices = edgeSubobjectIndices;
        _triangleFaceSubobjectIndices = triangleFaceSubobjectIndices;
    }
}

/// <summary>Camera-independent triangle data compiled from one CAD generation.</summary>
public sealed class CadRecordedMesh3DScene
{
    private readonly CadMesh3DDrawBatch[] _drawBatches;
    private readonly CadMesh3DSubobjectComponent[] _subobjectComponents;

    public ulong ContentGeneration { get; }
    public CadPoint3D RebaseOrigin { get; }
    public CadBounds3D Bounds { get; }
    public CadMesh3DSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadMesh3DDrawBatch> DrawBatches => _drawBatches;
    public ReadOnlyMemory<CadMesh3DSubobjectComponent> SubobjectComponents =>
        _subobjectComponents;

    internal CadRecordedMesh3DScene(
        ulong contentGeneration,
        CadPoint3D rebaseOrigin,
        CadBounds3D bounds,
        CadMesh3DSceneStatistics statistics,
        CadMesh3DDrawBatch[] drawBatches,
        CadMesh3DSubobjectComponent[]? subobjectComponents = null)
    {
        ContentGeneration = contentGeneration;
        RebaseOrigin = rebaseOrigin;
        Bounds = bounds;
        Statistics = statistics;
        _drawBatches = drawBatches;
        _subobjectComponents = subobjectComponents ?? [];
    }

    public bool TryGetSubobjectComponent(
        in CadMesh3DSubobjectId id,
        out CadMesh3DSubobjectComponent? component)
    {
        if (id.ContentGeneration != ContentGeneration)
        {
            component = null;
            return false;
        }
        for (int index = 0; index < _subobjectComponents.Length; index++)
        {
            CadMesh3DSubobjectComponent candidate =
                _subobjectComponents[index];
            if (candidate.ComponentIndex == id.ComponentIndex &&
                candidate.Handle == id.Handle)
            {
                component = candidate;
                return true;
            }
        }
        component = null;
        return false;
    }
}

/// <summary>
/// Compiles exact flat-shaded CAD mesh and face streams into
/// camera-independent, float-rebased draw batches shared by managed and native
/// 3D adapters.
/// </summary>
/// <remarks>
/// Compilation is O(M + F + R + V + I) time for M mesh instances, F retained
/// SOLID/3DFACE records, R face/style ranges, V expanded flat vertices, and I
/// triangle indices. Storage is O(V + I + B) for B consecutive same-style
/// batches. No camera projection, clipping, tessellation, or GPU resource
/// creation occurs here.
/// </remarks>
public sealed class CadMesh3DSceneCompiler
{
    public CadRecordedMesh3DScene Compile(
        CadDocumentSnapshot snapshot,
        CadMesh3DSceneOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new CadMesh3DSceneOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDrawBatches, 1);

        ReadOnlySpan<CadEntityHeader> entities = snapshot.Entities.Span;
        ReadOnlySpan<CadMesh3DPrimitive> meshes = snapshot.Meshes3D.Span;
        ReadOnlySpan<CadMesh3DDrawRange> ranges = snapshot.Mesh3DDrawRanges.Span;
        ReadOnlySpan<CadMesh3DVertex> vertices = snapshot.Mesh3DVertices.Span;
        ReadOnlySpan<uint> indices = snapshot.Mesh3DIndices.Span;
        ReadOnlySpan<int> vertexSubobjectIndices =
            snapshot.Mesh3DVertexSubobjectIndices.Span;
        ReadOnlySpan<int> edgeSubobjectIndices =
            snapshot.Mesh3DEdgeSubobjectIndices.Span;
        ReadOnlySpan<CadFacePrimitive> faces = snapshot.Faces.Span;
        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        var batches = new List<CadMesh3DDrawBatch>();
        var subobjectComponents = new List<CadMesh3DSubobjectComponent>();
        int sourceMeshCount = 0;
        int sourceFaceCount = 0;
        int faceRangeCount = 0;
        int triangleCount = 0;

        for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CadEntityHeader entity = entities[entityIndex];
            if (entity.Kind is CadEntityKind.Solid or CadEntityKind.Face3D)
            {
                int groupStart = entityIndex;
                int groupEnd = entityIndex + 1;
                while (groupEnd < entities.Length)
                {
                    CadEntityHeader next = entities[groupEnd];
                    if (next.Kind is not (CadEntityKind.Solid or CadEntityKind.Face3D) ||
                        next.Handle != entity.Handle ||
                        next.LayerIndex != entity.LayerIndex ||
                        next.StyleIndex != entity.StyleIndex)
                    {
                        break;
                    }
                    groupEnd++;
                }
                int groupCount = groupEnd - entityIndex;
                sourceFaceCount = checked(sourceFaceCount + groupCount);
                faceRangeCount = checked(faceRangeCount + groupCount);
                entityIndex = groupEnd - 1;
                if (!options.IncludeNonPlottableLayers &&
                    !layers[entity.LayerIndex].IsPlottable)
                {
                    continue;
                }

                ReadOnlySpan<CadEntityHeader> faceGroup = entities.Slice(
                    groupStart,
                    groupCount);
                int groupTriangleCount = CountFaceTriangles(
                    faceGroup,
                    faces,
                    cancellationToken);
                if (groupTriangleCount == 0)
                {
                    continue;
                }
                if (batches.Count >= options.MaxDrawBatches)
                {
                    throw new InvalidOperationException(
                        $"CAD 3D draw batches exceed the configured limit of {options.MaxDrawBatches}.");
                }
                triangleCount = checked(triangleCount + groupTriangleCount);
                batches.Add(CreateFaceBatch(
                    snapshot,
                    faceGroup,
                    faces,
                    styles[entity.StyleIndex],
                    groupTriangleCount,
                    cancellationToken));
                continue;
            }
            if (entity.Kind != CadEntityKind.Mesh3D)
            {
                continue;
            }
            sourceMeshCount++;
            CadMesh3DPrimitive mesh = meshes[entity.PrimitiveIndex];
            bool componentAdded = false;
            int rangeCursor = 0;
            while (rangeCursor < mesh.DrawRangeCount)
            {
                CadMesh3DDrawRange first = ranges[
                    mesh.DrawRangeOffset + rangeCursor];
                int layerIndex = first.LayerIndex;
                int styleIndex = first.StyleIndex;
                int rangeEnd = rangeCursor + 1;
                while (rangeEnd < mesh.DrawRangeCount)
                {
                    CadMesh3DDrawRange next = ranges[
                        mesh.DrawRangeOffset + rangeEnd];
                    if (next.LayerIndex != layerIndex || next.StyleIndex != styleIndex)
                    {
                        break;
                    }
                    rangeEnd++;
                }

                faceRangeCount = checked(faceRangeCount + (rangeEnd - rangeCursor));
                if (!options.IncludeNonPlottableLayers &&
                    !layers[layerIndex].IsPlottable)
                {
                    rangeCursor = rangeEnd;
                    continue;
                }
                if (batches.Count >= options.MaxDrawBatches)
                {
                    throw new InvalidOperationException(
                        $"CAD 3D draw batches exceed the configured limit of {options.MaxDrawBatches}.");
                }

                int vertexCount = 0;
                int indexCount = 0;
                for (int current = rangeCursor; current < rangeEnd; current++)
                {
                    CadMesh3DDrawRange range = ranges[mesh.DrawRangeOffset + current];
                    vertexCount = checked(vertexCount + range.VertexCount);
                    indexCount = checked(indexCount + range.IndexCount);
                }
                var positions = new Vector3[vertexCount];
                var normals = new Vector3[vertexCount];
                var textureCoordinates = new Vector2[vertexCount];
                var batchIndices = new uint[indexCount];
                var batchVertexSubobjectIndices = mesh.HasSubobjectTopology
                    ? new int[vertexCount]
                    : Array.Empty<int>();
                var batchEdgeSubobjectIndices = mesh.HasSubobjectTopology
                    ? new int[indexCount]
                    : Array.Empty<int>();
                var batchFaceSubobjectIndices = mesh.HasSubobjectTopology
                    ? new int[indexCount / 3]
                    : Array.Empty<int>();
                int vertexDestination = 0;
                int indexDestination = 0;
                int triangleDestination = 0;
                CadBounds3D batchBounds = CadBounds3D.Empty;

                for (int current = rangeCursor; current < rangeEnd; current++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CadMesh3DDrawRange range = ranges[mesh.DrawRangeOffset + current];
                    int rangeVertexBase = vertexDestination;
                    for (int vertexIndex = 0;
                         vertexIndex < range.VertexCount;
                         vertexIndex++)
                    {
                        CadMesh3DVertex source = vertices[
                            range.VertexOffset + vertexIndex];
                        positions[vertexDestination] = ToRebasedVector(
                            source.Position,
                            snapshot.RebaseOrigin);
                        normals[vertexDestination] = ToNormal(source.Normal);
                        textureCoordinates[vertexDestination] = source.TextureCoordinate;
                        if (mesh.HasSubobjectTopology)
                        {
                            batchVertexSubobjectIndices[vertexDestination] =
                                vertexSubobjectIndices[
                                    range.VertexOffset + vertexIndex];
                        }
                        batchBounds = batchBounds.Include(source.Position);
                        vertexDestination++;
                    }
                    for (int index = 0; index < range.IndexCount; index++)
                    {
                        uint sourceIndex = indices[range.IndexOffset + index];
                        if (sourceIndex >= range.VertexCount)
                        {
                            throw new InvalidOperationException(
                                "A retained mesh index exceeds its owning vertex range.");
                        }
                        batchIndices[indexDestination++] = checked(
                            (uint)rangeVertexBase + sourceIndex);
                        if (mesh.HasSubobjectTopology)
                        {
                            batchEdgeSubobjectIndices[indexDestination - 1] =
                                edgeSubobjectIndices[range.IndexOffset + index];
                            if (index % 3 == 2)
                            {
                                batchFaceSubobjectIndices[triangleDestination++] =
                                    range.FaceSubobjectIndex;
                            }
                        }
                    }
                }

                triangleCount = checked(triangleCount + (indexCount / 3));
                batches.Add(new CadMesh3DDrawBatch(
                    entity.Handle,
                    layerIndex,
                    styleIndex,
                    ToColor(styles[styleIndex]),
                    batchBounds,
                    positions,
                    normals,
                    textureCoordinates,
                    batchIndices,
                    mesh.HasSubobjectTopology ? entity.PrimitiveIndex : -1,
                    batchVertexSubobjectIndices,
                    batchEdgeSubobjectIndices,
                    batchFaceSubobjectIndices));
                if (mesh.HasSubobjectTopology && !componentAdded)
                {
                    subobjectComponents.Add(CreateSubobjectComponent(
                        snapshot,
                        mesh,
                        entity.Handle,
                        entity.PrimitiveIndex));
                    componentAdded = true;
                }
                rangeCursor = rangeEnd;
            }
        }

        return new CadRecordedMesh3DScene(
            snapshot.ContentGeneration,
            snapshot.RebaseOrigin,
            snapshot.Bounds,
            new CadMesh3DSceneStatistics(
                sourceMeshCount,
                faceRangeCount,
                triangleCount,
                batches.Count)
            {
                SourceFaceCount = sourceFaceCount,
                SubobjectComponentCount = subobjectComponents.Count,
                SubobjectVertexCount = subobjectComponents.Sum(
                    component => component.VertexPositions.Length),
                SubobjectEdgeCount = subobjectComponents.Sum(
                    component => component.Edges.Length),
                SubobjectFaceCount = subobjectComponents.Sum(
                    component => component.Faces.Length),
            },
            batches.ToArray(),
            subobjectComponents.ToArray());
    }

    private static CadMesh3DSubobjectComponent CreateSubobjectComponent(
        CadDocumentSnapshot snapshot,
        CadMesh3DPrimitive mesh,
        ulong handle,
        int componentIndex)
    {
        ReadOnlySpan<CadPoint3D> sourcePoints =
            snapshot.Mesh3DSubobjectPoints.Span;
        var vertices = new Vector3[mesh.SubobjectVertexCount];
        for (int vertex = 0; vertex < vertices.Length; vertex++)
        {
            vertices[vertex] = ToRebasedVector(
                sourcePoints[mesh.SubobjectVertexPointOffset + vertex],
                snapshot.RebaseOrigin);
        }

        ReadOnlySpan<CadMesh3DSubobjectEdge> sourceEdges =
            snapshot.Mesh3DSubobjectEdges.Span.Slice(
                mesh.SubobjectEdgeOffset,
                mesh.SubobjectEdgeCount);
        int edgePointCount = 0;
        for (int edge = 0; edge < sourceEdges.Length; edge++)
        {
            edgePointCount = checked(edgePointCount + sourceEdges[edge].PointCount);
        }
        var edgePoints = new Vector3[edgePointCount];
        var edges = new CadMesh3DSubobjectEdge[sourceEdges.Length];
        int edgePointDestination = 0;
        for (int edge = 0; edge < sourceEdges.Length; edge++)
        {
            CadMesh3DSubobjectEdge sourceEdge = sourceEdges[edge];
            edges[edge] = new CadMesh3DSubobjectEdge(
                edgePointDestination,
                sourceEdge.PointCount);
            for (int point = 0; point < sourceEdge.PointCount; point++)
            {
                edgePoints[edgePointDestination++] = ToRebasedVector(
                    sourcePoints[sourceEdge.PointOffset + point],
                    snapshot.RebaseOrigin);
            }
        }

        ReadOnlySpan<CadMesh3DSubobjectFace> sourceFaces =
            snapshot.Mesh3DSubobjectFaces.Span.Slice(
                mesh.SubobjectFaceOffset,
                mesh.SubobjectFaceCount);
        ReadOnlySpan<int> sourceFaceEdgeIndices =
            snapshot.Mesh3DSubobjectFaceEdgeIndices.Span;
        int faceEdgeCount = 0;
        for (int face = 0; face < sourceFaces.Length; face++)
        {
            faceEdgeCount = checked(faceEdgeCount + sourceFaces[face].EdgeIndexCount);
        }
        var faces = new CadMesh3DSubobjectFace[sourceFaces.Length];
        var faceEdgeIndices = new int[faceEdgeCount];
        int faceEdgeDestination = 0;
        for (int face = 0; face < sourceFaces.Length; face++)
        {
            CadMesh3DSubobjectFace sourceFace = sourceFaces[face];
            faces[face] = new CadMesh3DSubobjectFace(
                faceEdgeDestination,
                sourceFace.EdgeIndexCount);
            for (int edge = 0; edge < sourceFace.EdgeIndexCount; edge++)
            {
                faceEdgeIndices[faceEdgeDestination++] =
                    sourceFaceEdgeIndices[sourceFace.EdgeIndexOffset + edge] -
                    mesh.SubobjectEdgeOffset;
            }
        }
        return new CadMesh3DSubobjectComponent(
            handle,
            componentIndex,
            vertices,
            edgePoints,
            edges,
            faces,
            faceEdgeIndices);
    }

    private static CadMesh3DDrawBatch CreateFaceBatch(
        CadDocumentSnapshot snapshot,
        ReadOnlySpan<CadEntityHeader> entities,
        ReadOnlySpan<CadFacePrimitive> faces,
        CadStrokeStyle style,
        int triangleCount,
        CancellationToken cancellationToken)
    {
        int vertexCount = checked(triangleCount * 3);
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var textureCoordinates = new Vector2[vertexCount];
        var indices = new uint[vertexCount];
        CadBounds3D bounds = CadBounds3D.Empty;
        int destination = 0;
        for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendFace(
                entities[entityIndex].Kind,
                faces[entities[entityIndex].PrimitiveIndex]);
        }

        CadEntityHeader firstEntity = entities[0];
        return new CadMesh3DDrawBatch(
            firstEntity.Handle,
            firstEntity.LayerIndex,
            firstEntity.StyleIndex,
            ToColor(style),
            bounds,
            positions,
            normals,
            textureCoordinates,
            indices);

        void AppendFace(CadEntityKind kind, CadFacePrimitive face)
        {
            Span<CadFaceSurfaceTriangle> triangles =
                stackalloc CadFaceSurfaceTriangle[CadFaceSurfaceTopology.MaximumTriangleCount];
            int triangleCount = CadFaceSurfaceTopology.BuildTriangles(
                kind,
                face,
                triangles);
            for (int triangleIndex = 0;
                 triangleIndex < triangleCount;
                 triangleIndex++)
            {
                CadFaceSurfaceTriangle triangle = triangles[triangleIndex];
                AppendTriangle(
                    triangle.First,
                    triangle.Second,
                    triangle.Third,
                    triangle.Normal);
            }
        }

        void AppendTriangle(
            CadPoint3D first,
            CadPoint3D second,
            CadPoint3D third,
            CadPoint3D normal)
        {
            AppendVertex(first, normal);
            AppendVertex(second, normal);
            AppendVertex(third, normal);
        }

        void AppendVertex(CadPoint3D position, CadPoint3D normal)
        {
            positions[destination] = ToRebasedVector(
                position,
                snapshot.RebaseOrigin);
            normals[destination] = ToNormal(normal);
            indices[destination] = checked((uint)destination);
            bounds = bounds.Include(position);
            destination++;
        }
    }

    private static int CountFaceTriangles(
        ReadOnlySpan<CadEntityHeader> entities,
        ReadOnlySpan<CadFacePrimitive> faces,
        CancellationToken cancellationToken)
    {
        int count = 0;
        for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count = checked(
                count + CountFaceTriangles(
                    entities[entityIndex].Kind,
                    faces[entities[entityIndex].PrimitiveIndex]));
        }
        return count;
    }

    private static int CountFaceTriangles(
        CadEntityKind kind,
        CadFacePrimitive face)
    {
        Span<CadFaceSurfaceTriangle> triangles =
            stackalloc CadFaceSurfaceTriangle[CadFaceSurfaceTopology.MaximumTriangleCount];
        return CadFaceSurfaceTopology.BuildTriangles(kind, face, triangles);
    }

    private static Vector3 ToRebasedVector(
        CadPoint3D point,
        CadPoint3D origin)
    {
        var result = new Vector3(
            checked((float)(point.X - origin.X)),
            checked((float)(point.Y - origin.Y)),
            checked((float)(point.Z - origin.Z)));
        if (!float.IsFinite(result.X) ||
            !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z))
        {
            throw new InvalidOperationException(
                "A rebased mesh coordinate exceeds the retained float range.");
        }
        return result;
    }

    private static Vector3 ToNormal(CadPoint3D normal)
    {
        var result = new Vector3(
            checked((float)normal.X),
            checked((float)normal.Y),
            checked((float)normal.Z));
        if (!float.IsFinite(result.X) ||
            !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z) ||
            result.LengthSquared() == 0.0f)
        {
            throw new InvalidOperationException(
                "A retained mesh normal is not a finite nonzero vector.");
        }
        return Vector3.Normalize(result);
    }

    private static CadColor32 ToColor(CadStrokeStyle style) =>
        new(style.Red, style.Green, style.Blue, style.Alpha);
}
