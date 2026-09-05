using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadLineAuthoringTests
{
    [Fact]
    public void SessionAcceptsContiguousPointsAndUndoRetainsSegmentStart()
    {
        var authoring = new CadLineAuthoringSession(maximumSegmentCount: 3);

        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(1, 2, 3),
            out _));
        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(4, 2, 3),
            out _));
        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(4, 6, 3),
            out _));

        Assert.Equal(2, authoring.SegmentCount);
        Assert.Equal(new CadPoint3D(0, 4, 0),
            authoring.PreviousSegmentDirection);
        Assert.True(authoring.CanClose);
        Assert.True(authoring.TryUndoLastSegment());
        Assert.Equal(1, authoring.SegmentCount);
        Assert.Equal(new CadPoint3D(4, 2, 3), authoring.CurrentPoint);
        Assert.False(authoring.CanClose);
        Assert.True(authoring.TryUndoLastSegment());
        Assert.Equal(0, authoring.SegmentCount);
        Assert.Equal(new CadPoint3D(1, 2, 3), authoring.CurrentPoint);
        Assert.False(authoring.TryUndoLastSegment());
    }

    [Fact]
    public void SessionRejectsDegenerateNonFiniteAndOverLimitSegments()
    {
        var authoring = new CadLineAuthoringSession(maximumSegmentCount: 1);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));

        Assert.False(authoring.TryAcceptPoint(CadPoint3D.Zero, out string? same));
        Assert.Contains("distinct", same, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(double.NaN, 0, 0),
            out string? nonFinite));
        Assert.Contains("finite", nonFinite, StringComparison.OrdinalIgnoreCase);
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(1, 0, 0), out _));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(2, 0, 0),
            out string? bounded));
        Assert.Contains("limit", bounded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloseSnapshotAddsOneFinalSegmentWithoutMutatingSession()
    {
        var authoring = new CadLineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(1, 1, 0), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(4, 1, 0), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(4, 5, 0), out _));

        CadPoint3D[] closed = authoring.CreatePointSnapshot(close: true);

        Assert.Equal(4, closed.Length);
        Assert.Equal(closed[0], closed[^1]);
        Assert.Equal(2, authoring.SegmentCount);
        Assert.Equal(3, authoring.PointCount);
    }

    [Fact]
    public void CommandCapturesCurrentPropertiesAndRoundTripsAsOneHistoryEdit()
    {
        var document = new CadDocument();
        var layer = new Layer("AUTHORED")
        {
            Color = ACadSharp.Color.Blue,
            LineWeight = LineWeightType.W25,
        };
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Red;
        document.Header.CurrentLineTypeName = LineType.ContinuousName;
        document.Header.CurrentEntityLinetypeScale = 2.5;
        document.Header.CurrentEntityLineWeight = LineWeightType.W40;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var points = new[]
        {
            new CadPoint3D(1, 2, 3),
            new CadPoint3D(5, 2, 3),
            new CadPoint3D(5, 7, 3),
        };
        var command = new CadAddLineSequenceCommand(points);
        points[1] = new CadPoint3D(99, 99, 99);

        history.Execute(command);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(2, command.SegmentCount);
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.NotEqual(0UL, handle));
        Line[] lines = command.Lines.ToArray();
        Assert.Equal(new XYZ(1, 2, 3), lines[0].StartPoint);
        Assert.Equal(new XYZ(5, 2, 3), lines[0].EndPoint);
        Assert.Equal(new XYZ(5, 2, 3), lines[1].StartPoint);
        Assert.Equal(new XYZ(5, 7, 3), lines[1].EndPoint);
        Assert.All(lines, line =>
        {
            Assert.Same(layer, line.Layer);
            Assert.Equal(ACadSharp.Color.Red, line.Color);
            Assert.Equal(LineType.ContinuousName, line.LineType.Name);
            Assert.Equal(2.5, line.LineTypeScale);
            Assert.Equal(LineWeightType.W40, line.LineWeight);
        });

        Assert.True(history.TryUndo(out _));
        Assert.All(lines, line =>
        {
            Assert.Null(line.Owner);
            Assert.Equal(0UL, line.Handle);
        });
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.Equal(0UL, handle));

        Assert.True(history.TryRedo(out _));
        Assert.All(lines, line => Assert.Same(document.ModelSpace, line.Owner));
        Assert.All(command.CurrentHandles.ToArray(), handle => Assert.NotEqual(0UL, handle));
    }

    [Fact]
    public void CommandRejectsLockedCurrentLayerWithoutPublishingPartialLines()
    {
        var document = new CadDocument();
        var locked = new Layer("LOCKED")
        {
            Flags = LayerFlags.Locked,
        };
        document.Layers.Add(locked);
        document.Header.CurrentLayerName = locked.Name;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadAddLineSequenceCommand([
            CadPoint3D.Zero,
            new CadPoint3D(1, 0, 0),
            new CadPoint3D(1, 1, 0),
        ]);

        Assert.Throws<InvalidOperationException>(() => history.Execute(command));

        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.True(command.Lines.IsEmpty);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredLineSequenceRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddLineSequenceCommand([
            new CadPoint3D(-2, 3, 4),
            new CadPoint3D(5, 3, 4),
            new CadPoint3D(5, 9, 4),
        ]));
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
            sourceName: $"line-authoring.{format.ToString().ToLowerInvariant()}");

        Line[] lines = loaded.Session.Read(document =>
            document.Entities.OfType<Line>().ToArray());
        Assert.Equal(2, lines.Length);
        Assert.Equal(new XYZ(-2, 3, 4), lines[0].StartPoint);
        Assert.Equal(new XYZ(5, 3, 4), lines[0].EndPoint);
        Assert.Equal(new XYZ(5, 3, 4), lines[1].StartPoint);
        Assert.Equal(new XYZ(5, 9, 4), lines[1].EndPoint);
    }
}
