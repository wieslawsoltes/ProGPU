using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadWidePolylineTests
{
    [Fact]
    public void BulgeWidthKeepsAnalyticArcAndExactExpandedBounds()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());

        AssertPoint(new CadPoint3D(-1, -6, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(11, 0, 0), entity.Bounds.Max);
        PathFigure figure = Assert.Single(command.Path!.Figures);
        Assert.IsType<ArcSegment>(Assert.Single(figure.Segments));
        Assert.Equal(2.0f, command.Pen!.Thickness);
        Assert.Equal(PenStrokeTransformMode.Normal, command.Pen.StrokeTransformMode);
    }

    [Fact]
    public void NonuniformInsertTransformsSourceSpaceWidthWithoutChangingPenIdentity()
    {
        var document = new CadDocument();
        var block = new BlockRecord("WIDE_AFFINE");
        block.Entities.Add(CreateStraight(width: 2.0));
        block.Entities.Add(CreateStraight(width: 2.0, y: 4.0));
        document.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 3.0,
            ZScale = 1.0,
        });

        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader first = snapshot.Entities.Span[0];
        CadEntityHeader second = snapshot.Entities.Span[1];
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand firstCommand = scene.DrawingContext.Commands[0];
        RenderCommand secondCommand = scene.DrawingContext.Commands[1];

        AssertPoint(new CadPoint3D(0, -3, 0), first.Bounds.Min);
        AssertPoint(new CadPoint3D(20, 3, 0), first.Bounds.Max);
        AssertPoint(new CadPoint3D(0, 9, 0), second.Bounds.Min);
        AssertPoint(new CadPoint3D(20, 15, 0), second.Bounds.Max);
        Assert.Same(firstCommand.Pen, secondCommand.Pen);
        Assert.Equal(PenStrokeTransformMode.Normal, firstCommand.Pen!.StrokeTransformMode);
        Assert.Equal(2.0f, firstCommand.Pen.Thickness);
    }

    [Fact]
    public void FillModeOffStraightRetainsExactLineweightOutlineAndNativeParity()
    {
        var fillOff = new CadDocument();
        fillOff.Header.FillMode = false;
        LwPolyline straight = CreateStraight(width: 2.0);
        straight.LineWeight = LineWeightType.W25;
        fillOff.Entities.Add(straight);

        CadDocumentSnapshot snapshot = Compile(fillOff);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());

        Assert.False(primitive.IsFillEnabled);
        Assert.Null(command.Brush);
        Assert.NotNull(command.Pen);
        Assert.Equal(PenStrokeTransformMode.Fixed, command.Pen!.StrokeTransformMode);
        Assert.Equal(0.25f * 96.0f / 25.4f, command.Pen.Thickness, 5);
        PathFigure figure = Assert.Single(command.Path!.Figures);
        Assert.True(figure.IsClosed);
        Assert.False(figure.IsFilled);
        Assert.Equal(3, figure.Segments.Count);
        Assert.All(figure.Segments, segment => Assert.IsType<LineSegment>(segment));

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(1, native.SourceCommandCount);
        Assert.True(native.NativeDrawCount > 0);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = print.CreatePagePicture();
        RenderCommand replay = page.GetCommand(1);
        RenderCommand printOutline = replay.Picture!.GetCommand(0);
        Assert.Null(printOutline.Brush);
        Assert.NotNull(printOutline.Pen);
        Assert.True(Assert.Single(printOutline.Path!.Figures).IsClosed);
    }

    [Fact]
    public void FillModeOffBulgeRetainsExactConcentricArcSector()
    {
        var document = new CadDocument();
        document.Header.FillMode = false;
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        PathFigure figure = Assert.Single(command.Path!.Figures);
        ArcSegment outer = Assert.IsType<ArcSegment>(figure.Segments[0]);
        Assert.IsType<LineSegment>(figure.Segments[1]);
        ArcSegment inner = Assert.IsType<ArcSegment>(figure.Segments[2]);

        Assert.Null(command.Brush);
        Assert.NotNull(command.Pen);
        Assert.Equal(new Vector2(6, 6), outer.Size);
        Assert.Equal(new Vector2(4, 4), inner.Size);
        Assert.NotEqual(outer.SweepDirection, inner.SweepDirection);

        using GpuPicture picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? native,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(native);
        Assert.Equal(1, native.SourceCommandCount);
    }

    [Fact]
    public void FillModeOffBulgeAllowsInnerBoundaryToCollapseExactlyAtCenter()
    {
        var document = new CadDocument();
        document.Header.FillMode = false;
        var polyline = new LwPolyline { ConstantWidth = 10.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        PathFigure figure = Assert.Single(
            Assert.Single(scene.DrawingContext.Commands.ToArray()).Path!.Figures);

        Assert.True(figure.IsClosed);
        ArcSegment outer = Assert.IsType<ArcSegment>(figure.Segments[0]);
        LineSegment center = Assert.IsType<LineSegment>(figure.Segments[1]);
        Assert.Equal(new Vector2(10, 10), outer.Size);
        Assert.Equal(new Vector2(5, 0), center.Point);
        Assert.Equal(2, figure.Segments.Count);
    }

    [Fact]
    public void VariableWidthBulgesAndCenterCrossingOutlinesRemainExplicitlyUnsupported()
    {

        var variable = new CadDocument();
        var tapered = CreateStraight(width: 4.0);
        tapered.Vertices[0].StartWidth = 1.0;
        tapered.Vertices[0].EndWidth = 3.0;
        tapered.Vertices[0].Bulge = 0.5;
        variable.Entities.Add(tapered);

        var centerCrossing = new CadDocument();
        centerCrossing.Header.FillMode = false;
        var crossingBulge = new LwPolyline { ConstantWidth = 12.0 };
        crossingBulge.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        crossingBulge.Vertices.Add(new LwPolyline.Vertex(10, 0));
        centerCrossing.Entities.Add(crossingBulge);

        CadDocumentSnapshot variableSnapshot = Compile(variable);
        CadDocumentSnapshot crossingSnapshot = Compile(centerCrossing);

        Assert.Empty(variableSnapshot.Entities.ToArray());
        Assert.Equal(1, variableSnapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(variableSnapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("spiral-boundary", StringComparison.Ordinal));
        Assert.Empty(crossingSnapshot.Entities.ToArray());
        Assert.Contains(crossingSnapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("signed-inner-boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactSelectionTestsVisibleStraightStripRatherThanCenterline()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateStraight(width: 2.0));
        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            entity.Handle,
            entity.Kind,
            entity.Bounds);

        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0.75, 0),
            0.01);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(4.9, 0.7, -0.1),
                new CadPoint3D(5.1, 0.8, 0.1)),
            CadBoundsSelectionMode.Crossing);
        CadBoundsHitResult window = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(-1, -2, -1),
                new CadPoint3D(11, 2, 1)),
            CadBoundsSelectionMode.Window);
        CadBoundsHitResult partialWindow = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(0, -1, 0),
                new CadPoint3D(10, 0.99, 0)),
            CadBoundsSelectionMode.Window);

        Assert.Equal(CadPointHitStatus.Hit, point.Status);
        Assert.Equal(0.0, point.Distance, 10);
        Assert.Equal(CadBoundsHitStatus.Hit, crossing.Status);
        Assert.Equal(CadBoundsHitStatus.Hit, window.Status);
        Assert.Equal(CadBoundsHitStatus.Miss, partialWindow.Status);

        CadPointHitResult beyondFlatCap = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(10.25, 0, 0),
            0.01);
        CadPointHitResult outsideWidth = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 1.25, 0),
            0.01);

        Assert.Equal(CadPointHitStatus.Miss, beyondFlatCap.Status);
        Assert.Equal(0.25, beyondFlatCap.Distance, 10);
        Assert.Equal(CadPointHitStatus.Miss, outsideWidth.Status);
        Assert.Equal(0.25, outsideWidth.Distance, 10);
    }

    [Fact]
    public void ExactSelectionIncludesBevelJoinOutsideBothSegmentStrips()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0));
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 10));
        document.Entities.Add(polyline);

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(10.75, -0.20, 0),
            0.0);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(10.70, -0.25, -0.1),
                new CadPoint3D(10.80, -0.15, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadPointHitStatus.Hit, point.Status);
        Assert.Equal(0.0, point.Distance, 10);
        Assert.Equal(CadBoundsHitStatus.Hit, crossing.Status);
    }

    [Fact]
    public void ExactBulgeSelectionRetainsAnnularStripAndInteriorCrossing()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult inStrip = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, -5.5, 0),
            0.0);
        CadPointHitResult inHole = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, -2, 0),
            0.0);
        CadBoundsHitResult interiorCrossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(4.9, -5.6, -0.1),
                new CadPoint3D(5.1, -5.4, 0.1)),
            CadBoundsSelectionMode.Crossing);
        CadBoundsHitResult holeCrossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(4.9, -2.1, -0.1),
                new CadPoint3D(5.1, -1.9, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadPointHitStatus.Hit, inStrip.Status);
        Assert.Equal(0.0, inStrip.Distance, 10);
        Assert.Equal(CadPointHitStatus.Miss, inHole.Status);
        Assert.Equal(2.0, inHole.Distance, 9);
        Assert.Equal(CadBoundsHitStatus.Hit, interiorCrossing.Status);
        Assert.Equal(CadBoundsHitStatus.Miss, holeCrossing.Status);
    }

    [Fact]
    public void NegativeBulgeSelectionUsesTheOppositeAnalyticSweep()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = -1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult upperStrip = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 5.5, 0),
            0.0);
        CadPointHitResult mirroredMiss = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, -5.5, 0),
            0.0);

        Assert.Equal(CadPointHitStatus.Hit, upperStrip.Status);
        Assert.Equal(CadPointHitStatus.Miss, mirroredMiss.Status);
    }

    [Fact]
    public void ClosedPolylineSelectionIncludesTheFirstVertexBevel()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 2.0, IsClosed = true };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0));
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 10));
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 10));
        document.Entities.Add(polyline);

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult firstJoin = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(-0.2, -0.2, 0),
            0.0);

        Assert.Equal(CadPointHitStatus.Hit, firstJoin.Status);
        Assert.Equal(0.0, firstJoin.Distance, 10);
    }

    [Fact]
    public void WidthGreaterThanBulgeDiameterUsesSignedInnerRadius()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 12.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult center = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0, 0),
            0.0);
        CadPointHitResult oppositeSignedLobe = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0.5, 0),
            0.0);

        Assert.Equal(CadPointHitStatus.Hit, center.Status);
        Assert.Equal(CadPointHitStatus.Hit, oppositeSignedLobe.Status);
    }

    [Fact]
    public void WidthEqualToBulgeDiameterRetainsCollapsedInnerBoundary()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 10.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult center = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(5, 0, 0),
            0.0);
        CadBoundsHitResult centerCrossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(4.9, -0.1, -0.1),
                new CadPoint3D(5.1, 0.1, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadPointHitStatus.Hit, center.Status);
        Assert.Equal(0.0, center.Distance, 10);
        Assert.Equal(CadBoundsHitStatus.Hit, centerCrossing.Status);
    }

    [Fact]
    public void NonuniformAffineSelectionUsesTransformedSourceWidthAndPlaneDistance()
    {
        var document = new CadDocument();
        var block = new BlockRecord("WIDE_SELECTION_AFFINE");
        block.Entities.Add(CreateStraight(width: 2.0));
        document.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 3.0,
            ZScale = 1.0,
        });

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult inside = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(10, 2.5, 0.25),
            0.3);
        CadPointHitResult outside = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(10, 3.5, 0),
            0.0);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(9.9, 2.4, -0.1),
                new CadPoint3D(10.1, 2.6, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.Equal(CadPointHitStatus.Hit, inside.Status);
        Assert.Equal(0.25, inside.Distance, 10);
        Assert.Equal(CadPointHitStatus.Miss, outside.Status);
        Assert.Equal(0.5, outside.Distance, 10);
        Assert.Equal(CadBoundsHitStatus.Hit, crossing.Status);
    }

    [Fact]
    public void NestedNonuniformRotationSelectionSupportsShearedBulgeBasis()
    {
        var document = new CadDocument();
        var leaf = new BlockRecord("WIDE_SELECTION_SHEAR_LEAF");
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        leaf.Entities.Add(polyline);
        var assembly = new BlockRecord("WIDE_SELECTION_SHEAR_ASSEMBLY");
        assembly.Entities.Add(new Insert(leaf) { Rotation = Math.PI / 4.0 });
        document.Entities.Add(new Insert(assembly) { XScale = 2.0, YScale = 1.0 });

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        CadPoint3D onStrip = primitive.WorldOrigin +
            (primitive.CoordinateSystem.XAxis * 5.0) +
            (primitive.CoordinateSystem.YAxis * -5.5);
        CadPoint3D normal = CadPoint3D.Cross(
            primitive.CoordinateSystem.XAxis,
            primitive.CoordinateSystem.YAxis).Normalize();
        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            onStrip + (normal * 0.25),
            0.3);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                onStrip - new CadPoint3D(0.05, 0.05, 0.05),
                onStrip + new CadPoint3D(0.05, 0.05, 0.05)),
            CadBoundsSelectionMode.Crossing);

        Assert.NotEqual(
            0.0,
            CadPoint3D.Dot(
                primitive.CoordinateSystem.XAxis,
                primitive.CoordinateSystem.YAxis));
        Assert.Equal(CadPointHitStatus.Hit, point.Status);
        Assert.Equal(0.25, point.Distance, 9);
        Assert.Equal(CadBoundsHitStatus.Hit, crossing.Status);
    }

    [Fact]
    public void ExactWideSelectionMakesNoWarmQueryAllocation()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 2.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0) { Bulge = 1.0 });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);
        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        var point = new CadPoint3D(5, -5.5, 0);
        var bounds = new CadBounds3D(
            new CadPoint3D(4.9, -5.6, -0.1),
            new CadPoint3D(5.1, -5.4, 0.1));
        _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, point, 0.0);
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            bounds,
            CadBoundsSelectionMode.Crossing);

        long before = GC.GetAllocatedBytesForCurrentThread();
        CadPointHitResult result = default;
        CadBoundsHitResult boundsResult = default;
        for (int i = 0; i < 128; i++)
        {
            result = CadSelectionHitTester.HitTestPoint(
                snapshot,
                candidate,
                point,
                0.0);
            boundsResult = CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                bounds,
                CadBoundsSelectionMode.Crossing);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(CadPointHitStatus.Hit, result.Status);
        Assert.Equal(CadBoundsHitStatus.Hit, boundsResult.Status);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PatternedWidePolylineFallsBackToContinuousWithTypedDiagnostic()
    {
        var document = new CadDocument();
        var dashed = new LineType("WIDE_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 4.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        LwPolyline polyline = CreateStraight(width: 2.0);
        polyline.LineType = dashed;
        document.Entities.Add(polyline);

        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(Compile(document));
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());

        Assert.Equal(1, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal("CADSCENE009", diagnostic.Code);
        Assert.Contains("wide-polyline", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(2.0f, command.Pen!.Thickness);
        Assert.Single(command.Path!.Figures);
    }

    [Fact]
    public void PrintPlanRetainsModelWidthInsidePageTransform()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateStraight(width: 2.0));
        CadDocumentSnapshot snapshot = Compile(document);

        using CadPrintPlan plan = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = plan.CreatePagePicture();
        RenderCommand replay = page.GetCommand(1);
        RenderCommand stroke = replay.Picture!.GetCommand(0);

        Assert.Equal(RenderCommandType.DrawPicture, replay.Type);
        Assert.True(replay.UseGpuTransforms);
        Assert.Equal(2.0f, stroke.Pen!.Thickness);
        Assert.Equal(PenStrokeTransformMode.Normal, stroke.Pen.StrokeTransformMode);
    }

    private static CadDocumentSnapshot Compile(CadDocument document) =>
        new CadSnapshotCompiler().Compile(new CadDocumentSession(document));

    private static (CadDocumentSnapshot Snapshot, CadSelectionCandidate Candidate)
        CompileSelection(CadDocument document)
    {
        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        return (
            snapshot,
            new CadSelectionCandidate(
                snapshot.ContentGeneration,
                0,
                entity.Handle,
                entity.Kind,
                entity.Bounds));
    }

    private static LwPolyline CreateStraight(double width, double y = 0.0)
    {
        var polyline = new LwPolyline { ConstantWidth = width };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, y));
        polyline.Vertices.Add(new LwPolyline.Vertex(10, y));
        return polyline;
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 10);
        Assert.Equal(expected.Y, actual.Y, 10);
        Assert.Equal(expected.Z, actual.Z, 10);
    }
}
