using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMesh3DEditingTests
{
    [Fact]
    public void VertexEdgeAndFaceSelectionsMoveTheirAuthoredVertexUnionOnce()
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateDirectScene();
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);
        CadMesh3DSubobjectId edge = CreateId(
            scene,
            component,
            CadMesh3DSubobjectKind.Edge,
            2);
        CadMesh3DSubobjectId face = CreateId(
            scene,
            component,
            CadMesh3DSubobjectKind.Face,
            0);
        XYZ[] original = mesh.Vertices.ToArray();

        ulong generation = history.Execute(
            new CadTranslateMeshSubobjectsCommand(
                scene,
                [edge, face, face],
                new CadPoint3D(0, 0, 5)));

        Assert.Equal(1UL, generation);
        Assert.Equal(original[0] + new XYZ(0, 0, 5), mesh.Vertices[0]);
        Assert.Equal(original[1] + new XYZ(0, 0, 5), mesh.Vertices[1]);
        Assert.Equal(original[2] + new XYZ(0, 0, 5), mesh.Vertices[2]);
        Assert.Equal(original[3], mesh.Vertices[3]);
        Assert.True(history.TryUndo(out _));
        Assert.Equal(original, mesh.Vertices);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(original[0] + new XYZ(0, 0, 5), mesh.Vertices[0]);
        Assert.Equal(original[1] + new XYZ(0, 0, 5), mesh.Vertices[1]);
        Assert.Equal(original[2] + new XYZ(0, 0, 5), mesh.Vertices[2]);
        Assert.Equal(original[3], mesh.Vertices[3]);
    }

    [Fact]
    public void EditedSelectionCanBeRemappedBySourceIdentityAfterGenerationAdvance()
    {
        (CadDocumentSession session, Mesh _, CadRecordedMesh3DScene oldScene) =
            CreateDirectScene();
        CadMesh3DSubobjectComponent oldComponent = Assert.Single(
            oldScene.SubobjectComponents.ToArray());
        CadMesh3DSubobjectId oldId = CreateId(
            oldScene,
            oldComponent,
            CadMesh3DSubobjectKind.Vertex,
            3);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateMeshSubobjectsCommand(
            oldScene,
            [oldId],
            new CadPoint3D(1, 2, 3)));
        CadRecordedMesh3DScene newScene = CompileScene(session);
        CadMesh3DSubobjectComponent newComponent = Assert.Single(
            newScene.SubobjectComponents.ToArray());
        var newId = new CadMesh3DSubobjectId(
            newScene.ContentGeneration,
            newComponent.Handle,
            newComponent.ComponentIndex,
            oldId.Kind,
            oldId.Index);

        Assert.False(newScene.TryGetSubobjectComponent(oldId, out _));
        Assert.Equal(oldComponent.SourceHandle, newComponent.SourceHandle);
        Assert.True(newComponent.IsDirectModelSpaceSource);
        Assert.True(newScene.TryGetSubobjectComponent(newId, out _));
    }

    [Fact]
    public void StaleSceneCommandIsRejectedBeforeMutationOrGenerationAdvance()
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateDirectScene();
        var history = new CadDocumentHistory(session);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var command = new CadTranslateMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Vertex, 0)],
            new CadPoint3D(0, 0, 1));
        XYZ original = mesh.Vertices[0];
        session.Edit("Unrelated external edit", _ => { });

        CadEditHistoryDivergedException exception =
            Assert.Throws<CadEditHistoryDivergedException>(
                () => history.Execute(command));

        Assert.Equal(0UL, exception.ExpectedGeneration);
        Assert.Equal(1UL, exception.ActualGeneration);
        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(original, mesh.Vertices[0]);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void CollapsedEdgeIsRejectedTransactionally()
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateDirectScene();
        var history = new CadDocumentHistory(session);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        XYZ[] original = mesh.Vertices.ToArray();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(new CadTranslateMeshSubobjectsCommand(
                scene,
                [CreateId(scene, component, CadMesh3DSubobjectKind.Vertex, 0)],
                new CadPoint3D(2, 0, 0))));

        Assert.Contains("collapse", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, mesh.Vertices);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void FailureInOneMeshLeavesEverySelectedMeshUnchanged()
    {
        var document = new CadDocument();
        Mesh first = CreateMesh();
        Mesh second = CreateMesh();
        for (int vertex = 0; vertex < first.Vertices.Count; vertex++)
        {
            first.Vertices[vertex] += new XYZ(10, 0, 0);
        }
        document.Entities.Add(first);
        document.Entities.Add(second);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent[] components =
            scene.SubobjectComponents.ToArray();
        XYZ[] firstOriginal = first.Vertices.ToArray();
        XYZ[] secondOriginal = second.Vertices.ToArray();
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadTranslateMeshSubobjectsCommand(
                scene,
                [
                    CreateId(
                        scene,
                        components.Single(component =>
                            component.SourceHandle == first.Handle),
                        CadMesh3DSubobjectKind.Face,
                        0),
                    CreateId(
                        scene,
                        components.Single(component =>
                            component.SourceHandle == second.Handle),
                        CadMesh3DSubobjectKind.Vertex,
                        0),
                ],
                new CadPoint3D(2, 0, 0))));

        Assert.Equal(firstOriginal, first.Vertices);
        Assert.Equal(secondOriginal, second.Vertices);
        Assert.Equal(0UL, session.ContentGeneration);
    }

    [Fact]
    public void AuthoredFaceTranslationRebuildsSubdivisionFromMovedControlVertices()
    {
        var document = new CadDocument();
        Mesh mesh = CreateMesh();
        mesh.SubdivisionLevel = 1;
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 0)],
            new CadPoint3D(0, 0, 2)));
        CadRecordedMesh3DScene rebuilt = CompileScene(session);
        CadMesh3DSubobjectComponent rebuiltComponent = Assert.Single(
            rebuilt.SubobjectComponents.ToArray());

        Assert.Equal(1UL, rebuilt.ContentGeneration);
        Assert.Equal(component.VertexPositions.Length,
            rebuiltComponent.VertexPositions.Length);
        Assert.Equal(2.0, mesh.Vertices[0].Z, 12);
        Assert.Equal(2.0, mesh.Vertices[1].Z, 12);
        Assert.Equal(2.0, mesh.Vertices[2].Z, 12);
        Assert.Equal(0.0, mesh.Vertices[3].Z, 12);
        Assert.True(rebuilt.Statistics.TriangleCount > 2);
    }

    [Fact]
    public void NestedBlockMeshIsIdentifiedButRequiresReferenceEditingScope()
    {
        Mesh source = CreateMesh();
        var block = new BlockRecord("EDIT_NESTED_MESH");
        block.Entities.Add(source);
        var document = new CadDocument();
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 30),
            XScale = 2,
            YScale = 3,
            ZScale = 4,
        };
        document.Entities.Add(insert);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        XYZ original = source.Vertices[0];

        Assert.Equal(insert.Handle, component.Handle);
        Assert.Equal(source.Handle, component.SourceHandle);
        Assert.False(component.IsDirectModelSpaceSource);
        Assert.Equal(
            new CadPoint3D(10, 20, 30),
            component.SourceToWorld.TransformPoint(CadPoint3D.Zero));
        Assert.Equal(
            new CadPoint3D(2, 0, 0),
            component.SourceToWorld.TransformVector(new CadPoint3D(1, 0, 0)));
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new CadTranslateMeshSubobjectsCommand(
                scene,
                [CreateId(scene, component, CadMesh3DSubobjectKind.Vertex, 0)],
                new CadPoint3D(0, 0, 1)));
        Assert.Contains("reference-editing", exception.Message);
        Assert.Equal(original, source.Vertices[0]);
        Assert.Equal(0UL, session.ContentGeneration);
    }

    [Fact]
    public void LockedLayerAndAffectedVertexBoundsRejectBeforeMutation()
    {
        var document = new CadDocument();
        var locked = new Layer("LOCKED_MESH")
        {
            Flags = LayerFlags.Locked,
        };
        document.Layers.Add(locked);
        Mesh mesh = CreateMesh();
        mesh.Layer = locked;
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        CadRecordedMesh3DScene scene = CompileScene(session);
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        CadMesh3DSubobjectId face = CreateId(
            scene,
            component,
            CadMesh3DSubobjectKind.Face,
            0);
        XYZ[] original = mesh.Vertices.ToArray();
        var history = new CadDocumentHistory(session);

        InvalidOperationException lockedException =
            Assert.Throws<InvalidOperationException>(() => history.Execute(
                new CadTranslateMeshSubobjectsCommand(
                    scene,
                    [face],
                    new CadPoint3D(0, 0, 1))));
        Assert.Contains("locked layer", lockedException.Message);
        Assert.Equal(original, mesh.Vertices);
        Assert.Equal(0UL, session.ContentGeneration);

        locked.Flags &= ~LayerFlags.Locked;
        InvalidOperationException boundException =
            Assert.Throws<InvalidOperationException>(() => history.Execute(
                new CadTranslateMeshSubobjectsCommand(
                    scene,
                    [face],
                    new CadPoint3D(0, 0, 1),
                    maxAffectedVertices: 2)));
        Assert.Contains("more than", boundException.Message);
        Assert.Equal(original, mesh.Vertices);
        Assert.Equal(0UL, session.ContentGeneration);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task EditedControlVerticesRoundTripThroughAdvertisedFormats(
        CadDocumentFormat format)
    {
        (CadDocumentSession session, Mesh mesh, CadRecordedMesh3DScene scene) =
            CreateDirectScene();
        CadMesh3DSubobjectComponent component = Assert.Single(
            scene.SubobjectComponents.ToArray());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadTranslateMeshSubobjectsCommand(
            scene,
            [CreateId(scene, component, CadMesh3DSubobjectKind.Face, 1)],
            new CadPoint3D(0, 0, 7)));
        XYZ[] expected = mesh.Vertices.ToArray();
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
            sourceName: $"edited-mesh.{format.ToString().ToLowerInvariant()}");
        Mesh restored = loaded.Session.Read(document =>
            Assert.IsType<Mesh>(Assert.Single(document.Entities)));

        Assert.Equal(expected, restored.Vertices);
        Assert.Equal(mesh.Faces, restored.Faces);
        Assert.Single(CompileScene(loaded.Session).SubobjectComponents.ToArray());
    }

    private static (
        CadDocumentSession Session,
        Mesh Mesh,
        CadRecordedMesh3DScene Scene) CreateDirectScene()
    {
        var document = new CadDocument(ACadVersion.AC1032);
        Mesh mesh = CreateMesh();
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        return (session, mesh, CompileScene(session));
    }

    private static Mesh CreateMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 2, 0));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.Faces.Add([0, 1, 2]);
        mesh.Faces.Add([0, 2, 3]);
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
}
