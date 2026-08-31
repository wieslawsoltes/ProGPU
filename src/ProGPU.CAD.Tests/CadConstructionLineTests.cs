using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadConstructionLineTests
{
    [Fact]
    public void RayAndXLineSnapshotPreservesWcsAndAncestorTransformsWithoutFiniteExtents()
    {
        var document = new CadDocument();
        var finite = new Line(new XYZ(-2, -3, 4), new XYZ(5, 7, 8));
        document.Entities.Add(finite);
        document.Entities.Add(new Ray
        {
            StartPoint = new XYZ(1, 2, 3),
            Direction = new XYZ(2, 0, 0),
        });
        var block = new BlockRecord("CONSTRUCTION");
        block.Entities.Add(new XLine
        {
            FirstPoint = new XYZ(1, 1, 1),
            Direction = new XYZ(1, 1, 0),
        });
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

        Assert.Equal(2, snapshot.ConstructionLines.Length);
        Assert.Equal(CadEntityKind.Ray, snapshot.Entities.Span[1].Kind);
        Assert.Equal(CadEntityKind.XLine, snapshot.Entities.Span[2].Kind);
        Assert.True(snapshot.Entities.Span[1].Bounds.IsEmpty);
        Assert.True(snapshot.Entities.Span[2].Bounds.IsEmpty);
        Assert.Equal(finite.GetBoundingBox().Min.X, snapshot.Bounds.Min.X);
        Assert.Equal(finite.GetBoundingBox().Max.Y, snapshot.Bounds.Max.Y);
        Assert.Equal(new CadPoint3D(1, 2, 3), snapshot.ConstructionLines.Span[0].BasePoint);
        Assert.Equal(new CadPoint3D(1, 0, 0), snapshot.ConstructionLines.Span[0].Direction);
        Assert.Equal(new CadPoint3D(12, 23, 34), snapshot.ConstructionLines.Span[1].BasePoint);
        CadPoint3D transformedDirection = snapshot.ConstructionLines.Span[1].Direction;
        Assert.Equal(1.0, transformedDirection.Length, 12);
        Assert.Equal(2.0 / Math.Sqrt(13.0), transformedDirection.X, 12);
        Assert.Equal(3.0 / Math.Sqrt(13.0), transformedDirection.Y, 12);
        Assert.Equal(1, snapshot.SpatialIndex.EntityCount);
    }

    [Fact]
    public void ExplicitPlanClipProducesExactFiniteRayAndXLineSegments()
    {
        var document = new CadDocument();
        document.Entities.Add(new Ray
        {
            StartPoint = new XYZ(2, 3, 0),
            Direction = new XYZ(1, 0, 0),
        });
        document.Entities.Add(new XLine
        {
            FirstPoint = new XYZ(0, 0, 0),
            Direction = new XYZ(1, 1, 0),
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var clip = new CadBounds3D(
            new CadPoint3D(-5, -4, 0),
            new CadPoint3D(7, 6, 0));

        CadRecordedPlanScene finiteScene = new CadPlanSceneCompiler().Compile(snapshot);
        CadRecordedConstructionScene scene = new CadConstructionSceneCompiler().Compile(
            snapshot,
            clip);

        Assert.Equal(0, finiteScene.Statistics.RecordedEntityCount);
        Assert.Contains(finiteScene.Diagnostics.ToArray(), value => value.Code == "CADSCENE004");
        Assert.Equal(2, scene.Statistics.SourceEntityCount);
        Assert.Equal(2, scene.Statistics.RecordedEntityCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Path);
        Assert.Equal(2, command.Path.Figures.Count);
        Assert.Equal(new System.Numerics.Vector2(2, 3), command.Path.Figures[0].StartPoint);
        Assert.Equal(
            new System.Numerics.Vector2(7, 3),
            Assert.IsType<LineSegment>(command.Path.Figures[0].Segments[0]).Point);
        Assert.Equal(new System.Numerics.Vector2(-4, -4), command.Path.Figures[1].StartPoint);
        Assert.Equal(
            new System.Numerics.Vector2(6, 6),
            Assert.IsType<LineSegment>(command.Path.Figures[1].Segments[0]).Point);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void DeviceHairlinePrintPolicyOverridesConstructionLineweight()
    {
        var document = new CadDocument();
        document.Entities.Add(new Ray
        {
            StartPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineWeight = LineWeightType.W200,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var clip = new CadBounds3D(
            new CadPoint3D(-5, -5, 0),
            new CadPoint3D(5, 5, 0));

        CadRecordedConstructionScene physical =
            new CadConstructionSceneCompiler().Compile(
                snapshot,
                clip,
                new CadPlanSceneOptions { PhysicalDpi = 254 });
        CadRecordedConstructionScene hairline =
            new CadConstructionSceneCompiler().Compile(
                snapshot,
                clip,
                new CadPlanSceneOptions
                {
                    PhysicalDpi = 254,
                    LineWeightMode = CadPrintLineWeightMode.DeviceHairline,
                });

        Assert.Equal(20.0f, Assert.Single(physical.DrawingContext.Commands).Pen!.Thickness);
        Pen pen = Assert.Single(hairline.DrawingContext.Commands).Pen!;
        Assert.Equal(Pen.HairlineThickness, pen.Thickness);
        Assert.Equal(PenStrokeTransformMode.Fixed, pen.StrokeTransformMode);
    }

    [Fact]
    public void VerticalProjectionAndPatternedConstructionRetainExactVisibleFootprints()
    {
        var document = new CadDocument();
        var dashed = new LineType("CONSTRUCTION_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 4.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        document.Entities.Add(new Ray
        {
            StartPoint = new XYZ(2, 3, -10),
            Direction = XYZ.AxisZ,
        });
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = dashed,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedConstructionScene scene = new CadConstructionSceneCompiler().Compile(
            snapshot,
            new CadBounds3D(
                new CadPoint3D(-5, -5, 0),
                new CadPoint3D(5, 5, 0)));

        Assert.Equal(2, scene.Statistics.SourceEntityCount);
        Assert.Equal(2, scene.Statistics.RecordedEntityCount);
        Assert.Equal(0, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypeFigureCount);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Equal(RenderCommandType.DrawPointBatch, commands[0].Type);
        Assert.Equal(1, commands[0].PointBufferCount);
        Assert.Equal(new System.Numerics.Vector2(2, 3), scene.DrawingContext.PointBuffer[0]);
        Assert.Equal(RenderCommandType.DrawPath, commands[1].Type);
        Assert.Equal(3, commands[1].Path!.Figures.Count);
        Assert.Empty(scene.Diagnostics.ToArray());
    }

    [Fact]
    public void AuthoredPhaseOriginSurvivesPanAndRayUsesEndpointHalfDash()
    {
        var document = new CadDocument();
        var dashed = AddSimpleLineType(document, "ORIGIN_DASH", 4.0, -2.0);
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = dashed,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedConstructionScene centered = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(-5.0, 5.0));
        CadRecordedConstructionScene panned = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(-1.0, 7.0));

        AssertFigureIntervals(centered, (-5.0f, -4.0f), (-2.0f, 2.0f), (4.0f, 5.0f));
        AssertFigureIntervals(panned, (-1.0f, 2.0f), (4.0f, 7.0f));
        Assert.Equal(1, centered.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(3, centered.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(0, centered.Statistics.UnsupportedLineTypeCount);

        var rayDocument = new CadDocument();
        LineType rayDashed = AddSimpleLineType(rayDocument, "RAY_DASH", 4.0, -2.0);
        rayDocument.Entities.Add(new Ray
        {
            StartPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = rayDashed,
        });
        CadDocumentSnapshot raySnapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(rayDocument));
        CadRecordedConstructionScene ray = new CadConstructionSceneCompiler().Compile(
            raySnapshot,
            Clip(-5.0, 12.0));
        AssertFigureIntervals(ray, (0.0f, 2.0f), (4.0f, 8.0f), (10.0f, 12.0f));

        using GpuPicture picture = centered.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
    }

    [Fact]
    public void DotConstructionDescriptorsRemainPhaseAnchoredAcrossPan()
    {
        var document = new CadDocument();
        LineType dotted = AddSimpleLineType(document, "ORIGIN_DOT", 4.0, -2.0, 0.0, -2.0);
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = dotted,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedConstructionScene centered = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(-3.0, 8.0));
        CadRecordedConstructionScene panned = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(3.0, 7.0));

        AssertFigureIntervals(centered, (-2.0f, 2.0f), (4.0f, 4.0f), (6.0f, 8.0f));
        AssertFigureIntervals(panned, (4.0f, 4.0f), (6.0f, 7.0f));
        Assert.Equal(0, centered.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(0, panned.Statistics.UnsupportedLineTypeCount);
    }

    [Fact]
    public void StrokeOnlyConstructionPatternUsesContinuousPathWithoutDiagnosticOrBudgetUse()
    {
        var document = new CadDocument();
        LineType solidPattern = AddSimpleLineType(document, "SOLID_PATTERN", 4.0, 2.0);
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = solidPattern,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedConstructionScene scene = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(-5.0, 7.0),
            new CadPlanSceneOptions
            {
                MaxLineTypeFigures = 1,
                MaxLineTypePatternSteps = 1,
                MaxLineTypeSourceSegments = 1,
            });

        AssertFigureIntervals(scene, (-5.0f, 7.0f));
        Assert.Equal(1, scene.Statistics.RecordedEntityCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(0, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(0, scene.Statistics.LineTypePatternStepCount);
        Assert.Equal(0, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Empty(scene.Diagnostics.ToArray());
    }

    [Fact]
    public void FarViewportSeeksDirectlyIntoConstructionPatternWithinVisibleBudget()
    {
        var document = new CadDocument();
        LineType dashed = AddSimpleLineType(document, "FAR_DASH", 4.0, -2.0);
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = dashed,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedConstructionScene scene = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(1_000_000.0, 1_000_012.0),
            new CadPlanSceneOptions { MaxLineTypePatternSteps = 8 });

        Assert.Equal(1, scene.Statistics.RecordedEntityCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.InRange(scene.Statistics.LineTypePatternStepCount, 1, 8);
        Assert.Equal(0, scene.Statistics.UnsupportedLineTypeCount);
        AssertFigureIntervals(
            scene,
            (1_000_000.0f, 1_000_004.0f),
            (1_000_006.0f, 1_000_010.0f),
            (1_000_012.0f, 1_000_012.0f));
    }

    [Fact]
    public void PatternBudgetFailureFallsBackWithoutRetainingPartialFigures()
    {
        var document = new CadDocument();
        LineType dashed = AddSimpleLineType(document, "BOUNDED_DASH", 4.0, -2.0);
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = dashed,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadRecordedConstructionScene scene = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(-10.0, 10.0),
            new CadPlanSceneOptions { MaxLineTypeFigures = 1 });

        Assert.Equal(1, scene.Statistics.RecordedEntityCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(1, scene.Statistics.UnsupportedLineTypeCount);
        AssertFigureIntervals(scene, (-10.0f, 10.0f));
        Assert.Contains(scene.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADCON001" &&
            diagnostic.Message.Contains("figure", StringComparison.Ordinal));
    }

    [Fact]
    public void ComplexConstructionPlacementsKeepAuthoredPhaseAcrossPan()
    {
        var document = new CadDocument();
        var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(textStyle);
        var complex = new LineType("GAS_XLINE");
        complex.AddSegment(new LineType.Segment { Length = 4.0 });
        complex.AddSegment(new LineType.Segment { Length = -2.0 });
        complex.AddSegment(new LineType.Segment
        {
            Text = "G",
            Style = textStyle,
            Scale = 1.0,
            Flags = LineTypeShapeFlags.Text,
        });
        complex.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(complex);
        document.Entities.Add(new XLine
        {
            FirstPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = complex,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        CadRecordedConstructionScene wide = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(-5.0, 13.0));
        CadRecordedConstructionScene panned = new CadConstructionSceneCompiler().Compile(
            snapshot,
            Clip(0.0, 9.0));

        Assert.Equal(3, wide.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(1, panned.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal([-4.0f, 4.0f, 12.0f], wide.DrawingContext.Commands
            .Where(command => command.Type == RenderCommandType.DrawGlyphRun)
            .Select(command => command.Transform.M41)
            .ToArray());
        Assert.Equal(4.0f, Assert.Single(
            panned.DrawingContext.Commands,
            command => command.Type == RenderCommandType.DrawGlyphRun).Transform.M41);
        Assert.DoesNotContain(wide.Diagnostics.ToArray(), value => value.Code == "CADCON001");
        using GpuPicture picture = wide.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
    }

    [Fact]
    public void SelectionQueriesIncludeUnboundedGeometryAndUseExactParameterDomains()
    {
        var document = new CadDocument();
        var ray = new Ray
        {
            StartPoint = new XYZ(0, 0, 0),
            Direction = new XYZ(1, 0, 0),
        };
        var xline = new XLine
        {
            FirstPoint = new XYZ(-1.5, 3, 0),
            Direction = new XYZ(0, 1, 0),
        };
        document.Entities.Add(ray);
        document.Entities.Add(xline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var entityScratch = new int[2];
        var candidates = new CadSelectionCandidate[2];
        CadBounds3D query = new(
            new CadPoint3D(-2, -1, -1),
            new CadPoint3D(-1, 4, 1));

        CadSelectionQueryResult broad = CadSelectionQuery.QueryBounds(
            snapshot,
            query,
            entityScratch,
            candidates);

        Assert.Equal(2, broad.WrittenCount);
        Assert.Equal(2, broad.TotalCount);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidates[0],
                query,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidates[1],
                query,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.All(candidates, candidate => Assert.Equal(
            CadBoundsHitStatus.Miss,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                query,
                CadBoundsSelectionMode.Window).Status));
        Assert.Equal(
            CadPointHitStatus.Miss,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidates[0],
                new CadPoint3D(-1, 0, 0),
                0.01).Status);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidates[1],
                new CadPoint3D(-1.5, -100, 0),
                0.0).Status);

        var matches = new CadSelectionCandidate[2];
        var handleScratch = new int[CadSelectionQuery.GetUniqueHandleScratchLength(2)];
        var handles = new ulong[2];
        CadBoundsSelectionQueryResult exact = CadSelectionQuery.QueryExactBounds(
            snapshot,
            query,
            CadBoundsSelectionMode.Crossing,
            entityScratch,
            candidates,
            matches,
            handleScratch,
            handles);
        Assert.Equal(2, exact.CandidateTotalCount);
        Assert.Equal(1, exact.MatchedPrimitiveCount);
        Assert.Equal(1, exact.HandleTotalCount);
        Assert.Equal(xline.Handle, handles[0]);
    }

    [Fact]
    public void DegenerateDirectionIsRejectedTransactionally()
    {
        var document = new CadDocument();
        document.Entities.Add(new Ray
        {
            StartPoint = XYZ.Zero,
            Direction = XYZ.Zero,
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.ConstructionLines.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), value =>
            value.Message.Contains("direction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConstructionLinesUseGenericTransformHistoryAndDuplication()
    {
        var document = new CadDocument();
        var ray = new Ray
        {
            StartPoint = new XYZ(1, 2, 3),
            Direction = XYZ.AxisX,
        };
        document.Entities.Add(ray);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);

        history.Execute(new CadTranslateEntitiesCommand(
            [ray.Handle],
            new CadPoint3D(4, 5, 6)));
        var duplicate = new CadDuplicateModelSpaceEntityCommand(
            ray.Handle,
            new CadPoint3D(10, 0, 0));
        history.Execute(duplicate);

        Assert.IsType<Ray>(duplicate.Duplicate);
        CadConstructionLinePrimitive[] lines = new CadSnapshotCompiler()
            .Compile(session)
            .ConstructionLines
            .ToArray();
        Assert.Contains(lines, line => line.BasePoint == new CadPoint3D(5, 7, 9));
        Assert.Contains(lines, line => line.BasePoint == new CadPoint3D(15, 7, 9));
        Assert.All(lines, line => Assert.Equal(new CadPoint3D(1, 0, 0), line.Direction));
        Assert.True(history.TryUndo(out _));
        Assert.Single(new CadSnapshotCompiler().Compile(session).ConstructionLines.ToArray());
        Assert.True(history.TryUndo(out _));
        Assert.Equal(
            new CadPoint3D(1, 2, 3),
            Assert.Single(new CadSnapshotCompiler()
                .Compile(session)
                .ConstructionLines
                .ToArray()).BasePoint);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task ConstructionLinesRoundTripThroughAdvertisedFormats(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Entities.Add(new Ray
        {
            StartPoint = new XYZ(7, 11, 13),
            Direction = new XYZ(2, 3, 4),
        });
        document.Entities.Add(new XLine
        {
            FirstPoint = new XYZ(-5, -7, -9),
            Direction = new XYZ(4, -3, 2),
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
            sourceName: $"construction.{format.ToString().ToLowerInvariant()}");

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        Assert.Equal(2, snapshot.ConstructionLines.Length);
        Assert.Equal(new CadPoint3D(7, 11, 13), snapshot.ConstructionLines.Span[0].BasePoint);
        Assert.Equal(new CadPoint3D(-5, -7, -9), snapshot.ConstructionLines.Span[1].BasePoint);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
    }

    [Fact]
    public void PrintClipsConstructionGeometryToExplicitPlotBounds()
    {
        var document = new CadDocument();
        LineType dashed = AddSimpleLineType(document, "PRINT_DASH", 4.0, -2.0);
        document.Entities.Add(new Ray
        {
            StartPoint = XYZ.Zero,
            Direction = XYZ.AxisX,
            LineType = dashed,
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var plotBounds = new CadBounds3D(
            new CadPoint3D(-10, -5, 0),
            new CadPoint3D(20, 5, 0));

        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(
            snapshot,
            new CadPrintPlanOptions { PlotBounds = plotBounds });

        Assert.Equal(1, plan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(1, plan.SceneStatistics.LoweredLineTypeEntityCount);
        Assert.Equal(0, plan.SceneStatistics.UnsupportedLineTypeCount);
        Assert.True(plan.SceneStatistics.LoweredLineTypeFigureCount > 0);
        using GpuPicture page = plan.CreatePagePicture();
        Assert.True(page.Commands.Count() > 0);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            page,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
    }

    private static CadBounds3D Clip(double minimumX, double maximumX) =>
        new(
            new CadPoint3D(minimumX, -1.0, 0.0),
            new CadPoint3D(maximumX, 1.0, 0.0));

    private static LineType AddSimpleLineType(
        CadDocument document,
        string name,
        params double[] lengths)
    {
        var lineType = new LineType(name);
        foreach (double length in lengths)
        {
            lineType.AddSegment(new LineType.Segment { Length = length });
        }
        document.LineTypes.Add(lineType);
        return lineType;
    }

    private static void AssertFigureIntervals(
        CadRecordedConstructionScene scene,
        params (float Start, float End)[] expected)
    {
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(expected.Length, command.Path!.Figures.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            PathFigure figure = command.Path.Figures[i];
            Assert.Equal(expected[i].Start, figure.StartPoint.X);
            Assert.Equal(
                expected[i].End,
                Assert.IsType<LineSegment>(Assert.Single(figure.Segments)).Point.X);
        }
    }

    private sealed class FixedTextFontResolver(TtfFont font) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(font, IsSubstitution: false);
    }
}
