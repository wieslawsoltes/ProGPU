using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

/// <summary>Published result of one modern-MESH subobject deletion.</summary>
public readonly record struct CadMesh3DDeletionSummary(
    int SelectedSubobjectCount,
    int AffectedMeshCount,
    int DeletedFaceCount,
    int CompactedControlVertexCount,
    int RemovedMeshEntityCount);

/// <summary>
/// Deletes the authored faces selected directly or through modern-MESH edge
/// and vertex subobjects while retaining exact topology for Undo/Redo.
/// </summary>
/// <remarks>
/// A selected face deletes only that face, a selected edge deletes every face
/// incident to that authored edge, and a selected vertex deletes every face
/// incident to that authored vertex. Newly isolated vertices are compacted in
/// original order together with texture coordinates and crease-edge indices.
/// A mesh with no surviving faces is removed as a complete model-space entity.
/// First application is O(S + V + C + K) time and storage for selected
/// subobjects S, control vertices V, face corners C, and crease records K.
/// Undo and redo replace retained topology in O(V + C + K).
/// </remarks>
public sealed class CadDeleteMeshSubobjectsCommand : CadEditCommand
{
    public const int DefaultMaxSubobjects = 4_096;
    public const int DefaultMaxControlVertices = 1_000_000;
    public const int DefaultMaxFaceCorners = 4_000_000;

    private readonly CadMesh3DSubobjectId[] _subobjects;
    private readonly CadMesh3DSubobjectEditTarget[] _targets;
    private readonly int _affectedMeshCount;
    private MeshDeletion[]? _deletions;

    public ReadOnlyMemory<CadMesh3DSubobjectId> Subobjects => _subobjects;
    public ulong SourceContentGeneration { get; }
    public int MaxSubobjects { get; }
    public int MaxControlVertices { get; }
    public int MaxFaceCorners { get; }
    public int DeletedFaceCount { get; private set; }
    public int CompactedControlVertexCount { get; private set; }
    public int RemovedMeshEntityCount { get; private set; }
    public CadMesh3DDeletionSummary Summary => new(
        _subobjects.Length,
        _affectedMeshCount,
        DeletedFaceCount,
        CompactedControlVertexCount,
        RemovedMeshEntityCount);

    internal override ulong? ExpectedContentGeneration =>
        SourceContentGeneration;

    public CadDeleteMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        string description = "Delete mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxControlVertices = DefaultMaxControlVertices,
        int maxFaceCorners = DefaultMaxFaceCorners)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxControlVertices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFaceCorners);

        SourceContentGeneration = scene.ContentGeneration;
        MaxSubobjects = maxSubobjects;
        MaxControlVertices = maxControlVertices;
        MaxFaceCorners = maxFaceCorners;
        CadMesh3DSubobjectEditSelectionResolver.Resolve(
            scene,
            subobjects,
            maxSubobjects,
            out _subobjects,
            out _targets);
        _affectedMeshCount = _targets
            .Select(static target => target.SourceHandle)
            .Distinct()
            .Count();
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        MeshDeletion[] deletions = isRedo
            ? GetRetainedDeletions()
            : BuildDeletions(document);
        ApplyAfter(document, deletions);
        if (!isRedo)
        {
            _deletions = deletions;
        }
    }

    internal override void Revert(CadDocument document)
    {
        MeshDeletion[] deletions = GetRetainedDeletions();
        Mesh[] removedMeshes = deletions
            .Where(static deletion => deletion.RemovesEntity)
            .Select(static deletion => deletion.Mesh)
            .ToArray();
        foreach (Mesh mesh in removedMeshes)
        {
            if (mesh.Owner is not null || mesh.Handle != 0)
            {
                throw new InvalidOperationException(
                    "A deleted MESH entity is not detached and cannot be restored.");
            }
        }
        foreach (MeshDeletion deletion in deletions)
        {
            if (!deletion.RemovesEntity)
            {
                ValidateAttachedState(
                    document,
                    deletion,
                    deletion.After!);
            }
        }

        if (removedMeshes.Length != 0)
        {
            document.Entities.AddRange(removedMeshes);
        }
        foreach (MeshDeletion deletion in deletions)
        {
            if (!deletion.RemovesEntity)
            {
                ApplyState(deletion.Mesh, deletion.Before);
            }
        }
    }

    private MeshDeletion[] BuildDeletions(CadDocument document)
    {
        var builders = new Dictionary<ulong, DeletionBuilder>();
        foreach (CadMesh3DSubobjectEditTarget target in _targets)
        {
            if (!builders.TryGetValue(
                    target.SourceHandle,
                    out DeletionBuilder? builder))
            {
                Entity entity = ResolveModelSpaceEntity(
                    document,
                    target.SourceHandle);
                if (entity is not Mesh mesh)
                {
                    throw new InvalidOperationException(
                        $"Model-space entity handle {target.SourceHandle:X} is not a modern MESH.");
                }
                builder = new DeletionBuilder(mesh, target);
                builders.Add(target.SourceHandle, builder);
            }
            else
            {
                builder.ValidateExpectedTopology(target);
            }
            builder.Add(target);
        }

        int controlVertexCount = 0;
        int faceCornerCount = 0;
        var deletions = new MeshDeletion[builders.Count];
        int deletionIndex = 0;
        int deletedFaceCount = 0;
        int compactedControlVertexCount = 0;
        int removedEntityCount = 0;
        foreach (DeletionBuilder builder in builders.Values)
        {
            controlVertexCount = checked(
                controlVertexCount + builder.ControlVertexCount);
            faceCornerCount = checked(
                faceCornerCount + builder.FaceCornerCount);
            if (controlVertexCount > MaxControlVertices)
            {
                throw new InvalidOperationException(
                    $"Mesh-subobject deletion visits more than the configured {MaxControlVertices} control vertices.");
            }
            if (faceCornerCount > MaxFaceCorners)
            {
                throw new InvalidOperationException(
                    $"Mesh-subobject deletion visits more than the configured {MaxFaceCorners} face corners.");
            }

            MeshDeletion deletion = builder.CreateDeletion();
            deletions[deletionIndex++] = deletion;
            deletedFaceCount = checked(
                deletedFaceCount + deletion.DeletedFaceCount);
            compactedControlVertexCount = checked(
                compactedControlVertexCount +
                deletion.CompactedControlVertexCount);
            if (deletion.RemovesEntity)
            {
                removedEntityCount++;
            }
        }
        DeletedFaceCount = deletedFaceCount;
        CompactedControlVertexCount = compactedControlVertexCount;
        RemovedMeshEntityCount = removedEntityCount;
        return deletions;
    }

    private static void ApplyAfter(
        CadDocument document,
        MeshDeletion[] deletions)
    {
        Mesh[] removedMeshes = deletions
            .Where(static deletion => deletion.RemovesEntity)
            .Select(static deletion => deletion.Mesh)
            .ToArray();
        foreach (MeshDeletion deletion in deletions)
        {
            if (deletion.RemovesEntity)
            {
                ValidateModelSpaceEntity(document, deletion.Mesh);
            }
            else
            {
                ValidateAttachedState(
                    document,
                    deletion,
                    deletion.Before);
            }
        }

        if (removedMeshes.Length != 0 &&
            !document.Entities.TryRemoveRange(removedMeshes))
        {
            throw new InvalidOperationException(
                "The complete-MESH removal batch was cancelled before mutation.");
        }
        foreach (MeshDeletion deletion in deletions)
        {
            if (!deletion.RemovesEntity)
            {
                ApplyState(deletion.Mesh, deletion.After!);
            }
        }
    }

    private MeshDeletion[] GetRetainedDeletions() =>
        _deletions ?? throw new InvalidOperationException(
            "The mesh-subobject deletion command has not been applied.");

    private static void ValidateAttachedState(
        CadDocument document,
        MeshDeletion deletion,
        MeshTopologyState expected)
    {
        ValidateModelSpaceEntity(document, deletion.Mesh);
        if (deletion.Mesh.Vertices.Count != expected.Vertices.Length ||
            deletion.Mesh.Faces.Count != expected.Faces.Length ||
            deletion.Mesh.Edges.Count != expected.Edges.Length)
        {
            throw new InvalidOperationException(
                "A retained MESH topology changed outside its edit history.");
        }
    }

    private static void ApplyState(Mesh mesh, MeshTopologyState state)
    {
        mesh.Vertices.Clear();
        mesh.Vertices.AddRange(state.Vertices);
        mesh.Faces.Clear();
        mesh.Faces.AddRange(state.Faces);
        mesh.Edges.Clear();
        mesh.Edges.AddRange(state.Edges);
        if (state.TextureCoordinates.Length != 0)
        {
            mesh.TextureCoordinates = state.TextureCoordinates;
        }
    }

    private readonly record struct EdgeKey(int First, int Second)
    {
        public static EdgeKey Create(int first, int second) => first < second
            ? new EdgeKey(first, second)
            : new EdgeKey(second, first);
    }

    private sealed class DeletionBuilder
    {
        private readonly CadMeshSourceTopology _topology;
        private readonly MeshTopologyState _before;
        private readonly int _expectedVertexCount;
        private readonly int _expectedEdgeCount;
        private readonly int _expectedFaceCount;
        private readonly HashSet<int> _selectedVertices = [];
        private readonly HashSet<int> _selectedEdges = [];
        private readonly HashSet<int> _selectedFaces = [];

        public Mesh Mesh { get; }
        public int ControlVertexCount => _before.Vertices.Length;
        public int FaceCornerCount { get; }

        public DeletionBuilder(
            Mesh mesh,
            in CadMesh3DSubobjectEditTarget target)
        {
            Mesh = mesh;
            _before = CaptureState(mesh);
            ValidateState(mesh, _before);
            _topology = CadMeshSubdivision.CreateSourceTopology(_before.Faces);
            _expectedVertexCount = target.VertexCount;
            _expectedEdgeCount = target.EdgeCount;
            _expectedFaceCount = target.FaceCount;
            ValidateExpectedTopology(target);
            foreach (int[] face in _before.Faces)
            {
                FaceCornerCount = checked(FaceCornerCount + face.Length);
            }
        }

        public void ValidateExpectedTopology(
            in CadMesh3DSubobjectEditTarget target)
        {
            if (target.VertexCount != _expectedVertexCount ||
                target.EdgeCount != _expectedEdgeCount ||
                target.FaceCount != _expectedFaceCount ||
                _before.Vertices.Length != _expectedVertexCount ||
                _topology.SourceEdgeVertexChains.Length != _expectedEdgeCount ||
                _before.Faces.Length != _expectedFaceCount)
            {
                throw new InvalidOperationException(
                    "The selected MESH topology no longer matches its retained scene generation.");
            }
        }

        public void Add(in CadMesh3DSubobjectEditTarget target)
        {
            switch (target.Kind)
            {
                case CadMesh3DSubobjectKind.Vertex:
                    _selectedVertices.Add(target.Index);
                    break;
                case CadMesh3DSubobjectKind.Edge:
                    _selectedEdges.Add(target.Index);
                    break;
                case CadMesh3DSubobjectKind.Face:
                    _selectedFaces.Add(target.Index);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
            }
        }

        public MeshDeletion CreateDeletion()
        {
            var deletedFaces = new bool[_before.Faces.Length];
            int deletedFaceCount = 0;
            var originallyReferencedVertices =
                new bool[_before.Vertices.Length];
            for (int faceIndex = 0;
                 faceIndex < _before.Faces.Length;
                 faceIndex++)
            {
                int[] face = _before.Faces[faceIndex];
                int[] faceEdges =
                    _topology.FaceCornerSourceEdgeIndices[faceIndex];
                bool delete = _selectedFaces.Contains(faceIndex);
                for (int corner = 0; corner < face.Length; corner++)
                {
                    originallyReferencedVertices[face[corner]] = true;
                    delete |= _selectedVertices.Contains(face[corner]) ||
                        _selectedEdges.Contains(faceEdges[corner]);
                }
                deletedFaces[faceIndex] = delete;
                if (delete)
                {
                    deletedFaceCount++;
                }
            }

            if (deletedFaceCount == _before.Faces.Length)
            {
                return new MeshDeletion(
                    Mesh,
                    _before,
                    After: null,
                    deletedFaceCount,
                    CompactedControlVertexCount: 0,
                    RemovesEntity: true);
            }

            var afterReferencedVertices = new bool[_before.Vertices.Length];
            var retainedFaces = new List<int[]>(
                _before.Faces.Length - deletedFaceCount);
            for (int faceIndex = 0;
                 faceIndex < _before.Faces.Length;
                 faceIndex++)
            {
                if (deletedFaces[faceIndex])
                {
                    continue;
                }
                int[] face = _before.Faces[faceIndex];
                retainedFaces.Add(face);
                foreach (int vertex in face)
                {
                    afterReferencedVertices[vertex] = true;
                }
            }

            var oldToNew = new int[_before.Vertices.Length];
            Array.Fill(oldToNew, -1);
            var vertices = new List<XYZ>(_before.Vertices.Length);
            List<XYZ>? textureCoordinates =
                _before.TextureCoordinates.Length == 0
                    ? null
                    : new List<XYZ>(_before.TextureCoordinates.Length);
            for (int oldIndex = 0;
                 oldIndex < _before.Vertices.Length;
                 oldIndex++)
            {
                bool retain = afterReferencedVertices[oldIndex] ||
                    (!originallyReferencedVertices[oldIndex] &&
                     !_selectedVertices.Contains(oldIndex));
                if (!retain)
                {
                    continue;
                }
                oldToNew[oldIndex] = vertices.Count;
                vertices.Add(_before.Vertices[oldIndex]);
                textureCoordinates?.Add(
                    _before.TextureCoordinates[oldIndex]);
            }

            var faces = new int[retainedFaces.Count][];
            var survivingEdges = new HashSet<EdgeKey>();
            for (int faceIndex = 0;
                 faceIndex < retainedFaces.Count;
                 faceIndex++)
            {
                int[] source = retainedFaces[faceIndex];
                var remapped = new int[source.Length];
                for (int corner = 0; corner < source.Length; corner++)
                {
                    int start = oldToNew[source[corner]];
                    int end = oldToNew[source[(corner + 1) % source.Length]];
                    if (start < 0 || end < 0)
                    {
                        throw new InvalidOperationException(
                            "A surviving MESH face lost one of its control vertices.");
                    }
                    remapped[corner] = start;
                    survivingEdges.Add(EdgeKey.Create(start, end));
                }
                faces[faceIndex] = remapped;
            }

            var edges = new List<Mesh.Edge>(_before.Edges.Length);
            foreach (Mesh.Edge edge in _before.Edges)
            {
                int start = oldToNew[edge.Start];
                int end = oldToNew[edge.End];
                if (start < 0 || end < 0 ||
                    !survivingEdges.Contains(EdgeKey.Create(start, end)))
                {
                    continue;
                }
                edges.Add(new Mesh.Edge(start, end)
                {
                    Crease = edge.Crease,
                });
            }

            var after = new MeshTopologyState(
                vertices.ToArray(),
                faces,
                edges.ToArray(),
                textureCoordinates?.ToArray() ?? []);
            ValidateState(Mesh, after);
            return new MeshDeletion(
                Mesh,
                _before,
                after,
                deletedFaceCount,
                _before.Vertices.Length - after.Vertices.Length,
                RemovesEntity: false);
        }

        private static MeshTopologyState CaptureState(Mesh mesh)
        {
            var faces = new int[mesh.Faces.Count][];
            for (int index = 0; index < faces.Length; index++)
            {
                faces[index] = mesh.Faces[index]?.ToArray() ??
                    throw new InvalidOperationException(
                        "A modern MESH face cannot be null.");
            }
            return new MeshTopologyState(
                mesh.Vertices.ToArray(),
                faces,
                mesh.Edges.ToArray(),
                mesh.TextureCoordinates.ToArray());
        }

        private static void ValidateState(
            Mesh mesh,
            MeshTopologyState state)
        {
            if (state.Vertices.Length < 3 || state.Faces.Length == 0)
            {
                throw new InvalidOperationException(
                    "A modern MESH requires at least three control vertices and one face.");
            }
            foreach (XYZ point in state.Vertices)
            {
                if (!IsFinite(point))
                {
                    throw new InvalidOperationException(
                        "A modern MESH control vertex must be finite.");
                }
            }
            if (state.TextureCoordinates.Length != 0 &&
                state.TextureCoordinates.Length != state.Vertices.Length)
            {
                throw new InvalidOperationException(
                    "A modern MESH texture-coordinate count must match its control-vertex count.");
            }
            foreach (XYZ point in state.TextureCoordinates)
            {
                if (!IsFinite(point))
                {
                    throw new InvalidOperationException(
                        "A modern MESH texture coordinate must be finite.");
                }
            }

            var topologyEdges = new HashSet<EdgeKey>();
            var distinctVertices = new HashSet<int>();
            foreach (int[] face in state.Faces)
            {
                if (face is null || face.Length < 3)
                {
                    throw new InvalidOperationException(
                        "Every modern MESH face requires at least three control vertices.");
                }
                distinctVertices.Clear();
                for (int corner = 0; corner < face.Length; corner++)
                {
                    int start = face[corner];
                    int end = face[(corner + 1) % face.Length];
                    if ((uint)start >= (uint)state.Vertices.Length ||
                        (uint)end >= (uint)state.Vertices.Length)
                    {
                        throw new InvalidOperationException(
                            "A modern MESH face references a missing control vertex.");
                    }
                    distinctVertices.Add(start);
                    if (start != end &&
                        state.Vertices[start] == state.Vertices[end])
                    {
                        throw new InvalidOperationException(
                            "A modern MESH contains a collapsed control edge.");
                    }
                    topologyEdges.Add(EdgeKey.Create(start, end));
                }
                if (distinctVertices.Count < 3)
                {
                    throw new InvalidOperationException(
                        "A modern MESH face requires three distinct control vertices.");
                }
            }

            var creaseEdges = new HashSet<EdgeKey>();
            foreach (Mesh.Edge edge in state.Edges)
            {
                if (edge.Start == edge.End ||
                    (uint)edge.Start >= (uint)state.Vertices.Length ||
                    (uint)edge.End >= (uint)state.Vertices.Length)
                {
                    throw new InvalidOperationException(
                        "A modern MESH crease edge has invalid control-vertex indices.");
                }
                EdgeKey key = EdgeKey.Create(edge.Start, edge.End);
                if (!creaseEdges.Add(key) || !topologyEdges.Contains(key))
                {
                    throw new InvalidOperationException(
                        "A modern MESH crease edge must uniquely reference a persisted face edge.");
                }
                if (edge.Crease is double crease &&
                    (!double.IsFinite(crease) ||
                     (crease < 0.0 && crease != -1.0) ||
                     (!mesh.BlendCrease &&
                      crease >= 0.0 &&
                      crease != Math.Truncate(crease))))
                {
                    throw new InvalidOperationException(
                        "A modern MESH crease must be -1, zero, or a finite positive level; fractional levels require Blend Crease.");
                }
            }
        }

        private static bool IsFinite(XYZ point) =>
            double.IsFinite(point.X) &&
            double.IsFinite(point.Y) &&
            double.IsFinite(point.Z);
    }

    private sealed record MeshTopologyState(
        XYZ[] Vertices,
        int[][] Faces,
        Mesh.Edge[] Edges,
        XYZ[] TextureCoordinates);

    private sealed record MeshDeletion(
        Mesh Mesh,
        MeshTopologyState Before,
        MeshTopologyState? After,
        int DeletedFaceCount,
        int CompactedControlVertexCount,
        bool RemovesEntity);
}
