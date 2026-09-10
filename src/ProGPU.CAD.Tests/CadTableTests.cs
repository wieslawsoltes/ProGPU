using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadTableTests
{
    private static readonly TtfFont Font = InterFontFamily.Regular;

    [Fact]
    public void PersistedCacheReusesSelectionSceneNativeAndPrintPipelines()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        ulong tableHandle = 0;
        session.Edit("Add retained table cache", document =>
        {
            TextStyle textStyle = AddTextStyle(document);
            BlockRecord cache = CreateTableCache(textStyle);
            document.BlockRecords.Add(cache);
            var table = new TableEntity(cache)
            {
                InsertPoint = new XYZ(10, 20, 0),
                Rotation = Math.PI / 2.0,
                XScale = 2,
                YScale = 3,
                HorizontalDirection = XYZ.AxisY,
            };
            table.Columns.Add(new TableEntity.Column { Name = "Value", Width = 4 });
            table.Rows.Add(new TableEntity.Row
            {
                Height = 3,
                Cells = { new TableEntity.Cell() },
            });
            document.Entities.Add(table);
            tableHandle = table.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader[] entities = snapshot.Entities.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(7, entities.Length);
        Assert.Equal(5, snapshot.Lines.Length);
        Assert.Single(snapshot.Faces.ToArray());
        Assert.Single(snapshot.MTexts.ToArray());
        Assert.Equal(8, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.All(entities, entity => Assert.Equal(tableHandle, entity.Handle));
        CadLinePrimitive firstLine = snapshot.Lines.Span[0];
        AssertPoint(new CadPoint3D(10, 20, 0), firstLine.Start);
        AssertPoint(new CadPoint3D(10, 28, 0), firstLine.End);

        int entityCount = entities.Length;
        var entityScratch = new int[entityCount];
        var candidates = new CadSelectionCandidate[entityCount];
        var matches = new CadSelectionCandidate[entityCount];
        var hashScratch = new int[
            CadSelectionQuery.GetUniqueHandleScratchLength(entityCount)];
        var handles = new ulong[entityCount];
        CadBoundsSelectionQueryResult selection = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            handles);

        Assert.Equal(entityCount, selection.MatchedPrimitiveCount);
        Assert.Equal(0, selection.UnsupportedPrimitiveCount);
        Assert.Equal(1, selection.HandleTotalCount);
        Assert.Equal(tableHandle, handles[0]);

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
        using GpuPicture page = printPlan.CreatePagePicture();
        Assert.Equal(entities.Length, printPlan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(scene.DrawingContext.Commands.Count, page.GetCommand(1).Picture!.CommandCount);
    }

    [Fact]
    public void ParentInsertAndTableAffineTransformsComposeOnce()
    {
        var document = new CadDocument();
        var cache = new BlockRecord("*T2") { IsAnonymous = true };
        cache.Entities.Add(new Line(XYZ.Zero, new XYZ(2, 0, 0)));
        var table = new TableEntity(cache)
        {
            InsertPoint = new XYZ(1, 2, 0),
            XScale = 3,
            YScale = 4,
        };
        var assembly = new BlockRecord("TABLE_ASSEMBLY");
        assembly.Entities.Add(table);
        var outer = new Insert(assembly)
        {
            InsertPoint = new XYZ(10, 20, 0),
            Rotation = Math.PI / 2.0,
            XScale = 2,
            YScale = 2,
        };
        document.Entities.Add(outer);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadLinePrimitive line = Assert.Single(snapshot.Lines.ToArray());
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());

        AssertPoint(new CadPoint3D(6, 22, 0), line.Start);
        AssertPoint(new CadPoint3D(6, 34, 0), line.End);
        Assert.Equal(outer.Handle, header.Handle);
        Assert.Equal(3, snapshot.Statistics.ExpandedEntityCount);
    }

    [Fact]
    public void MissingEmptyAndXrefCachesFailClosed()
    {
        var document = new CadDocument();
        document.Entities.Add(new TableEntity());
        document.Entities.Add(new TableEntity(
            new BlockRecord("*T_EMPTY") { IsAnonymous = true }));
        var xref = new BlockRecord("*T_XREF") { IsAnonymous = true };
        xref.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        xref.Flags |= BlockTypeFlags.XRef;
        document.Entities.Add(new TableEntity(xref));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(2, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("no persisted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("external-reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PersistedCacheRoundTripsThroughDwgWithoutRegeneration()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add DWG table cache", document =>
        {
            var cache = new BlockRecord("*T3") { IsAnonymous = true };
            cache.Entities.Add(new Line(
                new XYZ(1, 2, 0),
                new XYZ(5, 2, 0)));
            document.BlockRecords.Add(cache);
            document.Entities.Add(new TableEntity(cache)
            {
                InsertPoint = new XYZ(10, 20, 0),
                HorizontalDirection = XYZ.AxisY,
            });
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dwg,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dwg,
            sourceName: "table.dwg");

        TableEntity restored = loaded.Session.Read(document =>
            Assert.IsType<TableEntity>(Assert.Single(document.Entities)));
        Assert.Equal(XYZ.AxisY, restored.HorizontalDirection);
        Assert.NotNull(restored.Block);
        Assert.StartsWith("*T", restored.Block.Name);
        CadLinePrimitive line = Assert.Single(
            new CadSnapshotCompiler().Compile(loaded.Session).Lines.ToArray());
        AssertPoint(new CadPoint3D(11, 22, 0), line.Start);
        AssertPoint(new CadPoint3D(15, 22, 0), line.End);
    }

    private static BlockRecord CreateTableCache(TextStyle textStyle)
    {
        var cache = new BlockRecord("*T1") { IsAnonymous = true };
        cache.Entities.Add(new Line(XYZ.Zero, new XYZ(4, 0, 0)));
        cache.Entities.Add(new Line(new XYZ(4, 0, 0), new XYZ(4, 3, 0)));
        cache.Entities.Add(new Line(new XYZ(4, 3, 0), new XYZ(0, 3, 0)));
        cache.Entities.Add(new Line(new XYZ(0, 3, 0), XYZ.Zero));
        cache.Entities.Add(new Line(new XYZ(0, 1.5, 0), new XYZ(4, 1.5, 0)));
        cache.Entities.Add(new Solid(
            new XYZ(0, 1.5, 0),
            new XYZ(4, 1.5, 0),
            new XYZ(0, 3, 0),
            new XYZ(4, 3, 0)));
        cache.Entities.Add(new MText("TABLE")
        {
            Style = textStyle,
            InsertPoint = new XYZ(0.5, 2.5, 0),
            Height = 0.5,
        });
        return cache;
    }

    private static TextStyle AddTextStyle(CadDocument document)
    {
        var style = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(style);
        return style;
    }

    private static CadDocumentSnapshot Compile(CadDocumentSession session) =>
        new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { TextFontResolver = new FixedResolver() });

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, 1e-9);
    }

    private sealed class FixedResolver : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(Font, false);
    }
}
