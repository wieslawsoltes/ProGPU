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
            Hatch hatch = CreateSolidHatch();
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
        Assert.All(command.Path!.Figures.SelectMany(figure => figure.Segments), segment =>
        {
            Vector2ValueIsSmall(segment);
        });
    }

    [Fact]
    public void UnsupportedHatchesAreTransactionalAndDiagnosedWithoutApproximation()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Add unsupported hatches", document =>
        {
            Hatch patterned = CreateSolidHatch();
            patterned.IsSolid = false;
            patterned.Pattern = new HatchPattern("ANSI31");
            patterned.Paths.Add(CreatePolylineLoop(
                (0.0, 0.0, 0.0),
                (1.0, 0.0, 0.0),
                (1.0, 1.0, 0.0),
                (0.0, 1.0, 0.0)));
            document.Entities.Add(patterned);

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
            var edge = new Hatch.BoundaryPath.Spline { Degree = 2 };
            edge.ControlPoints.AddRange(
            [
                new XYZ(4, 0, 1),
                new XYZ(5, 1, 1),
                new XYZ(4, 0, 1),
            ]);
            edge.Knots.AddRange([0.0, 0.0, 0.0, 1.0, 1.0, 1.0]);
            splineLoop.Edges.Add(edge);
            spline.Paths.Add(splineLoop);
            document.Entities.Add(spline);
        });

        CadDocumentSnapshot snapshot = Compile(session);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Hatches.ToArray());
        Assert.Empty(snapshot.HatchLoops.ToArray());
        Assert.Empty(snapshot.HatchSegments.ToArray());
        Assert.Equal(3, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), item => item.Message.Contains("Patterned and gradient", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), item => item.Message.Contains("island-depth", StringComparison.Ordinal));
        Assert.Contains(snapshot.Diagnostics.ToArray(), item => item.Message.Contains("Spline HATCH", StringComparison.Ordinal));
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

    private static Hatch CreateSolidHatch() => new()
    {
        IsSolid = true,
        Pattern = HatchPattern.Solid,
        PatternType = HatchPatternType.SolidFill,
        Style = HatchStyleType.Normal,
        Normal = XYZ.AxisZ,
    };

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

    private static CadDocumentSnapshot Compile(CadDocumentSession session) =>
        new CadSnapshotCompiler().Compile(session);

    private static CadSelectionCandidate Candidate(
        CadDocumentSnapshot snapshot,
        CadEntityHeader entity) =>
        new(snapshot.ContentGeneration, 0, entity.Handle, entity.Kind, entity.Bounds);

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
