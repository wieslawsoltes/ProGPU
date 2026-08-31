using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
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
    public void FillModeOffAndVariableWidthRemainExplicitlyUnsupported()
    {
        var fillOff = new CadDocument();
        fillOff.Header.FillMode = false;
        fillOff.Entities.Add(CreateStraight(width: 2.0));

        var variable = new CadDocument();
        var tapered = CreateStraight(width: 4.0);
        tapered.Vertices[0].StartWidth = 1.0;
        tapered.Vertices[0].EndWidth = 3.0;
        variable.Entities.Add(tapered);

        CadDocumentSnapshot fillOffSnapshot = Compile(fillOff);
        CadDocumentSnapshot variableSnapshot = Compile(variable);

        Assert.Empty(fillOffSnapshot.Entities.ToArray());
        Assert.Equal(1, fillOffSnapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(fillOffSnapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("FILLMODE", StringComparison.Ordinal));
        Assert.Empty(variableSnapshot.Entities.ToArray());
        Assert.Equal(1, variableSnapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(variableSnapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("Variable-width", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactSelectionDoesNotMisclassifyWideStrokeAsCenterline()
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

        Assert.Equal(CadPointHitStatus.UnsupportedGeometry, point.Status);
        Assert.Equal(CadBoundsHitStatus.UnsupportedGeometry, crossing.Status);
        Assert.Equal(CadBoundsHitStatus.Hit, window.Status);
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
