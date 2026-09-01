using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Translates authored vertices, edges, and faces of direct model-space modern
/// MESH entities while preserving topology and exact undo/redo coordinates.
/// </summary>
/// <remarks>
/// The selected IDs must belong to the supplied immutable scene generation.
/// Edge and face selections expand to their authored control vertices, and the
/// union is moved once per vertex. Nested block-definition sources are rejected
/// before document mutation because editing them would affect every insert.
/// First application is expected O(S + C), where S is selected subobjects and
/// C is authored face corners, with O(A + C) temporary/retained storage for A
/// affected vertices. Undo and redo are O(A)
/// with no new managed allocation. Work is bounded by <see cref="MaxSubobjects"/>
/// and <see cref="MaxAffectedVertices"/>.
/// </remarks>
public sealed class CadTranslateMeshSubobjectsCommand : CadEditCommand
{
    public const int DefaultMaxSubobjects = 4_096;
    public const int DefaultMaxAffectedVertices = 1_000_000;

    private readonly CadMesh3DSubobjectId[] _subobjects;
    private readonly EditTarget[] _targets;
    private MeshEdit[]? _edits;

    public ReadOnlyMemory<CadMesh3DSubobjectId> Subobjects => _subobjects;
    public CadPoint3D Translation { get; }
    public ulong SourceContentGeneration { get; }
    public int MaxSubobjects { get; }
    public int MaxAffectedVertices { get; }

    internal override ulong? ExpectedContentGeneration =>
        SourceContentGeneration;

    public CadTranslateMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        CadPoint3D translation,
        string description = "Translate mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(subobjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSubobjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAffectedVertices);
        if (!IsFinite(translation) || translation == CadPoint3D.Zero)
        {
            throw new ArgumentException(
                "A mesh-subobject translation must be finite and non-zero.",
                nameof(translation));
        }

        MaxSubobjects = maxSubobjects;
        MaxAffectedVertices = maxAffectedVertices;
        Translation = translation;
        SourceContentGeneration = scene.ContentGeneration;

        var ids = new List<CadMesh3DSubobjectId>();
        var targets = new List<EditTarget>();
        var distinct = new HashSet<CadMesh3DSubobjectId>();
        foreach (CadMesh3DSubobjectId id in subobjects)
        {
            if (!distinct.Add(id))
            {
                continue;
            }
            if (ids.Count >= maxSubobjects)
            {
                throw new ArgumentException(
                    $"Mesh-subobject selection exceeds the configured limit of {maxSubobjects}.",
                    nameof(subobjects));
            }
            if (!scene.TryGetSubobjectComponent(id, out CadMesh3DSubobjectComponent? component) ||
                component is null)
            {
                throw new InvalidOperationException(
                    "A mesh-subobject ID does not belong to the supplied scene generation.");
            }
            if (!component.IsDirectModelSpaceSource ||
                component.SourceHandle == 0)
            {
                throw new InvalidOperationException(
                    "Nested block-definition mesh subobjects require an explicit reference-editing scope.");
            }
            ValidateOrdinal(component, id);
            ids.Add(id);
            targets.Add(new EditTarget(
                component.SourceHandle,
                id.Kind,
                id.Index,
                component.VertexPositions.Length,
                component.Edges.Length,
                component.Faces.Length));
        }
        if (ids.Count == 0)
        {
            throw new ArgumentException(
                "At least one mesh subobject is required.",
                nameof(subobjects));
        }

        _subobjects = ids.ToArray();
        _targets = targets.ToArray();
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            MeshEdit[] edits = GetRetainedEdits(document);
            ApplyValues(edits, useAfterValues: true);
            return;
        }

        MeshEdit[] firstEdits = BuildEdits(document);
        ApplyValues(firstEdits, useAfterValues: true);
        _edits = firstEdits;
    }

    internal override void Revert(CadDocument document) =>
        ApplyValues(GetRetainedEdits(document), useAfterValues: false);

    private MeshEdit[] BuildEdits(CadDocument document)
    {
        var builders = new Dictionary<ulong, EditBuilder>();
        foreach (EditTarget target in _targets)
        {
            if (!builders.TryGetValue(target.SourceHandle, out EditBuilder? builder))
            {
                Entity entity = ResolveModelSpaceEntity(document, target.SourceHandle);
                if (entity is not Mesh mesh)
                {
                    throw new InvalidOperationException(
                        $"Model-space entity handle {target.SourceHandle:X} is not a modern MESH.");
                }
                builder = new EditBuilder(mesh, target);
                builders.Add(target.SourceHandle, builder);
            }
            else
            {
                builder.ValidateExpectedTopology(target);
            }
            builder.Add(target);
        }

        int affectedVertexCount = 0;
        var edits = new MeshEdit[builders.Count];
        int editIndex = 0;
        foreach (EditBuilder builder in builders.Values)
        {
            affectedVertexCount = checked(
                affectedVertexCount + builder.VertexIndices.Count);
            if (affectedVertexCount > MaxAffectedVertices)
            {
                throw new InvalidOperationException(
                    $"Mesh-subobject edit affects more than the configured {MaxAffectedVertices} vertices.");
            }
            edits[editIndex++] = builder.CreateEdit(Translation);
        }
        return edits;
    }

    private MeshEdit[] GetRetainedEdits(CadDocument document)
    {
        MeshEdit[] edits = _edits ?? throw new InvalidOperationException(
            "The mesh-subobject translation command has not been applied.");
        foreach (MeshEdit edit in edits)
        {
            ValidateModelSpaceEntity(document, edit.Mesh);
            if (edit.Mesh.Vertices.Count != edit.ExpectedVertexCount)
            {
                throw new InvalidOperationException(
                    "A retained MESH topology changed outside its edit history.");
            }
        }
        return edits;
    }

    private static void ApplyValues(MeshEdit[] edits, bool useAfterValues)
    {
        foreach (MeshEdit edit in edits)
        {
            XYZ[] values = useAfterValues ? edit.After : edit.Before;
            for (int index = 0; index < edit.VertexIndices.Length; index++)
            {
                edit.Mesh.Vertices[edit.VertexIndices[index]] = values[index];
            }
        }
    }

    private static void ValidateOrdinal(
        CadMesh3DSubobjectComponent component,
        in CadMesh3DSubobjectId id)
    {
        int count = id.Kind switch
        {
            CadMesh3DSubobjectKind.Vertex => component.VertexPositions.Length,
            CadMesh3DSubobjectKind.Edge => component.Edges.Length,
            CadMesh3DSubobjectKind.Face => component.Faces.Length,
            _ => throw new ArgumentOutOfRangeException(
                nameof(id),
                "Unknown mesh-subobject kind."),
        };
        if ((uint)id.Index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(id),
                "Mesh-subobject ordinal is outside its authored topology.");
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private readonly record struct EditTarget(
        ulong SourceHandle,
        CadMesh3DSubobjectKind Kind,
        int Index,
        int VertexCount,
        int EdgeCount,
        int FaceCount);

    private sealed class EditBuilder
    {
        private readonly CadMeshSourceTopology _topology;
        private readonly int _expectedVertexCount;
        private readonly int _expectedEdgeCount;
        private readonly int _expectedFaceCount;

        public Mesh Mesh { get; }
        public HashSet<int> VertexIndices { get; } = [];

        public EditBuilder(Mesh mesh, in EditTarget target)
        {
            Mesh = mesh;
            _topology = CadMeshSubdivision.CreateSourceTopology(mesh.Faces);
            _expectedVertexCount = target.VertexCount;
            _expectedEdgeCount = target.EdgeCount;
            _expectedFaceCount = target.FaceCount;
            ValidateExpectedTopology(target);
            ValidateMeshTopology(mesh, proposed: null);
        }

        public void ValidateExpectedTopology(in EditTarget target)
        {
            if (target.VertexCount != _expectedVertexCount ||
                target.EdgeCount != _expectedEdgeCount ||
                target.FaceCount != _expectedFaceCount ||
                Mesh.Vertices.Count != _expectedVertexCount ||
                _topology.SourceEdgeVertexChains.Length != _expectedEdgeCount ||
                Mesh.Faces.Count != _expectedFaceCount)
            {
                throw new InvalidOperationException(
                    "The selected MESH topology no longer matches its retained scene generation.");
            }
        }

        public void Add(in EditTarget target)
        {
            switch (target.Kind)
            {
                case CadMesh3DSubobjectKind.Vertex:
                    VertexIndices.Add(target.Index);
                    break;
                case CadMesh3DSubobjectKind.Edge:
                    int[] edge = _topology.SourceEdgeVertexChains[target.Index];
                    VertexIndices.Add(edge[0]);
                    VertexIndices.Add(edge[^1]);
                    break;
                case CadMesh3DSubobjectKind.Face:
                    foreach (int vertexIndex in Mesh.Faces[target.Index])
                    {
                        VertexIndices.Add(vertexIndex);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
            }
        }

        public MeshEdit CreateEdit(CadPoint3D translation)
        {
            int[] indices = VertexIndices.Order().ToArray();
            var before = new XYZ[indices.Length];
            var after = new XYZ[indices.Length];
            var proposed = new Dictionary<int, XYZ>(indices.Length);
            var displacement = new XYZ(
                translation.X,
                translation.Y,
                translation.Z);
            for (int index = 0; index < indices.Length; index++)
            {
                XYZ source = Mesh.Vertices[indices[index]];
                XYZ destination = source + displacement;
                if (!IsFinite(destination))
                {
                    throw new InvalidOperationException(
                        "Mesh-subobject translation produces a non-finite control vertex.");
                }
                before[index] = source;
                after[index] = destination;
                proposed.Add(indices[index], destination);
            }
            ValidateMeshTopology(Mesh, proposed);
            return new MeshEdit(
                Mesh,
                Mesh.Vertices.Count,
                indices,
                before,
                after);
        }

        private static void ValidateMeshTopology(
            Mesh mesh,
            Dictionary<int, XYZ>? proposed)
        {
            if (mesh.Vertices.Count < 3 || mesh.Faces.Count == 0)
            {
                throw new InvalidOperationException(
                    "A modern MESH requires at least three control vertices and one face.");
            }
            var distinct = new HashSet<int>();
            foreach (int[] face in mesh.Faces)
            {
                if (face is null || face.Length < 3)
                {
                    throw new InvalidOperationException(
                        "Every modern MESH face requires at least three control vertices.");
                }
                distinct.Clear();
                for (int corner = 0; corner < face.Length; corner++)
                {
                    int start = face[corner];
                    int end = face[(corner + 1) % face.Length];
                    if ((uint)start >= (uint)mesh.Vertices.Count ||
                        (uint)end >= (uint)mesh.Vertices.Count)
                    {
                        throw new InvalidOperationException(
                            "A modern MESH face references a missing control vertex.");
                    }
                    distinct.Add(start);
                    XYZ startPoint = proposed is not null &&
                        proposed.TryGetValue(start, out XYZ movedStart)
                            ? movedStart
                            : mesh.Vertices[start];
                    XYZ endPoint = proposed is not null &&
                        proposed.TryGetValue(end, out XYZ movedEnd)
                            ? movedEnd
                            : mesh.Vertices[end];
                    if (!IsFinite(startPoint) || !IsFinite(endPoint))
                    {
                        throw new InvalidOperationException(
                            "A modern MESH control vertex must be finite.");
                    }
                    if (start == end)
                    {
                        continue;
                    }
                    if (startPoint == endPoint)
                    {
                        throw new InvalidOperationException(
                            "Mesh-subobject translation would collapse an authored control edge.");
                    }
                }
                if (distinct.Count < 3)
                {
                    throw new InvalidOperationException(
                        "A modern MESH face requires three distinct control vertices.");
                }
            }
        }

        private static bool IsFinite(XYZ point) =>
            double.IsFinite(point.X) &&
            double.IsFinite(point.Y) &&
            double.IsFinite(point.Z);
    }

    private sealed record MeshEdit(
        Mesh Mesh,
        int ExpectedVertexCount,
        int[] VertexIndices,
        XYZ[] Before,
        XYZ[] After);
}
