using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMLineTests
{
    [Fact]
    public void SnapshotRetainsElementStylesAndPersistedCutIntervals()
    {
        CadDocumentSnapshot snapshot = Compile(CreateMLine(
            fill: false,
            cutParameters: [1.0, 0.0, 2.0, 4.0]));

        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadMLinePrimitive mline = Assert.Single(snapshot.MLines.ToArray());
        CadMLineStroke[] strokes = snapshot.MLineStrokes.ToArray();

        Assert.Equal(CadEntityKind.MLine, entity.Kind);
        Assert.Equal(4, strokes.Length);
        Assert.Equal(4, mline.StrokeCount);
        Assert.Equal(new CadPoint3D(0, 1, 0), strokes[0].Start);
        Assert.Equal(new CadPoint3D(2, 1, 0), strokes[0].End);
        Assert.Equal(new CadPoint3D(4, 1, 0), strokes[1].Start);
        Assert.Equal(new CadPoint3D(10, 1, 0), strokes[1].End);
        CadMLineElementPath[] elements = snapshot.MLineElementPaths.ToArray();
        Assert.NotEqual(elements[0].StyleIndex, elements[1].StyleIndex);
        Assert.Equal((byte)255, snapshot.Styles.Span[elements[0].StyleIndex].Red);
        Assert.Equal((byte)255, snapshot.Styles.Span[elements[1].StyleIndex].Blue);
    }

    [Fact]
    public void FilledMLineRetainsTwoTrianglesAndBatchedPlanCommands()
    {
        CadDocumentSnapshot snapshot = Compile(CreateMLine(fill: true));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(2, snapshot.MLineStrokes.Length);
        Assert.Equal(2, snapshot.MLineFillTriangles.Length);
        Assert.Equal(new CadPoint3D(0, -1, 0), snapshot.Bounds.Min);
        Assert.Equal(new CadPoint3D(10, 1, 0), snapshot.Bounds.Max);
        Assert.Equal(3, scene.Statistics.RecordedCommandCount);
        Assert.Equal(3, scene.DrawingContext.Commands.Count);
    }

    [Fact]
    public void SelectionUsesRetainedFillAndStrokeGeometry()
    {
        CadDocumentSnapshot snapshot = Compile(CreateMLine(fill: true));
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        CadPointHitResult inside = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0, 0),
            0.0);
        CadPointHitResult outside = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 3, 0),
            0.25);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(4, -0.25, -0.1),
                new CadPoint3D(6, 0.25, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.True(inside.IsHit);
        Assert.False(outside.IsHit);
        Assert.True(crossing.IsHit);
    }

    [Fact]
    public void NestedAffineInsertTransformsAuthoredMLineTopology()
    {
        var document = new CadDocument();
        var block = new BlockRecord("MLINE_BLOCK");
        block.Entities.Add(CreateMLine(fill: false));
        document.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(100, 200, 3),
            XScale = 2,
            YScale = 3,
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Equal(new CadPoint3D(100, 197, 3), snapshot.Bounds.Min);
        Assert.Equal(new CadPoint3D(120, 203, 3), snapshot.Bounds.Max);
    }

    [Fact]
    public void PatternedElementPreservesDashPhaseAcrossPersistedCuts()
    {
        var document = new CadDocument();
        var dashed = new LineType("MLINE_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 2.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        MLine source = CreateMLine(
            fill: false,
            cutParameters: [1.0, 0.0, 2.0, 4.0]);
        source.Style.Elements.First().LineType = dashed;
        document.Entities.Add(source);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(2, scene.Statistics.RecordedCommandCount);
        RenderCommand patterned = scene.DrawingContext.Commands[0];
        Assert.Equal(3, patterned.Path!.Figures.Count);
        Assert.Equal(new System.Numerics.Vector2(-5, 1), patterned.Path.Figures[0].StartPoint);
        Assert.Equal(new System.Numerics.Vector2(-1, 1), patterned.Path.Figures[1].StartPoint);
        Assert.Equal(new System.Numerics.Vector2(3, 1), patterned.Path.Figures[2].StartPoint);
    }

    [Fact]
    public void PatternFigureBudgetFallsBackWithExplicitDiagnostic()
    {
        var document = new CadDocument();
        var dashed = new LineType("MLINE_DENSE");
        dashed.AddSegment(new LineType.Segment { Length = 1.0 });
        dashed.AddSegment(new LineType.Segment { Length = -1.0 });
        document.LineTypes.Add(dashed);
        MLine source = CreateMLine(fill: false);
        source.Style.Elements.First().LineType = dashed;
        document.Entities.Add(source);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions { MaxLineTypeFigures = 2 });

        Assert.Equal(1, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Contains(scene.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSCENE002" &&
            diagnostic.Message.Contains("figure", StringComparison.Ordinal));
        Assert.Equal(2, scene.Statistics.RecordedCommandCount);
    }

    [Fact]
    public void StrokeBudgetFailsClosedBeforeReturningPartialSnapshot()
    {
        InvalidOperationException error = Assert.ThrowsAny<InvalidOperationException>(() => Compile(
            CreateMLine(fill: false, cutParameters: [1.0, 0.0, 2.0, 4.0]),
            new CadSnapshotOptions { MaxMLineStrokes = 3 }));

        Assert.Contains("configured limit of 3", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task ParameterizedMLineSurvivesDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        document.Entities.Add(CreateMLine(
            fill: true,
            cutParameters: [1.0, 0.0, 2.0, 4.0]));
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(stream, format);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);

        Assert.Single(snapshot.MLines.ToArray());
        Assert.Equal(4, snapshot.MLineStrokes.Length);
        Assert.Equal(2, snapshot.MLineFillTriangles.Length);
        Assert.Equal(new CadPoint3D(0, -1, 0), snapshot.Bounds.Min);
        Assert.Equal(new CadPoint3D(10, 1, 0), snapshot.Bounds.Max);
    }

    [Fact]
    public void RetainedMLineReusesNativePictureAndPrintPipelines()
    {
        CadDocumentSnapshot snapshot = Compile(CreateMLine(fill: true));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            811U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(scene.Statistics.RecordedCommandCount, native.SourceCommandCount);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = print.CreatePagePicture();
        Assert.Equal(scene.Statistics.RecordedCommandCount, print.SceneStatistics.RecordedCommandCount);
        Assert.Equal(scene.Statistics.RecordedEntityCount, print.SceneStatistics.RecordedEntityCount);
        Assert.Equal(RenderCommandType.DrawPicture, page.GetCommand(1).Type);
    }

    [Fact]
    public void WarmMLineSelectionAllocatesNoManagedMemory()
    {
        CadDocumentSnapshot snapshot = Compile(CreateMLine(fill: true));
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);
        _ = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0, 0),
            0.0);
        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 100; index++)
        {
            _ = CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(5, 0, 0),
                0.0);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static CadDocumentSnapshot Compile(
        MLine source,
        CadSnapshotOptions? options = null)
    {
        var document = new CadDocument();
        document.Entities.Add(source);
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            options);
    }

    private static MLine CreateMLine(
        bool fill,
        double[]? cutParameters = null)
    {
        var style = new MLineStyle("TEST")
        {
            Flags = fill ? MLineStyleFlags.FillOn : MLineStyleFlags.None,
            FillColor = new ACadSharp.Color(0, 255, 0),
        };
        style.AddElement(new MLineStyle.Element
        {
            Offset = 1.0,
            Color = new ACadSharp.Color(255, 0, 0),
            LineType = LineType.ByLayer,
        });
        style.AddElement(new MLineStyle.Element
        {
            Offset = -1.0,
            Color = new ACadSharp.Color(0, 0, 255),
            LineType = LineType.ByLayer,
        });
        var mline = new MLine
        {
            Style = style,
            Flags = MLineFlags.Has,
            StartPoint = XYZ.Zero,
        };
        mline.Vertices.Add(CreateVertex(0, cutParameters));
        mline.Vertices.Add(CreateVertex(10, cutParameters));
        return mline;
    }

    private static MLine.Vertex CreateVertex(double x, double[]? cutParameters)
    {
        var vertex = new MLine.Vertex
        {
            Position = new XYZ(x, 0, 0),
            Direction = XYZ.AxisX,
            Miter = XYZ.AxisY,
        };
        vertex.Segments.Add(new MLine.Vertex.Segment
        {
            Parameters = new List<double>(cutParameters ?? [1.0, 0.0]),
        });
        double[] lower = cutParameters is null
            ? [-1.0, 0.0]
            : [-1.0, .. cutParameters.AsSpan(1).ToArray()];
        vertex.Segments.Add(new MLine.Vertex.Segment
        {
            Parameters = new List<double>(lower),
        });
        return vertex;
    }
}
