using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadDrawOrderTests
{
    [Fact]
    public void RegenerationResolvesSparseModelOrderAndPreservesSceneCommandOrder()
    {
        CadDocumentSession session = CreateOrderedLineSession(
            ObjectSortingFlags.All);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadLinePrimitive[] lines = snapshot.Lines.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();

        Assert.Equal(CadDrawOrderPurpose.Regeneration, snapshot.DrawOrderPurpose);
        Assert.True(snapshot.HasDrawOrderOverrides);
        Assert.True(snapshot.IsPlotOrderCompatible);
        Assert.Equal(new[] { 20.0, 30.0, 10.0 }, lines.Select(line => line.Start.X));
        Assert.Equal(3, commands.Length);
        Assert.All(commands, command =>
            Assert.Equal(RenderCommandType.DrawLine, command.Type));
        Assert.Equal(
            lines.Select(line => checked((float)(line.Start.X - snapshot.RebaseOrigin.X))),
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

    [Fact]
    public void NestedBlockOrderIsCachedAndRemainsOneContiguousInsertUnit()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        ulong standaloneHandle = 0;
        session.Edit("Add ordered block", document =>
        {
            document.Header.EntitySortingFlags = ObjectSortingFlags.All;
            var block = new BlockRecord("ORDERED_BLOCK");
            var first = new Line(new XYZ(100, 0, 0), new XYZ(101, 0, 0));
            var second = new Line(new XYZ(200, 0, 0), new XYZ(201, 0, 0));
            block.Entities.Add(first);
            block.Entities.Add(second);
            SortEntitiesTable blockOrder = block.CreateSortEntitiesTable();
            blockOrder.Add(first, 20);
            blockOrder.Add(second, 10);
            document.BlockRecords.Add(block);

            var insert = new Insert(block)
            {
                ColumnCount = 2,
                ColumnSpacing = 1_000,
            };
            var standalone = new Line(XYZ.Zero, new XYZ(1, 0, 0));
            document.Entities.Add(insert);
            document.Entities.Add(standalone);
            insertHandle = insert.Handle;
            standaloneHandle = standalone.Handle;
            SortEntitiesTable modelOrder = document.ModelSpace.CreateSortEntitiesTable();
            modelOrder.Add(insert, 20);
            modelOrder.Add(standalone, 10);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Equal(new[] { 0.0, 200.0, 100.0, 1_200.0, 1_100.0 },
            snapshot.Lines.ToArray().Select(line => line.Start.X));
        Assert.Equal(
            new[]
            {
                standaloneHandle,
                insertHandle,
                insertHandle,
                insertHandle,
                insertHandle,
            },
            snapshot.Entities.ToArray().Select(entity => entity.Handle));
    }

    [Fact]
    public void PlottingPurposeHonorsSortentsPlotFlagAndRejectsIncompatibleSnapshot()
    {
        CadDocumentSession session = CreateOrderedLineSession(
            ObjectSortingFlags.Disabled);
        var compiler = new CadSnapshotCompiler();
        CadDocumentSnapshot regeneration = compiler.Compile(session);

        Assert.False(regeneration.IsPlotOrderCompatible);
        Assert.Throws<InvalidOperationException>(() =>
            new CadPrintPlanCompiler().Compile(regeneration));

        CadDocumentSnapshot plotting = compiler.Compile(
            session,
            new CadSnapshotOptions
            {
                DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            });

        Assert.Equal(CadDrawOrderPurpose.Plotting, plotting.DrawOrderPurpose);
        Assert.True(plotting.HasDrawOrderOverrides);
        Assert.True(plotting.IsPlotOrderCompatible);
        Assert.Equal(new[] { 10.0, 20.0, 30.0 },
            plotting.Lines.ToArray().Select(line => line.Start.X));
        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(plotting);
        Assert.Equal(3, plan.SceneStatistics.RecordedEntityCount);
    }

    [Fact]
    public void DuplicateSorterEntryFailsBeforePublishingAnySnapshot()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add malformed draw order", document =>
        {
            var line = new Line(XYZ.Zero, new XYZ(1, 0, 0));
            document.Entities.Add(line);
            SortEntitiesTable order = document.ModelSpace.CreateSortEntitiesTable();
            order.Add(line, 10);
            order.Add(line, 20);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            new CadSnapshotCompiler().Compile(session));

        Assert.Contains("duplicate entries", error.Message, StringComparison.Ordinal);
        Assert.Equal(1UL, session.ContentGeneration);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task PersistedModelOrderSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CreateOrderedLineSession(
            ObjectSortingFlags.All);
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
            sourceName: $"draw-order.{format.ToString().ToLowerInvariant()}");
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);

        Assert.True(snapshot.HasDrawOrderOverrides);
        Assert.Equal(new[] { 20.0, 30.0, 10.0 },
            snapshot.Lines.ToArray().Select(line => line.Start.X));
    }

    private static CadDocumentSession CreateOrderedLineSession(
        ObjectSortingFlags sortingFlags)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add ordered lines", document =>
        {
            document.Header.EntitySortingFlags = sortingFlags;
            var first = new Line(new XYZ(10, 0, 0), new XYZ(11, 0, 0));
            var second = new Line(new XYZ(20, 0, 0), new XYZ(21, 0, 0));
            var third = new Line(new XYZ(30, 0, 0), new XYZ(31, 0, 0));
            document.Entities.Add(first);
            document.Entities.Add(second);
            document.Entities.Add(third);
            SortEntitiesTable order = document.ModelSpace.CreateSortEntitiesTable();
            order.Add(first, 30);
            order.Add(second, 10);
            order.Add(third, 20);
        });
        return session;
    }
}
