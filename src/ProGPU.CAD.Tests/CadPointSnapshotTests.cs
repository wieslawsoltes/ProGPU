using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPointSnapshotTests
{
    [Fact]
    public void PointModeZeroUsesWcsAndRetainedHairlinePointPipeline()
    {
        var document = new CadDocument();
        var direct = new Point(new XYZ(0, 0, 0));
        document.Entities.Add(direct);
        var block = new BlockRecord("POINT_BLOCK");
        block.Entities.Add(new Point(new XYZ(1, 2, 3)));
        var insert = new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 30),
            XScale = 2,
            YScale = 3,
            ZScale = 4,
        };
        document.Entities.Add(insert);
        var session = new CadDocumentSession(document);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(2, snapshot.Points.Length);
        Assert.Equal(new CadPoint3D(0, 0, 0), snapshot.Points.Span[0].Position);
        Assert.Equal(new CadPoint3D(12, 26, 42), snapshot.Points.Span[1].Position);
        Assert.Equal(direct.Handle, snapshot.Entities.Span[0].Handle);
        Assert.Equal(insert.Handle, snapshot.Entities.Span[1].Handle);
        Assert.All(snapshot.Entities.ToArray(), entity =>
            Assert.Equal(CadEntityKind.Point, entity.Kind));
        Assert.Equal(2, scene.Statistics.RecordedEntityCount);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
        Assert.Equal(2, scene.DrawingContext.PointBuffer.Count);
        Assert.All(scene.DrawingContext.Commands, command =>
        {
            Assert.Equal(RenderCommandType.DrawPointBatch, command.Type);
            Assert.Null(command.PolylinePoints);
            Assert.Equal(1, command.PointBufferCount);
            Assert.Equal(0f, command.RadiusX);
            Assert.Equal(1, command.IntParam);
        });

        CadEntityHeader secondHeader = snapshot.Entities.Span[1];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            1,
            secondHeader.Handle,
            secondHeader.Kind,
            secondHeader.Bounds);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(12.1, 26, 42),
                tolerance: 0.1).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(12, 26, 42),
                    new CadPoint3D(12, 26, 42)),
                CadBoundsSelectionMode.Window).Status);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
        Assert.Equal(2, nativePicture.PointBatchCount);
        Assert.Equal(2, nativePicture.PointCount);
        using CadPrintPlan printPlan = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(2, printPlan.SceneStatistics.RecordedEntityCount);
    }

    [Fact]
    public void PointModeOneHidesPointsWithoutUnsupportedDiagnostic()
    {
        var document = new CadDocument();
        document.Header.PointDisplayMode = 1;
        document.Entities.Add(new Point(new XYZ(2, 3, 4)));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Points.ToArray());
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void ViewportSizedPointModesAndThicknessRemainExplicitlyUnsupported()
    {
        var markerDocument = new CadDocument();
        markerDocument.Header.PointDisplayMode = 2;
        markerDocument.Entities.Add(new Point(new XYZ(1, 2, 3)));
        var thicknessDocument = new CadDocument();
        thicknessDocument.Entities.Add(new Point(new XYZ(1, 2, 3))
        {
            Thickness = 1,
        });

        CadDocumentSnapshot marker = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(markerDocument));
        CadDocumentSnapshot thickness = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(thicknessDocument));

        Assert.Empty(marker.Entities.ToArray());
        Assert.Equal(1, marker.Statistics.UnsupportedEntityCount);
        Assert.Contains(marker.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("PDMODE 2", StringComparison.Ordinal));
        Assert.Empty(thickness.Entities.ToArray());
        Assert.Equal(1, thickness.Statistics.UnsupportedEntityCount);
        Assert.Contains(thickness.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("thickness", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OneVertexPolyfaceRecordsEmitStyledPointsTransactionally()
    {
        var document = new CadDocument();
        PolyfaceMesh mesh = CreatePolyface();
        AddFace(mesh, 1, 2, 0, 0);
        AddFace(mesh, 1, 0, 0, 0);
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Single(snapshot.Lines.ToArray());
        CadPointPrimitive point = Assert.Single(snapshot.Points.ToArray());
        Assert.Equal(new CadPoint3D(0, 0, 0), point.Position);
        Assert.Equal(2, snapshot.Entities.Length);
        Assert.All(snapshot.Entities.ToArray(), entity =>
            Assert.Equal(mesh.Handle, entity.Handle));
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);

        var unsupportedDocument = new CadDocument();
        unsupportedDocument.Header.PointDisplayMode = 3;
        PolyfaceMesh unsupportedMesh = CreatePolyface();
        AddFace(unsupportedMesh, 1, 2, 0, 0);
        AddFace(unsupportedMesh, 1, 0, 0, 0);
        unsupportedDocument.Entities.Add(unsupportedMesh);
        CadDocumentSnapshot unsupported = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(unsupportedDocument));
        Assert.Empty(unsupported.Entities.ToArray());
        Assert.Empty(unsupported.Lines.ToArray());
        Assert.Empty(unsupported.Points.ToArray());
        Assert.Equal(1, unsupported.Statistics.UnsupportedEntityCount);

        var hiddenDocument = new CadDocument();
        hiddenDocument.Header.PointDisplayMode = 1;
        PolyfaceMesh hiddenMesh = CreatePolyface();
        AddFace(hiddenMesh, 1, 2, 0, 0);
        AddFace(hiddenMesh, 1, 0, 0, 0);
        hiddenDocument.Entities.Add(hiddenMesh);
        CadDocumentSnapshot hidden = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(hiddenDocument));
        Assert.Single(hidden.Lines.ToArray());
        Assert.Empty(hidden.Points.ToArray());
        Assert.Equal(0, hidden.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void SingleCoordinatePolyfacePointRecordIsValid()
    {
        var document = new CadDocument();
        var mesh = new PolyfaceMesh();
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(5, 7, 11)));
        AddFace(mesh, 1, 0, 0, 0);
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Equal(
            new CadPoint3D(5, 7, 11),
            Assert.Single(snapshot.Points.ToArray()).Position);
        Assert.Equal(mesh.Handle, Assert.Single(snapshot.Entities.ToArray()).Handle);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task PointsRoundTripThroughAdvertisedFormats(CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(new Point(new XYZ(7, 11, 13)));
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
            sourceName: $"point.{format.ToString().ToLowerInvariant()}");

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Equal(new CadPoint3D(7, 11, 13), Assert.Single(snapshot.Points.ToArray()).Position);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void PointTransformsDuplicateAndRoundTripThroughHistory()
    {
        var document = new CadDocument();
        var point = new Point(new XYZ(1, 0, 0));
        document.Entities.Add(point);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateEntitiesCommand(
            [point.Handle],
            new CadPoint3D(1, 2, 3)));
        history.Execute(new CadRotateEntitiesCommand(
            [point.Handle],
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));
        history.Execute(new CadScaleEntitiesCommand([point.Handle], 2));
        var duplicate = new CadDuplicateModelSpaceEntityCommand(
            point.Handle,
            new CadPoint3D(1, 0, 0));
        history.Execute(duplicate);

        Assert.IsType<Point>(duplicate.Duplicate);
        CadPointPrimitive[] transformed = new CadSnapshotCompiler()
            .Compile(session)
            .Points
            .ToArray();
        Assert.Contains(transformed, value =>
            IsNear(value.Position, new CadPoint3D(-4, 4, 6)));
        Assert.Contains(transformed, value =>
            IsNear(value.Position, new CadPoint3D(-3, 4, 6)));
        Assert.True(history.TryUndo(out _));
        Assert.Single(new CadSnapshotCompiler().Compile(session).Points.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        Assert.True(IsNear(
            Assert.Single(new CadSnapshotCompiler().Compile(session).Points.ToArray()).Position,
            new CadPoint3D(1, 0, 0)));
        Assert.True(history.TryRedo(out _));
    }

    private static PolyfaceMesh CreatePolyface()
    {
        var mesh = new PolyfaceMesh();
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(0, 0, 0)));
        mesh.Vertices.Add(new VertexFaceMesh(new XYZ(2, 0, 0)));
        return mesh;
    }

    private static void AddFace(
        PolyfaceMesh mesh,
        short first,
        short second,
        short third,
        short fourth) =>
        mesh.Faces.Add(new VertexFaceRecord
        {
            Index1 = first,
            Index2 = second,
            Index3 = third,
            Index4 = fourth,
        });

    private static bool IsNear(CadPoint3D actual, CadPoint3D expected) =>
        Math.Abs(actual.X - expected.X) <= 1e-10 &&
        Math.Abs(actual.Y - expected.Y) <= 1e-10 &&
        Math.Abs(actual.Z - expected.Z) <= 1e-10;
}
