using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public class NativeGeometryUtilitiesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidToleranceIsRejectedBeforeNativeLoading(float tolerance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.FillContains(
            [], NativeFillRule.NonZero, Vector2.Zero, tolerance));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.Combine(
            [], NativeFillRule.NonZero, [], NativeFillRule.NonZero, NativeMilGeometryCombineMode.Union, tolerance));
    }

    [Fact]
    public void InvalidPoliciesAreRejectedBeforeNativeLoading()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.FillContains([], (NativeFillRule)2, Vector2.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.FillContains([], NativeFillRule.NonZero, new(float.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.FillContains([], NativeFillRule.NonZero, Vector2.Zero,
            backend: (NativeMilBackend)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.Combine(
            [], (NativeFillRule)2, [], NativeFillRule.NonZero, NativeMilGeometryCombineMode.Union));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.Combine(
            [], NativeFillRule.NonZero, [], (NativeFillRule)2, NativeMilGeometryCombineMode.Union));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.Combine(
            [], NativeFillRule.NonZero, [], NativeFillRule.NonZero, (NativeMilGeometryCombineMode)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeGeometryUtilities.Combine(
            [], NativeFillRule.NonZero, [], NativeFillRule.NonZero, NativeMilGeometryCombineMode.Union,
            backend: (NativeMilBackend)2));
    }

    [Fact]
    public void UtilityReusesCanonicalPathAndPointWireLayouts()
    {
        Assert.Equal(8, Unsafe.SizeOf<Vector2>());
        Assert.Equal(48, Unsafe.SizeOf<NativePathSegment>());
        Assert.Equal(32, Marshal.OffsetOf<NativePathSegment>(nameof(NativePathSegment.Kind)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<NativePathSegment>(nameof(NativePathSegment.Pad2)).ToInt32());
    }

    [Fact]
    public void OwnedContoursPreserveOffsetsAndRejectInvalidIndices()
    {
        var outline = new NativeGeometryOutline([new(0, 0), new(10, 0), new(0, 10)], [0, 3]);
        Assert.Equal(1, outline.ContourCount);
        Assert.Equal(NativeFillRule.EvenOdd, outline.FillRule);
        Assert.Equal(new Vector2(10, 0), outline.GetContour(0)[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => outline.GetContour(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => outline.GetContour(1));
    }
}
