using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLinearCopyTests
{
    [Fact]
    public void IncrementalArrayIsPlacementMajorAndRetainsExactUndoRedoGraphs()
    {
        var document = new CadDocument();
        var layer = new Layer("ARRAY") { Color = ACadSharp.Color.Green };
        document.Layers.Add(layer);
        var line = new Line(new XYZ(1, 2, 0), new XYZ(4, 2, 0))
        {
            Layer = layer,
        };
        var circle = new Circle
        {
            Center = new XYZ(-2, 3, 0),
            Radius = 2,
            Layer = layer,
        };
        document.Entities.Add(line);
        document.Entities.Add(circle);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadLinearCopyModelSpaceEntitiesCommand(
            [line.Handle, circle.Handle, line.Handle],
            new CadPoint3D(5, -2, 0),
            itemCount: 4);

        history.Execute(command);

        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(CadLinearCopyMode.Incremental, command.Mode);
        Assert.Equal(2, command.SourceEntityCount);
        Assert.Equal(4, command.ItemCount);
        Assert.Equal(3, command.PlacementCount);
        Assert.Equal(6, command.DuplicateEntityCount);
        Assert.Equal([line.Handle, circle.Handle], command.SourceHandles.ToArray());
        Assert.Equal(new CadPoint3D(5, -2, 0), command.GetPlacementDisplacement(0));
        Assert.Equal(new CadPoint3D(10, -4, 0), command.GetPlacementDisplacement(1));
        Assert.Equal(new CadPoint3D(15, -6, 0), command.GetPlacementDisplacement(2));
        Assert.All(command.CurrentHandles.ToArray(),
            static handle => Assert.NotEqual(0UL, handle));

        Entity[] duplicates = command.Duplicates.ToArray();
        Assert.Equal(new XYZ(6, 0, 0), Assert.IsType<Line>(duplicates[0]).StartPoint);
        Assert.Equal(new XYZ(3, 1, 0), Assert.IsType<Circle>(duplicates[1]).Center);
        Assert.Equal(new XYZ(11, -2, 0), Assert.IsType<Line>(duplicates[2]).StartPoint);
        Assert.Equal(new XYZ(8, -1, 0), Assert.IsType<Circle>(duplicates[3]).Center);
        Assert.Equal(new XYZ(16, -4, 0), Assert.IsType<Line>(duplicates[4]).StartPoint);
        Assert.Equal(new XYZ(13, -3, 0), Assert.IsType<Circle>(duplicates[5]).Center);
        Assert.All(duplicates, duplicate => Assert.Same(layer, duplicate.Layer));
        Assert.Equal(8, document.Entities.Count);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(picture.CommandCount, native.SourceCommandCount);

        Assert.True(history.TryUndo(out ulong undone));
        Assert.Equal(2UL, undone);
        Assert.Equal(2, document.Entities.Count);
        Assert.All(command.CurrentHandles.ToArray(),
            static handle => Assert.Equal(0UL, handle));
        Assert.All(duplicates, duplicate =>
        {
            Assert.Null(duplicate.Owner);
            Assert.Null(duplicate.Document);
            Assert.Equal(0UL, duplicate.Handle);
        });

        Assert.True(history.TryRedo(out ulong redone));
        Assert.Equal(3UL, redone);
        Assert.Equal(8, document.Entities.Count);
        Assert.Equal(duplicates, command.Duplicates.ToArray());
        Assert.All(command.CurrentHandles.ToArray(),
            static handle => Assert.NotEqual(0UL, handle));
    }

    [Fact]
    public void FitArrayPlacesFinalCopyAtDisplacementAndDistributesIntermediates()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var command = new CadLinearCopyModelSpaceEntitiesCommand(
            [line.Handle],
            new CadPoint3D(12, 6, 3),
            itemCount: 5,
            mode: CadLinearCopyMode.Fit);

        new CadDocumentHistory(session).Execute(command);

        Assert.Equal(new CadPoint3D(3, 1.5, 0.75),
            command.GetPlacementDisplacement(0));
        Assert.Equal(new CadPoint3D(6, 3, 1.5),
            command.GetPlacementDisplacement(1));
        Assert.Equal(new CadPoint3D(9, 4.5, 2.25),
            command.GetPlacementDisplacement(2));
        Assert.Equal(command.Displacement,
            command.GetPlacementDisplacement(3));
        Assert.Equal([
            new XYZ(3, 1.5, 0.75),
            new XYZ(6, 3, 1.5),
            new XYZ(9, 4.5, 2.25),
            new XYZ(12, 6, 3),
        ], command.Duplicates.Span
            .ToArray()
            .Cast<Line>()
            .Select(static duplicate => duplicate.StartPoint)
            .ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            command.GetPlacementDisplacement(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            command.GetPlacementDisplacement(4));
    }

    [Fact]
    public void FiniteZeroDisplacementCreatesAnIntentionalOverlappingArray()
    {
        var document = new CadDocument();
        var line = new Line(new XYZ(2, 3, 4), new XYZ(5, 6, 7));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var command = new CadLinearCopyModelSpaceEntitiesCommand(
            [line.Handle],
            CadPoint3D.Zero,
            itemCount: 3,
            mode: CadLinearCopyMode.Fit);

        new CadDocumentHistory(session).Execute(command);

        Assert.Equal(3, document.Entities.Count);
        Assert.All(
            command.Duplicates.ToArray().Cast<Line>(),
            duplicate =>
            {
                Assert.Equal(line.StartPoint, duplicate.StartPoint);
                Assert.Equal(line.EndPoint, duplicate.EndPoint);
            });
    }

    [Fact]
    public void ArrayValidationAndMissingSourcesCannotPartiallyMutateDocument()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        ulong handle = line.Handle;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidOperationException>(() => history.Execute(
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle, ulong.MaxValue],
                CadPoint3D.Zero,
                2)));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Single(document.Entities);
        Assert.Equal(handle, line.Handle);

        Assert.Throws<ArgumentNullException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                null!, CadPoint3D.Zero, 2));
        Assert.Throws<ArgumentException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [], CadPoint3D.Zero, 2));
        Assert.Throws<ArgumentException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle, 0], CadPoint3D.Zero, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle], CadPoint3D.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle], CadPoint3D.Zero, 2, (CadLinearCopyMode)byte.MaxValue));
        Assert.Throws<ArgumentException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle], new CadPoint3D(double.NaN, 0, 0), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle], new CadPoint3D(double.MaxValue, 0, 0), 3));
        Assert.Throws<ArgumentException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle, handle + 1],
                CadPoint3D.Zero,
                2,
                maximumSourceEntityCount: 1));
        Assert.Throws<ArgumentException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle],
                CadPoint3D.Zero,
                4,
                maximumDuplicateEntityCount: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadLinearCopyModelSpaceEntitiesCommand(
                [handle],
                CadPoint3D.Zero,
                2,
                maximumDuplicateEntityCount: 0));
    }

    [Fact]
    public void TenThousandCopyArrayUsesOneBoundedPlacementMajorBatch()
    {
        var document = new CadDocument();
        var line = new Line(XYZ.Zero, XYZ.AxisX);
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadLinearCopyModelSpaceEntitiesCommand(
            [line.Handle],
            new CadPoint3D(0.25, -0.5, 0),
            itemCount: 10_001);

        history.Execute(command);

        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(10_000, command.PlacementCount);
        Assert.Equal(10_000, command.DuplicateEntityCount);
        Assert.Equal(10_001, document.Entities.Count);
        Assert.Equal(new XYZ(0.25, -0.5, 0),
            Assert.IsType<Line>(command.Duplicates.Span[0]).StartPoint);
        Assert.Equal(new XYZ(2_500, -5_000, 0),
            Assert.IsType<Line>(command.Duplicates.Span[^1]).StartPoint);

        Assert.True(history.TryUndo(out _));
        Assert.Single(document.Entities);
        Assert.True(history.TryRedo(out _));
        Assert.Equal(10_001, document.Entities.Count);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task FitArraySurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        var line = new Line(new XYZ(1, 2, 3), new XYZ(4, 5, 6));
        document.Entities.Add(line);
        var session = new CadDocumentSession(document);
        new CadDocumentHistory(session).Execute(
            new CadLinearCopyModelSpaceEntitiesCommand(
                [line.Handle],
                new CadPoint3D(9, -6, 3),
                itemCount: 4,
                mode: CadLinearCopyMode.Fit));
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
            sourceName: $"linear-copy.{format.ToString().ToLowerInvariant()}");
        XYZ[] starts = loaded.Session.Read(loadedDocument =>
            loadedDocument.Entities
                .OfType<Line>()
                .Select(static candidate => candidate.StartPoint)
                .ToArray());

        Assert.Equal([
            new XYZ(1, 2, 3),
            new XYZ(4, 0, 4),
            new XYZ(7, -2, 5),
            new XYZ(10, -4, 6),
        ], starts);
    }
}
