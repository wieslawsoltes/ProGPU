using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

/// <summary>Published result of one modern-MESH smoothness adjustment.</summary>
public readonly record struct CadMesh3DSmoothnessSummary(
    int SelectedMeshCount,
    int AffectedMeshCount,
    int MinimumResultLevel,
    int MaximumResultLevel);

/// <summary>
/// Increases or decreases each eligible direct model-space modern-MESH
/// subdivision level by exactly one.
/// </summary>
/// <remarks>
/// Meshes already at the requested boundary are filtered from the edit. The
/// first application is O(M + C) for model-space meshes M and authored face
/// corners C because it preflights the aggregate retained-refinement budget.
/// Undo and redo are O(A) for affected meshes A with retained level arrays.
/// </remarks>
public sealed class CadAdjustMeshSubdivisionLevelCommand : CadEditCommand
{
    public const int MaximumMeshCount = 65_536;

    private readonly ulong[] _handles;
    private Mesh[]? _meshes;
    private int[]? _before;
    private int[]? _after;

    public ReadOnlyMemory<ulong> Handles => _handles;
    public int Delta { get; }
    public int MaxSubdivisionLevel { get; }
    public int MaxTopologyVisits { get; }
    public int AffectedMeshCount { get; private set; }
    public int MinimumResultLevel { get; private set; }
    public int MaximumResultLevel { get; private set; }
    public CadMesh3DSmoothnessSummary Summary => new(
        _handles.Length,
        AffectedMeshCount,
        MinimumResultLevel,
        MaximumResultLevel);

    public CadAdjustMeshSubdivisionLevelCommand(
        IEnumerable<ulong> handles,
        int delta,
        string description = "Adjust mesh smoothness",
        int maxSubdivisionLevel =
            CadSnapshotOptions.DefaultMaxMeshSubdivisionLevel,
        int maxTopologyVisits =
            CadSnapshotOptions.DefaultMaxMeshSubdivisionTopologyVisits)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (delta is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                "Mesh smoothness changes by exactly one level.");
        }
        if (maxSubdivisionLevel is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSubdivisionLevel),
                "The maximum mesh subdivision level must be between 1 and 255.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTopologyVisits);

        _handles = handles
            .Distinct()
            .Take(MaximumMeshCount + 1)
            .ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero modern-MESH handle is required.",
                nameof(handles));
        }
        if (_handles.Length > MaximumMeshCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handles),
                $"At most {MaximumMeshCount:N0} distinct modern-MESH handles are supported.");
        }

        Delta = delta;
        MaxSubdivisionLevel = maxSubdivisionLevel;
        MaxTopologyVisits = maxTopologyVisits;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (isRedo)
        {
            Mesh[] meshes = GetRetainedMeshes(document);
            int[] before = _before!;
            ValidateLevels(meshes, before);
            SetLevels(meshes, _after!);
            return;
        }

        Entity[] entities = ResolveModelSpaceEntities(document, _handles);
        var eligibleMeshes = new List<Mesh>(entities.Length);
        var beforeLevels = new List<int>(entities.Length);
        var afterLevels = new List<int>(entities.Length);
        foreach (Entity entity in entities)
        {
            Mesh mesh = entity as Mesh ?? throw new InvalidOperationException(
                $"Model-space entity handle {entity.Handle:X} is not a modern MESH.");
            if (mesh.SubdivisionLevel < 0)
            {
                throw new InvalidOperationException(
                    "A modern-MESH subdivision level cannot be negative.");
            }
            int after = Delta > 0
                ? mesh.SubdivisionLevel >= MaxSubdivisionLevel
                    ? mesh.SubdivisionLevel
                    : mesh.SubdivisionLevel + 1
                : Math.Max(mesh.SubdivisionLevel - 1, 0);
            if (after == mesh.SubdivisionLevel)
            {
                continue;
            }
            eligibleMeshes.Add(mesh);
            beforeLevels.Add(mesh.SubdivisionLevel);
            afterLevels.Add(after);
        }
        if (eligibleMeshes.Count == 0)
        {
            throw new InvalidOperationException(
                Delta > 0
                    ? $"Every selected modern MESH is already at subdivision level {MaxSubdivisionLevel}."
                    : "Every selected modern MESH is already at subdivision level zero.");
        }

        Mesh[] retainedMeshes = eligibleMeshes.ToArray();
        int[] retainedBefore = beforeLevels.ToArray();
        int[] retainedAfter = afterLevels.ToArray();
        ValidateAggregateTopologyBudget(
            document,
            retainedMeshes,
            retainedAfter,
            MaxSubdivisionLevel,
            MaxTopologyVisits);
        _meshes = retainedMeshes;
        _before = retainedBefore;
        _after = retainedAfter;
        AffectedMeshCount = retainedMeshes.Length;
        MinimumResultLevel = retainedAfter.Min();
        MaximumResultLevel = retainedAfter.Max();
        SetLevels(retainedMeshes, retainedAfter);
    }

    internal override void Revert(CadDocument document)
    {
        Mesh[] meshes = GetRetainedMeshes(document);
        ValidateLevels(meshes, _after!);
        SetLevels(meshes, _before!);
    }

    private Mesh[] GetRetainedMeshes(CadDocument document)
    {
        Mesh[] meshes = _meshes ?? throw new InvalidOperationException(
            "The mesh-smoothness command has not been applied.");
        foreach (Mesh mesh in meshes)
        {
            ValidateModelSpaceEntity(document, mesh);
        }
        return meshes;
    }

    private static void ValidateLevels(
        ReadOnlySpan<Mesh> meshes,
        ReadOnlySpan<int> expected)
    {
        for (int index = 0; index < meshes.Length; index++)
        {
            if (meshes[index].SubdivisionLevel != expected[index])
            {
                throw new InvalidOperationException(
                    "A retained modern-MESH subdivision level changed outside its edit history.");
            }
        }
    }

    private static void SetLevels(
        ReadOnlySpan<Mesh> meshes,
        ReadOnlySpan<int> levels)
    {
        for (int index = 0; index < meshes.Length; index++)
        {
            meshes[index].SubdivisionLevel = levels[index];
        }
    }

    private static void ValidateAggregateTopologyBudget(
        CadDocument document,
        ReadOnlySpan<Mesh> editedMeshes,
        ReadOnlySpan<int> editedLevels,
        int maxSubdivisionLevel,
        int maxTopologyVisits)
    {
        var proposedLevels = new Dictionary<Mesh, int>(
            ReferenceEqualityComparer.Instance);
        for (int index = 0; index < editedMeshes.Length; index++)
        {
            proposedLevels.Add(editedMeshes[index], editedLevels[index]);
        }

        long visits = 0;
        foreach (Entity entity in document.Entities)
        {
            if (entity is not Mesh mesh)
            {
                continue;
            }
            int level = proposedLevels.GetValueOrDefault(
                mesh,
                mesh.SubdivisionLevel);
            if (level < 0 || level > maxSubdivisionLevel)
            {
                if (!proposedLevels.ContainsKey(mesh))
                {
                    continue;
                }
                throw new InvalidOperationException(
                    $"Modern-MESH subdivision level {level} is outside the configured range 0-{maxSubdivisionLevel}.");
            }
            if (level == 0)
            {
                continue;
            }
            int corners = 0;
            foreach (int[]? face in mesh.Faces)
            {
                if (face is null || face.Length < 3)
                {
                    throw new InvalidOperationException(
                        "Every modern-MESH face requires at least three control vertices.");
                }
                corners = checked(corners + face.Length);
            }
            long levelCorners = corners;
            visits = checked(visits + levelCorners);
            for (int refinement = 0; refinement < level; refinement++)
            {
                levelCorners = checked(levelCorners * 4);
                visits = checked(visits + levelCorners);
                if (visits > maxTopologyVisits)
                {
                    throw new InvalidOperationException(
                        $"Mesh smoothing exceeds the configured {maxTopologyVisits:N0}-corner aggregate refinement limit.");
                }
            }
        }
        if (visits > maxTopologyVisits)
        {
            throw new InvalidOperationException(
                $"Mesh smoothing exceeds the configured {maxTopologyVisits:N0}-corner aggregate refinement limit.");
        }
    }
}

/// <summary>Published result of one modern-MESH crease edit.</summary>
public readonly record struct CadMesh3DCreaseSummary(
    int SelectedSubobjectCount,
    int AffectedMeshCount,
    int AffectedEdgeCount,
    double CreaseValue);

/// <summary>
/// Sets or removes creases on authored edges addressed by modern-MESH edge,
/// face, and vertex subobjects.
/// </summary>
/// <remarks>
/// An edge affects itself, a face affects every boundary edge, and a vertex
/// affects every incident edge. Value -1 means always sharp, zero removes the
/// crease, and a positive value is the smoothing level where decay begins.
/// First application is O(S + V + C + K) time and storage for selected
/// subobjects S, vertices V, face corners C, and crease records K. Undo and
/// redo replace retained crease tables in O(K).
/// </remarks>
public sealed class CadSetMeshSubobjectCreaseCommand : CadEditCommand
{
    public const int DefaultMaxSubobjects = 4_096;
    public const int DefaultMaxFaceCorners = 4_000_000;
    public const int DefaultMaxAffectedEdges = 1_000_000;

    private readonly CadMesh3DSubobjectId[] _subobjects;
    private readonly CadMesh3DSubobjectEditTarget[] _targets;
    private MeshCreaseEdit[]? _edits;

    public ReadOnlyMemory<CadMesh3DSubobjectId> Subobjects => _subobjects;
    public ulong SourceContentGeneration { get; }
    public double CreaseValue { get; }
    public int MaxSubobjects { get; }
    public int MaxFaceCorners { get; }
    public int MaxAffectedEdges { get; }
    public int AffectedMeshCount { get; private set; }
    public int AffectedEdgeCount { get; private set; }
    public CadMesh3DCreaseSummary Summary => new(
        _subobjects.Length,
        AffectedMeshCount,
        AffectedEdgeCount,
        CreaseValue);

    internal override ulong? ExpectedContentGeneration =>
        SourceContentGeneration;

    public CadSetMeshSubobjectCreaseCommand(
        CadRecordedMesh3DScene scene,
        IEnumerable<CadMesh3DSubobjectId> subobjects,
        double creaseValue,
        string description = "Set mesh-subobject crease",
        int maxSubobjects = DefaultMaxSubobjects,
        int maxFaceCorners = DefaultMaxFaceCorners,
        int maxAffectedEdges = DefaultMaxAffectedEdges)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!double.IsFinite(creaseValue) ||
            creaseValue < 0.0 && creaseValue != -1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(creaseValue),
                "A mesh crease must be -1, zero, or a finite positive level.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFaceCorners);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAffectedEdges);

        SourceContentGeneration = scene.ContentGeneration;
        CreaseValue = creaseValue;
        MaxSubobjects = maxSubobjects;
        MaxFaceCorners = maxFaceCorners;
        MaxAffectedEdges = maxAffectedEdges;
        CadMesh3DSubobjectEditSelectionResolver.Resolve(
            scene,
            subobjects,
            maxSubobjects,
            out _subobjects,
            out _targets);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        MeshCreaseEdit[] edits = isRedo
            ? GetRetainedEdits()
            : BuildEdits(document);
        foreach (MeshCreaseEdit edit in edits)
        {
            ValidateAttachedState(document, edit, edit.BeforeEdges);
        }
        foreach (MeshCreaseEdit edit in edits)
        {
            ApplyEdges(edit.Mesh, edit.AfterEdges);
        }
        if (!isRedo)
        {
            _edits = edits;
        }
    }

    internal override void Revert(CadDocument document)
    {
        MeshCreaseEdit[] edits = GetRetainedEdits();
        foreach (MeshCreaseEdit edit in edits)
        {
            ValidateAttachedState(document, edit, edit.AfterEdges);
        }
        foreach (MeshCreaseEdit edit in edits)
        {
            ApplyEdges(edit.Mesh, edit.BeforeEdges);
        }
    }

    private MeshCreaseEdit[] BuildEdits(CadDocument document)
    {
        var builders = new Dictionary<ulong, CreaseBuilder>();
        foreach (CadMesh3DSubobjectEditTarget target in _targets)
        {
            if (!builders.TryGetValue(
                    target.SourceHandle,
                    out CreaseBuilder? builder))
            {
                Entity entity = ResolveModelSpaceEntity(
                    document,
                    target.SourceHandle);
                Mesh mesh = entity as Mesh ?? throw new InvalidOperationException(
                    $"Model-space entity handle {target.SourceHandle:X} is not a modern MESH.");
                builder = new CreaseBuilder(mesh, target, CreaseValue);
                builders.Add(target.SourceHandle, builder);
            }
            else
            {
                builder.ValidateExpectedTopology(target);
            }
            builder.Add(target);
        }

        int faceCorners = 0;
        int affectedEdges = 0;
        var edits = new MeshCreaseEdit[builders.Count];
        int editIndex = 0;
        foreach (CreaseBuilder builder in builders.Values)
        {
            faceCorners = checked(faceCorners + builder.FaceCornerCount);
            if (faceCorners > MaxFaceCorners)
            {
                throw new InvalidOperationException(
                    $"Mesh-crease editing visits more than the configured {MaxFaceCorners:N0} face corners.");
            }
            MeshCreaseEdit edit = builder.CreateEdit();
            edits[editIndex++] = edit;
            affectedEdges = checked(affectedEdges + edit.AffectedEdgeCount);
            if (affectedEdges > MaxAffectedEdges)
            {
                throw new InvalidOperationException(
                    $"Mesh-crease editing affects more than the configured {MaxAffectedEdges:N0} authored edges.");
            }
        }
        if (affectedEdges == 0)
        {
            throw new InvalidOperationException(
                "The selected mesh subobjects have no incident authored edges.");
        }
        AffectedMeshCount = edits.Length;
        AffectedEdgeCount = affectedEdges;
        return edits;
    }

    private MeshCreaseEdit[] GetRetainedEdits() =>
        _edits ?? throw new InvalidOperationException(
            "The mesh-crease command has not been applied.");

    private static void ValidateAttachedState(
        CadDocument document,
        MeshCreaseEdit edit,
        ReadOnlySpan<Mesh.Edge> expectedEdges)
    {
        ValidateModelSpaceEntity(document, edit.Mesh);
        if (edit.Mesh.Vertices.Count != edit.VertexCount ||
            edit.Mesh.Faces.Count != edit.Faces.Length ||
            edit.Mesh.Edges.Count != expectedEdges.Length)
        {
            throw new InvalidOperationException(
                "A retained modern-MESH topology changed outside its edit history.");
        }
        for (int faceIndex = 0; faceIndex < edit.Faces.Length; faceIndex++)
        {
            if (!edit.Faces[faceIndex].AsSpan().SequenceEqual(
                    edit.Mesh.Faces[faceIndex]))
            {
                throw new InvalidOperationException(
                    "A retained modern-MESH face changed outside its edit history.");
            }
        }
        for (int edgeIndex = 0; edgeIndex < expectedEdges.Length; edgeIndex++)
        {
            if (!edit.Mesh.Edges[edgeIndex].Equals(expectedEdges[edgeIndex]))
            {
                throw new InvalidOperationException(
                    "A retained modern-MESH crease changed outside its edit history.");
            }
        }
    }

    private static void ApplyEdges(Mesh mesh, ReadOnlySpan<Mesh.Edge> edges)
    {
        mesh.Edges.Clear();
        foreach (Mesh.Edge edge in edges)
        {
            mesh.Edges.Add(edge);
        }
    }

    private readonly record struct EdgeKey(int First, int Second)
    {
        public static EdgeKey Create(int first, int second) => first < second
            ? new EdgeKey(first, second)
            : new EdgeKey(second, first);
    }

    private sealed class CreaseBuilder
    {
        private readonly CadMeshSourceTopology _topology;
        private readonly Mesh.Edge[] _beforeEdges;
        private readonly int[][] _faces;
        private readonly int _expectedVertexCount;
        private readonly int _expectedEdgeCount;
        private readonly int _expectedFaceCount;
        private readonly double _creaseValue;
        private readonly HashSet<int> _selectedVertices = [];
        private readonly HashSet<int> _selectedEdges = [];
        private readonly HashSet<int> _selectedFaces = [];

        public Mesh Mesh { get; }
        public int FaceCornerCount { get; }

        public CreaseBuilder(
            Mesh mesh,
            in CadMesh3DSubobjectEditTarget target,
            double creaseValue)
        {
            Mesh = mesh;
            _creaseValue = creaseValue;
            _faces = mesh.Faces
                .Select(static face => face?.ToArray() ??
                    throw new InvalidOperationException(
                        "A modern-MESH face cannot be null."))
                .ToArray();
            _beforeEdges = mesh.Edges.ToArray();
            _topology = ValidateTopology(mesh, _faces, _beforeEdges);
            _expectedVertexCount = target.VertexCount;
            _expectedEdgeCount = target.EdgeCount;
            _expectedFaceCount = target.FaceCount;
            ValidateExpectedTopology(target);
            foreach (int[] face in _faces)
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
                Mesh.Vertices.Count != _expectedVertexCount ||
                _topology.SourceEdgeVertexChains.Length != _expectedEdgeCount ||
                _faces.Length != _expectedFaceCount)
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

        public MeshCreaseEdit CreateEdit()
        {
            if (_creaseValue > 0.0 &&
                _creaseValue != Math.Truncate(_creaseValue) &&
                !Mesh.BlendCrease)
            {
                throw new InvalidOperationException(
                    "A fractional crease value requires Blend Crease on every selected modern MESH.");
            }

            var affected = new bool[_topology.SourceEdgeVertexChains.Length];
            for (int faceIndex = 0; faceIndex < _faces.Length; faceIndex++)
            {
                int[] face = _faces[faceIndex];
                int[] faceEdges =
                    _topology.FaceCornerSourceEdgeIndices[faceIndex];
                for (int corner = 0; corner < face.Length; corner++)
                {
                    int start = face[corner];
                    int end = face[(corner + 1) % face.Length];
                    int edgeIndex = faceEdges[corner];
                    if (_selectedFaces.Contains(faceIndex) ||
                        _selectedEdges.Contains(edgeIndex) ||
                        _selectedVertices.Contains(start) ||
                        _selectedVertices.Contains(end))
                    {
                        affected[edgeIndex] = true;
                    }
                }
            }

            int affectedEdgeCount = affected.Count(static value => value);
            var sourceEdgeIndices = new Dictionary<EdgeKey, int>(
                _topology.SourceEdgeVertexChains.Length);
            for (int edgeIndex = 0;
                 edgeIndex < _topology.SourceEdgeVertexChains.Length;
                 edgeIndex++)
            {
                int[] chain = _topology.SourceEdgeVertexChains[edgeIndex];
                sourceEdgeIndices.Add(
                    EdgeKey.Create(chain[0], chain[^1]),
                    edgeIndex);
            }

            var written = new bool[affected.Length];
            var after = new List<Mesh.Edge>(
                checked(_beforeEdges.Length + affectedEdgeCount));
            foreach (Mesh.Edge edge in _beforeEdges)
            {
                int edgeIndex = sourceEdgeIndices[
                    EdgeKey.Create(edge.Start, edge.End)];
                if (!affected[edgeIndex])
                {
                    after.Add(edge);
                    continue;
                }
                written[edgeIndex] = true;
                if (_creaseValue != 0.0)
                {
                    after.Add(new Mesh.Edge(edge.Start, edge.End)
                    {
                        Crease = _creaseValue,
                    });
                }
            }
            if (_creaseValue != 0.0)
            {
                for (int edgeIndex = 0; edgeIndex < affected.Length; edgeIndex++)
                {
                    if (!affected[edgeIndex] || written[edgeIndex])
                    {
                        continue;
                    }
                    int[] chain =
                        _topology.SourceEdgeVertexChains[edgeIndex];
                    after.Add(new Mesh.Edge(chain[0], chain[^1])
                    {
                        Crease = _creaseValue,
                    });
                }
            }

            return new MeshCreaseEdit(
                Mesh,
                _beforeEdges,
                after.ToArray(),
                Mesh.Vertices.Count,
                _faces,
                affectedEdgeCount);
        }

        private static CadMeshSourceTopology ValidateTopology(
            Mesh mesh,
            int[][] faces,
            ReadOnlySpan<Mesh.Edge> creaseEdges)
        {
            if (mesh.Vertices.Count < 3 || faces.Length == 0)
            {
                throw new InvalidOperationException(
                    "A modern MESH requires at least three control vertices and one face.");
            }
            foreach (XYZ point in mesh.Vertices)
            {
                if (!double.IsFinite(point.X) ||
                    !double.IsFinite(point.Y) ||
                    !double.IsFinite(point.Z))
                {
                    throw new InvalidOperationException(
                        "A modern-MESH control vertex must be finite.");
                }
            }

            var topologyEdges = new HashSet<EdgeKey>();
            var distinctVertices = new HashSet<int>();
            foreach (int[] face in faces)
            {
                if (face.Length < 3)
                {
                    throw new InvalidOperationException(
                        "Every modern-MESH face requires at least three control vertices.");
                }
                distinctVertices.Clear();
                for (int corner = 0; corner < face.Length; corner++)
                {
                    int start = face[corner];
                    int end = face[(corner + 1) % face.Length];
                    if ((uint)start >= (uint)mesh.Vertices.Count ||
                        (uint)end >= (uint)mesh.Vertices.Count)
                    {
                        throw new InvalidOperationException(
                            "A modern-MESH face references a missing control vertex.");
                    }
                    distinctVertices.Add(start);
                    if (start == end || mesh.Vertices[start] == mesh.Vertices[end])
                    {
                        throw new InvalidOperationException(
                            "A modern MESH contains a collapsed control edge.");
                    }
                    topologyEdges.Add(EdgeKey.Create(start, end));
                }
                if (distinctVertices.Count < 3)
                {
                    throw new InvalidOperationException(
                        "A modern-MESH face requires three distinct control vertices.");
                }
            }

            var seenCreases = new HashSet<EdgeKey>();
            foreach (Mesh.Edge edge in creaseEdges)
            {
                if (edge.Start == edge.End ||
                    (uint)edge.Start >= (uint)mesh.Vertices.Count ||
                    (uint)edge.End >= (uint)mesh.Vertices.Count)
                {
                    throw new InvalidOperationException(
                        "A modern-MESH crease edge has invalid control-vertex indices.");
                }
                EdgeKey key = EdgeKey.Create(edge.Start, edge.End);
                if (!seenCreases.Add(key) || !topologyEdges.Contains(key))
                {
                    throw new InvalidOperationException(
                        "A modern-MESH crease edge must uniquely reference a persisted face edge.");
                }
                if (edge.Crease is double crease &&
                    (!double.IsFinite(crease) ||
                     crease < 0.0 && crease != -1.0 ||
                     !mesh.BlendCrease &&
                     crease >= 0.0 &&
                     crease != Math.Truncate(crease)))
                {
                    throw new InvalidOperationException(
                        "A modern-MESH crease must be -1, zero, or a finite positive level; fractional levels require Blend Crease.");
                }
            }
            return CadMeshSubdivision.CreateSourceTopology(faces);
        }
    }

    private sealed record MeshCreaseEdit(
        Mesh Mesh,
        Mesh.Edge[] BeforeEdges,
        Mesh.Edge[] AfterEdges,
        int VertexCount,
        int[][] Faces,
        int AffectedEdgeCount);
}
