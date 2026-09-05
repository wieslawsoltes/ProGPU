using ACadSharp;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Scene.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPointAuthoringTests
{
    [Fact]
    public void SessionAcceptsOneFiniteWcsLocationWithoutRetainedGrowth()
    {
        var authoring = new CadPointAuthoringSession();

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(1, 2, 3),
            out CadPointAuthoringSnapshot snapshot,
            out string? error),
            error);
        Assert.Equal(new CadPoint3D(1, 2, 3), snapshot.Location);
        Assert.False(authoring.TryCreateSnapshot(
            new CadPoint3D(double.NaN, 0, 0),
            out _,
            out string? nonFinite));
        Assert.Contains("finite", nonFinite, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandCapturesPropertiesUcsOrientationAndIdentityAcrossHistory()
    {
        var document = new CadDocument();
        var layer = new Layer("POINTS")
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
        document.Header.PointDisplayMode = 98;
        document.Header.PointDisplaySize = -7.5;
        double angle = Math.PI / 6.0;
        document.VPorts[VPort.DefaultName].XAxis =
            new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
        document.VPorts[VPort.DefaultName].YAxis =
            new XYZ(-Math.Sin(angle), Math.Cos(angle), 0);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        var command = new CadAddPointCommand(
            new CadPointAuthoringSnapshot(new CadPoint3D(4, 5, 6)));

        history.Execute(command);

        ACadSharp.Entities.Point point = Assert.IsType<ACadSharp.Entities.Point>(
            Assert.Single(document.Entities));
        Assert.Same(point, command.Point);
        Assert.NotEqual(0UL, command.CurrentHandle);
        Assert.Equal(new XYZ(4, 5, 6), point.Location);
        Assert.Equal(XYZ.AxisZ, point.Normal);
        Assert.Equal(angle, point.Rotation, 12);
        Assert.Equal(0.0, point.Thickness);
        Assert.Same(layer, point.Layer);
        Assert.Equal(ACadSharp.Color.Red, point.Color);
        Assert.Equal(LineType.ContinuousName, point.LineType.Name);
        Assert.Equal(2.5, point.LineTypeScale);
        Assert.Equal(LineWeightType.W40, point.LineWeight);

        CadPointPrimitive primitive = Assert.Single(
            new CadSnapshotCompiler().Compile(session).Points.ToArray());
        AssertPoint(
            new CadPoint3D(Math.Cos(angle), Math.Sin(angle), 0),
            primitive.MarkerXAxis);

        Assert.True(history.TryUndo(out _));
        Assert.Null(point.Owner);
        Assert.Equal(0UL, point.Handle);
        Assert.True(history.TryRedo(out _));
        Assert.Same(point, Assert.Single(document.Entities));
    }

    [Fact]
    public void ModeZeroDoesNotRequireAnActiveViewportForIrrelevantOrientation()
    {
        var document = new CadDocument();
        document.Header.PointDisplayMode = 0;
        document.VPorts.Remove(VPort.DefaultName);
        var history = new CadDocumentHistory(new CadDocumentSession(document));

        history.Execute(new CadAddPointCommand(
            new CadPointAuthoringSnapshot(CadPoint3D.Zero)));

        ACadSharp.Entities.Point point = Assert.IsType<ACadSharp.Entities.Point>(
            Assert.Single(document.Entities));
        Assert.Equal(XYZ.AxisZ, point.Normal);
        Assert.Equal(0.0, point.Rotation);
    }

    [Fact]
    public void UnsupportedOrInvalidCurrentPropertiesFailBeforeMutation()
    {
        AssertPreflightFailure(
            document => document.Header.ThicknessDefault = 1.0,
            "CadUnsupportedEntityException");
        AssertPreflightFailure(
            document => document.Header.CurrentEntityLinetypeScale = 0.0,
            nameof(InvalidOperationException));
        AssertPreflightFailure(
            document => document.Header.PointDisplayMode = 5,
            "CadUnsupportedEntityException");
        AssertPreflightFailure(
            document => document.Header.PointDisplaySize = double.NaN,
            nameof(InvalidOperationException));
        AssertPreflightFailure(
            document => document.Header.CurrentLayer.Flags |= LayerFlags.Locked,
            nameof(InvalidOperationException));
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredPointRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var document = new CadDocument(ACadVersion.AC1032);
        document.Header.PointDisplayMode = 98;
        document.Header.PointDisplaySize = -6.0;
        double angle = Math.PI / 4.0;
        document.VPorts[VPort.DefaultName].XAxis =
            new XYZ(Math.Cos(angle), Math.Sin(angle), 0);
        document.VPorts[VPort.DefaultName].YAxis =
            new XYZ(-Math.Sin(angle), Math.Cos(angle), 0);
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPointCommand(
            new CadPointAuthoringSnapshot(new CadPoint3D(7, 11, 13))));
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
            sourceName: $"point-authoring.{format.ToString().ToLowerInvariant()}");

        ACadSharp.Entities.Point point = loaded.Session.Read(document =>
            Assert.IsType<ACadSharp.Entities.Point>(Assert.Single(document.Entities)));
        Assert.Equal(new XYZ(7, 11, 13), point.Location);
        Assert.Equal(angle, point.Rotation, 12);
        CadPointPrimitive primitive = Assert.Single(
            new CadSnapshotCompiler().Compile(loaded.Session).Points.ToArray());
        Assert.Equal(new CadPoint3D(7, 11, 13), primitive.Position);
        AssertPoint(
            new CadPoint3D(Math.Cos(angle), Math.Sin(angle), 0),
            primitive.MarkerXAxis);
    }

    [Fact]
    public void AuthoredPointUsesExactManagedAndNativeMarkerReplay()
    {
        var document = new CadDocument();
        document.Header.PointDisplayMode = 98;
        document.Header.PointDisplaySize = -10.0;
        var session = new CadDocumentSession(document);
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddPointCommand(
            new CadPointAuthoringSnapshot(new CadPoint3D(1, 2, 0))));
        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);

        CadRecordedPointMarkerScene scene =
            new CadPointMarkerSceneCompiler().Compile(
                snapshot,
                new CadPointMarkerView(200.0f, 0.5));

        Assert.Equal(1, scene.Statistics.RecordedPointCount);
        Assert.Equal(4, Assert.Single(scene.DrawingContext.Commands).Path!.Figures.Count);
        using var picture = scene.CreatePicture();
        Assert.True(GpuPictureNativeSceneCompiler.TryCompile(
            picture,
            96U,
            1U,
            out NativeCompiledPicture? nativePicture,
            out NativePictureCompileFailure failure),
            failure.ToString());
        Assert.NotNull(nativePicture);
        Assert.True(nativePicture.NativeDrawCount > 0);
    }

    private static void AssertPreflightFailure(
        Action<CadDocument> configure,
        string exceptionTypeName)
    {
        var document = new CadDocument();
        configure(document);
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddPointCommand(
            new CadPointAuthoringSnapshot(CadPoint3D.Zero));

        Exception exception = Assert.ThrowsAny<Exception>(() =>
            history.Execute(command));

        Assert.Equal(exceptionTypeName, exception.GetType().Name);
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Point);
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        Assert.Equal(expected.X, actual.X, 12);
        Assert.Equal(expected.Y, actual.Y, 12);
        Assert.Equal(expected.Z, actual.Z, 12);
    }
}
