using ACadSharp;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using CSMath;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPlanGridSnapTests
{
    [Fact]
    public void RectangularGridSnapsIndependentAxesAroundBaseAndPreservesDepth()
    {
        CadPlanGridSnapSettings settings =
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                new CadPoint3D(1.0, -2.0, 7.0),
                2.0,
                5.0);

        Assert.True(settings.TrySnap(
            new CadPoint3D(4.2, -4.6, 19.0),
            out CadPoint3D snapped));

        AssertPoint(new CadPoint3D(5.0, -7.0, 19.0), snapped);
    }

    [Fact]
    public void RotatedGridUsesDeterministicAwayFromZeroMidpoints()
    {
        CadPlanGridSnapSettings settings =
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                CadPoint3D.Zero,
                2.0,
                4.0,
                Math.PI / 2.0);

        Assert.True(settings.TrySnap(
            new CadPoint3D(-2.0, 1.0, 3.0),
            out CadPoint3D positiveTie));
        Assert.True(settings.TrySnap(
            new CadPoint3D(2.0, -1.0, -3.0),
            out CadPoint3D negativeTie));

        AssertPoint(new CadPoint3D(-4.0, 2.0, 3.0), positiveTie);
        AssertPoint(new CadPoint3D(4.0, -2.0, -3.0), negativeTie);
    }

    [Fact]
    public void ArbitraryGridPlanePreservesNormalComponent()
    {
        var settings = new CadPlanGridSnapSettings(
            true,
            CadPlanGridSnapStyle.Rectangular,
            new CadPoint3D(10.0, 20.0, 30.0),
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 0.0, 1.0),
            2.0,
            5.0);

        Assert.True(settings.TrySnap(
            new CadPoint3D(12.9, 27.0, 36.9),
            out CadPoint3D snapped));

        AssertPoint(new CadPoint3D(12.0, 27.0, 35.0), snapped);
    }

    [Fact]
    public void DisabledIsometricAndNonFiniteQueriesDoNotSnap()
    {
        CadPlanGridSnapSettings disabled =
            CadPlanGridSnapSettings.Disabled;
        var isometric = new CadPlanGridSnapSettings(
            true,
            CadPlanGridSnapStyle.Isometric,
            CadPoint3D.Zero,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            1.0,
            1.0);
        CadPoint3D query = new(0.4, 0.6, 0.0);

        Assert.False(disabled.TrySnap(query, out _));
        Assert.False(isometric.TrySnap(query, out _));
        Assert.False(isometric.WithEnabled(false).TrySnap(query, out _));
        Assert.False(CadPlanGridSnapSettings.CreateRectangular(
            true,
            CadPoint3D.Zero,
            1.0,
            1.0).TrySnap(new CadPoint3D(double.NaN, 0.0, 0.0), out _));
        Assert.False(CadPlanGridSnapSettings.CreateRectangular(
            true,
            new CadPoint3D(-double.MaxValue, 0.0, 0.0),
            1.0,
            1.0).TrySnap(new CadPoint3D(double.MaxValue, 0.0, 0.0), out _));
    }

    [Fact]
    public void InvalidSettingsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                CadPoint3D.Zero,
                0.0,
                1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                CadPoint3D.Zero,
                1.0,
                1.0,
                double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => new CadPlanGridSnapSettings(
            true,
            CadPlanGridSnapStyle.Rectangular,
            CadPoint3D.Zero,
            new CadPoint3D(2.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            1.0,
            1.0));
    }

    [Fact]
    public void SnapshotCapturesActiveViewportUcsBaseRotationSpacingAndMode()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew();
        session.Edit("Configure active snap grid", document =>
        {
            document.Header.OrthoMode = true;
            document.Header.AngleBase = Math.PI / 6.0;
            document.Header.AngularDirection = AngularDirection.ClockWise;
            VPort active = document.VPorts[VPort.DefaultName];
            active.SnapOn = true;
            active.IsometricSnap = false;
            active.Origin = new XYZ(100.0, 200.0, 3.0);
            active.XAxis = new XYZ(0.0, 1.0, 0.0);
            active.YAxis = new XYZ(-1.0, 0.0, 0.0);
            active.SnapBasePoint = new XY(2.0, 3.0);
            active.SnapRotation = Math.PI / 2.0;
            active.SnapSpacing = new XY(2.0, 4.0);
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session);
        CadPlanGridSnapSettings settings = snapshot.PlanGridSnapSettings;
        CadPlanPolarTrackingSettings polar = snapshot.PlanPolarTrackingSettings;

        Assert.True(settings.IsEnabled);
        Assert.True(snapshot.IsOrthoModeEnabled);
        Assert.True(settings.IsSupported);
        Assert.Equal(CadPlanGridSnapStyle.Rectangular, settings.Style);
        AssertPoint(new CadPoint3D(97.0, 202.0, 3.0), settings.Origin);
        AssertPoint(new CadPoint3D(-1.0, 0.0, 0.0), settings.XAxis);
        AssertPoint(new CadPoint3D(0.0, -1.0, 0.0), settings.YAxis);
        Assert.Equal(2.0, settings.SpacingX);
        Assert.Equal(4.0, settings.SpacingY);
        Assert.True(polar.IsSupported);
        Assert.False(polar.IsEnabled);
        Assert.True(polar.IsClockwise);
        Assert.Equal(90.0, polar.IncrementDegrees, 10);
        AssertPoint(
            new CadPoint3D(-0.5, Math.Sqrt(3.0) / 2.0, 0.0),
            polar.XAxis);
        AssertPoint(
            new CadPoint3D(-Math.Sqrt(3.0) / 2.0, -0.5, 0.0),
            polar.YAxis);
        Assert.True(settings.TrySnap(
            new CadPoint3D(94.4, 196.1, 8.0),
            out CadPoint3D snapped));
        AssertPoint(new CadPoint3D(95.0, 198.0, 8.0), snapped);
    }

    [Fact]
    public void WarmGridQueriesAllocateNoManagedMemory()
    {
        CadPlanGridSnapSettings settings =
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                new CadPoint3D(10.0, -20.0, 0.0),
                0.25,
                0.5,
                Math.PI / 7.0);
        CadPoint3D query = new(21.234, -43.567, 9.0);
        Assert.True(settings.TrySnap(query, out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allSnapped = true;
        for (int i = 0; i < 1_024; i++)
        {
            allSnapped &= settings.TrySnap(query, out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allSnapped);
        Assert.Equal(0, allocated);
    }

    private static void AssertPoint(
        CadPoint3D expected,
        CadPoint3D actual,
        double tolerance = 1e-10)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0.0, tolerance);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0.0, tolerance);
        Assert.InRange(Math.Abs(actual.Z - expected.Z), 0.0, tolerance);
    }
}
