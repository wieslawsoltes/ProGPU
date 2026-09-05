using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLineTypeTests
{
    private const double Tolerance = 1e-5;

    [Fact]
    public void SnapshotPacksReferencedPatternAndResolvesGlobalTimesEntityScale()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add patterned lines", document =>
        {
            document.Header.LineTypeScale = 2.5;
            LineType dashed = AddSimpleLineType(document, "PACKED", 4.0, -2.0, 0.0, -1.0);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(20, 0, 0))
            {
                LineType = dashed,
                LineTypeScale = 3.0,
            });
            document.Entities.Add(new Line(XYZ.AxisY, new XYZ(20, 1, 0))
            {
                LineType = dashed,
                LineTypeScale = 3.0,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Equal(2.5, snapshot.GlobalLineTypeScale);
        CadStrokeStyle style = Assert.Single(snapshot.Styles.ToArray());
        Assert.Equal(7.5, style.LineTypeScale);
        Assert.Equal("PACKED", style.LineTypeName);
        CadLineTypePattern pattern = Assert.Single(snapshot.LineTypePatterns.ToArray());
        Assert.Equal(0, style.LineTypePatternIndex);
        Assert.Equal('A', pattern.Alignment);
        Assert.Equal(CadLineTypePatternKind.Simple, pattern.Kind);
        Assert.Equal(4, pattern.ElementCount);
        Assert.Equal(7.0, pattern.PatternLength);
        Assert.Equal(
            new[] { 4.0, -2.0, 0.0, -1.0 },
            snapshot.LineTypeElements.ToArray().Select(static item => item.Length));
    }

    [Fact]
    public void OpenLineUsesExactAAlignmentAndKeepsOneRetainedCommand()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add A-aligned line", document =>
        {
            LineType dashed = AddSimpleLineType(document, "A_DASH", 4.0, -2.0);
            document.Entities.Add(new Line(new XYZ(100, 20, 0), new XYZ(117, 20, 0))
            {
                LineType = dashed,
            });
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(1, scene.Statistics.RecordedCommandCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(0, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Empty(scene.Diagnostics.ToArray());
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.Equal(PenStrokeTransformMode.Fixed, command.Pen!.StrokeTransformMode);
        PathFigure[] figures = command.Path!.Figures.ToArray();
        AssertLineFigure(figures[0], -8.5f, -4.0f);
        AssertLineFigure(figures[1], -2.0f, 2.0f);
        AssertLineFigure(figures[2], 4.0f, 8.5f);
    }

    [Fact]
    public void AAlignmentPropertyKeepsEveryFiniteLineInsideItsEndpoints()
    {
        const int lineCount = 256;
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add deterministic line corpus", document =>
        {
            LineType dashed = AddSimpleLineType(document, "PROPERTY", 4.0, -2.0, 0.0, -1.0);
            for (int i = 0; i < lineCount; i++)
            {
                double length = 0.125 + ((i * 7919) % 1000) * 0.137;
                document.Entities.Add(new Line(
                    new XYZ(0, i * 2.0, 0),
                    new XYZ(length, i * 2.0, 0))
                {
                    LineType = dashed,
                    LineTypeScale = 0.25 + ((i % 17) * 0.125),
                });
            }
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(lineCount, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(lineCount, scene.Statistics.RecordedCommandCount);
        int observedFigures = 0;
        for (int i = 0; i < lineCount; i++)
        {
            CadLinePrimitive line = snapshot.Lines.Span[i];
            float expectedStart = (float)(line.Start.X - snapshot.RebaseOrigin.X);
            float expectedEnd = (float)(line.End.X - snapshot.RebaseOrigin.X);
            PathFigure[] figures = scene.DrawingContext.Commands[i].Path!.Figures.ToArray();
            Assert.NotEmpty(figures);
            Assert.InRange(Math.Abs(figures[0].StartPoint.X - expectedStart), 0f, (float)Tolerance);
            Assert.InRange(
                Math.Abs(GetFigureEnd(figures[^1]).X - expectedEnd),
                0f,
                (float)Tolerance);
            float previous = expectedStart;
            foreach (PathFigure figure in figures)
            {
                Vector2 end = GetFigureEnd(figure);
                Assert.InRange(figure.StartPoint.X, previous - (float)Tolerance, expectedEnd + (float)Tolerance);
                Assert.InRange(end.X, figure.StartPoint.X - (float)Tolerance, expectedEnd + (float)Tolerance);
                previous = end.X;
            }

            observedFigures += figures.Length;
        }

        Assert.Equal(observedFigures, scene.Statistics.LoweredLineTypeFigureCount);
    }

    [Fact]
    public void ShortOpenEntityIsContinuousBetweenBothEndpoints()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add short line", document =>
        {
            LineType dashed = AddSimpleLineType(document, "SHORT", 4.0, -2.0);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(5, 0, 0))
            {
                LineType = dashed,
            });
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(1, scene.Statistics.LoweredLineTypeFigureCount);
        AssertLineFigure(
            Assert.Single(Assert.Single(scene.DrawingContext.Commands).Path!.Figures),
            -2.5f,
            2.5f);
    }

    [Fact]
    public void DotFirstPatternRetainsEndpointAndInteriorPointFigures()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add dotted line", document =>
        {
            LineType dotted = AddSimpleLineType(document, "DOTTED", 0.0, -2.0);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(8, 0, 0))
            {
                LineType = dotted,
            });
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));
        PathFigure[] figures = Assert.Single(scene.DrawingContext.Commands).Path!.Figures.ToArray();

        Assert.Equal(5, figures.Length);
        Assert.Equal(new[] { -4f, -2f, 0f, 2f, 4f }, figures.Select(static item => item.StartPoint.X));
        Assert.All(figures, figure =>
        {
            LineSegment point = Assert.IsType<LineSegment>(Assert.Single(figure.Segments));
            Assert.Equal(figure.StartPoint, point.Point);
        });
    }

    [Fact]
    public void ArcAndEllipseDashesRemainAnalyticThroughOwnedPictureAndNativeCompiler()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add patterned curves", document =>
        {
            LineType dashed = AddSimpleLineType(document, "CURVE_DASH", 4.0, -2.0);
            document.Entities.Add(new Arc
            {
                Center = XYZ.Zero,
                Radius = 10,
                StartAngle = 0,
                EndAngle = Math.PI,
                LineType = dashed,
            });
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(30, 0, 0),
                MajorAxisEndPoint = new XYZ(8, 0, 0),
                RadiusRatio = 0.25,
                StartParameter = 0,
                EndParameter = Math.PI * 1.5,
                LineType = dashed,
            });
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(2, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.True(scene.Statistics.LoweredLineTypeFigureCount > 4);
        Assert.All(scene.DrawingContext.Commands, command =>
        {
            Assert.Equal(RenderCommandType.DrawPath, command.Type);
            Assert.All(command.Path!.Figures, figure =>
                Assert.IsType<ArcSegment>(Assert.Single(figure.Segments)));
        });

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            91U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount >= scene.Statistics.LoweredLineTypeFigureCount);
    }

    [Fact]
    public void PolylineGenerationFlagSelectsSegmentResetOrContinuousPattern()
    {
        CadRecordedPlanScene reset = CompilePolyline(isContinuous: false);
        CadRecordedPlanScene continuous = CompilePolyline(isContinuous: true);

        RenderCommand resetCommand = Assert.Single(reset.DrawingContext.Commands);
        RenderCommand continuousCommand = Assert.Single(continuous.DrawingContext.Commands);
        Assert.Equal(-4f, resetCommand.Transform.M41);
        Assert.Equal(-4f, continuousCommand.Transform.M41);
        PathFigure[] resetFigures = resetCommand.Path!.Figures.ToArray();
        PathFigure[] continuousFigures = continuousCommand.Path!.Figures.ToArray();
        AssertLineFigure(resetFigures[0], 0f, 4f);
        AssertLineFigure(resetFigures[1], 4f, 8f);
        AssertLineFigure(continuousFigures[0], 0f, 3f);
        AssertLineFigure(continuousFigures[1], 5f, 8f);
    }

    [Fact]
    public void FigureLimitFallsBackWithTypedDiagnosticInsteadOfPartialGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add dense line", document =>
        {
            LineType dashed = AddSimpleLineType(document, "DENSE", 1.0, -1.0);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(100, 0, 0))
            {
                LineType = dashed,
            });
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions { MaxLineTypeFigures = 2 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(1, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(RenderCommandType.DrawLine, Assert.Single(scene.DrawingContext.Commands).Type);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("2-figure", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PatternStepLimitBoundsGapHeavyTraversalBeforePublishingGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add gap-heavy pattern", document =>
        {
            LineType sparse = AddSimpleLineType(
                document,
                "SPARSE",
                1.0,
                -0.25,
                -0.25,
                -0.25,
                -0.25);
            for (int i = 0; i < 32; i++)
            {
                document.Entities.Add(new Line(
                    new XYZ(0, i, 0),
                    new XYZ(100, i, 0))
                {
                    LineType = sparse,
                });
            }
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session),
            new CadPlanSceneOptions { MaxLineTypePatternSteps = 3 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(3, scene.Statistics.LineTypePatternStepCount);
        Assert.Equal(32, scene.DrawingContext.Commands.Count);
        Assert.All(scene.DrawingContext.Commands, command =>
            Assert.Equal(RenderCommandType.DrawLine, command.Type));
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("3-step", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanChunkCacheReplaysSimpleLineTypeOutputAndGlobalCounters()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add reusable patterned lines", document =>
        {
            LineType dashed = AddSimpleLineType(document, "CACHE_DASH", 4.0, -2.0);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(17, 0, 0))
            {
                LineType = dashed,
            });
            document.Entities.Add(new Line(new XYZ(0, 10, 0), new XYZ(31, 10, 0))
            {
                LineType = dashed,
            });
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        var compiler = new CadPlanSceneCompiler();
        using var cache = new CadPlanChunkCache();
        var options = new CadPlanSceneOptions { ChunkCache = cache };
        using CadRecordedPlanScene baseline = compiler.Compile(snapshot);
        using GpuPicture baselinePicture = baseline.CreatePicture();
        using CadRecordedPlanScene first = compiler.Compile(snapshot, options);
        using CadRecordedPlanScene second = compiler.Compile(snapshot, options);
        using GpuPicture secondPicture = second.CreatePicture();

        Assert.Equal(0, first.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(2, second.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(
            baseline.Statistics.LoweredLineTypeEntityCount,
            second.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(
            baseline.Statistics.LoweredLineTypeFigureCount,
            second.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(
            baseline.Statistics.LineTypePatternStepCount,
            second.Statistics.LineTypePatternStepCount);
        Assert.Equal(
            baseline.Statistics.LineTypeSourceSegmentCount,
            second.Statistics.LineTypeSourceSegmentCount);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            baselinePicture,
            703U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? baselineNative,
            out NativePictureCompileFailure baselineFailure),
            baselineFailure.ToString());
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            secondPicture,
            704U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? cachedNative,
            out NativePictureCompileFailure cachedFailure),
            cachedFailure.ToString());
        Assert.Equal(baselineNative!.NativeDrawCount, cachedNative!.NativeDrawCount);
        Assert.Equal(baselineNative.PathCount, cachedNative.PathCount);
        Assert.Equal(baselineNative.PathSegmentCount, cachedNative.PathSegmentCount);
    }

    [Fact]
    public void SourceSegmentLimitIsSharedAcrossPatternedEntities()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add source segment corpus", document =>
        {
            LineType dashed = AddSimpleLineType(document, "SOURCE_LIMIT", 4.0, -2.0);
            for (int i = 0; i < 8; i++)
            {
                document.Entities.Add(new Line(
                    new XYZ(0, i, 0),
                    new XYZ(12, i, 0))
                {
                    LineType = dashed,
                });
            }
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session),
            new CadPlanSceneOptions { MaxLineTypeSourceSegments = 3 });

        Assert.Equal(3, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(3, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Equal(8, scene.DrawingContext.Commands.Count);
        Assert.Equal(3, scene.DrawingContext.Commands.Count(command =>
            command.Type == RenderCommandType.DrawPath));
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("3-segment", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerEntityArcMapLimitFallsBackBeforeAllocatingPolylineMaps()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add multi-bulge polyline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "ARC_MAP_LIMIT", 4.0, -2.0);
            var polyline = new LwPolyline { LineType = dashed };
            polyline.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)) { Bulge = 0.25 });
            polyline.Vertices.Add(new LwPolyline.Vertex(new XY(5, 3)) { Bulge = -0.25 });
            polyline.Vertices.Add(new LwPolyline.Vertex(new XY(10, 0)));
            document.Entities.Add(polyline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session),
            new CadPlanSceneOptions { MaxLineTypeArcMapsPerEntity = 1 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(2, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Equal(RenderCommandType.DrawPath, Assert.Single(scene.DrawingContext.Commands).Type);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("1-arc", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotPatternLimitFailsBeforeAcceptingExcessReferencedPattern()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add two referenced patterns", document =>
        {
            LineType first = AddSimpleLineType(document, "FIRST", 4.0, -2.0);
            LineType second = AddSimpleLineType(document, "SECOND", 2.0, -1.0);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { LineType = first });
            document.Entities.Add(new Line(XYZ.AxisY, new XYZ(1, 1, 0)) { LineType = second });
        });

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions { MaxLineTypePatterns = 1 }));

        Assert.Contains("linetype count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotElementLimitFailsBeforeAcceptingExcessElements()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add oversized pattern", document =>
        {
            LineType oversized = AddSimpleLineType(document, "OVERSIZED", 3.0, -1.0, 0.0);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { LineType = oversized });
        });

        InvalidOperationException exception = Assert.ThrowsAny<InvalidOperationException>(() =>
            new CadSnapshotCompiler().Compile(
                session,
                new CadSnapshotOptions { MaxLineTypeElements = 2 }));

        Assert.Contains("element count", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComplexPatternIsCapturedAndExplicitlyFallsBack()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add complex linetype", document =>
        {
            var complex = new LineType("COMPLEX");
            complex.AddSegment(new LineType.Segment
            {
                Length = 4,
                IsText = true,
                Text = "GAS",
            });
            complex.AddSegment(new LineType.Segment { Length = -2 });
            document.LineTypes.Add(complex);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(20, 0, 0))
            {
                LineType = complex,
            });
            document.Entities.Add(new Line(XYZ.AxisY, new XYZ(20, 1, 0))
            {
                LineType = complex,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        var compiler = new CadPlanSceneCompiler();
        using var cache = new CadPlanChunkCache();
        var options = new CadPlanSceneOptions { ChunkCache = cache };
        using CadRecordedPlanScene firstScene = compiler.Compile(snapshot, options);
        using CadRecordedPlanScene scene = compiler.Compile(snapshot, options);

        Assert.Equal(CadLineTypePatternKind.Complex, Assert.Single(snapshot.LineTypePatterns.ToArray()).Kind);
        Assert.Equal(0, scene.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(0, cache.Count);
        Assert.Equal(2, scene.DrawingContext.Commands.Count);
        Assert.All(scene.DrawingContext.Commands, command =>
        {
            GpuPicture fallbackPicture = Assert.IsType<GpuPicture>(command.Picture);
            Assert.Equal(1, fallbackPicture.CommandCount);
            Assert.Equal(RenderCommandType.DrawLine, fallbackPicture.GetCommand(0).Type);
        });
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("unresolved", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComplexTrueTypePatternShapesOnceAndRetainsAbsolutePlacements()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add complex text linetype", document =>
        {
            var textStyle = new TextStyle("INTER")
            {
                Filename = "Inter.ttf",
                Width = 1.25,
            };
            document.TextStyles.Add(textStyle);
            var complex = new LineType("GAS_LINE");
            complex.AddSegment(new LineType.Segment { Length = 4.0 });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            complex.AddSegment(new LineType.Segment
            {
                Text = "GAS",
                Style = textStyle,
                Scale = 3.0,
                Rotation = Math.PI * 0.5,
                Flags = LineTypeShapeFlags.Text | LineTypeShapeFlags.RotationIsAbsolute,
                Offset = new XY(0.5, 1.0),
            });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            document.LineTypes.Add(complex);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(48, 0, 0))
            {
                LineType = complex,
                LineTypeScale = 2.0,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });
        var compiler = new CadPlanSceneCompiler();
        using var chunkCache = new CadPlanChunkCache();
        var chunkOptions = new CadPlanSceneOptions { ChunkCache = chunkCache };
        using CadRecordedPlanScene scene = compiler.Compile(snapshot);
        using CadRecordedPlanScene firstScene = compiler.Compile(snapshot, chunkOptions);
        using CadRecordedPlanScene cachedScene = compiler.Compile(snapshot, chunkOptions);

        CadLineTypeElement textElement = snapshot.LineTypeElements.Span[2];
        Assert.Equal(CadLineTypeElementKind.TrueTypeText, textElement.Kind);
        Assert.Equal(CadLineTypeRotationMode.Absolute, textElement.RotationMode);
        Assert.Single(snapshot.LineTypeTextResources.ToArray());
        Assert.Equal(1, cachedScene.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(
            scene.Statistics.LoweredLineTypePlacementCount,
            cachedScene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(0, scene.Statistics.UnsupportedLineTypeCount);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(4, commands.Length);
        Assert.Equal(RenderCommandType.DrawPath, commands[0].Type);
        Assert.All(commands[1..], command =>
        {
            Assert.Equal(RenderCommandType.DrawGlyphRun, command.Type);
            Assert.True(command.UseVectorGlyphRendering);
            Assert.Equal(0.0f, command.Transform.M11, 5);
            Assert.Equal(7.5f, command.Transform.M12, 5);
            Assert.Equal(2.0f, command.Transform.M42, 5);
        });
        Assert.Equal(-15.0f, commands[1].Transform.M41, 5);
        Assert.Equal(1.0f, commands[2].Transform.M41, 5);
        Assert.Equal(17.0f, commands[3].Transform.M41, 5);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            92U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount >= 3);
    }

    [Fact]
    public void OpenLinearRationalSplineRetainsUninterruptedComplexPattern()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add patterned linear spline", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var complex = new LineType("SPLINE_GAS");
            complex.AddSegment(new LineType.Segment { Length = 4.0 });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            complex.AddSegment(new LineType.Segment
            {
                Text = "X",
                Style = textStyle,
                Flags = LineTypeShapeFlags.Text,
            });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            document.LineTypes.Add(complex);

            var spline = new Spline { Degree = 1, LineType = complex };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(8, 0, 0),
                new XYZ(8, 8, 0),
            ]);
            spline.Knots.AddRange([0, 0, 1, 2, 2]);
            spline.Weights.AddRange([1, 2, 1]);
            document.Entities.Add(spline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(2, scene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(2, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Empty(scene.Diagnostics.ToArray());
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(3, commands.Length);
        Assert.Equal(RenderCommandType.DrawPath, commands[0].Type);
        PathGeometry path = Assert.IsType<PathGeometry>(commands[0].Path);
        Assert.Equal(3, path.Figures.Count);
        Assert.Equal(2, path.Figures[1].Segments.Count);
        Assert.Equal(0.0f, commands[1].Transform.M41, 5);
        Assert.Equal(-4.0f, commands[1].Transform.M42, 5);
        Assert.Equal(1.0f, commands[1].Transform.M11, 5);
        Assert.Equal(0.0f, commands[1].Transform.M12, 5);
        Assert.Equal(4.0f, commands[2].Transform.M41, 5);
        Assert.Equal(0.0f, commands[2].Transform.M42, 5);
        Assert.Equal(0.0f, commands[2].Transform.M11, 5);
        Assert.Equal(1.0f, commands[2].Transform.M12, 5);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            93U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.True(compiled.GeometryPrimitiveCount >= 3);
    }

    [Fact]
    public void LinearSplineSourceSegmentLimitFallsBackTransactionally()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add limited linear spline", document =>
        {
            LineType dashed = AddSimpleLineType(
                document,
                "LINEAR_SPLINE_LIMIT",
                3.0,
                -1.0,
                0.0,
                -1.0);
            var spline = new Spline { Degree = 1, LineType = dashed };
            spline.ControlPoints.AddRange([XYZ.Zero, new XYZ(5, 0, 0), new XYZ(10, 0, 0)]);
            spline.Knots.AddRange([0, 0, 1, 2, 2]);
            document.Entities.Add(spline);
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions { MaxLineTypeSourceSegments = 1 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(RenderCommandType.DrawExtension, Assert.Single(scene.DrawingContext.Commands).Type);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("segment", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenQuadraticSplinePatternRetainsExactRationalSubcurve()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add curved patterned spline", document =>
        {
            LineType dashed = AddSimpleLineType(
                document,
                "CURVED_SPLINE_EXACT",
                100.0,
                -1.0);
            var spline = new Spline { Degree = 2, LineType = dashed };
            spline.ControlPoints.AddRange([
                new XYZ(1, 0, 0),
                new XYZ(1, 1, 0),
                new XYZ(0, 1, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1.0, Math.Sqrt(0.5), 1.0]);
            document.Entities.Add(spline);
        });
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(1, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Empty(scene.Diagnostics.ToArray());
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(RenderCommandType.DrawExtension, command.Type);
        Assert.Equal(CompositorBuiltInExtensions.Spline, command.ExtensionId);
        Assert.Equal(2, command.SplineDegree);
        Assert.Equal(3, command.PointBufferCount);
        Assert.Equal(6, command.DoubleBufferCount);
        Assert.Equal(3, command.WeightBufferCount);
        Vector2[] points = scene.DrawingContext.PointBuffer
            .Skip(command.PointBufferOffset)
            .Take(command.PointBufferCount)
            .ToArray();
        Assert.Equal(new Vector2(0.5f, -0.5f), points[0]);
        Assert.Equal(new Vector2(0.5f, 0.5f), points[1]);
        Assert.Equal(new Vector2(-0.5f, 0.5f), points[2]);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            94U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.StrokeCount);
    }

    [Fact]
    public void MultiSpanQuadraticDashIsOneContinuousExactSplineCommand()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add multi-span quadratic spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "MULTI_SPLINE", 100.0, -1.0);
            var spline = new Spline { Degree = 2, LineType = dashed };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(2, 4, 1),
                new XYZ(4, 0, 2),
                new XYZ(6, -4, 1),
                new XYZ(8, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 2, 3, 3, 3]);
            spline.Weights.AddRange([1, 2, 1, 3, 1]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(3, scene.Statistics.LineTypeSourceSegmentCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(CompositorBuiltInExtensions.Spline, command.ExtensionId);
        Assert.Equal(7, command.PointBufferCount);
        Assert.Equal(10, command.DoubleBufferCount);
        Assert.Equal(7, command.WeightBufferCount);
        double[] knots = scene.DrawingContext.DoubleBuffer
            .Skip(command.DoubleBufferOffset)
            .Take(command.DoubleBufferCount)
            .ToArray();
        Assert.Equal(new double[] { 0, 0, 0, 1, 1, 2, 2, 3, 3, 3 }, knots);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            95U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.StrokeCount);
        Assert.Equal(7, compiled.StrokePointCount);
        Assert.Equal(17, compiled.StrokeDoubleCount);
    }

    [Fact]
    public void WeightedQuadraticDashEndpointsStayOnExactCircle()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add dashed rational quarter circle", document =>
        {
            LineType dashed = AddSimpleLineType(document, "RATIONAL_DASH", 0.4, -0.2);
            var spline = new Spline { Degree = 2, LineType = dashed };
            spline.ControlPoints.AddRange([
                new XYZ(1, 0, 0),
                new XYZ(1, 1, 0),
                new XYZ(0, 1, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1.0, Math.Sqrt(0.5), 1.0]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(3, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(3, scene.DrawingContext.Commands.Count);
        Vector2 center = new(-0.5f, -0.5f);
        foreach (RenderCommand command in scene.DrawingContext.Commands)
        {
            Assert.Equal(CompositorBuiltInExtensions.Spline, command.ExtensionId);
            Assert.Equal(3, command.PointBufferCount);
            ReadOnlySpan<Vector2> points = CollectionsMarshal.AsSpan(
                scene.DrawingContext.PointBuffer).Slice(
                    command.PointBufferOffset,
                    command.PointBufferCount);
            Assert.Equal(1.0f, Vector2.Distance(center, points[0]), 4);
            Assert.Equal(1.0f, Vector2.Distance(center, points[^1]), 4);
            ReadOnlySpan<double> weights = CollectionsMarshal.AsSpan(
                scene.DrawingContext.DoubleBuffer).Slice(
                    command.WeightBufferOffset,
                    command.WeightBufferCount);
            Assert.All(weights.ToArray(), weight => Assert.True(weight > 0.0));
        }
    }

    [Fact]
    public void HigherDegreeSplineArcMapLimitFallsBackTransactionally()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add map-limited quadratic spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "SPLINE_MAP_LIMIT", 1.0, -1.0);
            var spline = new Spline { Degree = 2, LineType = dashed };
            spline.ControlPoints.AddRange([
                XYZ.Zero,
                new XYZ(2, 4, 0),
                new XYZ(4, 0, 0),
                new XYZ(6, -4, 0),
                new XYZ(8, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 2, 3, 3, 3]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session),
            new CadPlanSceneOptions { MaxLineTypeArcMapsPerEntity = 2 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(RenderCommandType.DrawExtension, Assert.Single(scene.DrawingContext.Commands).Type);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("arc per-entity map limit", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurvedRationalSplinePlacesComplexTextOnMeasuredTangent()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add complex rational spline", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var complex = new LineType("RATIONAL_GAS");
            complex.AddSegment(new LineType.Segment { Length = 0.4 });
            complex.AddSegment(new LineType.Segment { Length = -0.2 });
            complex.AddSegment(new LineType.Segment
            {
                Text = "X",
                Style = textStyle,
                Flags = LineTypeShapeFlags.Text,
            });
            complex.AddSegment(new LineType.Segment { Length = -0.2 });
            document.LineTypes.Add(complex);
            var spline = new Spline { Degree = 2, LineType = complex };
            spline.ControlPoints.AddRange([
                new XYZ(1, 0, 0),
                new XYZ(1, 1, 0),
                new XYZ(0, 1, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            spline.Weights.AddRange([1.0, Math.Sqrt(0.5), 1.0]);
            document.Entities.Add(spline);
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.LoweredLineTypePlacementCount);
        RenderCommand glyph = Assert.Single(
            scene.DrawingContext.Commands,
            command => command.Type == RenderCommandType.DrawGlyphRun);
        Vector2 center = new(-0.5f, -0.5f);
        Vector2 radial = new(glyph.Transform.M41, glyph.Transform.M42);
        radial -= center;
        Assert.Equal(1.0f, radial.Length(), 4);
        Vector2 tangent = Vector2.Normalize(new Vector2(
            glyph.Transform.M11,
            glyph.Transform.M12));
        Assert.Equal(0.0f, Vector2.Dot(radial, tangent), 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeriodicSplinePatternRetainsExactCyclicSeamTopology(bool expandedKnots)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add periodic spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "PERIODIC_SPLINE", 100.0, -1.0);
            var spline = new Spline
            {
                Degree = 2,
                IsClosed = true,
                IsPeriodic = true,
                LineType = dashed,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(2, 3, 0),
                new XYZ(4, 0, 0),
                new XYZ(2, -3, 0),
            ]);
            spline.Knots.AddRange(expandedKnots
                ? [-2, -1, 0, 1, 2, 3, 4, 5, 6]
                : [0, 1, 2, 3, 4]);
            spline.Weights.AddRange([1, 2, 1, 2]);
            document.Entities.Add(spline);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadSplinePrimitive spline = Assert.Single(snapshot.Splines.ToArray());
        Assert.True(spline.IsClosed);
        Assert.True(spline.IsPeriodic);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(4, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Empty(scene.Diagnostics.ToArray());
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(CompositorBuiltInExtensions.Spline, command.ExtensionId);
        Assert.Equal(2, command.SplineDegree);
        Assert.Equal(9, command.PointBufferCount);
        Assert.Equal(12, command.DoubleBufferCount);
        Assert.Equal(9, command.WeightBufferCount);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            97U,
            scene.ContentGeneration,
            out NativeCompiledPicture? compiled,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(compiled);
        Assert.Equal(1, compiled.StrokeCount);
        Assert.Equal(9, compiled.StrokePointCount);
        Assert.Equal(21, compiled.StrokeDoubleCount);
    }

    [Fact]
    public void ClosedNonperiodicSplinePatternIncludesExactElevatedClosingEdge()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add legacy closed spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "CLOSED_SPLINE", 100.0, -1.0);
            var spline = new Spline
            {
                Degree = 2,
                IsClosed = true,
                LineType = dashed,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(2, 4, 0),
                new XYZ(4, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(2, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Empty(scene.Diagnostics.ToArray());
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(CompositorBuiltInExtensions.Spline, command.ExtensionId);
        Assert.Equal(5, command.PointBufferCount);
        Assert.Equal(8, command.DoubleBufferCount);
        Assert.Equal(5, command.WeightBufferCount);
        ReadOnlySpan<Vector2> points = CollectionsMarshal.AsSpan(
            scene.DrawingContext.PointBuffer).Slice(
            command.PointBufferOffset,
            command.PointBufferCount);
        Assert.True(Vector2.Distance(points[0], points[^1]) > 0.0f);
        Vector2 closingMidpoint = (points[2] + points[4]) * 0.5f;
        Assert.Equal(closingMidpoint.X, points[3].X, 5);
        Assert.Equal(closingMidpoint.Y, points[3].Y, 5);
    }

    [Fact]
    public void PeriodicSplineArcMapLimitFallsBackBeforeProportionalOutput()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add map-limited periodic spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "PERIODIC_MAP_LIMIT", 1.0, -1.0);
            var spline = new Spline
            {
                Degree = 2,
                IsClosed = true,
                IsPeriodic = true,
                LineType = dashed,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(2, 3, 0),
                new XYZ(4, 0, 0),
                new XYZ(2, -3, 0),
            ]);
            spline.Knots.AddRange([-2, -1, 0, 1, 2, 3, 4, 5, 6]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session),
            new CadPlanSceneOptions { MaxLineTypeArcMapsPerEntity = 3 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(4, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Equal(RenderCommandType.DrawExtension, Assert.Single(scene.DrawingContext.Commands).Type);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("arc per-entity map limit", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PeriodicSplineRejectsInconsistentExpandedKnotIntervals()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add malformed periodic spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "PERIODIC_INVALID", 1.0, -1.0);
            var spline = new Spline
            {
                Degree = 2,
                IsClosed = true,
                IsPeriodic = true,
                LineType = dashed,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(2, 3, 0),
                new XYZ(4, 0, 0),
                new XYZ(2, -3, 0),
            ]);
            spline.Knots.AddRange([-2, -0.5, 0, 1, 2, 3, 4, 5, 6]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(0, scene.Statistics.LineTypeSourceSegmentCount);
        Assert.Equal(RenderCommandType.DrawExtension, Assert.Single(scene.DrawingContext.Commands).Type);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE002", diagnostic.Code);
        Assert.Contains("no exact analytic linetype splitter", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstantSplineSpanConsumesNoPatternDistanceOrOutputPiece()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add spline with a constant span", document =>
        {
            LineType dashed = AddSimpleLineType(document, "CONSTANT_SPAN", 100.0, -1.0);
            var spline = new Spline { Degree = 2, LineType = dashed };
            spline.ControlPoints.AddRange([
                XYZ.Zero,
                XYZ.Zero,
                XYZ.Zero,
                new XYZ(2, 2, 0),
                new XYZ(4, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 2, 2, 2]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(2, scene.Statistics.LineTypeSourceSegmentCount);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands);
        Assert.Equal(3, command.PointBufferCount);
        Assert.Equal(6, command.DoubleBufferCount);
    }

    [Fact]
    public void DiscontinuousSplinePatternFallsBackWithoutConnectingStroke()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add discontinuous spline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "DISCONTINUITY_GATE", 1.0, -1.0);
            var spline = new Spline { Degree = 2, LineType = dashed };
            spline.ControlPoints.AddRange([
                XYZ.Zero,
                new XYZ(1, 2, 0),
                new XYZ(2, 0, 0),
                new XYZ(5, 0, 0),
                new XYZ(6, 2, 0),
                new XYZ(7, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 1, 1, 1, 2, 2, 2]);
            document.Entities.Add(spline);
        });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            new CadSnapshotCompiler().Compile(session));

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(RenderCommandType.DrawExtension, Assert.Single(scene.DrawingContext.Commands).Type);
        Assert.Single(scene.Diagnostics.ToArray());
    }

    [Fact]
    public void ComplexShapePatternUsesNonFontShxAndRelativeTangent()
    {
        CadShxGlyphCache cache = CreateShapeCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add complex shape linetype", document =>
        {
            var shapeStyle = new TextStyle("BOX_SHAPES")
            {
                Filename = "boxes.shx",
                Flags = StyleFlags.IsShape,
            };
            document.TextStyles.Add(shapeStyle);
            var complex = new LineType("BOX_LINE");
            complex.AddSegment(new LineType.Segment { Length = 4.0 });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            complex.AddSegment(new LineType.Segment
            {
                ShapeNumber = 230,
                Style = shapeStyle,
                Scale = 0.5,
                Rotation = Math.PI * 0.5,
                Flags = LineTypeShapeFlags.Shape,
            });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            document.LineTypes.Add(complex);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(0, 24, 0))
            {
                LineType = complex,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = new FixedShxFontResolver(cache) });
        var compiler = new CadPlanSceneCompiler();
        using var chunkCache = new CadPlanChunkCache();
        var chunkOptions = new CadPlanSceneOptions { ChunkCache = chunkCache };
        using CadRecordedPlanScene scene = compiler.Compile(snapshot);
        using CadRecordedPlanScene firstScene = compiler.Compile(snapshot, chunkOptions);
        using CadRecordedPlanScene cachedScene = compiler.Compile(snapshot, chunkOptions);

        Assert.False(cache.Font.IsTextFont);
        Assert.Equal(CadLineTypeElementKind.ShxShape, snapshot.LineTypeElements.Span[2].Kind);
        Assert.Single(snapshot.LineTypeShapeResources.ToArray());
        Assert.Equal(1, cachedScene.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(
            scene.Statistics.LoweredLineTypePlacementCount,
            cachedScene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(4, scene.DrawingContext.Commands.Count);
        RenderCommand shape = scene.DrawingContext.Commands.ToArray()[1];
        Assert.Equal(RenderCommandType.DrawPath, shape.Type);
        Assert.Equal(-0.5f, shape.Transform.M11, 5);
        Assert.Equal(0.0f, shape.Transform.M12, 5);
        Assert.Equal(0.0f, shape.Transform.M21, 5);
        Assert.Equal(-0.5f, shape.Transform.M22, 5);
    }

    [Fact]
    public void ComplexShxTextPatternReusesCachedGlyphPaths()
    {
        CadShxGlyphCache cache = CreateTextCache();
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add complex SHX text linetype", document =>
        {
            var textStyle = new TextStyle("TESTSHX") { Filename = "test.shx" };
            document.TextStyles.Add(textStyle);
            var complex = new LineType("SHX_TEXT_LINE");
            complex.AddSegment(new LineType.Segment { Length = 4.0 });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            complex.AddSegment(new LineType.Segment
            {
                Text = "A",
                Style = textStyle,
                Scale = 2.0,
                Flags = LineTypeShapeFlags.Text,
            });
            complex.AddSegment(new LineType.Segment { Length = -2.0 });
            document.LineTypes.Add(complex);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(24, 0, 0))
            {
                LineType = complex,
            });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { ShxFontResolver = new FixedShxFontResolver(cache) });
        var compiler = new CadPlanSceneCompiler();
        using var chunkCache = new CadPlanChunkCache();
        var chunkOptions = new CadPlanSceneOptions { ChunkCache = chunkCache };
        using CadRecordedPlanScene scene = compiler.Compile(snapshot);
        using CadRecordedPlanScene firstScene = compiler.Compile(snapshot, chunkOptions);
        using CadRecordedPlanScene cachedScene = compiler.Compile(snapshot, chunkOptions);

        Assert.Equal(CadLineTypeElementKind.ShxText, snapshot.LineTypeElements.Span[2].Kind);
        Assert.Single(snapshot.LineTypeTextResources.ToArray());
        Assert.Single(snapshot.ShxGlyphInstances.ToArray());
        Assert.Equal(1, cachedScene.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(
            scene.Statistics.LoweredLineTypePlacementCount,
            cachedScene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(3, scene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(4, scene.DrawingContext.Commands.Count);
        Assert.Equal(1, cache.Count);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Same(commands[1].Path, commands[2].Path);
        Assert.Same(commands[1].Path, commands[3].Path);
    }

    [Fact]
    public void ComplexPlacementLimitFallsBackTransactionally()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add dense complex linetype", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var complex = new LineType("DENSE_TEXT");
            complex.AddSegment(new LineType.Segment { Length = 1.0 });
            complex.AddSegment(new LineType.Segment { Length = -1.0 });
            complex.AddSegment(new LineType.Segment
            {
                Text = "X",
                Style = textStyle,
                Flags = LineTypeShapeFlags.Text,
            });
            document.LineTypes.Add(complex);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(20, 0, 0))
            {
                LineType = complex,
            });
        });
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions { MaxLineTypePlacements = 2 });

        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(RenderCommandType.DrawLine, Assert.Single(scene.DrawingContext.Commands).Type);
        Assert.Contains("placement", Assert.Single(scene.Diagnostics.ToArray()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComplexFontSubstitutionIsRetainedAndReportedOncePerPattern()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add substituted complex linetype", document =>
        {
            var textStyle = new TextStyle("MISSING") { Filename = "missing.ttf" };
            document.TextStyles.Add(textStyle);
            var complex = new LineType("SUBSTITUTED");
            complex.AddSegment(new LineType.Segment { Length = 2.0 });
            complex.AddSegment(new LineType.Segment { Length = -1.0 });
            complex.AddSegment(new LineType.Segment
            {
                Text = "X",
                Style = textStyle,
                Flags = LineTypeShapeFlags.Text,
            });
            document.LineTypes.Add(complex);
            document.Entities.Add(new Line(XYZ.Zero, new XYZ(8, 0, 0)) { LineType = complex });
            document.Entities.Add(new Line(XYZ.AxisY, new XYZ(8, 1, 0)) { LineType = complex });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(
                    InterFontFamily.Regular,
                    isSubstitution: true),
            });
        var compiler = new CadPlanSceneCompiler();
        using var chunkCache = new CadPlanChunkCache();
        var chunkOptions = new CadPlanSceneOptions { ChunkCache = chunkCache };
        using CadRecordedPlanScene firstScene = compiler.Compile(snapshot, chunkOptions);
        using CadRecordedPlanScene scene = compiler.Compile(snapshot, chunkOptions);

        Assert.True(Assert.Single(snapshot.LineTypeTextResources.ToArray()).IsSubstitution);
        Assert.Equal(2, scene.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(0, scene.Statistics.UnsupportedLineTypeCount);
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());
        Assert.Equal("CADSCENE003", diagnostic.Code);
        Assert.Contains("substitution", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidComplexPatternRollsBackAllDefinitionOwnedResources()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add invalid complex linetype", document =>
        {
            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            var invalid = new LineType("INVALID_COMPLEX");
            invalid.AddSegment(new LineType.Segment
            {
                Text = "X",
                Style = textStyle,
                Flags = LineTypeShapeFlags.Text,
            });
            document.LineTypes.Add(invalid);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { LineType = invalid });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.LineTypePatterns.ToArray());
        Assert.Empty(snapshot.LineTypeElements.ToArray());
        Assert.Empty(snapshot.LineTypeTextResources.ToArray());
        Assert.Empty(snapshot.TextGlyphIndices.ToArray());
        Assert.Empty(snapshot.TextGlyphRuns.ToArray());
        Assert.Empty(snapshot.TextFonts.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidGlobalLineTypeScaleRejectsSnapshot(double value)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Set invalid LTSCALE", document => document.Header.LineTypeScale = value);

        Assert.Throws<ArgumentException>(() => new CadSnapshotCompiler().Compile(session));
    }

    [Fact]
    public void InvalidSimplePatternIsRejectedPerEntityWithoutPartialSceneState()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add invalid pattern", document =>
        {
            LineType invalid = AddSimpleLineType(document, "INVALID", -1.0, 2.0);
            document.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX) { LineType = invalid });
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.LineTypePatterns.ToArray());
        Assert.Empty(snapshot.LineTypeElements.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), item =>
            item.Code == "CADSNAP002" && item.Message.Contains("non-negative first", StringComparison.Ordinal));
    }

    private static CadRecordedPlanScene CompilePolyline(bool isContinuous)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add polyline", document =>
        {
            LineType dashed = AddSimpleLineType(document, "PLINE_DASH", 4.0, -2.0);
            var polyline = new LwPolyline
            {
                LineType = dashed,
                Flags = isContinuous ? LwPolylineFlags.Plinegen : 0,
            };
            polyline.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
            polyline.Vertices.Add(new LwPolyline.Vertex(new XY(4, 0)));
            polyline.Vertices.Add(new LwPolyline.Vertex(new XY(8, 0)));
            document.Entities.Add(polyline);
        });
        return new CadPlanSceneCompiler().Compile(new CadSnapshotCompiler().Compile(session));
    }

    private static LineType AddSimpleLineType(
        ACadSharp.CadDocument document,
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

    private sealed class FixedTextFontResolver(
        TtfFont font,
        bool isSubstitution = false) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(font, isSubstitution);
    }

    private sealed class FixedShxFontResolver(CadShxGlyphCache cache) : ICadShxFontResolver
    {
        public CadShxFontResolution Resolve(in CadShxFontRequest request) =>
            new(cache, "boxes.shx", false);
    }

    private static CadShxGlyphCache CreateShapeCache()
    {
        const ushort shapeNumber = 230;
        byte[] program = [0x10, 0x14, 0x18, 0x1C, 0];
        byte[] name = Encoding.ASCII.GetBytes("BOX");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapeNumber);
        writer.Write(shapeNumber);
        writer.Write((ushort)1);
        writer.Write(shapeNumber);
        writer.Write(checked((ushort)(name.Length + 1 + program.Length)));
        writer.Write(name);
        writer.Write((byte)0);
        writer.Write(program);
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }

    private static CadShxGlyphCache CreateTextCache()
    {
        (ushort Number, string Name, byte[] Program)[] shapes =
        {
            (0, "TESTSHX", new byte[] { 10, 2, 0, 0 }),
            (65, "UCA", new byte[] { 0xA4, 0xA0, 2, 8, 20, 0xF6, 0 }),
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("AutoCAD-86 shapes 1.0\r\n\x1A"u8);
        writer.Write(shapes.Min(static shape => shape.Number));
        writer.Write(shapes.Max(static shape => shape.Number));
        writer.Write(checked((ushort)shapes.Length));
        foreach ((ushort number, string name, byte[] program) in shapes)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            writer.Write(number);
            writer.Write(checked((ushort)(nameBytes.Length + 1 + program.Length)));
        }
        foreach ((ushort _, string name, byte[] program) in shapes)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
            writer.Write(program);
        }
        writer.Write("EOF"u8);
        return new CadShxGlyphCache(CadShxFont.Parse(stream.ToArray()));
    }

    private static void AssertLineFigure(PathFigure figure, float startX, float endX)
    {
        Assert.InRange(Math.Abs(figure.StartPoint.X - startX), 0f, (float)Tolerance);
        LineSegment segment = Assert.IsType<LineSegment>(Assert.Single(figure.Segments));
        Assert.InRange(Math.Abs(segment.Point.X - endX), 0f, (float)Tolerance);
        Assert.InRange(Math.Abs(figure.StartPoint.Y), 0f, (float)Tolerance);
        Assert.InRange(Math.Abs(segment.Point.Y), 0f, (float)Tolerance);
    }

    private static Vector2 GetFigureEnd(PathFigure figure) =>
        Assert.IsType<LineSegment>(Assert.Single(figure.Segments)).Point;
}
