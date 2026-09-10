using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.XData;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadToleranceTests
{
    [Fact]
    public void MultiRowFrameRetainsCellsSymbolsAndFirstRowLeftEdgeAnchor()
    {
        Tolerance source = CreateTolerance(
            "{\\Fgdt;j}%%v{\\Fgdt;n}0.1{\\Fgdt;m}%%vA{\\Fgdt;s}\n" +
            "{\\Fgdt;f}%%v0.2%%vB");

        CadDocumentSnapshot snapshot = Compile(source);

        CadEntityHeader frameHeader = Assert.Single(
            snapshot.Entities.ToArray(), item => item.Kind == CadEntityKind.Tolerance);
        CadTolerancePrimitive frame = snapshot.Tolerances.Span[frameHeader.PrimitiveIndex];
        Assert.Equal(2, frame.RowCount);
        Assert.Equal(6, frame.CellCount);
        Assert.Equal(9, frame.StrokeCount);
        Assert.Equal(9, snapshot.ToleranceStrokes.Length);
        Assert.True(snapshot.Entities.Length > 2);
        Assert.All(snapshot.Entities.ToArray(), header =>
            Assert.Equal(source.Handle, header.Handle));

        CadToleranceStroke left = snapshot.ToleranceStrokes.Span[
            frame.StrokeOffset + frame.RowCount + 1];
        Assert.Equal(new CadPoint3D(10, 21.5, 0), left.Start);
        Assert.Equal(new CadPoint3D(10, 15.5, 0), left.End);

        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawPath);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawGlyphRun);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            812U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(scene.Statistics.RecordedCommandCount, native.SourceCommandCount);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        Assert.Equal(
            scene.Statistics.RecordedCommandCount,
            print.SceneStatistics.RecordedCommandCount);
    }

    [Fact]
    public void TypedDimensionOverridesControlFrameScaleGapAndPaint()
    {
        Tolerance source = CreateTolerance("A");
        source.ExtendedData.Add(
            AppId.DefaultName,
            new ExtendedData(
            [
                new ExtendedDataString(DimensionStyle.StyleOverrideEntryName),
                ExtendedDataControlString.Open,
                new ExtendedDataInteger16(140),
                new ExtendedDataReal(3.0),
                new ExtendedDataInteger16(147),
                new ExtendedDataReal(0.25),
                new ExtendedDataInteger16(176),
                new ExtendedDataInteger16(1),
                ExtendedDataControlString.Close,
            ]));

        CadDocumentSnapshot snapshot = Compile(source);

        CadEntityHeader frameHeader = Assert.Single(
            snapshot.Entities.ToArray(), item => item.Kind == CadEntityKind.Tolerance);
        CadTolerancePrimitive frame = snapshot.Tolerances.Span[frameHeader.PrimitiveIndex];
        CadToleranceStroke left = snapshot.ToleranceStrokes.Span[
            frame.StrokeOffset + frame.RowCount + 1];
        CadStrokeStyle paint = snapshot.Styles.Span[frameHeader.StyleIndex];
        Assert.Equal(21.75, left.Start.Y, 10);
        Assert.Equal(18.25, left.End.Y, 10);
        Assert.Equal((byte)255, paint.Red);
        Assert.Equal((byte)0, paint.Green);
        Assert.Equal((byte)0, paint.Blue);
    }

    [Fact]
    public void PlanChunkCacheReusesCompleteToleranceFrameAndTextRoot()
    {
        CadDocumentSnapshot snapshot = Compile(CreateTolerance(
            "{\\Fgdt;j}%%v{\\Fgdt;n}0.1{\\Fgdt;m}%%vA"));
        var compiler = new CadPlanSceneCompiler();
        using var cache = new CadPlanChunkCache();
        var options = new CadPlanSceneOptions { ChunkCache = cache };
        using CadRecordedPlanScene first = compiler.Compile(snapshot, options);
        using CadRecordedPlanScene second = compiler.Compile(snapshot, options);
        using GpuPicture firstPicture = first.CreatePicture();
        using GpuPicture secondPicture = second.CreatePicture();

        Assert.Equal(1, first.Statistics.RetainedChunkCount);
        Assert.Equal(1, second.Statistics.ReusedRetainedChunkCount);
        Assert.Same(
            firstPicture.GetCommand(0).Picture,
            secondPicture.GetCommand(0).Picture);
        Assert.Equal(
            first.Statistics.RecordedCommandCount,
            second.Statistics.RecordedCommandCount);
    }

    [Fact]
    public void ExactSelectionUsesFrameStrokesInsteadOfFilledBounds()
    {
        CadDocumentSnapshot snapshot = Compile(CreateTolerance("ABC"));
        int entityIndex = Array.FindIndex(
            snapshot.Entities.ToArray(),
            item => item.Kind == CadEntityKind.Tolerance);
        CadEntityHeader header = snapshot.Entities.Span[entityIndex];
        CadTolerancePrimitive frame = snapshot.Tolerances.Span[header.PrimitiveIndex];
        CadToleranceStroke left = snapshot.ToleranceStrokes.Span[
            frame.StrokeOffset + frame.RowCount + 1];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            entityIndex,
            header.Handle,
            header.Kind,
            header.Bounds);

        CadPointHitResult edge = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            (left.Start + left.End) * 0.5,
            0.0);
        CadPointHitResult interior = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            header.Bounds.Center,
            0.0);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(9.9, 19.9, -0.1),
                new CadPoint3D(10.1, 20.1, 0.1)),
            CadBoundsSelectionMode.Crossing);
        CadBoundsHitResult window = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                header.Bounds.Min - new CadPoint3D(0.1, 0.1, 0.1),
                header.Bounds.Max + new CadPoint3D(0.1, 0.1, 0.1)),
            CadBoundsSelectionMode.Window);

        Assert.True(edge.IsHit);
        Assert.False(interior.IsHit);
        Assert.True(crossing.IsHit);
        Assert.True(window.IsHit);
    }

    [Fact]
    public void ParentInsertTransformAppliesToFrameAndTextTogether()
    {
        var document = new CadDocument();
        var block = new BlockRecord("FCF_BLOCK");
        Tolerance source = CreateTolerance("{\\Fgdt;c}%%v0.05");
        source.InsertionPoint = new XYZ(1, 2, 0);
        block.Entities.Add(source);
        document.BlockRecords.Add(block);
        document.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(10, 20, 0),
            Rotation = Math.PI / 2.0,
            XScale = 2.0,
            YScale = 3.0,
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            Options());

        CadEntityHeader frameHeader = Assert.Single(
            snapshot.Entities.ToArray(), item => item.Kind == CadEntityKind.Tolerance);
        CadTolerancePrimitive frame = snapshot.Tolerances.Span[frameHeader.PrimitiveIndex];
        CadToleranceStroke left = snapshot.ToleranceStrokes.Span[
            frame.StrokeOffset + frame.RowCount + 1];
        CadPoint3D midpoint = (left.Start + left.End) * 0.5;
        Assert.Equal(4.0, midpoint.X, 10);
        Assert.Equal(22.0, midpoint.Y, 10);
        Assert.All(snapshot.Entities.ToArray(), header =>
            Assert.Equal(frameHeader.Handle, header.Handle));
    }

    [Fact]
    public void InvalidSymbolRollsBackAndCellBudgetFailsCompilation()
    {
        CadDocumentSnapshot symbol = Compile(CreateTolerance("{\\Fgdt;z}"));

        Assert.Empty(symbol.Entities.ToArray());
        Assert.Empty(symbol.Tolerances.ToArray());
        Assert.Empty(symbol.ToleranceStrokes.ToArray());
        Assert.Empty(symbol.Texts.ToArray());
        Assert.Contains(symbol.Diagnostics.ToArray(), item =>
            item.Code == "CADSNAP003" &&
            item.Message.Contains("symbol", StringComparison.OrdinalIgnoreCase));

        InvalidOperationException budget = Assert.ThrowsAny<InvalidOperationException>(() =>
            Compile(
                CreateTolerance("A%%vB%%vC"),
                new CadSnapshotOptions
                {
                    TextFontResolver = new FixedTextFontResolver(
                        InterFontFamily.Regular),
                    MaxToleranceCellsPerEntity = 2,
                }));
        Assert.Contains("cell", budget.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task FeatureControlFrameSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        document.Entities.Add(CreateTolerance(
            "{\\Fgdt;b}%%v{\\Fgdt;n}0.10{\\Fgdt;m}%%vA"));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        CadSaveResult saved = await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(stream, format);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            loaded.Session,
            Options());

        CadTolerancePrimitive frame = Assert.Single(snapshot.Tolerances.ToArray());
        Assert.Equal(1, frame.RowCount);
        Assert.Equal(3, frame.CellCount);
        Assert.Empty(snapshot.Diagnostics.ToArray());
        Assert.DoesNotContain(saved.Diagnostics, item =>
            item.Severity == CadDiagnosticSeverity.Error);
        Assert.DoesNotContain(loaded.Diagnostics, item =>
            item.Severity == CadDiagnosticSeverity.Error);
    }

    private static CadDocumentSnapshot Compile(
        Tolerance source,
        CadSnapshotOptions? options = null)
    {
        var document = new CadDocument();
        document.Entities.Add(source);
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            options ?? Options());
    }

    private static Tolerance CreateTolerance(string text)
    {
        var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
        var dimensionStyle = new DimensionStyle("FCF_STYLE")
        {
            ScaleFactor = 1.0,
            TextHeight = 2.0,
            DimensionLineGap = 0.5,
            DimensionLineColor = new ACadSharp.Color(255, 255, 255),
            TextColor = new ACadSharp.Color(255, 255, 255),
            Style = textStyle,
        };
        return new Tolerance
        {
            Text = text,
            InsertionPoint = new XYZ(10, 20, 0),
            Direction = XYZ.AxisX,
            Normal = XYZ.AxisZ,
            Style = dimensionStyle,
        };
    }

    private static CadSnapshotOptions Options() => new()
    {
        TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
    };

    private sealed class FixedTextFontResolver(TtfFont font) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(font, IsSubstitution: false);
    }
}
