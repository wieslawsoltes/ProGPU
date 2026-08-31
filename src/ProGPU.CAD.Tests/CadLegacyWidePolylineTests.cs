using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLegacyWidePolylineTests
{
    [Fact]
    public void EntityDefaultsRetainOneExactAnalyticWideStrokeAndNativeParity()
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = 2.0,
            EndWidth = 2.0,
            Elevation = 3.0,
            Normal = XYZ.AxisZ,
        };
        polyline.Vertices.Add(new Vertex2D(new XYZ(0, 0, 0)) { Bulge = 1.0 });
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());

        Assert.Equal(CadEntityKind.Polyline2D, entity.Kind);
        Assert.Equal(2.0, primitive.ConstantWidth);
        Assert.True(primitive.IsWide);
        AssertPoint(new CadPoint3D(-1, -6, 3), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(11, 0, 3), entity.Bounds.Max);
        Assert.IsType<ArcSegment>(Assert.Single(command.Path!.Figures[0].Segments));
        Assert.Equal(2.0f, command.Pen!.Thickness);
        Assert.Equal(PenStrokeTransformMode.Normal, command.Pen.StrokeTransformMode);
        Assert.Equal(PenLineJoin.Bevel, command.Pen.LineJoin);
        Assert.Equal(PenLineCap.Flat, command.Pen.StartLineCap);

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
        Assert.True(native.GeometryPrimitiveCount > 0);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = print.CreatePagePicture();
        RenderCommand replay = page.GetCommand(1);
        RenderCommand printStroke = replay.Picture!.GetCommand(0);
        Assert.Equal(RenderCommandType.DrawPicture, replay.Type);
        Assert.Equal(2.0f, printStroke.Pen!.Thickness);
        Assert.Equal(
            PenStrokeTransformMode.Normal,
            printStroke.Pen.StrokeTransformMode);
    }

    [Fact]
    public void TiltedOcsRetainsExactWideBulgeBounds()
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = 2.0,
            EndWidth = 2.0,
            Elevation = 3.0,
            Normal = XYZ.AxisY,
        };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero) { Bulge = 1.0 });
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());

        AssertPoint(new CadPoint3D(-11, 3, -6), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(1, 3, 0), entity.Bounds.Max);
        Assert.Equal(2.0, Assert.Single(snapshot.Polylines.ToArray()).ConstantWidth);
    }

    [Fact]
    public void VertexWidthsOverrideDefaultsOnlyForSegmentsAndMustResolveConstant()
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = 3.0,
            EndWidth = 3.0,
        };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero)
        {
            StartWidth = 2.0,
            EndWidth = 2.0,
        });
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0))
        {
            // An open polyline has no segment beginning at its terminal vertex.
            StartWidth = double.NaN,
            EndWidth = -1.0,
        });
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());

        Assert.Equal(2.0, primitive.ConstantWidth);
        AssertPoint(new CadPoint3D(0, -1, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(10, 1, 0), snapshot.Bounds.Max);
    }

    [Fact]
    public void NonuniformBlockTransformAndExactSelectionReuseSharedWideContract()
    {
        var document = new CadDocument();
        var block = new BlockRecord("LEGACY_WIDE");
        var polyline = new Polyline2D { StartWidth = 2.0, EndWidth = 2.0 };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero));
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        block.Entities.Add(polyline);
        document.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 3.0,
            ZScale = 1.0,
        });

        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(document);
        CadPointHitResult point = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(10, 2.5, 0.25),
            0.3);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(9.9, 2.4, -0.1),
                new CadPoint3D(10.1, 2.6, 0.1)),
            CadBoundsSelectionMode.Crossing);

        AssertPoint(new CadPoint3D(0, -3, 0), candidate.Bounds.Min);
        AssertPoint(new CadPoint3D(20, 3, 0), candidate.Bounds.Max);
        Assert.Equal(CadPointHitStatus.Hit, point.Status);
        Assert.Equal(0.25, point.Distance, 10);
        Assert.Equal(CadBoundsHitStatus.Hit, crossing.Status);
    }

    [Fact]
    public void VariableWidthBulgesAndFillModeOffRemainExplicitlyUnsupported()
    {
        var variableDocument = new CadDocument();
        var variable = new Polyline2D { StartWidth = 2.0, EndWidth = 3.0 };
        variable.Vertices.Add(new Vertex2D(XYZ.Zero) { Bulge = 0.5 });
        variable.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        variableDocument.Entities.Add(variable);

        var fillOffDocument = new CadDocument();
        fillOffDocument.Header.FillMode = false;
        var fillOff = new Polyline2D { StartWidth = 2.0, EndWidth = 2.0 };
        fillOff.Vertices.Add(new Vertex2D(XYZ.Zero));
        fillOff.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        fillOffDocument.Entities.Add(fillOff);

        CadDocumentSnapshot variableSnapshot = Compile(variableDocument);
        CadDocumentSnapshot fillOffSnapshot = Compile(fillOffDocument);

        Assert.Empty(variableSnapshot.Entities.ToArray());
        Assert.Equal(1, variableSnapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(variableSnapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("spiral-boundary", StringComparison.Ordinal));
        Assert.Empty(fillOffSnapshot.Entities.ToArray());
        Assert.Equal(1, fillOffSnapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(fillOffSnapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("FILLMODE", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedTerminalVertexWidthParticipatesInExactClosingSegment()
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = 2.0,
            EndWidth = 2.0,
            IsClosed = true,
        };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero));
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 10, 0))
        {
            StartWidth = 3.0,
            EndWidth = 3.0,
        });
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);

        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        CadPolylineVertex terminal = snapshot.PolylineVertices.Span[^1];

        Assert.True(primitive.HasVariableWidth);
        Assert.Equal(3.0, terminal.StartWidth);
        Assert.Equal(3.0, terminal.EndWidth);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidWidthsAreRejectedTransactionally(double width)
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = width,
            EndWidth = 2.0,
        };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero));
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Polylines.ToArray());
        Assert.Equal(1, snapshot.Statistics.InvalidEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP002" &&
            diagnostic.Message.Contains("width must be finite and non-negative", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactWideSelectionMakesNoWarmQueryAllocation()
    {
        var document = new CadDocument();
        var polyline = new Polyline2D { StartWidth = 2.0, EndWidth = 2.0 };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero) { Bulge = 1.0 });
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
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
        CadPointHitResult pointResult = default;
        CadBoundsHitResult boundsResult = default;
        for (int i = 0; i < 128; i++)
        {
            pointResult = CadSelectionHitTester.HitTestPoint(
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

        Assert.Equal(CadPointHitStatus.Hit, pointResult.Status);
        Assert.Equal(CadBoundsHitStatus.Hit, boundsResult.Status);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PatternedWidthFallsBackToOneContinuousStrokeWithDiagnostic()
    {
        var document = new CadDocument();
        var dashed = new LineType("LEGACY_WIDE_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 4.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        var polyline = new Polyline2D { StartWidth = 2.0, EndWidth = 2.0 };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero));
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        polyline.LineType = dashed;
        document.Entities.Add(polyline);

        using CadRecordedPlanScene scene =
            new CadPlanSceneCompiler().Compile(Compile(document));
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());

        Assert.Equal(1, scene.Statistics.UnsupportedLineTypeCount);
        Assert.Equal(0, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal("CADSCENE009", diagnostic.Code);
        Assert.Contains("wide-polyline", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(2.0f, command.Pen!.Thickness);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task DefaultWidthRoundTripsAndRecompilesExactly(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = 2.5,
            EndWidth = 2.5,
            IsClosed = true,
        };
        polyline.Vertices.Add(new Vertex2D(new XYZ(0, 0, 0)) { Bulge = 0.5 });
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 10, 0)));
        document.Entities.Add(polyline);
        var session = new CadDocumentSession(document);
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
            sourceName: $"legacy-wide.{format.ToString().ToLowerInvariant()}");

        Polyline2D loadedPolyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<Polyline2D>().ToArray()));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());

        Assert.True(loadedPolyline.IsClosed);
        Assert.Equal(2.5, loadedPolyline.StartWidth);
        Assert.Equal(2.5, loadedPolyline.EndWidth);
        Assert.Equal(0.5, loadedPolyline.Vertices[0].Bulge);
        Assert.Equal(2.5, primitive.ConstantWidth);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
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

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 10);
        Assert.Equal(expected.Y, actual.Y, 10);
        Assert.Equal(expected.Z, actual.Z, 10);
    }
}
