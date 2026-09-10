using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadCircleAuthoringTests
{
    [Theory]
    [InlineData(CadCircleAuthoringMode.CenterRadius, 2.0, 3.0, 5.0)]
    [InlineData(CadCircleAuthoringMode.CenterDiameter, 2.0, 3.0, 2.5)]
    [InlineData(CadCircleAuthoringMode.TwoPoint, 4.5, 3.0, 2.5)]
    public void TwoPointModesCreateExactCenterAndRadius(
        CadCircleAuthoringMode mode,
        double expectedCenterX,
        double expectedCenterY,
        double expectedRadius)
    {
        var authoring = new CadCircleAuthoringSession(mode);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(2, 3, 7),
            out _));

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(7, 3, 7),
            out CadCircleAuthoringSnapshot snapshot,
            out _));

        Assert.Equal(new CadPoint3D(expectedCenterX, expectedCenterY, 7), snapshot.Center);
        Assert.Equal(expectedRadius, snapshot.Radius, 12);
        Assert.Equal(1, authoring.PointCount);
    }

    [Theory]
    [InlineData(CadCircleAuthoringMode.CenterRadius, 8.0)]
    [InlineData(CadCircleAuthoringMode.CenterDiameter, 4.0)]
    public void CenterModesAcceptNumericRadiusOrDiameterWithoutDirection(
        CadCircleAuthoringMode mode,
        double expectedRadius)
    {
        var authoring = new CadCircleAuthoringSession(mode);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(2, 3, 7),
            out _));

        Assert.True(authoring.TryCreateSnapshotFromScalar(
            8.0,
            out CadCircleAuthoringSnapshot snapshot,
            out _));

        Assert.Equal(new CadPoint3D(2, 3, 7), snapshot.Center);
        Assert.Equal(expectedRadius, snapshot.Radius);
        Assert.Equal(1, authoring.PointCount);
    }

    [Fact]
    public void ThreePointModeSolvesExactCircumcircleWithoutMutatingFinalPoint()
    {
        var authoring = new CadCircleAuthoringSession(
            CadCircleAuthoringMode.ThreePoint);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1_000_000_000_005, -2_000_000_000_000, 9),
            out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1_000_000_000_000, -1_999_999_999_995, 9),
            out _));

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(999_999_999_995, -2_000_000_000_000, 9),
            out CadCircleAuthoringSnapshot snapshot,
            out _));

        Assert.Equal(
            new CadPoint3D(1_000_000_000_000, -2_000_000_000_000, 9),
            snapshot.Center);
        Assert.Equal(5.0, snapshot.Radius, 12);
        Assert.Equal(2, authoring.PointCount);
    }

    [Fact]
    public void SessionRejectsInvalidPlaneDuplicatesAndCollinearThreePointSolve()
    {
        var authoring = new CadCircleAuthoringSession(
            CadCircleAuthoringMode.ThreePoint);
        Assert.True(authoring.TryAcceptIntermediatePoint(CadPoint3D.Zero, out _));
        Assert.False(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1, 0, 2),
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryAcceptIntermediatePoint(
            CadPoint3D.Zero,
            out string? duplicate));
        Assert.Contains("distinct", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(double.NaN, 1, 0),
            out string? nonfinite));
        Assert.Contains("finite", nonfinite, StringComparison.OrdinalIgnoreCase);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1, 0, 0),
            out _));

        Assert.False(authoring.TryCreateSnapshot(
            new CadPoint3D(2, 0, 0),
            out _,
            out string? collinear));

        Assert.Contains("non-collinear", collinear, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, authoring.PointCount);
    }

    [Fact]
    public void CommandCapturesCurrentPropertiesAndRedoRetainsSameCircle()
    {
        var document = new CadDocument();
        var layer = new Layer("CIRCLES");
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Cyan;
        document.Header.CurrentLineTypeName = LineType.ContinuousName;
        document.Header.CurrentEntityLinetypeScale = 2.5;
        document.Header.CurrentEntityLineWeight = LineWeightType.W35;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddCircleCommand(
            new CadCircleAuthoringSnapshot(new CadPoint3D(4, 5, 6), 7));

        history.Execute(command);

        Circle circle = Assert.IsType<Circle>(Assert.Single(document.Entities));
        Assert.Same(circle, command.Circle);
        Assert.Same(layer, circle.Layer);
        Assert.Equal(ACadSharp.Color.Cyan, circle.Color);
        Assert.Equal(LineType.ContinuousName, circle.LineType.Name);
        Assert.Equal(2.5, circle.LineTypeScale);
        Assert.Equal(LineWeightType.W35, circle.LineWeight);
        Assert.Equal(new XYZ(4, 5, 6), circle.Center);
        Assert.Equal(XYZ.AxisZ, circle.Normal);
        Assert.Equal(7.0, circle.Radius);
        Assert.Equal(0.0, circle.Thickness);
        Assert.NotEqual(0UL, command.CurrentHandle);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(0UL, command.CurrentHandle);
        document.Header.CurrentEntityColor = ACadSharp.Color.Red;
        document.Header.CurrentLineTypeName = LineType.ByLayerName;
        Assert.True(history.TryRedo(out _));
        Assert.Same(circle, Assert.Single(document.Entities));
        Assert.Equal(ACadSharp.Color.Cyan, circle.Color);
        Assert.Equal(LineType.ContinuousName, circle.LineType.Name);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("celtscale")]
    [InlineData("thickness")]
    public void CommandRejectsUnsupportedCurrentStateBeforeMutation(string failure)
    {
        var document = new CadDocument();
        switch (failure)
        {
            case "locked":
                document.Header.CurrentLayer.Flags |= LayerFlags.Locked;
                break;
            case "celtscale":
                document.Header.CurrentEntityLinetypeScale = 0.0;
                break;
            case "thickness":
                document.Header.ThicknessDefault = 2.0;
                break;
        }
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddCircleCommand(
            new CadCircleAuthoringSnapshot(CadPoint3D.Zero, 4));

        Exception exception = Assert.ThrowsAny<Exception>(() => history.Execute(command));

        if (failure == "thickness")
        {
            Assert.Equal("CadUnsupportedEntityException", exception.GetType().Name);
        }
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Circle);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredCircleRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddCircleCommand(
            new CadCircleAuthoringSnapshot(new CadPoint3D(-2, 3, 4), 9.5)));
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
            sourceName: $"circle-authoring.{format.ToString().ToLowerInvariant()}");

        Circle circle = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<Circle>().ToArray()));
        Assert.Equal(new XYZ(-2, 3, 4), circle.Center);
        Assert.Equal(9.5, circle.Radius, 12);
        Assert.Equal(XYZ.AxisZ, circle.Normal);
        Assert.Equal(0.0, circle.Thickness);
    }
}
