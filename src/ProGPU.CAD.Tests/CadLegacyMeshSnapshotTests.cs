using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLegacyMeshSnapshotTests
{
    [Theory]
    [InlineData(false, false, 12)]
    [InlineData(true, false, 15)]
    [InlineData(false, true, 15)]
    [InlineData(true, true, 18)]
    public void PolygonMeshHonorsIndependentMAndNClosure(
        bool closeM,
        bool closeN,
        int expectedEdges)
    {
        PolygonMesh mesh = CreatePolygonMesh(closeM, closeN);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Equal(expectedEdges, snapshot.Lines.Length);
        Assert.Equal(expectedEdges + 1, snapshot.Entities.Length);
        Assert.Single(snapshot.Meshes3D.ToArray());
        int expectedFaces = (2 + (closeM ? 1 : 0)) *
            (2 + (closeN ? 1 : 0));
        Assert.Equal(expectedFaces, snapshot.Mesh3DDrawRanges.Length);
        Assert.Equal(expectedFaces * 6, snapshot.Mesh3DVertices.Length);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.All(snapshot.Entities.ToArray().Where(entity => entity.Kind == CadEntityKind.Line), entity =>
        {
            Assert.Equal(mesh.Handle, entity.Handle);
            Assert.Equal(CadEntityKind.Line, entity.Kind);
        });
    }

    [Fact]
    public void PolygonMeshUsesWcsVerticesBeforeAncestorInsertTransform()
    {
        var document = new CadDocument();
        var block = new BlockRecord("LEGACY_GRID");
        PolygonMesh mesh = CreatePolygonMesh(closeM: false, closeN: false);
        mesh.Normal = XYZ.AxisY;
        block.Entities.Add(mesh);
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

        Assert.Equal(12, snapshot.Lines.Length);
        Assert.All(snapshot.Entities.ToArray(), entity => Assert.Equal(insert.Handle, entity.Handle));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(
                line,
                new CadPoint3D(10, 20, 30),
                new CadPoint3D(10, 23, 34)));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(
                line,
                new CadPoint3D(10, 20, 30),
                new CadPoint3D(12, 20, 34)));
    }

    [Fact]
    public void PolyfaceHiddenEdgesAndTwoVertexFacesFollowPersistedTopology()
    {
        PolyfaceMesh mesh = CreatePolyfaceMesh();
        AddFace(mesh, 1, 2, -3, 0);
        AddFace(mesh, 1, 3, 4, 0);
        AddFace(mesh, 2, 4, 0, 0);
        var document = new CadDocument();
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Equal(6, snapshot.Lines.Length);
        Assert.Equal(2, snapshot.Mesh3DDrawRanges.Length);
        Assert.Equal(6, snapshot.Mesh3DVertices.Length);
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(line, new CadPoint3D(0, 0, 0), new CadPoint3D(2, 0, 0)));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(line, new CadPoint3D(2, 0, 0), new CadPoint3D(2, 1, 1)));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(line, new CadPoint3D(0, 0, 0), new CadPoint3D(2, 1, 1)));
        Assert.Contains(snapshot.Lines.ToArray(), line =>
            HasEndpoints(line, new CadPoint3D(2, 0, 0), new CadPoint3D(0, 1, 1)));
    }

    [Fact]
    public void PolyfacePreservesFaceStyleWhenDeduplicatingSharedEdges()
    {
        var document = new CadDocument();
        var greenLayer = new Layer("GREEN") { Color = ACadSharp.Color.Green };
        var redLayer = new Layer("RED") { Color = ACadSharp.Color.Red };
        document.Layers.Add(greenLayer);
        document.Layers.Add(redLayer);
        PolyfaceMesh mesh = CreatePolyfaceMesh();
        mesh.MatchVerticesEntityProperties = false;
        VertexFaceRecord first = AddFace(mesh, 1, 2, 3, 0);
        first.Layer = greenLayer;
        VertexFaceRecord second = AddFace(mesh, 1, 3, 4, 0);
        second.Layer = redLayer;
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Equal(6, snapshot.Lines.Length);
        CadEntityHeader[] diagonalHeaders = snapshot.Entities.ToArray()
            .Where(header => header.Kind == CadEntityKind.Line)
            .Where(header => HasEndpoints(
                snapshot.Lines.Span[header.PrimitiveIndex],
                new CadPoint3D(0, 0, 0),
                new CadPoint3D(2, 1, 1)))
            .ToArray();
        Assert.Equal(2, diagonalHeaders.Length);
        Assert.Equal(2, diagonalHeaders.Select(header => header.StyleIndex).Distinct().Count());
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
        Assert.Equal(2, scene.Statistics.FaceRangeCount);
        Assert.Equal(2, scene.Statistics.DrawBatchCount);
        Assert.Equal(2, scene.Statistics.TriangleCount);
    }

    [Fact]
    public void PolyfaceReusesSelectionSceneNativeAndPrintPipelines()
    {
        PolyfaceMesh mesh = CreatePolyfaceMesh();
        AddFace(mesh, 1, 2, -3, 0);
        AddFace(mesh, 1, 3, 4, 0);
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add polyface mesh", document => document.Entities.Add(mesh));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
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
        Assert.Equal(5, scene.Statistics.RecordedEntityCount);
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

    [Theory]
    [InlineData("polygon-count")]
    [InlineData("polygon-collapse")]
    [InlineData("polyface-termination")]
    [InlineData("polyface-outside")]
    [InlineData("polyface-collapse")]
    public void MalformedLegacyTopologyIsRejectedTransactionally(string kind)
    {
        var document = new CadDocument();
        switch (kind)
        {
            case "polygon-count":
                {
                    PolygonMesh mesh = CreatePolygonMesh(false, false);
                    mesh.MVertexCount = 2;
                    document.Entities.Add(mesh);
                    break;
                }
            case "polygon-collapse":
                {
                    PolygonMesh mesh = CreatePolygonMesh(false, false);
                    mesh.Vertices[1].Location = mesh.Vertices[0].Location;
                    document.Entities.Add(mesh);
                    break;
                }
            default:
                {
                    PolyfaceMesh mesh = CreatePolyfaceMesh();
                    VertexFaceRecord valid = AddFace(mesh, 1, 2, 3, 0);
                    valid.Color = ACadSharp.Color.Red;
                    switch (kind)
                    {
                        case "polyface-termination":
                            AddFace(mesh, 1, 0, 3, 0);
                            break;
                        case "polyface-outside":
                            AddFace(mesh, 1, 2, 5, 0);
                            break;
                        case "polyface-collapse":
                            AddFace(mesh, 1, 1, 2, 0);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(kind));
                    }
                    document.Entities.Add(mesh);
                    break;
                }
        }

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
    public void FittedLegacySurfacesAndTopologyLimitAreExplicitlyDiagnosed()
    {
        var document = new CadDocument();
        PolygonMesh fitted = CreatePolygonMesh(false, false);
        fitted.SmoothSurface = SmoothSurfaceType.Quadratic;
        document.Entities.Add(fitted);
        document.Entities.Add(CreatePolygonMesh(false, false));
        PolyfaceMesh pointRecord = CreatePolyfaceMesh();
        AddFace(pointRecord, 1, 0, 0, 0);
        document.Entities.Add(pointRecord);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions { MaxMeshFaceIndices = 17 });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(3, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("Fitted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("topology", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LegacyMeshEdgesRespectTheGlobalExpandedEntityLimit()
    {
        var document = new CadDocument();
        document.Entities.Add(CreatePolygonMesh(false, false));

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                new CadDocumentSession(document),
                new CadSnapshotOptions { MaxExpandedEntities = 12 }));

        Assert.Contains("Expanded entity count", exception.Message);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task LegacyMeshesRoundTripThroughAdvertisedFormats(CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(CreatePolygonMesh(false, false));
        PolyfaceMesh polyface = CreatePolyfaceMesh();
        AddFace(polyface, 1, 2, -3, 0);
        AddFace(polyface, 1, 3, 4, 0);
        document.Entities.Add(polyface);
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
            sourceName: $"legacy-mesh.{format.ToString().ToLowerInvariant()}");

        (int polygonCount, int polyfaceCount) = loaded.Session.Read(source =>
            (source.Entities.OfType<PolygonMesh>().Count(),
             source.Entities.OfType<PolyfaceMesh>().Count()));
        Assert.Equal(1, polygonCount);
        Assert.Equal(1, polyfaceCount);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Equal(17, snapshot.Lines.Length);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void LegacyMeshTransformsDuplicateAndRoundTripThroughHistory()
    {
        var document = new CadDocument();
        PolygonMesh polygon = CreatePolygonMesh(false, false);
        polygon.Normal = XYZ.AxisY;
        PolyfaceMesh polyface = CreatePolyfaceMesh();
        polyface.Normal = XYZ.AxisY;
        AddFace(polyface, 1, 2, 3, 0);
        document.Entities.Add(polygon);
        document.Entities.Add(polyface);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        ulong[] handles = [polygon.Handle, polyface.Handle];

        history.Execute(new CadTranslateEntitiesCommand(
            handles,
            new CadPoint3D(10, 20, 30)));
        history.Execute(new CadRotateEntitiesCommand(
            handles,
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));
        history.Execute(new CadScaleEntitiesCommand(handles, 2));
        var duplicate = new CadDuplicateModelSpaceEntityCommand(
            polygon.Handle,
            new CadPoint3D(5, 0, 0));
        history.Execute(duplicate);

        Assert.IsType<PolygonMesh>(duplicate.Duplicate);
        CadDocumentSnapshot transformed = new CadSnapshotCompiler().Compile(session);
        Assert.Equal(27, transformed.Lines.Length);
        Assert.Equal(3, transformed.Statistics.SourceEntityCount);
        Assert.Contains(transformed.Lines.ToArray(), line =>
            HasEndpoint(line, new CadPoint3D(-40, 20, 60)));

        Assert.True(history.TryUndo(out _));
        Assert.Equal(15, new CadSnapshotCompiler().Compile(session).Lines.Length);
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        CadDocumentSnapshot restored = new CadSnapshotCompiler().Compile(session);
        Assert.Contains(restored.Lines.ToArray(), line =>
            HasEndpoint(line, CadPoint3D.Zero));
        Assert.True(history.TryRedo(out _));
    }

    private static PolygonMesh CreatePolygonMesh(bool closeM, bool closeN)
    {
        var mesh = new PolygonMesh
        {
            MVertexCount = 3,
            NVertexCount = 3,
        };
        if (closeM)
        {
            mesh.Flags |= PolylineFlags.ClosedPolylineOrClosedPolygonMeshInM;
        }
        if (closeN)
        {
            mesh.Flags |= PolylineFlags.ClosedPolygonMeshInN;
        }
        for (int m = 0; m < mesh.MVertexCount; m++)
        {
            for (int n = 0; n < mesh.NVertexCount; n++)
            {
                mesh.Vertices.Add(new PolygonMeshVertex(new XYZ(m, n, m + n)));
            }
        }
        return mesh;
    }

    private static PolyfaceMesh CreatePolyfaceMesh()
    {
        var mesh = new PolyfaceMesh();
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(0, 0, 0)));
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(2, 0, 0)));
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(2, 1, 1)));
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(0, 1, 1)));
        return mesh;
    }

    private static VertexFaceRecord AddFace(
        PolyfaceMesh mesh,
        short first,
        short second,
        short third,
        short fourth)
    {
        var face = new VertexFaceRecord
        {
            Index1 = first,
            Index2 = second,
            Index3 = third,
            Index4 = fourth,
        };
        mesh.Faces.Add(face);
        return face;
    }

    private static bool HasEndpoints(
        CadLinePrimitive line,
        CadPoint3D first,
        CadPoint3D second) =>
        (line.Start == first && line.End == second) ||
        (line.Start == second && line.End == first);

    private static bool HasEndpoint(CadLinePrimitive line, CadPoint3D point) =>
        IsNear(line.Start, point) || IsNear(line.End, point);

    private static bool IsNear(CadPoint3D actual, CadPoint3D expected) =>
        Math.Abs(actual.X - expected.X) <= 1e-10 &&
        Math.Abs(actual.Y - expected.Y) <= 1e-10 &&
        Math.Abs(actual.Z - expected.Z) <= 1e-10;
}
