using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPlanPolarTrackingTests
{
    [Fact]
    public void NearestIncrementalPathProjectsPointerExactly()
    {
        var settings = new CadPlanPolarTrackingSettings(
            true,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            false,
            Math.PI / 4.0);
        CadPoint3D basePoint = new(10.0, -4.0, 3.0);

        Assert.True(settings.TryTrack(
            basePoint,
            new CadPoint3D(15.0, 0.0, 3.0),
            out CadPlanPolarTrackingResult result));

        Assert.InRange(Math.Abs(result.Point.X - 14.5), 0.0, 1e-12);
        Assert.InRange(Math.Abs(result.Point.Y - 0.5), 0.0, 1e-12);
        Assert.Equal(3.0, result.Point.Z);
        Assert.InRange(Math.Abs(result.AngleRadians - (Math.PI / 4.0)), 0.0, 1e-12);
        Assert.InRange(Math.Abs(result.PerpendicularDistance - Math.Sqrt(0.5)), 0.0, 1e-12);
    }

    [Fact]
    public void AdjustedBasisAndClockwiseMeasurementAreExplicit()
    {
        double cosine = Math.Cos(Math.PI / 6.0);
        double sine = Math.Sin(Math.PI / 6.0);
        var settings = new CadPlanPolarTrackingSettings(
            true,
            new CadPoint3D(cosine, sine, 0.0),
            new CadPoint3D(-sine, cosine, 0.0),
            true,
            Math.PI / 4.0);

        Assert.True(settings.TryTrack(
            CadPoint3D.Zero,
            new CadPoint3D(4.0, 0.0, 0.0),
            out CadPlanPolarTrackingResult result));

        Assert.InRange(Math.Abs(result.AngleRadians - (Math.PI / 4.0)), 0.0, 1e-12);
        Assert.InRange(Math.Abs(result.Direction.X - Math.Cos(-Math.PI / 12.0)), 0.0, 1e-12);
        Assert.InRange(Math.Abs(result.Direction.Y - Math.Sin(-Math.PI / 12.0)), 0.0, 1e-12);
    }

    [Fact]
    public void DisabledNonFiniteAndInvalidSettingsFailClosed()
    {
        Assert.False(CadPlanPolarTrackingSettings.Disabled.TryTrack(
            CadPoint3D.Zero,
            new CadPoint3D(1.0, 1.0, 0.0),
            out _));
        Assert.False(new CadPlanPolarTrackingSettings(
                true,
                new CadPoint3D(1.0, 0.0, 0.0),
                new CadPoint3D(0.0, 1.0, 0.0),
                false,
                Math.PI / 2.0)
            .TryTrack(
                CadPoint3D.Zero,
                new CadPoint3D(double.NaN, 1.0, 0.0),
                out _));
        Assert.Throws<ArgumentException>(() =>
            new CadPlanPolarTrackingSettings(
                true,
                new CadPoint3D(1.0, 0.0, 0.0),
                new CadPoint3D(0.0, 1.0, 0.0),
                false,
                0.3));
    }

    [Fact]
    public void WarmQueriesAllocateNoManagedMemory()
    {
        var settings = new CadPlanPolarTrackingSettings(
            true,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            false,
            Math.PI / 18.0);
        CadPoint3D basePoint = new(1.0, -2.0, 0.0);
        CadPoint3D pointer = new(7.0, 5.0, 0.0);
        Assert.True(settings.TryTrack(basePoint, pointer, out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allTracked = true;
        for (int i = 0; i < 1_024; i++)
        {
            allTracked &= settings.TryTrack(basePoint, pointer, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allTracked);
        Assert.Equal(0, allocated);
    }
}
