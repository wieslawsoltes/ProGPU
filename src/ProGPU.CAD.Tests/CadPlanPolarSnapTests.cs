using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPlanPolarSnapTests
{
    [Fact]
    public void ExplicitDistanceSnapsAlongAcquiredPathFromBase()
    {
        CadPoint3D basePoint = new(10.0, -4.0, 3.0);
        CadPlanPolarTrackingResult tracking = Track(
            basePoint,
            new CadPoint3D(20.4, -4.0, 3.0));
        var settings = new CadPlanPolarSnapSettings(true, 4.0);

        Assert.True(settings.TrySnap(
            basePoint,
            tracking,
            fallbackDistance: 7.0,
            out CadPlanPolarTrackingResult snapped));

        Assert.Equal(new CadPoint3D(22.0, -4.0, 3.0), snapped.Point);
        Assert.Equal(12.0, snapped.Distance);
        Assert.True(snapped.IsDistanceSnapped);
        Assert.Equal(4.0, snapped.SnapIncrement);
        Assert.Equal(tracking.PerpendicularDistance, snapped.PerpendicularDistance);
    }

    [Fact]
    public void ZeroDistanceUsesPositiveSnapXFallback()
    {
        CadPoint3D basePoint = new(1.0, 2.0, 0.0);
        CadPlanPolarTrackingResult tracking = Track(
            basePoint,
            new CadPoint3D(1.0, 8.1, 0.0));
        var settings = new CadPlanPolarSnapSettings(true, 0.0);

        Assert.True(settings.TrySnap(
            basePoint,
            tracking,
            fallbackDistance: 2.5,
            out CadPlanPolarTrackingResult snapped));

        Assert.InRange(Math.Abs(snapped.Point.X - 1.0), 0.0, 1e-12);
        Assert.InRange(Math.Abs(snapped.Point.Y - 7.0), 0.0, 1e-12);
        Assert.Equal(5.0, snapped.Distance, 12);
        Assert.Equal(2.5, snapped.SnapIncrement);
    }

    [Fact]
    public void DisabledInvalidAndOverflowingQueriesFailClosed()
    {
        CadPoint3D basePoint = CadPoint3D.Zero;
        CadPlanPolarTrackingResult tracking = Track(
            basePoint,
            new CadPoint3D(4.0, 0.0, 0.0));

        Assert.False(CadPlanPolarSnapSettings.Disabled.TrySnap(
            basePoint,
            tracking,
            fallbackDistance: 1.0,
            out _));
        Assert.False(new CadPlanPolarSnapSettings(true, 0.0).TrySnap(
            basePoint,
            tracking,
            fallbackDistance: 0.0,
            out _));
        Assert.False(new CadPlanPolarSnapSettings(true, double.Epsilon).TrySnap(
            basePoint,
            tracking with { Distance = double.MaxValue },
            fallbackDistance: 1.0,
            out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPlanPolarSnapSettings(true, -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CadPlanPolarSnapSettings(true, double.NaN));
    }

    [Fact]
    public void WarmQueriesAllocateNoManagedMemory()
    {
        CadPoint3D basePoint = new(1.0, -2.0, 0.0);
        CadPlanPolarTrackingResult tracking = Track(
            basePoint,
            new CadPoint3D(7.0, 4.0, 0.0));
        var settings = new CadPlanPolarSnapSettings(true, 0.25);
        Assert.True(settings.TrySnap(basePoint, tracking, 1.0, out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allSnapped = true;
        for (int i = 0; i < 1_024; i++)
        {
            allSnapped &= settings.TrySnap(basePoint, tracking, 1.0, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allSnapped);
        Assert.Equal(0, allocated);
    }

    private static CadPlanPolarTrackingResult Track(
        CadPoint3D basePoint,
        CadPoint3D pointerPoint)
    {
        var tracking = new CadPlanPolarTrackingSettings(
            true,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            false,
            Math.PI / 2.0);
        Assert.True(tracking.TryTrack(basePoint, pointerPoint, out var result));
        return result;
    }
}
