using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
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
    public void ViewportSizedPointModesAreRetainedWhileThicknessRemainsUnsupported()
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

        CadPointPrimitive retained = Assert.Single(marker.Points.ToArray());
        Assert.Equal((short)2, retained.DisplayMode);
        Assert.Equal(0.0, retained.DisplaySize);
        Assert.Equal(0, marker.Statistics.UnsupportedEntityCount);
        CadRecordedPlanScene plan = new CadPlanSceneCompiler().Compile(marker);
        Assert.Empty(plan.DrawingContext.Commands);
        Assert.Contains(plan.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSCENE005");
        CadRecordedPointMarkerScene pointScene =
            new CadPointMarkerSceneCompiler().Compile(
                marker,
                new CadPointMarkerView(200.0f, 0.5));
        Assert.Equal(1, pointScene.Statistics.RecordedPointCount);
        Assert.Equal(1, pointScene.Statistics.RecordedCommandCount);
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

        var markerDocument = new CadDocument();
        markerDocument.Header.PointDisplayMode = 3;
        PolyfaceMesh markerMesh = CreatePolyface();
        AddFace(markerMesh, 1, 2, 0, 0);
        AddFace(markerMesh, 1, 0, 0, 0);
        markerDocument.Entities.Add(markerMesh);
        CadDocumentSnapshot marker = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(markerDocument));
        Assert.Single(marker.Lines.ToArray());
        Assert.Single(marker.Points.ToArray());
        Assert.Equal(0, marker.Statistics.UnsupportedEntityCount);

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
        document.Header.PointDisplayMode = 98;
        document.Header.PointDisplaySize = -7.5;
        document.Entities.Add(new Point(new XYZ(7, 11, 13))
        {
            Rotation = Math.PI / 3.0,
        });
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
        CadPointPrimitive point = Assert.Single(snapshot.Points.ToArray());
        Assert.Equal(new CadPoint3D(7, 11, 13), point.Position);
        Assert.Equal((short)98, point.DisplayMode);
        Assert.Equal(-7.5, point.DisplaySize);
        Assert.True(IsNear(
            point.MarkerXAxis,
            new CadPoint3D(0.5, Math.Sqrt(3.0) * 0.5, 0.0)));
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
        Assert.All(transformed, value => Assert.True(IsNear(
            value.MarkerXAxis,
            new CadPoint3D(0, 1, 0))));
        Assert.True(history.TryUndo(out _));
        Assert.Single(new CadSnapshotCompiler().Compile(session).Points.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        Assert.True(history.TryUndo(out _));
        CadPointPrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Points.ToArray());
        Assert.True(IsNear(restored.Position, new CadPoint3D(1, 0, 0)));
        Assert.True(IsNear(restored.MarkerXAxis, new CadPoint3D(1, 0, 0)));
        Assert.True(history.TryRedo(out _));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    [InlineData(32, 1)]
    [InlineData(33, 1)]
    [InlineData(34, 3)]
    [InlineData(35, 3)]
    [InlineData(36, 2)]
    [InlineData(64, 1)]
    [InlineData(65, 1)]
    [InlineData(66, 3)]
    [InlineData(67, 3)]
    [InlineData(68, 2)]
    [InlineData(96, 2)]
    [InlineData(97, 2)]
    [InlineData(98, 4)]
    [InlineData(99, 4)]
    [InlineData(100, 3)]
    public void DocumentedPointMarkerCombinationsUseBoundedAnalyticCommands(
        short displayMode,
        int expectedFigures)
    {
        CadDocumentSnapshot snapshot = CreateMarkerSnapshot(
            displayMode,
            displaySize: -5.0,
            rotation: Math.PI / 2.0);
        CadRecordedPointMarkerScene scene =
            new CadPointMarkerSceneCompiler().Compile(
                snapshot,
                new CadPointMarkerView(200.0f, 0.5));

        Assert.Equal(1, scene.Statistics.RecordedPointCount);
        Assert.Equal(1, scene.Statistics.RecordedCommandCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(expectedFigures, command.Path!.Figures.Count);
    }

    [Fact]
    public void RelativeAndAbsolutePointSizesResolveAgainstTheExplicitView()
    {
        AssertMarkerHalfExtent(displaySize: 0.0, expected: 2.5f);
        AssertMarkerHalfExtent(displaySize: -10.0, expected: 5.0f);
        AssertMarkerHalfExtent(displaySize: 8.0, expected: 4.0f);
    }

    [Fact]
    public void RotatedCompoundPointMarkerReplaysThroughNativePictureAndPrint()
    {
        CadDocumentSnapshot snapshot = CreateMarkerSnapshot(
            displayMode: 98,
            displaySize: -10.0,
            rotation: Math.PI / 2.0);
        CadRecordedPointMarkerScene scene =
            new CadPointMarkerSceneCompiler().Compile(
                snapshot,
                new CadPointMarkerView(200.0f, 0.5));

        Assert.Equal(
            [RenderCommandType.DrawPath],
            scene.DrawingContext.Commands.Select(command => command.Type).ToArray());
        PathFigure first = scene.DrawingContext.Commands[0].Path!.Figures[0];
        AssertVectorNear(new Vector2(0.0f, -5.0f), first.StartPoint);
        AssertVectorNear(
            new Vector2(0.0f, 5.0f),
            Assert.IsType<LineSegment>(Assert.Single(first.Segments)).Point);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            97U,
            1U,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
        Assert.Equal(1, nativePicture.SourceCommandCount);
        Assert.True(nativePicture.NativeDrawCount > 0);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.True(print.SceneStatistics.RecordedCommandCount >= 1);
        using GpuPicture page = print.CreatePagePicture();
        Assert.NotNull(page);
    }

    [Fact]
    public void SameStyleMarkersCoalesceAndShearedCircleUsesExactEllipseFallback()
    {
        var coalescedDocument = new CadDocument();
        coalescedDocument.Header.PointDisplayMode = 98;
        coalescedDocument.Header.PointDisplaySize = 4.0;
        coalescedDocument.Entities.Add(new Point(new XYZ(0, 0, 0)));
        coalescedDocument.Entities.Add(new Point(new XYZ(10, 0, 0)));
        CadDocumentSnapshot coalescedSnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(coalescedDocument));
        CadRecordedPointMarkerScene coalesced =
            new CadPointMarkerSceneCompiler().Compile(
                coalescedSnapshot,
                new CadPointMarkerView(100.0f, 1.0));
        RenderCommand coalescedCommand = Assert.Single(
            coalesced.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, coalescedCommand.Type);
        Assert.Equal(8, coalescedCommand.Path!.Figures.Count);

        var shearedDocument = new CadDocument();
        shearedDocument.Header.PointDisplayMode = 32;
        shearedDocument.Header.PointDisplaySize = 4.0;
        var block = new BlockRecord("SHEARED_POINT");
        block.Entities.Add(new Point(new XYZ(0, 0, 0))
        {
            Rotation = Math.PI / 4.0,
        });
        shearedDocument.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 3.0,
        });
        CadDocumentSnapshot shearedSnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(shearedDocument));
        CadRecordedPointMarkerScene sheared =
            new CadPointMarkerSceneCompiler().Compile(
                shearedSnapshot,
                new CadPointMarkerView(100.0f, 1.0));
        RenderCommand ellipse = Assert.Single(sheared.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawEllipse, ellipse.Type);
        Assert.NotEqual(0.0f, ellipse.Transform.M12);
        Assert.NotEqual(0.0f, ellipse.Transform.M21);

        using GpuPicture picture = sheared.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            98U,
            1U,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(31)]
    [InlineData(101)]
    [InlineData(128)]
    public void UndocumentedPointModesFailTransactionally(short displayMode)
    {
        var document = new CadDocument();
        document.Header.PointDisplayMode = displayMode;
        document.Entities.Add(new Point(new XYZ(1, 2, 3)));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Points.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("PDMODE", StringComparison.Ordinal));
    }

    private static CadDocumentSnapshot CreateMarkerSnapshot(
        short displayMode,
        double displaySize,
        double rotation)
    {
        var document = new CadDocument();
        document.Header.PointDisplayMode = displayMode;
        document.Header.PointDisplaySize = displaySize;
        document.Entities.Add(new Point(new XYZ(10, 20, 0))
        {
            Rotation = rotation,
        });
        return new CadSnapshotCompiler().Compile(new CadDocumentSession(document));
    }

    private static void AssertMarkerHalfExtent(
        double displaySize,
        float expected)
    {
        CadDocumentSnapshot snapshot = CreateMarkerSnapshot(
            displayMode: 2,
            displaySize,
            rotation: 0.0);
        CadRecordedPointMarkerScene scene =
            new CadPointMarkerSceneCompiler().Compile(
                snapshot,
                new CadPointMarkerView(200.0f, 0.5));
        PathFigure horizontal = scene.DrawingContext.Commands[0].Path!.Figures[0];
        AssertVectorNear(new Vector2(-expected, 0.0f), horizontal.StartPoint);
        AssertVectorNear(
            new Vector2(expected, 0.0f),
            Assert.IsType<LineSegment>(Assert.Single(horizontal.Segments)).Point);
    }

    private static void AssertVectorNear(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0.0f, 1e-5f);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0f, 1e-5f);
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
