using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadVariableWidthPolylineTests
{
    [Fact]
    public void LightweightStraightTapersRetainOneExactFilledOutlineAcrossOutputs()
    {
        CadDocument document = CreateLightweightTaper();
        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());

        Assert.True(primitive.HasVariableWidth);
        Assert.True(primitive.IsWide);
        Assert.Equal(0.0, primitive.ConstantWidth);
        AssertPoint(new CadPoint3D(0, -2, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(13, 10, 0), entity.Bounds.Max);
        Assert.NotNull(command.Brush);
        Assert.Null(command.Pen);
        Assert.Equal(FillRule.Nonzero, command.Path!.FillRule);
        Assert.Equal(3, command.Path.Figures.Count);
        Assert.All(command.Path.Figures, figure =>
        {
            Assert.True(figure.IsClosed);
            Assert.True(figure.IsFilled);
        });

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
        Assert.Equal(1, native.PathCount);
        Assert.True(native.PathSegmentCount > 0);

        using CadPrintPlan print = new CadPrintPlanCompiler().Compile(snapshot);
        using GpuPicture page = print.CreatePagePicture();
        RenderCommand replay = page.GetCommand(1);
        RenderCommand printFill = replay.Picture!.GetCommand(0);
        Assert.NotNull(printFill.Brush);
        Assert.Null(printFill.Pen);
        Assert.Equal(FillRule.Nonzero, printFill.Path!.FillRule);
    }

    [Fact]
    public void ExactSelectionCoversTaperAndDiscontinuousBevelWithoutAllocation()
    {
        (CadDocumentSnapshot snapshot, CadSelectionCandidate candidate) =
            CompileSelection(CreateLightweightTaper());
        var insideTaper = new CadPoint3D(9, 8, 0);
        var outsideTaper = new CadPoint3D(8, 8, 0);
        var insideJoin = new CadPoint3D(11, -0.5, 0);
        var crossingBounds = new CadBounds3D(
            new CadPoint3D(10.9, -0.6, -0.1),
            new CadPoint3D(11.1, -0.4, 0.1));

        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(snapshot, candidate, insideTaper, 0).Status);
        Assert.Equal(
            CadPointHitStatus.Miss,
            CadSelectionHitTester.HitTestPoint(snapshot, candidate, outsideTaper, 0).Status);
        Assert.Equal(
            CadPointHitStatus.Hit,
            CadSelectionHitTester.HitTestPoint(snapshot, candidate, insideJoin, 0).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                crossingBounds,
                CadBoundsSelectionMode.Crossing).Status);
        Assert.Equal(
            CadBoundsHitStatus.Hit,
            CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                new CadBounds3D(
                    new CadPoint3D(-1, -3, -1),
                    new CadPoint3D(14, 11, 1)),
                CadBoundsSelectionMode.Window).Status);

        _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, insideTaper, 0);
        _ = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            crossingBounds,
            CadBoundsSelectionMode.Crossing);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++)
        {
            _ = CadSelectionHitTester.HitTestPoint(snapshot, candidate, insideTaper, 0);
            _ = CadSelectionHitTester.HitTestBounds(
                snapshot,
                candidate,
                crossingBounds,
                CadBoundsSelectionMode.Crossing);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void NonuniformBlockTransformAppliesToExactSourceOutline()
    {
        var document = new CadDocument();
        var block = new BlockRecord("TAPERED_AFFINE");
        block.Entities.Add(CreateLightweightTaperEntity());
        document.Entities.Add(new Insert(block)
        {
            XScale = 2.0,
            YScale = 3.0,
            ZScale = 1.0,
        });

        CadDocumentSnapshot snapshot = Compile(document);
        CadEntityHeader entity = Assert.Single(snapshot.Entities.ToArray());

        AssertPoint(new CadPoint3D(0, -6, 0), entity.Bounds.Min);
        AssertPoint(new CadPoint3D(26, 30, 0), entity.Bounds.Max);
        Assert.True(Assert.Single(snapshot.Polylines.ToArray()).HasVariableWidth);
    }

    [Fact]
    public void UniformVertexWidthsCollapseToAnalyticBulgeStroke()
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 9.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0)
        {
            StartWidth = 2.0,
            EndWidth = 2.0,
            Bulge = 1.0,
        });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);

        CadDocumentSnapshot snapshot = Compile(document);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        RenderCommand command = Assert.Single(
            new CadPlanSceneCompiler().Compile(snapshot).DrawingContext.Commands.ToArray());

        Assert.False(primitive.HasVariableWidth);
        Assert.Equal(2.0, primitive.ConstantWidth);
        Assert.Equal(2.0f, command.Pen!.Thickness);
        Assert.Null(command.Brush);
        Assert.IsType<ArcSegment>(Assert.Single(command.Path!.Figures[0].Segments));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task LegacyExplicitZeroTaperRoundTripsAndRecompilesExactly(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        var polyline = new Polyline2D
        {
            StartWidth = 3.0,
            EndWidth = 3.0,
        };
        polyline.Vertices.Add(new Vertex2D(XYZ.Zero)
        {
            StartWidth = 0.0,
            EndWidth = 4.0,
        });
        polyline.Vertices.Add(new Vertex2D(new XYZ(10, 0, 0)));
        document.Entities.Add(polyline);
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
            sourceName: "legacy-taper");
        Polyline2D restored = Assert.Single(loaded.Session.Read(
            value => value.Entities.OfType<Polyline2D>().ToArray()));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        CadPolylineVertex retained = snapshot.PolylineVertices.Span[0];

        Assert.True(restored.Vertices[0].HasStartWidth);
        Assert.True(restored.Vertices[0].HasEndWidth);
        Assert.Equal(0.0, retained.StartWidth);
        Assert.Equal(4.0, retained.EndWidth);
        Assert.True(primitive.HasVariableWidth);
        AssertPoint(new CadPoint3D(0, -2, 0), snapshot.Bounds.Min);
        AssertPoint(new CadPoint3D(10, 2, 0), snapshot.Bounds.Max);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task LightweightExplicitWidthsOverrideConstantAcrossRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        var polyline = new LwPolyline { ConstantWidth = 9.0 };
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0)
        {
            StartWidth = 0.0,
            EndWidth = 4.0,
        });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0));
        document.Entities.Add(polyline);
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
            sourceName: "lightweight-taper");
        LwPolyline restored = Assert.Single(loaded.Session.Read(
            value => value.Entities.OfType<LwPolyline>().ToArray()));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);
        CadPolylinePrimitive primitive = Assert.Single(snapshot.Polylines.ToArray());
        CadPolylineVertex retained = snapshot.PolylineVertices.Span[0];

        Assert.Equal(9.0, restored.ConstantWidth);
        Assert.True(restored.Vertices[0].HasStartWidth);
        Assert.True(restored.Vertices[0].HasEndWidth);
        Assert.Equal(0.0, retained.StartWidth);
        Assert.Equal(4.0, retained.EndWidth);
        Assert.Equal(0.0, primitive.ConstantWidth);
        Assert.True(primitive.HasVariableWidth);
    }

    [Fact]
    public void VariableBulgesThicknessFillModeAndOverflowFailTransactionally()
    {
        var bulgeDocument = CreateLightweightTaper();
        Assert.IsType<LwPolyline>(Assert.Single(bulgeDocument.Entities)).Vertices[0].Bulge = 0.5;
        var thicknessDocument = CreateLightweightTaper();
        Assert.IsType<LwPolyline>(Assert.Single(thicknessDocument.Entities)).Thickness = 1.0;
        var fillOffDocument = CreateLightweightTaper();
        fillOffDocument.Header.FillMode = false;
        var overflowDocument = CreateLightweightTaper();
        Assert.IsType<LwPolyline>(Assert.Single(overflowDocument.Entities)).Vertices[0].EndWidth = double.MaxValue;
        var zeroSegmentDocument = CreateLightweightTaper();
        LwPolyline zeroSegment = Assert.IsType<LwPolyline>(Assert.Single(zeroSegmentDocument.Entities));
        zeroSegment.Vertices[1].StartWidth = 0.0;
        zeroSegment.Vertices[1].EndWidth = 0.0;

        CadDocumentSnapshot bulge = Compile(bulgeDocument);
        CadDocumentSnapshot thickness = Compile(thicknessDocument);
        CadDocumentSnapshot fillOff = Compile(fillOffDocument);
        CadDocumentSnapshot overflow = Compile(overflowDocument);
        CadDocumentSnapshot zeroSegmentSnapshot = Compile(zeroSegmentDocument);

        AssertUnsupported(bulge, "spiral-boundary");
        AssertUnsupported(thickness, "side-surface");
        AssertUnsupported(fillOff, "FILLMODE");
        AssertUnsupported(zeroSegmentSnapshot, "skinny-stroke");
        Assert.Empty(overflow.Entities.ToArray());
        Assert.Equal(1, overflow.Statistics.InvalidEntityCount);
        Assert.Contains(overflow.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP002" &&
            diagnostic.Message.Contains("float geometry domain", StringComparison.Ordinal));
    }

    [Fact]
    public void PatternedTaperRetainsOneContinuousFillWithTypedDiagnostic()
    {
        CadDocument document = CreateLightweightTaper();
        var dashed = new LineType("TAPER_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 4.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        Assert.IsType<LwPolyline>(Assert.Single(document.Entities)).LineType = dashed;

        using CadRecordedPlanScene scene =
            new CadPlanSceneCompiler().Compile(Compile(document));
        RenderCommand command = Assert.Single(scene.DrawingContext.Commands.ToArray());
        CadDiagnostic diagnostic = Assert.Single(scene.Diagnostics.ToArray());

        Assert.Equal("CADSCENE009", diagnostic.Code);
        Assert.NotNull(command.Brush);
        Assert.Null(command.Pen);
        Assert.Equal(3, command.Path!.Figures.Count);
    }

    private static CadDocument CreateLightweightTaper()
    {
        var document = new CadDocument();
        document.Entities.Add(CreateLightweightTaperEntity());
        return document;
    }

    private static LwPolyline CreateLightweightTaperEntity()
    {
        var polyline = new LwPolyline();
        polyline.Vertices.Add(new LwPolyline.Vertex(0, 0)
        {
            StartWidth = 2.0,
            EndWidth = 4.0,
        });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 0)
        {
            StartWidth = 6.0,
            EndWidth = 2.0,
        });
        polyline.Vertices.Add(new LwPolyline.Vertex(10, 10));
        return polyline;
    }

    private static void AssertUnsupported(CadDocumentSnapshot snapshot, string message)
    {
        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains(message, StringComparison.Ordinal));
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
