using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

/// <summary>Published result of baking modern-MESH subdivision topology.</summary>
public readonly record struct CadMesh3DRefinementSummary(
    int SelectedMeshCount,
    int AffectedMeshCount,
    int SourceControlVertexCount,
    int SourceFaceCount,
    int ResultControlVertexCount,
    int ResultFaceCount,
    int ResultAuthoredCreaseEdgeCount,
    int TopologyVisitCount);

/// <summary>
/// Bakes every eligible direct model-space modern-MESH object's displayed
/// subdivision into editable level-zero topology.
/// </summary>
/// <remarks>
/// Whole-object refinement applies the existing bounded ProGPU-owned
/// Catmull-Clark implementation at the persisted level, lowers finite authored
/// crease values by that level, preserves infinitely sharp creases, refines
/// UVW values in double precision, and resets the persisted level to zero.
/// Level-zero meshes are filtered. Every result is built and validated before
/// the first document mutation. Initial application is O(T + K*2^L) time and
/// O(T + R) storage for bounded visited topology T, authored crease records K,
/// level L, and retained result topology R. Undo and redo are O(R).
/// </remarks>
public sealed class CadRefineMesh3DCommand : CadEditCommand
{
    public const int MaximumMeshCount = 65_536;
    public const int DefaultMaxResultControlVertices = 1_000_000;
    public const int DefaultMaxResultFaces = 1_000_000;
    public const int DefaultMaxResultAuthoredCreaseEdges = 1_000_000;

    private readonly ulong[] _handles;
    private readonly CancellationToken _cancellationToken;
    private MeshRefinement[]? _refinements;

    public ReadOnlyMemory<ulong> Handles => _handles;
    public int MaxTopologyVisits { get; }
    public int MaxResultControlVertices { get; }
    public int MaxResultFaces { get; }
    public int MaxResultAuthoredCreaseEdges { get; }
    public int AffectedMeshCount { get; private set; }
    public int SourceControlVertexCount { get; private set; }
    public int SourceFaceCount { get; private set; }
    public int ResultControlVertexCount { get; private set; }
    public int ResultFaceCount { get; private set; }
    public int ResultAuthoredCreaseEdgeCount { get; private set; }
    public int TopologyVisitCount { get; private set; }
    public CadMesh3DRefinementSummary Summary => new(
        _handles.Length,
        AffectedMeshCount,
        SourceControlVertexCount,
        SourceFaceCount,
        ResultControlVertexCount,
        ResultFaceCount,
        ResultAuthoredCreaseEdgeCount,
        TopologyVisitCount);

    public CadRefineMesh3DCommand(
        IEnumerable<ulong> handles,
        string description = "Refine mesh",
        int maxTopologyVisits =
            CadSnapshotOptions.DefaultMaxMeshSubdivisionTopologyVisits,
        int maxResultControlVertices = DefaultMaxResultControlVertices,
        int maxResultFaces = DefaultMaxResultFaces,
        int maxResultAuthoredCreaseEdges =
            DefaultMaxResultAuthoredCreaseEdges,
        CancellationToken cancellationToken = default)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTopologyVisits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maxResultControlVertices);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultFaces);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maxResultAuthoredCreaseEdges);

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

        MaxTopologyVisits = maxTopologyVisits;
        MaxResultControlVertices = maxResultControlVertices;
        MaxResultFaces = maxResultFaces;
        MaxResultAuthoredCreaseEdges = maxResultAuthoredCreaseEdges;
        _cancellationToken = cancellationToken;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        MeshRefinement[] refinements = isRedo
            ? GetRetainedRefinements()
            : BuildRefinements(document);
        foreach (MeshRefinement refinement in refinements)
        {
            ValidateAttachedState(document, refinement.Mesh, refinement.Before);
        }
        foreach (MeshRefinement refinement in refinements)
        {
            ApplyState(refinement.Mesh, refinement.After);
        }
        if (!isRedo)
        {
            _refinements = refinements;
        }
    }

    internal override void Revert(CadDocument document)
    {
        MeshRefinement[] refinements = GetRetainedRefinements();
        foreach (MeshRefinement refinement in refinements)
        {
            ValidateAttachedState(document, refinement.Mesh, refinement.After);
        }
        foreach (MeshRefinement refinement in refinements)
        {
            ApplyState(refinement.Mesh, refinement.Before);
        }
    }

    private MeshRefinement[] BuildRefinements(CadDocument document)
    {
        Entity[] entities = ResolveModelSpaceEntities(document, _handles);
        var refinements = new List<MeshRefinement>(entities.Length);
        int sourceVertices = 0;
        int sourceFaces = 0;
        int resultVertices = 0;
        int resultFaces = 0;
        int resultCreases = 0;
        int topologyVisits = 0;
        foreach (Entity entity in entities)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            Mesh mesh = entity as Mesh ?? throw new InvalidOperationException(
                $"Model-space entity handle {entity.Handle:X} is not a modern MESH.");
            if (mesh.SubdivisionLevel < 0)
            {
                throw new InvalidOperationException(
                    "A modern-MESH subdivision level cannot be negative.");
            }
            if (mesh.SubdivisionLevel == 0)
            {
                continue;
            }

            MeshTopologyState before = CaptureState(mesh);
            ValidateState(before);
            int remainingVisits = MaxTopologyVisits - topologyVisits;
            if (remainingVisits <= 0)
            {
                throw new InvalidOperationException(
                    $"Mesh refinement exceeds the configured {MaxTopologyVisits:N0}-corner aggregate topology-visit limit.");
            }
            CadMeshSubdivisionResult result;
            try
            {
                result = CadMeshSubdivision.Refine(
                    before.Vertices
                        .Select(static vertex => new CadPoint3D(
                            vertex.X,
                            vertex.Y,
                            vertex.Z))
                        .ToArray(),
                    before.TextureCoordinates
                        .Select(static texture => new CadPoint3D(
                            texture.X,
                            texture.Y,
                            texture.Z))
                        .ToArray(),
                    before.Faces,
                    before.Edges
                        .Select(static edge => new CadMeshSubdivisionEdge(
                            edge.Start,
                            edge.End,
                            edge.Crease))
                        .ToArray(),
                    before.SubdivisionLevel,
                    before.BlendCrease,
                    remainingVisits,
                    _cancellationToken);
            }
            catch (CadUnsupportedEntityException exception)
            {
                throw new InvalidOperationException(
                    $"Mesh refinement exceeds the configured {MaxTopologyVisits:N0}-corner aggregate topology-visit limit.",
                    exception);
            }

            MeshTopologyState after = CreateRefinedState(before, result);
            ValidateState(after);
            sourceVertices = checked(sourceVertices + before.Vertices.Length);
            sourceFaces = checked(sourceFaces + before.Faces.Length);
            resultVertices = checked(resultVertices + after.Vertices.Length);
            resultFaces = checked(resultFaces + after.Faces.Length);
            resultCreases = checked(resultCreases + after.Edges.Length);
            topologyVisits = checked(
                topologyVisits + result.TopologyVisitCount);
            ValidateResultLimits(resultVertices, resultFaces, resultCreases);
            refinements.Add(new MeshRefinement(mesh, before, after));
        }
        if (refinements.Count == 0)
        {
            throw new InvalidOperationException(
                "Every selected modern MESH is already at subdivision level zero.");
        }

        AffectedMeshCount = refinements.Count;
        SourceControlVertexCount = sourceVertices;
        SourceFaceCount = sourceFaces;
        ResultControlVertexCount = resultVertices;
        ResultFaceCount = resultFaces;
        ResultAuthoredCreaseEdgeCount = resultCreases;
        TopologyVisitCount = topologyVisits;
        return refinements.ToArray();
    }

    private void ValidateResultLimits(
        int resultVertices,
        int resultFaces,
        int resultCreases)
    {
        if (resultVertices > MaxResultControlVertices)
        {
            throw new InvalidOperationException(
                $"Mesh refinement produces more than the configured {MaxResultControlVertices:N0} control vertices.");
        }
        if (resultFaces > MaxResultFaces)
        {
            throw new InvalidOperationException(
                $"Mesh refinement produces more than the configured {MaxResultFaces:N0} faces.");
        }
        if (resultCreases > MaxResultAuthoredCreaseEdges)
        {
            throw new InvalidOperationException(
                $"Mesh refinement produces more than the configured {MaxResultAuthoredCreaseEdges:N0} authored crease edges.");
        }
    }

    private MeshRefinement[] GetRetainedRefinements() =>
        _refinements ?? throw new InvalidOperationException(
            "The mesh-refinement command has not been applied.");

    private static MeshTopologyState CreateRefinedState(
        MeshTopologyState before,
        CadMeshSubdivisionResult result)
    {
        Mesh.Edge[] edges = CreateRefinedCreases(before, result);
        return new MeshTopologyState(
            result.Vertices
                .Select(static vertex => new XYZ(
                    vertex.X,
                    vertex.Y,
                    vertex.Z))
                .ToArray(),
            result.Faces.Select(static face => face.ToArray()).ToArray(),
            edges,
            result.TextureCoordinates
                .Select(static texture => new XYZ(
                    texture.X,
                    texture.Y,
                    texture.Z))
                .ToArray(),
            SubdivisionLevel: 0,
            before.BlendCrease);
    }

    private static Mesh.Edge[] CreateRefinedCreases(
        MeshTopologyState before,
        CadMeshSubdivisionResult result)
    {
        CadMeshSourceTopology topology =
            CadMeshSubdivision.CreateSourceTopology(before.Faces);
        var sourceIndices = new Dictionary<EdgeKey, int>(
            topology.SourceEdgeVertexChains.Length);
        for (int index = 0;
             index < topology.SourceEdgeVertexChains.Length;
             index++)
        {
            int[] chain = topology.SourceEdgeVertexChains[index];
            sourceIndices.Add(EdgeKey.Create(chain[0], chain[^1]), index);
        }

        var refined = new List<Mesh.Edge>();
        foreach (Mesh.Edge edge in before.Edges)
        {
            if (edge.Crease is not double sourceCrease || sourceCrease == 0.0)
            {
                continue;
            }
            double crease = sourceCrease < 0.0
                ? -1.0
                : Math.Max(0.0, sourceCrease - before.SubdivisionLevel);
            if (crease == 0.0)
            {
                continue;
            }
            int sourceIndex = sourceIndices[EdgeKey.Create(edge.Start, edge.End)];
            int[] sourceChain = topology.SourceEdgeVertexChains[sourceIndex];
            int[] resultChain = result.SourceEdgeVertexChains[sourceIndex];
            bool forward = edge.Start == sourceChain[0];
            if (forward)
            {
                for (int index = 0; index + 1 < resultChain.Length; index++)
                {
                    refined.Add(new Mesh.Edge(
                        resultChain[index],
                        resultChain[index + 1])
                    {
                        Crease = crease,
                    });
                }
            }
            else
            {
                for (int index = resultChain.Length - 1; index > 0; index--)
                {
                    refined.Add(new Mesh.Edge(
                        resultChain[index],
                        resultChain[index - 1])
                    {
                        Crease = crease,
                    });
                }
            }
        }
        return refined.ToArray();
    }

    private static MeshTopologyState CaptureState(Mesh mesh) => new(
        mesh.Vertices.ToArray(),
        mesh.Faces
            .Select(static face => face?.ToArray() ??
                throw new InvalidOperationException(
                    "A modern MESH face cannot be null."))
            .ToArray(),
        mesh.Edges.ToArray(),
        mesh.TextureCoordinates.ToArray(),
        mesh.SubdivisionLevel,
        mesh.BlendCrease);

    private static void ValidateAttachedState(
        CadDocument document,
        Mesh mesh,
        MeshTopologyState expected)
    {
        ValidateModelSpaceEntity(document, mesh);
        MeshTopologyState actual = CaptureState(mesh);
        if (!StatesEqual(actual, expected))
        {
            throw new InvalidOperationException(
                "A retained modern-MESH state changed outside its edit history.");
        }
    }

    private static bool StatesEqual(
        MeshTopologyState first,
        MeshTopologyState second)
    {
        if (first.SubdivisionLevel != second.SubdivisionLevel ||
            first.BlendCrease != second.BlendCrease ||
            !first.Vertices.AsSpan().SequenceEqual(second.Vertices) ||
            !first.Edges.AsSpan().SequenceEqual(second.Edges) ||
            !first.TextureCoordinates.AsSpan().SequenceEqual(
                second.TextureCoordinates) ||
            first.Faces.Length != second.Faces.Length)
        {
            return false;
        }
        for (int index = 0; index < first.Faces.Length; index++)
        {
            if (!first.Faces[index].AsSpan().SequenceEqual(second.Faces[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static void ApplyState(Mesh mesh, MeshTopologyState state)
    {
        mesh.Vertices.Clear();
        mesh.Vertices.AddRange(state.Vertices);
        mesh.Faces.Clear();
        foreach (int[] face in state.Faces)
        {
            mesh.Faces.Add(face.ToArray());
        }
        mesh.Edges.Clear();
        mesh.Edges.AddRange(state.Edges);
        if (state.TextureCoordinates.Length != 0)
        {
            mesh.TextureCoordinates = state.TextureCoordinates;
        }
        mesh.SubdivisionLevel = state.SubdivisionLevel;
        mesh.BlendCrease = state.BlendCrease;
    }

    private static void ValidateState(MeshTopologyState state)
    {
        if (state.Vertices.Length < 3 || state.Faces.Length == 0)
        {
            throw new InvalidOperationException(
                "A modern MESH requires at least three control vertices and one face.");
        }
        if (state.SubdivisionLevel < 0)
        {
            throw new InvalidOperationException(
                "A modern-MESH subdivision level cannot be negative.");
        }
        foreach (XYZ vertex in state.Vertices)
        {
            if (!IsFinite(vertex))
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
        foreach (XYZ texture in state.TextureCoordinates)
        {
            if (!IsFinite(texture))
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
                    "Every modern-MESH face requires at least three control vertices.");
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
                        "A modern-MESH face references a missing control vertex.");
                }
                distinctVertices.Add(start);
                if (start == end || state.Vertices[start] == state.Vertices[end])
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
                 crease < 0.0 && crease != -1.0 ||
                 !state.BlendCrease && crease >= 0.0 &&
                 crease != Math.Truncate(crease)))
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

    private readonly record struct EdgeKey(int First, int Second)
    {
        public static EdgeKey Create(int first, int second) => first < second
            ? new EdgeKey(first, second)
            : new EdgeKey(second, first);
    }

    private sealed record MeshTopologyState(
        XYZ[] Vertices,
        int[][] Faces,
        Mesh.Edge[] Edges,
        XYZ[] TextureCoordinates,
        int SubdivisionLevel,
        bool BlendCrease);

    private sealed record MeshRefinement(
        Mesh Mesh,
        MeshTopologyState Before,
        MeshTopologyState After);
}
