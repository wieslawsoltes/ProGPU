using ProGPU.Wpf.Interop;
using Xunit;

namespace ProGPU.Backend.Tests;

public sealed class PortableWindowFrameInsetsTests
{
    [Fact]
    public void KnownBorderlessFrameIsDistinctFromUnavailableFrame()
    {
        var callbacks = new PortableWindowActivationCallbacks(_ => null)
        {
            GetFrameInsets = _ => PortableWindowFrameInsets.Empty
        };

        var reported = callbacks.GetFrameInsets!(new object());
        Assert.True(reported.HasValue);
        var frame = reported.Value;
        Assert.True(frame.IsValid);
        Assert.Equal(0, frame.Horizontal);
        Assert.Equal(0, frame.Vertical);

        var unavailable = new PortableWindowActivationCallbacks(_ => null)
        {
            GetFrameInsets = _ => null
        };
        Assert.Null(unavailable.GetFrameInsets!(new object()));
    }

    [Theory]
    [InlineData(6, 28, 7, 8, 13, 36)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    public void LogicalInsetsPreserveAllFourSides(
        double left, double top, double right, double bottom,
        double horizontal, double vertical)
    {
        var frame = new PortableWindowFrameInsets(left, top, right, bottom);

        Assert.True(frame.IsValid);
        Assert.Equal(horizontal, frame.Horizontal);
        Assert.Equal(vertical, frame.Vertical);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void NegativeInsetsCannotEnterSourceSizing(
        double left, double top, double right, double bottom)
    {
        Assert.False(new PortableWindowFrameInsets(left, top, right, bottom).IsValid);
    }

    [Fact]
    public void NonFiniteInsetsCannotEnterSourceSizing()
    {
        Assert.False(new PortableWindowFrameInsets(double.NaN, 0, 0, 0).IsValid);
        Assert.False(new PortableWindowFrameInsets(0, double.PositiveInfinity, 0, 0).IsValid);
    }
}
