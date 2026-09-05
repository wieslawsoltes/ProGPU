using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

internal readonly record struct CadMesh3DSubobjectEditTarget(
    ulong SourceHandle,
    CadMesh3DSubobjectKind Kind,
    int Index,
    int VertexCount,
    int EdgeCount,
    int FaceCount);

internal static class CadMesh3DSubobjectEditSelectionResolver
{
    public static void Resolve(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        int maxSubobjects,
        out CadMesh3DSubobjectId[] ids,
        out CadMesh3DSubobjectEditTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(subobjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSubobjects);

        var retainedIds = new List<CadMesh3DSubobjectId>();
        var retainedTargets = new List<CadMesh3DSubobjectEditTarget>();
        var distinct = new HashSet<CadMesh3DSubobjectId>();
        foreach (CadMesh3DSubobjectId id in subobjects)
        {
            if (!distinct.Add(id))
            {
                continue;
            }
            if (retainedIds.Count >= maxSubobjects)
            {
                throw new ArgumentException(
                    $"Mesh-subobject selection exceeds the configured limit of {maxSubobjects}.",
                    nameof(subobjects));
            }
            if (!scene.TryGetSubobjectComponent(
                    id,
                    out CadMesh3DSubobjectComponent? component) ||
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
            retainedIds.Add(id);
            retainedTargets.Add(new CadMesh3DSubobjectEditTarget(
                component.SourceHandle,
                id.Kind,
                id.Index,
                component.VertexPositions.Length,
                component.Edges.Length,
                component.Faces.Length));
        }
        if (retainedIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one mesh subobject is required.",
                nameof(subobjects));
        }

        ids = retainedIds.ToArray();
        targets = retainedTargets.ToArray();
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
}

/// <summary>
/// Base for bounded transforms of authored vertices, edges, and faces on
/// direct model-space modern MESH entities.
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
public abstract class CadTransformMeshSubobjectsCommand : CadEditCommand
{
    public const int DefaultMaxSubobjects = 4_096;
    public const int DefaultMaxAffectedVertices = 1_000_000;

    private readonly CadMesh3DSubobjectId[] _subobjects;
    private readonly CadMesh3DSubobjectEditTarget[] _targets;
    private MeshEdit[]? _edits;

    public ReadOnlyMemory<CadMesh3DSubobjectId> Subobjects => _subobjects;
    public ulong SourceContentGeneration { get; }
    public int MaxSubobjects { get; }
    public int MaxAffectedVertices { get; }

    internal override ulong? ExpectedContentGeneration =>
        SourceContentGeneration;

    private protected CadTransformMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        string description,
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAffectedVertices);
        MaxSubobjects = maxSubobjects;
        MaxAffectedVertices = maxAffectedVertices;
        SourceContentGeneration = scene.ContentGeneration;
        CadMesh3DSubobjectEditSelectionResolver.Resolve(
            scene,
            subobjects,
            maxSubobjects,
            out _subobjects,
            out _targets);
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
        foreach (CadMesh3DSubobjectEditTarget target in _targets)
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
        CadBounds3D affectedBounds = CadBounds3D.Empty;
        var edits = new MeshEdit[builders.Count];
        foreach (EditBuilder builder in builders.Values)
        {
            affectedVertexCount = checked(
                affectedVertexCount + builder.VertexIndices.Count);
            if (affectedVertexCount > MaxAffectedVertices)
            {
                throw new InvalidOperationException(
                    $"Mesh-subobject edit affects more than the configured {MaxAffectedVertices} vertices.");
            }
            affectedBounds = affectedBounds.Union(builder.GetAffectedBounds());
        }
        PrepareTransform(affectedBounds);
        int editIndex = 0;
        foreach (EditBuilder builder in builders.Values)
        {
            edits[editIndex++] = builder.CreateEdit(this);
        }
        return edits;
    }

    private MeshEdit[] GetRetainedEdits(CadDocument document)
    {
        MeshEdit[] edits = _edits ?? throw new InvalidOperationException(
            "The mesh-subobject transform command has not been applied.");
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

    protected static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private protected abstract CadPoint3D TransformPoint(CadPoint3D point);

    private protected virtual void PrepareTransform(CadBounds3D affectedBounds)
    {
    }

    private XYZ TransformPoint(XYZ point)
    {
        CadPoint3D transformed = TransformPoint(new CadPoint3D(
            point.X,
            point.Y,
            point.Z));
        if (!IsFinite(transformed))
        {
            throw new InvalidOperationException(
                "Mesh-subobject transform produces a non-finite control vertex.");
        }
        return new XYZ(transformed.X, transformed.Y, transformed.Z);
    }

    private sealed class EditBuilder
    {
        private readonly CadMeshSourceTopology _topology;
        private readonly int _expectedVertexCount;
        private readonly int _expectedEdgeCount;
        private readonly int _expectedFaceCount;

        public Mesh Mesh { get; }
        public HashSet<int> VertexIndices { get; } = [];

        public EditBuilder(
            Mesh mesh,
            in CadMesh3DSubobjectEditTarget target)
        {
            Mesh = mesh;
            _topology = CadMeshSubdivision.CreateSourceTopology(mesh.Faces);
            _expectedVertexCount = target.VertexCount;
            _expectedEdgeCount = target.EdgeCount;
            _expectedFaceCount = target.FaceCount;
            ValidateExpectedTopology(target);
            ValidateMeshTopology(mesh, proposed: null);
        }

        public void ValidateExpectedTopology(
            in CadMesh3DSubobjectEditTarget target)
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

        public void Add(in CadMesh3DSubobjectEditTarget target)
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

        public MeshEdit CreateEdit(
            CadTransformMeshSubobjectsCommand command)
        {
            int[] indices = VertexIndices.Order().ToArray();
            var before = new XYZ[indices.Length];
            var after = new XYZ[indices.Length];
            var proposed = new Dictionary<int, XYZ>(indices.Length);
            for (int index = 0; index < indices.Length; index++)
            {
                XYZ source = Mesh.Vertices[indices[index]];
                XYZ destination = command.TransformPoint(source);
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

        public CadBounds3D GetAffectedBounds()
        {
            CadBounds3D bounds = CadBounds3D.Empty;
            foreach (int index in VertexIndices)
            {
                XYZ point = Mesh.Vertices[index];
                if (!IsFinite(point))
                {
                    throw new InvalidOperationException(
                        "A modern MESH control vertex must be finite.");
                }
                bounds = bounds.Include(new CadPoint3D(
                    point.X,
                    point.Y,
                    point.Z));
            }
            return bounds;
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
                            "Mesh-subobject transform would collapse an authored control edge.");
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

/// <summary>Translates authored modern-MESH subobjects in WCS.</summary>
public sealed class CadTranslateMeshSubobjectsCommand :
    CadTransformMeshSubobjectsCommand
{
    public CadPoint3D Translation { get; }

    public CadTranslateMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        CadPoint3D translation,
        string description = "Translate mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : base(
            scene,
            subobjects,
            description,
            maxSubobjects,
            maxAffectedVertices)
    {
        if (!IsFinite(translation) || translation == CadPoint3D.Zero)
        {
            throw new ArgumentException(
                "A mesh-subobject translation must be finite and non-zero.",
                nameof(translation));
        }
        Translation = translation;
    }

    private protected override CadPoint3D TransformPoint(CadPoint3D point) =>
        point + Translation;
}

/// <summary>Rotates authored modern-MESH subobjects around one WCS axis.</summary>
public sealed class CadRotateMeshSubobjectsCommand :
    CadTransformMeshSubobjectsCommand
{
    private readonly double _cosine;
    private readonly double _sine;
    private readonly bool _usesSelectionCenter;

    public CadPoint3D Axis { get; }
    public double Radians { get; }
    public CadPoint3D Pivot { get; private set; }

    public CadRotateMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        CadPoint3D axis,
        double radians,
        string description = "Rotate mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : this(
            scene,
            subobjects,
            axis,
            radians,
            CadPoint3D.Zero,
            description,
            maxSubobjects,
            maxAffectedVertices)
    {
        _usesSelectionCenter = true;
    }

    public CadRotateMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        CadPoint3D axis,
        double radians,
        CadPoint3D pivot,
        string description = "Rotate mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : base(
            scene,
            subobjects,
            description,
            maxSubobjects,
            maxAffectedVertices)
    {
        if (!double.IsFinite(radians) || radians == 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radians),
                "A mesh-subobject rotation must be finite and non-zero.");
        }
        if (!IsFinite(pivot))
        {
            throw new ArgumentException(
                "A mesh-subobject rotation pivot must be finite.",
                nameof(pivot));
        }
        Axis = axis.Normalize();
        Radians = radians;
        Pivot = pivot;
        _cosine = Math.Cos(radians);
        _sine = Math.Sin(radians);
    }

    private protected override void PrepareTransform(CadBounds3D affectedBounds)
    {
        if (_usesSelectionCenter)
        {
            Pivot = affectedBounds.Center;
        }
    }

    private protected override CadPoint3D TransformPoint(CadPoint3D point)
    {
        CadPoint3D relative = point - Pivot;
        return Pivot +
            (relative * _cosine) +
            (CadPoint3D.Cross(Axis, relative) * _sine) +
            (Axis * (CadPoint3D.Dot(Axis, relative) * (1.0 - _cosine)));
    }
}

/// <summary>Uniformly scales authored modern-MESH subobjects around a WCS pivot.</summary>
public sealed class CadScaleMeshSubobjectsCommand :
    CadTransformMeshSubobjectsCommand
{
    public double Factor { get; }
    private readonly bool _usesSelectionCenter;

    public CadPoint3D Pivot { get; private set; }

    public CadScaleMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        double factor,
        string description = "Scale mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : this(
            scene,
            subobjects,
            factor,
            CadPoint3D.Zero,
            description,
            maxSubobjects,
            maxAffectedVertices)
    {
        _usesSelectionCenter = true;
    }

    public CadScaleMeshSubobjectsCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        double factor,
        CadPoint3D pivot,
        string description = "Scale mesh subobjects",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxAffectedVertices = DefaultMaxAffectedVertices)
        : base(
            scene,
            subobjects,
            description,
            maxSubobjects,
            maxAffectedVertices)
    {
        double inverseFactor = 1.0 / factor;
        if (!double.IsFinite(factor) ||
            factor <= 0.0 ||
            factor == 1.0 ||
            !double.IsFinite(inverseFactor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor),
                "A mesh-subobject scale factor must be positive, finite, non-unit, and have a finite inverse.");
        }
        if (!IsFinite(pivot))
        {
            throw new ArgumentException(
                "A mesh-subobject scale pivot must be finite.",
                nameof(pivot));
        }
        Factor = factor;
        Pivot = pivot;
    }

    private protected override void PrepareTransform(CadBounds3D affectedBounds)
    {
        if (_usesSelectionCenter)
        {
            Pivot = affectedBounds.Center;
        }
    }

    private protected override CadPoint3D TransformPoint(CadPoint3D point) =>
        Pivot + ((point - Pivot) * Factor);
}
