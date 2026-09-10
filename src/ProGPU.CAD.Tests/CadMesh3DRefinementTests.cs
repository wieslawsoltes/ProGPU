using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMesh3DRefinementTests
{
    [Fact]
    public void WholeObjectRefinementBakesDisplayedTopologyAndRetainsExactHistory()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        var document = new CadDocument();
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        XYZ[] beforeVertices = mesh.Vertices.ToArray();
        int[][] beforeFaces = CloneFaces(mesh.Faces);
        var command = new CadRefineMesh3DCommand([mesh.Handle]);

        history.Execute(command);

        Assert.Equal(0, mesh.SubdivisionLevel);
        Assert.Equal(9, mesh.Vertices.Count);
        Assert.Equal(4, mesh.Faces.Count);
        Assert.All(mesh.Faces, face => Assert.Equal(4, face.Length));
        Assert.Equal(
            new CadMesh3DRefinementSummary(1, 1, 4, 1, 9, 4, 0, 20),
            command.Summary);
        Assert.Equal(8, CompileScene(session).Statistics.TriangleCount);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(1, mesh.SubdivisionLevel);
        Assert.Equal(beforeVertices, mesh.Vertices);
        AssertFaces(beforeFaces, mesh.Faces);

        Assert.True(history.TryRedo(out _));
        Assert.Equal(0, mesh.SubdivisionLevel);
        Assert.Equal(9, mesh.Vertices.Count);
        Assert.Equal(4, mesh.Faces.Count);
    }

    [Fact]
    public void RefinementFiltersLevelZeroInMixedSelection()
    {
        Mesh zero = CreateQuadMesh(level: 0);
        Mesh two = CreateQuadMesh(level: 2, offset: 10.0);
        var document = new CadDocument();
        document.Entities.Add(zero);
        document.Entities.Add(two);
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadRefineMesh3DCommand(
            [zero.Handle, two.Handle, zero.Handle]);

        history.Execute(command);

        Assert.Equal(4, zero.Vertices.Count);
        Assert.Single(zero.Faces);
        Assert.Equal(25, two.Vertices.Count);
        Assert.Equal(16, two.Faces.Count);
        Assert.Equal(0, two.SubdivisionLevel);
        Assert.Equal(2, command.Summary.SelectedMeshCount);
        Assert.Equal(1, command.Summary.AffectedMeshCount);
        Assert.Equal(84, command.Summary.TopologyVisitCount);
    }

    [Fact]
    public void AllLevelZeroSelectionIsRejectedWithoutGenerationChange()
    {
        Mesh mesh = CreateQuadMesh(level: 0);
        var document = new CadDocument();
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRefineMesh3DCommand([mesh.Handle])));

        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(4, mesh.Vertices.Count);
    }

    [Fact]
    public void TypeLayerAndAggregateLimitsFailTransactionally()
    {
        Mesh first = CreateQuadMesh(level: 1);
        Mesh second = CreateQuadMesh(level: 1, offset: 10.0);
        var line = new Line
        {
            StartPoint = XYZ.Zero,
            EndPoint = XYZ.AxisX,
        };
        var lockedLayer = new Layer("LOCKED_MESH_REFINE")
        {
            Flags = LayerFlags.Locked,
        };
        var document = new CadDocument();
        document.Layers.Add(lockedLayer);
        document.Entities.Add(first);
        document.Entities.Add(second);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        second.Layer = lockedLayer;
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRefineMesh3DCommand([first.Handle, second.Handle])));
        second.Layer = document.Layers[Layer.DefaultName];
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRefineMesh3DCommand([first.Handle, line.Handle])));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRefineMesh3DCommand(
                [first.Handle, second.Handle],
                maxTopologyVisits: 39)));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRefineMesh3DCommand(
                [first.Handle],
                maxResultControlVertices: 8)));
        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadRefineMesh3DCommand(
                [first.Handle],
                maxResultFaces: 3)));

        Assert.Equal(1, first.SubdivisionLevel);
        Assert.Equal(4, first.Vertices.Count);
        Assert.Equal(1, second.SubdivisionLevel);
        Assert.Equal(4, second.Vertices.Count);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void RefinementDecaysOnlyAuthoredCreasesAlongSourceEdgeChains()
    {
        Mesh mesh = CreateTwoQuadMesh(level: 2);
        mesh.BlendCrease = true;
        mesh.Edges.AddRange([
            new Mesh.Edge(1, 4) { Crease = 4.0 },
            new Mesh.Edge(1, 0) { Crease = -1.0 },
            new Mesh.Edge(3, 0) { Crease = 2.5 },
            new Mesh.Edge(2, 5) { Crease = 1.0 },
        ]);
        var document = new CadDocument();
        document.Entities.Add(mesh);
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadRefineMesh3DCommand([mesh.Handle]);

        history.Execute(command);

        Assert.Equal(12, mesh.Edges.Count);
        Assert.Equal(4, mesh.Edges.Count(edge => edge.Crease == 2.0));
        Assert.Equal(4, mesh.Edges.Count(edge => edge.Crease == -1.0));
        Assert.Equal(4, mesh.Edges.Count(edge => edge.Crease == 0.5));
        Assert.DoesNotContain(mesh.Edges, edge => edge.Crease == 1.0);
        Assert.Equal(12, command.ResultAuthoredCreaseEdgeCount);
        Assert.All(mesh.Edges, edge =>
            Assert.NotEqual(EdgeKey(edge.Start, edge.End), (2, 5)));
    }

    [Fact]
    public void RefinementPreservesDoublePrecisionUvwAcrossHistory()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        XYZ[] sourceTextureCoordinates =
        [
            new XYZ(0.0, 0.0, 10.0),
            new XYZ(1.0, 0.0, 20.0),
            new XYZ(1.0, 1.0, 30.0),
            new XYZ(0.0, 1.0, 40.0),
        ];
        mesh.TextureCoordinates = sourceTextureCoordinates;
        var document = new CadDocument();
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadRefineMesh3DCommand([mesh.Handle]));

        XYZ[] refined = mesh.TextureCoordinates.ToArray();
        Assert.Equal(9, refined.Length);
        Assert.Equal(sourceTextureCoordinates, refined[..4]);
        Assert.Equal(new XYZ(0.5, 0.0, 15.0), refined[4]);
        Assert.Equal(new XYZ(0.5, 0.5, 25.0), refined[8]);
        Assert.True(history.TryUndo(out _));
        Assert.Equal(sourceTextureCoordinates, mesh.TextureCoordinates);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(refined, mesh.TextureCoordinates);
    }

    [Fact]
    public void UndoRejectsOutOfHistoryTopologyMutation()
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        var document = new CadDocument();
        document.Entities.Add(mesh);
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        history.Execute(new CadRefineMesh3DCommand([mesh.Handle]));
        mesh.Vertices[0] = new XYZ(99, 99, 99);

        Assert.Throws<InvalidOperationException>(
            () => history.TryUndo(out _));

        Assert.Equal(new XYZ(99, 99, 99), mesh.Vertices[0]);
        Assert.Equal(0, mesh.SubdivisionLevel);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task RefinedTopologyUvwAndCreasesRoundTrip(
        CadDocumentFormat format)
    {
        Mesh mesh = CreateQuadMesh(level: 1);
        mesh.Edges.Add(new Mesh.Edge(0, 1) { Crease = -1.0 });
        mesh.TextureCoordinates =
        [
            new XYZ(0, 0, 1),
            new XYZ(1, 0, 2),
            new XYZ(1, 1, 3),
            new XYZ(0, 1, 4),
        ];
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadRefineMesh3DCommand([mesh.Handle]));
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
            sourceName: $"mesh-refined.{format.ToString().ToLowerInvariant()}");
        Mesh restored = loaded.Session.Read(drawing =>
            Assert.IsType<Mesh>(Assert.Single(drawing.Entities)));

        Assert.Equal(0, restored.SubdivisionLevel);
        Assert.Equal(9, restored.Vertices.Count);
        Assert.Equal(4, restored.Faces.Count);
        Assert.Equal(2, restored.Edges.Count);
        Assert.All(restored.Edges, edge => Assert.Equal(-1.0, edge.Crease));
        Assert.Equal(9, restored.TextureCoordinates.Count());
        Assert.Equal(8, CompileScene(loaded.Session).Statistics.TriangleCount);
    }

    private static Mesh CreateQuadMesh(int level, double offset = 0.0)
    {
        var mesh = new Mesh { SubdivisionLevel = level };
        mesh.Vertices.AddRange([
            new XYZ(offset + 0, 0, 0),
            new XYZ(offset + 2, 0, 0),
            new XYZ(offset + 2, 2, 0),
            new XYZ(offset + 0, 2, 0),
        ]);
        mesh.Faces.Add([0, 1, 2, 3]);
        return mesh;
    }

    private static Mesh CreateTwoQuadMesh(int level)
    {
        var mesh = new Mesh { SubdivisionLevel = level };
        mesh.Vertices.AddRange([
            new XYZ(0, 0, 0),
            new XYZ(1, 0, 0),
            new XYZ(2, 0, 0),
            new XYZ(0, 1, 0),
            new XYZ(1, 1, 0),
            new XYZ(2, 1, 0),
        ]);
        mesh.Faces.AddRange([
            [0, 1, 4, 3],
            [1, 2, 5, 4],
        ]);
        return mesh;
    }

    private static CadRecordedMesh3DScene CompileScene(
        CadDocumentSession session) => new CadMesh3DSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

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

    private static (int, int) EdgeKey(int first, int second) => first < second
        ? (first, second)
        : (second, first);
}
