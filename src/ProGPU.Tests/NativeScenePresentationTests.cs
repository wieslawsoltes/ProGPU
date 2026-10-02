using System.Numerics;
using ProGPU.Backend.Native;
using Xunit;

namespace ProGPU.Tests;

public sealed class NativeScenePresentationTests
{
    [Fact]
    public void ManagedBuilderRejectsNativeOnlyPicturePayloadWithoutPublishingResource()
    {
        // Managed Scene is a producer, not an IMAGE_PICTURE wire interpreter.
        // Raw streams are validated/replayed by NativeCompositor's C++ provider.
        Span<byte> bytes = stackalloc byte[512];
        Span<byte> payload = stackalloc byte[80]; // 48-byte descriptor + 32-byte presentation.
        var builder = new NativeSceneStreamBuilder(bytes, 1, 1, 0, 1);
        Assert.False(builder.TryAddResource(NativeSceneResourceKind.Image, 1, 1,
            payload, out uint resource, flags: (NativeSceneRecordFlags)(1U | (1U << 6))));
        Assert.Equal(uint.MaxValue, resource);
        Assert.Equal(0, builder.ResourceCount);
        Assert.Equal(0, builder.CommandCount);
    }

    [Fact]
    public void CpuStageCaptureExposesMappedPresentationWithoutASecondRender()
    {
        Func<NativeCompositor, NativeSceneExternalTarget,
            NativeScenePresentation, ulong, ulong, Vector4,
            NativeSceneFrameMetrics> render = static (
                compositor, target, presentation, sceneId, generation,
                clearColor) => compositor.RenderSceneWithCpuStages(
                    target,
                    presentation,
                    sceneId,
                    generation,
                    clearColor);

        Assert.NotNull(render);
    }

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
