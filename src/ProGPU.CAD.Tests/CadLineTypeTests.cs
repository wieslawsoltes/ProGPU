using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Numerics;
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
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadLineTypePatternKind.Complex, Assert.Single(snapshot.LineTypePatterns.ToArray()).Kind);
        Assert.Equal(RenderCommandType.DrawLine, Assert.Single(scene.DrawingContext.Commands).Type);
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
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        CadLineTypeElement textElement = snapshot.LineTypeElements.Span[2];
        Assert.Equal(CadLineTypeElementKind.TrueTypeText, textElement.Kind);
        Assert.Equal(CadLineTypeRotationMode.Absolute, textElement.RotationMode);
        Assert.Single(snapshot.LineTypeTextResources.ToArray());
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
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.False(cache.Font.IsTextFont);
        Assert.Equal(CadLineTypeElementKind.ShxShape, snapshot.LineTypeElements.Span[2].Kind);
        Assert.Single(snapshot.LineTypeShapeResources.ToArray());
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
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(CadLineTypeElementKind.ShxText, snapshot.LineTypeElements.Span[2].Kind);
        Assert.Single(snapshot.LineTypeTextResources.ToArray());
        Assert.Single(snapshot.ShxGlyphInstances.ToArray());
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
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.True(Assert.Single(snapshot.LineTypeTextResources.ToArray()).IsSubstitution);
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
