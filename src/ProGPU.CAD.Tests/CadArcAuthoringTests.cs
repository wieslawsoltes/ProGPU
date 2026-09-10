using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadArcAuthoringTests
{
    private const double Tolerance = 1e-10;

    [Theory]
    [InlineData(CadArcAuthoringMode.ThreePoint)]
    [InlineData(CadArcAuthoringMode.CenterStartEnd)]
    [InlineData(CadArcAuthoringMode.StartCenterEnd)]
    [InlineData(CadArcAuthoringMode.StartEndDirection)]
    public void PointModesResolveExactAnalyticArcs(CadArcAuthoringMode mode)
    {
        var authoring = new CadArcAuthoringSession(mode);
        CadPoint3D first;
        CadPoint3D second;
        CadPoint3D final;
        CadPoint3D expectedCenter;
        double expectedRadius;
        double expectedSweep;
        switch (mode)
        {
            case CadArcAuthoringMode.ThreePoint:
                first = new CadPoint3D(1, 0, 7);
                second = new CadPoint3D(0, 1, 7);
                final = new CadPoint3D(-1, 0, 7);
                expectedCenter = new CadPoint3D(0, 0, 7);
                expectedRadius = 1.0;
                expectedSweep = Math.PI;
                break;
            case CadArcAuthoringMode.CenterStartEnd:
                first = new CadPoint3D(0, 0, 7);
                second = new CadPoint3D(2, 0, 7);
                final = new CadPoint3D(0, 5, 7);
                expectedCenter = first;
                expectedRadius = 2.0;
                expectedSweep = Math.PI / 2.0;
                break;
            case CadArcAuthoringMode.StartCenterEnd:
                first = new CadPoint3D(2, 0, 7);
                second = new CadPoint3D(0, 0, 7);
                final = new CadPoint3D(0, 5, 7);
                expectedCenter = second;
                expectedRadius = 2.0;
                expectedSweep = Math.PI / 2.0;
                break;
            case CadArcAuthoringMode.StartEndDirection:
                first = new CadPoint3D(0, 0, 7);
                second = new CadPoint3D(1, 1, 7);
                final = new CadPoint3D(1, 0, 7);
                expectedCenter = new CadPoint3D(0, 1, 7);
                expectedRadius = 1.0;
                expectedSweep = Math.PI / 2.0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Assert.True(authoring.TryAcceptIntermediatePoint(first, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(second, out _));
        Assert.True(authoring.TryCreateSnapshot(
            final,
            out CadArcAuthoringSnapshot snapshot,
            out _));

        AssertPoint(expectedCenter, snapshot.Center);
        AssertClose(expectedRadius, snapshot.Radius);
        AssertClose(expectedSweep, snapshot.SweepAngle);
        Assert.Equal(2, authoring.PointCount);
    }

    [Theory]
    [InlineData(CadArcAuthoringMode.CenterStartEnd)]
    [InlineData(CadArcAuthoringMode.StartCenterEnd)]
    [InlineData(CadArcAuthoringMode.StartEndDirection)]
    public void ClockwiseOverrideSelectsComplementOnSameCircle(
        CadArcAuthoringMode mode)
    {
        CadPoint3D center = new(0, 10, 4);
        CadPoint3D start = new(0, 0, 4);
        CadPoint3D end = new(10, 10, 4);
        CadPoint3D direction = new(10, 0, 4);
        var authoring = new CadArcAuthoringSession(mode);
        Assert.False(authoring.CanApplyClockwiseOverride);
        if (mode == CadArcAuthoringMode.CenterStartEnd)
        {
            Assert.True(authoring.TryAcceptIntermediatePoint(center, out _));
            Assert.True(authoring.TryAcceptIntermediatePoint(start, out _));
        }
        else
        {
            Assert.True(authoring.TryAcceptIntermediatePoint(start, out _));
            Assert.True(authoring.TryAcceptIntermediatePoint(
                mode == CadArcAuthoringMode.StartCenterEnd ? center : end,
                out _));
        }
        Assert.True(authoring.CanApplyClockwiseOverride);
        CadPoint3D finalPoint = mode == CadArcAuthoringMode.StartEndDirection
            ? direction
            : end;

        Assert.True(authoring.TryCreateSnapshot(
            finalPoint,
            out CadArcAuthoringSnapshot compatibleDefault,
            out _));
        Assert.True(authoring.TryCreateSnapshot(
            finalPoint,
            clockwiseOverride: false,
            out CadArcAuthoringSnapshot explicitDefault,
            out _));
        Assert.True(authoring.TryCreateSnapshot(
            finalPoint,
            clockwiseOverride: true,
            out CadArcAuthoringSnapshot clockwise,
            out _));

        Assert.Equal(compatibleDefault, explicitDefault);
        AssertPoint(explicitDefault.Center, clockwise.Center);
        AssertClose(explicitDefault.Radius, clockwise.Radius);
        AssertPoint(explicitDefault.StartPoint, clockwise.EndPoint);
        AssertPoint(explicitDefault.EndPoint, clockwise.StartPoint);
        AssertClose(Math.PI / 2.0, explicitDefault.SweepAngle);
        AssertClose(Math.PI * 3.0 / 2.0, clockwise.SweepAngle);
    }

    [Fact]
    public void ClockwiseOverrideDoesNotReplaceThreePointCircumferenceRoute()
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.ThreePoint);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1, 0, 3),
            out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(0, 1, 3),
            out _));
        Assert.False(authoring.CanApplyClockwiseOverride);

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(-1, 0, 3),
            clockwiseOverride: false,
            out CadArcAuthoringSnapshot defaultSnapshot,
            out _));
        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(-1, 0, 3),
            clockwiseOverride: true,
            out CadArcAuthoringSnapshot overrideSnapshot,
            out _));

        Assert.Equal(defaultSnapshot, overrideSnapshot);
    }

    [Fact]
    public void ClockwiseOverrideRetainsAlreadyClockwiseDirectionSolve()
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.StartEndDirection);
        Assert.True(authoring.TryAcceptIntermediatePoint(CadPoint3D.Zero, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(10, -10, 0),
            out _));

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(10, 0, 0),
            clockwiseOverride: false,
            out CadArcAuthoringSnapshot defaultSnapshot,
            out _));
        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(10, 0, 0),
            clockwiseOverride: true,
            out CadArcAuthoringSnapshot overrideSnapshot,
            out _));

        Assert.Equal(defaultSnapshot, overrideSnapshot);
    }

    [Fact]
    public void ThreePointClockwiseConstructionCanonicalizesSameGeometricInterval()
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.ThreePoint);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1, 0, 3),
            out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(0, -1, 3),
            out _));

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(-1, 0, 3),
            out CadArcAuthoringSnapshot snapshot,
            out _));

        AssertPoint(new CadPoint3D(0, 0, 3), snapshot.Center);
        AssertPoint(new CadPoint3D(-1, 0, 3), snapshot.StartPoint);
        AssertPoint(new CadPoint3D(1, 0, 3), snapshot.EndPoint);
        AssertClose(Math.PI, snapshot.SweepAngle);
    }

    [Fact]
    public void ThreePointSolveIsStableAtLargeWcsOrigin()
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.ThreePoint);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1_000_000_000_005, -2_000_000_000_000, 9),
            out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1_000_000_000_000, -1_999_999_999_995, 9),
            out _));

        Assert.True(authoring.TryCreateSnapshot(
            new CadPoint3D(999_999_999_995, -2_000_000_000_000, 9),
            out CadArcAuthoringSnapshot snapshot,
            out _));

        Assert.Equal(
            new CadPoint3D(1_000_000_000_000, -2_000_000_000_000, 9),
            snapshot.Center);
        AssertClose(5.0, snapshot.Radius);
        AssertClose(Math.PI, snapshot.SweepAngle);
    }

    [Fact]
    public void NumericDirectionDoesNotAddUnitVectorToLargeWcsOrigin()
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.StartEndDirection);
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(10_000_000_000_000_000, -10_000_000_000_000_000, 6),
            out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(10_000_000_000_000_010, -9_999_999_999_999_990, 6),
            out _));

        Assert.True(authoring.TryCreateSnapshotFromScalar(
            0.0,
            out CadArcAuthoringSnapshot snapshot,
            out _));

        Assert.Equal(
            new CadPoint3D(10_000_000_000_000_000, -9_999_999_999_999_990, 6),
            snapshot.Center);
        AssertClose(10.0, snapshot.Radius);
        AssertClose(Math.PI / 2.0, snapshot.SweepAngle);
    }

    [Theory]
    [InlineData(CadArcAuthoringMode.CenterStartAngle, Math.PI / 2.0, 0.0, 0.0, 2.0, Math.PI / 2.0)]
    [InlineData(CadArcAuthoringMode.CenterStartAngle, -Math.PI / 2.0, 0.0, 0.0, 2.0, Math.PI / 2.0)]
    [InlineData(CadArcAuthoringMode.StartCenterAngle, Math.PI / 2.0, 0.0, 0.0, 2.0, Math.PI / 2.0)]
    [InlineData(CadArcAuthoringMode.CenterStartChord, 2.0, 0.0, 0.0, 2.0, Math.PI / 3.0)]
    [InlineData(CadArcAuthoringMode.CenterStartChord, -2.0, 0.0, 0.0, 2.0, Math.PI * 5.0 / 3.0)]
    [InlineData(CadArcAuthoringMode.StartCenterChord, 2.0, 0.0, 0.0, 2.0, Math.PI / 3.0)]
    [InlineData(CadArcAuthoringMode.StartEndAngle, Math.PI / 2.0, 0.0, 0.0, 1.0, Math.PI / 2.0)]
    [InlineData(CadArcAuthoringMode.StartEndAngle, -Math.PI / 2.0, 1.0, 1.0, 1.0, Math.PI / 2.0)]
    [InlineData(CadArcAuthoringMode.StartEndDirection, 0.0, 0.0, 1.0, 1.0, Math.PI / 2.0)]
    [InlineData(CadArcAuthoringMode.StartEndRadius, 1.0, 0.5, 0.8660254037844386, 1.0, Math.PI / 3.0)]
    [InlineData(CadArcAuthoringMode.StartEndRadius, -1.0, 0.5, -0.8660254037844386, 1.0, Math.PI * 5.0 / 3.0)]
    public void ScalarModesResolveExactSignedContracts(
        CadArcAuthoringMode mode,
        double scalar,
        double expectedCenterX,
        double expectedCenterY,
        double expectedRadius,
        double expectedSweep)
    {
        var authoring = new CadArcAuthoringSession(mode);
        bool centerFirst = mode is
            CadArcAuthoringMode.CenterStartAngle or
            CadArcAuthoringMode.CenterStartChord;
        bool startCenter = mode is
            CadArcAuthoringMode.StartCenterAngle or
            CadArcAuthoringMode.StartCenterChord;
        CadPoint3D first = centerFirst
            ? new CadPoint3D(0, 0, 5)
            : startCenter
                ? new CadPoint3D(2, 0, 5)
                : mode == CadArcAuthoringMode.StartEndDirection
                    ? new CadPoint3D(0, 0, 5)
                    : mode == CadArcAuthoringMode.StartEndAngle
                        ? new CadPoint3D(1, 0, 5)
                        : new CadPoint3D(0, 0, 5);
        CadPoint3D second = centerFirst
            ? new CadPoint3D(2, 0, 5)
            : startCenter
                ? new CadPoint3D(0, 0, 5)
                : mode == CadArcAuthoringMode.StartEndDirection
                    ? new CadPoint3D(1, 1, 5)
                    : mode == CadArcAuthoringMode.StartEndAngle
                        ? new CadPoint3D(0, 1, 5)
                        : new CadPoint3D(1, 0, 5);
        Assert.True(authoring.TryAcceptIntermediatePoint(first, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(second, out _));

        Assert.True(authoring.TryCreateSnapshotFromScalar(
            scalar,
            out CadArcAuthoringSnapshot snapshot,
            out _));

        AssertPoint(
            new CadPoint3D(expectedCenterX, expectedCenterY, 5),
            snapshot.Center);
        AssertClose(expectedRadius, snapshot.Radius);
        AssertClose(expectedSweep, snapshot.SweepAngle);
        Assert.Equal(2, authoring.PointCount);
    }

    [Theory]
    [InlineData(CadArcAuthoringMode.ThreePoint, 0.0)]
    [InlineData(CadArcAuthoringMode.CenterStartAngle, 0.0)]
    [InlineData(CadArcAuthoringMode.CenterStartAngle, 6.283185307179586)]
    [InlineData(CadArcAuthoringMode.CenterStartChord, 5.0)]
    [InlineData(CadArcAuthoringMode.StartEndAngle, 0.0)]
    [InlineData(CadArcAuthoringMode.StartEndDirection, 0.0)]
    [InlineData(CadArcAuthoringMode.StartEndRadius, 0.4)]
    public void InvalidFinalGeometryFailsWithoutMutatingAcceptedPoints(
        CadArcAuthoringMode mode,
        double scalar)
    {
        var authoring = new CadArcAuthoringSession(mode);
        CadPoint3D first = mode is
            CadArcAuthoringMode.CenterStartAngle or
            CadArcAuthoringMode.CenterStartChord
                ? new CadPoint3D(0, 0, 4)
                : new CadPoint3D(0, 0, 4);
        CadPoint3D second = mode is
            CadArcAuthoringMode.CenterStartAngle or
            CadArcAuthoringMode.CenterStartChord
                ? new CadPoint3D(2, 0, 4)
                : new CadPoint3D(1, 0, 4);
        Assert.True(authoring.TryAcceptIntermediatePoint(first, out _));
        Assert.True(authoring.TryAcceptIntermediatePoint(second, out _));

        bool accepted = mode == CadArcAuthoringMode.ThreePoint
            ? authoring.TryCreateSnapshot(
                new CadPoint3D(2, 0, 4),
                out _,
                out _)
            : authoring.TryCreateSnapshotFromScalar(scalar, out _, out _);

        Assert.False(accepted);
        Assert.Equal(2, authoring.PointCount);
    }

    [Fact]
    public void SessionRejectsNonfiniteDuplicateAndOffPlanePoints()
    {
        var authoring = new CadArcAuthoringSession(
            CadArcAuthoringMode.ThreePoint);
        Assert.False(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(double.NaN, 0, 0),
            out string? nonfinite));
        Assert.Contains("finite", nonfinite, StringComparison.OrdinalIgnoreCase);
        Assert.True(authoring.TryAcceptIntermediatePoint(CadPoint3D.Zero, out _));
        Assert.False(authoring.TryAcceptIntermediatePoint(
            CadPoint3D.Zero,
            out string? duplicate));
        Assert.Contains("distinct", duplicate, StringComparison.OrdinalIgnoreCase);
        Assert.False(authoring.TryAcceptIntermediatePoint(
            new CadPoint3D(1, 0, 2),
            out string? offPlane));
        Assert.Contains("plane", offPlane, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CommandCapturesCurrentPropertiesAndRedoRetainsSameArc()
    {
        var document = new CadDocument();
        var layer = new Layer("ARCS");
        document.Layers.Add(layer);
        document.Header.CurrentLayerName = layer.Name;
        document.Header.CurrentEntityColor = ACadSharp.Color.Cyan;
        document.Header.CurrentLineTypeName = LineType.ContinuousName;
        document.Header.CurrentEntityLinetypeScale = 2.5;
        document.Header.CurrentEntityLineWeight = LineWeightType.W35;
        var history = new CadDocumentHistory(new CadDocumentSession(document));
        var command = new CadAddArcCommand(new CadArcAuthoringSnapshot(
            new CadPoint3D(4, 5, 6),
            7,
            0.25,
            1.5));

        history.Execute(command);

        Arc arc = Assert.IsType<Arc>(Assert.Single(document.Entities));
        Assert.Same(arc, command.Arc);
        Assert.Same(layer, arc.Layer);
        Assert.Equal(ACadSharp.Color.Cyan, arc.Color);
        Assert.Equal(LineType.ContinuousName, arc.LineType.Name);
        Assert.Equal(2.5, arc.LineTypeScale);
        Assert.Equal(LineWeightType.W35, arc.LineWeight);
        Assert.Equal(new XYZ(4, 5, 6), arc.Center);
        Assert.Equal(XYZ.AxisZ, arc.Normal);
        Assert.Equal(7.0, arc.Radius);
        AssertClose(0.25, arc.StartAngle);
        AssertClose(1.75, arc.EndAngle);
        Assert.Equal(0.0, arc.Thickness);
        Assert.NotEqual(0UL, command.CurrentHandle);

        Assert.True(history.TryUndo(out _));
        Assert.Empty(document.Entities);
        Assert.Equal(0UL, command.CurrentHandle);
        document.Header.CurrentEntityColor = ACadSharp.Color.Red;
        Assert.True(history.TryRedo(out _));
        Assert.Same(arc, Assert.Single(document.Entities));
        Assert.Equal(ACadSharp.Color.Cyan, arc.Color);
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
        var command = new CadAddArcCommand(new CadArcAuthoringSnapshot(
            CadPoint3D.Zero,
            4,
            0,
            Math.PI));

        Exception exception = Assert.ThrowsAny<Exception>(() =>
            history.Execute(command));

        if (failure == "thickness")
        {
            Assert.Equal("CadUnsupportedEntityException", exception.GetType().Name);
        }
        Assert.Empty(document.Entities);
        Assert.Equal(0, history.UndoCount);
        Assert.Null(command.Arc);
    }

    [Theory]
    [InlineData(CadDocumentFormat.Dxf)]
    [InlineData(CadDocumentFormat.Dwg)]
    public async Task AuthoredArcRoundTripsThroughCadStore(
        CadDocumentFormat format)
    {
        var session = new CadDocumentSession(new CadDocument());
        var history = new CadDocumentHistory(session);
        history.Execute(new CadAddArcCommand(new CadArcAuthoringSnapshot(
            new CadPoint3D(-2, 3, 4),
            9.5,
            0.25,
            4.5)));
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
            sourceName: $"arc-authoring.{format.ToString().ToLowerInvariant()}");

        Arc arc = Assert.Single(loaded.Session.Read(document =>
            document.Entities.OfType<Arc>().ToArray()));
        Assert.Equal(new XYZ(-2, 3, 4), arc.Center);
        AssertClose(9.5, arc.Radius);
        AssertClose(0.25, arc.StartAngle);
        AssertClose(4.75, arc.EndAngle);
        Assert.Equal(XYZ.AxisZ, arc.Normal);
        Assert.Equal(0.0, arc.Thickness);
    }

    private static void AssertPoint(CadPoint3D expected, CadPoint3D actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Z, actual.Z);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
}
