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
}

/// <summary>One immutable, contiguous, same-style triangle-list draw.</summary>
public sealed class CadMesh3DDrawBatch
{
    private readonly Vector3[] _positions;
    private readonly Vector3[] _normals;
    private readonly Vector2[] _textureCoordinates;
    private readonly uint[] _indices;

    public ulong Handle { get; }
    public int LayerIndex { get; }
    public int StyleIndex { get; }
    public CadColor32 Color { get; }
    public CadBounds3D Bounds { get; }
    public ReadOnlyMemory<Vector3> Positions => _positions;
    public ReadOnlyMemory<Vector3> Normals => _normals;
    public ReadOnlyMemory<Vector2> TextureCoordinates => _textureCoordinates;
    public ReadOnlyMemory<uint> Indices => _indices;

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
    }
}

/// <summary>Camera-independent triangle data compiled from one CAD generation.</summary>
public sealed class CadRecordedMesh3DScene
{
    private readonly CadMesh3DDrawBatch[] _drawBatches;

    public ulong ContentGeneration { get; }
    public CadPoint3D RebaseOrigin { get; }
    public CadBounds3D Bounds { get; }
    public CadMesh3DSceneStatistics Statistics { get; }
    public ReadOnlyMemory<CadMesh3DDrawBatch> DrawBatches => _drawBatches;

    internal CadRecordedMesh3DScene(
        ulong contentGeneration,
        CadPoint3D rebaseOrigin,
        CadBounds3D bounds,
        CadMesh3DSceneStatistics statistics,
        CadMesh3DDrawBatch[] drawBatches)
    {
        ContentGeneration = contentGeneration;
        RebaseOrigin = rebaseOrigin;
        Bounds = bounds;
        Statistics = statistics;
        _drawBatches = drawBatches;
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
        ReadOnlySpan<CadFacePrimitive> faces = snapshot.Faces.Span;
        ReadOnlySpan<CadLayerSnapshot> layers = snapshot.Layers.Span;
        ReadOnlySpan<CadStrokeStyle> styles = snapshot.Styles.Span;
        var batches = new List<CadMesh3DDrawBatch>();
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
                int vertexDestination = 0;
                int indexDestination = 0;
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
                    batchIndices));
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
            },
            batches.ToArray());
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
            AppendFace(faces[entities[entityIndex].PrimitiveIndex]);
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

        void AppendFace(CadFacePrimitive face)
        {
            if (CadMesh3DTopology.TryComputeFlatNormal(
                    face.First,
                    face.Second,
                    face.Third,
                    out CadPoint3D firstNormal))
            {
                AppendTriangle(face.First, face.Second, face.Third, firstNormal);
            }
            if (face.Fourth != face.Third &&
                CadMesh3DTopology.TryComputeFlatNormal(
                    face.First,
                    face.Third,
                    face.Fourth,
                    out CadPoint3D secondNormal))
            {
                AppendTriangle(face.First, face.Third, face.Fourth, secondNormal);
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
                    faces[entities[entityIndex].PrimitiveIndex]));
        }
        return count;
    }

    private static int CountFaceTriangles(CadFacePrimitive face)
    {
        int count = CadMesh3DTopology.TryComputeFlatNormal(
            face.First,
            face.Second,
            face.Third,
            out _) ? 1 : 0;
        if (face.Fourth != face.Third &&
            CadMesh3DTopology.TryComputeFlatNormal(
                face.First,
                face.Third,
                face.Fourth,
                out _))
        {
            count++;
        }
        return count;
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
