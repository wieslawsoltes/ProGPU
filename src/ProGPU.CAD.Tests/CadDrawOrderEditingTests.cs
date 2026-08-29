using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Objects;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadDrawOrderEditingTests
{
    [Theory]
    [InlineData(CadDrawOrderPlacement.BringToFront)]
    [InlineData(CadDrawOrderPlacement.SendToBack)]
    public void FrontAndBackPreserveEffectiveSelectionOrderThroughHistory(
        CadDrawOrderPlacement placement)
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(5);
        var history = new CadDocumentHistory(session);
        var command = new CadSetModelSpaceDrawOrderCommand(
            [lines[3].Handle, lines[1].Handle],
            placement);

        ulong appliedGeneration = history.Execute(command);

        double[] expected = placement == CadDrawOrderPlacement.BringToFront
            ? [10, 30, 50, 20, 40]
            : [20, 40, 10, 30, 50];
        Assert.Equal(1UL, appliedGeneration);
        Assert.Equal(expected, SnapshotStartXs(session));
        Assert.Equal(2, command.Handles.Length);
        Assert.Empty(command.ReferenceHandles.ToArray());
        Assert.NotNull(session.Read(document =>
            document.ModelSpace.SortEntitiesTable));

        Assert.True(history.TryUndo(out ulong undoGeneration));
        Assert.Equal(2UL, undoGeneration);
        Assert.Equal(new double[] { 10, 20, 30, 40, 50 },
            SnapshotStartXs(session));
        Assert.Null(session.Read(document =>
            document.ModelSpace.SortEntitiesTable));

        Assert.True(history.TryRedo(out ulong redoGeneration));
        Assert.Equal(3UL, redoGeneration);
        Assert.Equal(expected, SnapshotStartXs(session));
    }

    [Theory]
    [InlineData(CadDrawOrderPlacement.BringAbove)]
    [InlineData(CadDrawOrderPlacement.SendUnder)]
    public void AboveAndUnderUseFrontmostOrBackmostReferenceBoundary(
        CadDrawOrderPlacement placement)
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(5);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            [lines[3].Handle, lines[1].Handle],
            placement,
            [lines[2].Handle, lines[0].Handle]));

        double[] expected = placement == CadDrawOrderPlacement.BringAbove
            ? [10, 30, 20, 40, 50]
            : [20, 40, 10, 30, 50];
        Assert.Equal(expected, SnapshotStartXs(session));
        Assert.True(history.TryUndo(out _));
        Assert.Equal(new double[] { 10, 20, 30, 40, 50 },
            SnapshotStartXs(session));
    }

    [Fact]
    public void UndoRestoresExactPriorSparsePairs()
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(3);
        session.Edit("Add sparse order", document =>
        {
            SortEntitiesTable table =
                document.ModelSpace.CreateSortEntitiesTable();
            table.Add(lines[1], 0);
        });
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            [lines[0].Handle],
            CadDrawOrderPlacement.BringToFront));
        SortEntitiesTable applied = session.Read(document =>
            document.ModelSpace.SortEntitiesTable!);
        Assert.Equal(3, applied.Count());

        Assert.True(history.TryUndo(out _));

        SortEntitiesTable restored = session.Read(document =>
            document.ModelSpace.SortEntitiesTable!);
        SortEntitiesTable.Sorter sorter = Assert.Single(restored.ToArray());
        Assert.Same(lines[1], sorter.Entity);
        Assert.Equal(0UL, sorter.SortHandle);
        Assert.Equal(new double[] { 20, 10, 30 }, SnapshotStartXs(session));
    }

    [Fact]
    public void EditPreservesExplicitPlottingPolicy()
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(3);
        session.Edit("Disable plot sorting", document =>
            document.Header.EntitySortingFlags = ObjectSortingFlags.Disabled);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            [lines[0].Handle],
            CadDrawOrderPlacement.BringToFront));

        Assert.Equal(
            ObjectSortingFlags.Disabled,
            session.Read(document => document.Header.EntitySortingFlags));
        CadDocumentSnapshot regeneration = new CadSnapshotCompiler().Compile(session);
        CadDocumentSnapshot plotting = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            });
        Assert.False(regeneration.IsPlotOrderCompatible);
        Assert.Equal(new double[] { 20, 30, 10 },
            regeneration.Lines.ToArray().Select(line => line.Start.X));
        Assert.Equal(new double[] { 10, 20, 30 },
            plotting.Lines.ToArray().Select(line => line.Start.X));
    }

    [Fact]
    public void MalformedPriorTableFailsBeforeMutationOrHistoryPublication()
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(2);
        session.Edit("Add malformed sort table", document =>
        {
            SortEntitiesTable table =
                document.ModelSpace.CreateSortEntitiesTable();
            table.Add(lines[0], 1);
            table.Add(lines[0], 2);
        });
        var history = new CadDocumentHistory(session);

        Assert.Throws<InvalidDataException>(() =>
            history.Execute(new CadSetModelSpaceDrawOrderCommand(
                [lines[1].Handle],
                CadDrawOrderPlacement.BringToFront)));

        Assert.Equal(1UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(2, session.Read(document =>
            document.ModelSpace.SortEntitiesTable!.Count()));
    }

    [Fact]
    public void EditedOrderFeedsManagedCommandsAndOrderedNativePacking()
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(3);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            [lines[0].Handle],
            CadDrawOrderPlacement.BringToFront));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();

        Assert.Equal(new double[] { 20, 30, 10 },
            snapshot.Lines.ToArray().Select(line => line.Start.X));
        Assert.Equal(
            snapshot.Lines.ToArray().Select(line =>
                checked((float)(line.Start.X - snapshot.RebaseOrigin.X))),
            commands.Select(command => command.Position.X));
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(3, native.SourceCommandCount);
        Assert.Equal(1, native.NativeDrawCount);
        Assert.Equal(3, native.GeometryPrimitiveCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task EditedOrderSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        (CadDocumentSession session, Line[] lines) = CreateLineSession(5);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            [lines[4].Handle, lines[3].Handle],
            CadDrawOrderPlacement.SendToBack));
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
            sourceName: $"edited-order.{format.ToString().ToLowerInvariant()}");

        Assert.Equal(new double[] { 40, 50, 10, 20, 30 },
            SnapshotStartXs(loaded.Session));
    }

    [Fact]
    public void InvalidSetsAndLimitsFailBeforePublishingAnEdit()
    {
        Assert.Throws<ArgumentException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                Array.Empty<ulong>(),
                CadDrawOrderPlacement.BringToFront));
        Assert.Throws<ArgumentException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                [1UL],
                CadDrawOrderPlacement.BringAbove));
        Assert.Throws<ArgumentException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                [1UL],
                CadDrawOrderPlacement.SendToBack,
                [2UL]));
        Assert.Throws<ArgumentException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                [1UL],
                CadDrawOrderPlacement.SendUnder,
                [1UL]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                [1UL],
                (CadDrawOrderPlacement)byte.MaxValue));
        Assert.Throws<ArgumentException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                [1UL, 2UL],
                CadDrawOrderPlacement.BringToFront,
                maximumSelectionCount: 1));
        Assert.Throws<ArgumentException>(() =>
            new CadSetModelSpaceDrawOrderCommand(
                [1UL],
                CadDrawOrderPlacement.BringAbove,
                [2UL],
                maximumSelectionCount: 1));

        (CadDocumentSession session, Line[] lines) = CreateLineSession(2);
        var missingHistory = new CadDocumentHistory(session);
        Assert.Throws<InvalidOperationException>(() =>
            missingHistory.Execute(new CadSetModelSpaceDrawOrderCommand(
                [ulong.MaxValue],
                CadDrawOrderPlacement.BringToFront)));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, missingHistory.UndoCount);

        var limitedHistory = new CadDocumentHistory(session);
        Assert.Throws<InvalidOperationException>(() =>
            limitedHistory.Execute(new CadSetModelSpaceDrawOrderCommand(
                [lines[0].Handle],
                CadDrawOrderPlacement.BringToFront,
                maximumModelSpaceEntityCount: 1)));
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, limitedHistory.UndoCount);
        Assert.Null(session.Read(document =>
            document.ModelSpace.SortEntitiesTable));
    }

    private static (CadDocumentSession Session, Line[] Lines) CreateLineSession(
        int count)
    {
        var document = new CadDocument();
        document.Header.EntitySortingFlags = ObjectSortingFlags.All;
        var lines = new Line[count];
        for (int i = 0; i < count; i++)
        {
            double x = (i + 1) * 10.0;
            lines[i] = new Line(new XYZ(x, 0, 0), new XYZ(x + 1, 0, 0));
            document.Entities.Add(lines[i]);
        }
        return (new CadDocumentSession(document), lines);
    }

    private static double[] SnapshotStartXs(CadDocumentSession session) =>
        new CadSnapshotCompiler()
            .Compile(session)
            .Lines
            .ToArray()
            .Select(line => line.Start.X)
            .ToArray();
}
