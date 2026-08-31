using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPolylineAuthoringTests
{
    [Fact]
    public void SessionRetainsLineAndAnalyticTangentArcWithExactEndTangent()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(0, 0, 4), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 4), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;

        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(20, 10, 4), out _));

        Assert.Equal(2, authoring.SegmentCount);
        Assert.Equal(0.0, authoring.Bulges.Span[0]);
        Assert.Equal(Math.Sqrt(2.0) - 1.0, authoring.Bulges.Span[1], 12);
        CadPoint3D tangent = authoring.PreviousSegmentDirection!.Value;
        Assert.InRange(Math.Abs(tangent.X), 0.0, 1e-12);
        Assert.True(tangent.Y > 0.0);
        Assert.Equal(0.0, tangent.Z);

        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 20, 4), out _));
        Assert.Equal(Math.Sqrt(2.0) - 1.0, authoring.Bulges.Span[2], 12);
        Assert.True(authoring.TryUndoLastSegment());
        Assert.Equal(2, authoring.SegmentCount);
        Assert.Equal(0.0, authoring.Bulges.Span[2]);

        var clockwise = new CadPolylineAuthoringSession();
        Assert.True(clockwise.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(clockwise.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        clockwise.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(clockwise.TryAcceptPoint(new CadPoint3D(20, -10, 0), out _));
        Assert.Equal(-(Math.Sqrt(2.0) - 1.0), clockwise.Bulges.Span[1], 12);
        Assert.True(clockwise.PreviousSegmentDirection!.Value.Y < 0.0);
    }

    [Fact]
    public void SessionRejectsOffPlaneDegenerateArcAndBoundOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPolylineAuthoringSession(
                CadPolylineAuthoringSession.DefaultMaximumSegmentCount + 1));
        var authoring = new CadPolylineAuthoringSession(maximumSegmentCount: 2);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(1, 0, 1),
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(1, 1, 0),
            out string? noTangent));
        Assert.Contains("preceding", noTangent, StringComparison.OrdinalIgnoreCase);
        authoring.Mode = CadPolylineAuthoringMode.Line;
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(1, 0, 0), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(2, 0, 0),
            out string? degenerate));
        Assert.Contains("non-degenerate", degenerate, StringComparison.OrdinalIgnoreCase);
        authoring.Mode = CadPolylineAuthoringMode.Line;
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(2, 0, 0), out _));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(3, 0, 0),
            out string? bounded));
        Assert.Contains("limit", bounded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitSignedArcAngleMapsToExactDxfBulge()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptLinePoint(CadPoint3D.Zero, out _));
        Assert.True(authoring.TryAcceptArcPoint(
            new CadPoint3D(10, 0, 0),
            -Math.PI,
            out _));
        Assert.Equal(-1.0, authoring.Bulges.Span[0], 12);

        Assert.False(authoring.TryAcceptArcPoint(
            new CadPoint3D(20, 0, 0),
            Math.Tau,
            out string? fullTurn));
        Assert.Contains("complete turn", fullTurn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WidthAndHalfwidthPersistEndWidthForFollowingSegments()
    {
        var authoring = new CadPolylineAuthoringSession(initialWidth: 2.0);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));

        Assert.True(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Width,
            out _));
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptDefaultWidthValue(out _));
        Assert.Equal(CadPolylineAuthoringPrompt.EndingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptWidthValue(4.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, authoring.Prompt);
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));

        Assert.Equal(2.0, authoring.StartWidths.Span[0]);
        Assert.Equal(4.0, authoring.EndWidths.Span[0]);
        Assert.Equal(4.0, authoring.NextStartWidth);
        Assert.Equal(4.0, authoring.NextEndWidth);

        Assert.True(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Halfwidth,
            out _));
        Assert.Equal(4.0, authoring.WidthPromptDefault);
        Assert.True(authoring.TryAcceptWidthValue(2.0, out _));
        Assert.Equal(4.0, authoring.WidthPromptDefault);
        Assert.True(authoring.TryAcceptWidthValue(1.0, out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(20, 0, 0), out _));

        Assert.True(authoring.TryCreateSnapshot(false, out var snapshot, out _));
        Assert.NotNull(snapshot);
        Assert.Equal([2.0, 4.0, 0.0], snapshot.StartWidths.ToArray());
        Assert.Equal([4.0, 2.0, 0.0], snapshot.EndWidths.ToArray());
        Assert.Equal(2.0, snapshot.ResultingDefaultWidth);
    }

    [Fact]
    public void LengthContinuesActualLineAndArcEndTangents()
    {
        var line = new CadPolylineAuthoringSession();
        Assert.True(line.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(line.TryAcceptPoint(new CadPoint3D(3, 4, 0), out _));
        Assert.True(line.TryBeginLengthInput(out _));
        Assert.True(line.TryAcceptLength(10.0, out _));
        Assert.Equal(new CadPoint3D(9, 12, 0), line.CurrentPoint);

        var arc = new CadPolylineAuthoringSession();
        Assert.True(arc.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(arc.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        arc.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(arc.TryAcceptPoint(new CadPoint3D(20, 10, 0), out _));
        CadPoint3D start = arc.CurrentPoint!.Value;
        CadPoint3D tangent = arc.PreviousSegmentDirection!.Value;
        arc.Mode = CadPolylineAuthoringMode.Line;
        Assert.True(arc.TryBeginLengthInput(out _));
        Assert.True(arc.TryAcceptLength(5.0, out _));

        CadPoint3D delta = arc.CurrentPoint!.Value - start;
        Assert.Equal(5.0, Math.Sqrt(CadPoint3D.Dot(delta, delta)), 12);
        Assert.Equal(0.0, CadPoint3D.Cross(delta, tangent).Z, 12);
        Assert.True(CadPoint3D.Dot(delta, tangent) > 0.0);
    }

    [Fact]
    public void ScalarPromptsRejectInvalidValuesWithoutLosingPromptState()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.False(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Width,
            out string? beforePoint));
        Assert.Contains("first", beforePoint, StringComparison.OrdinalIgnoreCase);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));

        Assert.True(authoring.TryBeginWidthInput(
            CadPolylineWidthInputMode.Width,
            out _));
        Assert.False(authoring.TryAcceptWidthValue(-1.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, authoring.Prompt);
        Assert.False(authoring.TryAcceptWidthValue(double.PositiveInfinity, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptWidthValue(1.0, out _));
        Assert.False(authoring.TryAcceptWidthValue((double)float.MaxValue * 2.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.EndingWidth, authoring.Prompt);
        Assert.True(authoring.TryAcceptWidthValue(1.0, out _));

        Assert.True(authoring.TryBeginLengthInput(out _));
        Assert.False(authoring.TryAcceptLength(0.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Length, authoring.Prompt);
        Assert.False(authoring.TryAcceptPoint(new CadPoint3D(20, 0, 0), out _));
        Assert.True(authoring.TryAcceptLength(5.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, authoring.Prompt);
    }

    [Fact]
    public void VariableWidthsAndArcsFailClosedBeforeSnapshotMutation()
    {
        var tapered = new CadPolylineAuthoringSession();
        Assert.True(tapered.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(tapered.TryBeginWidthInput(CadPolylineWidthInputMode.Width, out _));
        Assert.True(tapered.TryAcceptWidthValue(1.0, out _));
        Assert.True(tapered.TryAcceptWidthValue(2.0, out _));
        Assert.True(tapered.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        tapered.Mode = CadPolylineAuthoringMode.TangentArc;

        Assert.False(tapered.TryAcceptPoint(
            new CadPoint3D(20, 10, 0),
            out string? taperedArc));
        Assert.Contains("uniform", taperedArc, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, tapered.SegmentCount);

        var curved = new CadPolylineAuthoringSession(initialWidth: 2.0);
        Assert.True(curved.TryAcceptPoint(CadPoint3D.Zero, out _));
        Assert.True(curved.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        curved.Mode = CadPolylineAuthoringMode.TangentArc;
        Assert.True(curved.TryAcceptPoint(new CadPoint3D(20, 10, 0), out _));
        Assert.True(curved.TryBeginWidthInput(CadPolylineWidthInputMode.Width, out _));
        Assert.False(curved.TryAcceptWidthValue(3.0, out string? changedArcWidth));
        Assert.Contains("uniform", changedArcWidth, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CadPolylineAuthoringPrompt.StartingWidth, curved.Prompt);
        Assert.True(curved.TryAcceptWidthValue(2.0, out _));
        Assert.False(curved.TryAcceptWidthValue(3.0, out changedArcWidth));
        Assert.Equal(CadPolylineAuthoringPrompt.EndingWidth, curved.Prompt);
        Assert.True(curved.TryAcceptWidthValue(2.0, out _));
        Assert.Equal(CadPolylineAuthoringPrompt.Point, curved.Prompt);

        Assert.True(curved.TryUndoLastSegment());
        Assert.True(curved.TryBeginWidthInput(CadPolylineWidthInputMode.Width, out _));
        Assert.True(curved.TryAcceptWidthValue(3.0, out _));
        Assert.True(curved.TryAcceptWidthValue(3.0, out _));
    }

    [Fact]
    public void CloseUsesFlagWithoutDuplicateVertexAndCanUseTangentArc()
    {
        var authoring = new CadPolylineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(0, 0, 0), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 0, 0), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(10, 10, 0), out _));
        authoring.Mode = CadPolylineAuthoringMode.TangentArc;

        Assert.True(authoring.TryCreateSnapshot(
            close: true,
            out CadPolylineAuthoringSnapshot? snapshot,
            out _));

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsClosed);
        Assert.Equal(3, snapshot.Points.Length);
        Assert.Equal(3, snapshot.SegmentCount);
        Assert.NotEqual(0.0, snapshot.Bulges.Span[^1]);
    }

    [Fact]
    public void CommandCapturesPropertiesPlinegenAndRoundTripsOneEntity()
    {
        var document = new CadDocument();
        var layer = new Layer("PLINES");
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Cyan;
        document.Header.CurrentLineTypeName = LineType.ContinuousName;
        document.Header.CurrentEntityLinetypeScale = 2.25;
        document.Header.CurrentEntityLineWeight = LineWeightType.W35;
        document.Header.PolylineLineTypeGeneration = true;
        document.Header.PolylineWidthDefault = 2.0;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var snapshot = new CadPolylineAuthoringSnapshot(
            [
                new CadPoint3D(1, 2, 3),
                new CadPoint3D(5, 2, 3),
                new CadPoint3D(5, 8, 3),
            ],
            [0.0, 0.5, 0.0],
            isClosed: true);
        var command = new CadAddPolylineCommand(snapshot);

        history.Execute(command);

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Same(polyline, command.Polyline);
        Assert.Same(layer, polyline.Layer);
        Assert.Equal(ACadSharp.Color.Cyan, polyline.Color);
        Assert.Equal(LineType.ContinuousName, polyline.LineType.Name);
        Assert.Equal(2.25, polyline.LineTypeScale);
        Assert.Equal(LineWeightType.W35, polyline.LineWeight);
        Assert.True(polyline.IsClosed);
        Assert.True(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(2.0, polyline.ConstantWidth);
        Assert.Equal(3.0, polyline.Elevation);
        Assert.Equal(XYZ.AxisZ, polyline.Normal);
        Assert.Equal(3, polyline.Vertices.Count);
        Assert.Equal(0.5, polyline.Vertices[1].Bulge);
        ulong handle = command.CurrentHandle;
        Assert.NotEqual(0UL, handle);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(0UL, command.CurrentHandle);
        document.Header.CurrentEntityColor = ACadSharp.Color.Red;
        document.Header.CurrentLineTypeName = LineType.ByLayerName;
        Assert.True(history.TryRedo(out _));
        Assert.Same(polyline, Assert.Single(document.Entities));
        Assert.Equal(ACadSharp.Color.Cyan, polyline.Color);
        Assert.Equal(LineType.ContinuousName, polyline.LineType.Name);
        Assert.NotEqual(0UL, command.CurrentHandle);
    }

    [Fact]
    public void CommandRejectsInvalidCeltscaleBeforeMutation()
    {
        var document = new CadDocument();
        document.Header.CurrentEntityLinetypeScale = 0.0;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => history.Execute(command));

        Assert.Contains("CELTSCALE", exception.Message, StringComparison.Ordinal);
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Polyline);
    }

    [Fact]
    public void CommandRejectsLockedLayerBeforeMutation()
    {
        var document = new CadDocument();
        document.Header.CurrentLayer.Flags |= LayerFlags.Locked;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        Assert.Throws<InvalidOperationException>(() => history.Execute(command));
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Polyline);
    }

    [Fact]
    public void CommandAuthorsNonzeroPlinewidWithFillModeOffForSnapshotOutline()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 2.0;
        document.Header.FillMode = false;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        history.Execute(command);

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Equal(2.0, polyline.ConstantWidth);
        Assert.Equal(1, history.UndoCount);
        Assert.Same(polyline, command.Polyline);
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        Assert.False(Assert.Single(snapshot.Polylines.ToArray()).IsFillEnabled);
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        RenderCommand outline = Assert.Single(
            scene.DrawingContext.Commands.ToArray());
        Assert.NotNull(outline.Pen);
        Assert.Null(outline.Brush);
    }

    [Fact]
    public void TaperedCommandPersistsVertexWidthsPlinewidAndEntityIdentity()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 7.0;
        document.Header.PolylineLineTypeGeneration = true;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [
                    CadPoint3D.Zero,
                    new CadPoint3D(10, 0, 0),
                    new CadPoint3D(20, 0, 0),
                ],
                [0.0, 0.0, 0.0],
                isClosed: false,
                startWidths: [2.0, 4.0, 0.0],
                endWidths: [4.0, 3.0, 0.0],
                resultingDefaultWidth: 3.0));

        history.Execute(command);

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Equal(0.0, polyline.ConstantWidth);
        Assert.False(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(2.0, polyline.Vertices[0].StartWidth);
        Assert.Equal(4.0, polyline.Vertices[0].EndWidth);
        Assert.Equal(4.0, polyline.Vertices[1].StartWidth);
        Assert.Equal(3.0, polyline.Vertices[1].EndWidth);
        Assert.Equal(3.0, document.Header.PolylineWidthDefault);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(7.0, document.Header.PolylineWidthDefault);
        Assert.True(history.TryRedo(out _));
        Assert.Same(polyline, Assert.Single(document.Entities));
        Assert.Equal(3.0, document.Header.PolylineWidthDefault);
    }

    [Fact]
    public void UniformExplicitWidthsCollapseToConstantWidth()
    {
        var document = new CadDocument();
        document.Header.PolylineLineTypeGeneration = true;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        history.Execute(new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false,
                startWidths: [2.5, 0.0],
                endWidths: [2.5, 0.0],
                resultingDefaultWidth: 2.5)));

        LwPolyline polyline = Assert.IsType<LwPolyline>(Assert.Single(document.Entities));
        Assert.Equal(2.5, polyline.ConstantWidth);
        Assert.True(polyline.Flags.HasFlag(LwPolylineFlags.Plinegen));
        Assert.Equal(0.0, polyline.Vertices[0].StartWidth);
        Assert.Equal(0.0, polyline.Vertices[0].EndWidth);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AnalyticPolylineRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        session.Edit(
            "Set PLINE width",
            document => document.Header.PolylineWidthDefault = 2.5);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [
                    new CadPoint3D(-2, 3, 4),
                    new CadPoint3D(5, 3, 4),
                    new CadPoint3D(5, 9, 4),
                ],
                [0.0, Math.Sqrt(2.0) - 1.0, -0.25],
                isClosed: true)));
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
            sourceName: $"polyline-authoring.{format.ToString().ToLowerInvariant()}");

        LwPolyline polyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<LwPolyline>().ToArray()));
        Assert.True(polyline.IsClosed);
        Assert.Equal(3, polyline.Vertices.Count);
        Assert.Equal(4.0, polyline.Elevation);
        Assert.Equal(2.5, polyline.ConstantWidth);
        Assert.Equal(Math.Sqrt(2.0) - 1.0, polyline.Vertices[1].Bulge, 12);
        Assert.Equal(-0.25, polyline.Vertices[2].Bulge, 12);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredTaperedWidthsRoundTripThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [
                    CadPoint3D.Zero,
                    new CadPoint3D(10, 0, 0),
                    new CadPoint3D(20, 5, 0),
                ],
                [0.0, 0.0, 0.0],
                isClosed: false,
                startWidths: [1.0, 3.0, 0.0],
                endWidths: [3.0, 2.0, 0.0],
                resultingDefaultWidth: 2.0)));
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
            sourceName: $"tapered-polyline-authoring.{format.ToString().ToLowerInvariant()}");

        LwPolyline polyline = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<LwPolyline>().ToArray()));
        Assert.Equal(0.0, polyline.ConstantWidth);
        Assert.Equal(1.0, polyline.Vertices[0].StartWidth);
        Assert.Equal(3.0, polyline.Vertices[0].EndWidth);
        Assert.Equal(3.0, polyline.Vertices[1].StartWidth);
        Assert.Equal(2.0, polyline.Vertices[1].EndWidth);
        Assert.Equal(2.0, loaded.Session.Read(document =>
            document.Header.PolylineWidthDefault));
    }
}
