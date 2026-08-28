using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMeshSnapshotTests
{
    [Fact]
    public void LevelZeroMeshReusesRetainedSelectionSceneNativeAndPrintPipelines()
    {
        Mesh mesh = CreateQuadMesh();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add level-zero mesh", document => document.Entities.Add(mesh));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(5, snapshot.Lines.Length);
        Assert.Equal(5, snapshot.Entities.Length);
        Assert.Equal(1, snapshot.Statistics.SourceEntityCount);
        Assert.Equal(6, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.All(snapshot.Entities.ToArray(), entity =>
        {
            Assert.Equal(mesh.Handle, entity.Handle);
            Assert.Equal(CadEntityKind.Line, entity.Kind);
        });
        Assert.Equal(5, scene.Statistics.RecordedEntityCount);

        var entityScratch = new int[snapshot.Entities.Length];
        var candidates = new CadSelectionCandidate[snapshot.Entities.Length];
        var matches = new CadSelectionCandidate[snapshot.Entities.Length];
        var hashScratch = new int[
            CadSelectionQuery.GetUniqueHandleScratchLength(snapshot.Entities.Length)];
        var handles = new ulong[snapshot.Entities.Length];
        CadBoundsSelectionQueryResult selection = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            handles);

        Assert.Equal(5, selection.MatchedPrimitiveCount);
        Assert.Equal(1, selection.HandleTotalCount);
        Assert.Equal(mesh.Handle, handles[0]);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);

        using CadPrintPlan printPlan = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(5, printPlan.SceneStatistics.RecordedEntityCount);
    }

    [Fact]
    public void SharedFaceEdgesAreDeduplicatedBeforeAncestorInsertTransform()
    {
        var document = new CadDocument();
        var block = new BlockRecord("MESH_BLOCK");
        block.Entities.Add(CreateQuadMesh());
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 30),
            XScale = 2,
            YScale = 3,
            ZScale = 4,
        };
        document.Entities.Add(insert);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Equal(5, snapshot.Lines.Length);
        Assert.All(snapshot.Entities.ToArray(), entity => Assert.Equal(insert.Handle, entity.Handle));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(
                line,
                new CadPoint3D(10, 20, 30),
                new CadPoint3D(14, 20, 30)));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(
                line,
                new CadPoint3D(10, 23, 34),
                new CadPoint3D(14, 23, 34)));
        Assert.Equal(7, snapshot.Statistics.ExpandedEntityCount);
    }

    [Theory]
    [InlineData("missing-face")]
    [InlineData("short-face")]
    [InlineData("outside-index")]
    [InlineData("degenerate-face")]
    [InlineData("collapsed-edge")]
    public void MalformedLevelZeroTopologyIsRejectedTransactionally(string kind)
    {
        var document = new CadDocument();
        var mesh = new Mesh();
        mesh.Vertices.Add(XYZ.Zero);
        mesh.Vertices.Add(XYZ.AxisX);
        mesh.Vertices.Add(XYZ.AxisY);
        switch (kind)
        {
            case "missing-face":
                break;
            case "short-face":
                mesh.Faces.Add([0, 1]);
                break;
            case "outside-index":
                mesh.Faces.Add([0, 1, 3]);
                break;
            case "degenerate-face":
                mesh.Faces.Add([0, 1, 0]);
                break;
            case "collapsed-edge":
                mesh.Vertices[2] = XYZ.AxisX;
                mesh.Faces.Add([0, 1, 2]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Lines.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP002" &&
            diagnostic.Message.Contains("MESH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SubdivisionAndTopologyVisitLimitAreExplicitlyDiagnosed()
    {
        var document = new CadDocument();
        Mesh subdivided = CreateTriangleMesh();
        subdivided.SubdivisionLevel = 1;
        document.Entities.Add(subdivided);
        document.Entities.Add(CreateTriangleMesh());

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions { MaxMeshFaceIndices = 2 });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(2, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("Subdivided", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("topology", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DerivedEdgesRespectTheGlobalExpandedEntityLimit()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateTriangleMesh());

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                new CadDocumentSession(document),
                new CadSnapshotOptions { MaxExpandedEntities = 3 }));

        Assert.Contains("Expanded entity count", exception.Message);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task LevelZeroMeshRoundTripsThroughAdvertisedFormats(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(CreateQuadMesh());
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            format,
            sourceName: $"mesh.{format.ToString().ToLowerInvariant()}");

        Mesh restored = loaded.Session.Read(source =>
            Assert.IsType<Mesh>(Assert.Single(source.Entities)));
        Assert.Equal(4, restored.Vertices.Count);
        Assert.Equal(2, restored.Faces.Count);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Equal(5, snapshot.Lines.Length);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void MeshTransformsAndDuplicateRoundTripThroughHistory()
    {
        var document = new CadDocument();
        Mesh mesh = CreateTriangleMesh();
        document.Entities.Add(mesh);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateEntitiesCommand(
            [mesh.Handle],
            new CadPoint3D(10, 20, 30)));
        history.Execute(new CadRotateEntitiesCommand(
            [mesh.Handle],
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));
        history.Execute(new CadScaleEntitiesCommand(
            [mesh.Handle],
            2,
            CadPoint3D.Zero));
        var duplicate = new CadDuplicateModelSpaceEntityCommand(
            mesh.Handle,
            new CadPoint3D(5, 0, 0));
        history.Execute(duplicate);

        Assert.IsType<Mesh>(duplicate.Duplicate);
        CadDocumentSnapshot transformed = new CadSnapshotCompiler().Compile(session);
        Assert.Equal(6, transformed.Lines.Length);
        Assert.Equal(2, transformed.Statistics.SourceEntityCount);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(3, new CadSnapshotCompiler().Compile(session).Lines.Length);
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        CadDocumentSnapshot restored = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(restored.Lines.ToArray(), line =>
            HasEndpoints(line, CadPoint3D.Zero, new CadPoint3D(2, 0, 0)));
    }

    private static Mesh CreateTriangleMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(XYZ.Zero);
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(0, 1, 2));
        mesh.Faces.Add([0, 1, 2]);
        return mesh;
    }

    private static Mesh CreateQuadMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(XYZ.Zero);
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 1, 1));
        mesh.Vertices.Add(new XYZ(0, 1, 1));
        mesh.Faces.Add([0, 1, 2]);
        mesh.Faces.Add([0, 2, 3]);
        return mesh;
    }

    private static bool HasEndpoints(
        CadLinePrimitive line,
        CadPoint3D first,
        CadPoint3D second) =>
        (line.Start == first && line.End == second) ||
        (line.Start == second && line.End == first);
}
