namespace ProGPU.CAD;

internal readonly record struct CadMeshSubdivisionEdge(
    int Start,
    int End,
    double? Crease);

internal sealed class CadMeshSubdivisionResult
{
    public required CadPoint3D[] Vertices { get; init; }
    public required CadPoint3D[] TextureCoordinates { get; init; }
    public required int[][] Faces { get; init; }
    public required CadPoint3D[][] FaceCornerNormals { get; init; }
    public required int[] FaceSourceIndices { get; init; }
    public required int[][] FaceCornerSourceEdgeIndices { get; init; }
    public required int[][] SourceEdgeVertexChains { get; init; }
    public required int TopologyVisitCount { get; init; }
}

internal sealed class CadMeshSourceTopology
{
    public required int[] FaceSourceIndices { get; init; }
    public required int[][] FaceCornerSourceEdgeIndices { get; init; }
    public required int[][] SourceEdgeVertexChains { get; init; }
}

/// <summary>
/// Bounded clean-room Catmull-Clark refinement for retained CAD MESH entities.
/// </summary>
/// <remarks>
/// A level visits O(V + E + C) topology and stores O(V + E + C) data for
/// vertices V, unique edges E, and face corners C. Each level emits C quads,
/// so final face-corner storage grows by exactly four per input corner per
/// level. The caller supplies one aggregate visit limit before any result is
/// published. Boundary edges use the standard cubic B-spline boundary mask.
/// Positive crease levels use uniform one-level decay; -1 remains infinitely
/// sharp. When blend-crease is enabled, a fractional final level blends the
/// smooth and sharp masks without changing topology.
/// </remarks>
internal static class CadMeshSubdivision
{
    private const double RelativeTolerance = 1e-12;

    private readonly record struct EdgeKey(int First, int Second)
    {
        public static EdgeKey Create(int first, int second) => first < second
            ? new EdgeKey(first, second)
            : new EdgeKey(second, first);
    }

    private sealed class EdgeTopology
    {
        public required EdgeKey Key { get; init; }
        public required int DirectedStart { get; init; }
        public required int DirectedEnd { get; init; }
        public required int FirstFace { get; init; }
        public required int FirstStartCorner { get; init; }
        public int SecondFace { get; set; } = -1;
        public int SecondStartCorner { get; set; } = -1;
    }

    private sealed class Topology
    {
        public required Dictionary<EdgeKey, EdgeTopology> Edges { get; init; }
        public required List<EdgeTopology> OrderedEdges { get; init; }
        public required List<int>[] VertexFaces { get; init; }
        public required List<EdgeTopology>[] VertexEdges { get; init; }
    }

    public static CadMeshSubdivisionResult Refine(
        ReadOnlySpan<CadPoint3D> sourceVertices,
        ReadOnlySpan<CadPoint3D> sourceTextureCoordinates,
        IReadOnlyList<int[]> sourceFaces,
        ReadOnlySpan<CadMeshSubdivisionEdge> sourceEdges,
        int subdivisionLevel,
        bool blendCrease,
        int maxTopologyVisits,
        CancellationToken cancellationToken)
    {
        if (sourceVertices.Length < 3)
        {
            throw new ArgumentException("A subdivided MESH requires at least three vertices.");
        }
        if (sourceTextureCoordinates.Length != 0 &&
            sourceTextureCoordinates.Length != sourceVertices.Length)
        {
            throw new ArgumentException(
                "A subdivided MESH texture-coordinate count must match its vertex count.");
        }
        if (subdivisionLevel <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subdivisionLevel),
                "Subdivision level must be positive.");
        }
        if (maxTopologyVisits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTopologyVisits));
        }

        CadPoint3D[] vertices = sourceVertices.ToArray();
        CadPoint3D[] textureCoordinates = sourceTextureCoordinates.ToArray();
        int[][] faces = CloneFaces(sourceFaces);
        CadMeshSourceTopology sourceTopology = CreateSourceTopology(faces);
        int[] faceSourceIndices = sourceTopology.FaceSourceIndices;
        int[][] sourceEdgeVertexChains = sourceTopology.SourceEdgeVertexChains;
        Dictionary<EdgeKey, int> displayedSourceEdges = CreateDisplayedSourceEdges(
            sourceEdgeVertexChains);
        int visits = CountFaceCorners(faces, maxTopologyVisits);
        Topology topology = BuildTopology(vertices, faces, cancellationToken);
        Dictionary<EdgeKey, double> creases = ReadCreases(
            sourceEdges,
            topology,
            blendCrease,
            cancellationToken);
        HashSet<EdgeKey> displayedSharpEdges = [];

        for (int level = 0; level < subdivisionLevel; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefineOneLevel(
                vertices,
                textureCoordinates,
                faces,
                topology,
                creases,
                faceSourceIndices,
                displayedSourceEdges,
                sourceEdgeVertexChains,
                blendCrease,
                maxTopologyVisits,
                ref visits,
                cancellationToken,
                out vertices,
                out textureCoordinates,
                out faces,
                out creases,
                out displayedSharpEdges,
                out faceSourceIndices,
                out displayedSourceEdges,
                out sourceEdgeVertexChains);
            topology = BuildTopology(vertices, faces, cancellationToken);
        }

        CadPoint3D[][] normals = ComputeCornerNormals(
            vertices,
            faces,
            topology,
            displayedSharpEdges,
            cancellationToken);
        return new CadMeshSubdivisionResult
        {
            Vertices = vertices,
            TextureCoordinates = textureCoordinates,
            Faces = faces,
            FaceCornerNormals = normals,
            FaceSourceIndices = faceSourceIndices,
            FaceCornerSourceEdgeIndices = CreateFaceCornerSourceEdgeIndices(
                faces,
                displayedSourceEdges),
            SourceEdgeVertexChains = sourceEdgeVertexChains,
            TopologyVisitCount = visits,
        };
    }

    internal static CadMeshSourceTopology CreateSourceTopology(
        IReadOnlyList<int[]> sourceFaces)
    {
        ArgumentNullException.ThrowIfNull(sourceFaces);
        var sourceEdgeIndices = new Dictionary<EdgeKey, int>();
        var sourceEdgeVertexChains = new List<int[]>();
        var faceSourceIndices = new int[sourceFaces.Count];
        var faceCornerSourceEdgeIndices = new int[sourceFaces.Count][];
        for (int faceIndex = 0; faceIndex < sourceFaces.Count; faceIndex++)
        {
            int[] face = sourceFaces[faceIndex] ?? throw new ArgumentException(
                "A MESH source face cannot be null.",
                nameof(sourceFaces));
            if (face.Length < 3)
            {
                throw new ArgumentException(
                    "A MESH source face requires at least three vertices.",
                    nameof(sourceFaces));
            }
            faceSourceIndices[faceIndex] = faceIndex;
            var cornerEdges = new int[face.Length];
            for (int corner = 0; corner < face.Length; corner++)
            {
                int start = face[corner];
                int end = face[(corner + 1) % face.Length];
                EdgeKey key = EdgeKey.Create(start, end);
                if (!sourceEdgeIndices.TryGetValue(key, out int edgeIndex))
                {
                    edgeIndex = sourceEdgeVertexChains.Count;
                    sourceEdgeIndices.Add(key, edgeIndex);
                    sourceEdgeVertexChains.Add([start, end]);
                }
                cornerEdges[corner] = edgeIndex;
            }
            faceCornerSourceEdgeIndices[faceIndex] = cornerEdges;
        }
        return new CadMeshSourceTopology
        {
            FaceSourceIndices = faceSourceIndices,
            FaceCornerSourceEdgeIndices = faceCornerSourceEdgeIndices,
            SourceEdgeVertexChains = sourceEdgeVertexChains.ToArray(),
        };
    }

    private static void RefineOneLevel(
        CadPoint3D[] vertices,
        CadPoint3D[] textureCoordinates,
        int[][] faces,
        Topology topology,
        Dictionary<EdgeKey, double> creases,
        int[] faceSourceIndices,
        Dictionary<EdgeKey, int> displayedSourceEdges,
        int[][] sourceEdgeVertexChains,
        bool blendCrease,
        int maxTopologyVisits,
        ref int visits,
        CancellationToken cancellationToken,
        out CadPoint3D[] refinedVertices,
        out CadPoint3D[] refinedTextureCoordinates,
        out int[][] refinedFaces,
        out Dictionary<EdgeKey, double> refinedCreases,
        out HashSet<EdgeKey> displayedSharpEdges,
        out int[] refinedFaceSourceIndices,
        out Dictionary<EdgeKey, int> refinedDisplayedSourceEdges,
        out int[][] refinedSourceEdgeVertexChains)
    {
        int faceCornerCount = CountFaceCorners(faces, int.MaxValue);
        int remainingVisits = maxTopologyVisits - visits;
        if (faceCornerCount > remainingVisits / 4)
        {
            throw new CadUnsupportedEntityException(
                $"Subdivided MESH topology exceeds the configured {maxTopologyVisits}-corner refinement limit.");
        }
        int refinedCornerCount = faceCornerCount * 4;
        visits += refinedCornerCount;

        var facePoints = new CadPoint3D[faces.Length];
        var faceTexturePoints = textureCoordinates.Length == 0
            ? Array.Empty<CadPoint3D>()
            : new CadPoint3D[faces.Length];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            if ((faceIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int[] face = faces[faceIndex];
            CadPoint3D position = CadPoint3D.Zero;
            CadPoint3D texture = CadPoint3D.Zero;
            for (int corner = 0; corner < face.Length; corner++)
            {
                position += vertices[face[corner]];
                if (textureCoordinates.Length != 0)
                {
                    texture += textureCoordinates[face[corner]];
                }
            }
            facePoints[faceIndex] = position / face.Length;
            if (textureCoordinates.Length != 0)
            {
                faceTexturePoints[faceIndex] = texture / face.Length;
            }
        }

        List<EdgeTopology> orderedEdges = topology.OrderedEdges;
        var edgePointIndices = new Dictionary<EdgeKey, int>(orderedEdges.Count);
        int edgePointOffset = vertices.Length;
        int facePointOffset = checked(edgePointOffset + orderedEdges.Count);
        refinedVertices = new CadPoint3D[checked(facePointOffset + faces.Length)];
        refinedTextureCoordinates = textureCoordinates.Length == 0
            ? Array.Empty<CadPoint3D>()
            : new CadPoint3D[refinedVertices.Length];

        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            if ((vertexIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            refinedVertices[vertexIndex] = ComputeVertexPoint(
                vertexIndex,
                vertices,
                facePoints,
                topology,
                creases,
                blendCrease);
            if (textureCoordinates.Length != 0)
            {
                // Persisted MESH UVs are one value per control vertex. Linear
                // topological refinement preserves that authored mapping and
                // does not invent unavailable face-varying seams.
                refinedTextureCoordinates[vertexIndex] = textureCoordinates[vertexIndex];
            }
        }

        refinedCreases = new Dictionary<EdgeKey, double>();
        displayedSharpEdges = [];
        refinedDisplayedSourceEdges = new Dictionary<EdgeKey, int>();
        for (int edgeIndex = 0; edgeIndex < orderedEdges.Count; edgeIndex++)
        {
            if ((edgeIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            EdgeTopology edge = orderedEdges[edgeIndex];
            int outputIndex = checked(edgePointOffset + edgeIndex);
            edgePointIndices.Add(edge.Key, outputIndex);
            double blend = GetEdgeSharpnessBlend(edge, creases, blendCrease);
            CadPoint3D sharp =
                (vertices[edge.Key.First] + vertices[edge.Key.Second]) / 2.0;
            CadPoint3D smooth = edge.SecondFace < 0
                ? sharp
                : (vertices[edge.Key.First] +
                    vertices[edge.Key.Second] +
                    facePoints[edge.FirstFace] +
                    facePoints[edge.SecondFace]) / 4.0;
            refinedVertices[outputIndex] = Lerp(smooth, sharp, blend);
            if (textureCoordinates.Length != 0)
            {
                refinedTextureCoordinates[outputIndex] =
                    (textureCoordinates[edge.Key.First] +
                    textureCoordinates[edge.Key.Second]) / 2.0;
            }

            double currentCrease = creases.GetValueOrDefault(edge.Key);
            double nextCrease = edge.SecondFace < 0
                ? -1.0
                : DecayCrease(currentCrease);
            EdgeKey firstChild = EdgeKey.Create(edge.Key.First, outputIndex);
            EdgeKey secondChild = EdgeKey.Create(outputIndex, edge.Key.Second);
            if (displayedSourceEdges.TryGetValue(edge.Key, out int sourceEdgeIndex))
            {
                refinedDisplayedSourceEdges.Add(firstChild, sourceEdgeIndex);
                refinedDisplayedSourceEdges.Add(secondChild, sourceEdgeIndex);
            }
            if (nextCrease != 0.0)
            {
                refinedCreases.Add(firstChild, nextCrease);
                refinedCreases.Add(secondChild, nextCrease);
            }
            if (edge.SecondFace < 0 || blend >= 1.0)
            {
                displayedSharpEdges.Add(firstChild);
                displayedSharpEdges.Add(secondChild);
            }
        }

        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            refinedVertices[facePointOffset + faceIndex] = facePoints[faceIndex];
            if (textureCoordinates.Length != 0)
            {
                refinedTextureCoordinates[facePointOffset + faceIndex] =
                    faceTexturePoints[faceIndex];
            }
        }

        refinedFaces = new int[faceCornerCount][];
        refinedFaceSourceIndices = new int[faceCornerCount];
        int refinedFaceIndex = 0;
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            int[] face = faces[faceIndex];
            for (int corner = 0; corner < face.Length; corner++)
            {
                int current = face[corner];
                int next = face[(corner + 1) % face.Length];
                int previous = face[(corner + face.Length - 1) % face.Length];
                refinedFaceSourceIndices[refinedFaceIndex] =
                    faceSourceIndices[faceIndex];
                refinedFaces[refinedFaceIndex++] =
                [
                    current,
                    edgePointIndices[EdgeKey.Create(current, next)],
                    facePointOffset + faceIndex,
                    edgePointIndices[EdgeKey.Create(previous, current)],
                ];
            }
        }

        refinedSourceEdgeVertexChains = new int[sourceEdgeVertexChains.Length][];
        for (int edgeIndex = 0; edgeIndex < sourceEdgeVertexChains.Length; edgeIndex++)
        {
            int[] sourceChain = sourceEdgeVertexChains[edgeIndex];
            var refinedChain = new int[checked(sourceChain.Length * 2 - 1)];
            refinedChain[0] = sourceChain[0];
            for (int point = 0; point + 1 < sourceChain.Length; point++)
            {
                EdgeKey segment = EdgeKey.Create(
                    sourceChain[point],
                    sourceChain[point + 1]);
                if (!edgePointIndices.TryGetValue(segment, out int edgePoint))
                {
                    throw new InvalidOperationException(
                        "A retained MESH source-edge chain is not present in the refinement topology.");
                }
                refinedChain[point * 2 + 1] = edgePoint;
                refinedChain[point * 2 + 2] = sourceChain[point + 1];
            }
            refinedSourceEdgeVertexChains[edgeIndex] = refinedChain;
        }
    }

    private static Dictionary<EdgeKey, int> CreateDisplayedSourceEdges(
        int[][] sourceEdgeVertexChains)
    {
        var result = new Dictionary<EdgeKey, int>();
        for (int edgeIndex = 0; edgeIndex < sourceEdgeVertexChains.Length; edgeIndex++)
        {
            int[] chain = sourceEdgeVertexChains[edgeIndex];
            for (int point = 0; point + 1 < chain.Length; point++)
            {
                result.Add(EdgeKey.Create(chain[point], chain[point + 1]), edgeIndex);
            }
        }
        return result;
    }

    private static int[][] CreateFaceCornerSourceEdgeIndices(
        int[][] faces,
        Dictionary<EdgeKey, int> displayedSourceEdges)
    {
        var result = new int[faces.Length][];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            int[] face = faces[faceIndex];
            var cornerEdges = new int[face.Length];
            for (int corner = 0; corner < face.Length; corner++)
            {
                cornerEdges[corner] = displayedSourceEdges.TryGetValue(
                    EdgeKey.Create(face[corner], face[(corner + 1) % face.Length]),
                    out int sourceEdgeIndex)
                    ? sourceEdgeIndex
                    : -1;
            }
            result[faceIndex] = cornerEdges;
        }
        return result;
    }

    private static CadPoint3D ComputeVertexPoint(
        int vertexIndex,
        CadPoint3D[] vertices,
        CadPoint3D[] facePoints,
        Topology topology,
        Dictionary<EdgeKey, double> creases,
        bool blendCrease)
    {
        List<EdgeTopology> incidentEdges = topology.VertexEdges[vertexIndex];
        List<int> incidentFaces = topology.VertexFaces[vertexIndex];
        if (incidentEdges.Count == 0 || incidentFaces.Count == 0)
        {
            throw new ArgumentException(
                "A subdivided MESH contains an isolated control vertex.");
        }

        CadPoint3D vertex = vertices[vertexIndex];
        CadPoint3D faceAverage = CadPoint3D.Zero;
        for (int i = 0; i < incidentFaces.Count; i++)
        {
            faceAverage += facePoints[incidentFaces[i]];
        }
        faceAverage /= incidentFaces.Count;

        CadPoint3D edgeMidpointAverage = CadPoint3D.Zero;
        int sharpCount = 0;
        double firstSharpWeight = 0.0;
        double secondSharpWeight = 0.0;
        double thirdSharpWeight = 0.0;
        int firstSharpNeighbor = -1;
        int secondSharpNeighbor = -1;
        int boundaryCount = 0;
        for (int i = 0; i < incidentEdges.Count; i++)
        {
            EdgeTopology edge = incidentEdges[i];
            int neighbor = edge.Key.First == vertexIndex
                ? edge.Key.Second
                : edge.Key.First;
            edgeMidpointAverage += (vertex + vertices[neighbor]) / 2.0;
            double weight = GetEdgeSharpnessBlend(edge, creases, blendCrease);
            if (edge.SecondFace < 0)
            {
                boundaryCount++;
            }
            if (weight > 0.0)
            {
                sharpCount++;
                if (weight > firstSharpWeight)
                {
                    thirdSharpWeight = secondSharpWeight;
                    secondSharpWeight = firstSharpWeight;
                    secondSharpNeighbor = firstSharpNeighbor;
                    firstSharpWeight = weight;
                    firstSharpNeighbor = neighbor;
                }
                else if (weight > secondSharpWeight)
                {
                    thirdSharpWeight = secondSharpWeight;
                    secondSharpWeight = weight;
                    secondSharpNeighbor = neighbor;
                }
                else if (weight > thirdSharpWeight)
                {
                    thirdSharpWeight = weight;
                }
            }
        }
        edgeMidpointAverage /= incidentEdges.Count;

        if (boundaryCount is not (0 or 2))
        {
            throw new CadUnsupportedEntityException(
                "A subdivided MESH vertex must have zero or two boundary edges.");
        }
        if (boundaryCount == 0 && incidentFaces.Count != incidentEdges.Count)
        {
            throw new CadUnsupportedEntityException(
                "A subdivided MESH vertex has a disconnected or non-manifold closed fan.");
        }
        if (boundaryCount == 2 && incidentFaces.Count != incidentEdges.Count - 1)
        {
            throw new CadUnsupportedEntityException(
                "A subdivided MESH vertex has a disconnected or non-manifold boundary fan.");
        }

        int valence = incidentEdges.Count;
        CadPoint3D smooth =
            (faceAverage + (edgeMidpointAverage * 2.0) +
                (vertex * (valence - 3.0))) / valence;
        if (sharpCount < 2)
        {
            return smooth;
        }

        if (sharpCount >= 3)
        {
            return Lerp(smooth, vertex, thirdSharpWeight);
        }

        CadPoint3D crease =
            ((vertex * 6.0) +
                vertices[firstSharpNeighbor] +
                vertices[secondSharpNeighbor]) / 8.0;
        return Lerp(smooth, crease, secondSharpWeight);
    }

    private static double GetEdgeSharpnessBlend(
        EdgeTopology edge,
        Dictionary<EdgeKey, double> creases,
        bool blendCrease)
    {
        if (edge.SecondFace < 0)
        {
            return 1.0;
        }
        double crease = creases.GetValueOrDefault(edge.Key);
        if (crease < 0.0)
        {
            return 1.0;
        }
        if (!(crease > 0.0))
        {
            return 0.0;
        }
        return blendCrease ? Math.Min(crease, 1.0) : 1.0;
    }

    private static double DecayCrease(double crease) => crease < 0.0
        ? -1.0
        : Math.Max(0.0, crease - 1.0);

    private static Dictionary<EdgeKey, double> ReadCreases(
        ReadOnlySpan<CadMeshSubdivisionEdge> sourceEdges,
        Topology topology,
        bool blendCrease,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<EdgeKey, double>();
        var seen = new HashSet<EdgeKey>();
        for (int i = 0; i < sourceEdges.Length; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            CadMeshSubdivisionEdge edge = sourceEdges[i];
            if (edge.Start == edge.End ||
                edge.Start < 0 || edge.End < 0 ||
                edge.Start >= topology.VertexEdges.Length ||
                edge.End >= topology.VertexEdges.Length)
            {
                throw new ArgumentException(
                    "A subdivided MESH crease edge has invalid vertex indices.");
            }
            EdgeKey key = EdgeKey.Create(edge.Start, edge.End);
            if (!seen.Add(key))
            {
                throw new ArgumentException(
                    "A subdivided MESH edge table contains a duplicate edge.");
            }
            if (!topology.Edges.ContainsKey(key))
            {
                throw new ArgumentException(
                    "A subdivided MESH crease does not reference a persisted face edge.");
            }
            if (!edge.Crease.HasValue)
            {
                continue;
            }
            double crease = edge.Crease.Value;
            if (!double.IsFinite(crease) || (crease < 0.0 && crease != -1.0) ||
                (!blendCrease && crease >= 0.0 && crease != Math.Truncate(crease)))
            {
                throw new ArgumentException(
                    "A MESH crease must be -1, zero, or a finite positive level; fractional levels require Blend Crease.");
            }
            if (crease != 0.0)
            {
                result.Add(key, crease);
            }
        }
        return result;
    }

    private static Topology BuildTopology(
        CadPoint3D[] vertices,
        int[][] faces,
        CancellationToken cancellationToken)
    {
        var edges = new Dictionary<EdgeKey, EdgeTopology>();
        var orderedEdges = new List<EdgeTopology>();
        var vertexFaces = new List<int>[vertices.Length];
        var vertexEdges = new List<EdgeTopology>[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            vertexFaces[i] = [];
            vertexEdges[i] = [];
        }

        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            if ((faceIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int[] face = faces[faceIndex];
            if (face is null || face.Length < 3)
            {
                throw new ArgumentException(
                    "Every subdivided MESH face must contain at least three vertex indices.");
            }
            var distinct = new HashSet<int>();
            for (int corner = 0; corner < face.Length; corner++)
            {
                int start = face[corner];
                int end = face[(corner + 1) % face.Length];
                if ((uint)start >= (uint)vertices.Length ||
                    (uint)end >= (uint)vertices.Length)
                {
                    throw new ArgumentException(
                        "A subdivided MESH face references a vertex outside the control array.");
                }
                if (!distinct.Add(start))
                {
                    throw new ArgumentException(
                        "A subdivided MESH face repeats a control vertex.");
                }
                if (start == end || vertices[start] == vertices[end])
                {
                    throw new ArgumentException(
                        "A subdivided MESH face contains a collapsed edge.");
                }
                vertexFaces[start].Add(faceIndex);
                EdgeKey key = EdgeKey.Create(start, end);
                if (!edges.TryGetValue(key, out EdgeTopology? edge))
                {
                    var created = new EdgeTopology
                    {
                        Key = key,
                        DirectedStart = start,
                        DirectedEnd = end,
                        FirstFace = faceIndex,
                        FirstStartCorner = corner,
                    };
                    edges.Add(key, created);
                    orderedEdges.Add(created);
                    continue;
                }
                if (edge.SecondFace >= 0)
                {
                    throw new CadUnsupportedEntityException(
                        "A subdivided MESH contains a non-manifold edge shared by more than two faces.");
                }
                if (edge.DirectedStart == start && edge.DirectedEnd == end)
                {
                    throw new CadUnsupportedEntityException(
                        "Adjacent subdivided MESH faces must use opposite shared-edge winding.");
                }
                edge.SecondFace = faceIndex;
                edge.SecondStartCorner = corner;
            }
        }

        foreach (EdgeTopology edge in orderedEdges)
        {
            vertexEdges[edge.Key.First].Add(edge);
            vertexEdges[edge.Key.Second].Add(edge);
        }
        ValidateConnectedVertexFans(
            faces,
            orderedEdges,
            vertexEdges,
            cancellationToken);
        return new Topology
        {
            Edges = edges,
            OrderedEdges = orderedEdges,
            VertexFaces = vertexFaces,
            VertexEdges = vertexEdges,
        };
    }

    private static void ValidateConnectedVertexFans(
        int[][] faces,
        IEnumerable<EdgeTopology> edges,
        List<EdgeTopology>[] vertexEdges,
        CancellationToken cancellationToken)
    {
        var offsets = new int[faces.Length + 1];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            offsets[faceIndex + 1] = checked(offsets[faceIndex] + faces[faceIndex].Length);
        }
        var parent = new int[offsets[^1]];
        var rank = new byte[parent.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int edgeIndex = 0;
        foreach (EdgeTopology edge in edges)
        {
            if ((edgeIndex++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (edge.SecondFace < 0)
            {
                continue;
            }
            Union(
                offsets[edge.FirstFace] + edge.FirstStartCorner,
                offsets[edge.SecondFace] +
                    ((edge.SecondStartCorner + 1) % faces[edge.SecondFace].Length));
            Union(
                offsets[edge.FirstFace] +
                    ((edge.FirstStartCorner + 1) % faces[edge.FirstFace].Length),
                offsets[edge.SecondFace] + edge.SecondStartCorner);
        }

        var firstCorner = new int[vertexEdges.Length];
        Array.Fill(firstCorner, -1);
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            if ((faceIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int[] face = faces[faceIndex];
            for (int corner = 0; corner < face.Length; corner++)
            {
                int vertex = face[corner];
                int index = offsets[faceIndex] + corner;
                if (firstCorner[vertex] < 0)
                {
                    firstCorner[vertex] = index;
                }
                else if (Find(firstCorner[vertex]) != Find(index))
                {
                    throw new CadUnsupportedEntityException(
                        "A subdivided MESH vertex has a disconnected face fan.");
                }
            }
        }

        for (int vertex = 0; vertex < vertexEdges.Length; vertex++)
        {
            if ((vertex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (firstCorner[vertex] < 0)
            {
                throw new ArgumentException(
                    "A subdivided MESH contains an isolated control vertex.");
            }
            int boundaryCount = 0;
            foreach (EdgeTopology edge in vertexEdges[vertex])
            {
                if (edge.SecondFace < 0) boundaryCount++;
            }
            if (boundaryCount is not (0 or 2))
            {
                throw new CadUnsupportedEntityException(
                    "A subdivided MESH vertex must have zero or two boundary edges.");
            }
        }

        int Find(int value)
        {
            int root = value;
            while (parent[root] != root) root = parent[root];
            while (parent[value] != value)
            {
                int next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot) return;
            if (rank[firstRoot] < rank[secondRoot])
            {
                parent[firstRoot] = secondRoot;
            }
            else
            {
                parent[secondRoot] = firstRoot;
                if (rank[firstRoot] == rank[secondRoot]) rank[firstRoot]++;
            }
        }
    }

    private static CadPoint3D[][] ComputeCornerNormals(
        CadPoint3D[] vertices,
        int[][] faces,
        Topology topology,
        HashSet<EdgeKey> sharpEdges,
        CancellationToken cancellationToken)
    {
        var offsets = new int[faces.Length + 1];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            offsets[faceIndex + 1] = checked(offsets[faceIndex] + faces[faceIndex].Length);
        }
        var parent = new int[offsets[^1]];
        var rank = new byte[parent.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int edgeIndex = 0;
        foreach (EdgeTopology edge in topology.OrderedEdges)
        {
            if ((edgeIndex++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (edge.SecondFace < 0 || sharpEdges.Contains(edge.Key))
            {
                continue;
            }
            Union(
                offsets[edge.FirstFace] + edge.FirstStartCorner,
                offsets[edge.SecondFace] +
                    ((edge.SecondStartCorner + 1) % faces[edge.SecondFace].Length));
            Union(
                offsets[edge.FirstFace] +
                    ((edge.FirstStartCorner + 1) % faces[edge.FirstFace].Length),
                offsets[edge.SecondFace] + edge.SecondStartCorner);
        }

        var sums = new CadPoint3D[parent.Length];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            if ((faceIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int[] face = faces[faceIndex];
            CadPoint3D area = ComputeAreaVector(vertices, face);
            double scale = 0.0;
            CadPoint3D origin = vertices[face[0]];
            for (int corner = 1; corner < face.Length; corner++)
            {
                scale = Math.Max(scale, (vertices[face[corner]] - origin).Length);
            }
            if (!double.IsFinite(area.Length) ||
                area.Length <= Math.Max(1.0, scale * scale) * RelativeTolerance)
            {
                throw new ArgumentException(
                    "A refined MESH face is geometrically degenerate.");
            }
            for (int corner = 0; corner < face.Length; corner++)
            {
                int root = Find(offsets[faceIndex] + corner);
                sums[root] += area;
            }
        }

        var result = new CadPoint3D[faces.Length][];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            int[] face = faces[faceIndex];
            var normals = new CadPoint3D[face.Length];
            for (int corner = 0; corner < face.Length; corner++)
            {
                CadPoint3D sum = sums[Find(offsets[faceIndex] + corner)];
                normals[corner] = sum.Normalize();
            }
            result[faceIndex] = normals;
        }
        return result;

        int Find(int value)
        {
            int root = value;
            while (parent[root] != root) root = parent[root];
            while (parent[value] != value)
            {
                int next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot) return;
            if (rank[firstRoot] < rank[secondRoot])
            {
                parent[firstRoot] = secondRoot;
            }
            else
            {
                parent[secondRoot] = firstRoot;
                if (rank[firstRoot] == rank[secondRoot]) rank[firstRoot]++;
            }
        }
    }

    private static CadPoint3D ComputeAreaVector(
        CadPoint3D[] vertices,
        int[] face)
    {
        CadPoint3D origin = vertices[face[0]];
        CadPoint3D sum = CadPoint3D.Zero;
        for (int corner = 1; corner < face.Length - 1; corner++)
        {
            sum += CadPoint3D.Cross(
                vertices[face[corner]] - origin,
                vertices[face[corner + 1]] - origin);
        }
        return sum;
    }

    private static int[][] CloneFaces(IReadOnlyList<int[]> sourceFaces)
    {
        ArgumentNullException.ThrowIfNull(sourceFaces);
        if (sourceFaces.Count == 0)
        {
            throw new ArgumentException("A subdivided MESH requires at least one face.");
        }
        var result = new int[sourceFaces.Count][];
        for (int i = 0; i < sourceFaces.Count; i++)
        {
            result[i] = sourceFaces[i]?.ToArray() ??
                throw new ArgumentException("A subdivided MESH face cannot be null.");
        }
        return result;
    }

    private static int CountFaceCorners(int[][] faces, int limit)
    {
        int count = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            int length = faces[i]?.Length ?? 0;
            if (length > limit - count)
            {
                throw new CadUnsupportedEntityException(
                    $"Subdivided MESH topology exceeds the configured {limit}-corner refinement limit.");
            }
            count += length;
        }
        return count;
    }

    private static CadPoint3D Lerp(
        CadPoint3D start,
        CadPoint3D end,
        double amount) =>
        start + ((end - start) * amount);
}
