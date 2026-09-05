using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMesh3DSmoothingEditingTests
{
    [Fact]
    public void SmoothMoreAdjustsEachEligibleMeshAndRetainsExactUndoRedo()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh first = CreateQuadMesh(level: 0);
        Mesh second = CreateQuadMesh(level: 2, offset: 10);
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadAdjustMeshSubdivisionLevelCommand(
            [first.Handle, second.Handle, first.Handle],
            delta: 1,
            maxSubdivisionLevel: 3);

        history.Execute(command);

        Assert.Equal(1, first.SubdivisionLevel);
        Assert.Equal(3, second.SubdivisionLevel);
        Assert.Equal(
            new CadMesh3DSmoothnessSummary(2, 2, 1, 3),
            command.Summary);
        Assert.Equal(1UL, session.ContentGeneration);
        Assert.True(history.TryUndo(out _));
        Assert.Equal(0, first.SubdivisionLevel);
        Assert.Equal(2, second.SubdivisionLevel);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(1, first.SubdivisionLevel);
        Assert.Equal(3, second.SubdivisionLevel);
    }

    [Fact]
    public void SmoothLessFiltersLevelZeroAndRejectsAnAllBoundarySelection()
    {
        var document = new CadDocument();
        Mesh zero = CreateQuadMesh(level: 0);
        Mesh one = CreateQuadMesh(level: 1, offset: 10);
        document.Entities.Add(zero);
        document.Entities.Add(one);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadAdjustMeshSubdivisionLevelCommand(
            [zero.Handle, one.Handle],
            delta: -1);

        history.Execute(command);

        Assert.Equal(0, zero.SubdivisionLevel);
        Assert.Equal(0, one.SubdivisionLevel);
        Assert.Equal(1, command.AffectedMeshCount);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadAdjustMeshSubdivisionLevelCommand(
                [zero.Handle, one.Handle],
                delta: -1)));
        Assert.Equal(1UL, session.ContentGeneration);
    }

    [Fact]
    public void SmoothnessTypeLayerAndTopologyLimitsFailBeforeMutation()
    {
        var document = new CadDocument();
        var lockedLayer = new Layer("LOCKED_MESH_SMOOTH")
        {
            Flags = LayerFlags.Locked,
        };
        document.Layers.Add(lockedLayer);
        Mesh locked = CreateQuadMesh(level: 0);
        locked.Layer = lockedLayer;
        var line = new Line
        {
            StartPoint = XYZ.Zero,
            EndPoint = XYZ.AxisX,
        };
        document.Entities.Add(locked);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadAdjustMeshSubdivisionLevelCommand(
                [locked.Handle],
                delta: 1)));
        locked.Layer = document.Layers[Layer.DefaultName];
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadAdjustMeshSubdivisionLevelCommand(
                [line.Handle],
                delta: 1)));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadAdjustMeshSubdivisionLevelCommand(
                [locked.Handle],
                delta: 1,
                maxTopologyVisits: 10)));

        Assert.Equal(0, locked.SubdivisionLevel);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void FaceCreaseUpdatesEveryBoundaryEdgeAndPreservesOtherCreases()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        mesh.Edges.AddRange([
            new Mesh.Edge(2, 3) { Crease = 2.0 },
            new Mesh.Edge(0, 2) { Crease = 1.0 },
        ]);
        (CadDocumentSession session, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        Mesh.Edge[] before = mesh.Edges.ToArray();
        var history = new CadDocumentHistory(session);
        var command = new CadSetMeshSubobjectCreaseCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)],
            -1.0);

        history.Execute(command);

        Assert.Equal(3, command.AffectedEdgeCount);
        Assert.Equal(1, command.AffectedMeshCount);
        Assert.Collection(
            mesh.Edges,
            edge => AssertEdge(edge, 2, 3, 2.0),
            edge => AssertEdge(edge, 0, 2, -1.0),
            edge => AssertEdge(edge, 0, 1, -1.0),
            edge => AssertEdge(edge, 1, 2, -1.0));
        CadRecordedMesh3DScene rebuilt = CompileScene(session);
        Assert.Equal(1UL, rebuilt.ContentGeneration);
        Assert.True(rebuilt.Statistics.TriangleCount > 2);

        Assert.True(history.TryUndo(out _));
        AssertEdges(before, mesh.Edges);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(4, mesh.Edges.Count);
    }

    [Fact]
    public void VertexAndEdgeUnionCreasesEachIncidentEdgeOnceThenUncreases()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        (CadDocumentSession session, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetMeshSubobjectCreaseCommand(
            scene,
            [
                CreateId(scene, component, CadMesh3DSubobjectKind.Vertex, 0),
                CreateId(scene, component, CadMesh3DSubobjectKind.Edge, 0),
            ],
            2.0));

        Assert.Collection(
            mesh.Edges.OrderBy(static edge => EdgeKey(edge.Start, edge.End)),
            edge => AssertUndirectedEdge(edge, 0, 1, 2.0),
            edge => AssertUndirectedEdge(edge, 0, 2, 2.0),
            edge => AssertUndirectedEdge(edge, 0, 3, 2.0));

        CadRecordedMesh3DScene creasedScene = CompileScene(session);
        CadMesh3DSubobjectComponent creasedComponent = Assert.Single(
            creasedScene.SubobjectComponents.ToArray());
        history.Execute(new CadSetMeshSubobjectCreaseCommand(
            creasedScene,
            [CreateId(
                creasedScene,
                creasedComponent,
                CadMesh3DSubobjectKind.Vertex,
                0)],
            0.0));

        Assert.Empty(mesh.Edges);
    }

    [Fact]
    public void FractionalCreasesRequireBlendAndWorkBoundsAreTransactional()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        (CadDocumentSession session, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);
        CadMesh3DSubobjectId face = CreateId(
            scene,
            component,
            CadMesh3DSubobjectKind.Face,
            0);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetMeshSubobjectCreaseCommand(scene, [face], 0.5)));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetMeshSubobjectCreaseCommand(
                scene,
                [face],
                -1.0,
                maxFaceCorners: 5)));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetMeshSubobjectCreaseCommand(
                scene,
                [face],
                -1.0,
                maxAffectedEdges: 2)));
        Assert.Empty(mesh.Edges);
        Assert.Equal(0UL, session.ContentGeneration);

        mesh.BlendCrease = true;
        history.Execute(new CadSetMeshSubobjectCreaseCommand(
            scene,
            [face],
            0.5));
        Assert.All(mesh.Edges, edge => Assert.Equal(0.5, edge.Crease));
    }

    [Fact]
    public void FractionalCreaseFailureAcrossMeshesMutatesNone()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh compatible = CreateQuadMesh(level: 1);
        compatible.BlendCrease = true;
        Mesh incompatible = CreateQuadMesh(level: 1, offset: 10);
        document.Entities.Add(compatible);
        document.Entities.Add(incompatible);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent[] components =
            scene.SubobjectComponents.ToArray();
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadSetMeshSubobjectCreaseCommand(
                scene,
                [
                    CreateId(
                        scene,
                        components[0],
                        CadMesh3DSubobjectKind.Face,
                        0),
                    CreateId(
                        scene,
                        components[1],
                        CadMesh3DSubobjectKind.Face,
                        0),
                ],
                0.5)));

        Assert.Empty(compatible.Edges);
        Assert.Empty(incompatible.Edges);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void StaleCreaseCommandIsRejectedBeforeMutation()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        (CadDocumentSession session, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);
        var command = new CadSetMeshSubobjectCreaseCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Edge, 0)],
            -1.0);
        session.Edit("Unrelated external edit", _ => { });

        Assert.Throws<CadEditHistoryDivergedException>(
            () => history.Execute(command));

        Assert.Empty(mesh.Edges);
        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredSmoothnessAndCreasesRoundTrip(
        CadDocumentFormat format)
    {
        Mesh mesh = CreateQuadMesh(level: 0);
        (CadDocumentSession session, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAdjustMeshSubdivisionLevelCommand(
            [mesh.Handle],
            delta: 1));
        CadRecordedMesh3DScene smoothedScene = CompileScene(session);
        CadMesh3DSubobjectComponent smoothedComponent = Assert.Single(
            smoothedScene.SubobjectComponents.ToArray());
        history.Execute(new CadSetMeshSubobjectCreaseCommand(
            smoothedScene,
            [CreateId(
                smoothedScene,
                smoothedComponent,
                CadMesh3DSubobjectKind.Face,
                0)],
            -1.0));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"mesh-smoothing.{format.ToString().ToLowerInvariant()}");
        Mesh restored = loaded.Session.Read(document =>
            Assert.IsType<Mesh>(Assert.Single(document.Entities)));

        Assert.Equal(1, restored.SubdivisionLevel);
        Assert.Equal(3, restored.Edges.Count);
        Assert.All(restored.Edges, edge => Assert.Equal(-1.0, edge.Crease));
        Assert.True(CompileScene(loaded.Session).Statistics.TriangleCount > 2);
    }

    private static (CadDocumentSession, CadRecordedMesh3DScene) CreateScene(
        Mesh mesh)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        return (session, CompileScene(session));
    }

    private static Mesh CreateQuadMesh(int level, double offset = 0.0)
    {
        var mesh = new Mesh
        {
            SubdivisionLevel = level,
        };
        mesh.Vertices.AddRange([
            new XYZ(offset + 0, 0, 0),
            new XYZ(offset + 2, 0, 0),
            new XYZ(offset + 2, 2, 0),
            new XYZ(offset + 0, 2, 0),
        ]);
        mesh.Faces.AddRange([[0, 1, 2], [0, 2, 3]]);
        return mesh;
    }

    private static CadRecordedMesh3DScene CompileScene(
        CadDocumentSession session) => new CadMesh3DSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

    private static CadMesh3DSubobjectId CreateId(
        CadRecordedMesh3DScene scene,
        CadMesh3DSubobjectComponent component,
        CadMesh3DSubobjectKind kind,
        int index) => new(
            scene.ContentGeneration,
            component.Handle,
            component.ComponentIndex,
            kind,
            index);

    private static (int, int) EdgeKey(int first, int second) => first < second
        ? (first, second)
        : (second, first);

    private static void AssertEdges(
        IReadOnlyList<Mesh.Edge> expected,
        IReadOnlyList<Mesh.Edge> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertEdge(
                actual[index],
                expected[index].Start,
                expected[index].End,
                expected[index].Crease);
        }
    }

    private static void AssertEdge(
        Mesh.Edge edge,
        int start,
        int end,
        double? crease)
    {
        Assert.Equal(start, edge.Start);
        Assert.Equal(end, edge.End);
        Assert.Equal(crease, edge.Crease);
    }

    private static void AssertUndirectedEdge(
        Mesh.Edge edge,
        int first,
        int second,
        double? crease)
    {
        Assert.Equal(EdgeKey(first, second), EdgeKey(edge.Start, edge.End));
        Assert.Equal(crease, edge.Crease);
    }
}
