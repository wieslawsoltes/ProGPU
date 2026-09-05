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

public sealed class CadDimensionSnapshotTests
{
    private static readonly TtfFont Font = InterFontFamily.Regular;

    [Fact]
    public void PersistedPictureReusesRetainedSelectionSceneNativeAndPrintPipelines()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        ulong dimensionHandle = 0;
        session.Edit("Add persisted dimension picture", document =>
        {
            TextStyle textStyle = AddTextStyle(document);
            var layer = new Layer("DIMENSIONS") { Color = ACadSharp.Color.Green };
            document.Layers.Add(layer);
            BlockRecord picture = CreatePictureBlock(textStyle);
            var dimension = new DimensionLinear
            {
                Block = picture,
                Layer = layer,
                Color = ACadSharp.Color.ByLayer,
            };
            document.Entities.Add(dimension);
            dimensionHandle = dimension.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader[] entities = snapshot.Entities.ToArray();
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(4, entities.Length);
        Assert.Single(snapshot.Faces.ToArray());
        Assert.Single(snapshot.MTexts.ToArray());
        Assert.Equal(5, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.All(entities, entity => Assert.Equal(dimensionHandle, entity.Handle));
        Assert.All(entities, entity =>
            Assert.Equal("DIMENSIONS", snapshot.Layers.Span[entity.LayerIndex].Name));
        Assert.Equal(4, scene.Statistics.RecordedEntityCount);

        var entityScratch = new int[entities.Length];
        var candidates = new CadSelectionCandidate[entities.Length];
        var matches = new CadSelectionCandidate[entities.Length];
        var hashScratch = new int[
            CadSelectionQuery.GetUniqueHandleScratchLength(entities.Length)];
        var handles = new ulong[entities.Length];
        CadBoundsSelectionQueryResult selection = CadSelectionQuery.QueryExactBounds(
            snapshot,
            snapshot.Bounds,
            CadBoundsSelectionMode.Window,
            entityScratch,
            candidates,
            matches,
            hashScratch,
            handles);

        Assert.Equal(4, selection.MatchedPrimitiveCount);
        Assert.Equal(0, selection.UnsupportedPrimitiveCount);
        Assert.Equal(1, selection.HandleTotalCount);
        Assert.Equal(dimensionHandle, handles[0]);

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
        Assert.Equal(4, printPlan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(scene.DrawingContext.Commands.Count, page.GetCommand(1).Picture!.CommandCount);
    }

    [Theory]
    [InlineData("linear")]
    [InlineData("aligned")]
    [InlineData("ordinate")]
    [InlineData("radius")]
    [InlineData("diameter")]
    [InlineData("arc")]
    [InlineData("angular-two-line")]
    [InlineData("angular-three-point")]
    public void EveryDimensionSubtypeExpandsItsPersistedPicture(string subtype)
    {
        var document = new CadDocument();
        Dimension dimension = CreateDimension(subtype);
        dimension.Block = CreateLinePicture();
        document.Entities.Add(dimension);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadLinePrimitive line = Assert.Single(snapshot.Lines.ToArray());
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        AssertPoint(new CadPoint3D(1, 2, 3), line.Start);
        AssertPoint(new CadPoint3D(4, 5, 6), line.End);
        Assert.Equal(dimension.Handle, header.Handle);
        Assert.Equal(2, snapshot.Statistics.ExpandedEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void PictureOcsDisplacementComposesWithAncestorInsertTransform()
    {
        var document = new CadDocument();
        var dimension = new DimensionAligned
        {
            Block = CreateLinePicture(
                new XYZ(1, 2, 3),
                new XYZ(2, 2, 3)),
            Normal = XYZ.AxisY,
            InsertionPoint = new XYZ(4, 5, 6),
        };
        var assembly = new BlockRecord("DIMENSION_ASSEMBLY");
        assembly.Entities.Add(dimension);
        var insert = new Insert(assembly)
        {
            InsertPoint = new XYZ(10, 20, 30),
            XScale = 2,
            YScale = 3,
            ZScale = 4,
        };
        document.Entities.Add(insert);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadLinePrimitive line = Assert.Single(snapshot.Lines.ToArray());
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());

        AssertPoint(new CadPoint3D(4, 44, 62), line.Start);
        AssertPoint(new CadPoint3D(6, 44, 62), line.End);
        Assert.Equal(insert.Handle, header.Handle);
        Assert.Equal(3, snapshot.Statistics.ExpandedEntityCount);
    }

    [Fact]
    public void TranslateCommandMovesPersistedPictureAndRoundTripsUndoRedo()
    {
        var document = new CadDocument();
        var dimension = new DimensionAligned
        {
            Block = CreateLinePicture(XYZ.Zero, XYZ.AxisX),
            Normal = XYZ.AxisY,
            DefinitionPoint = new XYZ(10, 20, 30),
        };
        document.Entities.Add(dimension);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateEntitiesCommand(
            [dimension.Handle],
            new CadPoint3D(5, 6, 7)));

        Assert.Equal(new XYZ(-5, 7, 6), dimension.InsertionPoint);
        Assert.Equal(new XYZ(15, 26, 37), dimension.DefinitionPoint);
        CadLinePrimitive moved = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(new CadPoint3D(5, 6, 7), moved.Start);
        AssertPoint(new CadPoint3D(6, 6, 7), moved.End);

        Assert.True(history.TryUndo(out _));
        Assert.Equal(XYZ.Zero, dimension.InsertionPoint);
        CadLinePrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(CadPoint3D.Zero, restored.Start);
        AssertPoint(new CadPoint3D(1, 0, 0), restored.End);

        Assert.True(history.TryRedo(out _));
        CadLinePrimitive redone = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(new CadPoint3D(5, 6, 7), redone.Start);
    }

    [Fact]
    public void DuplicateCommandOffsetsIndependentPersistedDimensionPicture()
    {
        var document = new CadDocument();
        var dimension = new DimensionLinear
        {
            Block = CreateLinePicture(XYZ.Zero, XYZ.AxisX),
        };
        document.Entities.Add(dimension);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadDuplicateModelSpaceEntityCommand(
            dimension.Handle,
            new CadPoint3D(10, 20, 30));

        history.Execute(command);

        Dimension duplicate = Assert.IsAssignableFrom<Dimension>(command.Duplicate);
        Assert.NotSame(dimension.Block, duplicate.Block);
        Assert.Equal(new XYZ(10, 20, 30), duplicate.InsertionPoint);
        CadLinePrimitive[] lines = new CadSnapshotCompiler()
            .Compile(session)
            .Lines
            .ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, line => line.Start == CadPoint3D.Zero);
        Assert.Contains(lines, line => line.Start == new CadPoint3D(10, 20, 30));

        Assert.True(history.TryUndo(out _));
        Assert.Single(new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        Assert.True(history.TryRedo(out _));
        Assert.Equal(2, new CadSnapshotCompiler().Compile(session).Lines.Length);
    }

    [Fact]
    public void RotateCommandTransformsPersistedPictureAndDisplacement()
    {
        var document = new CadDocument();
        var dimension = new DimensionAligned
        {
            Block = CreateLinePicture(XYZ.AxisX, new XYZ(2, 0, 0)),
            InsertionPoint = new XYZ(10, 0, 0),
            FirstPoint = XYZ.AxisX,
            SecondPoint = new XYZ(2, 0, 0),
        };
        document.Entities.Add(dimension);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadRotateEntitiesCommand(
            [dimension.Handle],
            new CadPoint3D(0, 0, 1),
            Math.PI / 2));

        CadLinePrimitive rotated = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(new CadPoint3D(0, 11, 0), rotated.Start);
        AssertPoint(new CadPoint3D(0, 12, 0), rotated.End);
        AssertPoint(new CadPoint3D(0, 1, 0), ToPoint(dimension.FirstPoint));
        AssertPoint(new CadPoint3D(0, 10, 0), ToPoint(dimension.InsertionPoint));

        Assert.True(history.TryUndo(out _));
        CadLinePrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(new CadPoint3D(11, 0, 0), restored.Start);
        AssertPoint(new CadPoint3D(12, 0, 0), restored.End);
        Assert.True(history.TryRedo(out _));
        AssertPoint(
            new CadPoint3D(0, 11, 0),
            Assert.Single(new CadSnapshotCompiler().Compile(session).Lines.ToArray()).Start);
    }

    [Fact]
    public void ScaleCommandTransformsPersistedPictureAroundOrigin()
    {
        var document = new CadDocument();
        var dimension = new DimensionLinear
        {
            Block = CreateLinePicture(
                new XYZ(2, 1, 0),
                new XYZ(3, 1, 0)),
        };
        document.Entities.Add(dimension);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadScaleEntitiesCommand(
            [dimension.Handle],
            2,
            new CadPoint3D(1, 1, 0)));

        CadLinePrimitive scaled = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(new CadPoint3D(3, 1, 0), scaled.Start);
        AssertPoint(new CadPoint3D(5, 1, 0), scaled.End);

        Assert.True(history.TryUndo(out _));
        CadLinePrimitive restored = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Lines.ToArray());
        AssertPoint(new CadPoint3D(2, 1, 0), restored.Start);
        AssertPoint(new CadPoint3D(3, 1, 0), restored.End);
        Assert.True(history.TryRedo(out _));
        AssertPoint(
            new CadPoint3D(5, 1, 0),
            Assert.Single(new CadSnapshotCompiler().Compile(session).Lines.ToArray()).End);
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("scale")]
    public void CyclicPersistedPictureEditFailsWithoutPartialMutation(string operation)
    {
        var document = new CadDocument();
        var picture = new BlockRecord("CYCLIC_DIMENSION_PICTURE") { IsAnonymous = true };
        var nested = new DimensionLinear { Block = picture };
        picture.Entities.Add(nested);
        var dimension = new DimensionLinear
        {
            Block = picture,
            DefinitionPoint = new XYZ(10, 20, 30),
        };
        document.Entities.Add(dimension);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        CadEditCommand command = operation switch
        {
            "rotate" => new CadRotateEntitiesCommand(
                [dimension.Handle],
                new CadPoint3D(0, 0, 1),
                Math.PI / 2),
            "scale" => new CadScaleEntitiesCommand(
                [dimension.Handle],
                2,
                CadPoint3D.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(command));

        Assert.Contains("cyclic", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new XYZ(10, 20, 30), dimension.DefinitionPoint);
        Assert.Equal(XYZ.Zero, dimension.InsertionPoint);
        Assert.Equal(XYZ.Zero, nested.DefinitionPoint);
        Assert.Equal(0UL, session.ContentGeneration);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void MissingEmptyXrefAndDepthLimitedPicturesAreDiagnosed()
    {
        var document = new CadDocument();
        document.Entities.Add(new DimensionLinear());
        document.Entities.Add(new DimensionAligned
        {
            Block = new BlockRecord("EMPTY_DIMENSION_PICTURE"),
        });
        var xref = CreateLinePicture();
        xref.Flags |= BlockTypeFlags.XRef;
        document.Entities.Add(new DimensionRadius { Block = xref });
        var nested = new BlockRecord("NESTED_DIMENSION_PICTURE");
        nested.Entities.Add(new Insert(CreateLinePicture()));
        document.Entities.Add(new DimensionDiameter { Block = nested });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions { MaxBlockNestingDepth = 1 });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(3, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("no persisted picture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("external-reference", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("nesting", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task PersistedPictureRoundTripsWithoutLayoutRegeneration(
        CadDocumentFormat format)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add round-trip dimension", document =>
        {
            var dimension = new DimensionLinear
            {
                Block = CreateLinePicture(
                    new XYZ(10, 20, 0),
                    new XYZ(30, 40, 0)),
                DefinitionPoint = new XYZ(30, 40, 0),
                Text = "PERSISTED",
            };
            document.Entities.Add(dimension);
        });
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
            sourceName: $"dimension.{format.ToString().ToLowerInvariant()}");

        Dimension restored = loaded.Session.Read(document =>
            Assert.IsAssignableFrom<Dimension>(Assert.Single(document.Entities)));
        Assert.NotNull(restored.Block);
        Assert.Single(restored.Block.Entities.OfType<Line>());
        Assert.Equal("PERSISTED", restored.Text);
        CadLinePrimitive line = Assert.Single(
            new CadSnapshotCompiler().Compile(loaded.Session).Lines.ToArray());
        AssertPoint(new CadPoint3D(10, 20, 0), line.Start);
        AssertPoint(new CadPoint3D(30, 40, 0), line.End);
    }

    private static BlockRecord CreatePictureBlock(TextStyle textStyle)
    {
        var block = new BlockRecord("DIMENSION_PICTURE") { IsAnonymous = true };
        block.Entities.Add(new Line(XYZ.Zero, new XYZ(10, 0, 0)));
        block.Entities.Add(new Line(new XYZ(100, 0, 0), new XYZ(110, 0, 0)));
        block.Entities.Add(new Solid(
            new XYZ(9, -1, 0),
            new XYZ(9, 1, 0),
            new XYZ(10, 0, 0)));
        block.Entities.Add(new MText("10.00")
        {
            Style = textStyle,
            InsertPoint = new XYZ(4, 2, 0),
            Height = 2,
        });
        block.Entities.Add(new Point(new XYZ(10, 0, 0))
        {
            Layer = Layer.Defpoints,
        });
        return block;
    }

    private static BlockRecord CreateLinePicture() =>
        CreateLinePicture(new XYZ(1, 2, 3), new XYZ(4, 5, 6));

    private static BlockRecord CreateLinePicture(XYZ start, XYZ end)
    {
        var block = new BlockRecord("DIMENSION_PICTURE") { IsAnonymous = true };
        block.Entities.Add(new Line(start, end));
        return block;
    }

    private static Dimension CreateDimension(string subtype) => subtype switch
    {
        "linear" => new DimensionLinear(),
        "aligned" => new DimensionAligned(),
        "ordinate" => new DimensionOrdinate(),
        "radius" => new DimensionRadius(),
        "diameter" => new DimensionDiameter(),
        "arc" => new DimensionArc(),
        "angular-two-line" => new DimensionAngular2Line(),
        "angular-three-point" => new DimensionAngular3Pt(),
        _ => throw new ArgumentOutOfRangeException(nameof(subtype)),
    };

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

    private static CadPoint3D ToPoint(XYZ point) =>
        new(point.X, point.Y, point.Z);

    private sealed class FixedResolver : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(Font, false);
    }
}
