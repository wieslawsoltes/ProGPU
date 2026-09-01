using ACadSharp;
using ACadSharp.Blocks;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Scene.Native;
using ProGPU.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLeaderTests
{
    [Fact]
    public void StraightLeaderRetainsDimensionStyleAndDefaultArrow()
    {
        Leader source = CreateLeader();
        source.Style.DimensionLineColor = new ACadSharp.Color(15, 120, 240);
        source.Style.DimensionLineWeight = LineWeightType.W50;

        CadDocumentSnapshot snapshot = Compile(source);

        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        CadLeaderPrimitive leader = Assert.Single(snapshot.Leaders.ToArray());
        CadSplinePrimitive path = snapshot.Splines.Span[leader.PathSplineIndex];
        CadStrokeStyle style = snapshot.Styles.Span[header.StyleIndex];
        Assert.Equal(CadEntityKind.Leader, header.Kind);
        Assert.Equal(1, path.Degree);
        Assert.Equal(2, path.ControlPointCount);
        Assert.True(leader.HasDefaultArrow);
        Assert.Equal(new CadPoint3D(0, 0, 0), leader.ArrowTip);
        Assert.Equal(new CadPoint3D(2, -1.0 / 3.0, 0), leader.ArrowFirstBase);
        Assert.Equal(new CadPoint3D(2, 1.0 / 3.0, 0), leader.ArrowSecondBase);
        Assert.Equal((byte)15, style.Red);
        Assert.Equal((byte)120, style.Green);
        Assert.Equal((byte)240, style.Blue);
        Assert.Equal(0.5, style.LineWeightMillimeters, 12);
    }

    [Fact]
    public void DimensionOverridesControlLeaderPaintAndArrowScale()
    {
        Leader source = CreateLeader();
        var styleOverride = (DimensionStyle)source.Style.Clone();
        styleOverride.ScaleFactor = 2.0;
        styleOverride.ArrowSize = 1.5;
        styleOverride.DimensionLineColor = ACadSharp.Color.Red;
        styleOverride.DimensionLineWeight = LineWeightType.W50;
        source.SetDimensionOverride(styleOverride);

        CadDocumentSnapshot snapshot = Compile(source);

        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        CadLeaderPrimitive leader = Assert.Single(snapshot.Leaders.ToArray());
        CadStrokeStyle style = snapshot.Styles.Span[header.StyleIndex];
        Assert.Equal(new CadPoint3D(3, -0.5, 0), leader.ArrowFirstBase);
        Assert.Equal(new CadPoint3D(3, 0.5, 0), leader.ArrowSecondBase);
        Assert.Equal((byte)255, style.Red);
        Assert.Equal((byte)0, style.Green);
        Assert.Equal((byte)0, style.Blue);
        Assert.Equal(0.5, style.LineWeightMillimeters, 12);
    }

    [Fact]
    public void ArrowIsSuppressedWhenFirstSegmentIsShorterThanTwiceItsSize()
    {
        Leader source = CreateLeader(new XYZ(0, 0, 0), new XYZ(3.9, 0, 0));

        CadLeaderPrimitive leader = Assert.Single(Compile(source).Leaders.ToArray());

        Assert.False(leader.HasDefaultArrow);
    }

    [Fact]
    public void SplineFitLeaderRetainsCubicInterpolationAndHorizontalEndTangent()
    {
        Leader source = CreateLeader(
            new XYZ(0, 0, 0),
            new XYZ(4, 4, 0),
            new XYZ(8, 5, 0));
        source.PathType = LeaderPathType.Spline;
        source.HorizontalDirection = XYZ.AxisX;
        source.HookLineDirection = HookLineDirection.Same;

        CadDocumentSnapshot snapshot = Compile(source);
        CadLeaderPrimitive leader = Assert.Single(snapshot.Leaders.ToArray());
        CadSplinePrimitive spline = snapshot.Splines.Span[leader.PathSplineIndex];
        ReadOnlySpan<CadPoint3D> controls = snapshot.SplineControlPoints.Span.Slice(
            spline.ControlPointOffset,
            spline.ControlPointCount);

        Assert.True(leader.IsSplineFit);
        Assert.Equal(3, spline.Degree);
        Assert.Equal(7, spline.ControlPointCount);
        Assert.Equal(new CadPoint3D(0, 0, 0), controls[0]);
        Assert.Equal(new CadPoint3D(4, 4, 0), controls[3]);
        Assert.Equal(new CadPoint3D(8, 5, 0), controls[6]);
        Assert.Equal(controls[5].Y, controls[6].Y, 12);
        Assert.True(controls[5].X < controls[6].X);
    }

    [Fact]
    public void CustomArrowBlockUsesTipDirectionAndDimensionScale()
    {
        Leader source = CreateLeader();
        var arrow = new BlockRecord("USER_ARROW");
        arrow.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        source.Style.LeaderArrow = arrow;

        CadDocumentSnapshot snapshot = Compile(source);

        Assert.Equal(2, snapshot.Entities.Length);
        CadEntityHeader leaderHeader = snapshot.Entities.Span[0];
        CadEntityHeader arrowHeader = snapshot.Entities.Span[1];
        Assert.Equal(CadEntityKind.Leader, leaderHeader.Kind);
        Assert.Equal(CadEntityKind.Line, arrowHeader.Kind);
        Assert.False(snapshot.Leaders.Span[0].HasDefaultArrow);
        CadLinePrimitive line = snapshot.Lines.Span[arrowHeader.PrimitiveIndex];
        Assert.Equal(new CadPoint3D(0, 0, 0), line.Start);
        Assert.Equal(new CadPoint3D(2, 0, 0), line.End);
        Assert.Equal(leaderHeader.Handle, arrowHeader.Handle);
    }

    [Fact]
    public void NestedAffineInsertTransformsLeaderPathAndArrowOnce()
    {
        var document = new CadDocument();
        var block = new BlockRecord("LEADER_BLOCK");
        block.Entities.Add(CreateLeader());
        document.Entities.Add(new Insert(block)
        {
            InsertPoint = new XYZ(100, 200, 3),
            XScale = 2,
            YScale = 3,
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadLeaderPrimitive leader = Assert.Single(snapshot.Leaders.ToArray());

        Assert.Equal(new CadPoint3D(100, 200, 3), leader.ArrowTip);
        Assert.Equal(new CadPoint3D(104, 199, 3), leader.ArrowFirstBase);
        Assert.Equal(new CadPoint3D(104, 201, 3), leader.ArrowSecondBase);
        Assert.Equal(new CadPoint3D(100, 199, 3), snapshot.Bounds.Min);
        Assert.Equal(new CadPoint3D(120, 201, 3), snapshot.Bounds.Max);
    }

    [Fact]
    public void PlanChunkCacheSharesLeaderAcrossAffineBlockInstances()
    {
        var document = new CadDocument();
        var block = new BlockRecord("LEADER_TILE");
        block.Entities.Add(CreateLeader());
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
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        var compiler = new CadPlanSceneCompiler();
        using CadRecordedPlanScene baseline = compiler.Compile(snapshot);
        using GpuPicture baselinePicture = baseline.CreatePicture();
        using var cache = new CadPlanChunkCache();
        using CadRecordedPlanScene cached = compiler.Compile(
            snapshot,
            new CadPlanSceneOptions { ChunkCache = cache });
        using GpuPicture cachedPicture = cached.CreatePicture();

        Assert.Equal(2, cached.Statistics.RetainedChunkCount);
        Assert.Equal(1, cached.Statistics.ReusedRetainedChunkCount);
        Assert.Same(
            cachedPicture.GetCommand(0).Picture,
            cachedPicture.GetCommand(1).Picture);
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            baselinePicture,
            813U,
            1U,
            out NativeCompiledPicture? baselineNative,
            out NativePictureCompileFailure baselineFailure),
            baselineFailure.ToString());
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            cachedPicture,
            814U,
            1U,
            out NativeCompiledPicture? cachedNative,
            out NativePictureCompileFailure cachedFailure),
            cachedFailure.ToString());
        Assert.Equal(baselineNative!.NativeDrawCount, cachedNative!.NativeDrawCount);
        Assert.Equal(baselineNative.PathSegmentCount, cachedNative.PathSegmentCount);
    }

    [Fact]
    public void PatternedLeaderLowersPathWithoutDroppingArrow()
    {
        var document = new CadDocument();
        var dashed = new LineType("LEADER_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 2.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        Leader source = CreateLeader(new XYZ(0, 0, 0), new XYZ(12, 0, 0));
        source.Style.LineType = dashed;
        source.Style.ArrowSize = 1.0;
        document.Entities.Add(source);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(4, scene.Statistics.LoweredLineTypeFigureCount);
        Assert.Equal(2, scene.Statistics.RecordedCommandCount);
        Assert.Equal(RenderCommandType.DrawPath, scene.DrawingContext.Commands[0].Type);
        Assert.Equal(RenderCommandType.DrawPath, scene.DrawingContext.Commands[1].Type);
    }

    [Fact]
    public void ComplexPatternPlacesRetainedTextWithoutDroppingArrow()
    {
        var document = new CadDocument();
        var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
        document.TextStyles.Add(textStyle);
        var complex = new LineType("LEADER_COMPLEX");
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
        Leader source = CreateLeader(new XYZ(0, 0, 0), new XYZ(12, 0, 0));
        source.Style.LineType = complex;
        source.Style.ArrowSize = 1.0;
        document.Entities.Add(source);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(1, scene.Statistics.LoweredLineTypePlacementCount);
        Assert.Equal(3, scene.Statistics.RecordedCommandCount);
        Assert.Equal(RenderCommandType.DrawPath, scene.DrawingContext.Commands[0].Type);
        Assert.Equal(RenderCommandType.DrawGlyphRun, scene.DrawingContext.Commands[1].Type);
        Assert.Equal(RenderCommandType.DrawPath, scene.DrawingContext.Commands[2].Type);
    }

    [Fact]
    public void SelectionUsesExactSplineAndFilledArrowGeometry()
    {
        Leader source = CreateLeader(
            new XYZ(0, 0, 0),
            new XYZ(4, 4, 0),
            new XYZ(8, 5, 0));
        source.PathType = LeaderPathType.Spline;
        CadDocumentSnapshot snapshot = Compile(source);
        CadEntityHeader header = snapshot.Entities.Span[0];
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        CadPointHitResult arrow = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(0.6, 0.6, 0),
            0.0);
        CadPointHitResult outside = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(8, 8, 0),
            0.1);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(3.9, 3.9, -0.1),
                new CadPoint3D(4.1, 4.1, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.True(arrow.IsHit);
        Assert.False(outside.IsHit);
        Assert.True(crossing.IsHit);
    }

    [Fact]
    public void LeaderBudgetsFailClosedWithoutPartialGeometry()
    {
        Leader source = CreateLeader(
            new XYZ(0, 0, 0),
            new XYZ(2, 2, 0),
            new XYZ(4, 0, 0));
        source.PathType = LeaderPathType.Spline;

        CadDocumentSnapshot snapshot = Compile(
            source,
            new CadSnapshotOptions { MaxLeaderControlPoints = 6 });

        Assert.Empty(snapshot.Entities.ToArray());
        Assert.Empty(snapshot.Leaders.ToArray());
        Assert.Empty(snapshot.Splines.ToArray());
        Assert.Empty(snapshot.SplineControlPoints.ToArray());
        Assert.Equal(1, snapshot.Statistics.UnsupportedEntityCount);
        Assert.Contains(snapshot.Diagnostics.ToArray(), diagnostic =>
            diagnostic.Code == "CADSNAP003" &&
            diagnostic.Message.Contains("control points", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task LeaderSurvivesDxfAndDwgRoundTrip(CadDocumentFormat format)
    {
        var document = new CadDocument();
        Leader source = CreateLeader(
            new XYZ(0, 0, 0),
            new XYZ(4, 4, 0),
            new XYZ(8, 5, 0));
        source.PathType = LeaderPathType.Spline;
        document.Entities.Add(source);
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

        CadLeaderPrimitive leader = Assert.Single(snapshot.Leaders.ToArray());
        Assert.True(leader.IsSplineFit);
        Assert.True(leader.HasDefaultArrow);
        Assert.Equal(7, snapshot.Splines.Span[leader.PathSplineIndex].ControlPointCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AssociatedMTextControlsEndpointAfterRoundTrip(CadDocumentFormat format)
    {
        var document = new CadDocument();
        Leader source = CreateLeader(
            new XYZ(0, 0, 0),
            new XYZ(4, 4, 0),
            new XYZ(6, 5, 0));
        source.Style.ScaleFactor = 2.0;
        source.Style.DimensionLineGap = 0.5;
        source.AnnotationOffset = new XYZ(1, 2, 0);
        source.HookLineDirection = HookLineDirection.Same;
        var annotation = new MText
        {
            InsertPoint = new XYZ(10, 10, 0),
            Value = "Pump A",
        };
        document.Entities.Add(annotation);
        document.Entities.Add(source);
        source.AttachAnnotation(annotation);
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

        CadLeaderPrimitive leader = Assert.Single(snapshot.Leaders.ToArray());
        CadSplinePrimitive path = snapshot.Splines.Span[leader.PathSplineIndex];
        ReadOnlySpan<CadPoint3D> controls = snapshot.SplineControlPoints.Span.Slice(
            path.ControlPointOffset,
            path.ControlPointCount);
        Assert.True(leader.HasAssociatedAnnotation);
        Assert.Equal(new CadPoint3D(12, 12, 0), controls[^1]);
    }

    [Fact]
    public void RetainedLeaderReusesNativePictureAndPrintPipelines()
    {
        CadDocumentSnapshot snapshot = Compile(CreateLeader());
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        using GpuPicture picture = scene.CreatePicture();

        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            812U,
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
    public void WarmLeaderSelectionAllocatesNoManagedMemory()
    {
        CadDocumentSnapshot snapshot = Compile(CreateLeader());
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
        Leader source,
        CadSnapshotOptions? options = null)
    {
        var document = new CadDocument();
        document.Entities.Add(source);
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            options);
    }

    private static Leader CreateLeader(params XYZ[]? vertices)
    {
        var style = new DimensionStyle("LEADER_STYLE")
        {
            ArrowSize = 2.0,
            ScaleFactor = 1.0,
            DimensionLineColor = new ACadSharp.Color(255, 255, 255),
            DimensionLineWeight = LineWeightType.W25,
        };
        var leader = new Leader
        {
            Style = style,
            ArrowHeadEnabled = true,
            Normal = XYZ.AxisZ,
            HorizontalDirection = XYZ.AxisX,
        };
        foreach (XYZ vertex in vertices is { Length: > 0 }
                     ? vertices
                     : [XYZ.Zero, new XYZ(10, 0, 0)])
        {
            leader.Vertices.Add(vertex);
        }
        return leader;
    }

    private sealed class FixedTextFontResolver(TtfFont font) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(font, IsSubstitution: false);
    }
}
