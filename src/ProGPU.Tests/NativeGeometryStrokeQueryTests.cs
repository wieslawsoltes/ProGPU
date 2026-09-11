using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeGeometryStrokeQueryTests
{
    private static NativeGeometryQueryPen Pen => new() { Thickness = 2, MiterLimit = 10 };

    [Fact]
    public void GeneratedQueryLayoutsMatchTheCHeader()
    {
        Assert.Equal(20, Unsafe.SizeOf<NativeGeometryQueryFigure>());
        Assert.Equal(16, Marshal.OffsetOf<NativeGeometryQueryFigure>(nameof(NativeGeometryQueryFigure.Flags)).ToInt32());
        Assert.Equal(28, Unsafe.SizeOf<NativeGeometryQueryPen>());
        Assert.Equal(24, Marshal.OffsetOf<NativeGeometryQueryPen>(nameof(NativeGeometryQueryPen.LineJoin)).ToInt32());
        Assert.Equal(24, Unsafe.SizeOf<Matrix3x2>());
        Assert.Equal(16, Marshal.OffsetOf<Matrix3x2>(nameof(Matrix3x2.M31)).ToInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidToleranceRejectsBeforeLoading(float tolerance)
    {
        Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.GetStrokeBounds(
            [], [], [], Pen, [], Matrix3x2.Identity, tolerance, out _));
    }

    [Fact]
    public void InvalidPenTransformPointAndSpanLengthsRejectBeforeLoading()
    {
        var pen = Pen;
        pen.LineJoin = 3;
        Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.GetStrokeBounds(
            [], [], [], pen, [], Matrix3x2.Identity, 0.25f, out _));
        Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.GetStrokeBounds(
            [], [default], [], Pen, [], Matrix3x2.Identity, 0.25f, out _));
        Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.StrokeContains(
            [], [], [], Pen, [], Matrix3x2.Identity, new(float.NaN, 0), 0.25f));
        Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.GetStrokeBounds(
            [], [], [], Pen, [], new(1, 0, 0, 1, float.PositiveInfinity, 0), 0.25f, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.GetStrokeBounds(
            [], [], [], Pen, [], Matrix3x2.Identity, 0.25f, out _, (NativeMilBackend)2));
    }

    [Fact]
    public void InvalidDashLanesAndTailsRejectBeforeLoading()
    {
        for (int count = 1; count <= 17; count++)
        {
            float[] storage = new float[count + 1];
            Array.Fill(storage, 1f);
            for (int lane = 0; lane < count; lane++)
            {
                foreach (float value in new[] { -1f, float.NaN, float.PositiveInfinity })
                {
                    storage[lane + 1] = value;
                    Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.GetStrokeBounds(
                        [], [], [], Pen, storage.AsSpan(1), Matrix3x2.Identity, 0.25f, out _));
                }
                storage[lane + 1] = 1;
            }
            Array.Clear(storage);
            Assert.Throws<ArgumentException>(() => NativeGeometryUtilities.GetStrokeBounds(
                [], [], [], Pen, storage.AsSpan(1), Matrix3x2.Identity, 0.25f, out _));
        }
    }
}
