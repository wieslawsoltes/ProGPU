using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMesh3DDeletionTests
{
    [Fact]
    public void FaceDeletionCompactsNewlyIsolatedVertexUvAndCreaseIndices()
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateScene(CreateQuadMesh(withTextureCoordinates: true));
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        XYZ[] originalVertices = mesh.Vertices.ToArray();
        int[][] originalFaces = CloneFaces(mesh.Faces);
        Mesh.Edge[] originalEdges = mesh.Edges.ToArray();
        XYZ[] originalTextureCoordinates = mesh.TextureCoordinates.ToArray();
        var history = new CadDocumentHistory(session);
        var command = new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)]);

        history.Execute(command);

        Assert.Equal(1, command.DeletedFaceCount);
        Assert.Equal(0, command.RemovedMeshEntityCount);
        Assert.Equal([originalVertices[0], originalVertices[2], originalVertices[3]],
            mesh.Vertices);
        Assert.Equal([0, 1, 2], Assert.Single(mesh.Faces));
        Assert.Equal(
            [
                originalTextureCoordinates[0],
                originalTextureCoordinates[2],
                originalTextureCoordinates[3],
            ],
            mesh.TextureCoordinates.ToArray());
        Assert.Collection(
            mesh.Edges,
            edge => AssertEdge(edge, 0, 1, 2.0),
            edge => AssertEdge(edge, 0, 2, 3.0));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(originalVertices, mesh.Vertices);
        AssertFaces(originalFaces, mesh.Faces);
        AssertEdges(originalEdges, mesh.Edges);
        Assert.Equal(originalTextureCoordinates, mesh.TextureCoordinates.ToArray());
        Assert.True(history.TryRedo(out _));
        Assert.Equal(3, mesh.Vertices.Count);
        Assert.Single(mesh.Faces);
    }

    [Fact]
    public void SharedEdgeDeletionRemovesBothFacesAndRetainsEntityForUndoRedo()
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateScene(CreateQuadMesh());
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        ulong originalHandle = mesh.Handle;
        var history = new CadDocumentHistory(session);
        var command = new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Edge, 2)]);

        history.Execute(command);

        Assert.Equal(2, command.DeletedFaceCount);
        Assert.Equal(1, command.RemovedMeshEntityCount);
        Assert.Null(mesh.Owner);
        Assert.Equal(0UL, mesh.Handle);
        Assert.Empty(session.Read(document => document.Entities.ToArray()));

        Assert.True(history.TryUndo(out _));
        Mesh restored = session.Read(document =>
            Assert.IsType<Mesh>(Assert.Single(document.Entities)));
        Assert.Same(mesh, restored);
        Assert.NotEqual(0UL, mesh.Handle);
        Assert.NotEqual(originalHandle, mesh.Handle);
        Assert.Equal(2, mesh.Faces.Count);

        Assert.True(history.TryRedo(out _));
        Assert.Null(mesh.Owner);
        Assert.Equal(0UL, mesh.Handle);
    }

    [Fact]
    public void VertexDeletionRemovesEveryIncidentFaceButKeepsDisconnectedFaces()
    {
        var mesh = new Mesh();
        mesh.Vertices.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(1, 0, 0),
            new XYZ(0, 1, 0),
            new XYZ(10, 0, 0),
            new XYZ(11, 0, 0),
            new XYZ(10, 1, 0),
        ]);
        mesh.Faces.AddRange([[0, 1, 2], [3, 4, 5]]);
        (CadDocumentSession session, _, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);

        history.Execute(new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Vertex, 0)]));

        Assert.Equal(
            [
                new XYZ(10, 0, 0),
                new XYZ(11, 0, 0),
                new XYZ(10, 1, 0),
            ],
            mesh.Vertices);
        Assert.Equal([0, 1, 2], Assert.Single(mesh.Faces));
        CadRecordedMesh3DScene rebuilt = CompileScene(session);
        CadMesh3DSubobjectComponent rebuiltComponent = Assert.Single(
            rebuilt.SubobjectComponents.ToArray());
        Assert.Equal(3, rebuiltComponent.VertexPositions.Length);
        Assert.Single(rebuiltComponent.Faces.ToArray());
    }

    [Fact]
    public void FaceDeletionPreservesUnrelatedPreexistingIsolatedVertices()
    {
        Mesh mesh = CreateQuadMesh();
        XYZ isolated = new(50, 60, 70);
        mesh.Vertices.Add(isolated);
        (CadDocumentSession session, _, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);

        history.Execute(new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)]));

        Assert.Equal(
            [new XYZ(0, 0, 0), new XYZ(2, 2, 0), new XYZ(0, 2, 0), isolated],
            mesh.Vertices);
        Assert.Equal([0, 1, 2], Assert.Single(mesh.Faces));
    }

    [Fact]
    public void StaleSceneDeletionIsRejectedBeforeMutationOrGenerationAdvance()
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateScene(CreateQuadMesh());
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        int[][] originalFaces = CloneFaces(mesh.Faces);
        var history = new CadDocumentHistory(session);
        var command = new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)]);
        session.Edit("Unrelated external edit", _ => { });

        CadEditHistoryDivergedException exception =
            Assert.Throws<CadEditHistoryDivergedException>(
                () => history.Execute(command));

        Assert.Equal(0UL, exception.ExpectedGeneration);
        Assert.Equal(1UL, exception.ActualGeneration);
        AssertFaces(originalFaces, mesh.Faces);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void CancelledCompleteMeshRemovalLeavesEveryMeshUnchanged()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh complete = CreateTriangleMesh(0);
        Mesh partial = CreateQuadMesh();
        for (int index = 0; index < partial.Vertices.Count; index++)
        {
            partial.Vertices[index] += new XYZ(10, 0, 0);
        }
        document.Entities.Add(complete);
        document.Entities.Add(partial);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent[] components =
            scene.SubobjectComponents.ToArray();
        CadMesh3DSubobjectComponent completeComponent = components.Single(
            component => component.SourceHandle == complete.Handle);
        CadMesh3DSubobjectComponent partialComponent = components.Single(
            component => component.SourceHandle == partial.Handle);
        int[][] originalPartialFaces = CloneFaces(partial.Faces);
        EventHandler<CollectionChangedEventArgs> cancel = (_, args) =>
            args.Cancel = true;
        document.Entities.OnBeforeRemove += cancel;
        try
        {
            var history = new CadDocumentHistory(session);

            Assert.Throws<InvalidOperationException>(() => history.Execute(
                new CadDeleteMeshSubobjectsCommand(
                    scene,
                    [
                        CreateId(
                            scene,
                            completeComponent,
                            CadMesh3DSubobjectKind.Face,
                            0),
                        CreateId(
                            scene,
                            partialComponent,
                            CadMesh3DSubobjectKind.Face,
                            0),
                    ])));

            Assert.Same(document.ModelSpace, complete.Owner);
            Assert.Equal(2, partial.Faces.Count);
            AssertFaces(originalPartialFaces, partial.Faces);
            Assert.Equal(0UL, session.ContentGeneration);
            Assert.Equal(0, history.UndoCount);
        }
        finally
        {
            document.Entities.OnBeforeRemove -= cancel;
        }
    }

    [Fact]
    public void WorkBoundsAndLockedLayersRejectBeforeTopologyMutation()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var layer = new Layer("LOCKED_DELETE")
        {
            Flags = LayerFlags.Locked,
        };
        document.Layers.Add(layer);
        Mesh mesh = CreateQuadMesh();
        mesh.Layer = layer;
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        int[][] originalFaces = CloneFaces(mesh.Faces);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadDeleteMeshSubobjectsCommand(
                scene,
                [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)])));
        layer.Flags &= ~LayerFlags.Locked;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadDeleteMeshSubobjectsCommand(
                scene,
                [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)],
                maxControlVertices: 3)));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadDeleteMeshSubobjectsCommand(
                scene,
                [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)],
                maxFaceCorners: 5)));

        AssertFaces(originalFaces, mesh.Faces);
        Assert.Equal(0UL, session.ContentGeneration);
    }

    [Fact]
    public void FaceDeletionRebuildsAuthoredSubdivisionTopology()
    {
        Mesh mesh = CreateQuadMesh();
        mesh.SubdivisionLevel = 1;
        (CadDocumentSession session, _, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);

        history.Execute(new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)]));
        CadRecordedMesh3DScene rebuilt = CompileScene(session);
        CadMesh3DSubobjectComponent rebuiltComponent = Assert.Single(
            rebuilt.SubobjectComponents.ToArray());

        Assert.Equal(1UL, rebuilt.ContentGeneration);
        Assert.Equal(3, rebuiltComponent.VertexPositions.Length);
        Assert.Equal(3, rebuiltComponent.Edges.Length);
        Assert.Single(rebuiltComponent.Faces.ToArray());
        Assert.True(rebuilt.Statistics.TriangleCount > 1);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task DeletedFacesAndCompactedAttributesRoundTrip(
        CadDocumentFormat format)
    {
        Mesh mesh = CreateThreeTriangleMesh();
        (CadDocumentSession session, _, CadRecordedMesh3DScene scene) =
            CreateScene(mesh);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadDeleteMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 1)]));
        XYZ[] expectedVertices = mesh.Vertices.ToArray();
        int[][] expectedFaces = CloneFaces(mesh.Faces);
        Mesh.Edge[] expectedEdges = mesh.Edges.ToArray();
        XYZ[] expectedTextureCoordinates = mesh.TextureCoordinates.ToArray();
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
            sourceName: $"deleted-mesh.{format.ToString().ToLowerInvariant()}");
        Mesh restored = loaded.Session.Read(document =>
            Assert.IsType<Mesh>(Assert.Single(document.Entities)));

        Assert.Equal(expectedVertices, restored.Vertices);
        AssertFaces(expectedFaces, restored.Faces);
        AssertEdges(expectedEdges, restored.Edges);
        Assert.Equal(
            expectedTextureCoordinates,
            restored.TextureCoordinates.ToArray());
        Assert.Single(CompileScene(loaded.Session).SubobjectComponents.ToArray());
    }

    private static (
        CadDocumentSession Session,
        Mesh Mesh,
        CadRecordedMesh3DScene Scene) CreateScene(Mesh mesh)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        return (session, mesh, CompileScene(session));
    }

    private static Mesh CreateQuadMesh(bool withTextureCoordinates = false)
    {
        var mesh = new Mesh();
        mesh.Vertices.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(2, 0, 0),
            new XYZ(2, 2, 0),
            new XYZ(0, 2, 0),
        ]);
        mesh.Faces.AddRange([[0, 1, 2], [0, 2, 3]]);
        mesh.Edges.AddRange([
            new Mesh.Edge(0, 1) { Crease = 1.0 },
            new Mesh.Edge(0, 2) { Crease = 2.0 },
            new Mesh.Edge(0, 3) { Crease = 3.0 },
        ]);
        if (withTextureCoordinates)
        {
            mesh.TextureCoordinates =
            [
                new XYZ(0, 0, 0),
                new XYZ(1, 0, 0),
                new XYZ(1, 1, 0),
                new XYZ(0, 1, 0),
            ];
        }
        return mesh;
    }

    private static Mesh CreateTriangleMesh(double offset)
    {
        var mesh = new Mesh();
        mesh.Vertices.AddRange([
            new XYZ(offset, 0, 0),
            new XYZ(offset + 1, 0, 0),
            new XYZ(offset, 1, 0),
        ]);
        mesh.Faces.Add([0, 1, 2]);
        return mesh;
    }

    private static Mesh CreateThreeTriangleMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(1, 0, 0),
            new XYZ(0, 1, 0),
            new XYZ(3, 0, 0),
            new XYZ(4, 0, 0),
            new XYZ(3, 1, 0),
            new XYZ(6, 0, 0),
            new XYZ(7, 0, 0),
            new XYZ(6, 1, 0),
        ]);
        mesh.Faces.AddRange([
            [0, 1, 2],
            [3, 4, 5],
            [6, 7, 8],
        ]);
        mesh.Edges.AddRange([
            new Mesh.Edge(0, 1) { Crease = -1.0 },
            new Mesh.Edge(3, 4) { Crease = 1.0 },
            new Mesh.Edge(6, 7) { Crease = 2.0 },
        ]);
        mesh.TextureCoordinates = Enumerable.Range(0, 9)
            .Select(index => new XYZ(index / 8.0, 0, 0));
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

    private static int[][] CloneFaces(IReadOnlyList<int[]> faces) =>
        faces.Select(static face => face.ToArray()).ToArray();

    private static void AssertFaces(
        IReadOnlyList<int[]> expected,
        IReadOnlyList<int[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }

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
}
