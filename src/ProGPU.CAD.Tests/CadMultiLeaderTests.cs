using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Text;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMultiLeaderTests
{
    [Fact]
    public void MultipleBranchesRetainIndependentPathsArrowsAndOneDogleg()
    {
        MultiLeader source = CreateMultiLeader();
        MultiLeaderObjectContextData.LeaderRoot root = source.ContextData.LeaderRoots[0];
        var second = new MultiLeaderObjectContextData.LeaderLine();
        second.Points.Add(new XYZ(-4, 3, 0));
        root.Lines.Add(second);

        CadDocumentSnapshot snapshot = Compile(source);

        Assert.Equal(3, snapshot.Entities.Length);
        Assert.Equal(3, snapshot.MultiLeaders.Length);
        Assert.Equal(3, snapshot.Splines.Length);
        CadMultiLeaderPrimitive[] retained = snapshot.MultiLeaders.ToArray();
        Assert.Equal(2, retained.Count(item => !item.IsDogleg));
        Assert.Equal(2, retained.Count(item => item.HasDefaultArrow));
        CadMultiLeaderPrimitive dogleg = Assert.Single(retained, item => item.IsDogleg);
        Assert.Equal(0, dogleg.LeaderRootIndex);
        Assert.Equal(-1, dogleg.LeaderLineIndex);
        CadSplinePrimitive doglegPath = snapshot.Splines.Span[dogleg.PathSplineIndex];
        ReadOnlySpan<CadPoint3D> points = snapshot.SplineControlPoints.Span.Slice(
            doglegPath.ControlPointOffset,
            doglegPath.ControlPointCount);
        Assert.Equal(new CadPoint3D(4, 0, 0), points[0]);
        Assert.Equal(new CadPoint3D(6, 0, 0), points[1]);
        Assert.All(snapshot.Entities.ToArray(), header =>
            Assert.Equal(CadEntityKind.MultiLeader, header.Kind));
    }

    [Fact]
    public void PerLineSplineAndPaintOverridesAreRetained()
    {
        MultiLeader source = CreateMultiLeader();
        MultiLeaderObjectContextData.LeaderLine line =
            source.ContextData.LeaderRoots[0].Lines[0];
        line.Points.Clear();
        line.Points.Add(XYZ.Zero);
        line.Points.Add(new XYZ(2, 3, 0));
        line.OverrideFlags = LeaderLinePropertOverrideFlags.PathType |
            LeaderLinePropertOverrideFlags.LineColor |
            LeaderLinePropertOverrideFlags.LineWeight |
            LeaderLinePropertOverrideFlags.ArrowheadSize;
        line.PathType = MultiLeaderPathType.Spline;
        line.LineColor = new ACadSharp.Color(12, 34, 56);
        line.LineWeight = LineWeightType.W50;
        line.ArrowheadSize = 1.5;

        CadDocumentSnapshot snapshot = Compile(source);
        CadEntityHeader header = snapshot.Entities.Span[0];
        CadMultiLeaderPrimitive branch = snapshot.MultiLeaders.Span[header.PrimitiveIndex];
        CadSplinePrimitive path = snapshot.Splines.Span[branch.PathSplineIndex];
        CadStrokeStyle paint = snapshot.Styles.Span[header.StyleIndex];

        Assert.True(branch.IsSplineFit);
        Assert.Equal(3, path.Degree);
        Assert.Equal(7, path.ControlPointCount);
        Assert.Equal((byte)12, paint.Red);
        Assert.Equal((byte)34, paint.Green);
        Assert.Equal((byte)56, paint.Blue);
        Assert.Equal(0.5, paint.LineWeightMillimeters, 12);
        Assert.Equal(new CadPoint3D(0, 0, 0), branch.ArrowTip);
    }

    [Fact]
    public void PatternedBranchRetainsArrowAndUsesSharedSplineLowerer()
    {
        var document = new CadDocument();
        var dashed = new LineType("MLEADER_DASH");
        dashed.AddSegment(new LineType.Segment { Length = 2.0 });
        dashed.AddSegment(new LineType.Segment { Length = -2.0 });
        document.LineTypes.Add(dashed);
        MultiLeader source = CreateMultiLeader();
        source.Style.LeaderLineType = dashed;
        source.Style.EnableDogleg = false;
        source.ContextData.LeaderRoots[0].Lines[0].Points[0] = new XYZ(-8, 0, 0);
        document.Entities.Add(source);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);

        Assert.Equal(1, scene.Statistics.LoweredLineTypeEntityCount);
        Assert.Equal(2, scene.Statistics.RecordedCommandCount);
        Assert.All(scene.DrawingContext.Commands.ToArray(), command =>
            Assert.Equal(RenderCommandType.DrawPath, command.Type));
    }

    [Fact]
    public void PlanChunkCacheReusesExactMultiLeaderRoot()
    {
        CadDocumentSnapshot snapshot = Compile(CreateMultiLeader());
        var compiler = new CadPlanSceneCompiler();
        using var cache = new CadPlanChunkCache();
        var options = new CadPlanSceneOptions { ChunkCache = cache };
        using CadRecordedPlanScene first = compiler.Compile(snapshot, options);
        using CadRecordedPlanScene second = compiler.Compile(snapshot, options);
        using GpuPicture firstPicture = first.CreatePicture();
        using GpuPicture secondPicture = second.CreatePicture();

        Assert.Equal(1, first.Statistics.RetainedChunkCount);
        Assert.Equal(0, first.Statistics.ReusedRetainedChunkCount);
        Assert.Equal(1, second.Statistics.RetainedChunkCount);
        Assert.Equal(1, second.Statistics.ReusedRetainedChunkCount);
        Assert.Same(
            firstPicture.GetCommand(0).Picture,
            secondPicture.GetCommand(0).Picture);
        Assert.Equal(
            first.Statistics.RecordedCommandCount,
            second.Statistics.RecordedCommandCount);
    }

    [Fact]
    public void ExactSelectionIncludesSplineAndDefaultArrow()
    {
        MultiLeader source = CreateMultiLeader();
        source.Style.EnableDogleg = false;
        CadDocumentSnapshot snapshot = Compile(source);
        CadEntityHeader header = Assert.Single(snapshot.Entities.ToArray());
        var candidate = new CadSelectionCandidate(
            snapshot.ContentGeneration,
            0,
            header.Handle,
            header.Kind,
            header.Bounds);

        CadPointHitResult arrow = CadSelectionHitTester.HitTestPoint(
            snapshot,
            candidate,
            new CadPoint3D(0.2, 0, 0),
            0.0);
        CadBoundsHitResult crossing = CadSelectionHitTester.HitTestBounds(
            snapshot,
            candidate,
            new CadBounds3D(
                new CadPoint3D(1.9, -0.1, -0.1),
                new CadPoint3D(2.1, 0.1, 0.1)),
            CadBoundsSelectionMode.Crossing);

        Assert.True(arrow.IsHit);
        Assert.True(crossing.IsHit);
    }

    [Fact]
    public void EmbeddedMTextUsesExistingFullTextStack()
    {
        MultiLeader source = CreateMultiLeader();
        source.PropertyOverrideFlags &= ~MultiLeaderPropertyOverrideFlags.ContentType;
        source.Style.ContentType = LeaderContentType.MText;
        source.ContextData.HasTextContents = true;
        source.ContextData.TextLabel = "Pump\\PStation";
        source.ContextData.TextHeight = 2.0;
        source.ContextData.TextLocation = new XYZ(6.5, 1, 0);
        source.ContextData.TextNormal = XYZ.AxisZ;
        source.ContextData.Direction = XYZ.AxisX;
        source.ContextData.TextAttachmentPoint = TextAttachmentPointType.Left;
        var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
        source.ContextData.TextStyle = textStyle;
        source.Style.TextStyle = textStyle;

        CadDocumentSnapshot snapshot = Compile(
            source,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        Assert.Single(snapshot.MTexts.ToArray());
        Assert.Contains(snapshot.Entities.ToArray(), item => item.Kind == CadEntityKind.MText);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawGlyphRun);
    }

    [Fact]
    public void EmbeddedToleranceUsesFeatureControlFrameAndSharedSemanticHandle()
    {
        MultiLeader source = CreateMultiLeader();
        source.PropertyOverrideFlags &= ~MultiLeaderPropertyOverrideFlags.ContentType;
        source.Style.ContentType = LeaderContentType.Tolerance;
        source.Style.TextHeight = 2.0;
        source.Style.LandingGap = 0.5;
        source.ContextData.TextLabel =
            "{\\Fgdt;j}%%v{\\Fgdt;n}0.1{\\Fgdt;m}%%vA";
        source.ContextData.TextHeight = 2.0;
        source.ContextData.TextLocation = new XYZ(6.5, 1, 0);
        source.ContextData.TextNormal = XYZ.AxisZ;
        source.ContextData.Direction = XYZ.AxisX;
        var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
        source.ContextData.TextStyle = textStyle;
        source.Style.TextStyle = textStyle;

        CadDocumentSnapshot snapshot = Compile(
            source,
            new CadSnapshotOptions
            {
                TextFontResolver = new FixedTextFontResolver(InterFontFamily.Regular),
            });

        CadEntityHeader frame = Assert.Single(
            snapshot.Entities.ToArray(), item => item.Kind == CadEntityKind.Tolerance);
        Assert.Equal(3, snapshot.Tolerances.Span[frame.PrimitiveIndex].CellCount);
        Assert.All(snapshot.Entities.ToArray(), item =>
            Assert.Equal(source.Handle, item.Handle));
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawPath);
        Assert.Contains(scene.DrawingContext.Commands.ToArray(), command =>
            command.Type == RenderCommandType.DrawGlyphRun);
    }

    [Fact]
    public void EmbeddedStaticBlockUsesPersistedCompleteAffineTransform()
    {
        MultiLeader source = CreateMultiLeader();
        source.PropertyOverrideFlags &= ~MultiLeaderPropertyOverrideFlags.ContentType;
        source.Style.ContentType = LeaderContentType.Block;
        var block = new BlockRecord("PUMP_SYMBOL");
        block.Entities.Add(new Line(XYZ.Zero, XYZ.AxisX));
        source.ContextData.HasContentsBlock = true;
        source.ContextData.BlockContent = block;
        source.ContextData.BlockContentColor = ACadSharp.Color.ByBlock;
        source.ContextData.TransformationMatrix = new Matrix4(
            2, 0, 0, 10,
            0, 3, 0, 20,
            0, 0, 1, 0,
            0, 0, 0, 1);

        CadDocumentSnapshot snapshot = Compile(source);

        CadEntityHeader blockHeader = Assert.Single(
            snapshot.Entities.ToArray(), item => item.Kind == CadEntityKind.Line);
        CadLinePrimitive line = snapshot.Lines.Span[blockHeader.PrimitiveIndex];
        Assert.Equal(new CadPoint3D(10, 20, 0), line.Start);
        Assert.Equal(new CadPoint3D(12, 20, 0), line.End);
        Assert.Equal(snapshot.Entities.Span[0].Handle, blockHeader.Handle);
    }

    [Fact]
    public void AuthoredBreaksAndAnnotativeContextFailClosedWithoutPartialGeometry()
    {
        MultiLeader withBreak = CreateMultiLeader();
        withBreak.ContextData.LeaderRoots[0].Lines[0].StartEndPoints.Add(
            new MultiLeaderObjectContextData.StartEndPointPair(
                new XYZ(1, 0, 0),
                new XYZ(2, 0, 0)));
        MultiLeader annotative = CreateMultiLeader();
        annotative.EnableAnnotationScale = true;

        CadDocumentSnapshot breakSnapshot = Compile(withBreak);
        CadDocumentSnapshot annotationSnapshot = Compile(annotative);

        Assert.Empty(breakSnapshot.Entities.ToArray());
        Assert.Empty(breakSnapshot.MultiLeaders.ToArray());
        Assert.Empty(breakSnapshot.Splines.ToArray());
        Assert.Contains(breakSnapshot.Diagnostics.ToArray(), item =>
            item.Code == "CADSNAP003" && item.Message.Contains("break", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(annotationSnapshot.Entities.ToArray());
        Assert.Contains(annotationSnapshot.Diagnostics.ToArray(), item =>
            item.Code == "CADSNAP003" && item.Message.Contains("annotation context", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task BranchesAndDoglegSurviveDxfAndDwgRoundTrip(
        CadDocumentFormat format)
    {
        var document = new CadDocument();
        document.Entities.Add(CreateMultiLeader());
        var store = new CadDocumentStore();
        using var stream = new MemoryStream();

        CadSaveResult saved = await store.SaveAsync(
            new CadDocumentSession(document),
            stream,
            format,
            new CadSaveOptions { AllowUncertifiedWrite = true });
        stream.Position = 0;
        CadLoadResult loaded = await store.LoadAsync(stream, format);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(loaded.Session);

        Assert.True(
            snapshot.MultiLeaders.Length == 2,
            loaded.Session.Read(doc => string.Join(",", doc.Entities.Select(item => item.ObjectName))) +
            Environment.NewLine +
            string.Join(Environment.NewLine, saved.Diagnostics.Select(item => item.Message)) +
            Environment.NewLine +
            string.Join(Environment.NewLine, loaded.Diagnostics.Select(item => item.Message)) +
            Environment.NewLine +
            string.Join(Environment.NewLine, snapshot.Diagnostics.ToArray().Select(item => item.Message)));
        Assert.Contains(snapshot.MultiLeaders.ToArray(), item => !item.IsDogleg);
        Assert.Contains(snapshot.MultiLeaders.ToArray(), item => item.IsDogleg);
        Assert.Empty(snapshot.Diagnostics.ToArray());
    }

    private static CadDocumentSnapshot Compile(
        MultiLeader source,
        CadSnapshotOptions? options = null)
    {
        var document = new CadDocument();
        document.Entities.Add(source);
        return new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document),
            options);
    }

    private static MultiLeader CreateMultiLeader()
    {
        var style = new MultiLeaderStyle("MLEADER_STYLE")
        {
            PathType = MultiLeaderPathType.StraightLineSegments,
            ContentType = LeaderContentType.None,
            LineColor = new ACadSharp.Color(255, 255, 255),
            LeaderLineWeight = LineWeightType.W25,
            ArrowheadSize = 1.0,
            EnableLanding = true,
            EnableDogleg = true,
            LandingDistance = 2.0,
        };
        var source = new MultiLeader
        {
            Style = style,
            PropertyOverrideFlags = MultiLeaderPropertyOverrideFlags.ContentType,
            ContentType = LeaderContentType.None,
        };
        source.ContextData.BaseDirection = XYZ.AxisX;
        source.ContextData.BaseVertical = XYZ.AxisY;
        source.ContextData.TextNormal = XYZ.AxisZ;
        source.ContextData.ArrowheadSize = 1.0;
        var root = new MultiLeaderObjectContextData.LeaderRoot
        {
            ConnectionPoint = new XYZ(4, 0, 0),
            Direction = XYZ.AxisX,
            LandingDistance = 2.0,
            ContentValid = true,
        };
        var line = new MultiLeaderObjectContextData.LeaderLine();
        line.Points.Add(XYZ.Zero);
        root.Lines.Add(line);
        source.ContextData.LeaderRoots.Add(root);
        return source;
    }

    private sealed class FixedTextFontResolver(TtfFont font) : ICadTextFontResolver
    {
        public CadTextFontResolution Resolve(in CadTextFontRequest request) =>
            new(font, IsSubstitution: false);
    }
}
