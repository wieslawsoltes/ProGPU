using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadHatchSnapshotTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void PolylineLoopsRetainOneEvenOddFillWithExactHoleSelectionAndOutputParity()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong handle = 0;
        session.Edit("Add polygon hatch with island", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (20.0, 0.0, 0.0),
                (20.0, 20.0, 0.0),
                (0.0, 20.0, 0.0)));
            hatch.Paths.Add(CreatePolylineLoop(
                (5.0, 5.0, 0.0),
                (15.0, 5.0, 0.0),
                (15.0, 15.0, 0.0),
                (5.0, 15.0, 0.0)));
            document.Entities.Add(hatch);
            handle = hatch.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadHatchPrimitive hatch = Assert.Single(snapshot.Hatches.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(handle, entity.Handle);
        Assert.Equal(CadEntityKind.Hatch, entity.Kind);
        Assert.Equal(2, hatch.LoopCount);
        Assert.Equal(8, snapshot.HatchSegments.Length);
        Assert.False(hatch.HasCurvedSegments);
        AssertPoint(new CadPoint3D(0, 0, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(20, 20, 0), entity.Bounds.Max);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.Equal(RenderCommandType.DrawPath, command.Type);
        Assert.NotNull(command.Brush);
        Assert.Null(command.Pen);
        Assert.Equal(FillRule.EvenOdd, command.Path!.FillRule);
        Assert.Equal(2, command.Path.Figures.Count);
        Assert.All(command.Path.Figures, figure => Assert.True(figure.IsClosed));

        CadSelectionCandidate candidate = Candidate(snapshot, entity);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(2, 2, 0),
                0.0).Status);
        Assert.Equal(
            CadPointHitStatus.Miss,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(10, 10, 0),
                0.0).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(new CadPoint3D(1, 1, -1), new CadPoint3D(3, 3, 1)),
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(new CadPoint3D(7, 7, -1), new CadPoint3D(13, 13, 1)),
                CadBoundsSelectionMode.Crossing).Status);

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
        Assert.Equal(1, printPlan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(1, page.GetCommand(1).Picture!.CommandCount);
    }

    [Fact]
    public void AllIslandStylesRetainSourceLoopsAndSelectTheirExactFilledRegions()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add all hatch island styles", document =>
        {
            for (int styleIndex = 0; styleIndex < 3; styleIndex++)
            {
                double x = styleIndex * 40.0;
                Hatch hatch = CreateSolidHatch();
                hatch.Style = (HatchStyleType)styleIndex;
                AddThreeNestedRectangleLoops(hatch, x);
                document.Entities.Add(hatch);
            }
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.Equal(3, snapshot.Hatches.Length);
        Assert.Equal(9, snapshot.HatchLoops.Length);
        Assert.Equal(
            new[] { true, true, true, true, true, false, true, false, false },
            snapshot.HatchLoops.ToArray().Select(loop => loop.ContributesToFill).ToArray());

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand[] commands = scene.DrawingContext.Commands.ToArray();
        Assert.Equal(new[] { 3, 2, 1 }, commands.Select(command => command.Path!.Figures.Count));
        Assert.All(commands, command => Assert.Equal(FillRule.EvenOdd, command.Path!.FillRule));

        CadEntityHeader[] entities = snapshot.Entities.ToArray();
        for (int styleIndex = 0; styleIndex < entities.Length; styleIndex++)
        {
            double x = styleIndex * 40.0;
            CadSelectionCandidate candidate = Candidate(
                snapshot,
                entities[styleIndex],
                styleIndex);
            Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, candidate, x + 2, 2));
            Assert.Equal(
                styleIndex == (int)HatchStyleType.Ignore
                    ? CadPointHitStatus.Hit
                    : CadPointHitStatus.Miss,
                PointStatus(snapshot, candidate, x + 7, 7));
            Assert.Equal(
                styleIndex == (int)HatchStyleType.Outer
                    ? CadPointHitStatus.Miss
                    : CadPointHitStatus.Hit,
                PointStatus(snapshot, candidate, x + 12, 12));
            Assert.Equal(
                styleIndex == (int)HatchStyleType.Outer
                    ? CadBoundsHitStatus.Miss
                    : CadBoundsHitStatus.Hit,
                CadSelectionHitTester.HitTestBounds(
                    snapshot,
                    candidate,
                    new CadBounds3D(
                        new CadPoint3D(x + 11, 11, -1),
                        new CadPoint3D(x + 13, 13, 1)),
                    CadBoundsSelectionMode.Crossing).Status);
        }

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
        Assert.Equal(3, printPlan.SceneStatistics.RecordedEntityCount);
        Assert.Equal(3, page.GetCommand(1).Picture!.CommandCount);
    }

    [Fact]
    public void CurvedOuterIslandClassificationRemainsAnalytic()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add curved outer hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Style = HatchStyleType.Outer;
            foreach (double radius in new[] { 10.0, 6.0, 2.0 })
            {
                var loop = new Hatch.BoundaryPath();
                loop.Edges.Add(new Hatch.BoundaryPath.Arc
                {
                    Center = XY.Zero,
                    Radius = radius,
                    StartAngle = 0.0,
                    EndAngle = Math.PI * 2.0,
                    CounterClockWise = true,
                });
                hatch.Paths.Add(loop);
            }
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(
            new[] { true, true, false },
            snapshot.HatchLoops.ToArray().Select(loop => loop.ContributesToFill).ToArray());
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());
        Assert.Equal(2, command.Path!.Figures.Count);
        Assert.All(command.Path.Figures.SelectMany(figure => figure.Segments), segment =>
            Assert.IsType<ArcSegment>(segment));
        CadSelectionCandidate candidate = Candidate(snapshot, entity);
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, candidate, 8, 0));
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, candidate, 4, 0));
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, candidate, 0, 0));
    }

    [Fact]
    public void CubicSplineEdgeRetainsExactBoundsFillSelectionAndNativeOutput()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add exact cubic spline hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Paths.Add(CreateCubicSplineCapLoop());
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadHatchPrimitive hatch = Assert.Single(snapshot.Hatches.ToArray());
        CadHatchSegment[] retained = snapshot.HatchSegments.ToArray();
        Assert.Equal(
            new[] { CadHatchSegmentKind.CubicBezier, CadHatchSegmentKind.Line },
            retained.Select(segment => segment.Kind));
        Assert.True(hatch.HasCurvedSegments);
        AssertPoint(new CadPoint3D(0, 0, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(10, 7.5, 0), entity.Bounds.Max);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        PathFigure figure = Assert.Single(command.Path!.Figures);
        var cubic = Assert.IsType<CubicBezierSegment>(figure.Segments[0]);
        Assert.Equal(new System.Numerics.Vector2(0, 10), cubic.ControlPoint1);
        Assert.Equal(new System.Numerics.Vector2(10, 10), cubic.ControlPoint2);
        Assert.Equal(new System.Numerics.Vector2(10, 0), cubic.Point);
        Assert.IsType<LineSegment>(figure.Segments[1]);

        CadSelectionCandidate candidate = Candidate(snapshot, entity);
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, candidate, 5, 2));
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, candidate, 5, 7.5));
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, candidate, 5, 8));
        CadPointHitResult proximity = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 7.6, 0),
            0.11);
        Assert.Equal(CadPointHitStatus.Hit, proximity.Status);
        Assert.Equal(0.1, proximity.Distance, 10);
        CadPointHitResult proximityMiss = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 7.6, 0),
            0.05);
        Assert.Equal(CadPointHitStatus.Miss, proximityMiss.Status);
        Assert.Equal(0.1, proximityMiss.Distance, 10);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(4.9, 7.4, -1),
                    new CadPoint3D(5.1, 7.6, 1)),
                CadBoundsSelectionMode.Crossing).Status);

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
        Assert.Single(page.GetCommand(1).Picture!.Commands.ToArray());
    }

    [Fact]
    public void PatternedSplineEdgeRetainsProceduralBrushAndNativeOutput()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add patterned spline-edge hatch", document =>
        {
            Hatch hatch = CreatePatternedHatch(
                "SPLINE_PATTERN",
                HatchPatternType.PatternFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(0, 2),
                offset: new XY(0, 4));
            hatch.Paths.Add(CreateCubicSplineCapLoop());
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        Assert.IsType<HatchPatternBrush>(command.Brush);
        Assert.IsType<CubicBezierSegment>(command.Path!.Figures[0].Segments[0]);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void QuadraticSplineEdgesRetainExactPolynomialAndRationalSegments()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add exact quadratic spline hatches", document =>
        {
            for (int i = 0; i < 4; i++)
            {
                double x = i * 20.0;
                Hatch hatch = CreateSolidHatch();
                var loop = new Hatch.BoundaryPath();
                var spline = new Hatch.BoundaryPath.Spline
                {
                    Degree = 2,
                    IsRational = i != 0,
                };
                double startWeight = i == 0 ? 0.0 : i == 1 ? 2.0 : i == 3 ? 4.0 : 1.0;
                double endWeight = i == 0 ? 0.0 : i == 1 ? 2.0 : 1.0;
                double middleWeight = i >= 2 ? 1.0 : startWeight;
                spline.ControlPoints.AddRange([
                    new XYZ(x, 0, startWeight),
                    new XYZ(x + 5, 10, i == 2 ? 0.5 : middleWeight),
                    new XYZ(x + 10, 0, endWeight),
                ]);
                spline.Knots.AddRange([0, 0, 0, 1, 1, 1]);
                loop.Edges.Add(spline);
                loop.Edges.Add(new Hatch.BoundaryPath.Line
                {
                    Start = new XY(x + 10, 0),
                    End = new XY(x, 0),
                });
                hatch.Paths.Add(loop);
                document.Entities.Add(hatch);
            }
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.Equal(4, snapshot.Hatches.Length);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(
            new[]
            {
                CadHatchSegmentKind.QuadraticBezier,
                CadHatchSegmentKind.Line,
                CadHatchSegmentKind.QuadraticBezier,
                CadHatchSegmentKind.Line,
                CadHatchSegmentKind.RationalQuadraticBezier,
                CadHatchSegmentKind.Line,
                CadHatchSegmentKind.RationalQuadraticBezier,
                CadHatchSegmentKind.Line,
            },
            snapshot.HatchSegments.ToArray().Select(segment => segment.Kind));
        Assert.Equal(5.0, snapshot.Entities.Span[0].Bounds.Max.Y, 12);
        Assert.Equal(5.0, snapshot.Entities.Span[1].Bounds.Max.Y, 12);
        Assert.Equal(10.0 / 3.0, snapshot.Entities.Span[2].Bounds.Max.Y, 11);
        Assert.Equal(10.0 / 3.0, snapshot.Entities.Span[3].Bounds.Max.Y, 11);
        Assert.Equal(0.5, snapshot.HatchSegments.Span[4].Weight, 12);
        Assert.Equal(0.5, snapshot.HatchSegments.Span[6].Weight, 12);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(
                    snapshot,
                    snapshot.Entities.Span[2],
                    entityIndex: 2),
                new CadPoint3D(45.0, 10.0 / 3.0, 0.0),
                1e-9).Status);

        RenderCommand[] commands = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .DrawingContext.Commands.ToArray();
        Assert.IsType<QuadraticBezierSegment>(commands[0].Path!.Figures[0].Segments[0]);
        Assert.IsType<QuadraticBezierSegment>(commands[1].Path!.Figures[0].Segments[0]);
        var rational = Assert.IsType<RationalQuadraticBezierSegment>(
            commands[2].Path!.Figures[0].Segments[0]);
        Assert.Equal(0.5f, rational.Weight);
        Assert.False(rational.IsStroked);
        var reparameterized = Assert.IsType<RationalQuadraticBezierSegment>(
            commands[3].Path!.Figures[0].Segments[0]);
        Assert.Equal(0.5f, reparameterized.Weight);

        using GpuPicture picture = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void HatchAndStandaloneSplineShareIdenticalExactBoundaryEvaluation()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add differential spline records", document =>
        {
            var spline = new Spline { Degree = 3 };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 0),
                new XYZ(0, 10, 0),
                new XYZ(10, 10, 0),
                new XYZ(10, 0, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
            document.Entities.Add(spline);
            Hatch hatch = CreateSolidHatch();
            hatch.Paths.Add(CreateCubicSplineCapLoop());
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.Single(snapshot.Splines.ToArray());
        CadEntityHeader standalone = snapshot.Entities.Span[0];
        CadEntityHeader hatch = snapshot.Entities.Span[1];
        foreach (CadPoint3D boundaryPoint in new[]
        {
            new CadPoint3D(0, 0, 0),
            new CadPoint3D(5, 7.5, 0),
            new CadPoint3D(10, 0, 0),
        })
        {
            Assert.Equal(
                CadPointHitStatus.Hit,
                CadSelectionHitTester.HitTestPoint(
                    snapshot,
                    Candidate(snapshot, standalone),
                    boundaryPoint,
                    1e-9).Status);
            Assert.Equal(
                CadPointHitStatus.Hit,
                CadSelectionHitTester.HitTestPoint(
                    snapshot,
                    Candidate(snapshot, hatch, entityIndex: 1),
                    boundaryPoint,
                    1e-9).Status);
        }
    }

    [Fact]
    public void OuterIslandClassificationUsesExactSplineContainment()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add spline outer-island hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Style = HatchStyleType.Outer;
            hatch.Paths.Add(CreateCubicSplineCapLoop());
            hatch.Paths.Add(CreatePolylineLoop(
                (4.0, 1.0, 0.0),
                (6.0, 1.0, 0.0),
                (6.0, 3.0, 0.0),
                (4.0, 3.0, 0.0)));
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.Equal(
            new[] { true, true },
            snapshot.HatchLoops.ToArray().Select(loop => loop.ContributesToFill));
        CadSelectionCandidate candidate = Candidate(snapshot, snapshot.Entities.Span[0]);
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, candidate, 2, 2));
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, candidate, 5, 2));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PeriodicQuadraticSplineEdgeRetainsExactClosedTopology(
        bool expandedKnots,
        bool rational)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add periodic spline hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            var loop = new Hatch.BoundaryPath();
            var spline = new Hatch.BoundaryPath.Spline
            {
                Degree = 2,
                IsPeriodic = true,
                IsRational = rational,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, rational ? 1.0 : 0.0),
                new XYZ(10, 0, rational ? 0.5 : 0.0),
                new XYZ(10, 10, rational ? 1.0 : 0.0),
                new XYZ(0, 10, rational ? 0.5 : 0.0),
            ]);
            spline.Knots.AddRange(expandedKnots
                ? [-2, -1, 0, 1, 2, 3, 4, 5, 6]
                : [0, 1, 2, 3, 4]);
            loop.Edges.Add(spline);
            hatch.Paths.Add(loop);
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.True(
            snapshot.HatchLoops.Length == 1,
            string.Join(Environment.NewLine, snapshot.Diagnostics.ToArray().Select(item => item.Message)));
        CadHatchLoop loop = snapshot.HatchLoops.Span[0];
        Assert.Equal(4, loop.SegmentCount);
        if (rational)
        {
            Assert.Contains(snapshot.HatchSegments.ToArray(), segment =>
                segment.Kind == CadHatchSegmentKind.RationalQuadraticBezier);
        }
        else
        {
            Assert.All(snapshot.HatchSegments.ToArray(), segment =>
                Assert.Equal(CadHatchSegmentKind.QuadraticBezier, segment.Kind));
        }
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(
            CadPointHitStatus.Hit,
            PointStatus(snapshot, Candidate(snapshot, snapshot.Entities.Span[0]), 5, 5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeriodicRationalCubicSplineEdgeRetainsExactClosedTopology(
        bool expandedKnots)
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add periodic rational cubic spline hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            var loop = new Hatch.BoundaryPath();
            var spline = new Hatch.BoundaryPath.Spline
            {
                Degree = 3,
                IsPeriodic = true,
                IsRational = true,
            };
            spline.ControlPoints.AddRange([
                new XYZ(0, 0, 1.0),
                new XYZ(10, 0, 0.5),
                new XYZ(10, 10, 1.5),
                new XYZ(0, 10, 0.75),
            ]);
            spline.Knots.AddRange(expandedKnots
                ? [-3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7]
                : [0, 1, 2, 3, 4]);
            loop.Edges.Add(spline);
            hatch.Paths.Add(loop);
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.True(
            snapshot.HatchLoops.Length == 1,
            string.Join(Environment.NewLine, snapshot.Diagnostics.ToArray().Select(item => item.Message)));
        Assert.Equal(4, snapshot.HatchLoops.Span[0].SegmentCount);
        Assert.All(snapshot.HatchSegments.ToArray(), segment =>
            Assert.Equal(CadHatchSegmentKind.RationalCubicBezier, segment.Kind));
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(
            CadPointHitStatus.Hit,
            PointStatus(snapshot, Candidate(snapshot, snapshot.Entities.Span[0]), 5, 5));
    }

    [Fact]
    public void NonUniformRationalCubicSplineRetainsExactSegmentWhileHigherDegreeFailsTransactionally()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add rational cubic and unsupported higher-degree spline hatches", document =>
        {
            Hatch rational = CreateSolidHatch();
            Hatch.BoundaryPath rationalLoop = CreateCubicSplineCapLoop();
            var rationalEdge = (Hatch.BoundaryPath.Spline)rationalLoop.Edges[0];
            rationalEdge.IsRational = true;
            rationalEdge.ControlPoints[0] = new XYZ(0, 0, 8);
            rationalEdge.ControlPoints[1] = new XYZ(0, 10, 2);
            rationalEdge.ControlPoints[2] = new XYZ(10, 10, 3);
            rationalEdge.ControlPoints[3] = new XYZ(10, 0, 1);
            rational.Paths.Add(rationalLoop);
            document.Entities.Add(rational);

            Hatch highDegree = CreateSolidHatch();
            var highDegreeLoop = new Hatch.BoundaryPath();
            var edge = new Hatch.BoundaryPath.Spline { Degree = 4 };
            edge.ControlPoints.AddRange([
                new XYZ(20, 0, 0),
                new XYZ(22, 5, 0),
                new XYZ(25, 8, 0),
                new XYZ(28, 5, 0),
                new XYZ(30, 0, 0),
            ]);
            edge.Knots.AddRange([0, 0, 0, 0, 0, 1, 1, 1, 1, 1]);
            highDegreeLoop.Edges.Add(edge);
            highDegree.Paths.Add(highDegreeLoop);
            document.Entities.Add(highDegree);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Single(snapshot.Hatches.ToArray());
        Assert.Single(snapshot.HatchLoops.ToArray());
        Assert.Equal(2, snapshot.HatchSegments.Length);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("Degree-4 HATCH spline", StringComparison.Ordinal));

        CadHatchSegment retained = snapshot.HatchSegments.Span[0];
        Assert.Equal(CadHatchSegmentKind.RationalCubicBezier, retained.Kind);
        Assert.Equal(0.5, retained.Weight, 12);
        Assert.Equal(1.5, retained.Weight2, 12);
        Assert.True(entity.Bounds.Max.X >= 6.875);
        Assert.True(entity.Bounds.Max.Y >= 7.5);

        CadSelectionCandidate candidate = Candidate(snapshot, entity);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(6.875, 7.5, 0.0),
                1e-10).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(6.87, 7.49, -0.1),
                    new CadPoint3D(6.88, 7.51, 0.1)),
                CadBoundsSelectionMode.Crossing).Status);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        var rationalSegment = Assert.IsType<RationalCubicBezierSegment>(
            Assert.Single(scene.DrawingContext.Commands.ToArray())
                .Path!.Figures[0].Segments[0]);
        Assert.Equal(0.5f, rationalSegment.Weight1);
        Assert.Equal(1.5f, rationalSegment.Weight2);
        Assert.False(rationalSegment.IsStroked);
        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void RationalCubicBernsteinBoundsContainExactHomogeneousEvaluation()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add symmetric rational cubic spline hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            Hatch.BoundaryPath loop = CreateCubicSplineCapLoop();
            var spline = (Hatch.BoundaryPath.Spline)loop.Edges[0];
            spline.IsRational = true;
            spline.ControlPoints[0] = new XYZ(0, 0, 4);
            spline.ControlPoints[1] = new XYZ(0, 10, 2);
            spline.ControlPoints[2] = new XYZ(10, 10, 2);
            spline.ControlPoints[3] = new XYZ(10, 0, 4);
            hatch.Paths.Add(loop);
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        Assert.Equal(0.0, entity.Bounds.Min.X, 12);
        Assert.Equal(0.0, entity.Bounds.Min.Y, 12);
        Assert.Equal(10.0, entity.Bounds.Max.X, 12);
        Assert.Equal(6.0, entity.Bounds.Max.Y, 11);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, entity),
                new CadPoint3D(5.0, 6.0, 0.0),
                1e-10).Status);
    }

    [Fact]
    public void OuterIslandClassificationIsPathOrderIndependentAcrossDisconnectedRegions()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add shuffled outer hatch loops", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Style = HatchStyleType.Outer;
            hatch.Paths.Add(CreatePolylineLoop(
                (10.0, 10.0, 0.0),
                (20.0, 10.0, 0.0),
                (20.0, 20.0, 0.0),
                (10.0, 20.0, 0.0)));
            hatch.Paths.Add(CreatePolylineLoop(
                (40.0, 0.0, 0.0),
                (50.0, 0.0, 0.0),
                (50.0, 10.0, 0.0),
                (40.0, 10.0, 0.0)));
            hatch.Paths.Add(CreatePolylineLoop(
                (5.0, 5.0, 0.0),
                (25.0, 5.0, 0.0),
                (25.0, 25.0, 0.0),
                (5.0, 25.0, 0.0)));
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (30.0, 0.0, 0.0),
                (30.0, 30.0, 0.0),
                (0.0, 30.0, 0.0)));
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.Equal(
            new[] { false, true, true, true },
            snapshot.HatchLoops.ToArray().Select(loop => loop.ContributesToFill).ToArray());
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());
        Assert.Equal(3, command.Path!.Figures.Count);
        CadSelectionCandidate candidate = Candidate(snapshot, snapshot.Entities.Span[0]);
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, candidate, 15, 15));
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, candidate, 45, 5));
    }

    [Fact]
    public void PatternedOuterAndIgnoreStylesClipTheSameProceduralGrammar()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add patterned island styles", document =>
        {
            foreach (HatchStyleType style in new[] { HatchStyleType.Outer, HatchStyleType.Ignore })
            {
                double x = style == HatchStyleType.Outer ? 0.0 : 40.0;
                Hatch hatch = CreatePatternedHatch(
                    style.ToString(),
                    HatchPatternType.PatternFill,
                    isDouble: false,
                    angle: 0.0,
                    basePoint: new XY(x, 0.0),
                    offset: new XY(0.0, 1.0));
                hatch.Style = style;
                AddThreeNestedRectangleLoops(hatch, x);
                document.Entities.Add(hatch);
            }
        });

        CadDocumentSnapshot snapshot = Compile(session);
        RenderCommand[] commands = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .DrawingContext.Commands.ToArray();
        Assert.Equal(new[] { 2, 1 }, commands.Select(command => command.Path!.Figures.Count));
        Assert.All(commands, command => Assert.IsType<HatchPatternBrush>(command.Brush));

        CadEntityHeader[] entities = snapshot.Entities.ToArray();
        CadSelectionCandidate outer = Candidate(snapshot, entities[0], 0);
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, outer, 2, 2));
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, outer, 7, 7));
        Assert.Equal(CadPointHitStatus.Miss, PointStatus(snapshot, outer, 12, 12));
        CadSelectionCandidate ignore = Candidate(snapshot, entities[1], 1);
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, ignore, 42, 2));
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, ignore, 47, 7));
        Assert.Equal(CadPointHitStatus.Hit, PointStatus(snapshot, ignore, 52, 12));
        Assert.Equal(
            CadBoundsHitStatus.UnsupportedGeometry,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                ignore,
                new CadBounds3D(
                    new CadPoint3D(41, 1, -1),
                    new CadPoint3D(43, 3, 1)),
                CadBoundsSelectionMode.Crossing).Status);

        using GpuPicture picture = new CadPlanSceneCompiler().Compile(snapshot).CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    [Fact]
    public void CircularAndEllipticLoopsRemainAnalyticAndUseDirectionAwareParity()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add analytic hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            var outer = new Hatch.BoundaryPath();
            outer.Edges.Add(new Hatch.BoundaryPath.Arc
            {
                Center = XY.Zero,
                Radius = 10,
                StartAngle = 0,
                EndAngle = Math.PI * 2,
                CounterClockWise = true,
            });
            var island = new Hatch.BoundaryPath();
            island.Edges.Add(new Hatch.BoundaryPath.Ellipse
            {
                Center = XY.Zero,
                MajorAxisEndPoint = new XY(4, 0),
                RadiusRatio = 0.5,
                StartAngle = 0,
                EndAngle = Math.PI * 2,
                CounterClockWise = false,
            });
            hatch.Paths.Add(outer);
            hatch.Paths.Add(island);
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadHatchPrimitive hatch = Assert.Single(snapshot.Hatches.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());

        Assert.True(hatch.HasCurvedSegments);
        Assert.Equal(2, snapshot.HatchSegments.Length);
        Assert.All(snapshot.HatchSegments.ToArray(), segment =>
            Assert.Equal(CadHatchSegmentKind.EllipticArc, segment.Kind));
        AssertPoint(new CadPoint3D(-10, -10, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(10, 10, 0), entity.Bounds.Max);
        Assert.Equal(2, command.Path!.Figures.Count);
        Assert.All(command.Path.Figures, figure => Assert.Equal(2, figure.Segments.Count));
        Assert.All(command.Path.Figures.SelectMany(figure => figure.Segments), segment =>
            Assert.IsType<ArcSegment>(segment));

        CadSelectionCandidate candidate = Candidate(snapshot, entity);
        Assert.Equal(
            CadPointHitStatus.Miss,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                CadPoint3D.Zero,
                0.0).Status);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(7, 0, 0),
                0.0).Status);
        Assert.Equal(
            CadBoundsHitStatus.Miss,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(new CadPoint3D(-1, -1, -1), new CadPoint3D(1, 1, 1)),
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(new CadPoint3D(9, -1, -1), new CadPoint3D(11, 1, 1)),
                CadBoundsSelectionMode.Crossing).Status);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            scene.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);

    }

    [Fact]
    public void OrderedLineAndArcEdgesRemainOneClosedAnalyticContour()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add edge-defined stadium hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            var loop = new Hatch.BoundaryPath();
            loop.Edges.Add(new Hatch.BoundaryPath.Line
            {
                Start = new XY(0, 0),
                End = new XY(10, 0),
            });
            loop.Edges.Add(new Hatch.BoundaryPath.Arc
            {
                Center = new XY(10, 5),
                Radius = 5,
                StartAngle = -Math.PI / 2,
                EndAngle = Math.PI / 2,
                CounterClockWise = true,
            });
            loop.Edges.Add(new Hatch.BoundaryPath.Line
            {
                Start = new XY(10, 10),
                End = new XY(0, 10),
            });
            loop.Edges.Add(new Hatch.BoundaryPath.Arc
            {
                Center = new XY(0, 5),
                Radius = 5,
                StartAngle = Math.PI / 2,
                EndAngle = Math.PI * 1.5,
                CounterClockWise = true,
            });
            hatch.Paths.Add(loop);
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.Equal(4, snapshot.HatchSegments.Length);
        Assert.Equal(2, snapshot.HatchSegments.ToArray().Count(segment =>
            segment.Kind == CadHatchSegmentKind.EllipticArc));
        AssertPoint(new CadPoint3D(-5, 0, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(15, 10, 0), entity.Bounds.Max);
        Assert.Equal(4, Assert.Single(command.Path!.Figures).Segments.Count);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, entity),
                new CadPoint3D(12, 5, 0),
                0.0).Status);
    }

    [Fact]
    public void BulgedPolylineAndTiltedOcsRetainExactAnalyticGeometry()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add tilted bulged hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Normal = XYZ.AxisY;
            hatch.Elevation = 3;
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 1.0),
                (10.0, 0.0, 0.0),
                (10.0, 4.0, 0.0),
                (0.0, 4.0, 0.0)));
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadHatchPrimitive hatch = Assert.Single(snapshot.Hatches.ToArray());
        CadHatchSegment arc = snapshot.HatchSegments.Span[0];
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.Equal(CadHatchSegmentKind.EllipticArc, arc.Kind);
        Assert.Equal(Math.PI, arc.SweepParameter, 12);
        AssertPoint(new CadPoint3D(0, 3, 0), hatch.WorldOrigin);
        AssertPoint(new CadPoint3D(-1, 0, 0), hatch.CoordinateSystem.XAxis);
        AssertPoint(new CadPoint3D(0, 0, 1), hatch.CoordinateSystem.YAxis);
        AssertPoint(new CadPoint3D(-10, 3, -5), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(0, 3, 4), entity.Bounds.Max);
        Assert.IsType<ArcSegment>(Assert.Single(command.Path!.Figures).Segments[0]);
        Assert.Equal(
            CadPointHitStatus.UnsupportedGeometry,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, entity),
                new CadPoint3D(-5, 3, 0),
                0.0).Status);
    }

    [Fact]
    public void NestedHatchPreservesRootStyleLayerAndOneAffineReplayTransform()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        ulong insertHandle = 0;
        session.Edit("Add transformed hatch block", document =>
        {
            var layer = new Layer("HATCHES") { Color = ACadSharp.Color.Green };
            document.Layers.Add(layer);
            Hatch hatch = CreatePatternedHatch(
                "USER",
                HatchPatternType.PatternFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(1_000_002.0, 2_000_003.0),
                offset: new XY(4, 5));
            hatch.Color = ACadSharp.Color.ByBlock;
            hatch.Paths.Add(CreatePolylineLoop(
                (1_000_000.0, 2_000_000.0, 0.0),
                (1_000_010.0, 2_000_000.0, 0.0),
                (1_000_010.0, 2_000_005.0, 0.0),
                (1_000_000.0, 2_000_005.0, 0.0)));
            var block = new BlockRecord("HATCH_BLOCK");
            block.Entities.Add(hatch);
            var insert = new Insert(block)
            {
                InsertPoint = new XYZ(30, 40, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 2,
                Layer = layer,
                Color = ACadSharp.Color.Red,
            };
            document.Entities.Add(insert);
            insertHandle = insert.Handle;
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadHatchPrimitive hatch = Assert.Single(snapshot.Hatches.ToArray());
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.Equal(insertHandle, entity.Handle);
        Assert.Equal("HATCHES", snapshot.Layers.Span[entity.LayerIndex].Name);
        CadStrokeStyle style = snapshot.Styles.Span[entity.StyleIndex];
        Assert.Equal(byte.MaxValue, style.Red);
        Assert.Equal((byte)0, style.Green);
        AssertPoint(new CadPoint3D(-5_999_970, 2_000_040, 0), hatch.WorldOrigin);
        AssertPoint(new CadPoint3D(0, 2, 0), hatch.CoordinateSystem.XAxis);
        AssertPoint(new CadPoint3D(-3, 0, 0), hatch.CoordinateSystem.YAxis);
        Assert.NotEqual(System.Numerics.Matrix4x4.Identity, command.Transform);
        CadHatchPattern pattern = Assert.Single(snapshot.HatchPatterns.ToArray());
        CadHatchPatternFamily family = snapshot.HatchPatternFamilies.Span[pattern.FamilyOffset];
        Assert.Equal(2.0, family.BasePointX, 12);
        Assert.Equal(3.0, family.BasePointY, 12);
        var patternBrush = Assert.IsType<HatchPatternBrush>(command.Brush);
        Assert.Equal(-2.0f, patternBrush.CoordinateTransform.M41);
        Assert.Equal(-0.5f, patternBrush.CoordinateTransform.M42);
        Assert.All(command.Path!.Figures.SelectMany(figure => figure.Segments), segment =>
        {
            Vector2ValueIsSmall(segment);
        });
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, entity),
                new CadPoint3D(-5_999_979, 2_000_050, 0),
                0.0).Status);
    }

    [Fact]
    public void ContinuousPatternRetainsExactFamilyOriginAndAnalyticBoundary()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add continuous patterned hatch", document =>
        {
            Hatch hatch = CreatePatternedHatch(
                "USER",
                HatchPatternType.PatternFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(2, 3),
                offset: new XY(4, 5));
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (20.0, 0.0, 0.0),
                (20.0, 20.0, 0.0),
                (0.0, 20.0, 0.0)));
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadHatchPrimitive hatch = Assert.Single(snapshot.Hatches.ToArray());
        CadHatchPattern pattern = Assert.Single(snapshot.HatchPatterns.ToArray());
        CadHatchPatternFamily family = snapshot.HatchPatternFamilies.Span[pattern.FamilyOffset];
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.Equal(0, hatch.PatternIndex);
        Assert.Equal(1, pattern.FamilyCount);
        Assert.Equal(2.0, family.BasePointX, 12);
        Assert.Equal(3.0, family.BasePointY, 12);
        Assert.Equal(1.0, family.DirectionX, 12);
        Assert.Equal(0.0, family.DirectionY, 12);
        Assert.Equal(4.0, family.TangentShift, 12);
        Assert.Equal(5.0, family.Spacing, 12);
        Assert.Equal(0, family.DashCount);
        var brush = Assert.IsType<HatchPatternBrush>(command.Brush);
        Assert.Equal(MathF.PI / 2.0f, brush.Angle, 5);
        Assert.Equal(5.0f, brush.Spacing);
        Assert.Equal(0.0f, brush.Thickness);
        Assert.Equal(-2.0f, brush.CoordinateTransform.M41);
        Assert.Equal(-0.5f, brush.CoordinateTransform.M42);
        Assert.All(command.Path!.Figures.SelectMany(figure => figure.Segments), segment =>
            Assert.IsType<LineSegment>(segment));

        CadSelectionCandidate candidate = Candidate(snapshot, entity);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(10, 3, 0),
                0.0).Status);
        CadPointHitResult miss = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(10, 5, 0),
            0.0);
        Assert.Equal(CadPointHitStatus.Miss, miss.Status);
        Assert.Equal(2.0, miss.Distance, 12);
        Assert.Equal(
            CadBoundsHitStatus.UnsupportedGeometry,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(9, 2, -1),
                    new CadPoint3D(11, 4, 1)),
                CadBoundsSelectionMode.Crossing).Status);

        using GpuPicture picture = new CadPlanSceneCompiler().Compile(snapshot).CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            snapshot.ContentGeneration,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);

        using CadPrintPlan patternPrintPlan = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture patternPage = patternPrintPlan.CreatePagePicture();
        RenderCommand retainedPatternCommand = Assert.Single(
            patternPage.GetCommand(1).Picture!.Commands.ToArray());
        Assert.IsType<HatchPatternBrush>(retainedPatternCommand.Brush);
    }

    [Fact]
    public void UserDefinedDoubleAddsPerpendicularFamilyButPredefinedDoubleIsIgnored()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add double patterned hatches", document =>
        {
            Hatch user = CreatePatternedHatch(
                "USER",
                HatchPatternType.PatternFill,
                isDouble: true,
                angle: 0.0,
                basePoint: new XY(1, 2),
                offset: new XY(0, 4));
            user.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (10.0, 0.0, 0.0),
                (10.0, 10.0, 0.0),
                (0.0, 10.0, 0.0)));
            document.Entities.Add(user);

            Hatch predefined = CreatePatternedHatch(
                "ANSI31",
                HatchPatternType.SolidFill,
                isDouble: true,
                angle: Math.PI / 4.0,
                basePoint: new XY(20, 0),
                offset: new XY(-3, 3));
            predefined.Paths.Add(CreatePolylineLoop(
                (20.0, 0.0, 0.0),
                (30.0, 0.0, 0.0),
                (30.0, 10.0, 0.0),
                (20.0, 10.0, 0.0)));
            document.Entities.Add(predefined);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadHatchPattern[] patterns = snapshot.HatchPatterns.ToArray();
        RenderCommand[] commands = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .DrawingContext.Commands.ToArray();

        Assert.Equal(2, patterns[0].FamilyCount);
        Assert.Equal(1, patterns[1].FamilyCount);
        Assert.IsType<CrossHatchBrush>(commands[0].Brush);
        Assert.IsType<HatchPatternBrush>(commands[1].Brush);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);

        CadEntityHeader userEntity = snapshot.Entities.Span[0];
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, userEntity),
                new CadPoint3D(1, 7, 0),
                0.0).Status);
    }

    [Fact]
    public void PatternGrammarAndOuterStyleRetainWhileHighDegreeSplineBoundaryIsTransactional()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add unsupported hatches", document =>
        {
            Hatch patterned = CreateSolidHatch();
            patterned.IsSolid = false;
            patterned.Pattern = new HatchPattern("ANSI31");
            patterned.Pattern.Lines.Add(new HatchPattern.Line
            {
                Angle = Math.PI / 4.0,
                BasePoint = XY.Zero,
                Offset = new XY(-2, 2),
                DashLengths = { 1.0, -1.0 },
            });
            patterned.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (1.0, 0.0, 0.0),
                (1.0, 1.0, 0.0),
                (0.0, 1.0, 0.0)));
            document.Entities.Add(patterned);

            Hatch multiFamily = CreatePatternedHatch(
                "GRID",
                HatchPatternType.SolidFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(4, 0),
                offset: new XY(0, 1));
            multiFamily.Pattern.Lines.Add(new HatchPattern.Line
            {
                Angle = Math.PI / 2.0,
                BasePoint = new XY(4, 0),
                Offset = new XY(-1, 0),
            });
            multiFamily.Paths.Add(CreatePolylineLoop(
                (4.0, 0.0, 0.0),
                (5.0, 0.0, 0.0),
                (5.0, 1.0, 0.0),
                (4.0, 1.0, 0.0)));
            document.Entities.Add(multiFamily);

            Hatch outerStyle = CreateSolidHatch();
            outerStyle.Style = HatchStyleType.Outer;
            outerStyle.Paths.Add(CreatePolylineLoop(
                (2.0, 0.0, 0.0),
                (3.0, 0.0, 0.0),
                (3.0, 1.0, 0.0),
                (2.0, 1.0, 0.0)));
            document.Entities.Add(outerStyle);

            Hatch spline = CreateSolidHatch();
            var splineLoop = new Hatch.BoundaryPath();
            var edge = new Hatch.BoundaryPath.Spline { Degree = 4 };
            edge.ControlPoints.AddRange(
            [
                new XYZ(4, 0, 1),
                new XYZ(5, 1, 1),
                new XYZ(5.5, 0.5, 1),
                new XYZ(5, 0, 1),
                new XYZ(4, 0, 1),
            ]);
            edge.Knots.AddRange([0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0, 1.0]);
            splineLoop.Edges.Add(edge);
            spline.Paths.Add(splineLoop);
            document.Entities.Add(spline);
        });

        CadDocumentSnapshot snapshot = Compile(session);

        Assert.Equal(3, snapshot.Entities.Length);
        Assert.Equal(3, snapshot.Hatches.Length);
        Assert.Equal(2, snapshot.HatchPatterns.Length);
        Assert.Equal(3, snapshot.HatchPatternFamilies.Length);
        Assert.Equal(new[] { 1.0, -1.0 }, snapshot.HatchPatternDashes.ToArray());
        Assert.Equal(3, snapshot.HatchLoops.Length);
        Assert.Equal(12, snapshot.HatchSegments.Length);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), item =>
            item.Message.Contains("Degree-4 HATCH spline", StringComparison.Ordinal));
        RenderCommand[] commands = new CadPlanSceneCompiler()
            .Compile(snapshot)
            .DrawingContext.Commands.ToArray();
        Assert.IsType<HatchPatternSetBrush>(commands[0].Brush);
        Assert.IsType<CrossHatchBrush>(commands[1].Brush);
        Assert.IsType<SolidColorBrush>(commands[2].Brush);

        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, snapshot.Entities.Span[0]),
                new CadPoint3D(0.25, 0.25, 0),
                0.0).Status);
        Assert.Equal(
            CadPointHitStatus.Miss,
            CadSelectionHitTester.HitTestPoint(
                snapshot,
                Candidate(snapshot, snapshot.Entities.Span[0]),
                new CadPoint3D(0.9, 0.9, 0),
                0.0).Status);
    }

    [Fact]
    public void HatchBudgetsRejectTheWholePrimitiveWithoutLeakingSegments()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add budgeted hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (10.0, 0.0, 0.0),
                (10.0, 10.0, 0.0),
                (0.0, 10.0, 0.0)));
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { MaxHatchSegments = 3 });

        Assert.Empty(snapshot.Hatches.ToArray());
        Assert.Empty(snapshot.HatchLoops.ToArray());
        Assert.Empty(snapshot.HatchSegments.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), item =>
            item.Message.Contains("3-segment HATCH document limit", StringComparison.Ordinal));
    }

    [Fact]
    public void HatchSplineSourceBudgetIsDocumentWideAndRejectsWithoutLeakingStreams()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add source-budgeted spline hatches", document =>
        {
            for (int i = 0; i < 2; i++)
            {
                Hatch hatch = CreateSolidHatch();
                Hatch.BoundaryPath loop = CreateCubicSplineCapLoop(i * 20.0);
                hatch.Paths.Add(loop);
                document.Entities.Add(hatch);
            }
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { MaxHatchSplineSourceValues = 30 });

        Assert.Single(snapshot.Entities.ToArray());
        Assert.Single(snapshot.Hatches.ToArray());
        Assert.Single(snapshot.HatchLoops.ToArray());
        Assert.Equal(2, snapshot.HatchSegments.Length);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains(
                "30-value document source limit",
                StringComparison.Ordinal));
    }

    [Fact]
    public void IslandTopologyBudgetIsDocumentWideAndFailedPrimitiveLeaksNoStreams()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add topology-budgeted hatches", document =>
        {
            for (int i = 0; i < 2; i++)
            {
                Hatch hatch = CreateSolidHatch();
                hatch.Style = HatchStyleType.Outer;
                double x = i * 20.0;
                hatch.Paths.Add(CreatePolylineLoop(
                    (x, 0.0, 0.0),
                    (x + 10.0, 0.0, 0.0),
                    (x + 10.0, 10.0, 0.0),
                    (x, 10.0, 0.0)));
                hatch.Paths.Add(CreatePolylineLoop(
                    (x + 2.0, 2.0, 0.0),
                    (x + 8.0, 2.0, 0.0),
                    (x + 8.0, 8.0, 0.0),
                    (x + 2.0, 8.0, 0.0)));
                document.Entities.Add(hatch);
            }
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { MaxHatchTopologyVisits = 6 });

        Assert.Single(snapshot.Entities.ToArray());
        Assert.Single(snapshot.Hatches.ToArray());
        Assert.Equal(2, snapshot.HatchLoops.Length);
        Assert.Equal(8, snapshot.HatchSegments.Length);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("6-visit document topology limit", StringComparison.Ordinal));
    }

    [Fact]
    public void CoincidentOuterLoopsAreDiagnosedWithoutPublishingPartialStreams()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add coincident outer hatch loops", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Style = HatchStyleType.Outer;
            for (int i = 0; i < 2; i++)
            {
                hatch.Paths.Add(CreatePolylineLoop(
                    (0.0, 0.0, 0.0),
                    (10.0, 0.0, 0.0),
                    (10.0, 10.0, 0.0),
                    (0.0, 10.0, 0.0)));
            }
            document.Entities.Add(hatch);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Hatches.ToArray());
        Assert.Empty(snapshot.HatchLoops.ToArray());
        Assert.Empty(snapshot.HatchSegments.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("coincident or touch", StringComparison.Ordinal));
    }

    [Fact]
    public void PatternFamiliesRetainSixDashGrammarAndNormalizeNegativeRowDirection()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add bounded PAT grammar", document =>
        {
            Hatch valid = CreatePatternedHatch(
                "SIX",
                HatchPatternType.SolidFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(2, 3),
                offset: new XY(4, -5));
            valid.Pattern.Lines[0].DashLengths.AddRange([2.0, -1.0, 0.0, -0.5, 3.0, -2.0]);
            valid.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (20.0, 0.0, 0.0),
                (20.0, 20.0, 0.0),
                (0.0, 20.0, 0.0)));
            document.Entities.Add(valid);

            Hatch invalid = CreatePatternedHatch(
                "SEVEN",
                HatchPatternType.SolidFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(30, 0),
                offset: new XY(0, 2));
            invalid.Pattern.Lines[0].DashLengths.AddRange([1.0, -1.0, 1.0, -1.0, 1.0, -1.0, 1.0]);
            invalid.Paths.Add(CreatePolylineLoop(
                (30.0, 0.0, 0.0),
                (40.0, 0.0, 0.0),
                (40.0, 10.0, 0.0),
                (30.0, 10.0, 0.0)));
            document.Entities.Add(invalid);
        });

        CadDocumentSnapshot snapshot = Compile(session);
        CadHatchPattern pattern = Assert.Single(snapshot.HatchPatterns.ToArray());
        CadHatchPatternFamily family = snapshot.HatchPatternFamilies.Span[pattern.FamilyOffset];
        Assert.Equal(1.0, family.DirectionX, 12);
        Assert.Equal(0.0, family.DirectionY, 12);
        Assert.Equal(-4.0, family.TangentShift, 12);
        Assert.Equal(5.0, family.Spacing, 12);
        Assert.Equal(6, family.DashCount);
        Assert.Equal(8.5, family.DashPeriod, 12);
        Assert.Equal(
            new[] { 2.0, -1.0, 0.0, -0.5, 3.0, -2.0 },
            snapshot.HatchPatternDashes.ToArray());
        Assert.IsType<HatchPatternSetBrush>(Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray()).Brush);
        InvalidOperationException budgetFailure = Assert.Throws<InvalidOperationException>(() =>
            new CadPlanSceneCompiler().Compile(
                snapshot,
                new CadPlanSceneOptions { MaxHatchPatternAuxiliaryRecords = 3 }));
        Assert.Contains("3 retained auxiliary records", budgetFailure.Message, StringComparison.Ordinal);
        CadSelectionCandidate candidate = Candidate(snapshot, snapshot.Entities.Span[0]);
        Assert.Equal(1_000, CountPatternHits(snapshot, candidate, 1_000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int hits = CountPatternHits(snapshot, candidate, 1_000);
        Assert.Equal(1_000, hits);
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before) / 1_000);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Message.Contains("6-dash PAT definition limit", StringComparison.Ordinal));
    }

    [Fact]
    public void PatternBudgetRejectsOnlyTheExcessPrimitiveWithoutLeakingBoundaryData()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add budgeted patterns", document =>
        {
            for (int i = 0; i < 2; i++)
            {
                double x = i * 4.0;
                Hatch hatch = CreatePatternedHatch(
                    $"USER{i}",
                    HatchPatternType.PatternFill,
                    isDouble: false,
                    angle: 0.0,
                    basePoint: new XY(x, 0),
                    offset: new XY(0, 1));
                hatch.Paths.Add(CreatePolylineLoop(
                    (x, 0.0, 0.0),
                    (x + 2.0, 0.0, 0.0),
                    (x + 2.0, 2.0, 0.0),
                    (x, 2.0, 0.0)));
                document.Entities.Add(hatch);
            }
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions { MaxHatchPatterns = 1 });

        Assert.Single(snapshot.Entities.ToArray());
        Assert.Single(snapshot.Hatches.ToArray());
        Assert.Single(snapshot.HatchPatterns.ToArray());
        Assert.Single(snapshot.HatchLoops.ToArray());
        Assert.Equal(4, snapshot.HatchSegments.Length);
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), item =>
            item.Message.Contains("1-pattern HATCH document limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinuousPatternSurvivesDxfSaveReloadWithAuthoritativeLineRecords()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add saved patterned hatch", document =>
        {
            Hatch hatch = CreatePatternedHatch(
                "USER",
                HatchPatternType.PatternFill,
                isDouble: false,
                angle: Math.PI / 6.0,
                basePoint: new XY(2, 3),
                offset: new XY(-2, 2 * Math.Sqrt(3)));
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (8.0, 0.0, 0.0),
                (8.0, 6.0, 0.0),
                (0.0, 6.0, 0.0)));
            document.Entities.Add(hatch);
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "continuous-pattern-hatch-roundtrip.dxf");

        CadDocumentSnapshot snapshot = Compile(loaded.Session);
        CadHatchPattern pattern = Assert.Single(snapshot.HatchPatterns.ToArray());
        CadHatchPatternFamily family = snapshot.HatchPatternFamilies.Span[pattern.FamilyOffset];
        Assert.Equal(Math.Sqrt(3) / 2.0, family.DirectionX, 12);
        Assert.Equal(0.5, family.DirectionY, 12);
        Assert.Equal(0.0, family.TangentShift, 12);
        Assert.Equal(4.0, family.Spacing, 12);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.IsType<HatchPatternBrush>(Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray()).Brush);
    }

    [Fact]
    public async Task MultiFamilyDashGapDotPatternSurvivesDxfSaveReload()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add saved multi-family pattern", document =>
        {
            Hatch hatch = CreatePatternedHatch(
                "GRID_DASH",
                HatchPatternType.SolidFill,
                isDouble: false,
                angle: 0.0,
                basePoint: new XY(2, 3),
                offset: new XY(4, 5));
            hatch.Pattern.Lines[0].DashLengths.AddRange([2.0, -1.0, 0.0, -3.0]);
            hatch.Pattern.Lines.Add(new HatchPattern.Line
            {
                Angle = Math.PI / 2.0,
                BasePoint = new XY(5, 1),
                Offset = new XY(-6, 2),
                DashLengths = { 1.5, -0.5 },
            });
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (12.0, 0.0, 0.0),
                (12.0, 10.0, 0.0),
                (0.0, 10.0, 0.0)));
            document.Entities.Add(hatch);
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "multi-family-pattern-hatch-roundtrip.dxf");

        CadDocumentSnapshot snapshot = Compile(loaded.Session);
        CadHatchPattern pattern = Assert.Single(snapshot.HatchPatterns.ToArray());
        Assert.Equal(2, pattern.FamilyCount);
        Assert.Equal(6, snapshot.HatchPatternDashes.Length);
        Assert.Equal(
            new[] { 2.0, -1.0, 0.0, -3.0, 1.5, -0.5 },
            snapshot.HatchPatternDashes.ToArray());
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.IsType<HatchPatternSetBrush>(Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray()).Brush);
    }

    [Fact]
    public async Task OuterAndIgnoreIslandStylesSurviveDxfSaveReload()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add saved island styles", document =>
        {
            foreach (HatchStyleType style in new[] { HatchStyleType.Outer, HatchStyleType.Ignore })
            {
                double x = style == HatchStyleType.Outer ? 0.0 : 40.0;
                Hatch hatch = CreateSolidHatch();
                hatch.Style = style;
                AddThreeNestedRectangleLoops(hatch, x);
                document.Entities.Add(hatch);
            }
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "hatch-island-styles-roundtrip.dxf");

        CadDocumentSnapshot snapshot = Compile(loaded.Session);
        Assert.Equal(2, snapshot.Hatches.Length);
        Assert.Equal(6, snapshot.HatchLoops.Length);
        Assert.Equal(
            new[] { true, true, false, true, false, false },
            snapshot.HatchLoops.ToArray().Select(loop => loop.ContributesToFill).ToArray());
        Assert.Equal(
            new[] { 2, 1 },
            new CadPlanSceneCompiler()
                .Compile(snapshot)
                .DrawingContext.Commands.ToArray()
                .Select(command => command.Path!.Figures.Count));
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public async Task PolynomialSplineHatchEdgeSurvivesDxfSaveReload()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add saved spline-edge hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Paths.Add(CreateCubicSplineCapLoop());
            document.Entities.Add(hatch);
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "polynomial-spline-edge-hatch-roundtrip.dxf");

        CadDocumentSnapshot snapshot = Compile(loaded.Session);
        Assert.Equal(
            new[] { CadHatchSegmentKind.CubicBezier, CadHatchSegmentKind.Line },
            snapshot.HatchSegments.ToArray().Select(segment => segment.Kind));
        Assert.Equal(7.5, snapshot.Entities.Span[0].Bounds.Max.Y, 12);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        Assert.IsType<CubicBezierSegment>(Assert.Single(
            new CadPlanSceneCompiler()
                .Compile(snapshot)
                .DrawingContext.Commands.ToArray())
            .Path!.Figures[0].Segments[0]);
    }

    [Fact]
    public async Task RationalCubicSplineHatchEdgeSurvivesDxfSaveReload()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add saved rational cubic spline-edge hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            Hatch.BoundaryPath loop = CreateCubicSplineCapLoop();
            var spline = (Hatch.BoundaryPath.Spline)loop.Edges[0];
            spline.IsRational = true;
            spline.ControlPoints[0] = new XYZ(0, 0, 8);
            spline.ControlPoints[1] = new XYZ(0, 10, 2);
            spline.ControlPoints[2] = new XYZ(10, 10, 3);
            spline.ControlPoints[3] = new XYZ(10, 0, 1);
            hatch.Paths.Add(loop);
            document.Entities.Add(hatch);
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "rational-cubic-spline-edge-hatch-roundtrip.dxf");

        CadDocumentSnapshot snapshot = Compile(loaded.Session);
        Assert.Equal(
            new[] { CadHatchSegmentKind.RationalCubicBezier, CadHatchSegmentKind.Line },
            snapshot.HatchSegments.ToArray().Select(segment => segment.Kind));
        Assert.Equal(0.5, snapshot.HatchSegments.Span[0].Weight, 12);
        Assert.Equal(1.5, snapshot.HatchSegments.Span[0].Weight2, 12);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
        var segment = Assert.IsType<RationalCubicBezierSegment>(Assert.Single(
            new CadPlanSceneCompiler()
                .Compile(snapshot)
                .DrawingContext.Commands.ToArray())
            .Path!.Figures[0].Segments[0]);
        Assert.Equal(0.5f, segment.Weight1);
        Assert.Equal(1.5f, segment.Weight2);
    }

    [Fact]
    public async Task SolidHatchSurvivesDxfSaveReloadAndRetainsZeroAllocationLineSelection()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Add saved hatch", document =>
        {
            Hatch hatch = CreateSolidHatch();
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (8.0, 0.0, 0.0),
                (8.0, 6.0, 0.0),
                (0.0, 6.0, 0.0)));
            document.Entities.Add(hatch);
        });
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();
        await store.SaveAsync(
            session,
            stream,
            CadDocumentFormat.Dxf,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(
            stream,
            CadDocumentFormat.Dxf,
            sourceName: "solid-hatch-roundtrip.dxf");
        CadDocumentSnapshot snapshot = Compile(loaded.Session);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadSelectionCandidate candidate = Candidate(snapshot, entity);

        for (int i = 0; i < 32; i++)
        {
            _ = CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(2, 2, 0),
                0.25);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int hitCount = 0;
        for (int i = 0; i < 1_000; i++)
        {
            CadPointHitResult result = CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                new CadPoint3D(2, 2, 0),
                0.25);
            if (result.Status == CadPointHitStatus.Hit)
            {
                hitCount++;
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1_000, hitCount);
        Assert.Equal(0, allocated);
        Assert.Single(snapshot.Hatches.ToArray());
        Assert.Equal(4, snapshot.HatchSegments.Length);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void PlanChunkCacheSharesPatternedHatchAndRestoresItsGlobalBudget()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add repeated affine patterned hatch", document =>
        {
            var block = new BlockRecord("PATTERN_TILE");
            Hatch hatch = CreatePatternedHatch(
                "DASH_TILE",
                HatchPatternType.PatternFill,
                isDouble: false,
                angle: 0.0,
                basePoint: XY.Zero,
                offset: new XY(0, 2));
            hatch.Pattern.Lines[0].DashLengths.AddRange([1.0, -1.0]);
            hatch.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (10.0, 0.0, 0.0),
                (10.0, 10.0, 0.0),
                (0.0, 10.0, 0.0)));
            block.Entities.Add(hatch);
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(100, 200, 0),
                XScale = 2,
                YScale = 3,
                Rotation = Math.PI / 5,
            });
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(-50, 75, 0),
                XScale = -4,
                YScale = 1.5,
                Rotation = -Math.PI / 7,
            });
        });
        CadDocumentSnapshot snapshot = Compile(session);
        var compiler = new CadPlanSceneCompiler();
        var options = new CadPlanSceneOptions
        {
            ChunkCache = new CadPlanChunkCache(),
            MaxHatchPatternAuxiliaryRecords = 8,
        };
        using (options.ChunkCache)
        using (CadRecordedPlanScene baseline = compiler.Compile(
            snapshot,
            new CadPlanSceneOptions { MaxHatchPatternAuxiliaryRecords = 8 }))
        using (GpuPicture baselinePicture = baseline.CreatePicture())
        using (CadRecordedPlanScene first = compiler.Compile(snapshot, options))
        using (GpuPicture firstPicture = first.CreatePicture())
        using (CadRecordedPlanScene second = compiler.Compile(snapshot, options))
        using (GpuPicture secondPicture = second.CreatePicture())
        {
            Assert.Equal(2, first.Statistics.RetainedChunkCount);
            Assert.Equal(1, first.Statistics.ReusedRetainedChunkCount);
            Assert.Equal(2, second.Statistics.ReusedRetainedChunkCount);
            Assert.Same(
                firstPicture.GetCommand(0).Picture,
                firstPicture.GetCommand(1).Picture);
            Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
                baselinePicture,
                701U,
                snapshot.ContentGeneration,
                out NativeCompiledPicture? baselineNative,
                out NativePictureCompileFailure baselineFailure),
                baselineFailure.ToString());
            Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
                secondPicture,
                702U,
                snapshot.ContentGeneration,
                out NativeCompiledPicture? cachedNative,
                out NativePictureCompileFailure cachedFailure),
                cachedFailure.ToString());
            Assert.Equal(baselineNative!.NativeDrawCount, cachedNative!.NativeDrawCount);
            Assert.Equal(baselineNative.PathCount, cachedNative.PathCount);
            Assert.Equal(baselineNative.PathSegmentCount, cachedNative.PathSegmentCount);
        }

        using var constrainedCache = new CadPlanChunkCache();
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            compiler.Compile(
                snapshot,
                new CadPlanSceneOptions
                {
                    ChunkCache = constrainedCache,
                    MaxHatchPatternAuxiliaryRecords = 4,
                }));
        Assert.Contains("4 retained auxiliary records", failure.Message, StringComparison.Ordinal);
    }

    private static Hatch CreateSolidHatch() => new()
    {
        IsSolid = true,
        Pattern = HatchPattern.Solid,
        PatternType = HatchPatternType.SolidFill,
        Style = HatchStyleType.Normal,
        Normal = XYZ.AxisZ,
    };

    private static Hatch CreatePatternedHatch(
        string name,
        HatchPatternType patternType,
        bool isDouble,
        double angle,
        XY basePoint,
        XY offset)
    {
        var definition = new HatchPattern(name);
        var hatch = new Hatch
        {
            IsSolid = false,
            Pattern = definition,
            PatternType = patternType,
            IsDouble = isDouble,
            PatternAngle = 0.125,
            PatternScale = 2.0,
            Style = HatchStyleType.Normal,
            Normal = XYZ.AxisZ,
        };
        definition.Lines.Add(new HatchPattern.Line
        {
            Angle = angle,
            BasePoint = basePoint,
            Offset = offset,
        });
        return hatch;
    }

    private static Hatch.BoundaryPath CreatePolylineLoop(
        params (double X, double Y, double Bulge)[] vertices)
    {
        var polyline = new Hatch.BoundaryPath.Polyline { IsClosed = true };
        foreach ((double x, double y, double bulge) in vertices)
        {
            polyline.Vertices.Add(new XYZ(x, y, bulge));
        }
        var path = new Hatch.BoundaryPath();
        path.Edges.Add(polyline);
        return path;
    }

    private static Hatch.BoundaryPath CreateCubicSplineCapLoop(double x = 0.0)
    {
        var spline = new Hatch.BoundaryPath.Spline { Degree = 3 };
        spline.ControlPoints.AddRange([
            new XYZ(x, 0, 0),
            new XYZ(x, 10, 0),
            new XYZ(x + 10, 10, 0),
            new XYZ(x + 10, 0, 0),
        ]);
        spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);

        var loop = new Hatch.BoundaryPath();
        loop.Edges.Add(spline);
        loop.Edges.Add(new Hatch.BoundaryPath.Line
        {
            Start = new XY(x + 10, 0),
            End = new XY(x, 0),
        });
        return loop;
    }

    private static void AddThreeNestedRectangleLoops(Hatch hatch, double x)
    {
        hatch.Paths.Add(CreatePolylineLoop(
            (x, 0.0, 0.0),
            (x + 30.0, 0.0, 0.0),
            (x + 30.0, 30.0, 0.0),
            (x, 30.0, 0.0)));
        hatch.Paths.Add(CreatePolylineLoop(
            (x + 5.0, 5.0, 0.0),
            (x + 25.0, 5.0, 0.0),
            (x + 25.0, 25.0, 0.0),
            (x + 5.0, 25.0, 0.0)));
        hatch.Paths.Add(CreatePolylineLoop(
            (x + 10.0, 10.0, 0.0),
            (x + 20.0, 10.0, 0.0),
            (x + 20.0, 20.0, 0.0),
            (x + 10.0, 20.0, 0.0)));
    }

    private static CadDocumentSnapshot Compile(CadDocumentSession session) =>
        new CadSnapshotCompiler().Compile(session);

    private static int CountPatternHits(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate,
        int count)
    {
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            if (CadSelectionHitTester.HitTestPoint(
                    snapshot,
                    candidate,
                    new CadPoint3D(2.5, 3, 0),
                    0.0).Status == CadPointHitStatus.Hit)
                hits++;
        }
        return hits;
    }

    private static CadSelectionCandidate Candidate(
        CadDocumentSnapshot snapshot,
        CadEntityHeader entity,
        int entityIndex = 0) =>
        new(
            snapshot.ContentGeneration,
            entityIndex,
            entity.Handle,
            entity.Kind,
            entity.Bounds);

    private static CadPointHitStatus PointStatus(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate,
        double x,
        double y) =>
        CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(x, y, 0.0),
            0.0).Status;

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0, Tolerance);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, Tolerance);
        Assert.InRange(Math.Abs(actual.Z - expected.Z), 0, Tolerance);
    }

    private static void Vector2ValueIsSmall(PathSegment segment)
    {
        System.Numerics.Vector2 point = segment switch
        {
            LineSegment line => line.Point,
            ArcSegment arc => arc.Point,
            _ => throw new InvalidOperationException(),
        };
        Assert.InRange(Math.Abs(point.X), 0, 20);
        Assert.InRange(Math.Abs(point.Y), 0, 20);
    }
}
