using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadPlanOrthoConstraintTests
{
    [Fact]
    public void NearestAxisWinsAndExactTieChoosesX()
    {
        CadPlanGridSnapSettings basis =
            CadPlanGridSnapSettings.CreateRectangular(
                false,
                CadPoint3D.Zero,
                1.0,
                1.0);
        CadPoint3D basePoint = new(10.0, 20.0, 3.0);

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            basePoint,
            new CadPoint3D(15.0, 22.0, 9.0),
            basis,
            out CadPlanOrthoResult horizontal));
        Assert.Equal(CadPlanOrthoAxis.X, horizontal.Axis);
        Assert.Equal(new CadPoint3D(15.0, 20.0, 3.0), horizontal.Point);
        Assert.False(horizontal.IsGridSnapped);

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            basePoint,
            new CadPoint3D(12.0, 25.0, -4.0),
            basis,
            out CadPlanOrthoResult vertical));
        Assert.Equal(CadPlanOrthoAxis.Y, vertical.Axis);
        Assert.Equal(new CadPoint3D(10.0, 25.0, 3.0), vertical.Point);

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            basePoint,
            new CadPoint3D(14.0, 24.0, 3.0),
            basis,
            out CadPlanOrthoResult tie));
        Assert.Equal(CadPlanOrthoAxis.X, tie.Axis);
    }

    [Fact]
    public void RotationUsesActiveSnapBasis()
    {
        CadPlanGridSnapSettings basis =
            CadPlanGridSnapSettings.CreateRectangular(
                false,
                CadPoint3D.Zero,
                1.0,
                1.0,
                Math.PI / 4.0);

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            CadPoint3D.Zero,
            new CadPoint3D(5.0, 4.0, 0.0),
            basis,
            out CadPlanOrthoResult result));

        Assert.Equal(CadPlanOrthoAxis.X, result.Axis);
        Assert.InRange(Math.Abs(result.Point.X - 4.5), 0.0, 1e-10);
        Assert.InRange(Math.Abs(result.Point.Y - 4.5), 0.0, 1e-10);
        Assert.Equal(0.0, result.Point.Z);
    }

    [Fact]
    public void GridCompositionSnapsMovingCoordinateButPreservesOffGridBaseAxis()
    {
        CadPlanGridSnapSettings basis =
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                new CadPoint3D(0.25, -0.5, 0.0),
                2.0,
                3.0);
        CadPoint3D objectSnapBase = new(0.8, 1.1, 7.0);

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            objectSnapBase,
            new CadPoint3D(4.4, 2.0, -5.0),
            basis,
            out CadPlanOrthoResult result));

        Assert.Equal(CadPlanOrthoAxis.X, result.Axis);
        Assert.True(result.IsGridSnapped);
        Assert.Equal(new CadPoint3D(4.25, 1.1, 7.0), result.Point);
    }

    [Fact]
    public void IsometricOrthoUsesActiveAxisPair()
    {
        CadPlanGridSnapSettings isometric =
            CadPlanGridSnapSettings.CreateIsometric(
                false,
                CadPoint3D.Zero,
                1.0,
                CadPlanIsoplane.Right);

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            CadPoint3D.Zero,
            new CadPoint3D(4.0, 2.5, 0.0),
            isometric,
            out CadPlanOrthoResult result));

        Assert.Equal(CadPlanOrthoAxis.X, result.Axis);
        double distance = CadPoint3D.Dot(
            new CadPoint3D(4.0, 2.5, 0.0),
            isometric.XAxis);
        Assert.InRange(
            (result.Point - (isometric.XAxis * distance)).Length,
            0.0,
            1e-10);
    }

    [Fact]
    public void IsometricGridCompositionProjectsNearestLatticePointOntoBaseAxis()
    {
        CadPlanGridSnapSettings isometric =
            CadPlanGridSnapSettings.CreateIsometric(
                true,
                CadPoint3D.Zero,
                1.0,
                CadPlanIsoplane.Top);
        CadPoint3D basePoint = new(0.2, -0.1, 2.0);
        CadPoint3D pointer = new(2.1, 1.1, 2.0);
        Assert.True(isometric.TrySnap(pointer, out CadPoint3D gridPoint));

        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            basePoint,
            pointer,
            isometric,
            out CadPlanOrthoResult result));

        Assert.Equal(CadPlanOrthoAxis.X, result.Axis);
        Assert.True(result.IsGridSnapped);
        double expectedDistance = CadPoint3D.Dot(
            gridPoint - basePoint,
            isometric.XAxis);
        CadPoint3D expected = basePoint + (isometric.XAxis * expectedDistance);
        Assert.InRange((result.Point - expected).Length, 0.0, 1e-10);
    }

    [Fact]
    public void NonFiniteInputsFailClosed()
    {
        CadPlanGridSnapSettings isometric =
            CadPlanGridSnapSettings.CreateIsometric(
                false,
                CadPoint3D.Zero,
                1.0,
                CadPlanIsoplane.Left);

        Assert.False(CadPlanOrthoConstraint.TryConstrain(
            CadPoint3D.Zero,
            new CadPoint3D(double.NaN, 2.0, 0.0),
            isometric,
            out _));
        Assert.Throws<ArgumentException>(() => new CadPlanGridSnapSettings(
            false,
            CadPlanGridSnapStyle.Isometric,
            CadPoint3D.Zero,
            new CadPoint3D(1.0, 0.0, 0.0),
            new CadPoint3D(0.0, 1.0, 0.0),
            1.0,
            1.0));
    }

    [Fact]
    public void WarmQueriesAllocateNoManagedMemory()
    {
        CadPlanGridSnapSettings basis =
            CadPlanGridSnapSettings.CreateRectangular(
                true,
                CadPoint3D.Zero,
                0.25,
                0.5,
                Math.PI / 9.0);
        CadPoint3D basePoint = new(1.2, -3.4, 0.0);
        CadPoint3D pointer = new(7.8, 9.1, 0.0);
        Assert.True(CadPlanOrthoConstraint.TryConstrain(
            basePoint,
            pointer,
            basis,
            out _));

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allConstrained = true;
        for (int i = 0; i < 1_024; i++)
        {
            allConstrained &= CadPlanOrthoConstraint.TryConstrain(
                basePoint,
                pointer,
                basis,
                out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allConstrained);
        Assert.Equal(0, allocated);
    }
}
