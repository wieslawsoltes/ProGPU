using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadXLineAuthoringTests
{
    [Fact]
    public void SessionKeepsOneFirstPointAndNormalizesEveryThroughPoint()
    {
        var authoring = new CadXLineAuthoringSession(maximumLineCount: 3);

        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(1, 2, 3), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(4, 6, 3), out _));
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(1, 2, 8), out _));

        Assert.Equal(new CadPoint3D(1, 2, 3), authoring.FirstPoint);
        Assert.Equal(2, authoring.LineCount);
        Assert.Equal(3, authoring.PointCount);
        CadPoint3D[] directions = authoring.CreateDirectionSnapshot();
        AssertPoint(new CadPoint3D(0.6, 0.8, 0), directions[0]);
        AssertPoint(new CadPoint3D(0, 0, 1), directions[1]);

        Assert.True(authoring.TryUndoLastLine());
        Assert.Equal(1, authoring.LineCount);
        Assert.True(authoring.TryUndoLastLine());
        Assert.False(authoring.TryUndoLastLine());
        Assert.Equal(new CadPoint3D(1, 2, 3), authoring.FirstPoint);
        Assert.Throws<InvalidOperationException>(
            authoring.CreateDirectionSnapshot);
    }

    [Fact]
    public void SessionRejectsDegenerateNonFiniteAndOverLimitThroughPoints()
    {
        var authoring = new CadXLineAuthoringSession(maximumLineCount: 1);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));

        Assert.False(authoring.TryAcceptPoint(CadPoint3D.Zero, out string? same));
        Assert.Contains("distinct", same, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(double.NaN, 0, 0),
            out string? nonFinite));
        Assert.Contains("finite", nonFinite, StringComparison.OrdinalIgnoreCase);
        Assert.True(authoring.TryAcceptPoint(new CadPoint3D(2, 0, 0), out _));
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(0, 2, 0),
            out string? bounded));
        Assert.Contains("limit", bounded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OppositeMaximumFiniteEndpointsNormalizeWithoutOverflow()
    {
        double maximum = double.MaxValue;
        var authoring = new CadXLineAuthoringSession();
        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(maximum, maximum / 2.0, 0),
            out _));

        Assert.True(authoring.TryAcceptPoint(
            new CadPoint3D(-maximum, -maximum / 2.0, 0),
            out string? error),
            error);

        CadPoint3D direction = Assert.Single(
            authoring.CreateDirectionSnapshot());
        Assert.True(double.IsFinite(direction.X));
        Assert.True(double.IsFinite(direction.Y));
        Assert.Equal(1.0, direction.Length, 12);
        AssertPoint(
            new CadPoint3D(-2.0 / Math.Sqrt(5.0), -1.0 / Math.Sqrt(5.0), 0),
            direction);
    }

    [Fact]
    public void MaximumSizedSessionUsesTheConfiguredBoundExactly()
    {
        const int maximum = 65_536;
        var authoring = new CadXLineAuthoringSession(maximum);
        Assert.True(authoring.TryAcceptPoint(CadPoint3D.Zero, out _));
        for (int i = 1; i <= maximum; i++)
        {
            Assert.True(authoring.TryAcceptPoint(
                new CadPoint3D(i, 1, 0),
                out string? error),
                error);
        }

        Assert.Equal(maximum, authoring.LineCount);
        Assert.Equal(maximum, authoring.CreateDirectionSnapshot().Length);
        Assert.False(authoring.TryAcceptPoint(
            new CadPoint3D(maximum + 1.0, 1, 0),
            out string? bounded));
        Assert.Contains("limit", bounded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandCapturesPropertiesAndPreservesIdentityAcrossHistory()
    {
        var document = new CadDocument();
        var layer = new Layer("XLINES")
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
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var directions = new[]
        {
            new CadPoint3D(3, 4, 0),
            new CadPoint3D(0, 0, -9),
        };
        var command = new CadAddXLineSequenceCommand(
            new CadPoint3D(1, 2, 3),
            directions);
        directions[0] = CadPoint3D.Zero;

        history.Execute(command);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(2, command.LineCount);
        Assert.All(command.CurrentHandles.ToArray(), handle =>
            Assert.NotEqual(0UL, handle));
        XLine[] lines = command.Lines.ToArray();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal(new XYZ(1, 2, 3), line.FirstPoint);
            Assert.Same(layer, line.Layer);
            Assert.Equal(ACadSharp.Color.Red, line.Color);
            Assert.Equal(LineType.ContinuousName, line.LineType.Name);
            Assert.Equal(2.5, line.LineTypeScale);
            Assert.Equal(LineWeightType.W40, line.LineWeight);
        });
        AssertPoint(new CadPoint3D(0.6, 0.8, 0), ToPoint(lines[0].Direction));
        AssertPoint(new CadPoint3D(0, 0, -1), ToPoint(lines[1].Direction));

        Assert.True(history.TryUndo(out _));
        Assert.All(lines, line =>
        {
            Assert.Null(line.Owner);
            Assert.Equal(0UL, line.Handle);
        });
        Assert.All(command.CurrentHandles.ToArray(), handle =>
            Assert.Equal(0UL, handle));

        Assert.True(history.TryRedo(out _));
        Assert.All(lines, line => Assert.Same(document.ModelSpace, line.Owner));
        Assert.Equal(lines, document.Entities.OfType<XLine>().ToArray());
    }

    [Fact]
    public void CommandRejectsInvalidInputAndCurrentStateBeforeMutation()
    {
        Assert.Throws<ArgumentException>(() => new CadAddXLineSequenceCommand(
            CadPoint3D.Zero,
            [CadPoint3D.Zero]));
        Assert.Throws<ArgumentException>(() => new CadAddXLineSequenceCommand(
            new CadPoint3D(double.PositiveInfinity, 0, 0),
            [new CadPoint3D(1, 0, 0)]));

        var document = new CadDocument();
        var locked = new Layer("LOCKED") { Flags = LayerFlags.Locked };
        document.Layers.Add(locked);
        document.Header.CurrentLayerName = locked.Name;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddXLineSequenceCommand(
            CadPoint3D.Zero,
            [new CadPoint3D(1, 0, 0), new CadPoint3D(0, 1, 0)]);

        Assert.Throws<InvalidOperationException>(() => history.Execute(command));
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.True(command.Lines.IsEmpty);

        var invalidScaleDocument = new CadDocument();
        invalidScaleDocument.Header.CurrentEntityLinetypeScale = 0.0;
        var invalidScaleHistory = new CadDocumentHistory(
            new CadDocumentSession(invalidScaleDocument));
        Assert.Throws<InvalidOperationException>(() =>
            invalidScaleHistory.Execute(new CadAddXLineSequenceCommand(
                CadPoint3D.Zero,
                [new CadPoint3D(1, 0, 0)])));
        Assert.Empty(invalidScaleDocument.Entities);
        Assert.Equal(0, invalidScaleHistory.UndoCount);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredSequenceRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument(ACadVersion.AC1032));
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddXLineSequenceCommand(
            new CadPoint3D(-2, 3, 4),
            [new CadPoint3D(3, 4, 0), new CadPoint3D(0, -2, 0)]));
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
            sourceName: $"xline-authoring.{format.ToString().ToLowerInvariant()}");

        XLine[] lines = loaded.Session.Read(document =>
            document.Entities.OfType<XLine>().ToArray());
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Equal(
            new XYZ(-2, 3, 4),
            line.FirstPoint));
        AssertPoint(new CadPoint3D(0.6, 0.8, 0), ToPoint(lines[0].Direction));
        AssertPoint(new CadPoint3D(0, -1, 0), ToPoint(lines[1].Direction));

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            loaded.Session);
        Assert.Equal(2, snapshot.ConstructionLines.Length);
        Assert.Equal(0, snapshot.Statistics.InvalidEntityCount);
        Assert.Equal(0, snapshot.Statistics.UnsupportedEntityCount);
    }

    [Fact]
    public void AuthoredLinesUseSharedManagedAndNativeRetainedReplay()
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddXLineSequenceCommand(
            new CadPoint3D(1, 2, 0),
            [new CadPoint3D(1, 0, 0), new CadPoint3D(0, 1, 0)]));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadRecordedConstructionScene scene = new CadConstructionSceneCompiler().Compile(
            snapshot,
            new CadBounds3D(
                new CadPoint3D(-5, -5, 0),
                new CadPoint3D(5, 5, 0)));

        Assert.Equal(2, scene.Statistics.RecordedEntityCount);
        Assert.Equal(2, Assert.Single(scene.DrawingContext.Commands).Path!.Figures.Count);
        using var picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
    }

    private static CadPoint3D ToPoint(XYZ value) =>
        new(value.X, value.Y, value.Z);

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 12);
        Assert.Equal(expected.Y, actual.Y, 12);
        Assert.Equal(expected.Z, actual.Z, 12);
    }
}
