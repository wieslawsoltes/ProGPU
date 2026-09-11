using System.Numerics;
using ProGPU.Scene;
using ProGPU.Vector;
using Xunit;

namespace ProGPU.Tests;

public sealed class GpuPictureBoundsTests
{
    [Fact]
    public void RetainedPictureBoundsApplyTransformAndClip()
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext context = recorder.BeginRecording(
            new Rect(0f, 0f, 32f, 32f));
        context.PushClip(new Rect(0f, 0f, 8f, 8f));
        context.DrawRectangle(
            new SolidColorBrush(Vector4.One),
            null,
            new Rect(-2f, 2f, 12f, 10f),
            Matrix4x4.CreateTranslation(5f, 1f, 0f));
        context.PopClip();
        using GpuPicture picture = recorder.EndRecording();

        Assert.True(GpuPictureBounds.TryGetBounds(
            picture,
            out Rect bounds));
        Assert.Equal(new Rect(3f, 3f, 5f, 5f), bounds);
    }

    [Fact]
    public void RetainedPictureBoundsTraverseNestedPictures()
    {
        var childRecorder = new GpuPictureRecorder();
        DrawingContext childContext = childRecorder.BeginRecording(
            new Rect(0f, 0f, 16f, 16f));
        childContext.DrawRectangle(
            new SolidColorBrush(Vector4.One),
            null,
            new Rect(1f, 2f, 3f, 4f));
        using GpuPicture child = childRecorder.EndRecording();

        var parentRecorder = new GpuPictureRecorder();
        DrawingContext parentContext = parentRecorder.BeginRecording(
            new Rect(0f, 0f, 64f, 64f));
        parentContext.DrawPictureTransformed(
            child,
            Matrix4x4.CreateScale(2f, 2f, 1f) *
            Matrix4x4.CreateTranslation(10f, 20f, 0f));
        using GpuPicture parent = parentRecorder.EndRecording();

        Assert.True(GpuPictureBounds.TryGetBounds(
            parent,
            out Rect bounds));
        Assert.Equal(new Rect(12f, 24f, 6f, 8f), bounds);
    }

    [Fact]
    public void RetainedPictureBoundsFailClosedForGpuTransformPictures()
    {
        var childRecorder = new GpuPictureRecorder();
        DrawingContext childContext = childRecorder.BeginRecording(
            new Rect(0f, 0f, 16f, 16f));
        childContext.DrawRectangle(
            new SolidColorBrush(Vector4.One),
            null,
            new Rect(1f, 2f, 3f, 4f));
        using GpuPicture child = childRecorder.EndRecording();

        var parentRecorder = new GpuPictureRecorder();
        DrawingContext parentContext = parentRecorder.BeginRecording(
            new Rect(0f, 0f, 64f, 64f));
        parentContext.DrawPicture(
            child,
            Matrix4x4.CreateTranslation(10f, 20f, 0f));
        using GpuPicture parent = parentRecorder.EndRecording();

        Assert.False(GpuPictureBounds.TryGetBounds(
            parent,
            out _));
    }

    [Fact]
    public void RetainedPictureBoundsFailClosedForUnbalancedState()
    {
        using var picture = new GpuPicture(
            [new RenderCommand { Type = RenderCommandType.PopClip }],
            [],
            [],
            [],
            []);

        Assert.False(GpuPictureBounds.TryGetBounds(
            picture,
            out _));
    }
}
