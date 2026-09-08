using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeScenePresentationTests
{
    [Fact]
    public void ExplicitViewportPreservesIndependentAxesAndPhysicalOrigin()
    {
        var value = new NativeScenePresentation(13, 17, 640, 480, 1.25f, 1.5f).ToNative(800, 600);
        Assert.Equal(32U, value.StructSize);
        Assert.Equal(13U, value.ViewportX);
        Assert.Equal(17U, value.ViewportY);
        Assert.Equal(640U, value.ViewportWidth);
        Assert.Equal(480U, value.ViewportHeight);
        Assert.Equal(1.25f, value.DpiScaleX);
        Assert.Equal(1.5f, value.DpiScaleY);
        Assert.Equal(0U, value.Reserved);
        var full = NativeScenePresentation.Full(800, 600, 2).ToNative(800, 600);
        Assert.Equal(0U, full.ViewportX);
        Assert.Equal(0U, full.ViewportY);
        Assert.Equal(800U, full.ViewportWidth);
        Assert.Equal(600U, full.ViewportHeight);
        Assert.Equal(2f, full.DpiScaleX);
        Assert.Equal(2f, full.DpiScaleY);
    }

    [Fact]
    public void InvalidExtentsAndAxesFailBeforeNativeSubmission()
    {
        var valid = new NativeScenePresentation(13, 17, 640, 480, 1.25f, 1.5f);
        foreach (float scale in new[] { 0f, -1f, float.PositiveInfinity, float.NaN, float.Epsilon })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { DpiScaleX = scale }).ToNative(800, 600));
            Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { DpiScaleY = scale }).ToNative(800, 600));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { ViewportX = uint.MaxValue }).ToNative(800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { ViewportWidth = uint.MaxValue }).ToNative(800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => (valid with { ViewportHeight = 0 }).ToNative(800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.ToNative(0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => valid.ToNative(800, 0));
    }
}
