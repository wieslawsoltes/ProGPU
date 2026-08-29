using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using System.Buffers.Binary;
using CSMath;
using ProGPU.Backend.Native;
using ProGPU.CAD.Native;
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
        Assert.Equal(6, snapshot.Entities.Length);
        Assert.Single(snapshot.Meshes3D.ToArray());
        Assert.Equal(1, snapshot.Statistics.SourceEntityCount);
        Assert.Equal(6, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.All(snapshot.Entities.ToArray().Where(entity => entity.Kind == CadEntityKind.Line), entity =>
        {
            Assert.Equal(mesh.Handle, entity.Handle);
            Assert.Equal(CadEntityKind.Line, entity.Kind);
        });
        Assert.Equal(
            mesh.Handle,
            Assert.Single(snapshot.Entities.ToArray(), entity =>
                entity.Kind == CadEntityKind.Mesh3D).Handle);
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

        Assert.Equal(6, selection.MatchedPrimitiveCount);
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

    [Fact]
    public void ConcavePlanarMeshRetainsExactFlatTrianglesUvSelectionAndRebasedScene()
    {
        const double world = 1_000_000_000_000.0;
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(world, world, 25));
        mesh.Vertices.Add(new XYZ(world + 4, world, 25));
        mesh.Vertices.Add(new XYZ(world + 4, world + 4, 25));
        mesh.Vertices.Add(new XYZ(world + 2, world + 2, 25));
        mesh.Vertices.Add(new XYZ(world, world + 4, 25));
        mesh.TextureCoordinates =
        [
            new XYZ(0, 0, 0),
            new XYZ(1, 0, 0),
            new XYZ(1, 1, 0),
            new XYZ(0.5, 0.5, 0),
            new XYZ(0, 1, 0),
        ];
        mesh.Faces.Add([0, 1, 2, 3, 4]);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadMesh3DPrimitive primitive = Assert.Single(snapshot.Meshes3D.ToArray());
        CadMesh3DDrawRange range = Assert.Single(snapshot.Mesh3DDrawRanges.ToArray());

        Assert.Equal(9, range.VertexCount);
        Assert.Equal(9, range.IndexCount);
        Assert.Equal([0U, 1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U],
            snapshot.Mesh3DIndices.ToArray());
        Assert.All(snapshot.Mesh3DVertices.ToArray(), vertex =>
        {
            Assert.Equal(0.0, vertex.Normal.X, 12);
            Assert.Equal(0.0, vertex.Normal.Y, 12);
            Assert.Equal(1.0, vertex.Normal.Z, 12);
        });
        Assert.Contains(snapshot.Mesh3DVertices.ToArray(), vertex =>
            vertex.TextureCoordinate == new System.Numerics.Vector2(0.5f, 0.5f));

        CadSelectionCandidate candidate = MeshCandidate(snapshot);
        CadPointHitResult interior = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(world + 1, world + 1, 25.125),
            0.125);
        CadPointHitResult notch = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(world + 2, world + 3, 25),
            0.1);
        Assert.Equal(CadPointHitStatus.Hit, interior.Status);
        Assert.Equal(0.125, interior.Distance, 12);
        Assert.Equal(CadPointHitStatus.Miss, notch.Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(world + 0.9, world + 0.9, 24.9),
                    new CadPoint3D(world + 1.1, world + 1.1, 25.1)),
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(world, world, 25),
                    new CadPoint3D(world + 3, world + 4, 25)),
                CadBoundsSelectionMode.Window).Status);

        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
        CadMesh3DDrawBatch batch = Assert.Single(scene.DrawBatches.ToArray());
        Assert.Equal(1, scene.Statistics.SourceMeshCount);
        Assert.Equal(1, scene.Statistics.FaceRangeCount);
        Assert.Equal(3, scene.Statistics.TriangleCount);
        Assert.Equal(1, scene.Statistics.DrawBatchCount);
        Assert.Equal(mesh.Handle, batch.Handle);
        Assert.Equal(range.VertexCount, batch.Positions.Length);
        CadMesh3DVertex source = snapshot.Mesh3DVertices.Span[0];
        System.Numerics.Vector3 rebased = batch.Positions.Span[0];
        Assert.Equal((float)(source.Position.X - snapshot.RebaseOrigin.X), rebased.X);
        Assert.Equal((float)(source.Position.Y - snapshot.RebaseOrigin.Y), rebased.Y);
        Assert.Equal((float)(source.Position.Z - snapshot.RebaseOrigin.Z), rebased.Z);
        Assert.Equal(primitive.Bounds, batch.Bounds);
    }

    [Fact]
    public void NonPlanarQuadUsesPersistedZeroTwoDiagonalAndFlatNormals()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 0, 0));
        mesh.Vertices.Add(new XYZ(2, 2, 1));
        mesh.Vertices.Add(new XYZ(0, 2, 0));
        mesh.Faces.Add([0, 1, 2, 3]);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadMesh3DVertex[] vertices = snapshot.Mesh3DVertices.ToArray();

        Assert.Equal(6, vertices.Length);
        Assert.Equal(new CadPoint3D(0, 0, 0), vertices[0].Position);
        Assert.Equal(new CadPoint3D(2, 2, 1), vertices[2].Position);
        Assert.Equal(new CadPoint3D(0, 0, 0), vertices[3].Position);
        Assert.Equal(new CadPoint3D(2, 2, 1), vertices[4].Position);
        Assert.Equal(new CadPoint3D(0, 2, 0), vertices[5].Position);
        Assert.NotEqual(vertices[0].Normal, vertices[3].Normal);
    }

    [Fact]
    public void NativeAdapterBatchesMeshSceneIntoOneCanonicalPointerFreeDraw()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateQuadMesh());
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
        var camera = new CadNativeMesh3DCamera(
            System.Numerics.Matrix4x4.Identity,
            System.Numerics.Matrix4x4.Identity,
            new System.Numerics.Vector3(0, 0, 5),
            new NativeImageRect(0, 0, 640, 480));

        CadNativeMesh3DScene native = new CadNativeMesh3DSceneCompiler().Compile(
            scene,
            camera,
            sceneId: 90210U);
        ReadOnlySpan<byte> stream = native.Stream;

        Assert.Equal(1, native.DrawBatchCount);
        Assert.Equal(6, native.VertexCount);
        Assert.Equal(6, native.IndexCount);
        Assert.Equal(80U, BinaryPrimitives.ReadUInt32LittleEndian(stream));
        Assert.Equal(0x31534750U, BinaryPrimitives.ReadUInt32LittleEndian(stream[4..]));
        Assert.Equal((uint)native.Length, BinaryPrimitives.ReadUInt32LittleEndian(stream[20..]));
        Assert.Equal(90210U, BinaryPrimitives.ReadUInt64LittleEndian(stream[24..]));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(stream[44..]));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(stream[56..]));
        Assert.Equal(snapshot.ContentGeneration + 1U, native.NativeGeneration);
        Assert.Equal(native.NativeGeneration, BinaryPrimitives.ReadUInt64LittleEndian(stream[32..]));
    }

    [Fact]
    public void Mesh3DSceneCanExcludeNonPlottableFaceBatches()
    {
        var document = new CadDocument();
        var noPlot = new Layer("NO_PLOT") { PlotFlag = false };
        document.Layers.Add(noPlot);
        Mesh mesh = CreateTriangleMesh();
        mesh.Layer = noPlot;
        document.Entities.Add(mesh);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedMesh3DScene screen = new CadMesh3DSceneCompiler().Compile(snapshot);
        CadRecordedMesh3DScene plotFiltered = new CadMesh3DSceneCompiler().Compile(
            snapshot,
            new CadMesh3DSceneOptions { IncludeNonPlottableLayers = false });

        Assert.Single(screen.DrawBatches.ToArray());
        Assert.Empty(plotFiltered.DrawBatches.ToArray());
        Assert.Equal(1, plotFiltered.Statistics.SourceMeshCount);
        Assert.Equal(1, plotFiltered.Statistics.FaceRangeCount);
        Assert.Equal(0, plotFiltered.Statistics.TriangleCount);
    }

    [Theory]
    [InlineData("self-intersecting", false)]
    [InlineData("non-planar-ngon", true)]
    public void AmbiguousFacesAreRejectedBeforeAnyMeshOrWireCommit(
        string kind,
        bool unsupported)
    {
        var mesh = new Mesh();
        if (kind == "self-intersecting")
        {
            mesh.Vertices.Add(new XYZ(0, 0, 0));
            mesh.Vertices.Add(new XYZ(2, 2, 0));
            mesh.Vertices.Add(new XYZ(0, 2, 0));
            mesh.Vertices.Add(new XYZ(2, 0, 0));
        }
        else
        {
            mesh.Vertices.Add(new XYZ(0, 0, 0));
            mesh.Vertices.Add(new XYZ(2, 0, 0));
            mesh.Vertices.Add(new XYZ(3, 1, 0));
            mesh.Vertices.Add(new XYZ(1, 2, 1));
            mesh.Vertices.Add(new XYZ(0, 1, 0));
        }
        mesh.Faces.Add(Enumerable.Range(0, mesh.Vertices.Count).ToArray());
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Lines.ToArray());
        Assert.Empty(snapshot.Meshes3D.ToArray());
        Assert.Equal(unsupported ? 1 : 0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(unsupported ? 0 : 1, snapshot.Statistics.InvalidEntityCount);
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

    private static CadSelectionCandidate MeshCandidate(CadDocumentSnapshot snapshot)
    {
        CadEntityHeader header = Assert.Single(
            snapshot.Entities.ToArray(),
            entity => entity.Kind == CadEntityKind.Mesh3D);
        int entityIndex = Array.IndexOf(snapshot.Entities.ToArray(), header);
        return new CadSelectionCandidate(
            snapshot.ContentGeneration,
            entityIndex,
            header.Handle,
            header.Kind,
            header.Bounds);
    }
}
