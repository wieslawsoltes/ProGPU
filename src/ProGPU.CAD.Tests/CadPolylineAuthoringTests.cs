using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
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
        var history = new CadDocumentHistory(new CadDocumentSession(document));
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
    public void CommandRejectsNonzeroPlinewidWithFillModeOffBeforeMutation()
    {
        var document = new CadDocument();
        document.Header.PolylineWidthDefault = 2.0;
        document.Header.FillMode = false;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPolylineCommand(
            new CadPolylineAuthoringSnapshot(
                [CadPoint3D.Zero, new CadPoint3D(10, 0, 0)],
                [0.0, 0.0],
                isClosed: false));

        Exception exception = Assert.ThrowsAny<Exception>(() => history.Execute(command));

        Assert.Equal("CadUnsupportedEntityException", exception.GetType().Name);
        Assert.Contains("FILLMODE", exception.Message, StringComparison.Ordinal);
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Polyline);
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
}
