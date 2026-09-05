using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPlanPolarAdditionalAnglesTests
{
    [Fact]
    public void InvariantSemicolonParserIsBoundedAndNormalizesOneTurn()
    {
        Assert.True(CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
            " -10 ; 25.5 ; 370 ",
            out CadPlanPolarAdditionalAngles angles));

        Assert.Equal(3, angles.Count);
        Assert.Equal(350.0, ToDegrees(angles[0]), 10);
        Assert.Equal(25.5, ToDegrees(angles[1]), 10);
        Assert.Equal(10.0, ToDegrees(angles[2]), 10);
        Assert.Equal(
            CadPlanPolarAdditionalAngles.FromDegrees([-10, 25.5, 370]),
            angles);
        Assert.True(CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
            string.Empty,
            out CadPlanPolarAdditionalAngles empty));
        Assert.Equal(CadPlanPolarAdditionalAngles.Empty, empty);

        Assert.False(CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
            "0;1;2;3;4;5;6;7;8;9;10",
            out _));
        Assert.False(CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
            "10;;20",
            out _));
        Assert.False(CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
            "NaN",
            out _));
        Assert.Throws<ArgumentException>(() =>
            CadPlanPolarAdditionalAngles.FromDegrees(
                [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]));
    }

    [Fact]
    public void AdditionalAngleWinsNearestPathWithoutCreatingMultiples()
    {
        CadPlanPolarAdditionalAngles angles =
            CadPlanPolarAdditionalAngles.FromDegrees([25.0]);
        var settings = CreateSettings(
            isClockwise: false,
            useAdditionalAngles: true,
            angles);

        Assert.True(settings.TryTrack(
            CadPoint3D.Zero,
            PointAtDegrees(27.0, 10.0),
            out CadPlanPolarTrackingResult exact));

        Assert.True(exact.IsAdditionalAngle);
        Assert.Equal(25.0, ToDegrees(exact.AngleRadians), 10);

        Assert.True(settings.TryTrack(
            CadPoint3D.Zero,
            PointAtDegrees(48.0, 10.0),
            out CadPlanPolarTrackingResult nonIncremental));

        Assert.True(nonIncremental.IsAdditionalAngle);
        Assert.Equal(25.0, ToDegrees(nonIncremental.AngleRadians), 10);
        Assert.NotEqual(50.0, ToDegrees(nonIncremental.AngleRadians), 10);
    }

    [Fact]
    public void DisabledListAndClockwiseDirectionRemainExplicit()
    {
        CadPlanPolarAdditionalAngles angles =
            CadPlanPolarAdditionalAngles.FromDegrees([25.0]);
        CadPlanPolarTrackingSettings disabled = CreateSettings(
            isClockwise: false,
            useAdditionalAngles: false,
            angles);

        Assert.True(disabled.TryTrack(
            CadPoint3D.Zero,
            PointAtDegrees(27.0, 10.0),
            out CadPlanPolarTrackingResult incremental));
        Assert.False(incremental.IsAdditionalAngle);
        Assert.Equal(0.0, incremental.AngleRadians, 12);

        CadPlanPolarTrackingSettings clockwise = CreateSettings(
            isClockwise: true,
            useAdditionalAngles: true,
            angles);
        Assert.True(clockwise.TryTrack(
            CadPoint3D.Zero,
            PointAtDegrees(-27.0, 10.0),
            out CadPlanPolarTrackingResult clockwiseResult));

        Assert.True(clockwiseResult.IsAdditionalAngle);
        Assert.Equal(25.0, ToDegrees(clockwiseResult.AngleRadians), 10);
        Assert.Equal(Math.Cos(ToRadians(25.0)), clockwiseResult.Direction.X, 10);
        Assert.Equal(-Math.Sin(ToRadians(25.0)), clockwiseResult.Direction.Y, 10);
    }

    [Fact]
    public void TenAngleWarmQueriesAllocateNoManagedMemory()
    {
        CadPlanPolarAdditionalAngles angles =
            CadPlanPolarAdditionalAngles.FromDegrees(
                [1, 7, 13, 19, 25, 31, 37, 43, 49, 55]);
        CadPlanPolarTrackingSettings settings = CreateSettings(
            isClockwise: false,
            useAdditionalAngles: true,
            angles);
        CadPoint3D pointer = PointAtDegrees(43.2, 100.0);
        Assert.True(settings.TryTrack(CadPoint3D.Zero, pointer, out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allTracked = true;
        for (int i = 0; i < 1_024; i++)
        {
            allTracked &= settings.TryTrack(CadPoint3D.Zero, pointer, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allTracked);
        Assert.Equal(0, allocated);
    }

    private static CadPlanPolarTrackingSettings CreateSettings(
        bool isClockwise,
        bool useAdditionalAngles,
        CadPlanPolarAdditionalAngles angles) =>
        new(
            true,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            isClockwise,
            Math.PI / 2.0,
            useAdditionalAngles,
            angles);

    private static CadPoint3D PointAtDegrees(double degrees, double distance)
    {
        double radians = ToRadians(degrees);
        return new CadPoint3D(
            Math.Cos(radians) * distance,
            Math.Sin(radians) * distance,
            0.0);
    }

    private static double ToDegrees(double radians) =>
        radians * (180.0 / Math.PI);

    private static double ToRadians(double degrees) =>
        degrees * (Math.PI / 180.0);
}
