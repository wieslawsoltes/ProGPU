using System.Numerics;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPlanViewportTests
{
    [Fact]
    public void WorldScreenMappingRoundTripsAndMatchesCameraMatrix()
    {
        var rebase = new CadPoint3D(1_000_000_000, -2_000_000_000, 30);
        var viewport = new CadPlanViewport(
            rebase,
            new Vector2(800, 600),
            new Vector2(25, -30),
            2.5f);
        var world = new CadPoint3D(
            rebase.X + 12.5,
            rebase.Y - 8.25,
            44);

        Vector2 screen = viewport.WorldToScreen(world);
        Vector3 matrixScreen = Vector3.Transform(
            new Vector3(12.5f, -8.25f, 0),
            viewport.CreateCameraMatrix());
        CadPoint3D roundTrip = viewport.ScreenToWorld(screen, world.Z);

        Assert.Equal(matrixScreen.X, screen.X, 5);
        Assert.Equal(matrixScreen.Y, screen.Y, 5);
        Assert.Equal(world.X, roundTrip.X, 5);
        Assert.Equal(world.Y, roundTrip.Y, 5);
        Assert.Equal(world.Z, roundTrip.Z);
    }

    [Fact]
    public void SelectionBoundsAreDirectionIndependentAndSpanDocumentDepth()
    {
        var viewport = new CadPlanViewport(
            new CadPoint3D(100, 200, 0),
            new Vector2(400, 300),
            new Vector2(10, -20),
            4);
        var depth = new CadBounds3D(
            new CadPoint3D(50, 100, -30),
            new CadPoint3D(150, 300, 70));
        Vector2 first = new(250, 100);
        Vector2 second = new(150, 220);

        CadBounds3D forward = viewport.CreateSelectionBounds(
            first,
            second,
            depth,
            inflationPixels: 4);
        CadBounds3D reverse = viewport.CreateSelectionBounds(
            second,
            first,
            depth,
            inflationPixels: 4);

        Assert.Equal(forward, reverse);
        Assert.Equal(-30, forward.Min.Z);
        Assert.Equal(70, forward.Max.Z);
        Assert.Equal(1.0, forward.Max.X - viewport.ScreenToWorld(first).X, 10);
        Assert.Equal(1.0, forward.Max.Y - viewport.ScreenToWorld(first).Y, 10);
    }

    [Fact]
    public void InvalidViewportAndSelectionInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CadPlanViewport(
            CadPoint3D.Zero,
            new Vector2(100, 100),
            Vector2.Zero,
            0));
        Assert.Throws<ArgumentException>(() => new CadPlanViewport(
            CadPoint3D.Zero,
            new Vector2(float.NaN, 100),
            Vector2.Zero,
            1));

        var viewport = new CadPlanViewport(
            CadPoint3D.Zero,
            new Vector2(100, 100),
            Vector2.Zero,
            1);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.CreateSelectionBounds(
            Vector2.Zero,
            Vector2.One,
            CadBounds3D.Empty,
            inflationPixels: -1));
    }
}
